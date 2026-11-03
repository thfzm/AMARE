//! Cross-domain search over FTS5 indexes.
//!
//! Every result carries its original type and row ID. Callers must re-fetch the
//! row through the owning repository before displaying or mutating it.

use crate::db::normalize_limit;
use crate::error::StoreError;
use amare_core::{SearchHit, SearchKind};
use rusqlite::{Connection, params};

pub struct SearchRepository<'a> {
    conn: &'a Connection,
}

impl<'a> SearchRepository<'a> {
    pub(crate) fn new(conn: &'a Connection) -> Self {
        Self { conn }
    }

    pub fn search(&self, query: &str, limit: u32) -> Result<Vec<SearchHit>, StoreError> {
        self.search_kinds(query, &ALL_FTS_KINDS, limit)
    }

    /// Searches the selected FTS-backed kinds. Bookmark/history search is owned
    /// by their repositories and is composed by the service layer.
    pub fn search_kinds(
        &self,
        query: &str,
        kinds: &[SearchKind],
        per_kind_limit: u32,
    ) -> Result<Vec<SearchHit>, StoreError> {
        let per_kind_limit = normalize_limit(per_kind_limit);
        let Some(match_query) = fts_query(query) else {
            return Ok(Vec::new());
        };
        let mut hits = Vec::new();
        for kind in kinds {
            match kind {
                SearchKind::LibraryItem => {
                    self.library_hits(&match_query, per_kind_limit, &mut hits)?
                }
                SearchKind::Tag => self.tag_hits(&match_query, per_kind_limit, &mut hits)?,
                SearchKind::Collection => {
                    self.collection_hits(&match_query, per_kind_limit, &mut hits)?
                }
                SearchKind::Conversation => {
                    self.conversation_hits(&match_query, per_kind_limit, &mut hits)?
                }
                SearchKind::Message => {
                    self.message_hits(&match_query, per_kind_limit, &mut hits)?
                }
                SearchKind::Workspace => {
                    self.workspace_hits(&match_query, per_kind_limit, &mut hits)?
                }
                SearchKind::Task => self.task_hits(&match_query, per_kind_limit, &mut hits)?,
                SearchKind::TaskMemory => {
                    self.memory_hits(&match_query, per_kind_limit, &mut hits)?
                }
                SearchKind::Bookmark | SearchKind::History => {}
            }
        }
        Ok(hits)
    }

    /// Counts matches without loading result rows.
    pub fn count_kinds(&self, query: &str, kinds: &[SearchKind]) -> Result<u64, StoreError> {
        let Some(match_query) = fts_query(query) else {
            return Ok(0);
        };
        let mut total = 0u64;
        for kind in kinds {
            total += match kind {
                SearchKind::LibraryItem => self.count(
                    "SELECT COUNT(*) FROM library_fts
                     JOIN library_items i ON i.id = library_fts.rowid
                     WHERE library_fts MATCH ?1 AND i.status != 'trashed'",
                    &match_query,
                )?,
                SearchKind::Tag => self.count(
                    "SELECT COUNT(*) FROM tag_fts
                     JOIN tags t ON t.id = tag_fts.rowid
                     WHERE tag_fts MATCH ?1",
                    &match_query,
                )?,
                SearchKind::Collection => self.count(
                    "SELECT COUNT(*) FROM collection_fts
                     JOIN collections c ON c.id = collection_fts.rowid
                     WHERE collection_fts MATCH ?1",
                    &match_query,
                )?,
                SearchKind::Conversation => self.count(
                    "SELECT COUNT(*) FROM conversation_fts
                     JOIN conversations c ON c.id = conversation_fts.rowid
                     WHERE conversation_fts MATCH ?1 AND c.deleted_at_ms IS NULL",
                    &match_query,
                )?,
                SearchKind::Message => self.count(
                    "SELECT COUNT(*) FROM message_fts
                     JOIN messages m ON m.id = message_fts.rowid
                     JOIN conversations c ON c.id = m.conversation_id
                     WHERE message_fts MATCH ?1 AND c.deleted_at_ms IS NULL
                       AND m.visibility = 'user'",
                    &match_query,
                )?,
                SearchKind::Workspace => self.count(
                    "SELECT COUNT(*) FROM workspace_fts
                     JOIN workspaces w ON w.id = workspace_fts.rowid
                     WHERE workspace_fts MATCH ?1 AND w.archived = 0",
                    &match_query,
                )?,
                SearchKind::Task => self.count(
                    "SELECT COUNT(*) FROM task_fts
                     JOIN tasks t ON t.id = task_fts.rowid
                     WHERE task_fts MATCH ?1 AND t.status != 'archived'",
                    &match_query,
                )?,
                SearchKind::TaskMemory => self.count(
                    "SELECT COUNT(*) FROM task_memory_fts
                     JOIN task_memories m ON m.id = task_memory_fts.rowid
                     JOIN tasks t ON t.id = m.task_id
                     WHERE task_memory_fts MATCH ?1 AND t.status != 'archived'",
                    &match_query,
                )?,
                SearchKind::Bookmark | SearchKind::History => 0,
            };
        }
        Ok(total)
    }

