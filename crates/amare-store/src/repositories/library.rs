use crate::db::{normalize_limit, now_ms, validate_text};
use crate::error::StoreError;
use amare_core::{
    Collection, LibraryItem, LibraryKind, LibraryStatus, LibraryUpdate, NewLibraryItem,
    NewSourceReference, Page, SourceReference, Tag,
};
use rusqlite::{Connection, OptionalExtension, Row, params};

const MAX_TEXT: usize = 4 * 1024 * 1024;

pub struct LibraryRepository<'a> {
    conn: &'a Connection,
}

impl<'a> LibraryRepository<'a> {
    pub(crate) fn new(conn: &'a Connection) -> Self {
        Self { conn }
    }

    pub fn create(&self, input: &NewLibraryItem) -> Result<LibraryItem, StoreError> {
        validate_library_input(
            &input.title,
            &input.description,
            &input.original_url,
            &input.source_name,
            &input.image_ref,
            &input.user_note,
        )?;
        let now = now_ms();
        self.conn.execute(
            "INSERT INTO library_items
                (title, description, kind, original_url, source_name, image_ref,
                 user_note, created_at_ms, updated_at_ms, last_accessed_at_ms,
                 favorite, status, sort_order)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?8, ?8, ?9, 'active', 0)",
            params![
                input.title.trim(),
                input.description,
                kind_str(input.kind),
                input.original_url,
                input.source_name,
                input.image_ref,
                input.user_note,
                now,
                input.favorite,
            ],
        )?;
        let id = self.conn.last_insert_rowid();
        self.get_required(id)
    }

    pub fn get(&self, id: i64) -> Result<Option<LibraryItem>, StoreError> {
        Ok(self
            .conn
            .query_row(
                "SELECT id, title, description, kind, original_url, source_name,
                        image_ref, user_note, created_at_ms, updated_at_ms,
                        last_accessed_at_ms, favorite, status, sort_order
                 FROM library_items WHERE id = ?1",
                params![id],
                map_library_item,
            )
            .optional()?)
    }

    pub fn get_required(&self, id: i64) -> Result<LibraryItem, StoreError> {
        self.get(id)?
            .ok_or_else(|| StoreError::NotFound(format!("library item {id}")))
    }

    pub fn list(&self, limit: u32, offset: u64) -> Result<Page<LibraryItem>, StoreError> {
        self.list_by_status(LibraryStatus::Active, limit, offset)
    }

    pub fn list_by_status(
        &self,
        status: LibraryStatus,
        limit: u32,
        offset: u64,
    ) -> Result<Page<LibraryItem>, StoreError> {
        let limit = normalize_limit(limit);
        let total: u64 = self.conn.query_row(
            "SELECT COUNT(*) FROM library_items WHERE status = ?1",
            params![status_str(status)],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT id, title, description, kind, original_url, source_name,
                    image_ref, user_note, created_at_ms, updated_at_ms,
                    last_accessed_at_ms, favorite, status, sort_order
             FROM library_items WHERE status = ?1
             ORDER BY favorite DESC, sort_order ASC, updated_at_ms DESC, id DESC
             LIMIT ?2 OFFSET ?3",
        )?;
        let rows = stmt.query_map(params![status_str(status), limit, offset], map_library_item)?;
        let mut items = Vec::new();
        for row in rows {
            items.push(row?);
        }
        Ok(Page {
            items,
            total,
            limit,
            offset,
        })
    }

    pub fn update(&self, id: i64, input: &LibraryUpdate) -> Result<LibraryItem, StoreError> {
        validate_library_input(
            &input.title,
            &input.description,
            &input.original_url,
            &input.source_name,
            &input.image_ref,
            &input.user_note,
        )?;
        let changed = self.conn.execute(
            "UPDATE library_items SET
                title = ?1, description = ?2, kind = ?3, original_url = ?4,
                source_name = ?5, image_ref = ?6, user_note = ?7,
                favorite = ?8, status = ?9, sort_order = ?10, updated_at_ms = ?11
             WHERE id = ?12",
            params![
                input.title.trim(),
                input.description,
                kind_str(input.kind),
                input.original_url,
                input.source_name,
                input.image_ref,
                input.user_note,
                input.favorite,
                status_str(input.status),
                input.sort_order,
                now_ms(),
                id,
            ],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("library item {id}")));
        }
        self.get_required(id)
    }

    pub fn set_status(&self, id: i64, status: LibraryStatus) -> Result<(), StoreError> {
        let changed = self.conn.execute(
            "UPDATE library_items SET status = ?1, updated_at_ms = ?2 WHERE id = ?3",
            params![status_str(status), now_ms(), id],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("library item {id}")));
        }
        Ok(())
    }

    pub fn touch(&self, id: i64) -> Result<(), StoreError> {
        let changed = self.conn.execute(
            "UPDATE library_items SET last_accessed_at_ms = ?1 WHERE id = ?2",
            params![now_ms(), id],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("library item {id}")));
        }
        Ok(())
    }

    /// Soft delete: moves to trash while preserving the item for undo.
    pub fn trash(&self, id: i64) -> Result<(), StoreError> {
        self.set_status(id, LibraryStatus::Trashed)
    }

    /// Permanent delete. Source references are preserved with their link cleared.
    pub fn purge(&self, id: i64) -> Result<bool, StoreError> {
        Ok(self
            .conn
            .execute("DELETE FROM library_items WHERE id = ?1", params![id])?
            > 0)
    }

    pub fn search(
        &self,
        query: &str,
        limit: u32,
        offset: u64,
    ) -> Result<Page<LibraryItem>, StoreError> {
        let limit = normalize_limit(limit);
        let Some(match_query) = crate::search::fts_query(query) else {
            return Ok(Page {
                items: Vec::new(),
                total: 0,
                limit,
                offset,
            });
        };
        let total: u64 = self.conn.query_row(
            "SELECT COUNT(*) FROM library_fts
             JOIN library_items i ON i.id = library_fts.rowid
             WHERE library_fts MATCH ?1 AND i.status != 'trashed'",
            params![match_query],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT i.id, i.title, i.description, i.kind, i.original_url,
                    i.source_name, i.image_ref, i.user_note, i.created_at_ms,
                    i.updated_at_ms, i.last_accessed_at_ms, i.favorite,
                    i.status, i.sort_order
             FROM library_fts
             JOIN library_items i ON i.id = library_fts.rowid
             WHERE library_fts MATCH ?1 AND i.status != 'trashed'
             ORDER BY bm25(library_fts), i.updated_at_ms DESC, i.id DESC
             LIMIT ?2 OFFSET ?3",
        )?;
        let rows = stmt.query_map(params![match_query, limit, offset], map_library_item)?;
        let mut items = Vec::new();
        for row in rows {
            items.push(row?);
        }
        Ok(Page {
            items,
            total,
            limit,
            offset,
        })
    }

    pub fn create_tag(&self, name: &str) -> Result<Tag, StoreError> {
        validate_text("tag name", name, 128)?;
        let name = normalize_tag_name(name);
        let name_norm = name.to_lowercase();
        let now = now_ms();
        self.conn.execute(
            "INSERT INTO tags(name, name_norm, created_at_ms) VALUES (?1, ?2, ?3)
             ON CONFLICT(name_norm) DO NOTHING",
            params![name, name_norm, now],
        )?;
        self.conn
            .query_row(
                "SELECT id, name, created_at_ms FROM tags WHERE name_norm = ?1",
                params![name_norm],
                |row| {
                    Ok(Tag {
                        id: row.get(0)?,
                        name: row.get(1)?,
                        created_at_ms: row.get(2)?,
                    })
                },
            )
            .map_err(StoreError::from)
    }

    pub fn list_tags(&self) -> Result<Vec<Tag>, StoreError> {
        let mut stmt = self
            .conn
            .prepare("SELECT id, name, created_at_ms FROM tags ORDER BY name COLLATE NOCASE, id")?;
        let rows = stmt.query_map([], |row| {
            Ok(Tag {
                id: row.get(0)?,
                name: row.get(1)?,
                created_at_ms: row.get(2)?,
            })
        })?;
        let mut tags = Vec::new();
        for row in rows {
            tags.push(row?);
        }
        Ok(tags)
    }

    pub fn delete_tag(&self, tag_id: i64) -> Result<bool, StoreError> {
        Ok(self
            .conn
            .execute("DELETE FROM tags WHERE id = ?1", params![tag_id])?
            > 0)
    }

    pub fn attach_tag(&self, item_id: i64, tag_id: i64) -> Result<(), StoreError> {
        self.conn.execute(
            "INSERT OR IGNORE INTO library_item_tags(item_id, tag_id) VALUES (?1, ?2)",
            params![item_id, tag_id],
        )?;
        Ok(())
    }

    pub fn detach_tag(&self, item_id: i64, tag_id: i64) -> Result<(), StoreError> {
        self.conn.execute(
            "DELETE FROM library_item_tags WHERE item_id = ?1 AND tag_id = ?2",
            params![item_id, tag_id],
        )?;
        Ok(())
    }

    pub fn tags_for_item(&self, item_id: i64) -> Result<Vec<Tag>, StoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT t.id, t.name, t.created_at_ms
             FROM tags t JOIN library_item_tags lit ON lit.tag_id = t.id
             WHERE lit.item_id = ?1 ORDER BY t.name COLLATE NOCASE, t.id",
        )?;
        let rows = stmt.query_map(params![item_id], |row| {
            Ok(Tag {
                id: row.get(0)?,
                name: row.get(1)?,
                created_at_ms: row.get(2)?,
            })
        })?;
        let mut tags = Vec::new();
        for row in rows {
            tags.push(row?);
        }
        Ok(tags)
    }

    pub fn create_collection(
        &self,
        name: &str,
        description: &str,
    ) -> Result<Collection, StoreError> {
        validate_text("collection name", name, 256)?;
        validate_optional_text("collection description", description, MAX_TEXT)?;
        let now = now_ms();
        self.conn.execute(
            "INSERT INTO collections(name, description, created_at_ms, updated_at_ms)
             VALUES (?1, ?2, ?3, ?3)",
            params![name.trim(), description, now],
        )?;
        self.get_collection_required(self.conn.last_insert_rowid())
    }

    pub fn get_collection(&self, id: i64) -> Result<Option<Collection>, StoreError> {
        Ok(self
            .conn
            .query_row(
                "SELECT id, name, description, created_at_ms, updated_at_ms
                 FROM collections WHERE id = ?1",
                params![id],
                map_collection,
            )
            .optional()?)
    }

    fn get_collection_required(&self, id: i64) -> Result<Collection, StoreError> {
        self.get_collection(id)?
            .ok_or_else(|| StoreError::NotFound(format!("collection {id}")))
    }

    pub fn list_collections(&self) -> Result<Vec<Collection>, StoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT id, name, description, created_at_ms, updated_at_ms
             FROM collections ORDER BY name COLLATE NOCASE, id",
        )?;
        let rows = stmt.query_map([], map_collection)?;
        let mut collections = Vec::new();
        for row in rows {
            collections.push(row?);
        }
        Ok(collections)
    }

    pub fn update_collection(
        &self,
        id: i64,
        name: &str,
        description: &str,
    ) -> Result<Collection, StoreError> {
        validate_text("collection name", name, 256)?;
        validate_optional_text("collection description", description, MAX_TEXT)?;
        let changed = self.conn.execute(
            "UPDATE collections SET name = ?1, description = ?2, updated_at_ms = ?3 WHERE id = ?4",
            params![name.trim(), description, now_ms(), id],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("collection {id}")));
        }
        self.get_collection_required(id)
    }

    pub fn delete_collection(&self, id: i64) -> Result<bool, StoreError> {
        Ok(self
            .conn
            .execute("DELETE FROM collections WHERE id = ?1", params![id])?
            > 0)
    }

    pub fn add_to_collection(&self, item_id: i64, collection_id: i64) -> Result<(), StoreError> {
        self.conn.execute(
            "INSERT OR IGNORE INTO library_item_collections(item_id, collection_id)
             VALUES (?1, ?2)",
            params![item_id, collection_id],
        )?;
        Ok(())
    }

    pub fn remove_from_collection(
        &self,
        item_id: i64,
        collection_id: i64,
    ) -> Result<(), StoreError> {
        self.conn.execute(
            "DELETE FROM library_item_collections
             WHERE item_id = ?1 AND collection_id = ?2",
            params![item_id, collection_id],
        )?;
        Ok(())
    }

    pub fn collections_for_item(&self, item_id: i64) -> Result<Vec<Collection>, StoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT c.id, c.name, c.description, c.created_at_ms, c.updated_at_ms
             FROM collections c
             JOIN library_item_collections lic ON lic.collection_id = c.id
             WHERE lic.item_id = ?1 ORDER BY c.name COLLATE NOCASE, c.id",
        )?;
        let rows = stmt.query_map(params![item_id], map_collection)?;
        let mut collections = Vec::new();
        for row in rows {
            collections.push(row?);
        }
        Ok(collections)
    }

    pub fn items_in_collection(
        &self,
        collection_id: i64,
        limit: u32,
        offset: u64,
    ) -> Result<Page<LibraryItem>, StoreError> {
        let limit = normalize_limit(limit);
        let total: u64 = self.conn.query_row(
            "SELECT COUNT(*) FROM library_item_collections lic
             JOIN library_items i ON i.id = lic.item_id
             WHERE lic.collection_id = ?1 AND i.status != 'trashed'",
            params![collection_id],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT i.id, i.title, i.description, i.kind, i.original_url,
                    i.source_name, i.image_ref, i.user_note, i.created_at_ms,
                    i.updated_at_ms, i.last_accessed_at_ms, i.favorite,
                    i.status, i.sort_order
             FROM library_item_collections lic
             JOIN library_items i ON i.id = lic.item_id
             WHERE lic.collection_id = ?1 AND i.status != 'trashed'
             ORDER BY i.sort_order ASC, i.updated_at_ms DESC, i.id DESC
             LIMIT ?2 OFFSET ?3",
        )?;
        let rows = stmt.query_map(params![collection_id, limit, offset], map_library_item)?;
        let mut items = Vec::new();
        for row in rows {
            items.push(row?);
        }
        Ok(Page {
            items,
            total,
            limit,
            offset,
        })
    }
}

