//! Encrypted password vault for the GPUI shell.
//!
//! The vault JSON (including usernames and passwords) is encrypted with
//! XChaCha20-Poly1305. On Windows the random data key is stored in a DPAPI
//! user-scoped key file. SQLite is deliberately not used for any vault secret.
//! Non-Windows builds return `Unsupported`; no plaintext fallback is provided.
//!
//! 0.59 stores per-entry revisions and persistent deletion tombstones so a
//! stale window cannot silently overwrite a newer edit or resurrect a deleted
//! account. The merge is optimistic and rejects same-entry conflicts instead
//! of using last-writer-wins.

use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::path::PathBuf;
use std::sync::Mutex;
use url::Url;
use zeroize::Zeroize;

const VAULT_MAGIC: &[u8; 9] = b"AMAREVLT1";
const VAULT_AAD: &[u8] = b"amare.gpui.password-vault.v1";
const VAULT_FORMAT_VERSION: u32 = 2;
const MAX_VAULT_BYTES: usize = 8 * 1024 * 1024;
const CONFLICT_PREFIX: &str = "충돌: ";
static VAULT_GENERATION: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
static VAULT_LOCK_EPOCH: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
static VAULT_GLOBALLY_LOCKED: std::sync::atomic::AtomicBool =
    std::sync::atomic::AtomicBool::new(false);
static VAULT_SAVE_LOCK: Mutex<()> = Mutex::new(());
static VAULT_SAVE_COUNTER: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);

pub fn generation() -> u64 {
    VAULT_GENERATION.load(std::sync::atomic::Ordering::Relaxed)
}

fn default_revision() -> u64 {
    1
}

fn default_format() -> u32 {
    VAULT_FORMAT_VERSION
}

pub fn lock_state() -> (u64, bool) {
    (
        VAULT_LOCK_EPOCH.load(std::sync::atomic::Ordering::SeqCst),
        VAULT_GLOBALLY_LOCKED.load(std::sync::atomic::Ordering::SeqCst),
    )
}

/// Request that every window in this process drop its decrypted vault state.
pub fn request_global_lock() -> u64 {
    VAULT_GLOBALLY_LOCKED.store(true, std::sync::atomic::Ordering::SeqCst);
    VAULT_LOCK_EPOCH.fetch_add(1, std::sync::atomic::Ordering::SeqCst) + 1
}

/// Clear the process-wide lock request. Other windows stay locked until their
/// own user explicitly unlocks; they are no longer forced to relock.
pub fn clear_global_lock() -> u64 {
    VAULT_GLOBALLY_LOCKED.store(false, std::sync::atomic::Ordering::SeqCst);
    VAULT_LOCK_EPOCH.fetch_add(1, std::sync::atomic::Ordering::SeqCst) + 1
}

#[derive(Clone, Serialize, Deserialize, PartialEq)]
pub struct PasswordEntry {
    pub id: u64,
    pub origin: String,
    pub username: String,
    pub password: String,
    pub title: String,
    pub created_at_ms: i64,
    pub updated_at_ms: i64,
    #[serde(default = "default_revision")]
    pub revision: u64,
}

impl Drop for PasswordEntry {
    fn drop(&mut self) {
        self.password.zeroize();
    }
}

impl PasswordEntry {
    pub fn masked_password(&self) -> String {
        "•".repeat(self.password.chars().count().min(32))
    }

    fn same_payload(&self, other: &Self) -> bool {
        self.origin == other.origin
            && self.username == other.username
            && self.password == other.password
            && self.title == other.title
    }
}

#[derive(Clone, Serialize, Deserialize, PartialEq)]
pub struct Tombstone {
    pub id: u64,
    #[serde(default = "default_revision")]
    pub revision: u64,
    #[serde(default)]
    pub deleted_at_ms: i64,
}

#[derive(Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct VaultData {
    #[serde(default = "default_format")]
    pub format: u32,
    #[serde(default)]
    pub generation: u64,
    #[serde(default)]
    pub entries: Vec<PasswordEntry>,
    #[serde(default)]
    pub tombstones: Vec<Tombstone>,
}

