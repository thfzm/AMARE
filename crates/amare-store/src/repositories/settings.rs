use crate::db::{normalize_limit, now_ms, validate_text};
use crate::error::StoreError;
use crate::models::SettingRow;
use amare_core::{Page, SettingEntry, SettingValue};
use rusqlite::{Connection, OptionalExtension, Row, params};

pub struct SettingsRepository<'a> {
    conn: &'a Connection,
}

impl<'a> SettingsRepository<'a> {
    pub(crate) fn new(conn: &'a Connection) -> Self {
        Self { conn }
    }

    pub fn set(
        &self,
        key: &str,
        group: &str,
        value: SettingValue,
    ) -> Result<SettingEntry, StoreError> {
        validate_text("setting key", key, 256)?;
        validate_text("setting group", group, 128)?;
        reject_secret_like_key(key)?;
        let (value_type, value_text) = encode_value(&value)?;
        let now = now_ms();
        self.conn.execute(
            "INSERT INTO settings(key, group_name, value_type, value_text, updated_at_ms)
             VALUES (?1, ?2, ?3, ?4, ?5)
             ON CONFLICT(key) DO UPDATE SET
                group_name = excluded.group_name,
                value_type = excluded.value_type,
                value_text = excluded.value_text,
                updated_at_ms = excluded.updated_at_ms",
            params![key, group, value_type, value_text, now],
        )?;
        Ok(SettingEntry {
            key: key.to_string(),
            group: group.to_string(),
            value,
            updated_at_ms: now,
        })
    }

    /// Applies multiple settings in one transaction. Either every value is
    /// validated and written, or none are.
    pub fn set_many(
        &self,
        entries: &[(&str, &str, SettingValue)],
    ) -> Result<Vec<SettingEntry>, StoreError> {
        let mut encoded = Vec::with_capacity(entries.len());
        for (key, group, value) in entries {
            validate_text("setting key", key, 256)?;
            validate_text("setting group", group, 128)?;
            reject_secret_like_key(key)?;
            let (value_type, value_text) = encode_value(value)?;
            encoded.push((*key, *group, value_type, value_text));
        }
        let now = now_ms();
        let tx = crate::db::write_transaction(self.conn)?;
        for (key, group, value_type, value_text) in &encoded {
            tx.execute(
                "INSERT INTO settings(key, group_name, value_type, value_text, updated_at_ms)
                 VALUES (?1, ?2, ?3, ?4, ?5)
                 ON CONFLICT(key) DO UPDATE SET
                    group_name = excluded.group_name,
                    value_type = excluded.value_type,
                    value_text = excluded.value_text,
                    updated_at_ms = excluded.updated_at_ms",
                params![key, group, value_type, value_text, now],
            )?;
        }
        tx.commit()?;
        Ok(entries
            .iter()
            .map(|(key, group, value)| SettingEntry {
                key: (*key).to_string(),
                group: (*group).to_string(),
                value: value.clone(),
                updated_at_ms: now,
            })
            .collect())
    }

    pub fn delete_many(&self, keys: &[&str]) -> Result<usize, StoreError> {
        let tx = crate::db::write_transaction(self.conn)?;
        let mut changed = 0;
        for key in keys {
            changed += tx.execute("DELETE FROM settings WHERE key = ?1", params![key])?;
        }
        tx.commit()?;
        Ok(changed)
    }

    pub fn delete_group(&self, group: &str) -> Result<usize, StoreError> {
        Ok(self
            .conn
            .execute("DELETE FROM settings WHERE group_name = ?1", params![group])?)
    }

    pub fn delete_all(&self) -> Result<usize, StoreError> {
        Ok(self.conn.execute("DELETE FROM settings", [])?)
    }

    pub fn get(&self, key: &str) -> Result<Option<SettingEntry>, StoreError> {
        let row = self
            .conn
            .query_row(
                "SELECT key, group_name, value_type, value_text, updated_at_ms
                 FROM settings WHERE key = ?1",
                params![key],
                map_setting,
            )
            .optional()?;
        row.map(SettingRow::into_domain)
            .transpose()
            .map_err(StoreError::Corrupt)
    }

