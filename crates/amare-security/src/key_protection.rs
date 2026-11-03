//! OS/platform key protection abstraction.
//!
//! The common crate defines the contract only. Windows DPAPI is implemented in
//! `amare-platform`; Android Keystore remains an interface until Phase 5.

use crate::{decrypt, encrypt};
use rand::RngCore;
use rand::rngs::OsRng;

#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum KeyProtectionError {
    #[error("key protection is not implemented on this platform")]
    Unsupported,
    #[error("key protection failed")]
    Failed,
    #[error("key material is too large")]
    TooLarge,
    #[error("protected key material is invalid")]
    InvalidMaterial,
}

pub trait KeyProtector: Send + Sync {
    fn protect(&self, context: &str, plaintext: &[u8]) -> Result<Vec<u8>, KeyProtectionError>;
    fn unprotect(&self, context: &str, protected: &[u8]) -> Result<Vec<u8>, KeyProtectionError>;
}

/// Test-only reversible protector. **Never use this for production secrets.**
///
/// It models an OS protector for Linux/unit tests: the key is held in memory,
/// and protected bytes are XChaCha20-Poly1305 encrypted with that key.
pub struct InMemoryKeyProtector {
    key: [u8; 32],
}

impl InMemoryKeyProtector {
    pub fn new(key: [u8; 32]) -> Self {
        Self { key }
    }

    pub fn random() -> Self {
        let mut key = [0u8; 32];
        OsRng.fill_bytes(&mut key);
        Self { key }
    }
}

impl KeyProtector for InMemoryKeyProtector {
    fn protect(&self, context: &str, plaintext: &[u8]) -> Result<Vec<u8>, KeyProtectionError> {
        encrypt(&self.key, plaintext, context.as_bytes()).map_err(|_| KeyProtectionError::Failed)
    }

    fn unprotect(&self, context: &str, protected: &[u8]) -> Result<Vec<u8>, KeyProtectionError> {
        decrypt(&self.key, protected, context.as_bytes())
            .map_err(|_| KeyProtectionError::InvalidMaterial)
    }
}