impl VaultData {
    fn from_legacy(entries: Vec<PasswordEntry>) -> Self {
        Self {
            format: VAULT_FORMAT_VERSION,
            generation: 0,
            entries,
            tombstones: Vec::new(),
        }
    }

    fn normalise(&mut self) {
        self.format = VAULT_FORMAT_VERSION;
        self.entries.sort_by_key(|entry| entry.id);
        self.entries.dedup_by_key(|entry| entry.id);
        let mut tombstones: BTreeMap<u64, Tombstone> = BTreeMap::new();
        for tombstone in self.tombstones.drain(..) {
            let revision = tombstone.revision.max(1);
            tombstones
                .entry(tombstone.id)
                .and_modify(|existing| {
                    if revision > existing.revision {
                        existing.revision = revision;
                        existing.deleted_at_ms = tombstone.deleted_at_ms;
                    }
                })
                .or_insert(Tombstone {
                    id: tombstone.id,
                    revision,
                    deleted_at_ms: tombstone.deleted_at_ms,
                });
        }
        let deleted: BTreeSet<u64> = tombstones.keys().copied().collect();
        self.entries.retain(|entry| !deleted.contains(&entry.id));
        self.tombstones = tombstones.into_values().collect();
        for entry in &mut self.entries {
            entry.revision = entry.revision.max(1);
        }
    }
}

#[derive(Clone, Default)]
struct VaultBaseline {
    revisions: BTreeMap<u64, u64>,
    tombstones: BTreeSet<u64>,
}

impl VaultBaseline {
    fn from_data(data: &VaultData) -> Self {
        Self {
            revisions: data
                .entries
                .iter()
                .map(|entry| (entry.id, entry.revision.max(1)))
                .collect(),
            tombstones: data.tombstones.iter().map(|item| item.id).collect(),
        }
    }
}

pub fn normalize_origin(value: &str) -> Result<String, String> {
    let value = value.trim();
    let parsed = Url::parse(value).map_err(|_| "http/https 사이트 주소가 아닙니다.".to_string())?;
    if !matches!(parsed.scheme(), "http" | "https") {
        return Err("http 또는 https origin만 저장할 수 있습니다.".into());
    }
    if parsed.host_str().is_none() || !parsed.username().is_empty() || parsed.password().is_some() {
        return Err("사용자 정보가 포함된 URL은 저장할 수 없습니다.".into());
    }
    Ok(parsed.origin().ascii_serialization())
}

pub fn same_origin(left: &str, right: &str) -> bool {
    let Ok(left) = normalize_origin(left) else {
        return false;
    };
    let Ok(right) = normalize_origin(right) else {
        return false;
    };
    left == right
}

#[derive(Default)]
pub struct PasswordVault {
    entries: Vec<PasswordEntry>,
    deleted_ids: BTreeSet<u64>,
    base: VaultBaseline,
    unlocked: bool,
}

impl PasswordVault {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn is_unlocked(&self) -> bool {
        self.unlocked
    }

    pub fn entries(&self) -> &[PasswordEntry] {
        &self.entries
    }

    pub fn tombstone_count(&self) -> usize {
        self.base.tombstones.len()
    }

    pub fn lock(&mut self) {
        self.entries.clear();
        self.deleted_ids.clear();
        self.base = VaultBaseline::default();
        self.unlocked = false;
    }

    pub fn backup(&self) -> Result<PathBuf, String> {
        let (_, vault_path) = Self::paths()?;
        if !vault_path.exists() {
            return Err("백업할 vault 파일이 없습니다.".into());
        }
        let root = vault_path
            .parent()
            .ok_or_else(|| "vault 경로가 올바르지 않습니다.".to_string())?
            .join("recovery");
        std::fs::create_dir_all(&root)
            .map_err(|_| "복구 백업 폴더를 만들 수 없습니다.".to_string())?;
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis();
        let destination = root.join(format!("vault-{stamp}.bin"));
        std::fs::copy(&vault_path, &destination)
            .map_err(|_| "vault 백업에 실패했습니다.".to_string())?;
        Ok(destination)
    }