pub struct SourceRepository<'a> {
    conn: &'a Connection,
}

impl<'a> SourceRepository<'a> {
    pub(crate) fn new(conn: &'a Connection) -> Self {
        Self { conn }
    }

    pub fn create(&self, input: &NewSourceReference) -> Result<SourceReference, StoreError> {
        validate_text("source url", &input.url, 8192)?;
        validate_optional_text("source page title", &input.page_title, 4096)?;
        validate_optional_text("source site name", &input.site_name, 512)?;
        validate_optional_text("source quote", &input.quote_text, MAX_TEXT)?;
        validate_optional_text("source location", &input.location_info, 1024)?;
        let now = now_ms();
        self.conn.execute(
            "INSERT INTO source_references
                (url, page_title, site_name, quote_text, location_info,
                 collected_at_ms, library_item_id)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![
                input.url.trim(),
                input.page_title,
                input.site_name,
                input.quote_text,
                input.location_info,
                now,
                input.library_item_id,
            ],
        )?;
        self.get_required(self.conn.last_insert_rowid())
    }

    pub fn get(&self, id: i64) -> Result<Option<SourceReference>, StoreError> {
        Ok(self
            .conn
            .query_row(
                "SELECT id, url, page_title, site_name, quote_text, location_info,
                        collected_at_ms, library_item_id
                 FROM source_references WHERE id = ?1",
                params![id],
                map_source,
            )
            .optional()?)
    }

