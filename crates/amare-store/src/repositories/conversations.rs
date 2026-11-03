use crate::db::{normalize_limit, now_ms, validate_text};
use crate::error::StoreError;
use amare_core::{
    Conversation, Message, MessagePart, MessagePartKind, MessageRole, MessageStatus,
    MessageVisibility, NewConversation, NewMessage, Page, SourceReference,
};
use rusqlite::{Connection, OptionalExtension, Row, params};

const MAX_MESSAGE_BODY: usize = 8 * 1024 * 1024;
const MAX_PART_TEXT: usize = 8 * 1024 * 1024;
const MAX_PARTS: usize = 512;

pub struct ConversationRepository<'a> {
    conn: &'a Connection,
}

impl<'a> ConversationRepository<'a> {
    pub(crate) fn new(conn: &'a Connection) -> Self {
        Self { conn }
    }

    pub fn create(&self, input: &NewConversation) -> Result<Conversation, StoreError> {
        validate_text("conversation title", &input.title, 1024)?;
        let now = now_ms();
        self.conn.execute(
            "INSERT INTO conversations
                (title, workspace_id, task_id, archived, deleted_at_ms,
                 created_at_ms, updated_at_ms, last_message_at_ms)
             VALUES (?1, ?2, ?3, 0, NULL, ?4, ?4, ?4)",
            params![input.title.trim(), input.workspace_id, input.task_id, now],
        )?;
        self.get_required(self.conn.last_insert_rowid())
    }

    pub fn get(&self, id: i64) -> Result<Option<Conversation>, StoreError> {
        Ok(self
            .conn
            .query_row(
                "SELECT id, title, workspace_id, task_id, archived, deleted_at_ms,
                        created_at_ms, updated_at_ms, last_message_at_ms
                 FROM conversations WHERE id = ?1",
                params![id],
                map_conversation,
            )
            .optional()?)
    }

    pub fn get_required(&self, id: i64) -> Result<Conversation, StoreError> {
        self.get(id)?
            .ok_or_else(|| StoreError::NotFound(format!("conversation {id}")))
    }

