//! Encrypted password-vault core for the WinUI shell.
//!
//! The vault is stored as one authenticated blob in the existing profile
//! `secrets` table. The 32-byte data key lives in a separate per-profile file
//! protected by an OS `KeyProtector` (DPAPI on Windows). The database and the
//! key file therefore have different confidentiality boundaries.
//!
//! This module is platform-neutral: callers provide a `KeyProtector`. Windows
//! production code passes `WindowsDpapiKeyProtector`; tests use the in-memory
//! protector.

use amare_core::ProfileId;
use amare_security::{
    KeyFile, KeyProtector, VaultKey, decrypt, derive_key, encrypt, generate_salt,
};
use amare_store::{Store, StoreError};
use fs2::FileExt;
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;
use std::fs::{File, OpenOptions};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use url::Url;
use zeroize::Zeroize;

pub const VAULT_FORMAT_VERSION: u32 = 1;
pub const VAULT_SECRET_KEY: &str = "password-vault";
pub const VAULT_SECRET_GROUP: &str = "security";
const KEY_CONTEXT_PREFIX: &str = "amare.winui.password-vault.key.v1:";
const VAULT_AAD_PREFIX: &str = "amare.winui.password-vault.v1:";
const BACKUP_MAGIC: &[u8; 8] = b"AMAREVB1";
const BACKUP_VERSION: u32 = 1;
const BACKUP_AAD: &[u8] = b"amare.winui.password-vault.backup.v1";
const MIN_BACKUP_LEN: usize = 8 + 4 + 16 + 24 + 16;
const MAX_VAULT_BYTES: usize = 8 * 1024 * 1024;
const MAX_ENTRIES: usize = 10_000;