    pub fn get_required(&self, id: i64) -> Result<SourceReference, StoreError> {
        self.get(id)?
            .ok_or_else(|| StoreError::NotFound(format!("source reference {id}")))
    }

    pub fn list_for_library_item(
        &self,
        item_id: i64,
        limit: u32,
        offset: u64,
    ) -> Result<Page<SourceReference>, StoreError> {
        let limit = normalize_limit(limit);
        let total: u64 = self.conn.query_row(
            "SELECT COUNT(*) FROM source_references WHERE library_item_id = ?1",
            params![item_id],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT id, url, page_title, site_name, quote_text, location_info,
                    collected_at_ms, library_item_id
             FROM source_references WHERE library_item_id = ?1
             ORDER BY collected_at_ms DESC, id DESC LIMIT ?2 OFFSET ?3",
        )?;
        let rows = stmt.query_map(params![item_id, limit, offset], map_source)?;
        let mut items = Vec::new();
        for row in rows {
            items.push(row?);
        }
        Ok(Page {
            items,
            total,
            limit,
            offset,
        })
    }

    pub fn delete(&self, id: i64) -> Result<bool, StoreError> {
        Ok(self
            .conn
            .execute("DELETE FROM source_references WHERE id = ?1", params![id])?
            > 0)
    }
}