    fn count(&self, sql: &str, query: &str) -> Result<u64, StoreError> {
        Ok(self.conn.query_row(sql, params![query], |row| row.get(0))?)
    }

    fn library_hits(
        &self,
        query: &str,
        limit: u32,
        hits: &mut Vec<SearchHit>,
    ) -> Result<(), StoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT i.id, i.title, snippet(library_fts, 1, '', '', '…', 24),
                    i.updated_at_ms
             FROM library_fts JOIN library_items i ON i.id = library_fts.rowid
             WHERE library_fts MATCH ?1 AND i.status != 'trashed'
             ORDER BY bm25(library_fts), i.updated_at_ms DESC LIMIT ?2",
        )?;
        let rows = stmt.query_map(params![query, limit], |row| {
            Ok(SearchHit {
                kind: SearchKind::LibraryItem,
                id: row.get(0)?,
                title: row.get(1)?,
                snippet: row.get(2)?,
                updated_at_ms: row.get(3)?,
            })
        })?;
        push_rows(rows, hits)
    }

    fn tag_hits(
        &self,
        query: &str,
        limit: u32,
        hits: &mut Vec<SearchHit>,
    ) -> Result<(), StoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT t.id, t.name, snippet(tag_fts, 0, '', '', '…', 8),
                    t.created_at_ms
             FROM tag_fts JOIN tags t ON t.id = tag_fts.rowid
             WHERE tag_fts MATCH ?1 ORDER BY bm25(tag_fts), t.name COLLATE NOCASE LIMIT ?2",
        )?;
        let rows = stmt.query_map(params![query, limit], |row| {
            Ok(SearchHit {
                kind: SearchKind::Tag,
                id: row.get(0)?,
                title: row.get(1)?,
                snippet: row.get(2)?,
                updated_at_ms: row.get(3)?,
            })
        })?;
        push_rows(rows, hits)
    }

    fn collection_hits(
        &self,
        query: &str,
        limit: u32,
        hits: &mut Vec<SearchHit>,
    ) -> Result<(), StoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT c.id, c.name, snippet(collection_fts, 1, '', '', '…', 24),
                    c.updated_at_ms
             FROM collection_fts JOIN collections c ON c.id = collection_fts.rowid
             WHERE collection_fts MATCH ?1
             ORDER BY bm25(collection_fts), c.name COLLATE NOCASE LIMIT ?2",
        )?;
        let rows = stmt.query_map(params![query, limit], |row| {
            Ok(SearchHit {
                kind: SearchKind::Collection,
                id: row.get(0)?,
                title: row.get(1)?,
                snippet: row.get(2)?,
                updated_at_ms: row.get(3)?,
            })
        })?;
        push_rows(rows, hits)
    }

    fn conversation_hits(
        &self,
        query: &str,
        limit: u32,
        hits: &mut Vec<SearchHit>,
    ) -> Result<(), StoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT c.id, c.title, NULL, c.last_message_at_ms
             FROM conversation_fts JOIN conversations c ON c.id = conversation_fts.rowid
             WHERE conversation_fts MATCH ?1 AND c.deleted_at_ms IS NULL
             ORDER BY bm25(conversation_fts), c.last_message_at_ms DESC LIMIT ?2",
        )?;
        let rows = stmt.query_map(params![query, limit], |row| {
            Ok(SearchHit {
                kind: SearchKind::Conversation,
                id: row.get(0)?,
                title: row.get(1)?,
                snippet: row.get(2)?,
                updated_at_ms: row.get(3)?,
            })
        })?;
        push_rows(rows, hits)
    }

    fn message_hits(
        &self,
        query: &str,
        limit: u32,
        hits: &mut Vec<SearchHit>,
    ) -> Result<(), StoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT m.id, c.title, snippet(message_fts, 0, '', '', '…', 24),
                    m.created_at_ms
             FROM message_fts
             JOIN messages m ON m.id = message_fts.rowid
             JOIN conversations c ON c.id = m.conversation_id
             WHERE message_fts MATCH ?1 AND c.deleted_at_ms IS NULL
               AND m.visibility = 'user'
             ORDER BY bm25(message_fts), m.created_at_ms DESC LIMIT ?2",
        )?;
        let rows = stmt.query_map(params![query, limit], |row| {
            Ok(SearchHit {
                kind: SearchKind::Message,
                id: row.get(0)?,
                title: row.get(1)?,
                snippet: row.get(2)?,
                updated_at_ms: row.get(3)?,
            })
        })?;
        push_rows(rows, hits)
    }

    fn workspace_hits(
        &self,
        query: &str,
        limit: u32,
        hits: &mut Vec<SearchHit>,
    ) -> Result<(), StoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT w.id, w.name, snippet(workspace_fts, 1, '', '', '…', 24),
                    w.updated_at_ms
             FROM workspace_fts JOIN workspaces w ON w.id = workspace_fts.rowid
             WHERE workspace_fts MATCH ?1 AND w.archived = 0
             ORDER BY bm25(workspace_fts), w.updated_at_ms DESC LIMIT ?2",
        )?;
        let rows = stmt.query_map(params![query, limit], |row| {
            Ok(SearchHit {
                kind: SearchKind::Workspace,
                id: row.get(0)?,
                title: row.get(1)?,
                snippet: row.get(2)?,
                updated_at_ms: row.get(3)?,
            })
        })?;
        push_rows(rows, hits)
    }

    fn task_hits(
        &self,
        query: &str,
        limit: u32,
        hits: &mut Vec<SearchHit>,
    ) -> Result<(), StoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT t.id, t.title, snippet(task_fts, 1, '', '', '…', 24),
                    t.updated_at_ms
             FROM task_fts JOIN tasks t ON t.id = task_fts.rowid
             WHERE task_fts MATCH ?1 AND t.status != 'archived'
             ORDER BY bm25(task_fts), t.updated_at_ms DESC LIMIT ?2",
        )?;
        let rows = stmt.query_map(params![query, limit], |row| {
            Ok(SearchHit {
                kind: SearchKind::Task,
                id: row.get(0)?,
                title: row.get(1)?,
                snippet: row.get(2)?,
                updated_at_ms: row.get(3)?,
            })
        })?;
        push_rows(rows, hits)
    }

    fn memory_hits(
        &self,
        query: &str,
        limit: u32,
        hits: &mut Vec<SearchHit>,
    ) -> Result<(), StoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT m.id, m.title, snippet(task_memory_fts, 1, '', '', '…', 24),
                    m.updated_at_ms
             FROM task_memory_fts
             JOIN task_memories m ON m.id = task_memory_fts.rowid
             JOIN tasks t ON t.id = m.task_id
             WHERE task_memory_fts MATCH ?1 AND t.status != 'archived'
             ORDER BY bm25(task_memory_fts), m.updated_at_ms DESC LIMIT ?2",
        )?;
        let rows = stmt.query_map(params![query, limit], |row| {
            Ok(SearchHit {
                kind: SearchKind::TaskMemory,
                id: row.get(0)?,
                title: row.get(1)?,
                snippet: row.get(2)?,
                updated_at_ms: row.get(3)?,
            })
        })?;
        push_rows(rows, hits)
    }
}

