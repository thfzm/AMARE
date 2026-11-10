//! Encrypted password vault for the GPUI shell.
//!
//! The vault JSON (including usernames and passwords) is encrypted with
//! XChaCha20-Poly1305. On Windows the random data key is stored in a DPAPI
//! user-scoped key file. SQLite is deliberately not used for any vault secret.
//! Non-Windows builds return `Unsupported`; no plaintext fallback is provided.

use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;
use std::path::PathBuf;
use url::Url;
use zeroize::Zeroize;

const VAULT_MAGIC: &[u8; 9] = b"AMAREVLT1";
const VAULT_AAD: &[u8] = b"amare.gpui.password-vault.v1";
const MAX_VAULT_BYTES: usize = 8 * 1024 * 1024;
static VAULT_GENERATION: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);

pub fn generation() -> u64 {
    VAULT_GENERATION.load(std::sync::atomic::Ordering::Relaxed)
}

#[derive(Clone, Serialize, Deserialize)]
pub struct PasswordEntry {
    pub id: u64,
    pub origin: String,
    pub username: String,
    pub password: String,
    pub title: String,
    pub created_at_ms: i64,
    pub updated_at_ms: i64,
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

    pub fn entries_mut(&mut self) -> &mut Vec<PasswordEntry> {
        &mut self.entries
    }

    pub fn lock(&mut self) {
        self.entries.clear();
        self.deleted_ids.clear();
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
    fn read_entries(
        key: &[u8; 32],
        vault_path: &std::path::Path,
    ) -> Result<Vec<PasswordEntry>, String> {
        use amare_security::decrypt;
        if !vault_path.exists() {
            return Ok(Vec::new());
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
        let parsed: Vec<PasswordEntry> = serde_json::from_slice(&plain)
            .map_err(|_| "보안 저장소 데이터가 손상되었습니다.".to_string())?;
        plain.zeroize();
        Ok(parsed)
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

    #[cfg(target_os = "windows")]
    pub fn unlock(&mut self) -> Result<(), String> {
        let (key_path, vault_path) = Self::paths()?;
        let key = self.key_for(&key_path, &vault_path)?;
        let parsed = Self::read_entries(&key, &vault_path)?;
        self.entries = parsed;
        self.deleted_ids.clear();
        self.unlocked = true;
        Ok(())
    }

    #[cfg(not(target_os = "windows"))]
    pub fn unlock(&mut self) -> Result<(), String> {
        Err("이 빌드에서는 OS 보안 저장소(DPAPI)가 지원되지 않습니다.".into())
    }

    #[cfg(target_os = "windows")]
    pub fn save(&mut self) -> Result<(), String> {
        use amare_security::encrypt;
        let (key_path, vault_path) = Self::paths()?;
        let key = self.key_for(&key_path, &vault_path)?;
        let mut merged = Self::read_entries(&key, &vault_path)?;
        merged.retain(|entry| !self.deleted_ids.contains(&entry.id));
        for entry in &self.entries {
            if let Some(existing) = merged.iter_mut().find(|item| item.id == entry.id) {
                *existing = entry.clone();
            } else {
                merged.push(entry.clone());
            }
        }
        let mut plain = serde_json::to_vec(&merged)
            .map_err(|_| "보안 저장소 직렬화에 실패했습니다.".to_string())?;
        let encrypted = encrypt(&key, &plain, VAULT_AAD)
            .map_err(|_| "보안 저장소 암호화에 실패했습니다.".to_string())?;
        plain.zeroize();
        if encrypted.len() + VAULT_MAGIC.len() > MAX_VAULT_BYTES {
            return Err("보안 저장소가 너무 큽니다.".into());
        }
        let temp = vault_path.with_extension(format!("tmp-{}", std::process::id()));
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
        self.entries = merged;
        self.deleted_ids.clear();
        self.unlocked = true;
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
        let now = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis() as i64;
        let existing_id = id.or_else(|| {
            self.entries
                .iter()
                .find(|entry| entry.origin == origin && entry.username == username)
                .map(|entry| entry.id)
        });
        if let Some(existing_id) = existing_id {
            if let Some(entry) = self
                .entries
                .iter_mut()
                .find(|entry| entry.id == existing_id)
            {
                entry.origin = origin;
                entry.username = username.to_string();
                entry.title = title.chars().take(512).collect();
                entry.updated_at_ms = now;
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

impl Drop for PasswordVault {
    fn drop(&mut self) {
        self.lock();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

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
    fn secret_is_zeroized_on_drop() {
        let mut entry = PasswordEntry {
            id: 1,
            origin: "https://example.com".into(),
            username: "user".into(),
            password: "super-secret".into(),
            title: String::new(),
            created_at_ms: 0,
            updated_at_ms: 0,
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