fn validate_library_input(
    title: &str,
    description: &str,
    original_url: &Option<String>,
    source_name: &Option<String>,
    image_ref: &Option<String>,
    user_note: &str,
) -> Result<(), StoreError> {
    validate_text("library title", title, 4096)?;
    validate_optional_text("library description", description, MAX_TEXT)?;
    if let Some(url) = original_url {
        validate_optional_text("library original URL", url, 8192)?;
    }
    if let Some(source) = source_name {
        validate_optional_text("library source name", source, 512)?;
    }
    if let Some(image) = image_ref {
        validate_optional_text("library image reference", image, 8192)?;
    }
    validate_optional_text("library user note", user_note, MAX_TEXT)
}

fn validate_optional_text(field: &str, value: &str, max_len: usize) -> Result<(), StoreError> {
    if value.len() > max_len {
        return Err(StoreError::InvalidInput(format!(
            "{field} must be <= {max_len} bytes"
        )));
    }
    Ok(())
}

fn normalize_tag_name(name: &str) -> String {
    name.split_whitespace().collect::<Vec<_>>().join(" ")
}

fn map_library_item(row: &Row<'_>) -> rusqlite::Result<LibraryItem> {
    let kind: String = row.get(3)?;
    let status: String = row.get(12)?;
    Ok(LibraryItem {
        id: row.get(0)?,
        title: row.get(1)?,
        description: row.get(2)?,
        kind: parse_library_kind(&kind).map_err(to_sql_error)?,
        original_url: row.get(4)?,
        source_name: row.get(5)?,
        image_ref: row.get(6)?,
        user_note: row.get(7)?,
        created_at_ms: row.get(8)?,
        updated_at_ms: row.get(9)?,
        last_accessed_at_ms: row.get(10)?,
        favorite: row.get(11)?,
        status: parse_library_status(&status).map_err(to_sql_error)?,
        sort_order: row.get(13)?,
    })
}

