use amare_security::SecurityError;

#[derive(Debug, thiserror::Error)]
pub enum StoreError {
    #[error("sqlite error: {0}")]
    Sqlite(#[from] rusqlite::Error),
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("invalid input: {0}")]
    InvalidInput(String),
    #[error("not found: {0}")]
    NotFound(String),
    #[error("profile not found: {0}")]
    ProfileNotFound(String),
    #[error("profile is being deleted: {0}")]
    ProfileDeleting(String),
    #[error("migration v{version} failed: {source}")]
    Migration {
        version: u32,
        #[source]
        source: rusqlite::Error,
    },
    #[error("security error: {0}")]
    Security(#[from] SecurityError),
    #[error("database is corrupt or incomplete: {0}")]
    Corrupt(String),
}