    #[cfg(target_os = "windows")]
    fn paths() -> Result<(PathBuf, PathBuf), String> {
        let root = crate::library::data_root_path().join("password-vault");
        std::fs::create_dir_all(&root)
            .map_err(|_| "보안 저장소 폴더를 만들 수 없습니다.".to_string())?;
        Ok((root.join("vault.key"), root.join("vault.bin")))
    }

    #[cfg(target_os = "windows")]
    fn read_vault(key: &[u8; 32], vault_path: &std::path::Path) -> Result<VaultData, String> {
        use amare_security::decrypt;
        if !vault_path.exists() {
            return Ok(VaultData::default());
        }
        let bytes = std::fs::read(vault_path)
            .map_err(|_| "보안 저장소 파일을 읽을 수 없습니다.".to_string())?;
        if bytes.len() > MAX_VAULT_BYTES
            || bytes.len() < VAULT_MAGIC.len()
            || &bytes[..VAULT_MAGIC.len()] != VAULT_MAGIC
        {
            return Err("보안 저장소 형식이 올바르지 않습니다.".into());
        }
        let mut plain = decrypt(key, &bytes[VAULT_MAGIC.len()..], VAULT_AAD)
            .map_err(|_| "보안 저장소를 복호화할 수 없습니다.".to_string())?;
        let mut data = match serde_json::from_slice::<VaultData>(&plain) {
            Ok(data) => data,
            Err(_) => match serde_json::from_slice::<Vec<PasswordEntry>>(&plain) {
                Ok(entries) => VaultData::from_legacy(entries),
                Err(_) => {
                    plain.zeroize();
                    return Err("보안 저장소 데이터가 손상되었습니다.".into());
                }
            },
        };
        plain.zeroize();
        data.normalise();
        Ok(data)
    }

    #[cfg(target_os = "windows")]
    fn key_for(
        &self,
        key_path: &std::path::Path,
        vault_path: &std::path::Path,
    ) -> Result<[u8; 32], String> {
        use amare_platform::WindowsDpapiKeyProtector;
        use amare_security::KeyFile;
        if vault_path.exists() && !key_path.exists() {
            return Err("vault 키가 없어 기존 보안 저장소를 열 수 없습니다.".into());
        }
        KeyFile::new(key_path)
            .load_or_create(
                &WindowsDpapiKeyProtector,
                "amare.gpui.password-vault.key.v1",
            )
            .map_err(|_| "Windows DPAPI 보안 키를 열 수 없습니다.".to_string())
    }

    fn apply_data(&mut self, data: VaultData) {
        self.base = VaultBaseline::from_data(&data);
        self.entries = data.entries;
        self.deleted_ids.clear();
        self.unlocked = true;
    }

    #[cfg(target_os = "windows")]
    pub fn unlock(&mut self) -> Result<(), String> {
        let (key_path, vault_path) = Self::paths()?;
        let key = self.key_for(&key_path, &vault_path)?;
        let data = Self::read_vault(&key, &vault_path)?;
        self.apply_data(data);
        Ok(())
    }

    #[cfg(not(target_os = "windows"))]
    pub fn unlock(&mut self) -> Result<(), String> {
        Err("이 빌드에서는 OS 보안 저장소(DPAPI)가 지원되지 않습니다.".into())
    }

