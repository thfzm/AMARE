use crate::db::{normalize_limit, now_ms, validate_text};
use crate::error::StoreError;
use crate::models::{SessionRow, SessionTabRow};
use amare_core::{NewSessionTab, Page, ProfileId, Session, SessionState, SessionTab};
use rusqlite::{Connection, OptionalExtension, Row, params};

pub struct SessionRepository<'a> {
    conn: &'a Connection,
    profile_id: &'a ProfileId,
}

impl<'a> SessionRepository<'a> {
    pub(crate) fn new(conn: &'a Connection, profile_id: &'a ProfileId) -> Self {
        Self { conn, profile_id }
    }

    pub fn create(
        &self,
        name: &str,
        tabs: &[NewSessionTab],
        active_position: Option<usize>,
    ) -> Result<SessionState, StoreError> {
        validate_text("session name", name, 256)?;
        let now = now_ms();
        let tx = crate::db::write_transaction(self.conn)?;
        tx.execute(
            "INSERT INTO sessions(name, active_tab_id, created_at_ms, updated_at_ms)
             VALUES (?1, NULL, ?2, ?2)",
            params![name, now],
        )?;
        let session_id = tx.last_insert_rowid();
        let tab_ids = insert_tabs(&tx, session_id, tabs, now)?;
        let active_tab_id = resolve_active(&tab_ids, active_position)?;
        if let Some(tab_id) = active_tab_id {
            tx.execute(
                "UPDATE sessions SET active_tab_id = ?1, updated_at_ms = ?2 WHERE id = ?3",
                params![tab_id, now, session_id],
            )?;
        }
        let state = load_state(&tx, self.profile_id, session_id)?;
        tx.commit()?;
        Ok(state)
    }

    pub fn get(&self, id: i64) -> Result<Option<Session>, StoreError> {
        let row = self
            .conn
            .query_row(
                "SELECT id, name, active_tab_id, created_at_ms, updated_at_ms
                 FROM sessions WHERE id = ?1",
                params![id],
                map_session,
            )
            .optional()?;
        Ok(row.map(|row| row.into_domain(self.profile_id)))
    }

    pub fn list(&self, limit: u32, offset: u64) -> Result<Page<Session>, StoreError> {
        let limit = normalize_limit(limit);
        let total: u64 = self
            .conn
            .query_row("SELECT COUNT(*) FROM sessions", [], |row| row.get(0))?;
        let mut stmt = self.conn.prepare(
            "SELECT id, name, active_tab_id, created_at_ms, updated_at_ms
             FROM sessions ORDER BY updated_at_ms DESC, id DESC
             LIMIT ?1 OFFSET ?2",
        )?;
        let rows = stmt.query_map(params![limit, offset], map_session)?;
        let mut items = Vec::new();
        for row in rows {
            items.push(row?.into_domain(self.profile_id));
        }
        Ok(Page {
            items,
            total,
            limit,
            offset,
        })
    }

    pub fn get_tabs(&self, session_id: i64) -> Result<Vec<SessionTab>, StoreError> {
        self.conn
            .query_row(
                "SELECT 1 FROM sessions WHERE id = ?1",
                params![session_id],
                |_| Ok(()),
            )
            .optional()?
            .ok_or_else(|| StoreError::NotFound(format!("session {session_id}")))?;
        let mut stmt = self.conn.prepare(
            "SELECT id, session_id, url, title, position, pinned, group_id, last_accessed_at_ms
             FROM session_tabs WHERE session_id = ?1
             ORDER BY position ASC, id ASC",
        )?;
        let rows = stmt.query_map(params![session_id], map_tab)?;
        let mut tabs = Vec::new();
        for row in rows {
            tabs.push(row?.into_domain());
        }
        Ok(tabs)
    }

    pub fn restore(&self, id: i64) -> Result<SessionState, StoreError> {
        load_state(self.conn, self.profile_id, id)
    }

    pub fn replace_tabs(
        &self,
        session_id: i64,
        tabs: &[NewSessionTab],
        active_position: Option<usize>,
    ) -> Result<SessionState, StoreError> {
        let now = now_ms();
        let tx = crate::db::write_transaction(self.conn)?;
        let exists: Option<i64> = tx
            .query_row(
                "SELECT id FROM sessions WHERE id = ?1",
                params![session_id],
                |row| row.get(0),
            )
            .optional()?;
        if exists.is_none() {
            return Err(StoreError::NotFound(format!("session {session_id}")));
        }
        tx.execute(
            "DELETE FROM session_tabs WHERE session_id = ?1",
            params![session_id],
        )?;
        let tab_ids = insert_tabs(&tx, session_id, tabs, now)?;
        let active_tab_id = resolve_active(&tab_ids, active_position)?;
        tx.execute(
            "UPDATE sessions SET active_tab_id = ?1, updated_at_ms = ?2 WHERE id = ?3",
            params![active_tab_id, now, session_id],
        )?;
        let state = load_state(&tx, self.profile_id, session_id)?;
        tx.commit()?;
        Ok(state)
    }

