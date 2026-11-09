use crate::db::{now_ms, validate_text};
use crate::error::StoreError;
use amare_core::ProfileId;
use rusqlite::{Connection, OptionalExtension, Row, params};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DownloadRecord {
    pub id: i64,
    pub profile_id: ProfileId,
    pub filename: String,
    pub url: String,
    pub path: String,
    pub mime_type: String,
    pub total_bytes: i64,
    pub received_bytes: i64,
    pub state: String,
    pub error: String,
    pub created_at_ms: i64,
    pub updated_at_ms: i64,
}

pub struct DownloadRepository<'a> {
    conn: &'a Connection,
    profile_id: &'a ProfileId,
}

impl<'a> DownloadRepository<'a> {
    pub(crate) fn new(conn: &'a Connection, profile_id: &'a ProfileId) -> Self {
        Self { conn, profile_id }
    }

    pub fn list(&self, limit: u32, offset: u64) -> Result<Vec<DownloadRecord>, StoreError> {
        let limit = limit.clamp(1, 500);
        let mut stmt = self.conn.prepare(
            "SELECT id, filename, url, path, mime_type, total_bytes, received_bytes, state, error, created_at_ms, updated_at_ms
             FROM downloads ORDER BY created_at_ms DESC, id DESC LIMIT ?1 OFFSET ?2",
        )?;
        let rows = stmt.query_map(params![limit, offset], map_download)?;
        let mut out = Vec::new();
        for row in rows {
            out.push(row?.into_record(self.profile_id));
        }
        Ok(out)
    }

    #[allow(clippy::too_many_arguments)]
    pub fn upsert(
        &self,
        id: i64,
        filename: &str,
        url: &str,
        path: &str,
        mime_type: &str,
        total_bytes: i64,
        received_bytes: i64,
        state: &str,
        error: &str,
    ) -> Result<(), StoreError> {
        validate_text("download filename", filename, 1024)?;
        validate_text("download url", url, 8192)?;
        validate_text("download path", path, 8192)?;
        if state.len() > 64 || error.len() > 2048 {
            return Err(StoreError::InvalidInput(
                "download state/error too long".into(),
            ));
        }
        let now = now_ms();
        self.conn.execute(
            "INSERT INTO downloads(id, filename, url, path, mime_type, total_bytes, received_bytes, state, error, created_at_ms, updated_at_ms)
             VALUES(?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?10)
             ON CONFLICT(id) DO UPDATE SET
               filename=excluded.filename, url=excluded.url, path=excluded.path,
               mime_type=excluded.mime_type, total_bytes=excluded.total_bytes,
               received_bytes=excluded.received_bytes, state=excluded.state,
               error=excluded.error, updated_at_ms=excluded.updated_at_ms",
            params![id, filename, url, path, mime_type, total_bytes, received_bytes, state, error, now],
        )?;
        Ok(())
    }

    pub fn delete(&self, id: i64) -> Result<bool, StoreError> {
        Ok(self
            .conn
            .execute("DELETE FROM downloads WHERE id = ?1", params![id])?
            > 0)
    }

    pub fn get(&self, id: i64) -> Result<Option<DownloadRecord>, StoreError> {
        let row = self
            .conn
            .query_row(
                "SELECT id, filename, url, path, mime_type, total_bytes, received_bytes, state, error, created_at_ms, updated_at_ms
                 FROM downloads WHERE id = ?1",
                params![id],
                map_download,
            )
            .optional()?;
        Ok(row.map(|row| row.into_record(self.profile_id)))
    }
}

struct DownloadRow {
    id: i64,
    filename: String,
    url: String,
    path: String,
    mime_type: String,
    total_bytes: i64,
    received_bytes: i64,
    state: String,
    error: String,
    created_at_ms: i64,
    updated_at_ms: i64,
}

impl DownloadRow {
    fn into_record(self, profile_id: &ProfileId) -> DownloadRecord {
        DownloadRecord {
            id: self.id,
            profile_id: profile_id.clone(),
            filename: self.filename,
            url: self.url,
            path: self.path,
            mime_type: self.mime_type,
            total_bytes: self.total_bytes,
            received_bytes: self.received_bytes,
            state: self.state,
            error: self.error,
            created_at_ms: self.created_at_ms,
            updated_at_ms: self.updated_at_ms,
        }
    }
}

fn map_download(row: &Row<'_>) -> rusqlite::Result<DownloadRow> {
    Ok(DownloadRow {
        id: row.get(0)?,
        filename: row.get(1)?,
        url: row.get(2)?,
        path: row.get(3)?,
        mime_type: row.get(4)?,
        total_bytes: row.get(5)?,
        received_bytes: row.get(6)?,
        state: row.get(7)?,
        error: row.get(8)?,
        created_at_ms: row.get(9)?,
        updated_at_ms: row.get(10)?,
    })
}
