use amare_store::StoreError;

/// Unified error surface for the shared service layer.
///
/// Repository errors remain available through [`ServiceError::Store`] so
/// callers can distinguish not-found/corrupt/invalid states while using one
/// service-facing error type.
#[derive(Debug, thiserror::Error)]
pub enum ServiceError {
    #[error("store error: {0}")]
    Store(#[from] StoreError),
    #[error("invalid input: {0}")]
    InvalidInput(String),
    #[error("no active profile")]
    NoActiveProfile,
    #[error("profile context is no longer active")]
    ProfileSwitched,
    #[error("the last profile cannot be deleted")]
    LastProfile,
    #[error("unknown setting: {0}")]
    UnknownSetting(String),
    #[error("invalid value type for setting {0}")]
    InvalidSettingType(String),
    #[error("setting value is out of range: {0}")]
    SettingOutOfRange(String),
    #[error("sensitive values must use SecretRepository: {0}")]
    SensitiveSetting(String),
    #[error("duplicate setting in one atomic change: {0}")]
    DuplicateSetting(String),
    #[error("search query is empty")]
    EmptyQuery,
    #[error("search query must be <= {max} bytes")]
    QueryTooLong { max: usize },
    #[error("search page limit must be between 1 and {max}")]
    InvalidLimit { max: u32 },
    #[error("search offset must be <= {max}")]
    OffsetTooLarge { max: u64 },
}
