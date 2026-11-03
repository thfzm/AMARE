//! Backup/restore, integrity checks, and safe replacement.
//!
//! Container layout (all integers little-endian):
//! `MAGIC(8) | version(u16) | flags(u8) | salt_len(u8) | payload_len(u64) | salt | payload`
//!
//! Payload (after optional gzip/encryption):
//! `manifest_len(u32) | manifest_json | file bytes...`
//!
//! The manifest's file offsets are relative to the start of the file-bytes area.
//! A backup is only published after the complete container and sidecar metadata
//! have been written; failed/incomplete temp files are ignored.

use crate::db::{now_ms, validate_profile_id};
use crate::error::StoreError;
use crate::maintenance::check_database;
use crate::migrations::{PROFILE_SCHEMA_VERSION, ROOT_SCHEMA_VERSION};
use crate::{ProfileStore, Store};
use amare_core::ProfileId;
use amare_security::{SecurityError, decrypt, derive_key, encrypt, generate_salt, sha256_hex};
use flate2::Compression;
use flate2::read::GzDecoder;
use flate2::write::GzEncoder;
use rusqlite::Connection;
use rusqlite::backup::Backup;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

pub const BACKUP_FORMAT_VERSION: u32 = 1;
const MAGIC: &[u8; 8] = b"AMAREBAK";
const CONTAINER_VERSION: u16 = 1;
const FLAG_ENCRYPTED: u8 = 1;
const FLAG_COMPRESSED: u8 = 2;
const PASSWORD_SALT_LEN: usize = 16;
const BACKUP_EXTENSION: &str = "amarebak";
const MAX_CONTAINER_BYTES: u64 = 2 * 1024 * 1024 * 1024;
const MAX_UNCOMPRESSED_BYTES: usize = 2 * 1024 * 1024 * 1024;
const MAX_MANIFEST_BYTES: usize = 4 * 1024 * 1024;
const MAX_FILE_COUNT: usize = 512;
const MAX_FILE_BYTES: u64 = 1024 * 1024 * 1024;
const MAX_TOTAL_FILE_BYTES: u64 = 2 * 1024 * 1024 * 1024;
static BACKUP_COUNTER: AtomicU64 = AtomicU64::new(1);
type FileInput = (PathBuf, String, BackupFileKind);
type FileIndex = (Vec<BackupFile>, Vec<FileInput>);

