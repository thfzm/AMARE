use crate::db::{normalize_limit, now_ms, validate_text};
use crate::error::StoreError;
use amare_core::{
    MemoryType, NewTask, NewTaskMemory, NewWorkspace, Page, Task, TaskMemory, TaskStatus, Workspace,
};
use rusqlite::{Connection, OptionalExtension, Row, params};

const MAX_TEXT: usize = 4 * 1024 * 1024;

pub struct WorkspaceRepository<'a> {
    conn: &'a Connection,
}

impl<'a> WorkspaceRepository<'a> {
    pub(crate) fn new(conn: &'a Connection) -> Self {
        Self { conn }
    }

    pub fn create(&self, input: &NewWorkspace) -> Result<Workspace, StoreError> {
        validate_text("workspace name", &input.name, 512)?;
        validate_optional_text("workspace description", &input.description, MAX_TEXT)?;
        if let Some(icon) = &input.icon {
            validate_optional_text("workspace icon", icon, 1024)?;
        }
        let now = now_ms();
        self.conn.execute(
            "INSERT INTO workspaces(name, description, icon, archived, created_at_ms, updated_at_ms)
             VALUES (?1, ?2, ?3, 0, ?4, ?4)",
            params![input.name.trim(), input.description, input.icon, now],
        )?;
        self.get_required(self.conn.last_insert_rowid())
    }

    pub fn get(&self, id: i64) -> Result<Option<Workspace>, StoreError> {
        Ok(self
            .conn
            .query_row(
                "SELECT id, name, description, icon, archived, created_at_ms, updated_at_ms
                 FROM workspaces WHERE id = ?1",
                params![id],
                map_workspace,
            )
            .optional()?)
    }

    pub fn get_required(&self, id: i64) -> Result<Workspace, StoreError> {
        self.get(id)?
            .ok_or_else(|| StoreError::NotFound(format!("workspace {id}")))
    }

    pub fn list(
        &self,
        include_archived: bool,
        limit: u32,
        offset: u64,
    ) -> Result<Page<Workspace>, StoreError> {
        let limit = normalize_limit(limit);
        let archived_filter = if include_archived { 1 } else { 0 };
        let total: u64 = self.conn.query_row(
            "SELECT COUNT(*) FROM workspaces WHERE (?1 = 1 OR archived = 0)",
            params![archived_filter],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT id, name, description, icon, archived, created_at_ms, updated_at_ms
             FROM workspaces WHERE (?1 = 1 OR archived = 0)
             ORDER BY updated_at_ms DESC, id DESC LIMIT ?2 OFFSET ?3",
        )?;
        let rows = stmt.query_map(params![archived_filter, limit, offset], map_workspace)?;
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
        name: &str,
        description: &str,
        icon: Option<&str>,
    ) -> Result<Workspace, StoreError> {
        validate_text("workspace name", name, 512)?;
        validate_optional_text("workspace description", description, MAX_TEXT)?;
        if let Some(icon) = icon {
            validate_optional_text("workspace icon", icon, 1024)?;
        }
        let changed = self.conn.execute(
            "UPDATE workspaces SET name = ?1, description = ?2, icon = ?3, updated_at_ms = ?4
             WHERE id = ?5",
            params![name.trim(), description, icon, now_ms(), id],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("workspace {id}")));
        }
        self.get_required(id)
    }

    pub fn set_archived(&self, id: i64, archived: bool) -> Result<(), StoreError> {
        let changed = self.conn.execute(
            "UPDATE workspaces SET archived = ?1, updated_at_ms = ?2 WHERE id = ?3",
            params![archived, now_ms(), id],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("workspace {id}")));
        }
        Ok(())
    }

    /// Deletes the workspace, its tasks, and its task memories. Conversations
    /// linked to them have their workspace/task link cleared.
    pub fn delete(&self, id: i64) -> Result<bool, StoreError> {
        Ok(self
            .conn
            .execute("DELETE FROM workspaces WHERE id = ?1", params![id])?
            > 0)
    }

    pub fn search(
        &self,
        query: &str,
        limit: u32,
        offset: u64,
    ) -> Result<Page<Workspace>, StoreError> {
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
            "SELECT COUNT(*) FROM workspace_fts
             JOIN workspaces w ON w.id = workspace_fts.rowid
             WHERE workspace_fts MATCH ?1",
            params![match_query],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT w.id, w.name, w.description, w.icon, w.archived,
                    w.created_at_ms, w.updated_at_ms
             FROM workspace_fts
             JOIN workspaces w ON w.id = workspace_fts.rowid
             WHERE workspace_fts MATCH ?1
             ORDER BY bm25(workspace_fts), w.updated_at_ms DESC, w.id DESC
             LIMIT ?2 OFFSET ?3",
        )?;
        let rows = stmt.query_map(params![match_query, limit, offset], map_workspace)?;
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

