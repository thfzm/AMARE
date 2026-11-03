use crate::db::{normalize_limit, validate_text};
use crate::error::StoreError;
use crate::models::HistoryRow;
use crate::repositories::settings::escape_like;
use amare_core::{HistoryEntry, NewHistoryVisit, Page, ProfileId};
use rusqlite::{Connection, Row, params};

pub struct HistoryRepository<'a> {
    conn: &'a Connection,
    profile_id: &'a ProfileId,
}

impl<'a> HistoryRepository<'a> {
    pub(crate) fn new(conn: &'a Connection, profile_id: &'a ProfileId) -> Self {
        Self { conn, profile_id }
    }

    pub fn record_visit(&self, visit: NewHistoryVisit) -> Result<(), StoreError> {
        validate_text("history url", &visit.url, 8192)?;
        if visit.title.len() > 2048 {
            return Err(StoreError::InvalidInput(
                "history title must be <= 2048 bytes".into(),
            ));
        }
        self.conn.execute(
            "INSERT INTO history_visits(url, title, visited_at_ms) VALUES (?1, ?2, ?3)",
            params![visit.url, visit.title, visit.visited_at_ms],
        )?;
        Ok(())
    }

    pub fn record_visits(&self, visits: &[NewHistoryVisit]) -> Result<(), StoreError> {
        let tx = crate::db::write_transaction(self.conn)?;
        for visit in visits {
            validate_text("history url", &visit.url, 8192)?;
            if visit.title.len() > 2048 {
                return Err(StoreError::InvalidInput(
                    "history title must be <= 2048 bytes".into(),
                ));
            }
            tx.execute(
                "INSERT INTO history_visits(url, title, visited_at_ms) VALUES (?1, ?2, ?3)",
                params![visit.url, visit.title, visit.visited_at_ms],
            )?;
        }
        tx.commit()?;
        Ok(())
    }

    pub fn list(&self, limit: u32, offset: u64) -> Result<Page<HistoryEntry>, StoreError> {
        self.search(None, None, None, limit, offset)
    }

    pub fn search(
        &self,
        query: Option<&str>,
        from_ms: Option<i64>,
        to_ms: Option<i64>,
        limit: u32,
        offset: u64,
    ) -> Result<Page<HistoryEntry>, StoreError> {
        if let (Some(from), Some(to)) = (from_ms, to_ms) {
            if from > to {
                return Err(StoreError::InvalidInput(
                    "history from_ms must be <= to_ms".into(),
                ));
            }
        }
        let limit = normalize_limit(limit);
        let pattern = query.map(|value| format!("%{}%", escape_like(value)));
        let total: u64 = self.conn.query_row(
            "SELECT COUNT(DISTINCT url) FROM history_visits
             WHERE (?1 IS NULL OR url LIKE ?1 ESCAPE '\\' OR title LIKE ?1 ESCAPE '\\')
               AND (?2 IS NULL OR visited_at_ms >= ?2)
               AND (?3 IS NULL OR visited_at_ms <= ?3)",
            params![pattern, from_ms, to_ms],
            |row| row.get(0),
        )?;

        let mut stmt = self.conn.prepare(
            "SELECT
                MAX(id) AS latest_id,
                url,
                (SELECT h2.title FROM history_visits h2
                 WHERE h2.url = h.url
                 ORDER BY h2.visited_at_ms DESC, h2.id DESC LIMIT 1) AS latest_title,
                MIN(visited_at_ms) AS first_visited,
                MAX(visited_at_ms) AS last_visited,
                COUNT(*) AS visit_count
             FROM history_visits h
             WHERE (?1 IS NULL OR url LIKE ?1 ESCAPE '\\' OR title LIKE ?1 ESCAPE '\\')
               AND (?2 IS NULL OR visited_at_ms >= ?2)
               AND (?3 IS NULL OR visited_at_ms <= ?3)
             GROUP BY url
             ORDER BY last_visited DESC, url ASC
             LIMIT ?4 OFFSET ?5",
        )?;
        let rows = stmt.query_map(params![pattern, from_ms, to_ms, limit, offset], map_history)?;
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

    pub fn delete_range(&self, from_ms: i64, to_ms: i64) -> Result<usize, StoreError> {
        if from_ms > to_ms {
            return Err(StoreError::InvalidInput(
                "history from_ms must be <= to_ms".into(),
            ));
        }
        let changed = self.conn.execute(
            "DELETE FROM history_visits WHERE visited_at_ms BETWEEN ?1 AND ?2",
            params![from_ms, to_ms],
        )?;
        Ok(changed)
    }

    pub fn delete_url(&self, url: &str) -> Result<usize, StoreError> {
        let changed = self
            .conn
            .execute("DELETE FROM history_visits WHERE url = ?1", params![url])?;
        Ok(changed)
    }

    pub fn clear(&self) -> Result<usize, StoreError> {
        let changed = self.conn.execute("DELETE FROM history_visits", [])?;
        Ok(changed)
    }
}

fn map_history(row: &Row<'_>) -> rusqlite::Result<HistoryRow> {
    Ok(HistoryRow {
        id: row.get(0)?,
        url: row.get(1)?,
        title: row.get(2)?,
        first_visited_at_ms: row.get(3)?,
        last_visited_at_ms: row.get(4)?,
        visit_count: row.get(5)?,
    })
}
