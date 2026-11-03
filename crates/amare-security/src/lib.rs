//! Local security primitives for AMARE (Phase 1 scaffold).
//!
//! This crate covers the testable, platform-independent parts:
//! - Argon2id key derivation for an optional master password
//! - XChaCha20-Poly1305 authenticated encryption for local secrets
//! - Ed25519 update-manifest signatures
//! - simple monotonic update version checks
//!
//! OS keychain/DPAPI/Keystore integration is intentionally left to the
//! platform crates and is not claimed as verified here.

use argon2::{Algorithm, Argon2, Params, Version};
use chacha20poly1305::aead::{Aead, AeadCore, KeyInit, Payload};
use chacha20poly1305::{XChaCha20Poly1305, XNonce};
use ed25519_dalek::{Signature, Signer, SigningKey, Verifier, VerifyingKey};
use rand::rngs::OsRng;
use sha2::{Digest, Sha256};
use zeroize::{Zeroize, ZeroizeOnDrop};

mod key_protection;
mod keyfile;
pub use key_protection::{InMemoryKeyProtector, KeyProtectionError, KeyProtector};
pub use keyfile::{KeyFile, KeyFileError};

pub const KEY_LEN: usize = 32;
pub const SALT_LEN: usize = 16;
pub const XNONCE_LEN: usize = 24;
pub const SIGNATURE_LEN: usize = 64;

#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum SecurityError {
    #[error("argon2 key derivation failed")]
    KeyDerivation,
    #[error("encryption failed")]
    Encrypt,
    #[error("decryption failed")]
    Decrypt,
    #[error("ciphertext is too short")]
    CiphertextTooShort,
    #[error("invalid signing key")]
    InvalidSigningKey,
    #[error("invalid verifying key")]
    InvalidVerifyingKey,
    #[error("invalid signature")]
    InvalidSignature,
    #[error("invalid version string: {0}")]
    InvalidVersion(String),
}

/// Derive a 32-byte key with Argon2id.
pub fn derive_key(password: &[u8], salt: &[u8]) -> Result<[u8; KEY_LEN], SecurityError> {
    if salt.len() < SALT_LEN {
        return Err(SecurityError::KeyDerivation);
    }

    let params =
        Params::new(19 * 1024, 2, 1, Some(KEY_LEN)).map_err(|_| SecurityError::KeyDerivation)?;
    let argon = Argon2::new(Algorithm::Argon2id, Version::V0x13, params);
    let mut key = [0u8; KEY_LEN];
    argon
        .hash_password_into(password, salt, &mut key)
        .map_err(|_| SecurityError::KeyDerivation)?;
    Ok(key)
}

/// Generate a fresh salt. Callers must persist it next to the ciphertext.
pub fn generate_salt() -> [u8; SALT_LEN] {
    use rand::RngCore;
    let mut salt = [0u8; SALT_LEN];
    OsRng.fill_bytes(&mut salt);
    salt
}

/// XChaCha20-Poly1305 encryption. Output layout: nonce(24) || ciphertext+tag.
pub fn encrypt(
    key: &[u8; KEY_LEN],
    plaintext: &[u8],
    aad: &[u8],
) -> Result<Vec<u8>, SecurityError> {
    let cipher = XChaCha20Poly1305::new(key.into());
    let nonce = XChaCha20Poly1305::generate_nonce(&mut OsRng);
    let ciphertext = cipher
        .encrypt(
            &nonce,
            Payload {
                msg: plaintext,
                aad,
            },
        )
        .map_err(|_| SecurityError::Encrypt)?;

    let mut out = Vec::with_capacity(XNONCE_LEN + ciphertext.len());
    out.extend_from_slice(&nonce);
    out.extend_from_slice(&ciphertext);
    Ok(out)
}

/// Decrypt an `encrypt` output. The same AAD is required.
pub fn decrypt(key: &[u8; KEY_LEN], blob: &[u8], aad: &[u8]) -> Result<Vec<u8>, SecurityError> {
    if blob.len() < XNONCE_LEN {
        return Err(SecurityError::CiphertextTooShort);
    }
    let (nonce_bytes, ciphertext) = blob.split_at(XNONCE_LEN);
    let nonce = XNonce::from_slice(nonce_bytes);
    let cipher = XChaCha20Poly1305::new(key.into());
    cipher
        .decrypt(
            nonce,
            Payload {
                msg: ciphertext,
                aad,
            },
        )
        .map_err(|_| SecurityError::Decrypt)
}

/// A small zeroizing wrapper for a derived data key.
#[derive(Zeroize, ZeroizeOnDrop)]
pub struct VaultKey([u8; KEY_LEN]);

impl VaultKey {
    pub fn new(key: [u8; KEY_LEN]) -> Self {
        Self(key)
    }

    pub fn as_bytes(&self) -> &[u8; KEY_LEN] {
        &self.0
    }
}

pub fn generate_signing_key() -> [u8; KEY_LEN] {
    SigningKey::generate(&mut OsRng).to_bytes()
}

