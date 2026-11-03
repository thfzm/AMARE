//! Android Keystore interface placeholder.
//!
//! The actual JNI/Keystore implementation is Phase 5 work and must be validated
//! on an Android device/emulator. Until then this adapter explicitly reports
//! `Unsupported` instead of pretending key protection exists.

use amare_security::{KeyProtectionError, KeyProtector};

pub struct AndroidKeystoreKeyProtector;

impl KeyProtector for AndroidKeystoreKeyProtector {
    fn protect(&self, _context: &str, _plaintext: &[u8]) -> Result<Vec<u8>, KeyProtectionError> {
        Err(KeyProtectionError::Unsupported)
    }

    fn unprotect(&self, _context: &str, _protected: &[u8]) -> Result<Vec<u8>, KeyProtectionError> {
        Err(KeyProtectionError::Unsupported)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn interface_is_explicitly_unsupported_until_phase5() {
        let protector = AndroidKeystoreKeyProtector;
        assert_eq!(
            protector.protect("x", b"y").unwrap_err(),
            KeyProtectionError::Unsupported
        );
    }
}
