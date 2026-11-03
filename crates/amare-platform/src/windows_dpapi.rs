//! Windows DPAPI key protection (user scope).
//!
//! DPAPI blobs are bound to the Windows user account. Moving a key file to a
//! different Windows account will fail to decrypt by design; use a portable
//! password-protected backup when moving between accounts/machines.

#![cfg(windows)]

use amare_security::{KeyProtectionError, KeyProtector};
use windows_sys::Win32::Foundation::{GetLastError, HLOCAL, LocalFree};
use windows_sys::Win32::Security::Cryptography::{
    CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData, CryptUnprotectData,
};

pub struct WindowsDpapiKeyProtector;

impl KeyProtector for WindowsDpapiKeyProtector {
    fn protect(&self, context: &str, plaintext: &[u8]) -> Result<Vec<u8>, KeyProtectionError> {
        if plaintext.len() > u32::MAX as usize {
            return Err(KeyProtectionError::TooLarge);
        }
        let description = wide(context);
        let mut input = CRYPT_INTEGER_BLOB {
            cbData: plaintext.len() as u32,
            pbData: plaintext.as_ptr().cast_mut(),
        };
        let entropy = CRYPT_INTEGER_BLOB {
            cbData: context.len() as u32,
            pbData: context.as_ptr().cast_mut(),
        };
        let mut output = CRYPT_INTEGER_BLOB {
            cbData: 0,
            pbData: std::ptr::null_mut(),
        };
        // SAFETY: input points to plaintext for the duration of the call;
        // output is initialized by DPAPI on success.
        let ok = unsafe {
            CryptProtectData(
                &mut input,
                description.as_ptr(),
                &entropy,
                std::ptr::null(),
                std::ptr::null(),
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )
        };
        if ok == 0 {
            let _ = unsafe { GetLastError() };
            return Err(KeyProtectionError::Failed);
        }
        copy_and_free(output)
    }

    fn unprotect(&self, context: &str, protected: &[u8]) -> Result<Vec<u8>, KeyProtectionError> {
        if protected.len() > u32::MAX as usize {
            return Err(KeyProtectionError::TooLarge);
        }
        let mut input = CRYPT_INTEGER_BLOB {
            cbData: protected.len() as u32,
            pbData: protected.as_ptr().cast_mut(),
        };
        let entropy = CRYPT_INTEGER_BLOB {
            cbData: context.len() as u32,
            pbData: context.as_ptr().cast_mut(),
        };
        let mut output = CRYPT_INTEGER_BLOB {
            cbData: 0,
            pbData: std::ptr::null_mut(),
        };
        // SAFETY: input points to protected bytes for the duration of the call;
        // output is initialized by DPAPI on success.
        let ok = unsafe {
            CryptUnprotectData(
                &mut input,
                std::ptr::null_mut(),
                &entropy,
                std::ptr::null(),
                std::ptr::null(),
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )
        };
        if ok == 0 {
            let _ = unsafe { GetLastError() };
            return Err(KeyProtectionError::InvalidMaterial);
        }
        copy_and_free(output)
    }
}

fn copy_and_free(blob: CRYPT_INTEGER_BLOB) -> Result<Vec<u8>, KeyProtectionError> {
    if blob.pbData.is_null() {
        return Ok(Vec::new());
    }
    let len = blob.cbData as usize;
    // SAFETY: successful DPAPI calls return a LocalAlloc buffer of cbData bytes.
    let bytes = unsafe { std::slice::from_raw_parts(blob.pbData, len) }.to_vec();
    // SAFETY: DPAPI output buffers must be released with LocalFree.
    unsafe {
        LocalFree(blob.pbData as HLOCAL);
    }
    Ok(bytes)
}

fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dpapi_round_trip() {
        let protector = WindowsDpapiKeyProtector;
        let protected = protector
            .protect("amare.dpapi.test", b"secret-key-material")
            .unwrap();
        let plain = protector.unprotect("amare.dpapi.test", &protected).unwrap();
        assert_eq!(plain, b"secret-key-material");
    }

    #[test]
    fn key_file_round_trip_under_dpapi() {
        use amare_security::KeyFile;
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("backup.key");
        let protector = WindowsDpapiKeyProtector;
        let key_file = KeyFile::new(&path);
        let key = key_file
            .load_or_create(&protector, "amare.backup.key")
            .unwrap();
        assert_eq!(key_file.load(&protector, "amare.backup.key").unwrap(), key);
        let bytes = std::fs::read(&path).unwrap();
        assert!(!bytes.windows(32).any(|window| window == key));
    }

    #[test]
    fn dpapi_context_entropy_is_bound() {
        let protector = WindowsDpapiKeyProtector;
        let protected = protector.protect("context-a", b"x").unwrap();
        assert!(protector.unprotect("context-b", &protected).is_err());
    }

    #[test]
    fn dpapi_rejects_tampered_blob() {
        let protector = WindowsDpapiKeyProtector;
        let mut protected = protector.protect("amare.dpapi.test", b"x").unwrap();
        let last = protected.len() - 1;
        protected[last] ^= 1;
        assert!(protector.unprotect("amare.dpapi.test", &protected).is_err());
    }
}