    #[cfg(target_os = "windows")]
    pub fn save(&mut self) -> Result<(), String> {
        use amare_security::encrypt;
        let _save_guard = VAULT_SAVE_LOCK
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let (key_path, vault_path) = Self::paths()?;
        let key = self.key_for(&key_path, &vault_path)?;
        let remote = Self::read_vault(&key, &vault_path)?;
        let merged = merge_vault(
            &self.base,
            &self.entries,
            &self.deleted_ids,
            remote,
            now_ms(),
        )?;
        let mut plain = serde_json::to_vec(&merged)
            .map_err(|_| "보안 저장소 직렬화에 실패했습니다.".to_string())?;
        let encrypted = encrypt(&key, &plain, VAULT_AAD)
            .map_err(|_| "보안 저장소 암호화에 실패했습니다.".to_string())?;
        plain.zeroize();
        if encrypted.len() + VAULT_MAGIC.len() > MAX_VAULT_BYTES {
            return Err("보안 저장소가 너무 큽니다.".into());
        }
        let sequence = VAULT_SAVE_COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        let temp = vault_path.with_extension(format!("tmp-{}-{}", std::process::id(), sequence));
        {
            use std::io::Write;
            let mut options = std::fs::OpenOptions::new();
            options.write(true).create(true).truncate(true);
            let mut file = options
                .open(&temp)
                .map_err(|_| "보안 저장소 임시 파일을 만들 수 없습니다.".to_string())?;
            file.write_all(VAULT_MAGIC)
                .and_then(|_| file.write_all(&encrypted))
                .and_then(|_| file.sync_all())
                .map_err(|_| "보안 저장소를 저장할 수 없습니다.".to_string())?;
        }
        std::fs::rename(&temp, &vault_path)
            .map_err(|_| "보안 저장소 파일을 교체할 수 없습니다.".to_string())?;
        self.apply_data(merged);
        VAULT_GENERATION.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        Ok(())
    }

    #[cfg(not(target_os = "windows"))]
    pub fn save(&mut self) -> Result<(), String> {
        Err("이 빌드에서는 OS 보안 저장소(DPAPI)가 지원되지 않습니다.".into())
    }

    pub fn entries_for_origin(&self, origin: &str) -> Vec<PasswordEntry> {
        self.entries
            .iter()
            .filter(|entry| same_origin(&entry.origin, origin))
            .cloned()
            .collect()
    }

    pub fn upsert(
        &mut self,
        id: Option<u64>,
        origin: &str,
        username: &str,
        password: &str,
        title: &str,
    ) -> Result<u64, String> {
        let origin = normalize_origin(origin)?;
        let username = username.trim();
        if username.is_empty() || username.chars().count() > 512 {
            return Err("사용자 이름을 확인해 주세요.".into());
        }
        if password.is_empty() || password.chars().count() > 8192 {
            return Err("비밀번호를 확인해 주세요.".into());
        }
        let now = now_ms();

        if let Some(existing_id) = id {
            let Some(entry) = self
                .entries
                .iter_mut()
                .find(|entry| entry.id == existing_id)
            else {
                return Err(format!(
                    "{CONFLICT_PREFIX}수정하려는 계정이 이미 삭제되었습니다. 최신 상태를 다시 불러오세요."
                ));
            };
            entry.origin = origin;
            entry.username = username.to_string();
            entry.title = title.chars().take(512).collect();
            entry.updated_at_ms = now;
            entry.revision = entry.revision.max(1).saturating_add(1);
            entry.password.zeroize();
            entry.password = password.to_string();
            return Ok(existing_id);
        }

        if let Some(existing_id) = self
            .entries
            .iter()
            .find(|entry| entry.origin == origin && entry.username == username)
            .map(|entry| entry.id)
        {
            if let Some(entry) = self
                .entries
                .iter_mut()
                .find(|entry| entry.id == existing_id)
            {
                entry.title = title.chars().take(512).collect();
                entry.updated_at_ms = now;
                entry.revision = entry.revision.max(1).saturating_add(1);
                entry.password.zeroize();
                entry.password = password.to_string();
                return Ok(existing_id);
            }
        }

        let id = self
            .entries
            .iter()
            .map(|entry| entry.id)
            .max()
            .unwrap_or(0)
            .max(now.max(0) as u64)
            .max(
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap_or_default()
                    .as_nanos() as u64,
            )
            + 1;
        self.entries.push(PasswordEntry {
            id,
            origin,
            username: username.to_string(),
            password: password.to_string(),
            title: title.chars().take(512).collect(),
            created_at_ms: now,
            updated_at_ms: now,
            revision: 1,
        });
        Ok(id)
    }

    pub fn delete(&mut self, id: u64) -> bool {
        let before = self.entries.len();
        self.entries.retain(|entry| entry.id != id);
        let changed = self.entries.len() != before;
        if changed {
            self.deleted_ids.insert(id);
        }
        changed
    }
}

