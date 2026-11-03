//! Database integrity checks and recovery diagnostics.
//!
//! These functions never delete or reinitialize a database. A corrupt file is
//! reported and left in place so it can be copied/backed up before any user
//! decision.

use crate::migrations::{PROFILE_SCHEMA_VERSION, ROOT_SCHEMA_VERSION};
use rusqlite::Connection;
use serde::Serialize;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Serialize)]
pub struct IntegrityReport {
    pub path: PathBuf,
    pub exists: bool,
    pub ok: bool,
    pub schema_version: Option<u32>,
    pub supported_schema_version: Option<u32>,
    pub integrity_messages: Vec<String>,
    pub foreign_key_violations: u64,
    pub error: Option<String>,
}

pub fn check_database(path: impl AsRef<Path>) -> IntegrityReport {
    let path = path.as_ref().to_path_buf();
    check_database_with_max(&path, None)
}

pub fn check_store(root: impl AsRef<Path>) -> Vec<IntegrityReport> {
    let root = root.as_ref();
    let mut reports = Vec::new();
    let root_db = root.join("amare.db");
    let root_report = check_database_with_max(&root_db, Some(ROOT_SCHEMA_VERSION));
    let root_ok = root_report.ok;
    reports.push(root_report);

    if !root_ok {
        return reports;
    }

    let conn = match Connection::open(&root_db) {
        Ok(conn) => conn,
        Err(_) => return reports,
    };
    let mut stmt = match conn.prepare("SELECT id FROM profiles ORDER BY id") {
        Ok(stmt) => stmt,
        Err(_) => return reports,
    };
    let ids = match stmt.query_map([], |row| row.get::<_, String>(0)) {
        Ok(rows) => rows.filter_map(Result::ok).collect::<Vec<_>>(),
        Err(_) => return reports,
    };
    drop(stmt);
    for id in ids {
        reports.push(check_database_with_max(
            &root.join("profiles").join(id).join("amare.db"),
            Some(PROFILE_SCHEMA_VERSION),
        ));
    }
    reports
}

fn check_database_with_max(path: &Path, supported: Option<u32>) -> IntegrityReport {
    if !path.exists() {
        return IntegrityReport {
            path: path.to_path_buf(),
            exists: false,
            ok: false,
            schema_version: None,
            supported_schema_version: supported,
            integrity_messages: Vec::new(),
            foreign_key_violations: 0,
            error: Some("database file does not exist".into()),
        };
    }

    let conn = match Connection::open(path) {
        Ok(conn) => conn,
        Err(error) => {
            return IntegrityReport {
                path: path.to_path_buf(),
                exists: true,
                ok: false,
                schema_version: None,
                supported_schema_version: supported,
                integrity_messages: Vec::new(),
                foreign_key_violations: 0,
                error: Some(error.to_string()),
            };
        }
    };

    let schema_version = conn
        .query_row(
            "SELECT COALESCE(MAX(version), 0) FROM schema_migrations",
            [],
            |row| row.get::<_, u32>(0),
        )
        .ok();
    let mut integrity_messages = Vec::new();
    let mut error = None;
    match conn.prepare("PRAGMA integrity_check") {
        Ok(mut stmt) => match stmt.query_map([], |row| row.get::<_, String>(0)) {
            Ok(rows) => {
                for row in rows.flatten() {
                    integrity_messages.push(row);
                }
            }
            Err(failure) => error = Some(failure.to_string()),
        },
        Err(failure) => error = Some(failure.to_string()),
    }
    let foreign_key_violations = match conn.prepare("PRAGMA foreign_key_check") {
        Ok(mut stmt) => match stmt.query_map([], |_| Ok(())) {
            Ok(rows) => rows.filter_map(Result::ok).count() as u64,
            Err(failure) => {
                error = Some(failure.to_string());
                0
            }
        },
        Err(failure) => {
            error = Some(failure.to_string());
            0
        }
    };
    let integrity_ok = integrity_messages.len() == 1 && integrity_messages[0] == "ok";
    let schema_ok = match (schema_version, supported) {
        (Some(version), Some(max)) => version <= max,
        _ => true,
    };
    IntegrityReport {
        path: path.to_path_buf(),
        exists: true,
        ok: error.is_none() && integrity_ok && foreign_key_violations == 0 && schema_ok,
        schema_version,
        supported_schema_version: supported,
        integrity_messages,
        foreign_key_violations,
        error,
    }
}
