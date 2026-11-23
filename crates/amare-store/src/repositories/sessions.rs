use crate::db::{normalize_limit, now_ms, validate_text};
use crate::error::StoreError;
use crate::models::{SessionRow, SessionTabRow, SessionWindowRow};
use amare_core::{
    NewSessionTab, NewSessionWindow, Page, ProfileId, Session, SessionState, SessionTab,
    SessionWindowsState,
};
use rusqlite::{Connection, OptionalExtension, Row, Transaction, params};

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
        let tab_ids = insert_tabs(&tx, session_id, "main", tabs, now)?;
        let active_tab_id = resolve_active(&tab_ids, active_position)?;
        if let Some(tab_id) = active_tab_id {
            tx.execute(
                "UPDATE sessions SET active_tab_id = ?1, updated_at_ms = ?2 WHERE id = ?3",
                params![tab_id, now, session_id],
            )?;
        }
        insert_window(
            &tx,
            session_id,
            "main",
            0,
            active_tab_id,
            &NewSessionWindow {
                window_id: "main".into(),
                ordinal: 0,
                active_position,
                bounds_x: None,
                bounds_y: None,
                bounds_width: None,
                bounds_height: None,
                maximized: false,
                tabs: Vec::new(),
            },
            now,
        )?;
        let state = load_state(&tx, self.profile_id, session_id)?;
        tx.commit()?;
        Ok(state)
    }

    /// Atomically create or update one window inside a session group.
    ///
    /// Only the target window's tabs are replaced. Other windows in the same
    /// session are never modified by this call.
    pub fn save_window(
        &self,
        session_id: Option<i64>,
        name: &str,
        window: &NewSessionWindow,
    ) -> Result<SessionWindowsState, StoreError> {
        validate_text("session name", name, 256)?;
        validate_window(window)?;
        let now = now_ms();
        let tx = crate::db::write_transaction(self.conn)?;
        let session_id = match session_id {
            Some(id) => {
                let exists: Option<i64> = tx
                    .query_row(
                        "SELECT id FROM sessions WHERE id = ?1",
                        params![id],
                        |row| row.get(0),
                    )
                    .optional()?;
                exists.ok_or_else(|| StoreError::NotFound(format!("session {id}")))?
            }
            None => {
                tx.execute(
                    "INSERT INTO sessions(name, active_tab_id, created_at_ms, updated_at_ms)
                     VALUES (?1, NULL, ?2, ?2)",
                    params![name, now],
                )?;
                tx.last_insert_rowid()
            }
        };

        let active_tab_id = upsert_window(&tx, session_id, window, now)?;
        let current_active: Option<i64> = tx.query_row(
            "SELECT active_tab_id FROM sessions WHERE id = ?1",
            params![session_id],
            |row| row.get(0),
        )?;
        // Keep the legacy sessions.active_tab_id useful for old clients while
        // the window row remains the source of truth for new clients.
        let mirror_active = if window.window_id == "main" {
            active_tab_id
        } else {
            current_active.or(active_tab_id)
        };
        tx.execute(
            "UPDATE sessions SET active_tab_id = ?1, updated_at_ms = ?2 WHERE id = ?3",
            params![mirror_active, now, session_id],
        )?;

        let state = load_windows_state(&tx, self.profile_id, session_id)?;
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

    /// Legacy flat tab view. New clients should use `restore_windows`.
    pub fn get_tabs(&self, session_id: i64) -> Result<Vec<SessionTab>, StoreError> {
        self.conn
            .query_row(
                "SELECT 1 FROM sessions WHERE id = ?1",
                params![session_id],
                |_| Ok(()),
            )
            .optional()?
            .ok_or_else(|| StoreError::NotFound(format!("session {session_id}")))?;
        query_tabs(self.conn, session_id, None)
    }

    pub fn restore(&self, id: i64) -> Result<SessionState, StoreError> {
        load_state(self.conn, self.profile_id, id)
    }

    pub fn restore_windows(&self, id: i64) -> Result<SessionWindowsState, StoreError> {
        load_windows_state(self.conn, self.profile_id, id)
    }

    pub fn latest_windows(&self) -> Result<Option<SessionWindowsState>, StoreError> {
        let session_id: Option<i64> = self
            .conn
            .query_row(
                "SELECT id FROM sessions ORDER BY updated_at_ms DESC, id DESC LIMIT 1",
                [],
                |row| row.get(0),
            )
            .optional()?;
        match session_id {
            Some(id) => Ok(Some(load_windows_state(self.conn, self.profile_id, id)?)),
            None => Ok(None),
        }
    }

    pub fn delete_window(&self, session_id: i64, window_id: &str) -> Result<bool, StoreError> {
        validate_text("window id", window_id, 128)?;
        let tx = crate::db::write_transaction(self.conn)?;
        tx.execute(
            "DELETE FROM session_tabs WHERE session_id = ?1 AND window_id = ?2",
            params![session_id, window_id],
        )?;
        let changed = tx.execute(
            "DELETE FROM session_windows WHERE session_id = ?1 AND window_id = ?2",
            params![session_id, window_id],
        )?;
        tx.execute(
            "UPDATE sessions SET updated_at_ms = ?1 WHERE id = ?2",
            params![now_ms(), session_id],
        )?;
        tx.commit()?;
        Ok(changed > 0)
    }

    /// Legacy single-window replacement. It intentionally flattens the session
    /// into the `main` window so an older client cannot silently corrupt the
    /// windowed structure while keeping the tabs it understands.
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
        tx.execute(
            "DELETE FROM session_windows WHERE session_id = ?1",
            params![session_id],
        )?;
        let tab_ids = insert_tabs(&tx, session_id, "main", tabs, now)?;
        let active_tab_id = resolve_active(&tab_ids, active_position)?;
        insert_window(
            &tx,
            session_id,
            "main",
            0,
            active_tab_id,
            &NewSessionWindow {
                window_id: "main".into(),
                ordinal: 0,
                active_position,
                bounds_x: None,
                bounds_y: None,
                bounds_width: None,
                bounds_height: None,
                maximized: false,
                tabs: Vec::new(),
            },
            now,
        )?;
        tx.execute(
            "UPDATE sessions SET active_tab_id = ?1, updated_at_ms = ?2 WHERE id = ?3",
            params![active_tab_id, now, session_id],
        )?;
        let state = load_state(&tx, self.profile_id, session_id)?;
        tx.commit()?;
        Ok(state)
    }

    pub fn set_active_tab(&self, session_id: i64, tab_id: Option<i64>) -> Result<(), StoreError> {
        let window_id = if let Some(tab_id) = tab_id {
            let window_id: Option<Option<String>> = self
                .conn
                .query_row(
                    "SELECT window_id FROM session_tabs WHERE id = ?1 AND session_id = ?2",
                    params![tab_id, session_id],
                    |row| row.get(0),
                )
                .optional()?;
            window_id.ok_or_else(|| {
                StoreError::NotFound(format!("tab {tab_id} in session {session_id}"))
            })?
        } else {
            None
        };
        let changed = self.conn.execute(
            "UPDATE sessions SET active_tab_id = ?1, updated_at_ms = ?2 WHERE id = ?3",
            params![tab_id, now_ms(), session_id],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("session {session_id}")));
        }
        if let Some(window_id) = window_id {
            self.conn.execute(
                "UPDATE session_windows SET active_tab_id = ?1, updated_at_ms = ?2
                 WHERE session_id = ?3 AND window_id = ?4",
                params![tab_id, now_ms(), session_id, window_id],
            )?;
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

fn validate_window(window: &NewSessionWindow) -> Result<(), StoreError> {
    validate_text("window id", &window.window_id, 128)?;
    if window.ordinal < 0 {
        return Err(StoreError::InvalidInput(
            "window ordinal must be non-negative".into(),
        ));
    }
    for (name, value) in [
        ("bounds width", window.bounds_width),
        ("bounds height", window.bounds_height),
    ] {
        if let Some(value) = value {
            if !value.is_finite() || value <= 0.0 {
                return Err(StoreError::InvalidInput(format!(
                    "{name} must be a positive finite number"
                )));
            }
        }
    }
    Ok(())
}

fn upsert_window(
    conn: &Transaction<'_>,
    session_id: i64,
    window: &NewSessionWindow,
    now: i64,
) -> Result<Option<i64>, StoreError> {
    conn.execute(
        "DELETE FROM session_tabs WHERE session_id = ?1 AND window_id = ?2",
        params![session_id, window.window_id],
    )?;
    let tab_ids = insert_tabs(conn, session_id, &window.window_id, &window.tabs, now)?;
    let active_tab_id = resolve_active(&tab_ids, window.active_position)?;
    insert_window(
        conn,
        session_id,
        &window.window_id,
        window.ordinal,
        active_tab_id,
        window,
        now,
    )?;
    Ok(active_tab_id)
}

#[allow(clippy::too_many_arguments)]
fn insert_window(
    conn: &Transaction<'_>,
    session_id: i64,
    window_id: &str,
    ordinal: i64,
    active_tab_id: Option<i64>,
    window: &NewSessionWindow,
    now: i64,
) -> Result<(), StoreError> {
    conn.execute(
        "INSERT INTO session_windows
            (session_id, window_id, ordinal, active_tab_id,
             bounds_x, bounds_y, bounds_width, bounds_height, maximized, updated_at_ms)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)
         ON CONFLICT(session_id, window_id) DO UPDATE SET
            ordinal = excluded.ordinal,
            active_tab_id = excluded.active_tab_id,
            bounds_x = excluded.bounds_x,
            bounds_y = excluded.bounds_y,
            bounds_width = excluded.bounds_width,
            bounds_height = excluded.bounds_height,
            maximized = excluded.maximized,
            updated_at_ms = excluded.updated_at_ms",
        params![
            session_id,
            window_id,
            ordinal,
            active_tab_id,
            window.bounds_x,
            window.bounds_y,
            window.bounds_width,
            window.bounds_height,
            window.maximized,
            now
        ],
    )?;
    Ok(())
}

