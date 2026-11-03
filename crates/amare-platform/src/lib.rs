//! Platform abstraction traits (Phase 1 scaffold).
//!
//! Implementations live in the Windows (C#) and Android (Kotlin) shells. Rust
//! core depends only on these traits so it remains testable off-device.

pub mod android_keystore;
#[cfg(windows)]
pub mod windows_dpapi;

pub use android_keystore::AndroidKeystoreKeyProtector;
#[cfg(windows)]
pub use windows_dpapi::WindowsDpapiKeyProtector;

pub trait Keychain {
    type Error;
    fn get(&self, service: &str, account: &str) -> Result<Option<Vec<u8>>, Self::Error>;
    fn set(&self, service: &str, account: &str, secret: &[u8]) -> Result<(), Self::Error>;
    fn delete(&self, service: &str, account: &str) -> Result<(), Self::Error>;
}

pub trait Clock {
    fn now_unix_ms(&self) -> i64;
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlatformInfo {
    pub os: &'static str,
    pub arch: &'static str,
}

pub fn info() -> PlatformInfo {
    PlatformInfo {
        os: std::env::consts::OS,
        arch: std::env::consts::ARCH,
    }
}