pub fn verifying_key(signing_key: &[u8; KEY_LEN]) -> Result<[u8; KEY_LEN], SecurityError> {
    let key = SigningKey::from_bytes(signing_key);
    Ok(key.verifying_key().to_bytes())
}

pub fn sign_payload(signing_key: &[u8; KEY_LEN], payload: &[u8]) -> [u8; SIGNATURE_LEN] {
    let key = SigningKey::from_bytes(signing_key);
    key.sign(payload).to_bytes()
}

pub fn verify_payload(
    verifying_key: &[u8; KEY_LEN],
    payload: &[u8],
    signature: &[u8],
) -> Result<bool, SecurityError> {
    if signature.len() != SIGNATURE_LEN {
        return Err(SecurityError::InvalidSignature);
    }
    let key =
        VerifyingKey::from_bytes(verifying_key).map_err(|_| SecurityError::InvalidVerifyingKey)?;
    let signature =
        Signature::from_slice(signature).map_err(|_| SecurityError::InvalidSignature)?;
    Ok(key.verify(payload, &signature).is_ok())
}

pub fn sha256_hex(payload: &[u8]) -> String {
    let digest = Sha256::digest(payload);
    hex::encode(digest)
}

fn parse_version(value: &str) -> Result<(u64, u64, u64), SecurityError> {
    let mut parts = value.split('.');
    let major = parts
        .next()
        .ok_or_else(|| SecurityError::InvalidVersion(value.to_string()))?
        .parse()
        .map_err(|_| SecurityError::InvalidVersion(value.to_string()))?;
    let minor = parts
        .next()
        .ok_or_else(|| SecurityError::InvalidVersion(value.to_string()))?
        .parse()
        .map_err(|_| SecurityError::InvalidVersion(value.to_string()))?;
    let patch = parts
        .next()
        .ok_or_else(|| SecurityError::InvalidVersion(value.to_string()))?
        .parse()
        .map_err(|_| SecurityError::InvalidVersion(value.to_string()))?;
    if parts.next().is_some() {
        return Err(SecurityError::InvalidVersion(value.to_string()));
    }
    Ok((major, minor, patch))
}

/// Reject downgrades. Equal versions are not an upgrade.
pub fn update_allowed(current: &str, candidate: &str) -> Result<bool, SecurityError> {
    Ok(parse_version(candidate)? > parse_version(current)?)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn derive_key_is_deterministic_for_same_salt() {
        let salt = [7u8; SALT_LEN];
        let a = derive_key(b"correct horse battery staple", &salt).unwrap();
        let b = derive_key(b"correct horse battery staple", &salt).unwrap();
        assert_eq!(a, b);
    }

    #[test]
    fn derive_key_changes_with_salt() {
        let a = derive_key(b"password", &[1u8; SALT_LEN]).unwrap();
        let b = derive_key(b"password", &[2u8; SALT_LEN]).unwrap();
        assert_ne!(a, b);
    }

    #[test]
    fn encrypt_decrypt_round_trip() {
        let key = [3u8; KEY_LEN];
        let blob = encrypt(&key, b"secret data", b"amare/profile/1").unwrap();
        let plain = decrypt(&key, &blob, b"amare/profile/1").unwrap();
        assert_eq!(plain, b"secret data");
    }

    #[test]
    fn decrypt_fails_with_wrong_key() {
        let blob = encrypt(&[1u8; KEY_LEN], b"secret", b"aad").unwrap();
        assert_eq!(
            decrypt(&[2u8; KEY_LEN], &blob, b"aad"),
            Err(SecurityError::Decrypt)
        );
    }

    #[test]
    fn decrypt_fails_with_wrong_aad() {
        let key = [1u8; KEY_LEN];
        let blob = encrypt(&key, b"secret", b"aad-1").unwrap();
        assert_eq!(decrypt(&key, &blob, b"aad-2"), Err(SecurityError::Decrypt));
    }

    #[test]
    fn tampered_ciphertext_fails() {
        let key = [1u8; KEY_LEN];
        let mut blob = encrypt(&key, b"secret", b"aad").unwrap();
        let last = blob.len() - 1;
        blob[last] ^= 0x01;
        assert_eq!(decrypt(&key, &blob, b"aad"), Err(SecurityError::Decrypt));
    }

    #[test]
    fn signature_round_trip_and_tamper_rejection() {
        let signing_key = generate_signing_key();
        let verifying_key = verifying_key(&signing_key).unwrap();
        let payload = b"version=0.1.1;sha256=abc";
        let signature = sign_payload(&signing_key, payload);
        assert!(verify_payload(&verifying_key, payload, &signature).unwrap());
        assert!(!verify_payload(&verifying_key, b"tampered", &signature).unwrap());
    }

    #[test]
    fn update_rejects_downgrade_and_equal() {
        assert!(update_allowed("0.1.0", "0.1.1").unwrap());
        assert!(!update_allowed("0.1.1", "0.1.0").unwrap());
        assert!(!update_allowed("0.1.1", "0.1.1").unwrap());
        assert!(update_allowed("0.9.9", "1.0.0").unwrap());
    }

    #[test]
    fn sha256_is_stable() {
        assert_eq!(
            sha256_hex(b"abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
    }
}