#[derive(Debug, thiserror::Error)]
pub enum VaultError {
    #[error("store error: {0}")]
    Store(#[from] StoreError),
    #[error("invalid profile")]
    InvalidProfile,
    #[error("vault is locked")]
    Locked,
    #[error("vault entry not found")]
    NotFound,
    #[error("vault conflict: expected revision {expected}, actual {actual}")]
    Conflict { expected: u64, actual: u64 },
    #[error("origin does not match the stored entry")]
    OriginMismatch,
    #[error("invalid origin")]
    InvalidOrigin,
    #[error("invalid vault input: {0}")]
    InvalidInput(String),
    #[error("vault ciphertext is corrupt")]
    Corrupt,
    #[error("unsupported vault format: {0}")]
    UnsupportedFormat(u32),
    #[error("vault authentication failed")]
    Auth,
    #[error("vault key file is missing")]
    KeyMissing,
    #[error("vault key is unavailable or corrupt")]
    KeyUnavailable,
    #[error("backup authentication failed")]
    BackupAuth,
    #[error("backup is corrupt")]
    BackupCorrupt,
    #[error("unsupported backup format: {0}")]
    BackupUnsupported(u32),
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct VaultStatus {
    pub profile_id: String,
    pub initialized: bool,
    pub unlocked: bool,
    pub entry_count: usize,
    pub tombstone_count: usize,
    pub generation: u64,
    pub key_present: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct VaultEntryView {
    pub id: u64,
    pub origin: String,
    pub username: String,
    pub title: String,
    pub created_at_ms: i64,
    pub updated_at_ms: i64,
    pub revision: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct VaultBackupSummary {
    pub format: u32,
    pub generation: u64,
    pub entry_count: usize,
    pub tombstone_count: usize,
}

#[derive(Clone, Serialize, Deserialize)]
pub struct VaultEntry {
    pub id: u64,
    pub origin: String,
    pub username: String,
    pub password: String,
    pub title: String,
    pub created_at_ms: i64,
    pub updated_at_ms: i64,
    pub revision: u64,
}

impl Drop for VaultEntry {
    fn drop(&mut self) {
        self.password.zeroize();
    }
}

impl VaultEntry {
    pub fn view(&self) -> VaultEntryView {
        VaultEntryView {
            id: self.id,
            origin: self.origin.clone(),
            username: self.username.clone(),
            title: self.title.clone(),
            created_at_ms: self.created_at_ms,
            updated_at_ms: self.updated_at_ms,
            revision: self.revision,
        }
    }
}

#[derive(Clone, Serialize, Deserialize)]
struct VaultTombstone {
    id: u64,
    revision: u64,
    deleted_at_ms: i64,
}

#[derive(Clone, Serialize, Deserialize)]
struct VaultData {
    format: u32,
    generation: u64,
    entries: Vec<VaultEntry>,
    tombstones: Vec<VaultTombstone>,
}

impl VaultData {
    fn empty() -> Self {
        Self {
            format: VAULT_FORMAT_VERSION,
            generation: 1,
            entries: Vec::new(),
            tombstones: Vec::new(),
        }
    }
}

#[derive(Default)]
struct VaultState {
    initialized: bool,
    data: Option<VaultData>,
    key: Option<VaultKey>,
    version: Option<i64>,
}

pub struct VaultService {
    root: PathBuf,
    profile: ProfileId,
    key_path: PathBuf,
    protector: Arc<dyn KeyProtector>,
    store: Mutex<Store>,
    state: Mutex<VaultState>,
}

impl VaultService {
    pub fn new(
        root: impl AsRef<Path>,
        profile: ProfileId,
        protector: Arc<dyn KeyProtector>,
    ) -> Result<Self, VaultError> {
        let profile_text = profile.as_str();
        if !is_safe_profile_id(profile_text) {
            return Err(VaultError::InvalidProfile);
        }
        let root = root.as_ref().to_path_buf();
        let store = Store::open(&root)?;
        store.open_profile(&profile)?;
        let key_path = root.join("vault").join(format!("{}.key", profile_text));
        Ok(Self {
            root,
            profile,
            key_path,
            protector,
            store: Mutex::new(store),
            state: Mutex::new(VaultState::default()),
        })
    }

    pub fn key_path(&self) -> &Path {
        &self.key_path
    }

    pub fn root_dir(&self) -> &Path {
        &self.root
    }

    pub fn profile_id(&self) -> &ProfileId {
        &self.profile
    }

    pub fn initialize(&self) -> Result<VaultStatus, VaultError> {
        let mut state = self.state.lock().map_err(|_| VaultError::Corrupt)?;
        if state.data.is_some() {
            return Ok(self.snapshot(&state));
        }
        let _key_lock = self.lock_key_file()?;
        let store = self.store.lock().map_err(|_| VaultError::Corrupt)?;
        let profile = store.open_profile(&self.profile)?;
        let existing = profile.secrets().version(VAULT_SECRET_KEY)?;
        let (key, data, version) = match existing {
            Some(_) => {
                let key = self.load_key(false)?;
                let (mut plaintext, version) = profile
                    .secrets()
                    .get_with_aad_version(
                        VAULT_SECRET_KEY,
                        key.as_bytes(),
                        &vault_aad(&self.profile),
                    )
                    .map_err(map_store_error)?
                    .ok_or(VaultError::Corrupt)?;
                let decoded = decode_data(&plaintext);
                plaintext.zeroize();
                (key, decoded?, version)
            }
            None => {
                let key = self.load_key(true)?;
                let data = VaultData::empty();
                let mut plaintext = encode_data(&data)?;
                let version = profile
                    .secrets()
                    .set_with_aad_cas(
                        VAULT_SECRET_KEY,
                        VAULT_SECRET_GROUP,
                        &plaintext,
                        key.as_bytes(),
                        &vault_aad(&self.profile),
                        None,
                    )
                    .map_err(map_store_error)?;
                plaintext.zeroize();
                (key, data, version)
            }
        };
        state.initialized = true;
        state.data = Some(data);
        state.key = Some(key);
        state.version = Some(version);
        Ok(self.snapshot(&state))
    }

    pub fn unlock(&self) -> Result<VaultStatus, VaultError> {
        let mut state = self.state.lock().map_err(|_| VaultError::Corrupt)?;
        let _key_lock = self.lock_key_file()?;
        let store = self.store.lock().map_err(|_| VaultError::Corrupt)?;
        let profile = store.open_profile(&self.profile)?;
        profile
            .secrets()
            .version(VAULT_SECRET_KEY)?
            .ok_or(VaultError::NotFound)?;
        let key = self.load_key(false)?;
        let (mut plaintext, version) = profile
            .secrets()
            .get_with_aad_version(VAULT_SECRET_KEY, key.as_bytes(), &vault_aad(&self.profile))
            .map_err(map_store_error)?
            .ok_or(VaultError::Corrupt)?;
        let decoded = decode_data(&plaintext);
        plaintext.zeroize();
        let data = decoded?;
        state.initialized = true;
        state.data = Some(data);
        state.key = Some(key);
        state.version = Some(version);
        Ok(self.snapshot(&state))
    }

    pub fn lock(&self) {
        if let Ok(mut state) = self.state.lock() {
            state.data = None;
            state.key = None;
            state.version = None;
        }
    }

    pub fn status(&self) -> Result<VaultStatus, VaultError> {
        let state = self.state.lock().map_err(|_| VaultError::Corrupt)?;
        Ok(self.snapshot(&state))
    }

    pub fn list_entries(&self) -> Result<Vec<VaultEntryView>, VaultError> {
        let state = self.state.lock().map_err(|_| VaultError::Corrupt)?;
        let data = state.data.as_ref().ok_or(VaultError::Locked)?;
        Ok(data.entries.iter().map(VaultEntry::view).collect())
    }

    pub fn get_entry(&self, id: u64) -> Result<VaultEntry, VaultError> {
        let state = self.state.lock().map_err(|_| VaultError::Corrupt)?;
        let data = state.data.as_ref().ok_or(VaultError::Locked)?;
        data.entries
            .iter()
            .find(|entry| entry.id == id)
            .cloned()
            .ok_or(VaultError::NotFound)
    }

    /// Return a credential only when the requested origin exactly matches.
    pub fn get_for_fill(&self, origin: &str, id: u64) -> Result<VaultEntry, VaultError> {
        let requested = normalize_origin(origin)?;
        let state = self.state.lock().map_err(|_| VaultError::Corrupt)?;
        let data = state.data.as_ref().ok_or(VaultError::Locked)?;
        let entry = data
            .entries
            .iter()
            .find(|entry| entry.id == id)
            .ok_or(VaultError::NotFound)?;
        if entry.origin != requested {
            return Err(VaultError::OriginMismatch);
        }
        Ok(entry.clone())
    }

    pub fn create_entry(
        &self,
        origin: &str,
        username: &str,
        password: &str,
        title: &str,
    ) -> Result<VaultEntry, VaultError> {
        let origin = normalize_origin(origin)?;
        validate_entry_fields(username, password, title)?;
        let mut state = self.state.lock().map_err(|_| VaultError::Corrupt)?;
        let mut data = state.data.as_ref().ok_or(VaultError::Locked)?.clone();
        if data.entries.len() >= MAX_ENTRIES {
            return Err(VaultError::InvalidInput("too many vault entries".into()));
        }
        let now = now_ms();
        let id = next_id(&data, now);
        let entry = VaultEntry {
            id,
            origin,
            username: username.trim().to_string(),
            password: password.to_string(),
            title: title.trim().to_string(),
            created_at_ms: now,
            updated_at_ms: now,
            revision: 1,
        };
        data.entries.push(entry.clone());
        data.generation = data.generation.saturating_add(1).max(1);
        self.persist(&mut state, data)?;
        Ok(entry)
    }

    pub fn update_entry(
        &self,
        id: u64,
        expected_revision: u64,
        origin: &str,
        username: &str,
        password: &str,
        title: &str,
    ) -> Result<VaultEntry, VaultError> {
        let origin = normalize_origin(origin)?;
        validate_entry_fields(username, password, title)?;
        let mut state = self.state.lock().map_err(|_| VaultError::Corrupt)?;
        let mut data = state.data.as_ref().ok_or(VaultError::Locked)?.clone();
        let entry = data
            .entries
            .iter_mut()
            .find(|entry| entry.id == id)
            .ok_or(VaultError::NotFound)?;
        if entry.revision != expected_revision {
            return Err(VaultError::Conflict {
                expected: expected_revision,
                actual: entry.revision,
            });
        }
        entry.origin = origin;
        entry.username = username.trim().to_string();
        entry.password.zeroize();
        entry.password = password.to_string();
        entry.title = title.trim().to_string();
        entry.updated_at_ms = now_ms();
        entry.revision = entry.revision.saturating_add(1);
        let updated = entry.clone();
        data.generation = data.generation.saturating_add(1).max(1);
        self.persist(&mut state, data)?;
        Ok(updated)
    }

    /// Returns the tombstone revision.
    pub fn delete_entry(&self, id: u64, expected_revision: u64) -> Result<u64, VaultError> {
        let mut state = self.state.lock().map_err(|_| VaultError::Corrupt)?;
        let mut data = state.data.as_ref().ok_or(VaultError::Locked)?.clone();
        let index = data
            .entries
            .iter()
            .position(|entry| entry.id == id)
            .ok_or(VaultError::NotFound)?;
        let entry = &data.entries[index];
        if entry.revision != expected_revision {
            return Err(VaultError::Conflict {
                expected: expected_revision,
                actual: entry.revision,
            });
        }
        let revision = entry.revision.saturating_add(1).max(1);
        data.entries.remove(index);
        data.tombstones.push(VaultTombstone {
            id,
            revision,
            deleted_at_ms: now_ms(),
        });
        data.generation = data.generation.saturating_add(1).max(1);
        self.persist(&mut state, data)?;
        Ok(revision)
    }

    pub fn export_backup(&self, password: &str) -> Result<Vec<u8>, VaultError> {
        if password.is_empty() {
            return Err(VaultError::InvalidInput(
                "backup password is required".into(),
            ));
        }
        let state = self.state.lock().map_err(|_| VaultError::Corrupt)?;
        let data = state.data.as_ref().ok_or(VaultError::Locked)?;
        let mut plaintext = encode_data(data)?;
        let result = (|| {
            let salt = generate_salt();
            let mut key =
                derive_key(password.as_bytes(), &salt).map_err(|_| VaultError::BackupAuth)?;
            let encrypted = encrypt(&key, &plaintext, BACKUP_AAD);
            key.zeroize();
            let blob = encrypted.map_err(|_| VaultError::BackupAuth)?;
            let mut output = Vec::with_capacity(8 + 4 + 16 + blob.len());
            output.extend_from_slice(BACKUP_MAGIC);
            output.extend_from_slice(&BACKUP_VERSION.to_le_bytes());
            output.extend_from_slice(&salt);
            output.extend_from_slice(&blob);
            Ok(output)
        })();
        plaintext.zeroize();
        result
    }

    pub fn validate_backup(
        &self,
        payload: &[u8],
        password: &str,
    ) -> Result<VaultBackupSummary, VaultError> {
        let data = decode_backup(payload, password)?;
        Ok(VaultBackupSummary {
            format: data.format,
            generation: data.generation,
            entry_count: data.entries.len(),
            tombstone_count: data.tombstones.len(),
        })
    }

    pub fn import_backup(&self, payload: &[u8], password: &str) -> Result<VaultStatus, VaultError> {
        let imported = decode_backup(payload, password)?;
        let mut state = self.state.lock().map_err(|_| VaultError::Corrupt)?;
        let current = state.data.as_ref().ok_or(VaultError::Locked)?;
        let mut data = imported;
        data.generation = data
            .generation
            .max(current.generation.saturating_add(1))
            .max(1);
        self.persist(&mut state, data)?;
        Ok(self.snapshot(&state))
    }

    fn lock_key_file(&self) -> Result<File, VaultError> {
        if let Some(parent) = self.key_path.parent() {
            std::fs::create_dir_all(parent).map_err(|_| VaultError::KeyUnavailable)?;
        }
        let lock_path = self.key_path.with_extension("lock");
        let file = OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(&lock_path)
            .map_err(|_| VaultError::KeyUnavailable)?;
        FileExt::lock_exclusive(&file).map_err(|_| VaultError::KeyUnavailable)?;
        Ok(file)
    }

    fn load_key(&self, create: bool) -> Result<VaultKey, VaultError> {
        if !create && !self.key_path.exists() {
            return Err(VaultError::KeyMissing);
        }
        let key_file = KeyFile::new(&self.key_path);
        let context = key_context(&self.profile);
        let mut key = if create {
            key_file
                .load_or_create(self.protector.as_ref(), &context)
                .map_err(|_| VaultError::KeyUnavailable)?
        } else {
            key_file
                .load(self.protector.as_ref(), &context)
                .map_err(|_| VaultError::KeyUnavailable)?
        };
        // A simultaneous first-run can race on create. The loser must not
        // write a vault with a key file that another process replaced.
        let current = key_file
            .load(self.protector.as_ref(), &context)
            .map_err(|_| VaultError::KeyUnavailable)?;
        if current != key {
            key.zeroize();
            return Err(VaultError::KeyUnavailable);
        }
        Ok(VaultKey::new(key))
    }

    fn persist(&self, state: &mut VaultState, data: VaultData) -> Result<(), VaultError> {
        let key = state.key.as_ref().ok_or(VaultError::Locked)?;
        let mut key_bytes = *key.as_bytes();
        let _key_lock = self.lock_key_file()?;
        if !self.key_path.exists() {
            key_bytes.zeroize();
            return Err(VaultError::KeyMissing);
        }
        let current = KeyFile::new(&self.key_path)
            .load(self.protector.as_ref(), &key_context(&self.profile))
            .map_err(|_| VaultError::KeyUnavailable)?;
        if current != key_bytes {
            key_bytes.zeroize();
            return Err(VaultError::KeyUnavailable);
        }
        let expected = state.version;
        let result = (|| {
            let mut plaintext = encode_data(&data)?;
            let store = self.store.lock().map_err(|_| VaultError::Corrupt)?;
            let profile = store.open_profile(&self.profile)?;
            let version = profile
                .secrets()
                .set_with_aad_cas(
                    VAULT_SECRET_KEY,
                    VAULT_SECRET_GROUP,
                    &plaintext,
                    &key_bytes,
                    &vault_aad(&self.profile),
                    expected,
                )
                .map_err(map_store_error)?;
            plaintext.zeroize();
            Ok::<i64, VaultError>(version)
        })();
        key_bytes.zeroize();
        let version = result?;
        state.data = Some(data);
        state.version = Some(version);
        state.initialized = true;
        Ok(())
    }

    fn snapshot(&self, state: &VaultState) -> VaultStatus {
        let (entry_count, tombstone_count, generation) = match state.data.as_ref() {
            Some(data) => (data.entries.len(), data.tombstones.len(), data.generation),
            None => (0, 0, 0),
        };
        VaultStatus {
            profile_id: self.profile.as_str().to_string(),
            initialized: state.initialized,
            unlocked: state.data.is_some(),
            entry_count,
            tombstone_count,
            generation,
            key_present: self.key_path.exists(),
        }
    }
}

impl Drop for VaultService {
    fn drop(&mut self) {
        self.lock();
    }
}

fn is_safe_profile_id(id: &str) -> bool {
    !id.is_empty()
        && id.len() <= 128
        && id != "."
        && id != ".."
        && id
            .chars()
            .all(|ch| ch.is_ascii_alphanumeric() || ch == '-' || ch == '_' || ch == '.')
}

fn key_context(profile: &ProfileId) -> String {
    format!("{KEY_CONTEXT_PREFIX}{}", profile.as_str())
}

fn vault_aad(profile: &ProfileId) -> Vec<u8> {
    format!("{VAULT_AAD_PREFIX}{}", profile.as_str()).into_bytes()
}

fn map_store_error(error: StoreError) -> VaultError {
    match error {
        StoreError::Security(_) => VaultError::Auth,
        StoreError::Conflict { expected, actual } => VaultError::Conflict {
            expected: expected.unwrap_or(0).max(0) as u64,
            actual: actual.unwrap_or(0).max(0) as u64,
        },
        other => VaultError::Store(other),
    }
}

fn normalize_origin(value: &str) -> Result<String, VaultError> {
    let parsed = Url::parse(value.trim()).map_err(|_| VaultError::InvalidOrigin)?;
    if !matches!(parsed.scheme(), "http" | "https")
        || parsed.host_str().is_none()
        || !parsed.username().is_empty()
        || parsed.password().is_some()
    {
        return Err(VaultError::InvalidOrigin);
    }
    Ok(parsed.origin().ascii_serialization())
}

fn validate_entry_fields(username: &str, password: &str, title: &str) -> Result<(), VaultError> {
    if username.trim().is_empty() || username.chars().count() > 512 {
        return Err(VaultError::InvalidInput(
            "username must be 1..=512 characters".into(),
        ));
    }
    if password.is_empty() || password.chars().count() > 8192 {
        return Err(VaultError::InvalidInput(
            "password must be 1..=8192 characters".into(),
        ));
    }
    if title.chars().count() > 512 {
        return Err(VaultError::InvalidInput(
            "title must be <= 512 characters".into(),
        ));
    }
    Ok(())
}

fn next_id(data: &VaultData, now: i64) -> u64 {
    let max = data
        .entries
        .iter()
        .map(|entry| entry.id)
        .chain(data.tombstones.iter().map(|tombstone| tombstone.id))
        .max()
        .unwrap_or(0)
        .max(now.max(0) as u64);
    max.saturating_add(1).max(1)
}

fn encode_data(data: &VaultData) -> Result<Vec<u8>, VaultError> {
    let plaintext = serde_json::to_vec(data).map_err(|_| VaultError::Corrupt)?;
    if plaintext.len() > MAX_VAULT_BYTES {
        return Err(VaultError::InvalidInput("vault is too large".into()));
    }
    Ok(plaintext)
}

fn decode_data(plaintext: &[u8]) -> Result<VaultData, VaultError> {
    let data: VaultData = serde_json::from_slice(plaintext).map_err(|_| VaultError::Corrupt)?;
    validate_data(data)
}

fn validate_data(mut data: VaultData) -> Result<VaultData, VaultError> {
    if data.format != VAULT_FORMAT_VERSION {
        return Err(VaultError::UnsupportedFormat(data.format));
    }
    if data.generation == 0 || data.entries.len() > MAX_ENTRIES {
        return Err(VaultError::Corrupt);
    }
    let mut ids = BTreeSet::new();
    for entry in &data.entries {
        if entry.id == 0 || entry.revision == 0 || !ids.insert(entry.id) {
            return Err(VaultError::Corrupt);
        }
        if normalize_origin(&entry.origin).ok().as_deref() != Some(entry.origin.as_str()) {
            return Err(VaultError::Corrupt);
        }
        validate_entry_fields(&entry.username, &entry.password, &entry.title)
            .map_err(|_| VaultError::Corrupt)?;
    }
    let mut tombstone_ids = BTreeSet::new();
    for tombstone in &data.tombstones {
        if tombstone.id == 0 || tombstone.revision == 0 || !tombstone_ids.insert(tombstone.id) {
            return Err(VaultError::Corrupt);
        }
        if ids.contains(&tombstone.id) {
            return Err(VaultError::Corrupt);
        }
    }
    data.entries.sort_by_key(|entry| entry.id);
    data.tombstones.sort_by_key(|tombstone| tombstone.id);
    Ok(data)
}

fn decode_backup(payload: &[u8], password: &str) -> Result<VaultData, VaultError> {
    if password.is_empty()
        || payload.len() < MIN_BACKUP_LEN
        || payload.len() > MAX_VAULT_BYTES + 1024
    {
        return Err(VaultError::BackupCorrupt);
    }
    if &payload[..8] != BACKUP_MAGIC {
        return Err(VaultError::BackupCorrupt);
    }
    let version = u32::from_le_bytes(payload[8..12].try_into().unwrap_or_default());
    if version != BACKUP_VERSION {
        return Err(VaultError::BackupUnsupported(version));
    }
    let salt = &payload[12..28];
    let blob = &payload[28..];
    let mut key = derive_key(password.as_bytes(), salt).map_err(|_| VaultError::BackupAuth)?;
    let decrypted = decrypt(&key, blob, BACKUP_AAD);
    key.zeroize();
    let mut plaintext = decrypted.map_err(|_| VaultError::BackupAuth)?;
    let parsed = decode_data(&plaintext);
    plaintext.zeroize();
    let data = parsed.map_err(|error| match error {
        VaultError::UnsupportedFormat(version) => VaultError::BackupUnsupported(version),
        _ => VaultError::BackupCorrupt,
    })?;
    Ok(data)
}

fn now_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as i64
}