pub struct TaskRepository<'a> {
    conn: &'a Connection,
}

impl<'a> TaskRepository<'a> {
    pub(crate) fn new(conn: &'a Connection) -> Self {
        Self { conn }
    }

    pub fn create(&self, input: &NewTask) -> Result<Task, StoreError> {
        validate_text("task title", &input.title, 2048)?;
        validate_optional_text("task description", &input.description, MAX_TEXT)?;
        let now = now_ms();
        self.conn.execute(
            "INSERT INTO tasks(workspace_id, title, description, status, created_at_ms, updated_at_ms)
             VALUES (?1, ?2, ?3, ?4, ?5, ?5)",
            params![
                input.workspace_id,
                input.title.trim(),
                input.description,
                task_status_str(input.status),
                now
            ],
        )?;
        self.get_required(self.conn.last_insert_rowid())
    }

    pub fn get(&self, id: i64) -> Result<Option<Task>, StoreError> {
        Ok(self
            .conn
            .query_row(
                "SELECT id, workspace_id, title, description, status, created_at_ms, updated_at_ms
                 FROM tasks WHERE id = ?1",
                params![id],
                map_task,
            )
            .optional()?)
    }

    pub fn get_required(&self, id: i64) -> Result<Task, StoreError> {
        self.get(id)?
            .ok_or_else(|| StoreError::NotFound(format!("task {id}")))
    }

    pub fn list(
        &self,
        workspace_id: i64,
        include_archived: bool,
        limit: u32,
        offset: u64,
    ) -> Result<Page<Task>, StoreError> {
        let limit = normalize_limit(limit);
        let archived_filter = if include_archived { 1 } else { 0 };
        let total: u64 = self.conn.query_row(
            "SELECT COUNT(*) FROM tasks
             WHERE workspace_id = ?1 AND (?2 = 1 OR status != 'archived')",
            params![workspace_id, archived_filter],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT id, workspace_id, title, description, status, created_at_ms, updated_at_ms
             FROM tasks WHERE workspace_id = ?1 AND (?2 = 1 OR status != 'archived')
             ORDER BY CASE status WHEN 'in_progress' THEN 0 WHEN 'open' THEN 1
                                  WHEN 'done' THEN 2 ELSE 3 END,
                      updated_at_ms DESC, id DESC
             LIMIT ?3 OFFSET ?4",
        )?;
        let rows = stmt.query_map(
            params![workspace_id, archived_filter, limit, offset],
            map_task,
        )?;
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
        description: &str,
        status: TaskStatus,
    ) -> Result<Task, StoreError> {
        validate_text("task title", title, 2048)?;
        validate_optional_text("task description", description, MAX_TEXT)?;
        let changed = self.conn.execute(
            "UPDATE tasks SET title = ?1, description = ?2, status = ?3, updated_at_ms = ?4
             WHERE id = ?5",
            params![
                title.trim(),
                description,
                task_status_str(status),
                now_ms(),
                id
            ],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("task {id}")));
        }
        self.get_required(id)
    }

    pub fn delete(&self, id: i64) -> Result<bool, StoreError> {
        Ok(self
            .conn
            .execute("DELETE FROM tasks WHERE id = ?1", params![id])?
            > 0)
    }

    pub fn search(&self, query: &str, limit: u32, offset: u64) -> Result<Page<Task>, StoreError> {
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
            "SELECT COUNT(*) FROM task_fts JOIN tasks t ON t.id = task_fts.rowid
             WHERE task_fts MATCH ?1",
            params![match_query],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT t.id, t.workspace_id, t.title, t.description, t.status,
                    t.created_at_ms, t.updated_at_ms
             FROM task_fts JOIN tasks t ON t.id = task_fts.rowid
             WHERE task_fts MATCH ?1
             ORDER BY bm25(task_fts), t.updated_at_ms DESC, t.id DESC
             LIMIT ?2 OFFSET ?3",
        )?;
        let rows = stmt.query_map(params![match_query, limit, offset], map_task)?;
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

pub struct TaskMemoryRepository<'a> {
    conn: &'a Connection,
}

impl<'a> TaskMemoryRepository<'a> {
    pub(crate) fn new(conn: &'a Connection) -> Self {
        Self { conn }
    }

