use crate::db::{normalize_limit, now_ms, validate_text};
use crate::error::StoreError;
use crate::models::{BookmarkRow, FolderRow};
use crate::repositories::settings::escape_like;
use amare_core::{Bookmark, BookmarkFolder, BookmarkUpdate, NewBookmark, Page, ProfileId};
use rusqlite::{Connection, OptionalExtension, Row, params};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FolderScope {
    All,
    Root,
    Folder(i64),
}

pub struct BookmarkRepository<'a> {
    conn: &'a Connection,
    profile_id: &'a ProfileId,
}

impl<'a> BookmarkRepository<'a> {
    pub(crate) fn new(conn: &'a Connection, profile_id: &'a ProfileId) -> Self {
        Self { conn, profile_id }
    }

    pub fn create(&self, bookmark: NewBookmark) -> Result<Bookmark, StoreError> {
        let tx = crate::db::write_transaction(self.conn)?;
        let id = insert_bookmark(&tx, &bookmark)?;
        let created = load_bookmark(&tx, self.profile_id, id)?;
        tx.commit()?;
        Ok(created)
    }

    pub fn create_many(&self, bookmarks: &[NewBookmark]) -> Result<Vec<Bookmark>, StoreError> {
        let tx = crate::db::write_transaction(self.conn)?;
        let mut ids = Vec::with_capacity(bookmarks.len());
        for bookmark in bookmarks {
            ids.push(insert_bookmark(&tx, bookmark)?);
        }
        let mut out = Vec::with_capacity(ids.len());
        for id in ids {
            out.push(load_bookmark(&tx, self.profile_id, id)?);
        }
        tx.commit()?;
        Ok(out)
    }

    pub fn get(&self, id: i64) -> Result<Option<Bookmark>, StoreError> {
        let row = self
            .conn
            .query_row(
                "SELECT id, title, url, folder_id, position, created_at_ms, updated_at_ms
                 FROM bookmarks WHERE id = ?1",
                params![id],
                map_bookmark,
            )
            .optional()?;
        Ok(row.map(|row| row.into_domain(self.profile_id)))
    }