    pub fn list(
        &self,
        include_archived: bool,
        limit: u32,
        offset: u64,
    ) -> Result<Page<Conversation>, StoreError> {
        let limit = normalize_limit(limit);
        let archived_filter = if include_archived { 1 } else { 0 };
        let total: u64 = self.conn.query_row(
            "SELECT COUNT(*) FROM conversations
             WHERE deleted_at_ms IS NULL AND (?1 = 1 OR archived = 0)",
            params![archived_filter],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT id, title, workspace_id, task_id, archived, deleted_at_ms,
                    created_at_ms, updated_at_ms, last_message_at_ms
             FROM conversations
             WHERE deleted_at_ms IS NULL AND (?1 = 1 OR archived = 0)
             ORDER BY last_message_at_ms DESC, id DESC
             LIMIT ?2 OFFSET ?3",
        )?;
        let rows = stmt.query_map(params![archived_filter, limit, offset], map_conversation)?;
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

    pub fn update(
        &self,
        id: i64,
        title: &str,
        workspace_id: Option<i64>,
        task_id: Option<i64>,
    ) -> Result<Conversation, StoreError> {
        validate_text("conversation title", title, 1024)?;
        let changed = self.conn.execute(
            "UPDATE conversations
             SET title = ?1, workspace_id = ?2, task_id = ?3, updated_at_ms = ?4
             WHERE id = ?5 AND deleted_at_ms IS NULL",
            params![title.trim(), workspace_id, task_id, now_ms(), id],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("conversation {id}")));
        }
        self.get_required(id)
    }

    pub fn set_archived(&self, id: i64, archived: bool) -> Result<(), StoreError> {
        let changed = self.conn.execute(
            "UPDATE conversations SET archived = ?1, updated_at_ms = ?2
             WHERE id = ?3 AND deleted_at_ms IS NULL",
            params![archived, now_ms(), id],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("conversation {id}")));
        }
        Ok(())
    }

    /// Soft delete. The conversation and its messages remain recoverable.
    pub fn delete(&self, id: i64) -> Result<(), StoreError> {
        let changed = self.conn.execute(
            "UPDATE conversations SET deleted_at_ms = ?1, updated_at_ms = ?1
             WHERE id = ?2 AND deleted_at_ms IS NULL",
            params![now_ms(), id],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("conversation {id}")));
        }
        Ok(())
    }

    pub fn restore(&self, id: i64) -> Result<(), StoreError> {
        let changed = self.conn.execute(
            "UPDATE conversations SET deleted_at_ms = NULL, updated_at_ms = ?1 WHERE id = ?2",
            params![now_ms(), id],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("conversation {id}")));
        }
        Ok(())
    }

    /// Permanent delete. Messages, parts, and source links cascade.
    pub fn purge(&self, id: i64) -> Result<bool, StoreError> {
        Ok(self
            .conn
            .execute("DELETE FROM conversations WHERE id = ?1", params![id])?
            > 0)
    }

    pub fn add_message(
        &self,
        conversation_id: i64,
        input: &NewMessage,
    ) -> Result<Message, StoreError> {
        validate_message(input)?;
        let tx = crate::db::write_transaction(self.conn)?;
        let deleted: Option<Option<i64>> = tx
            .query_row(
                "SELECT deleted_at_ms FROM conversations WHERE id = ?1",
                params![conversation_id],
                |row| row.get(0),
            )
            .optional()?;
        match deleted {
            None => {
                return Err(StoreError::NotFound(format!(
                    "conversation {conversation_id}"
                )));
            }
            Some(Some(_)) => {
                return Err(StoreError::InvalidInput(
                    "cannot append to a deleted conversation".into(),
                ));
            }
            Some(None) => {}
        }
        let now = now_ms();
        let position: i64 = tx.query_row(
            "SELECT COALESCE(MAX(position), -1) + 1 FROM messages WHERE conversation_id = ?1",
            params![conversation_id],
            |row| row.get(0),
        )?;
        tx.execute(
            "INSERT INTO messages
                (conversation_id, role, body, status, visibility, position,
                 created_at_ms, updated_at_ms)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?7)",
            params![
                conversation_id,
                role_str(input.role),
                input.body,
                status_str(input.status),
                visibility_str(input.visibility),
                position,
                now,
            ],
        )?;
        let message_id = tx.last_insert_rowid();
        for (index, part) in input.parts.iter().enumerate() {
            tx.execute(
                "INSERT INTO message_parts
                    (message_id, kind, text, reference, metadata_json, position)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
                params![
                    message_id,
                    part_kind_str(part.kind),
                    part.text,
                    part.reference,
                    part.metadata_json,
                    index as i64,
                ],
            )?;
        }
        tx.execute(
            "UPDATE conversations
             SET updated_at_ms = ?1, last_message_at_ms = ?1,
                 title = CASE WHEN title = '' THEN ?2 ELSE title END
             WHERE id = ?3",
            params![now, make_title(&input.body), conversation_id],
        )?;
        let message = load_message(&tx, message_id)?;
        tx.commit()?;
        Ok(message)
    }

    pub fn get_message(&self, id: i64) -> Result<Option<Message>, StoreError> {
        let exists: Option<i64> = self
            .conn
            .query_row(
                "SELECT id FROM messages WHERE id = ?1",
                params![id],
                |row| row.get(0),
            )
            .optional()?;
        match exists {
            Some(_) => Ok(Some(load_message(self.conn, id)?)),
            None => Ok(None),
        }
    }

    pub fn list_messages(
        &self,
        conversation_id: i64,
        include_internal: bool,
        limit: u32,
        offset: u64,
    ) -> Result<Page<Message>, StoreError> {
        self.get_required(conversation_id)?;
        let limit = normalize_limit(limit);
        let visibility_filter = if include_internal { 1 } else { 0 };
        let total: u64 = self.conn.query_row(
            "SELECT COUNT(*) FROM messages
             WHERE conversation_id = ?1 AND (?2 = 1 OR visibility = 'user')",
            params![conversation_id, visibility_filter],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT id FROM messages
             WHERE conversation_id = ?1 AND (?2 = 1 OR visibility = 'user')
             ORDER BY position ASC, id ASC LIMIT ?3 OFFSET ?4",
        )?;
        let ids = stmt.query_map(
            params![conversation_id, visibility_filter, limit, offset],
            |row| row.get::<_, i64>(0),
        )?;
        let mut items = Vec::new();
        for id in ids {
            items.push(load_message(self.conn, id?)?);
        }
        Ok(Page {
            items,
            total,
            limit,
            offset,
        })
    }

    pub fn update_message(
        &self,
        id: i64,
        body: &str,
        status: MessageStatus,
    ) -> Result<Message, StoreError> {
        if body.len() > MAX_MESSAGE_BODY {
            return Err(StoreError::InvalidInput(format!(
                "message body must be <= {MAX_MESSAGE_BODY} bytes"
            )));
        }
        reject_private_key(body)?;
        let changed = self.conn.execute(
            "UPDATE messages SET body = ?1, status = ?2, updated_at_ms = ?3 WHERE id = ?4",
            params![body, status_str(status), now_ms(), id],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("message {id}")));
        }
        load_message(self.conn, id)
    }

    pub fn delete_message(&self, id: i64) -> Result<bool, StoreError> {
        Ok(self
            .conn
            .execute("DELETE FROM messages WHERE id = ?1", params![id])?
            > 0)
    }

    pub fn attach_source(
        &self,
        message_id: i64,
        source_reference_id: i64,
    ) -> Result<(), StoreError> {
        self.conn.execute(
            "INSERT OR IGNORE INTO message_sources(message_id, source_reference_id)
             VALUES (?1, ?2)",
            params![message_id, source_reference_id],
        )?;
        Ok(())
    }

    pub fn detach_source(
        &self,
        message_id: i64,
        source_reference_id: i64,
    ) -> Result<(), StoreError> {
        self.conn.execute(
            "DELETE FROM message_sources
             WHERE message_id = ?1 AND source_reference_id = ?2",
            params![message_id, source_reference_id],
        )?;
        Ok(())
    }

    pub fn sources_for_message(&self, message_id: i64) -> Result<Vec<SourceReference>, StoreError> {
        let mut stmt = self.conn.prepare(
            "SELECT s.id, s.url, s.page_title, s.site_name, s.quote_text,
                    s.location_info, s.collected_at_ms, s.library_item_id
             FROM source_references s
             JOIN message_sources ms ON ms.source_reference_id = s.id
             WHERE ms.message_id = ?1
             ORDER BY s.collected_at_ms DESC, s.id DESC",
        )?;
        let rows = stmt.query_map(params![message_id], map_source)?;
        let mut sources = Vec::new();
        for row in rows {
            sources.push(row?);
        }
        Ok(sources)
    }

    pub fn search(
        &self,
        query: &str,
        limit: u32,
        offset: u64,
    ) -> Result<Page<Conversation>, StoreError> {
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
            "SELECT COUNT(*) FROM conversation_fts
             JOIN conversations c ON c.id = conversation_fts.rowid
             WHERE conversation_fts MATCH ?1 AND c.deleted_at_ms IS NULL",
            params![match_query],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT c.id, c.title, c.workspace_id, c.task_id, c.archived,
                    c.deleted_at_ms, c.created_at_ms, c.updated_at_ms,
                    c.last_message_at_ms
             FROM conversation_fts
             JOIN conversations c ON c.id = conversation_fts.rowid
             WHERE conversation_fts MATCH ?1 AND c.deleted_at_ms IS NULL
             ORDER BY bm25(conversation_fts), c.last_message_at_ms DESC, c.id DESC
             LIMIT ?2 OFFSET ?3",
        )?;
        let rows = stmt.query_map(params![match_query, limit, offset], map_conversation)?;
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