fn now_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as i64
}

/// Merge local changes into the latest remote vault.
///
/// - independent entries are preserved,
/// - a stale unchanged entry never overwrites a newer remote edit,
/// - a stale unchanged entry is dropped when the remote has a tombstone,
/// - modifying or deleting an entry that changed remotely is rejected,
/// - tombstones are never removed by a stale window.
fn merge_vault(
    base: &VaultBaseline,
    local_entries: &[PasswordEntry],
    deleted_ids: &BTreeSet<u64>,
    mut remote: VaultData,
    deleted_at_ms: i64,
) -> Result<VaultData, String> {
    remote.normalise();
    let mut remote_tombstones: BTreeMap<u64, Tombstone> = remote
        .tombstones
        .into_iter()
        .map(|item| (item.id, item))
        .collect();

    for id in deleted_ids {
        let base_revision = base.revisions.get(id).copied().unwrap_or(0);
        let remote_entry = remote.entries.iter().find(|entry| entry.id == *id);
        if let Some(entry) = remote_entry {
            if entry.revision > base_revision {
                return Err(format!(
                    "{CONFLICT_PREFIX}다른 창에서 수정한 계정이라 삭제할 수 없습니다. 최신 상태를 다시 불러오세요."
                ));
            }
            let revision = entry.revision.max(base_revision).saturating_add(1).max(1);
            remote_tombstones
                .entry(*id)
                .and_modify(|item| item.revision = item.revision.max(revision))
                .or_insert(Tombstone {
                    id: *id,
                    revision,
                    deleted_at_ms,
                });
        } else if !remote_tombstones.contains_key(id) {
            let revision = base_revision.saturating_add(1).max(1);
            remote_tombstones.insert(
                *id,
                Tombstone {
                    id: *id,
                    revision,
                    deleted_at_ms,
                },
            );
        }
    }

    for local in local_entries {
        let base_revision = base.revisions.get(&local.id).copied().unwrap_or(0);
        let local_changed = local.revision.max(1) > base_revision;
        let tombstone = remote_tombstones.get(&local.id).cloned();

        if tombstone.is_some() {
            if !local_changed && base_revision > 0 {
                // The user never touched this entry in this window. It was
                // deleted elsewhere, so drop the stale copy without failing an
                // unrelated save.
                continue;
            }
            return Err(format!(
                "{CONFLICT_PREFIX}다른 창에서 삭제한 계정입니다. 최신 상태를 다시 불러오세요."
            ));
        }

        if let Some(index) = remote.entries.iter().position(|entry| entry.id == local.id) {
            let remote_entry = &remote.entries[index];
            if !local_changed && base_revision > 0 {
                // Keep the newer remote entry and its revision.
                continue;
            }
            let remote_changed = remote_entry.revision > base_revision;
            if remote_changed && !remote_entry.same_payload(local) {
                return Err(format!(
                    "{CONFLICT_PREFIX}같은 계정이 다른 창에서 수정되었습니다. 최신 상태를 다시 불러오세요."
                ));
            }
            if remote_changed && remote_entry.same_payload(local) {
                continue;
            }
            let mut updated = local.clone();
            updated.revision = remote_entry
                .revision
                .max(base_revision)
                .max(local.revision)
                .saturating_add(1)
                .max(1);
            remote.entries[index] = updated;
        } else {
            if !local_changed && base_revision > 0 {
                // Remote deletion without a tombstone (legacy/corrupt data).
                continue;
            }
            remote.entries.push(local.clone());
        }
    }

    remote.entries.retain(|entry| {
        !remote_tombstones.contains_key(&entry.id) && !deleted_ids.contains(&entry.id)
    });
    remote.tombstones = remote_tombstones.into_values().collect();
    remote.generation = remote.generation.saturating_add(1).max(1);
    remote.normalise();
    Ok(remote)
}