    pub fn update(&self, id: i64, changes: BookmarkUpdate) -> Result<Bookmark, StoreError> {
        validate_text("bookmark title", &changes.title, 1024)?;
        validate_text("bookmark url", &changes.url, 8192)?;
        let changed = self.conn.execute(
            "UPDATE bookmarks
             SET title = ?1, url = ?2, folder_id = ?3, position = ?4, updated_at_ms = ?5
             WHERE id = ?6",
            params![
                changes.title,
                changes.url,
                changes.folder_id,
                changes.position,
                now_ms(),
                id
            ],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("bookmark {id}")));
        }
        self.get(id)?
            .ok_or_else(|| StoreError::NotFound(format!("bookmark {id}")))
    }

    pub fn delete(&self, id: i64) -> Result<bool, StoreError> {
        let changed = self
            .conn
            .execute("DELETE FROM bookmarks WHERE id = ?1", params![id])?;
        Ok(changed > 0)
    }

    pub fn list(
        &self,
        scope: FolderScope,
        limit: u32,
        offset: u64,
    ) -> Result<Page<Bookmark>, StoreError> {
        let limit = normalize_limit(limit);
        let (scope_kind, folder_id) = scope_params(scope);
        let total: u64 = self.conn.query_row(
            "SELECT COUNT(*) FROM bookmarks
             WHERE (?1 = 'all'
                OR (?1 = 'root' AND folder_id IS NULL)
                OR (?1 = 'folder' AND folder_id = ?2))",
            params![scope_kind, folder_id],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT id, title, url, folder_id, position, created_at_ms, updated_at_ms
             FROM bookmarks
             WHERE (?1 = 'all'
                OR (?1 = 'root' AND folder_id IS NULL)
                OR (?1 = 'folder' AND folder_id = ?2))
             ORDER BY COALESCE(folder_id, -1), position ASC, id ASC
             LIMIT ?3 OFFSET ?4",
        )?;
        let rows = stmt.query_map(params![scope_kind, folder_id, limit, offset], map_bookmark)?;
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

    pub fn search(
        &self,
        query: &str,
        limit: u32,
        offset: u64,
    ) -> Result<Page<Bookmark>, StoreError> {
        let limit = normalize_limit(limit);
        let pattern = format!("%{}%", escape_like(query));
        let total: u64 = self.conn.query_row(
            "SELECT COUNT(*) FROM bookmarks
             WHERE title LIKE ?1 ESCAPE '\\' OR url LIKE ?1 ESCAPE '\\'",
            params![pattern],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT id, title, url, folder_id, position, created_at_ms, updated_at_ms
             FROM bookmarks
             WHERE title LIKE ?1 ESCAPE '\\' OR url LIKE ?1 ESCAPE '\\'
             ORDER BY position ASC, id ASC
             LIMIT ?2 OFFSET ?3",
        )?;
        let rows = stmt.query_map(params![pattern, limit, offset], map_bookmark)?;
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

    pub fn create_folder(
        &self,
        name: &str,
        parent_id: Option<i64>,
        position: Option<i64>,
    ) -> Result<BookmarkFolder, StoreError> {
        validate_text("folder name", name, 256)?;
        let tx = crate::db::write_transaction(self.conn)?;
        if let Some(parent_id) = parent_id {
            ensure_folder_exists(&tx, parent_id)?;
        }
        let position = match position {
            Some(value) => value,
            None => next_folder_position(&tx, parent_id)?,
        };
        let now = now_ms();
        tx.execute(
            "INSERT INTO bookmark_folders(parent_id, name, position, created_at_ms, updated_at_ms)
             VALUES (?1, ?2, ?3, ?4, ?4)",
            params![parent_id, name, position, now],
        )?;
        let id = tx.last_insert_rowid();
        let folder = load_folder(&tx, self.profile_id, id)?;
        tx.commit()?;
        Ok(folder)
    }

    pub fn get_folder(&self, id: i64) -> Result<Option<BookmarkFolder>, StoreError> {
        let row = self
            .conn
            .query_row(
                "SELECT id, parent_id, name, position, created_at_ms, updated_at_ms
                 FROM bookmark_folders WHERE id = ?1",
                params![id],
                map_folder,
            )
            .optional()?;
        Ok(row.map(|row| row.into_domain(self.profile_id)))
    }

    pub fn list_folders(
        &self,
        scope: FolderScope,
        limit: u32,
        offset: u64,
    ) -> Result<Page<BookmarkFolder>, StoreError> {
        let limit = normalize_limit(limit);
        let (scope_kind, parent_id) = scope_params(scope);
        let total: u64 = self.conn.query_row(
            "SELECT COUNT(*) FROM bookmark_folders
             WHERE (?1 = 'all'
                OR (?1 = 'root' AND parent_id IS NULL)
                OR (?1 = 'folder' AND parent_id = ?2))",
            params![scope_kind, parent_id],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT id, parent_id, name, position, created_at_ms, updated_at_ms
             FROM bookmark_folders
             WHERE (?1 = 'all'
                OR (?1 = 'root' AND parent_id IS NULL)
                OR (?1 = 'folder' AND parent_id = ?2))
             ORDER BY COALESCE(parent_id, -1), position ASC, id ASC
             LIMIT ?3 OFFSET ?4",
        )?;
        let rows = stmt.query_map(params![scope_kind, parent_id, limit, offset], map_folder)?;
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

    pub fn update_folder(
        &self,
        id: i64,
        name: &str,
        parent_id: Option<i64>,
        position: i64,
    ) -> Result<BookmarkFolder, StoreError> {
        validate_text("folder name", name, 256)?;
        let tx = crate::db::write_transaction(self.conn)?;
        ensure_folder_exists(&tx, id)?;
        if parent_id == Some(id) {
            return Err(StoreError::InvalidInput(
                "folder cannot be its own parent".into(),
            ));
        }
        if let Some(parent_id) = parent_id {
            ensure_folder_exists(&tx, parent_id)?;
            if is_descendant(&tx, id, parent_id)? {
                return Err(StoreError::InvalidInput(
                    "folder move would create a cycle".into(),
                ));
            }
        }
        tx.execute(
            "UPDATE bookmark_folders
             SET name = ?1, parent_id = ?2, position = ?3, updated_at_ms = ?4
             WHERE id = ?5",
            params![name, parent_id, position, now_ms(), id],
        )?;
        let folder = load_folder(&tx, self.profile_id, id)?;
        tx.commit()?;
        Ok(folder)
    }

    pub fn delete_folder(&self, id: i64) -> Result<bool, StoreError> {
        let changed = self
            .conn
            .execute("DELETE FROM bookmark_folders WHERE id = ?1", params![id])?;
        Ok(changed > 0)
    }
}

fn insert_bookmark(conn: &Connection, bookmark: &NewBookmark) -> Result<i64, StoreError> {
    validate_text("bookmark title", &bookmark.title, 1024)?;
    validate_text("bookmark url", &bookmark.url, 8192)?;
    if let Some(folder_id) = bookmark.folder_id {
        ensure_folder_exists(conn, folder_id)?;
    }
    let position = match bookmark.position {
        Some(value) => value,
        None => next_bookmark_position(conn, bookmark.folder_id)?,
    };
    let now = now_ms();
    conn.execute(
        "INSERT INTO bookmarks(title, url, folder_id, position, created_at_ms, updated_at_ms)
         VALUES (?1, ?2, ?3, ?4, ?5, ?5)",
        params![
            bookmark.title,
            bookmark.url,
            bookmark.folder_id,
            position,
            now
        ],
    )?;
    Ok(conn.last_insert_rowid())
}

fn next_bookmark_position(conn: &Connection, folder_id: Option<i64>) -> Result<i64, StoreError> {
    let next: i64 = conn.query_row(
        "SELECT COALESCE(MAX(position), -1) + 1 FROM bookmarks
         WHERE (?1 IS NULL AND folder_id IS NULL) OR (?1 IS NOT NULL AND folder_id = ?1)",
        params![folder_id],
        |row| row.get(0),
    )?;
    Ok(next)
}

fn next_folder_position(conn: &Connection, parent_id: Option<i64>) -> Result<i64, StoreError> {
    let next: i64 = conn.query_row(
        "SELECT COALESCE(MAX(position), -1) + 1 FROM bookmark_folders
         WHERE (?1 IS NULL AND parent_id IS NULL) OR (?1 IS NOT NULL AND parent_id = ?1)",
        params![parent_id],
        |row| row.get(0),
    )?;
    Ok(next)
}

fn ensure_folder_exists(conn: &Connection, id: i64) -> Result<(), StoreError> {
    let exists: u32 = conn.query_row(
        "SELECT COUNT(*) FROM bookmark_folders WHERE id = ?1",
        params![id],
        |row| row.get(0),
    )?;
    if exists == 0 {
        return Err(StoreError::NotFound(format!("bookmark folder {id}")));
    }
    Ok(())
}

fn is_descendant(conn: &Connection, ancestor: i64, candidate: i64) -> Result<bool, StoreError> {
    let mut current = Some(candidate);
    while let Some(id) = current {
        if id == ancestor {
            return Ok(true);
        }
        current = conn
            .query_row(
                "SELECT parent_id FROM bookmark_folders WHERE id = ?1",
                params![id],
                |row| row.get(0),
            )
            .optional()?
            .flatten();
    }
    Ok(false)
}

fn scope_params(scope: FolderScope) -> (&'static str, Option<i64>) {
    match scope {
        FolderScope::All => ("all", None),
        FolderScope::Root => ("root", None),
        FolderScope::Folder(id) => ("folder", Some(id)),
    }
}

fn load_bookmark(
    conn: &Connection,
    profile_id: &ProfileId,
    id: i64,
) -> Result<Bookmark, StoreError> {
    let row = conn
        .query_row(
            "SELECT id, title, url, folder_id, position, created_at_ms, updated_at_ms
             FROM bookmarks WHERE id = ?1",
            params![id],
            map_bookmark,
        )
        .optional()?
        .ok_or_else(|| StoreError::NotFound(format!("bookmark {id}")))?;
    Ok(row.into_domain(profile_id))
}

fn load_folder(
    conn: &Connection,
    profile_id: &ProfileId,
    id: i64,
) -> Result<BookmarkFolder, StoreError> {
    let row = conn
        .query_row(
            "SELECT id, parent_id, name, position, created_at_ms, updated_at_ms
             FROM bookmark_folders WHERE id = ?1",
            params![id],
            map_folder,
        )
        .optional()?
        .ok_or_else(|| StoreError::NotFound(format!("bookmark folder {id}")))?;
    Ok(row.into_domain(profile_id))
}

fn map_bookmark(row: &Row<'_>) -> rusqlite::Result<BookmarkRow> {
    Ok(BookmarkRow {
        id: row.get(0)?,
        title: row.get(1)?,
        url: row.get(2)?,
        folder_id: row.get(3)?,
        position: row.get(4)?,
        created_at_ms: row.get(5)?,
        updated_at_ms: row.get(6)?,
    })
}

fn map_folder(row: &Row<'_>) -> rusqlite::Result<FolderRow> {
    Ok(FolderRow {
        id: row.get(0)?,
        parent_id: row.get(1)?,
        name: row.get(2)?,
        position: row.get(3)?,
        created_at_ms: row.get(4)?,
        updated_at_ms: row.get(5)?,
    })
}