fn validate_message(input: &NewMessage) -> Result<(), StoreError> {
    if input.body.len() > MAX_MESSAGE_BODY {
        return Err(StoreError::InvalidInput(format!(
            "message body must be <= {MAX_MESSAGE_BODY} bytes"
        )));
    }
    if input.parts.len() > MAX_PARTS {
        return Err(StoreError::InvalidInput(format!(
            "message must contain <= {MAX_PARTS} parts"
        )));
    }
    reject_private_key(&input.body)?;
    for part in &input.parts {
        if part.text.len() > MAX_PART_TEXT {
            return Err(StoreError::InvalidInput(format!(
                "message part text must be <= {MAX_PART_TEXT} bytes"
            )));
        }
        reject_private_key(&part.text)?;
    }
    Ok(())
}

fn reject_private_key(text: &str) -> Result<(), StoreError> {
    // Do not persist private key material. Other secret handling is enforced by
    // callers: the AI layer must use SecretRepository handles, never message text.
    if text.contains("-----BEGIN PRIVATE KEY-----")
        || text.contains("-----BEGIN RSA PRIVATE KEY-----")
        || text.contains("-----BEGIN OPENSSH PRIVATE KEY-----")
        || text.contains("-----BEGIN EC PRIVATE KEY-----")
    {
        return Err(StoreError::InvalidInput(
            "private key material must not be stored in messages".into(),
        ));
    }
    Ok(())
}

fn make_title(body: &str) -> String {
    let compact = body.split_whitespace().collect::<Vec<_>>().join(" ");
    let title: String = compact.chars().take(80).collect();
    if title.is_empty() {
        "New conversation".into()
    } else {
        title
    }
}

fn load_message(conn: &Connection, id: i64) -> Result<Message, StoreError> {
    let mut message = conn.query_row(
        "SELECT id, conversation_id, role, body, status, visibility, position,
                created_at_ms, updated_at_ms
         FROM messages WHERE id = ?1",
        params![id],
        map_message,
    )?;
    let mut stmt = conn.prepare(
        "SELECT id, message_id, kind, text, reference, metadata_json, position
         FROM message_parts WHERE message_id = ?1 ORDER BY position ASC, id ASC",
    )?;
    let rows = stmt.query_map(params![id], map_part)?;
    for row in rows {
        message.parts.push(row?);
    }
    Ok(message)
}

