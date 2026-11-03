use crate::db::{normalize_limit, now_ms, validate_profile_id, validate_text};
use crate::error::StoreError;
use crate::models::ProfileRow;
use amare_core::{Page, Profile, ProfileId};
use rusqlite::{Connection, OptionalExtension, Row, params};

pub struct ProfileRepository<'a> {
    conn: &'a Connection,
}

impl<'a> ProfileRepository<'a> {
    pub(crate) fn new(conn: &'a Connection) -> Self {
        Self { conn }
    }

    pub fn create(
        &self,
        id: &ProfileId,
        name: &str,
        is_default: bool,
    ) -> Result<Profile, StoreError> {
        validate_profile_id(id.as_str())?;
        validate_text("profile name", name, 256)?;
        let now = now_ms();
        let tx = crate::db::write_transaction(self.conn)?;
        if is_default {
            tx.execute(
                "UPDATE profiles SET is_default = 0, updated_at_ms = ?1 WHERE is_default = 1",
                params![now],
            )?;
        }
        tx.execute(
            "INSERT INTO profiles(id, name, icon, is_default, created_at_ms, updated_at_ms)
             VALUES (?1, ?2, NULL, ?3, ?4, ?4)",
            params![id.as_str(), name, is_default, now],
        )?;
        tx.commit()?;
        Ok(Profile {
            id: id.clone(),
            name: name.to_string(),
            icon: None,
            is_default,
            created_at_ms: now,
            updated_at_ms: now,
        })
    }

    pub fn get(&self, id: &ProfileId) -> Result<Option<Profile>, StoreError> {
        let row = self
            .conn
            .query_row(
                "SELECT id, name, icon, is_default, created_at_ms, updated_at_ms
                 FROM profiles WHERE id = ?1",
                params![id.as_str()],
                map_profile,
            )
            .optional()?;
        Ok(row.map(ProfileRow::into_domain))
    }

    pub fn get_required(&self, id: &ProfileId) -> Result<Profile, StoreError> {
        self.get(id)?
            .ok_or_else(|| StoreError::ProfileNotFound(id.to_string()))
    }

    pub fn list(&self, limit: u32, offset: u64) -> Result<Page<Profile>, StoreError> {
        let limit = normalize_limit(limit);
        let total: u64 = self
            .conn
            .query_row("SELECT COUNT(*) FROM profiles", [], |row| row.get(0))?;
        let mut stmt = self.conn.prepare(
            "SELECT id, name, icon, is_default, created_at_ms, updated_at_ms
             FROM profiles ORDER BY is_default DESC, created_at_ms ASC, id ASC
             LIMIT ?1 OFFSET ?2",
        )?;
        let rows = stmt.query_map(params![limit, offset], map_profile)?;
        let mut items = Vec::new();
        for row in rows {
            items.push(row?.into_domain());
        }
        Ok(Page {
            items,
            total,
            limit,
            offset,
        })
    }

    pub fn default_profile(&self) -> Result<Option<Profile>, StoreError> {
        let row = self
            .conn
            .query_row(
                "SELECT id, name, icon, is_default, created_at_ms, updated_at_ms
                 FROM profiles WHERE is_default = 1",
                [],
                map_profile,
            )
            .optional()?;
        Ok(row.map(ProfileRow::into_domain))
    }

    pub fn set_default(&self, id: &ProfileId) -> Result<(), StoreError> {
        let now = now_ms();
        let tx = crate::db::write_transaction(self.conn)?;
        let exists: Option<i64> = tx
            .query_row(
                "SELECT 1 FROM profiles WHERE id = ?1",
                params![id.as_str()],
                |row| row.get(0),
            )
            .optional()?;
        if exists.is_none() {
            return Err(StoreError::ProfileNotFound(id.to_string()));
        }
        // The single-default unique index requires clearing the old default
        // before promoting the new one.
        tx.execute(
            "UPDATE profiles SET is_default = 0, updated_at_ms = ?1 WHERE id <> ?2 AND is_default = 1",
            params![now, id.as_str()],
        )?;
        tx.execute(
            "UPDATE profiles SET is_default = 1, updated_at_ms = ?1 WHERE id = ?2",
            params![now, id.as_str()],
        )?;
        tx.commit()?;
        Ok(())
    }

    pub fn rename(&self, id: &ProfileId, name: &str) -> Result<(), StoreError> {
        validate_text("profile name", name, 256)?;
        let changed = self.conn.execute(
            "UPDATE profiles SET name = ?1, updated_at_ms = ?2 WHERE id = ?3",
            params![name, now_ms(), id.as_str()],
        )?;
        if changed == 0 {
            return Err(StoreError::ProfileNotFound(id.to_string()));
        }
        Ok(())
    }

    pub fn set_icon(&self, id: &ProfileId, icon: Option<&str>) -> Result<(), StoreError> {
        if let Some(icon) = icon {
            validate_text("profile icon", icon, 1024)?;
        }
        let changed = self.conn.execute(
            "UPDATE profiles SET icon = ?1, updated_at_ms = ?2 WHERE id = ?3",
            params![icon, now_ms(), id.as_str()],
        )?;
        if changed == 0 {
            return Err(StoreError::ProfileNotFound(id.to_string()));
        }
        Ok(())
    }

    pub fn delete(&self, id: &ProfileId) -> Result<(), StoreError> {
        let now = now_ms();
        let tx = crate::db::write_transaction(self.conn)?;
        let was_default: Option<bool> = tx
            .query_row(
                "SELECT is_default FROM profiles WHERE id = ?1",
                params![id.as_str()],
                |row| row.get(0),
            )
            .optional()?;
        let Some(was_default) = was_default else {
            return Err(StoreError::ProfileNotFound(id.to_string()));
        };
        tx.execute("DELETE FROM profiles WHERE id = ?1", params![id.as_str()])?;
        if was_default {
            let next: Option<String> = tx
                .query_row(
                    "SELECT id FROM profiles ORDER BY created_at_ms ASC, id ASC LIMIT 1",
                    [],
                    |row| row.get(0),
                )
                .optional()?;
            if let Some(next) = next {
                tx.execute(
                    "UPDATE profiles SET is_default = 1, updated_at_ms = ?1 WHERE id = ?2",
                    params![now, next],
                )?;
            }
        }
        tx.commit()?;
        Ok(())
    }

    pub fn count(&self) -> Result<u64, StoreError> {
        let count = self
            .conn
            .query_row("SELECT COUNT(*) FROM profiles", [], |row| row.get(0))?;
        Ok(count)
    }
}

fn map_profile(row: &Row<'_>) -> rusqlite::Result<ProfileRow> {
    Ok(ProfileRow {
        id: row.get(0)?,
        name: row.get(1)?,
        icon: row.get(2)?,
        is_default: row.get(3)?,
        created_at_ms: row.get(4)?,
        updated_at_ms: row.get(5)?,
    })
}
