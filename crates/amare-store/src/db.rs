use crate::error::StoreError;
use rusqlite::{Connection, Transaction, TransactionBehavior};
use std::path::{Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub(crate) fn now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis() as i64)
        .unwrap_or(0)
}

pub(crate) fn open_connection(path: &Path) -> Result<Connection, StoreError> {
    let conn = Connection::open(path)?;
    configure_connection(&conn)?;
    Ok(conn)
}

fn configure_connection(conn: &Connection) -> Result<(), StoreError> {
    conn.busy_timeout(Duration::from_secs(5))?;
    conn.execute_batch("PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;")?;
    // WAL is ignored for in-memory DBs (returns "memory").
    let _: String = conn.query_row("PRAGMA journal_mode=WAL", [], |row| row.get(0))?;
    Ok(())
}

pub(crate) fn write_transaction(conn: &Connection) -> Result<Transaction<'_>, StoreError> {
    Ok(Transaction::new_unchecked(
        conn,
        TransactionBehavior::Immediate,
    )?)
}

pub(crate) fn schema_version(conn: &Connection) -> Result<u32, StoreError> {
    let version = conn.query_row(
        "SELECT COALESCE(MAX(version), 0) FROM schema_migrations",
        [],
        |row| row.get(0),
    )?;
    Ok(version)
}

pub(crate) fn sqlite_version() -> String {
    rusqlite::version().to_string()
}

pub(crate) fn validate_profile_id(id: &str) -> Result<(), StoreError> {
    if id.is_empty() || id.len() > 128 {
        return Err(StoreError::InvalidInput(
            "profile id must be 1..=128 characters".into(),
        ));
    }
    if id == "." || id == ".." {
        return Err(StoreError::InvalidInput("profile id is reserved".into()));
    }
    if !id
        .chars()
        .all(|ch| ch.is_ascii_alphanumeric() || ch == '-' || ch == '_' || ch == '.')
    {
        return Err(StoreError::InvalidInput(
            "profile id may only contain ASCII letters, digits, '-', '_' and '.'".into(),
        ));
    }
    Ok(())
}

pub(crate) fn profile_db_path(root_dir: &Path, id: &str) -> PathBuf {
    root_dir.join("profiles").join(id).join("amare.db")
}

pub(crate) fn normalize_limit(limit: u32) -> u32 {
    limit.clamp(1, 500)
}

pub(crate) fn validate_text(field: &str, value: &str, max_len: usize) -> Result<(), StoreError> {
    if value.trim().is_empty() {
        return Err(StoreError::InvalidInput(format!("{field} is required")));
    }
    if value.len() > max_len {
        return Err(StoreError::InvalidInput(format!(
            "{field} must be <= {max_len} bytes"
        )));
    }
    Ok(())
}
