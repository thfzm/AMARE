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
        self.get_with_aad(key, encryption_key, key.as_bytes())
    }

    /// Read and decrypt a secret with an explicit AAD.
    pub fn get_with_aad(
        &self,
        key: &str,
        encryption_key: &[u8; 32],
        aad: &[u8],
    ) -> Result<Option<Vec<u8>>, StoreError> {
        Ok(self
            .get_with_aad_version(key, encryption_key, aad)?
            .map(|(plaintext, _)| plaintext))
    }

    /// Read a secret plus the database version used for compare-and-swap.
    pub fn get_with_aad_version(
        &self,
        key: &str,
        encryption_key: &[u8; 32],
        aad: &[u8],
    ) -> Result<Option<(Vec<u8>, i64)>, StoreError> {
        let row: Option<(Vec<u8>, i64)> = self
            .conn
            .query_row(
                "SELECT ciphertext, updated_at_ms FROM secrets WHERE key = ?1",
                params![key],
                |row| Ok((row.get(0)?, row.get(1)?)),
            )
            .optional()?;
        let Some((ciphertext, version)) = row else {
            return Ok(None);
        };
        Ok(Some((decrypt(encryption_key, &ciphertext, aad)?, version)))
    }

    /// Current database version for a secret, without decrypting it.
    pub fn version(&self, key: &str) -> Result<Option<i64>, StoreError> {
        self.conn
            .query_row(
                "SELECT updated_at_ms FROM secrets WHERE key = ?1",
                params![key],
                |row| row.get(0),
            )
            .optional()
            .map_err(StoreError::from)
    }

    /// Write a secret with an explicit AAD. Existing callers keep using
    /// [`Self::set`], which binds AAD to the key name.
    pub fn set_with_aad(
        &self,
        key: &str,
        group: &str,
        plaintext: &[u8],
        encryption_key: &[u8; 32],
        aad: &[u8],
    ) -> Result<(), StoreError> {
        validate_text("secret key", key, 256)?;
        validate_text("secret group", group, 128)?;
        let ciphertext = encrypt(encryption_key, plaintext, aad)?;
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

    /// Atomic compare-and-swap write for multiprocess/multiwindow safety.
    ///
    /// `expected_version = None` means the row must not exist. The stored
    /// version is monotonic even when the wall clock repeats a millisecond.
    pub fn set_with_aad_cas(
        &self,
        key: &str,
        group: &str,
        plaintext: &[u8],
        encryption_key: &[u8; 32],
        aad: &[u8],
        expected_version: Option<i64>,
    ) -> Result<i64, StoreError> {
        validate_text("secret key", key, 256)?;
        validate_text("secret group", group, 128)?;
        let tx = crate::db::write_transaction(self.conn)?;
        let actual: Option<i64> = tx
            .query_row(
                "SELECT updated_at_ms FROM secrets WHERE key = ?1",
                params![key],
                |row| row.get(0),
            )
            .optional()?;
        if actual != expected_version {
            return Err(StoreError::Conflict {
                expected: expected_version,
                actual,
            });
        }
        let now = now_ms();
        let next = actual
            .map(|version| version.saturating_add(1).max(now))
            .unwrap_or(now);
        let ciphertext = encrypt(encryption_key, plaintext, aad)?;
        tx.execute(
            "INSERT INTO secrets(key, group_name, ciphertext, updated_at_ms)
             VALUES (?1, ?2, ?3, ?4)
             ON CONFLICT(key) DO UPDATE SET
                group_name = excluded.group_name,
                ciphertext = excluded.ciphertext,
                updated_at_ms = excluded.updated_at_ms",
            params![key, group, ciphertext, next],
        )?;
        tx.commit()?;
        Ok(next)
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