fn insert_tabs(
    conn: &Connection,
    session_id: i64,
    window_id: &str,
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
                (session_id, window_id, url, title, position, pinned, group_id, last_accessed_at_ms)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
            params![
                session_id,
                window_id,
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
    let session = load_session(conn, profile_id, session_id)?;
    let tabs = query_tabs(conn, session_id, None)?;
    Ok(SessionState { session, tabs })
}

fn load_windows_state(
    conn: &Connection,
    profile_id: &ProfileId,
    session_id: i64,
) -> Result<SessionWindowsState, StoreError> {
    let session = load_session(conn, profile_id, session_id)?;
    let mut stmt = conn.prepare(
        "SELECT window_id, ordinal, active_tab_id,
                bounds_x, bounds_y, bounds_width, bounds_height,
                maximized, updated_at_ms
         FROM session_windows WHERE session_id = ?1 ORDER BY ordinal ASC, id ASC",
    )?;
    let rows = stmt.query_map(params![session_id], map_window)?;
    let mut windows = Vec::new();
    for row in rows {
        let row = row?;
        let tabs = query_tabs(conn, session_id, Some(&row.window_id))?;
        windows.push(row.into_domain(tabs));
    }
    Ok(SessionWindowsState { session, windows })
}

fn load_session(
    conn: &Connection,
    profile_id: &ProfileId,
    session_id: i64,
) -> Result<Session, StoreError> {
    conn.query_row(
        "SELECT id, name, active_tab_id, created_at_ms, updated_at_ms
         FROM sessions WHERE id = ?1",
        params![session_id],
        map_session,
    )
    .optional()?
    .ok_or_else(|| StoreError::NotFound(format!("session {session_id}")))
    .map(|row| row.into_domain(profile_id))
}

fn query_tabs(
    conn: &Connection,
    session_id: i64,
    window_id: Option<&str>,
) -> Result<Vec<SessionTab>, StoreError> {
    let mut tabs = Vec::new();
    if let Some(window_id) = window_id {
        let mut stmt = conn.prepare(
            "SELECT id, session_id, url, title, position, pinned, group_id, last_accessed_at_ms
             FROM session_tabs WHERE session_id = ?1 AND window_id = ?2
             ORDER BY position ASC, id ASC",
        )?;
        let rows = stmt.query_map(params![session_id, window_id], map_tab)?;
        for row in rows {
            tabs.push(row?.into_domain());
        }
    } else {
        let mut stmt = conn.prepare(
            "SELECT id, session_id, url, title, position, pinned, group_id, last_accessed_at_ms
             FROM session_tabs WHERE session_id = ?1 ORDER BY position ASC, id ASC",
        )?;
        let rows = stmt.query_map(params![session_id], map_tab)?;
        for row in rows {
            tabs.push(row?.into_domain());
        }
    }
    Ok(tabs)
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

fn map_window(row: &Row<'_>) -> rusqlite::Result<SessionWindowRow> {
    Ok(SessionWindowRow {
        window_id: row.get(0)?,
        ordinal: row.get(1)?,
        active_tab_id: row.get(2)?,
        bounds_x: row.get(3)?,
        bounds_y: row.get(4)?,
        bounds_width: row.get(5)?,
        bounds_height: row.get(6)?,
        maximized: row.get(7)?,
        updated_at_ms: row.get(8)?,
    })
}