    pub fn create(&self, input: &NewTaskMemory) -> Result<TaskMemory, StoreError> {
        validate_text("task memory title", &input.title, 1024)?;
        validate_text("task memory content", &input.content, MAX_TEXT)?;
        if !(0..=5).contains(&input.importance) {
            return Err(StoreError::InvalidInput(
                "task memory importance must be between 0 and 5".into(),
            ));
        }
        let now = now_ms();
        self.conn.execute(
            "INSERT INTO task_memories
                (task_id, title, content, memory_type, source_reference_id,
                 importance, user_confirmed, created_at_ms, updated_at_ms)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?8)",
            params![
                input.task_id,
                input.title.trim(),
                input.content,
                memory_type_str(input.memory_type),
                input.source_reference_id,
                input.importance,
                input.user_confirmed,
                now
            ],
        )?;
        self.get_required(self.conn.last_insert_rowid())
    }

    pub fn get(&self, id: i64) -> Result<Option<TaskMemory>, StoreError> {
        Ok(self
            .conn
            .query_row(
                "SELECT id, task_id, title, content, memory_type, source_reference_id,
                        importance, user_confirmed, created_at_ms, updated_at_ms
                 FROM task_memories WHERE id = ?1",
                params![id],
                map_memory,
            )
            .optional()?)
    }

    pub fn get_required(&self, id: i64) -> Result<TaskMemory, StoreError> {
        self.get(id)?
            .ok_or_else(|| StoreError::NotFound(format!("task memory {id}")))
    }

    /// Lists memories owned by a task, optionally including explicit shares
    /// from other tasks. Sharing is never implicit.
    pub fn list_for_task(
        &self,
        task_id: i64,
        include_shared: bool,
        limit: u32,
        offset: u64,
    ) -> Result<Page<TaskMemory>, StoreError> {
        let limit = normalize_limit(limit);
        let shared_filter = if include_shared { 1 } else { 0 };
        let total: u64 = self.conn.query_row(
            "SELECT COUNT(*) FROM task_memories m
             WHERE m.task_id = ?1
                OR (?2 = 1 AND EXISTS(
                    SELECT 1 FROM task_memory_links l
                    WHERE l.memory_id = m.id AND l.target_task_id = ?1))",
            params![task_id, shared_filter],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT m.id, m.task_id, m.title, m.content, m.memory_type,
                    m.source_reference_id, m.importance, m.user_confirmed,
                    m.created_at_ms, m.updated_at_ms
             FROM task_memories m
             WHERE m.task_id = ?1
                OR (?2 = 1 AND EXISTS(
                    SELECT 1 FROM task_memory_links l
                    WHERE l.memory_id = m.id AND l.target_task_id = ?1))
             ORDER BY m.updated_at_ms DESC, m.id DESC
             LIMIT ?3 OFFSET ?4",
        )?;
        let rows = stmt.query_map(params![task_id, shared_filter, limit, offset], map_memory)?;
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
        content: &str,
        memory_type: MemoryType,
        importance: i64,
        user_confirmed: bool,
    ) -> Result<TaskMemory, StoreError> {
        validate_text("task memory title", title, 1024)?;
        validate_text("task memory content", content, MAX_TEXT)?;
        if !(0..=5).contains(&importance) {
            return Err(StoreError::InvalidInput(
                "task memory importance must be between 0 and 5".into(),
            ));
        }
        let changed = self.conn.execute(
            "UPDATE task_memories
             SET title = ?1, content = ?2, memory_type = ?3, importance = ?4,
                 user_confirmed = ?5, updated_at_ms = ?6
             WHERE id = ?7",
            params![
                title.trim(),
                content,
                memory_type_str(memory_type),
                importance,
                user_confirmed,
                now_ms(),
                id
            ],
        )?;
        if changed == 0 {
            return Err(StoreError::NotFound(format!("task memory {id}")));
        }
        self.get_required(id)
    }

    pub fn delete(&self, id: i64) -> Result<bool, StoreError> {
        Ok(self
            .conn
            .execute("DELETE FROM task_memories WHERE id = ?1", params![id])?
            > 0)
    }

    /// Explicitly shares a memory with another task. Returns true when a new
    /// link was created, false when it already existed.
    pub fn share_with_task(&self, memory_id: i64, target_task_id: i64) -> Result<bool, StoreError> {
        let source_task: i64 = self
            .conn
            .query_row(
                "SELECT task_id FROM task_memories WHERE id = ?1",
                params![memory_id],
                |row| row.get(0),
            )
            .optional()?
            .ok_or_else(|| StoreError::NotFound(format!("task memory {memory_id}")))?;
        if source_task == target_task_id {
            return Err(StoreError::InvalidInput(
                "task memory is already owned by the target task".into(),
            ));
        }
        let changed = self.conn.execute(
            "INSERT OR IGNORE INTO task_memory_links(memory_id, target_task_id, created_at_ms)
             VALUES (?1, ?2, ?3)",
            params![memory_id, target_task_id, now_ms()],
        )?;
        Ok(changed > 0)
    }

    pub fn unshare_from_task(&self, memory_id: i64, target_task_id: i64) -> Result<(), StoreError> {
        self.conn.execute(
            "DELETE FROM task_memory_links WHERE memory_id = ?1 AND target_task_id = ?2",
            params![memory_id, target_task_id],
        )?;
        Ok(())
    }

    pub fn search(
        &self,
        query: &str,
        limit: u32,
        offset: u64,
    ) -> Result<Page<TaskMemory>, StoreError> {
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
            "SELECT COUNT(*) FROM task_memory_fts
             JOIN task_memories m ON m.id = task_memory_fts.rowid
             WHERE task_memory_fts MATCH ?1",
            params![match_query],
            |row| row.get(0),
        )?;
        let mut stmt = self.conn.prepare(
            "SELECT m.id, m.task_id, m.title, m.content, m.memory_type,
                    m.source_reference_id, m.importance, m.user_confirmed,
                    m.created_at_ms, m.updated_at_ms
             FROM task_memory_fts
             JOIN task_memories m ON m.id = task_memory_fts.rowid
             WHERE task_memory_fts MATCH ?1
             ORDER BY bm25(task_memory_fts), m.updated_at_ms DESC, m.id DESC
             LIMIT ?2 OFFSET ?3",
        )?;
        let rows = stmt.query_map(params![match_query, limit, offset], map_memory)?;
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

