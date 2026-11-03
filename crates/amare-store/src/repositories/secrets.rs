use crate::db::{now_ms, validate_text};
use crate::error::StoreError;
use amare_security::{decrypt, encrypt};
use rusqlite::{Connection, OptionalExtension, params};

pub struct SecretRepository<'a> {
    conn: &'a Connection,
}

impl<'a> SecretRepository<'a> {
    pub(crate) fn new(conn: &'a Connection) -> Self {
        Self { conn }
    }

    pub fn set(
        &self,
        key: &str,
        group: &str,
        plaintext: &[u8],
        encryption_key: &[u8; 32],
    ) -> Result<(), StoreError> {
        validate_text("secret key", key, 256)?;
        validate_text("secret group", group, 128)?;
        let ciphertext = encrypt(encryption_key, plaintext, key.as_bytes())?;
        self.conn.execute(
            "INSERT INTO secrets(key, group_name, ciphertext, updated_at_ms)
             VALUES (?1, ?2, ?3, ?4)
             ON CONFLICT(key) DO UPDATE SET
                group_name = excluded.group_name,
                ciphertext = excluded.ciphertext,
                updated_at_ms = excluded.updated_at_ms",
            params![key, group, ciphertext, now_ms()],
        )?;
        Ok(())
    }

    pub fn get(&self, key: &str, encryption_key: &[u8; 32]) -> Result<Option<Vec<u8>>, StoreError> {
        let ciphertext: Option<Vec<u8>> = self
            .conn
            .query_row(
                "SELECT ciphertext FROM secrets WHERE key = ?1",
                params![key],
                |row| row.get(0),
            )
            .optional()?;
        let Some(ciphertext) = ciphertext else {
            return Ok(None);
        };
        Ok(Some(decrypt(encryption_key, &ciphertext, key.as_bytes())?))
    }

    pub fn delete(&self, key: &str) -> Result<bool, StoreError> {
        let changed = self
            .conn
            .execute("DELETE FROM secrets WHERE key = ?1", params![key])?;
        Ok(changed > 0)
    }

    pub fn list_keys(&self, group: Option<&str>) -> Result<Vec<String>, StoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT key FROM secrets WHERE (?1 IS NULL OR group_name = ?1) ORDER BY group_name, key",
        )?;
        let rows = stmt.query_map(params![group], |row| row.get::<_, String>(0))?;
        let mut keys = Vec::new();
        for row in rows {
            keys.push(row?);
        }
        Ok(keys)
    }
}