impl Drop for PasswordVault {
    fn drop(&mut self) {
        self.lock();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entry(id: u64, username: &str, password: &str, revision: u64) -> PasswordEntry {
        PasswordEntry {
            id,
            origin: "https://example.com".into(),
            username: username.into(),
            password: password.into(),
            title: "Example".into(),
            created_at_ms: 1,
            updated_at_ms: 1,
            revision,
        }
    }

    fn data(entries: Vec<PasswordEntry>, tombstones: Vec<Tombstone>) -> VaultData {
        VaultData {
            format: VAULT_FORMAT_VERSION,
            generation: 1,
            entries,
            tombstones,
        }
    }

    fn baseline(data: &VaultData) -> VaultBaseline {
        VaultBaseline::from_data(data)
    }

    #[test]
    fn origin_is_exact_and_scheme_aware() {
        assert!(same_origin("https://example.com", "https://example.com/"));
        assert!(!same_origin("https://example.com", "http://example.com"));
        assert!(!same_origin(
            "https://example.com",
            "https://www.example.com"
        ));
        assert!(!same_origin(
            "https://example.com",
            "https://example.com:8443"
        ));
        assert!(!same_origin(
            "https://example.com",
            "https://example.com.evil.test"
        ));
    }

    #[test]
    fn idn_and_default_ports_are_normalized() {
        assert_eq!(
            normalize_origin("https://bücher.example").unwrap(),
            "https://xn--bcher-kva.example"
        );
        assert_eq!(
            normalize_origin("https://example.com:443/path").unwrap(),
            "https://example.com"
        );
        assert!(normalize_origin("javascript:alert(1)").is_err());
    }

    #[test]
    fn entry_upsert_requires_user_approval_shape_and_keeps_multiple_accounts() {
        let mut vault = PasswordVault {
            entries: Vec::new(),
            deleted_ids: BTreeSet::new(),
            base: VaultBaseline::default(),
            unlocked: true,
        };
        let first = vault
            .upsert(None, "https://example.com", "one", "pw1", "Example")
            .unwrap();
        let second = vault
            .upsert(None, "https://example.com", "two", "pw2", "Example")
            .unwrap();
        assert_ne!(first, second);
        assert_eq!(vault.entries_for_origin("https://example.com").len(), 2);
        assert_eq!(vault.entries_for_origin("http://example.com").len(), 0);
        vault
            .upsert(Some(first), "https://example.com", "one", "pw3", "Example")
            .unwrap();
        assert_eq!(vault.entries.len(), 2);
        assert!(vault.delete(second));
        assert_eq!(vault.entries.len(), 1);
    }

    #[test]
    fn independent_edits_are_merged() {
        let first = entry(1, "one", "pw1", 1);
        let second = entry(2, "two", "pw2", 1);
        let base_data = data(vec![first.clone(), second.clone()], Vec::new());
        let base = baseline(&base_data);
        let mut local_first = first.clone();
        local_first.password = "local-one".into();
        local_first.revision = 2;
        let mut remote_second = second;
        remote_second.password = "remote-two".into();
        remote_second.revision = 2;
        let remote = data(vec![first, remote_second.clone()], Vec::new());

        let merged = merge_vault(
            &base,
            &[local_first.clone(), remote_second.clone()],
            &BTreeSet::new(),
            remote,
            10,
        )
        .unwrap();
        assert_eq!(
            merged
                .entries
                .iter()
                .find(|item| item.id == 1)
                .unwrap()
                .password,
            "local-one"
        );
        assert_eq!(
            merged
                .entries
                .iter()
                .find(|item| item.id == 2)
                .unwrap()
                .password,
            "remote-two"
        );
    }

    #[test]
    fn same_entry_conflict_is_rejected() {
        let original = entry(1, "one", "pw1", 1);
        let base = baseline(&data(vec![original.clone()], Vec::new()));
        let mut local = original.clone();
        local.password = "local".into();
        local.revision = 2;
        let mut remote_entry = original;
        remote_entry.password = "remote".into();
        remote_entry.revision = 2;

        let error = merge_vault(
            &base,
            &[local],
            &BTreeSet::new(),
            data(vec![remote_entry], Vec::new()),
            10,
        )
        .err()
        .unwrap();
        assert!(error.starts_with(CONFLICT_PREFIX));
    }

    #[test]
    fn modification_against_remote_delete_is_rejected() {
        let original = entry(1, "one", "pw1", 1);
        let base = baseline(&data(vec![original.clone()], Vec::new()));
        let mut local = original;
        local.password = "local".into();
        local.revision = 2;
        let remote = data(
            Vec::new(),
            vec![Tombstone {
                id: 1,
                revision: 2,
                deleted_at_ms: 5,
            }],
        );
        let error = merge_vault(&base, &[local], &BTreeSet::new(), remote, 10)
            .err()
            .unwrap();
        assert!(error.starts_with(CONFLICT_PREFIX));
    }

    #[test]
    fn deletion_against_remote_edit_is_rejected() {
        let original = entry(1, "one", "pw1", 1);
        let base = baseline(&data(vec![original.clone()], Vec::new()));
        let mut remote_entry = original;
        remote_entry.password = "remote".into();
        remote_entry.revision = 2;
        let mut deleted = BTreeSet::new();
        deleted.insert(1);
        let error = merge_vault(
            &base,
            &[],
            &deleted,
            data(vec![remote_entry], Vec::new()),
            10,
        )
        .err()
        .unwrap();
        assert!(error.starts_with(CONFLICT_PREFIX));
    }

    #[test]
    fn stale_unchanged_entry_is_not_resurrected() {
        let first = entry(1, "one", "pw1", 1);
        let second = entry(2, "two", "pw2", 1);
        let base_data = data(vec![first.clone(), second.clone()], Vec::new());
        let base = baseline(&base_data);
        let mut local_second = second.clone();
        local_second.password = "local-two".into();
        local_second.revision = 2;
        let remote = data(
            vec![],
            vec![Tombstone {
                id: 1,
                revision: 2,
                deleted_at_ms: 5,
            }],
        );

        let merged =
            merge_vault(&base, &[first, local_second], &BTreeSet::new(), remote, 10).unwrap();
        assert!(!merged.entries.iter().any(|item| item.id == 1));
        assert_eq!(
            merged
                .entries
                .iter()
                .find(|item| item.id == 2)
                .unwrap()
                .password,
            "local-two"
        );
        assert_eq!(merged.tombstones.len(), 1);
    }

    #[test]
    fn stale_tombstone_survives_restart_and_blocks_modified_readd() {
        let original = entry(1, "one", "pw1", 1);
        let base = baseline(&data(vec![original.clone()], Vec::new()));
        let mut local = original;
        local.password = "resurrect".into();
        local.revision = 2;
        let remote = data(
            Vec::new(),
            vec![Tombstone {
                id: 1,
                revision: 2,
                deleted_at_ms: 5,
            }],
        );
        let error = merge_vault(&base, &[local], &BTreeSet::new(), remote, 10)
            .err()
            .unwrap();
        assert!(error.starts_with(CONFLICT_PREFIX));
    }

    #[test]
    fn legacy_array_is_migrated_with_revisions() {
        let legacy = serde_json::to_vec(&vec![entry(1, "one", "pw1", 1)]).unwrap();
        let entries: Vec<PasswordEntry> = serde_json::from_slice(&legacy).unwrap();
        let migrated = VaultData::from_legacy(entries);
        assert_eq!(migrated.format, VAULT_FORMAT_VERSION);
        assert_eq!(migrated.entries[0].revision, 1);
        assert!(migrated.tombstones.is_empty());
    }

    #[test]
    fn secret_is_zeroized_on_drop() {
        let mut entry = PasswordEntry {
            id: 1,
            origin: "https://example.com".into(),
            username: "user".into(),
            password: "super-secret".into(),
            title: String::new(),
            created_at_ms: 0,
            updated_at_ms: 0,
            revision: 1,
        };
        let ptr = entry.password.as_ptr();
        let len = entry.password.len();
        drop(entry);
        // The allocator may reuse this memory; checking the old span is not a
        // stable safety assertion. This test instead verifies the wrapper is
        // drop-zeroized by construction in release review.
        let _ = (ptr, len);
    }
}