    pub fn set_active_tab(&self, session_id: i64, tab_id: Option<i64>) -> Result<(), StoreError> {
        if let Some(tab_id) = tab_id {
            let count: u32 = self.conn.query_row(
                "SELECT COUNT(*) FROM session_tabs WHERE id = ?1 AND session_id = ?2",
                params![tab_id, session_id],
                |row| row.get(0),
            )?;
            if count == 0 {
                return Err(StoreError::NotFound(format!(
                    "tab {tab_id} in session {session_id}"
                )));
            }
        }
        let changed = self.conn.execute(
            "UPDATE sessions SET active_tab_id = ?1, updated_at_ms = ?2 WHERE id = ?3",
            params![tab_id, now_ms(), session_id],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("session {session_id}")));
        }
        Ok(())
    }

    pub fn rename(&self, session_id: i64, name: &str) -> Result<(), StoreError> {
        validate_text("session name", name, 256)?;
        let changed = self.conn.execute(
            "UPDATE sessions SET name = ?1, updated_at_ms = ?2 WHERE id = ?3",
            params![name, now_ms(), session_id],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("session {session_id}")));
        }
        Ok(())
    }

    pub fn delete(&self, id: i64) -> Result<bool, StoreError> {
        let changed = self
            .conn
            .execute("DELETE FROM sessions WHERE id = ?1", params![id])?;
        Ok(changed > 0)
    }
}

fn insert_tabs(
    conn: &Connection,
    session_id: i64,
    tabs: &[NewSessionTab],
    now: i64,
) -> Result<Vec<i64>, StoreError> {
    let mut ids = Vec::with_capacity(tabs.len());
    for (index, tab) in tabs.iter().enumerate() {
        validate_text("session tab url", &tab.url, 8192)?;
        if tab.title.len() > 2048 {
            return Err(StoreError::InvalidInput(
                "session tab title must be <= 2048 bytes".into(),
            ));
        }
        if let Some(group_id) = &tab.group_id {
            if group_id.len() > 128 {
                return Err(StoreError::InvalidInput(
                    "session tab group id must be <= 128 bytes".into(),
                ));
            }
        }
        let position = tab.position.unwrap_or(index as i64);
        conn.execute(
            "INSERT INTO session_tabs
                (session_id, url, title, position, pinned, group_id, last_accessed_at_ms)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![
                session_id,
                tab.url,
                tab.title,
                position,
                tab.pinned,
                tab.group_id,
                now
            ],
        )?;
        ids.push(conn.last_insert_rowid());
    }
    Ok(ids)
}

fn resolve_active(ids: &[i64], position: Option<usize>) -> Result<Option<i64>, StoreError> {
    match position {
        None => Ok(None),
        Some(index) => {
            ids.get(index).copied().map(Some).ok_or_else(|| {
                StoreError::InvalidInput("active tab position is out of range".into())
            })
        }
    }
}

fn load_state(
    conn: &Connection,
    profile_id: &ProfileId,
    session_id: i64,
) -> Result<SessionState, StoreError> {
    let session = conn
        .query_row(
            "SELECT id, name, active_tab_id, created_at_ms, updated_at_ms
             FROM sessions WHERE id = ?1",
            params![session_id],
            map_session,
        )
        .optional()?
        .ok_or_else(|| StoreError::NotFound(format!("session {session_id}")))?
        .into_domain(profile_id);
    let mut stmt = conn.prepare(
        "SELECT id, session_id, url, title, position, pinned, group_id, last_accessed_at_ms
         FROM session_tabs WHERE session_id = ?1 ORDER BY position ASC, id ASC",
    )?;
    let rows = stmt.query_map(params![session_id], map_tab)?;
    let mut tabs = Vec::new();
    for row in rows {
        tabs.push(row?.into_domain());
    }
    Ok(SessionState { session, tabs })
}

fn map_session(row: &Row<'_>) -> rusqlite::Result<SessionRow> {
    Ok(SessionRow {
        id: row.get(0)?,
        name: row.get(1)?,
        active_tab_id: row.get(2)?,
        created_at_ms: row.get(3)?,
        updated_at_ms: row.get(4)?,
    })
}

fn map_tab(row: &Row<'_>) -> rusqlite::Result<SessionTabRow> {
    Ok(SessionTabRow {
        id: row.get(0)?,
        session_id: row.get(1)?,
        url: row.get(2)?,
        title: row.get(3)?,
        position: row.get(4)?,
        pinned: row.get(5)?,
        group_id: row.get(6)?,
        last_accessed_at_ms: row.get(7)?,
    })
}