const ALL_FTS_KINDS: [SearchKind; 8] = [
    SearchKind::LibraryItem,
    SearchKind::Tag,
    SearchKind::Collection,
    SearchKind::Conversation,
    SearchKind::Message,
    SearchKind::Workspace,
    SearchKind::Task,
    SearchKind::TaskMemory,
];

fn push_rows(
    rows: rusqlite::MappedRows<'_, impl FnMut(&rusqlite::Row<'_>) -> rusqlite::Result<SearchHit>>,
    hits: &mut Vec<SearchHit>,
) -> Result<(), StoreError> {
    for row in rows {
        hits.push(row?);
    }
    Ok(())
}

/// Converts untrusted user text into a safe FTS5 AND query. Each token is
/// quoted and treated as a prefix, so punctuation cannot become FTS5 operators.
pub(crate) fn fts_query(input: &str) -> Option<String> {
    let tokens: Vec<String> = input
        .split_whitespace()
        .filter_map(|token| {
            let cleaned = token.trim_matches(|ch: char| {
                !(ch.is_alphanumeric() || ch == '_' || ch == '-' || ch == '.')
            });
            if cleaned.is_empty() {
                None
            } else {
                Some(format!("\"{}\"*", cleaned.replace('"', "\"\"")))
            }
        })
        .collect();
    if tokens.is_empty() {
        None
    } else {
        Some(tokens.join(" AND "))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn escapes_fts_operators() {
        assert_eq!(
            fts_query("hello OR world"),
            Some("\"hello\"* AND \"OR\"* AND \"world\"*".into())
        );
        assert_eq!(fts_query("  ??? "), None);
        assert_eq!(fts_query("a-b"), Some("\"a-b\"*".into()));
    }
}