#[derive(Debug, thiserror::Error)]
pub enum BackupError {
    #[error("store error: {0}")]
    Store(#[from] StoreError),
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("json error: {0}")]
    Json(#[from] serde_json::Error),
    #[error("sqlite error: {0}")]
    Sqlite(#[from] rusqlite::Error),
    #[error("security error: {0}")]
    Security(#[from] SecurityError),
    #[error("invalid backup format: {0}")]
    InvalidFormat(String),
    #[error("unsupported backup/container version: {0}")]
    UnsupportedVersion(u32),
    #[error("backup integrity check failed: {0}")]
    Integrity(String),
    #[error("backup password/key is required")]
    CredentialRequired,
    #[error("backup password/key is incorrect")]
    WrongCredential,
    #[error("no profiles selected for backup")]
    NoProfiles,
    #[error("profile not found: {0}")]
    ProfileNotFound(String),
    #[error("unsafe path in backup: {0}")]
    UnsafePath(String),
    #[error("backup state error: {0}")]
    State(String),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum BackupScope {
    Full,
    Profiles,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum BackupFileKind {
    RootDb,
    ProfileDb,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BackupFile {
    pub path: String,
    pub kind: BackupFileKind,
    pub offset: u64,
    pub size: u64,
    pub sha256: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BackupProfile {
    pub id: String,
    pub name: String,
    pub schema_version: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BackupManifest {
    pub format_version: u32,
    pub app_version: String,
    pub created_at_ms: i64,
    pub scope: BackupScope,
    pub data_kinds: Vec<String>,
    pub profiles: Vec<BackupProfile>,
    pub root_schema_version: Option<u32>,
    pub compression: String,
    pub encryption: String,
    pub contains_secrets: bool,
    pub consistency: String,
    pub files: Vec<BackupFile>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct BackupRecord {
    pub file_name: String,
    pub created_at_ms: i64,
    pub size_bytes: u64,
    pub sha256: String,
    pub encrypted: bool,
    pub format_version: u32,
    pub app_version: String,
    pub scope: BackupScope,
    pub profiles: Vec<String>,
    pub data_kinds: Vec<String>,
    pub contains_secrets: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BackupData {
    pub settings: bool,
    pub bookmarks: bool,
    pub history: bool,
    pub sessions: bool,
    pub secrets: bool,
    pub library: bool,
    pub conversations: bool,
    pub workspaces: bool,
}

impl Default for BackupData {
    fn default() -> Self {
        Self {
            settings: true,
            bookmarks: true,
            history: true,
            sessions: true,
            secrets: true,
            library: true,
            conversations: true,
            workspaces: true,
        }
    }
}

#[derive(Debug, Clone)]
pub enum BackupEncryption {
    None,
    Password(String),
    /// Caller-provided 32-byte key. Obtain it from a DPAPI/Keystore-protected
    /// `KeyFile`, never from a plaintext config file.
    Key([u8; 32]),
}

#[derive(Debug, Clone)]
pub enum BackupCredential {
    None,
    Password(String),
    Key([u8; 32]),
}

#[derive(Debug, Clone)]
pub struct BackupOptions {
    pub include_root: bool,
    /// `None` means all profiles.
    pub profiles: Option<Vec<ProfileId>>,
    pub data: BackupData,
    pub encryption: BackupEncryption,
    pub compression: bool,
}

impl Default for BackupOptions {
    fn default() -> Self {
        Self {
            include_root: true,
            profiles: None,
            data: BackupData::default(),
            encryption: BackupEncryption::None,
            compression: true,
        }
    }
}

#[derive(Debug, Clone)]
pub enum RestoreMode {
    /// Replace the root DB and all profile DBs from a Full backup.
    Full,
    /// Replace only the selected profile DBs; the target root/profiles must exist.
    Profiles(Vec<ProfileId>),
}

#[derive(Debug, Clone)]
pub struct RestoreOptions {
    pub credential: BackupCredential,
    pub mode: RestoreMode,
}

#[derive(Debug, Clone, Serialize)]
pub struct RestoreReport {
    pub file_name: String,
    pub restored_profiles: Vec<String>,
    pub recovery_dir: Option<PathBuf>,
    pub verified: bool,
}

#[derive(Debug, Clone)]
pub struct BackupPolicy {
    pub interval_ms: u64,
    pub keep: usize,
}

impl Default for BackupPolicy {
    fn default() -> Self {
        Self {
            interval_ms: 24 * 60 * 60 * 1000,
            keep: 7,
        }
    }
}

#[derive(Debug, Serialize, Deserialize, Default)]
struct BackupState {
    last_backup_ms: Option<i64>,
}

pub struct BackupManager {
    root: PathBuf,
    backup_dir: PathBuf,
}

impl BackupManager {
    pub fn new(root: impl AsRef<Path>) -> Result<Self, BackupError> {
        let root = root.as_ref().to_path_buf();
        let backup_dir = root.join("backups");
        fs::create_dir_all(&backup_dir)?;
        Ok(Self { root, backup_dir })
    }

    pub fn root(&self) -> &Path {
        &self.root
    }

    pub fn backup_dir(&self) -> &Path {
        &self.backup_dir
    }

    pub fn create_backup(
        &self,
        store: &Store,
        options: &BackupOptions,
    ) -> Result<BackupRecord, BackupError> {
        if store.root_dir() != self.root {
            return Err(BackupError::InvalidFormat(
                "store root does not match backup manager root".into(),
            ));
        }
        let all = store.profiles().list(500, 0)?.items;
        let selected: Vec<_> = match &options.profiles {
            Some(ids) => {
                let mut out = Vec::with_capacity(ids.len());
                for id in ids {
                    let profile = all
                        .iter()
                        .find(|profile| &profile.id == id)
                        .ok_or_else(|| BackupError::ProfileNotFound(id.to_string()))?;
                    if !out.iter().any(|p: &amare_core::Profile| &p.id == id) {
                        out.push(profile.clone());
                    }
                }
                out
            }
            None => all,
        };
        if !options.include_root && selected.is_empty() {
            return Err(BackupError::NoProfiles);
        }

        let started = now_ms();
        let id = backup_id(started);
        let temp_dir = self.backup_dir.join(format!(".tmp-{id}"));
        let temp_file = self.backup_dir.join(format!(".tmp-{id}.amarebak"));
        let final_file = self.backup_dir.join(format!("{id}.{BACKUP_EXTENSION}"));
        let result = (|| -> Result<BackupRecord, BackupError> {
            fs::create_dir_all(&temp_dir)?;
            let mut manifest_profiles = Vec::new();
            let mut root_schema = None;
            let mut files = Vec::new();

            if options.include_root {
                let dest = temp_dir.join("amare.db");
                snapshot_connection(store.root_connection(), &dest)?;
                root_schema = Some(store.schema_version()?);
                files.push((dest, "amare.db".to_string(), BackupFileKind::RootDb));
            }

            for profile in selected {
                let path = store.profile_db_path(&profile.id)?;
                let profile_store = ProfileStore::open_at(&path, profile.id.clone())?;
                let rel = format!("profiles/{}/amare.db", profile.id);
                let dest = temp_dir.join(&rel);
                snapshot_connection(profile_store.connection(), &dest)?;
                filter_profile_snapshot(&dest, &options.data)?;
                manifest_profiles.push(BackupProfile {
                    id: profile.id.as_str().to_string(),
                    name: profile.name.clone(),
                    schema_version: profile_store.schema_version()?,
                });
                files.push((dest, rel, BackupFileKind::ProfileDb));
            }

            let scope = if options.include_root && options.profiles.is_none() {
                BackupScope::Full
            } else {
                BackupScope::Profiles
            };
            let data_kinds = selected_data_kinds(options);
            let contains_secrets = options.data.secrets && !manifest_profiles.is_empty();
            let (manifest_files, file_bytes) = build_file_index(files)?;
            let manifest = BackupManifest {
                format_version: BACKUP_FORMAT_VERSION,
                app_version: amare_core::version().to_string(),
                created_at_ms: started,
                scope,
                data_kinds,
                profiles: manifest_profiles,
                root_schema_version: root_schema,
                compression: if options.compression { "gzip" } else { "none" }.into(),
                encryption: encryption_label(&options.encryption).into(),
                contains_secrets,
                consistency: "SQLite Backup API per database; root and profile snapshots are sequential, not a cross-database transaction.".into(),
                files: manifest_files,
            };
            let raw_payload = encode_payload(&manifest, &file_bytes)?;
            let (payload, compressed) = compress_payload(raw_payload, options.compression)?;
            let container = encode_container(&payload, &options.encryption, compressed)?;
            let mut out = fs::File::create(&temp_file)?;
            out.write_all(&container)?;
            out.sync_all()?;
            drop(out);
            let sha256 = sha256_hex(&container);
            fs::rename(&temp_file, &final_file)?;
            let record = BackupRecord {
                file_name: final_file
                    .file_name()
                    .and_then(|name| name.to_str())
                    .unwrap_or_default()
                    .to_string(),
                created_at_ms: started,
                size_bytes: container.len() as u64,
                sha256: sha256.clone(),
                encrypted: !matches!(options.encryption, BackupEncryption::None),
                format_version: BACKUP_FORMAT_VERSION,
                app_version: amare_core::version().to_string(),
                scope: manifest.scope,
                profiles: manifest
                    .profiles
                    .iter()
                    .map(|profile| profile.id.clone())
                    .collect(),
                data_kinds: manifest.data_kinds.clone(),
                contains_secrets: manifest.contains_secrets,
            };
            write_meta(&self.backup_dir, &record)?;
            fs::write(sidecar_path(&final_file, "sha256"), format!("{sha256}\n"))?;
            Ok(record)
        })();
        let _ = fs::remove_dir_all(&temp_dir);
        let _ = fs::remove_file(&temp_file);
        if result.is_err() {
            let _ = fs::remove_file(&final_file);
            let _ = remove_meta(&self.backup_dir, &final_file);
            let _ = fs::remove_file(sidecar_path(&final_file, "sha256"));
        }
        result
    }

    pub fn list(&self) -> Result<Vec<BackupRecord>, BackupError> {
        let mut records = Vec::new();
        if !self.backup_dir.exists() {
            return Ok(records);
        }
        for entry in fs::read_dir(&self.backup_dir)? {
            let entry = entry?;
            let path = entry.path();
            if path.extension().and_then(|ext| ext.to_str()) != Some(BACKUP_EXTENSION) {
                continue;
            }
            if let Ok(record) = read_meta(&self.backup_dir, &path) {
                records.push(record);
            }
        }
        records.sort_by(|a, b| {
            b.created_at_ms
                .cmp(&a.created_at_ms)
                .then_with(|| b.file_name.cmp(&a.file_name))
        });
        Ok(records)
    }

    pub fn verify(
        &self,
        path: impl AsRef<Path>,
        credential: &BackupCredential,
    ) -> Result<BackupManifest, BackupError> {
        let path = path.as_ref();
        let bytes = read_container(path)?;
        verify_sidecar_sha(path, &bytes)?;
        let decoded = decode_container(&bytes, credential)?;
        Ok(decoded.manifest)
    }

    pub fn restore(
        &self,
        backup: impl AsRef<Path>,
        target_root: impl AsRef<Path>,
        options: &RestoreOptions,
    ) -> Result<RestoreReport, BackupError> {
        let backup = backup.as_ref();
        let target_root = target_root.as_ref();
        let bytes = read_container(backup)?;
        verify_sidecar_sha(backup, &bytes)?;
        let decoded = decode_container(&bytes, &options.credential)?;
        let manifest = decoded.manifest;
        validate_manifest(&manifest)?;

        let staging = restore_staging_path(target_root);
        fs::create_dir_all(&staging)?;
        for (path, data) in &decoded.files {
            let dest = safe_join(&staging, path)?;
            if let Some(parent) = dest.parent() {
                fs::create_dir_all(parent)?;
            }
            fs::write(&dest, data)?;
        }

        let validation = (|| -> Result<(), BackupError> {
            if manifest.files.iter().any(|file| file.path == "amare.db") {
                let root_check = check_database(staging.join("amare.db"));
                if !root_check.ok {
                    return Err(BackupError::Integrity(root_check.error.unwrap_or_else(
                        || {
                            format!(
                                "root integrity check failed: {:?}",
                                root_check.integrity_messages
                            )
                        },
                    )));
                }
                if root_check.schema_version.unwrap_or(0) > ROOT_SCHEMA_VERSION {
                    return Err(BackupError::UnsupportedVersion(
                        root_check.schema_version.unwrap_or(0),
                    ));
                }
                let migrated = Store::open(&staging)?;
                if migrated.schema_version()? > ROOT_SCHEMA_VERSION {
                    return Err(BackupError::UnsupportedVersion(migrated.schema_version()?));
                }
            }
            for profile in &manifest.profiles {
                validate_profile_id(&profile.id)?;
                let path = staging.join("profiles").join(&profile.id).join("amare.db");
                let check = check_database(&path);
                if !check.ok {
                    return Err(BackupError::Integrity(format!(
                        "profile {} integrity check failed: {:?}",
                        profile.id, check.integrity_messages
                    )));
                }
                if check.schema_version.unwrap_or(0) > PROFILE_SCHEMA_VERSION {
                    return Err(BackupError::UnsupportedVersion(
                        check.schema_version.unwrap_or(0),
                    ));
                }
                let profile_store =
                    ProfileStore::open_at(&path, ProfileId::new(profile.id.clone()))?;
                if profile_store.schema_version()? > PROFILE_SCHEMA_VERSION {
                    return Err(BackupError::UnsupportedVersion(
                        profile_store.schema_version()?,
                    ));
                }
            }
            Ok(())
        })();
        if let Err(error) = validation {
            let _ = fs::remove_dir_all(&staging);
            return Err(error);
        }

        let recovery = recovery_path(target_root);
        let result = match &options.mode {
            RestoreMode::Full => restore_full(target_root, &staging, &recovery, &manifest),
            RestoreMode::Profiles(ids) => {
                restore_profiles(target_root, &staging, &recovery, ids, &manifest)
            }
        };
        let _ = fs::remove_dir_all(&staging);
        match result {
            Ok(profiles) => {
                // Revalidate the live target before reporting success. If this
                // fails, put the recovery directory back.
                let verification = (|| -> Result<(), BackupError> {
                    let store = Store::open(target_root)?;
                    if store.schema_version()? > ROOT_SCHEMA_VERSION {
                        return Err(BackupError::UnsupportedVersion(store.schema_version()?));
                    }
                    for id in &profiles {
                        let path = target_root.join("profiles").join(id).join("amare.db");
                        let check = check_database(&path);
                        if !check.ok {
                            return Err(BackupError::Integrity(format!(
                                "restored profile {id} failed verification"
                            )));
                        }
                    }
                    Ok(())
                })();
                if let Err(error) = verification {
                    let _ = rollback_restore(target_root, &recovery, &options.mode, &manifest);
                    return Err(error);
                }
                Ok(RestoreReport {
                    file_name: backup
                        .file_name()
                        .and_then(|name| name.to_str())
                        .unwrap_or_default()
                        .to_string(),
                    restored_profiles: profiles,
                    recovery_dir: if recovery.exists() {
                        Some(recovery)
                    } else {
                        None
                    },
                    verified: true,
                })
            }
            Err(error) => Err(error),
        }
    }

    pub fn delete(&self, path: impl AsRef<Path>) -> Result<(), BackupError> {
        let path = path.as_ref();
        if path.parent() != Some(self.backup_dir.as_path())
            || path.extension().and_then(|ext| ext.to_str()) != Some(BACKUP_EXTENSION)
        {
            return Err(BackupError::UnsafePath(path.display().to_string()));
        }
        if path.exists() {
            fs::remove_file(path)?;
        }
        let _ = remove_meta(&self.backup_dir, path);
        let _ = fs::remove_file(sidecar_path(path, "sha256"));
        Ok(())
    }

    pub fn prune(&self, keep: usize) -> Result<Vec<BackupRecord>, BackupError> {
        let keep = keep.max(1);
        let mut removed = Vec::new();
        for record in self.list()?.into_iter().skip(keep) {
            let path = self.backup_dir.join(&record.file_name);
            self.delete(&path)?;
            removed.push(record);
        }
        Ok(removed)
    }

    pub fn auto_backup_if_due(
        &self,
        store: &Store,
        options: &BackupOptions,
        policy: &BackupPolicy,
    ) -> Result<Option<BackupRecord>, BackupError> {
        let state_path = self.backup_dir.join("state.json");
        let state: BackupState = if state_path.exists() {
            serde_json::from_slice(&fs::read(&state_path)?)?
        } else {
            BackupState::default()
        };
        let now = now_ms();
        if let Some(last) = state.last_backup_ms {
            if now.saturating_sub(last) < policy.interval_ms as i64 {
                return Ok(None);
            }
        }
        let record = self.create_backup(store, options)?;
        self.prune(policy.keep)?;
        let state = BackupState {
            last_backup_ms: Some(record.created_at_ms),
        };
        write_json_atomic(&state_path, &state)?;
        Ok(Some(record))
    }
}

struct DecodedBackup {
    manifest: BackupManifest,
    files: BTreeMap<String, Vec<u8>>,
}

fn build_file_index(files: Vec<FileInput>) -> Result<FileIndex, BackupError> {
    let mut entries = Vec::with_capacity(files.len());
    let mut offset = 0u64;
    for (path, logical, kind) in &files {
        let bytes = fs::read(path)?;
        let size = bytes.len() as u64;
        if size > MAX_FILE_BYTES {
            return Err(BackupError::InvalidFormat(format!(
                "file {logical} exceeds the size limit"
            )));
        }
        offset = offset
            .checked_add(size)
            .ok_or_else(|| BackupError::InvalidFormat("backup size overflow".into()))?;
        if offset > MAX_TOTAL_FILE_BYTES {
            return Err(BackupError::InvalidFormat(
                "backup data exceeds the size limit".into(),
            ));
        }
        entries.push(BackupFile {
            path: logical.clone(),
            kind: *kind,
            offset: offset - size,
            size,
            sha256: sha256_hex(&bytes),
        });
    }
    Ok((entries, files))
}

fn encode_payload(manifest: &BackupManifest, files: &[FileInput]) -> Result<Vec<u8>, BackupError> {
    let mut file_data = Vec::new();
    let mut entries = Vec::new();
    let mut offset = 0u64;
    for (path, logical, kind) in files {
        let bytes = fs::read(path)?;
        let size = bytes.len() as u64;
        if size > MAX_FILE_BYTES {
            return Err(BackupError::InvalidFormat(format!(
                "file {logical} exceeds the size limit"
            )));
        }
        offset = offset
            .checked_add(size)
            .ok_or_else(|| BackupError::InvalidFormat("backup size overflow".into()))?;
        if offset > MAX_TOTAL_FILE_BYTES {
            return Err(BackupError::InvalidFormat(
                "backup data exceeds the size limit".into(),
            ));
        }
        entries.push(BackupFile {
            path: logical.clone(),
            kind: *kind,
            offset: offset - size,
            size,
            sha256: sha256_hex(&bytes),
        });
        file_data.extend_from_slice(&bytes);
    }
    let mut manifest = manifest.clone();
    manifest.files = entries;
    let manifest_bytes = serde_json::to_vec(&manifest)?;
    if manifest_bytes.len() > MAX_MANIFEST_BYTES {
        return Err(BackupError::InvalidFormat(
            "manifest exceeds the size limit".into(),
        ));
    }
    let mut payload = Vec::with_capacity(4 + manifest_bytes.len() + file_data.len());
    payload.extend_from_slice(&(manifest_bytes.len() as u32).to_le_bytes());
    payload.extend_from_slice(&manifest_bytes);
    payload.extend_from_slice(&file_data);
    Ok(payload)
}

fn encode_container(
    payload: &[u8],
    encryption: &BackupEncryption,
    compressed: bool,
) -> Result<Vec<u8>, BackupError> {
    let (flags, salt, body) = match encryption {
        BackupEncryption::None => (0u8, Vec::new(), payload.to_vec()),
        BackupEncryption::Password(password) => {
            let salt = generate_salt();
            let key = derive_key(password.as_bytes(), &salt)?;
            let aad = container_aad(FLAG_ENCRYPTED, &salt);
            (FLAG_ENCRYPTED, salt.to_vec(), encrypt(&key, payload, &aad)?)
        }
        BackupEncryption::Key(key) => {
            let aad = container_aad(FLAG_ENCRYPTED, &[]);
            (FLAG_ENCRYPTED, Vec::new(), encrypt(key, payload, &aad)?)
        }
    };
    let flags = flags | if compressed { FLAG_COMPRESSED } else { 0 };
    let mut out = Vec::with_capacity(8 + 2 + 1 + 1 + 8 + salt.len() + body.len());
    out.extend_from_slice(MAGIC);
    out.extend_from_slice(&CONTAINER_VERSION.to_le_bytes());
    out.push(flags);
    out.push(salt.len() as u8);
    out.extend_from_slice(&(body.len() as u64).to_le_bytes());
    out.extend_from_slice(&salt);
    out.extend_from_slice(&body);
    Ok(out)
}

fn compress_payload(raw: Vec<u8>, compress: bool) -> Result<(Vec<u8>, bool), BackupError> {
    if !compress {
        return Ok((raw, false));
    }
    let mut encoder = GzEncoder::new(Vec::new(), Compression::default());
    encoder.write_all(&raw)?;
    Ok((encoder.finish().map_err(BackupError::Io)?, true))
}

fn decode_container(
    bytes: &[u8],
    credential: &BackupCredential,
) -> Result<DecodedBackup, BackupError> {
    if bytes.len() < 8 + 2 + 1 + 1 + 8 || &bytes[..8] != MAGIC {
        return Err(BackupError::InvalidFormat("missing AMAREBAK magic".into()));
    }
    if bytes.len() as u64 > MAX_CONTAINER_BYTES {
        return Err(BackupError::InvalidFormat(
            "backup container exceeds the size limit".into(),
        ));
    }
    let version = u16::from_le_bytes([bytes[8], bytes[9]]);
    if version != CONTAINER_VERSION {
        return Err(BackupError::UnsupportedVersion(version as u32));
    }
    let flags = bytes[10];
    let salt_len = bytes[11] as usize;
    let payload_len_u64 = u64::from_le_bytes(bytes[12..20].try_into().expect("8 bytes"));
    if payload_len_u64 > MAX_CONTAINER_BYTES {
        return Err(BackupError::InvalidFormat(
            "backup payload exceeds the size limit".into(),
        ));
    }
    let payload_len = payload_len_u64 as usize;
    let salt_start = 20;
    let payload_start = salt_start + salt_len;
    let payload_end = payload_start.saturating_add(payload_len);
    if salt_len > 32 || payload_end != bytes.len() {
        return Err(BackupError::InvalidFormat(
            "invalid container length/salt".into(),
        ));
    }
    let salt = &bytes[salt_start..payload_start];
    let body = &bytes[payload_start..payload_end];
    let encrypted = flags & FLAG_ENCRYPTED != 0;
    let compressed = flags & FLAG_COMPRESSED != 0;
    let payload = if encrypted {
        let aad = container_aad(FLAG_ENCRYPTED, salt);
        match credential {
            BackupCredential::None => return Err(BackupError::CredentialRequired),
            BackupCredential::Password(password) => {
                if salt.len() != PASSWORD_SALT_LEN {
                    return Err(BackupError::InvalidFormat(
                        "password backup missing salt".into(),
                    ));
                }
                let key = derive_key(password.as_bytes(), salt)?;
                decrypt(&key, body, &aad).map_err(|_| BackupError::WrongCredential)?
            }
            BackupCredential::Key(key) => {
                decrypt(key, body, &aad).map_err(|_| BackupError::WrongCredential)?
            }
        }
    } else {
        body.to_vec()
    };
    let raw = if compressed {
        let decoder = GzDecoder::new(payload.as_slice());
        let mut out = Vec::new();
        let read = decoder
            .take(MAX_UNCOMPRESSED_BYTES as u64 + 1)
            .read_to_end(&mut out)?;
        if read > MAX_UNCOMPRESSED_BYTES {
            return Err(BackupError::InvalidFormat(
                "decompressed backup exceeds the size limit".into(),
            ));
        }
        out
    } else {
        payload
    };
    decode_payload(&raw)
}

fn decode_payload(raw: &[u8]) -> Result<DecodedBackup, BackupError> {
    if raw.len() < 4 {
        return Err(BackupError::InvalidFormat("payload too short".into()));
    }
    let manifest_len = u32::from_le_bytes(raw[..4].try_into().expect("4 bytes")) as usize;
    if manifest_len > MAX_MANIFEST_BYTES {
        return Err(BackupError::InvalidFormat(
            "manifest exceeds the size limit".into(),
        ));
    }
    let manifest_end = 4 + manifest_len;
    if manifest_end > raw.len() {
        return Err(BackupError::InvalidFormat(
            "manifest length out of bounds".into(),
        ));
    }
    let manifest: BackupManifest = serde_json::from_slice(&raw[4..manifest_end])?;
    if manifest.format_version != BACKUP_FORMAT_VERSION {
        return Err(BackupError::UnsupportedVersion(manifest.format_version));
    }
    validate_manifest(&manifest)?;
    let data = &raw[manifest_end..];
    let mut files = BTreeMap::new();
    for file in &manifest.files {
        let start = file.offset as usize;
        let end = start
            .checked_add(file.size as usize)
            .ok_or_else(|| BackupError::InvalidFormat("file offset overflow".into()))?;
        if end > data.len() {
            return Err(BackupError::InvalidFormat(
                "file offset out of bounds".into(),
            ));
        }
        let bytes = &data[start..end];
        if sha256_hex(bytes) != file.sha256 {
            return Err(BackupError::Integrity(format!(
                "checksum mismatch for {}",
                file.path
            )));
        }
        if files.insert(file.path.clone(), bytes.to_vec()).is_some() {
            return Err(BackupError::InvalidFormat("duplicate file entry".into()));
        }
    }
    Ok(DecodedBackup { manifest, files })
}

fn validate_manifest(manifest: &BackupManifest) -> Result<(), BackupError> {
    if manifest.format_version != BACKUP_FORMAT_VERSION {
        return Err(BackupError::UnsupportedVersion(manifest.format_version));
    }
    if manifest.files.is_empty() {
        return Err(BackupError::InvalidFormat("backup has no files".into()));
    }
    if manifest.files.len() > MAX_FILE_COUNT {
        return Err(BackupError::InvalidFormat(
            "backup file count exceeds the limit".into(),
        ));
    }
    for profile in &manifest.profiles {
        validate_profile_id(&profile.id)
            .map_err(|error| BackupError::InvalidFormat(error.to_string()))?;
    }
    let mut seen = std::collections::BTreeSet::new();
    let mut total_size = 0u64;
    for file in &manifest.files {
        safe_manifest_path(&file.path)?;
        if !seen.insert(file.path.as_str()) {
            return Err(BackupError::InvalidFormat("duplicate file entry".into()));
        }
        if file.size > MAX_FILE_BYTES {
            return Err(BackupError::InvalidFormat(format!(
                "file {} exceeds the size limit",
                file.path
            )));
        }
        let end = file
            .offset
            .checked_add(file.size)
            .ok_or_else(|| BackupError::InvalidFormat("file offset overflow".into()))?;
        if end > MAX_TOTAL_FILE_BYTES {
            return Err(BackupError::InvalidFormat(
                "backup data exceeds the size limit".into(),
            ));
        }
        total_size = total_size
            .checked_add(file.size)
            .ok_or_else(|| BackupError::InvalidFormat("backup size overflow".into()))?;
        if total_size > MAX_TOTAL_FILE_BYTES {
            return Err(BackupError::InvalidFormat(
                "backup data exceeds the size limit".into(),
            ));
        }
    }
    if manifest.scope == BackupScope::Full
        && !manifest.files.iter().any(|file| file.path == "amare.db")
    {
        return Err(BackupError::InvalidFormat(
            "Full backup is missing root database".into(),
        ));
    }
    for profile in &manifest.profiles {
        let expected = format!("profiles/{}/amare.db", profile.id);
        if !manifest.files.iter().any(|file| file.path == expected) {
            return Err(BackupError::InvalidFormat(format!(
                "missing database for profile {}",
                profile.id
            )));
        }
    }
    Ok(())
}

fn safe_manifest_path(path: &str) -> Result<(), BackupError> {
    if path.is_empty() || path.contains('\0') {
        return Err(BackupError::UnsafePath(path.to_string()));
    }
    let candidate = Path::new(path);
    if candidate.is_absolute()
        || candidate.components().any(|part| {
            matches!(
                part,
                std::path::Component::ParentDir | std::path::Component::Prefix(_)
            )
        })
    {
        return Err(BackupError::UnsafePath(path.to_string()));
    }
    if path == "amare.db" {
        return Ok(());
    }
    let mut parts = path.split('/');
    if parts.next() == Some("profiles") {
        if let Some(id) = parts.next() {
            validate_profile_id(id)
                .map_err(|error| BackupError::InvalidFormat(error.to_string()))?;
            if parts.next() == Some("amare.db") && parts.next().is_none() {
                return Ok(());
            }
        }
    }
    Err(BackupError::InvalidFormat(format!(
        "unexpected backup path: {path}"
    )))
}

fn safe_join(root: &Path, path: &str) -> Result<PathBuf, BackupError> {
    safe_manifest_path(path)?;
    Ok(root.join(path))
}

fn container_aad(flags: u8, salt: &[u8]) -> Vec<u8> {
    let mut aad = Vec::with_capacity(8 + 2 + 1 + salt.len());
    aad.extend_from_slice(MAGIC);
    aad.extend_from_slice(&CONTAINER_VERSION.to_le_bytes());
    aad.push(flags);
    aad.extend_from_slice(salt);
    aad
}

fn snapshot_connection(src: &Connection, dest: &Path) -> Result<(), BackupError> {
    if let Some(parent) = dest.parent() {
        fs::create_dir_all(parent)?;
    }
    let mut dst = Connection::open(dest)?;
    let backup = Backup::new(src, &mut dst)?;
    backup.run_to_completion(100, Duration::from_millis(3), None)?;
    drop(backup);
    Ok(())
}

fn filter_profile_snapshot(path: &Path, data: &BackupData) -> Result<(), BackupError> {
    let conn = Connection::open(path)?;
    // Category deletion must run with FKs enabled so SET NULL/cascade policies
    // preserve a valid profile DB when, for example, workspace data is excluded
    // while conversations or Library source references remain.
    conn.execute_batch("PRAGMA foreign_keys=ON;")?;
    let tx = conn.unchecked_transaction()?;
    if !data.settings {
        tx.execute_batch("DELETE FROM settings;")?;
    }
    if !data.bookmarks {
        tx.execute_batch("DELETE FROM bookmarks; DELETE FROM bookmark_folders;")?;
    }
    if !data.history {
        tx.execute_batch("DELETE FROM history_visits;")?;
    }
    if !data.sessions {
        tx.execute_batch("DELETE FROM sessions;")?;
    }
    if !data.secrets {
        tx.execute_batch("DELETE FROM secrets;")?;
    }
    if !data.library {
        tx.execute_batch(
            "DELETE FROM library_item_tags;
             DELETE FROM library_item_collections;
             DELETE FROM tags;
             DELETE FROM collections;
             DELETE FROM library_items;",
        )?;
    }
    if !data.conversations {
        tx.execute_batch(
            "DELETE FROM message_sources;
             DELETE FROM message_parts;
             DELETE FROM messages;
             DELETE FROM conversations;",
        )?;
    }
    if !data.workspaces {
        tx.execute_batch(
            "DELETE FROM task_memory_links;
             DELETE FROM task_memories;
             DELETE FROM tasks;
             DELETE FROM workspaces;",
        )?;
    }
    // Source references are shared by Library and AI citations. Keep them while
    // either owner is included; remove them when both categories are excluded.
    if !data.library && !data.conversations {
        tx.execute_batch("DELETE FROM source_references;")?;
    }
    tx.commit()?;
    let _ = conn.execute_batch("PRAGMA wal_checkpoint(TRUNCATE);");
    Ok(())
}

fn selected_data_kinds(options: &BackupOptions) -> Vec<String> {
    let mut kinds = Vec::new();
    if options.include_root {
        kinds.push("root_profile_registry".into());
    }
    if options.data.settings {
        kinds.push("settings".into());
    }
    if options.data.bookmarks {
        kinds.push("bookmarks".into());
    }
    if options.data.history {
        kinds.push("history".into());
    }
    if options.data.sessions {
        kinds.push("sessions".into());
    }
    if options.data.secrets {
        kinds.push("secrets".into());
    }
    if options.data.library {
        kinds.push("library".into());
    }
    if options.data.conversations {
        kinds.push("conversations".into());
    }
    if options.data.workspaces {
        kinds.push("workspaces".into());
    }
    kinds
}

fn encryption_label(encryption: &BackupEncryption) -> &'static str {
    match encryption {
        BackupEncryption::None => "none",
        BackupEncryption::Password(_) => "password-argon2id-xchacha20poly1305",
        BackupEncryption::Key(_) => "caller-key-xchacha20poly1305",
    }
}

fn backup_id(started: i64) -> String {
    let sequence = BACKUP_COUNTER.fetch_add(1, Ordering::Relaxed);
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or(0);
    format!("backup-{started}-{nanos}-{}-{sequence}", std::process::id())
}

fn meta_path(backup_dir: &Path, backup: &Path) -> PathBuf {
    let name = backup
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("backup");
    backup_dir.join(format!("{name}.meta.json"))
}

fn sidecar_path(backup: &Path, extension: &str) -> PathBuf {
    PathBuf::from(format!("{}.{}", backup.display(), extension))
}

fn write_meta(backup_dir: &Path, record: &BackupRecord) -> Result<(), BackupError> {
    let path = meta_path(backup_dir, &backup_dir.join(&record.file_name));
    write_json_atomic(&path, record)
}

fn remove_meta(backup_dir: &Path, backup: &Path) -> Result<(), BackupError> {
    let path = meta_path(backup_dir, backup);
    if path.exists() {
        fs::remove_file(path)?;
    }
    Ok(())
}

fn read_meta(backup_dir: &Path, backup: &Path) -> Result<BackupRecord, BackupError> {
    let path = meta_path(backup_dir, backup);
    Ok(serde_json::from_slice(&fs::read(path)?)?)
}

fn write_json_atomic(path: &Path, value: &impl Serialize) -> Result<(), BackupError> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    let temp = path.with_extension(format!("tmp-{}", std::process::id()));
    let bytes = serde_json::to_vec_pretty(value)?;
    fs::write(&temp, bytes)?;
    fs::rename(&temp, path)?;
    Ok(())
}

fn read_container(path: &Path) -> Result<Vec<u8>, BackupError> {
    let metadata = fs::metadata(path)?;
    if metadata.len() > MAX_CONTAINER_BYTES {
        return Err(BackupError::InvalidFormat(
            "backup container exceeds the size limit".into(),
        ));
    }
    Ok(fs::read(path)?)
}

fn verify_sidecar_sha(path: &Path, bytes: &[u8]) -> Result<(), BackupError> {
    let sidecar = sidecar_path(path, "sha256");
    if sidecar.exists() {
        let expected = fs::read_to_string(sidecar)?;
        if expected.trim() != sha256_hex(bytes) {
            return Err(BackupError::Integrity(
                "backup file checksum mismatch".into(),
            ));
        }
    }
    Ok(())
}

fn restore_staging_path(target_root: &Path) -> PathBuf {
    let name = target_root
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("amare");
    target_root.with_file_name(format!(
        ".{name}.restore-{}-{}",
        now_ms(),
        std::process::id()
    ))
}

fn recovery_path(target_root: &Path) -> PathBuf {
    let name = target_root
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("amare");
    target_root.with_file_name(format!(
        "{name}.restore-recovery-{}-{}",
        now_ms(),
        std::process::id()
    ))
}

fn rollback_restore(
    target_root: &Path,
    recovery: &Path,
    mode: &RestoreMode,
    _manifest: &BackupManifest,
) -> Result<(), BackupError> {
    match mode {
        RestoreMode::Full => {
            let _ = fs::remove_file(target_root.join("amare.db"));
            let _ = fs::remove_file(target_root.join("amare.db-wal"));
            let _ = fs::remove_file(target_root.join("amare.db-shm"));
            let _ = fs::remove_file(target_root.join("amare.db-journal"));
            let _ = fs::remove_dir_all(target_root.join("profiles"));
            for name in [
                "amare.db",
                "amare.db-wal",
                "amare.db-shm",
                "amare.db-journal",
            ] {
                let old = recovery.join(name);
                if old.exists() {
                    fs::rename(old, target_root.join(name))?;
                }
            }
            let old_profiles = recovery.join("profiles");
            if old_profiles.exists() {
                fs::rename(old_profiles, target_root.join("profiles"))?;
            }
        }
        RestoreMode::Profiles(ids) => {
            for id in ids {
                let target = target_root.join("profiles").join(id.as_str());
                let old = recovery.join("profiles").join(id.as_str());
                let _ = fs::remove_dir_all(&target);
                if old.exists() {
                    fs::rename(old, target)?;
                }
            }
        }
    }
    Ok(())
}

fn restore_full(
    target_root: &Path,
    staging: &Path,
    recovery: &Path,
    manifest: &BackupManifest,
) -> Result<Vec<String>, BackupError> {
    if manifest.scope != BackupScope::Full
        || !manifest.files.iter().any(|file| file.path == "amare.db")
    {
        return Err(BackupError::InvalidFormat(
            "full restore requires a Full backup with root DB".into(),
        ));
    }
    fs::create_dir_all(target_root)?;
    fs::create_dir_all(recovery)?;
    let old_root = target_root.join("amare.db");
    let old_wal = target_root.join("amare.db-wal");
    let old_shm = target_root.join("amare.db-shm");
    let old_journal = target_root.join("amare.db-journal");
    let old_profiles = target_root.join("profiles");
    let mut moved_old = Vec::new();
    let old_move_result = (|| -> Result<(), BackupError> {
        for (old, name) in [
            (&old_root, "amare.db"),
            (&old_wal, "amare.db-wal"),
            (&old_shm, "amare.db-shm"),
            (&old_journal, "amare.db-journal"),
        ] {
            if old.exists() {
                let dest = recovery.join(name);
                fs::rename(old, &dest)?;
                moved_old.push((dest, old.clone()));
            }
        }
        if old_profiles.exists() {
            let dest = recovery.join("profiles");
            fs::rename(&old_profiles, &dest)?;
            moved_old.push((dest, old_profiles));
        }
        Ok(())
    })();
    if let Err(error) = old_move_result {
        for (from, to) in moved_old.into_iter().rev() {
            let _ = fs::rename(from, to);
        }
        return Err(error);
    }
    let result = (|| -> Result<(), BackupError> {
        fs::rename(staging.join("amare.db"), target_root.join("amare.db"))?;
        if staging.join("profiles").exists() {
            fs::rename(staging.join("profiles"), target_root.join("profiles"))?;
        }
        Ok(())
    })();
    if let Err(error) = result {
        let _ = fs::remove_file(target_root.join("amare.db"));
        let _ = fs::remove_dir_all(target_root.join("profiles"));
        for (from, to) in moved_old {
            let _ = fs::rename(from, to);
        }
        return Err(error);
    }
    Ok(manifest
        .profiles
        .iter()
        .map(|profile| profile.id.clone())
        .collect())
}

fn restore_profiles(
    target_root: &Path,
    staging: &Path,
    recovery: &Path,
    ids: &[ProfileId],
    manifest: &BackupManifest,
) -> Result<Vec<String>, BackupError> {
    if ids.is_empty() {
        return Err(BackupError::NoProfiles);
    }
    fs::create_dir_all(recovery.join("profiles"))?;
    let mut moved = Vec::new();
    let result = (|| -> Result<(), BackupError> {
        for id in ids {
            validate_profile_id(id.as_str())?;
            if !manifest
                .profiles
                .iter()
                .any(|profile| profile.id == id.as_str())
            {
                return Err(BackupError::ProfileNotFound(id.to_string()));
            }
            let target = target_root.join("profiles").join(id.as_str());
            if !target.exists() {
                return Err(BackupError::ProfileNotFound(id.to_string()));
            }
            let source = staging.join("profiles").join(id.as_str());
            if !source.exists() {
                return Err(BackupError::ProfileNotFound(id.to_string()));
            }
            let old = recovery.join("profiles").join(id.as_str());
            fs::rename(&target, &old)?;
            moved.push((old, target.clone()));
            fs::rename(&source, &target)?;
        }
        Ok(())
    })();
    if let Err(error) = result {
        for (old, target) in moved.into_iter().rev() {
            let _ = fs::remove_dir_all(&target);
            let _ = fs::rename(old, target);
        }
        return Err(error);
    }
    Ok(ids.iter().map(|id| id.as_str().to_string()).collect())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn manifest_with_files(files: Vec<BackupFile>) -> BackupManifest {
        BackupManifest {
            format_version: BACKUP_FORMAT_VERSION,
            app_version: "test".into(),
            created_at_ms: 1,
            scope: BackupScope::Full,
            data_kinds: vec![],
            profiles: vec![],
            root_schema_version: Some(ROOT_SCHEMA_VERSION),
            compression: "none".into(),
            encryption: "none".into(),
            contains_secrets: false,
            consistency: "test".into(),
            files,
        }
    }

    fn file(path: &str, offset: u64, size: u64) -> BackupFile {
        BackupFile {
            path: path.into(),
            kind: BackupFileKind::RootDb,
            offset,
            size,
            sha256: "00".into(),
        }
    }

    #[test]
    fn rejects_path_traversal_and_unexpected_paths() {
        for path in [
            "../amare.db",
            "/tmp/amare.db",
            "profiles/../amare.db",
            "profiles/p1/other.db",
            "profiles/p1/amare.db/..",
            "amare.db\0",
            "",
        ] {
            assert!(safe_manifest_path(path).is_err(), "{path:?}");
        }
        assert!(safe_manifest_path("amare.db").is_ok());
        assert!(safe_manifest_path("profiles/p1/amare.db").is_ok());
    }

    #[test]
    fn rejects_duplicate_files_and_offset_overflow() {
        let duplicate = manifest_with_files(vec![file("amare.db", 0, 1), file("amare.db", 1, 1)]);
        assert!(validate_manifest(&duplicate).is_err());

        let overflow = manifest_with_files(vec![file("amare.db", u64::MAX, 1)]);
        assert!(validate_manifest(&overflow).is_err());
    }

    #[test]
    fn rejects_oversized_file_and_too_many_files() {
        let oversized = manifest_with_files(vec![file("amare.db", 0, MAX_FILE_BYTES + 1)]);
        assert!(validate_manifest(&oversized).is_err());

        let many = (0..=MAX_FILE_COUNT)
            .map(|index| file("amare.db", index as u64, 1))
            .collect();
        assert!(validate_manifest(&manifest_with_files(many)).is_err());
    }
}