fn map_conversation(row: &Row<'_>) -> rusqlite::Result<Conversation> {
    Ok(Conversation {
        id: row.get(0)?,
        title: row.get(1)?,
        workspace_id: row.get(2)?,
        task_id: row.get(3)?,
        archived: row.get(4)?,
        deleted_at_ms: row.get(5)?,
        created_at_ms: row.get(6)?,
        updated_at_ms: row.get(7)?,
        last_message_at_ms: row.get(8)?,
    })
}

fn map_message(row: &Row<'_>) -> rusqlite::Result<Message> {
    let role: String = row.get(2)?;
    let status: String = row.get(4)?;
    let visibility: String = row.get(5)?;
    Ok(Message {
        id: row.get(0)?,
        conversation_id: row.get(1)?,
        role: parse_role(&role).map_err(crate::repositories::library::to_sql_error)?,
        body: row.get(3)?,
        status: parse_status(&status).map_err(crate::repositories::library::to_sql_error)?,
        visibility: parse_visibility(&visibility)
            .map_err(crate::repositories::library::to_sql_error)?,
        position: row.get(6)?,
        created_at_ms: row.get(7)?,
        updated_at_ms: row.get(8)?,
        parts: Vec::new(),
    })
}

fn map_part(row: &Row<'_>) -> rusqlite::Result<MessagePart> {
    let kind: String = row.get(2)?;
    Ok(MessagePart {
        id: row.get(0)?,
        message_id: row.get(1)?,
        kind: parse_part_kind(&kind).map_err(crate::repositories::library::to_sql_error)?,
        text: row.get(3)?,
        reference: row.get(4)?,
        metadata_json: row.get(5)?,
        position: row.get(6)?,
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

fn role_str(role: MessageRole) -> &'static str {
    match role {
        MessageRole::System => "system",
        MessageRole::User => "user",
        MessageRole::Assistant => "assistant",
        MessageRole::Tool => "tool",
    }
}

fn parse_role(value: &str) -> Result<MessageRole, String> {
    match value {
        "system" => Ok(MessageRole::System),
        "user" => Ok(MessageRole::User),
        "assistant" => Ok(MessageRole::Assistant),
        "tool" => Ok(MessageRole::Tool),
        _ => Err(format!("unknown message role: {value}")),
    }
}

fn status_str(status: MessageStatus) -> &'static str {
    match status {
        MessageStatus::Pending => "pending",
        MessageStatus::Complete => "complete",
        MessageStatus::Error => "error",
        MessageStatus::Cancelled => "cancelled",
    }
}

fn parse_status(value: &str) -> Result<MessageStatus, String> {
    match value {
        "pending" => Ok(MessageStatus::Pending),
        "complete" => Ok(MessageStatus::Complete),
        "error" => Ok(MessageStatus::Error),
        "cancelled" => Ok(MessageStatus::Cancelled),
        _ => Err(format!("unknown message status: {value}")),
    }
}

fn visibility_str(visibility: MessageVisibility) -> &'static str {
    match visibility {
        MessageVisibility::User => "user",
        MessageVisibility::Internal => "internal",
    }
}

fn parse_visibility(value: &str) -> Result<MessageVisibility, String> {
    match value {
        "user" => Ok(MessageVisibility::User),
        "internal" => Ok(MessageVisibility::Internal),
        _ => Err(format!("unknown message visibility: {value}")),
    }
}

fn part_kind_str(kind: MessagePartKind) -> &'static str {
    match kind {
        MessagePartKind::Text => "text",
        MessagePartKind::Code => "code",
        MessagePartKind::Image => "image",
        MessagePartKind::Source => "source",
        MessagePartKind::Citation => "citation",
        MessagePartKind::ToolResult => "tool_result",
        MessagePartKind::Error => "error",
        MessagePartKind::Attachment => "attachment",
    }
}

fn parse_part_kind(value: &str) -> Result<MessagePartKind, String> {
    match value {
        "text" => Ok(MessagePartKind::Text),
        "code" => Ok(MessagePartKind::Code),
        "image" => Ok(MessagePartKind::Image),
        "source" => Ok(MessagePartKind::Source),
        "citation" => Ok(MessagePartKind::Citation),
        "tool_result" => Ok(MessagePartKind::ToolResult),
        "error" => Ok(MessagePartKind::Error),
        "attachment" => Ok(MessagePartKind::Attachment),
        _ => Err(format!("unknown message part kind: {value}")),
    }
}