    pub fn delete(&self, key: &str) -> Result<bool, StoreError> {
        let changed = self
            .conn
            .execute("DELETE FROM settings WHERE key = ?1", params![key])?;
        Ok(changed > 0)
    }

    pub fn list(
        &self,
        group: Option<&str>,
        limit: u32,
        offset: u64,
    ) -> Result<Page<SettingEntry>, StoreError> {
        let limit = normalize_limit(limit);
        let total: u64 = self.conn.query_row(
            "SELECT COUNT(*) FROM settings WHERE (?1 IS NULL OR group_name = ?1)",
            params![group],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT key, group_name, value_type, value_text, updated_at_ms
             FROM settings
             WHERE (?1 IS NULL OR group_name = ?1)
             ORDER BY group_name ASC, key ASC
             LIMIT ?2 OFFSET ?3",
        )?;
        let rows = stmt.query_map(params![group, limit, offset], map_setting)?;
        let mut items = Vec::new();
        for row in rows {
            items.push(row?.into_domain().map_err(StoreError::Corrupt)?);
        }
        Ok(Page {
            items,
            total,
            limit,
            offset,
        })
    }

    pub fn search(
        &self,
        query: &str,
        limit: u32,
        offset: u64,
    ) -> Result<Page<SettingEntry>, StoreError> {
        let limit = normalize_limit(limit);
        let pattern = format!("%{}%", escape_like(query));
        let total: u64 = self.conn.query_row(
            "SELECT COUNT(*) FROM settings
             WHERE key LIKE ?1 ESCAPE '\\' OR value_text LIKE ?1 ESCAPE '\\'",
            params![pattern],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT key, group_name, value_type, value_text, updated_at_ms
             FROM settings
             WHERE key LIKE ?1 ESCAPE '\\' OR value_text LIKE ?1 ESCAPE '\\'
             ORDER BY group_name ASC, key ASC
             LIMIT ?2 OFFSET ?3",
        )?;
        let rows = stmt.query_map(params![pattern, limit, offset], map_setting)?;
        let mut items = Vec::new();
        for row in rows {
            items.push(row?.into_domain().map_err(StoreError::Corrupt)?);
        }
        Ok(Page {
            items,
            total,
            limit,
            offset,
        })
    }
}

fn map_setting(row: &Row<'_>) -> rusqlite::Result<SettingRow> {
    Ok(SettingRow {
        key: row.get(0)?,
        group_name: row.get(1)?,
        value_type: row.get(2)?,
        value_text: row.get(3)?,
        updated_at_ms: row.get(4)?,
    })
}

fn reject_secret_like_key(key: &str) -> Result<(), StoreError> {
    let lower = key.to_ascii_lowercase();
    const SENSITIVE: &[&str] = &[
        "password",
        "passwd",
        "secret",
        "token",
        "api_key",
        "apikey",
        "credential",
        "private_key",
    ];
    if SENSITIVE.iter().any(|part| lower.contains(part)) {
        return Err(StoreError::InvalidInput(
            "sensitive values must use SecretRepository, not settings".into(),
        ));
    }
    Ok(())
}

fn encode_value(value: &SettingValue) -> Result<(&'static str, String), StoreError> {
    match value {
        SettingValue::Text(text) => Ok(("text", text.clone())),
        SettingValue::Integer(value) => Ok(("integer", value.to_string())),
        SettingValue::Float(value) => {
            if !value.is_finite() {
                return Err(StoreError::InvalidInput(
                    "float setting must be finite".into(),
                ));
            }
            Ok(("float", value.to_string()))
        }
        SettingValue::Boolean(value) => {
            let text = if *value { "1" } else { "0" };
            Ok(("boolean", text.into()))
        }
        SettingValue::Json(text) => {
            serde_json::from_str::<serde_json::Value>(text)
                .map_err(|_| StoreError::InvalidInput("invalid JSON setting".into()))?;
            Ok(("json", text.clone()))
        }
    }
}

pub(crate) fn escape_like(value: &str) -> String {
    value
        .replace('\\', "\\\\")
        .replace('%', "\\%")
        .replace('_', "\\_")
}
