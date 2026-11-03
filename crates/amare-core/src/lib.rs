//! AMARE shared domain core (Phase 1 scaffold).
//!
//! This crate intentionally contains no UI, engine, or platform types. Keep it
//! usable from Windows, Android, and headless tests.

use serde::{Deserialize, Serialize};

mod domain;
pub use domain::*;

pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// Minimal core information exposed to platform shells.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CoreInfo {
    pub version: String,
    pub contract_version: u32,
}

impl Default for CoreInfo {
    fn default() -> Self {
        Self {
            version: VERSION.to_string(),
            contract_version: 1,
        }
    }
}

/// Placeholder for a typed core operation result.
#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum CoreError {
    #[error("invalid argument: {0}")]
    InvalidArgument(String),
    #[error("not implemented in Phase 1 scaffold: {0}")]
    NotImplemented(&'static str),
}

pub fn version() -> &'static str {
    VERSION
}

pub fn ping(input: &str) -> String {
    format!("pong:{input}")
}

pub fn core_info() -> CoreInfo {
    CoreInfo::default()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn version_is_not_empty() {
        assert!(!version().is_empty());
    }

    #[test]
    fn ping_round_trips() {
        assert_eq!(ping("hello"), "pong:hello");
        assert_eq!(ping("한글"), "pong:한글");
    }

    #[test]
    fn core_info_has_contract_version() {
        let info = core_info();
        assert_eq!(info.version, VERSION);
        assert_eq!(info.contract_version, 1);
    }

    #[test]
    fn profile_id_is_opaque_but_comparable() {
        let a = ProfileId::new("p1");
        let b = ProfileId::new("p1");
        assert_eq!(a, b);
        assert_eq!(a.as_str(), "p1");
    }
}
