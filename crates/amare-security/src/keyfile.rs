use crate::key_protection::{KeyProtectionError, KeyProtector};
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};

const MAGIC: &[u8; 9] = b"AMAREKEY1";
const KEY_LEN: usize = 32;

#[derive(Debug, thiserror::Error)]
pub enum KeyFileError {
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("invalid protected key file")]
    InvalidFormat,
    #[error("protected key has wrong length")]
    WrongKeyLength,
    #[error("key protection error: {0}")]
    Protection(#[from] KeyProtectionError),
}

/// A key file whose payload is protected by an OS key protector (DPAPI on
/// Windows). The plaintext 32-byte key never goes to disk.
pub struct KeyFile {
    path: PathBuf,
}

impl KeyFile {
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self { path: path.into() }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn load_or_create(
        &self,
        protector: &dyn KeyProtector,
        context: &str,
    ) -> Result<[u8; KEY_LEN], KeyFileError> {
        if self.path.exists() {
            self.load(protector, context)
        } else {
            let key = random_key();
            self.store(protector, context, &key)?;
            Ok(key)
        }
    }

    pub fn load(
        &self,
        protector: &dyn KeyProtector,
        context: &str,
    ) -> Result<[u8; KEY_LEN], KeyFileError> {
        let bytes = fs::read(&self.path)?;
        if bytes.len() < MAGIC.len() || &bytes[..MAGIC.len()] != MAGIC {
            return Err(KeyFileError::InvalidFormat);
        }
        let plaintext = protector.unprotect(context, &bytes[MAGIC.len()..])?;
        if plaintext.len() != KEY_LEN {
            return Err(KeyFileError::WrongKeyLength);
        }
        let mut key = [0u8; KEY_LEN];
        key.copy_from_slice(&plaintext);
        Ok(key)
    }

    pub fn store(
        &self,
        protector: &dyn KeyProtector,
        context: &str,
        key: &[u8; KEY_LEN],
    ) -> Result<(), KeyFileError> {
        let protected = protector.protect(context, key)?;
        if protected.len() > 1024 * 1024 {
            return Err(KeyFileError::InvalidFormat);
        }
        if let Some(parent) = self.path.parent() {
            fs::create_dir_all(parent)?;
        }
        let tmp = self
            .path
            .with_extension(format!("tmp-{}", std::process::id()));
        {
            let mut options = fs::OpenOptions::new();
            options.write(true).create(true).truncate(true);
            #[cfg(unix)]
            {
                use std::os::unix::fs::OpenOptionsExt;
                options.mode(0o600);
            }
            let mut file = options.open(&tmp)?;
            file.write_all(MAGIC)?;
            file.write_all(&protected)?;
            file.sync_all()?;
        }
        fs::rename(&tmp, &self.path)?;
        Ok(())
    }
}

fn random_key() -> [u8; KEY_LEN] {
    use rand::RngCore;
    let mut key = [0u8; KEY_LEN];
    rand::rngs::OsRng.fill_bytes(&mut key);
    key
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::key_protection::InMemoryKeyProtector;
    use std::fs;
    use tempfile::tempdir;

    #[test]
    fn key_file_round_trip_and_no_plaintext_key() {
        let dir = tempdir().unwrap();
        let path = dir.path().join("keys/backup.key");
        let protector = InMemoryKeyProtector::new([7u8; 32]);
        let key_file = KeyFile::new(&path);
        let key = key_file
            .load_or_create(&protector, "amare.backup.key")
            .unwrap();
        assert_eq!(key.len(), 32);

        let bytes = fs::read(&path).unwrap();
        assert!(bytes.starts_with(MAGIC));
        assert!(!bytes.windows(32).any(|window| window == key));

        let loaded = key_file.load(&protector, "amare.backup.key").unwrap();
        assert_eq!(loaded, key);
    }

    #[test]
    fn wrong_protector_or_context_fails() {
        let dir = tempdir().unwrap();
        let path = dir.path().join("backup.key");
        let protector = InMemoryKeyProtector::new([1u8; 32]);
        let key_file = KeyFile::new(&path);
        key_file.load_or_create(&protector, "context-a").unwrap();
        assert!(
            key_file
                .load(&InMemoryKeyProtector::new([2u8; 32]), "context-a")
                .is_err()
        );
        assert!(key_file.load(&protector, "context-b").is_err());
    }
}