fn map_collection(row: &Row<'_>) -> rusqlite::Result<Collection> {
    Ok(Collection {
        id: row.get(0)?,
        name: row.get(1)?,
        description: row.get(2)?,
        created_at_ms: row.get(3)?,
        updated_at_ms: row.get(4)?,
    })
}

fn map_source(row: &Row<'_>) -> rusqlite::Result<SourceReference> {
    Ok(SourceReference {
        id: row.get(0)?,
        url: row.get(1)?,
        page_title: row.get(2)?,
        site_name: row.get(3)?,
        quote_text: row.get(4)?,
        location_info: row.get(5)?,
        collected_at_ms: row.get(6)?,
        library_item_id: row.get(7)?,
    })
}

fn kind_str(kind: LibraryKind) -> &'static str {
    match kind {
        LibraryKind::WebPage => "web_page",
        LibraryKind::Link => "link",
        LibraryKind::Excerpt => "excerpt",
        LibraryKind::Note => "note",
        LibraryKind::Image => "image",
        LibraryKind::Pdf => "pdf",
        LibraryKind::Document => "document",
        LibraryKind::Research => "research",
        LibraryKind::Summary => "summary",
        LibraryKind::Other => "other",
    }
}

fn parse_library_kind(value: &str) -> Result<LibraryKind, String> {
    match value {
        "web_page" => Ok(LibraryKind::WebPage),
        "link" => Ok(LibraryKind::Link),
        "excerpt" => Ok(LibraryKind::Excerpt),
        "note" => Ok(LibraryKind::Note),
        "image" => Ok(LibraryKind::Image),
        "pdf" => Ok(LibraryKind::Pdf),
        "document" => Ok(LibraryKind::Document),
        "research" => Ok(LibraryKind::Research),
        "summary" => Ok(LibraryKind::Summary),
        "other" => Ok(LibraryKind::Other),
        _ => Err(format!("unknown library kind: {value}")),
    }
}

fn status_str(status: LibraryStatus) -> &'static str {
    match status {
        LibraryStatus::Active => "active",
        LibraryStatus::Archived => "archived",
        LibraryStatus::Trashed => "trashed",
    }
}

fn parse_library_status(value: &str) -> Result<LibraryStatus, String> {
    match value {
        "active" => Ok(LibraryStatus::Active),
        "archived" => Ok(LibraryStatus::Archived),
        "trashed" => Ok(LibraryStatus::Trashed),
        _ => Err(format!("unknown library status: {value}")),
    }
}

pub(crate) fn to_sql_error(message: String) -> rusqlite::Error {
    rusqlite::Error::FromSqlConversionFailure(
        0,
        rusqlite::types::Type::Text,
        Box::new(std::io::Error::new(
            std::io::ErrorKind::InvalidData,
            message,
        )),
    )
}