fn validate_optional_text(field: &str, value: &str, max_len: usize) -> Result<(), StoreError> {
    if value.len() > max_len {
        return Err(StoreError::InvalidInput(format!(
            "{field} must be <= {max_len} bytes"
        )));
    }
    Ok(())
}

fn map_workspace(row: &Row<'_>) -> rusqlite::Result<Workspace> {
    Ok(Workspace {
        id: row.get(0)?,
        name: row.get(1)?,
        description: row.get(2)?,
        icon: row.get(3)?,
        archived: row.get(4)?,
        created_at_ms: row.get(5)?,
        updated_at_ms: row.get(6)?,
    })
}

fn map_task(row: &Row<'_>) -> rusqlite::Result<Task> {
    let status: String = row.get(4)?;
    Ok(Task {
        id: row.get(0)?,
        workspace_id: row.get(1)?,
        title: row.get(2)?,
        description: row.get(3)?,
        status: parse_task_status(&status).map_err(crate::repositories::library::to_sql_error)?,
        created_at_ms: row.get(5)?,
        updated_at_ms: row.get(6)?,
    })
}

fn map_memory(row: &Row<'_>) -> rusqlite::Result<TaskMemory> {
    let memory_type: String = row.get(4)?;
    Ok(TaskMemory {
        id: row.get(0)?,
        task_id: row.get(1)?,
        title: row.get(2)?,
        content: row.get(3)?,
        memory_type: parse_memory_type(&memory_type)
            .map_err(crate::repositories::library::to_sql_error)?,
        source_reference_id: row.get(5)?,
        importance: row.get(6)?,
        user_confirmed: row.get(7)?,
        created_at_ms: row.get(8)?,
        updated_at_ms: row.get(9)?,
    })
}

fn task_status_str(status: TaskStatus) -> &'static str {
    match status {
        TaskStatus::Open => "open",
        TaskStatus::InProgress => "in_progress",
        TaskStatus::Done => "done",
        TaskStatus::Archived => "archived",
    }
}

fn parse_task_status(value: &str) -> Result<TaskStatus, String> {
    match value {
        "open" => Ok(TaskStatus::Open),
        "in_progress" => Ok(TaskStatus::InProgress),
        "done" => Ok(TaskStatus::Done),
        "archived" => Ok(TaskStatus::Archived),
        _ => Err(format!("unknown task status: {value}")),
    }
}

fn memory_type_str(memory_type: MemoryType) -> &'static str {
    match memory_type {
        MemoryType::ResearchResult => "research_result",
        MemoryType::Decision => "decision",
        MemoryType::UserRule => "user_rule",
        MemoryType::Fact => "fact",
        MemoryType::Progress => "progress",
        MemoryType::OpenQuestion => "open_question",
    }
}

fn parse_memory_type(value: &str) -> Result<MemoryType, String> {
    match value {
        "research_result" => Ok(MemoryType::ResearchResult),
        "decision" => Ok(MemoryType::Decision),
        "user_rule" => Ok(MemoryType::UserRule),
        "fact" => Ok(MemoryType::Fact),
        "progress" => Ok(MemoryType::Progress),
        "open_question" => Ok(MemoryType::OpenQuestion),
        _ => Err(format!("unknown memory type: {value}")),
    }
}
