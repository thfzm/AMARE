use crate::error::StoreError;
use rusqlite::{Connection, Transaction, TransactionBehavior};

#[derive(Debug, Clone, Copy)]
pub(crate) struct Migration {
    pub version: u32,
    pub sql: &'static str,
}

pub(crate) fn run_migrations(
    conn: &Connection,
    migrations: &[Migration],
) -> Result<u32, StoreError> {
    conn.execute_batch(
        "CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER PRIMARY KEY,
            applied_at_ms INTEGER NOT NULL
        );",
    )?;

    let mut current: u32 = conn.query_row(
        "SELECT COALESCE(MAX(version), 0) FROM schema_migrations",
        [],
        |row| row.get(0),
    )?;

    for migration in migrations {
        if migration.version <= current {
            continue;
        }
        let tx = Transaction::new_unchecked(conn, TransactionBehavior::Immediate)?;
        if let Err(source) = tx.execute_batch(migration.sql) {
            return Err(StoreError::Migration {
                version: migration.version,
                source,
            });
        }
        let now = crate::now_ms();
        if let Err(source) = tx.execute(
            "INSERT INTO schema_migrations(version, applied_at_ms) VALUES (?1, ?2)",
            rusqlite::params![migration.version, now],
        ) {
            return Err(StoreError::Migration {
                version: migration.version,
                source,
            });
        }
        if let Err(source) = tx.pragma_update(None, "user_version", migration.version as i64) {
            return Err(StoreError::Migration {
                version: migration.version,
                source,
            });
        }
        tx.commit()?;
        current = migration.version;
    }

    Ok(current)
}

pub(crate) const ROOT_SCHEMA_VERSION: u32 = 2;
pub(crate) const PROFILE_SCHEMA_VERSION: u32 = 4;

pub(crate) const ROOT_MIGRATIONS: &[Migration] = &[
    Migration {
        version: 1,
        sql: r#"
            CREATE TABLE profiles (
                id             TEXT PRIMARY KEY,
                name           TEXT NOT NULL,
                is_default     INTEGER NOT NULL DEFAULT 0 CHECK (is_default IN (0, 1)),
                created_at_ms  INTEGER NOT NULL,
                updated_at_ms  INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX idx_profiles_single_default
                ON profiles(is_default) WHERE is_default = 1;
            CREATE INDEX idx_profiles_name ON profiles(name);
        "#,
    },
    Migration {
        version: 2,
        sql: r#"
            ALTER TABLE profiles ADD COLUMN icon TEXT NULL;
        "#,
    },
];

pub(crate) const PROFILE_MIGRATIONS: &[Migration] = &[
    Migration {
        version: 1,
        sql: r#"
            CREATE TABLE settings (
                key            TEXT PRIMARY KEY,
                group_name     TEXT NOT NULL DEFAULT 'general',
                value_type     TEXT NOT NULL CHECK (value_type IN ('text','integer','float','boolean','json')),
                value_text     TEXT NOT NULL,
                updated_at_ms  INTEGER NOT NULL
            );
            CREATE INDEX idx_settings_group ON settings(group_name, key);

            CREATE TABLE bookmark_folders (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                parent_id      INTEGER NULL REFERENCES bookmark_folders(id) ON DELETE CASCADE,
                name           TEXT NOT NULL,
                position       INTEGER NOT NULL,
                created_at_ms  INTEGER NOT NULL,
                updated_at_ms  INTEGER NOT NULL
            );
            CREATE INDEX idx_bookmark_folders_parent
                ON bookmark_folders(parent_id, position, id);

            CREATE TABLE bookmarks (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                title          TEXT NOT NULL,
                url            TEXT NOT NULL,
                folder_id      INTEGER NULL REFERENCES bookmark_folders(id) ON DELETE SET NULL,
                position       INTEGER NOT NULL,
                created_at_ms  INTEGER NOT NULL,
                updated_at_ms  INTEGER NOT NULL
            );
            CREATE INDEX idx_bookmarks_folder ON bookmarks(folder_id, position, id);
            CREATE INDEX idx_bookmarks_url ON bookmarks(url);
            CREATE INDEX idx_bookmarks_title ON bookmarks(title);

            CREATE TABLE history_visits (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                url            TEXT NOT NULL,
                title          TEXT NOT NULL,
                visited_at_ms  INTEGER NOT NULL
            );
            CREATE INDEX idx_history_url_visited
                ON history_visits(url, visited_at_ms DESC);
            CREATE INDEX idx_history_visited
                ON history_visits(visited_at_ms DESC);
        "#,
    },
    Migration {
        version: 2,
        sql: r#"
            CREATE TABLE sessions (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                name           TEXT NOT NULL,
                active_tab_id  INTEGER NULL,
                created_at_ms  INTEGER NOT NULL,
                updated_at_ms  INTEGER NOT NULL
            );
            CREATE INDEX idx_sessions_updated ON sessions(updated_at_ms DESC);

            CREATE TABLE session_tabs (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id          INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                url                 TEXT NOT NULL,
                title               TEXT NOT NULL,
                position            INTEGER NOT NULL,
                pinned              INTEGER NOT NULL DEFAULT 0 CHECK (pinned IN (0, 1)),
                group_id            TEXT NULL,
                last_accessed_at_ms INTEGER NOT NULL
            );
            CREATE INDEX idx_session_tabs_session
                ON session_tabs(session_id, position, id);

            CREATE TABLE secrets (
                key            TEXT PRIMARY KEY,
                group_name     TEXT NOT NULL DEFAULT 'general',
                ciphertext     BLOB NOT NULL,
                updated_at_ms  INTEGER NOT NULL
            );
            CREATE INDEX idx_secrets_group ON secrets(group_name, key);
        "#,
    },
    Migration {
        version: 3,
        sql: r#"
            CREATE TABLE library_items (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                title               TEXT NOT NULL,
                description         TEXT NOT NULL DEFAULT '',
                kind                TEXT NOT NULL CHECK (kind IN ('web_page','link','excerpt','note','image','pdf','document','research','summary','other')),
                original_url        TEXT NULL,
                source_name         TEXT NULL,
                image_ref           TEXT NULL,
                user_note           TEXT NOT NULL DEFAULT '',
                created_at_ms       INTEGER NOT NULL,
                updated_at_ms       INTEGER NOT NULL,
                last_accessed_at_ms INTEGER NOT NULL,
                favorite            INTEGER NOT NULL DEFAULT 0 CHECK (favorite IN (0, 1)),
                status              TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active','archived','trashed')),
                sort_order          INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX idx_library_items_status_updated
                ON library_items(status, updated_at_ms DESC, id DESC);
            CREATE INDEX idx_library_items_favorite
                ON library_items(favorite, updated_at_ms DESC, id DESC);
            CREATE INDEX idx_library_items_url ON library_items(original_url);

            CREATE TABLE tags (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                name          TEXT NOT NULL,
                name_norm     TEXT NOT NULL UNIQUE,
                created_at_ms INTEGER NOT NULL
            );

            CREATE TABLE library_item_tags (
                item_id INTEGER NOT NULL REFERENCES library_items(id) ON DELETE CASCADE,
                tag_id  INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                PRIMARY KEY (item_id, tag_id)
            );
            CREATE INDEX idx_library_item_tags_tag ON library_item_tags(tag_id, item_id);

            CREATE TABLE collections (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                name          TEXT NOT NULL,
                description   TEXT NOT NULL DEFAULT '',
                created_at_ms INTEGER NOT NULL,
                updated_at_ms INTEGER NOT NULL
            );
            CREATE INDEX idx_collections_name ON collections(name);

            CREATE TABLE library_item_collections (
                item_id       INTEGER NOT NULL REFERENCES library_items(id) ON DELETE CASCADE,
                collection_id INTEGER NOT NULL REFERENCES collections(id) ON DELETE CASCADE,
                PRIMARY KEY (item_id, collection_id)
            );
            CREATE INDEX idx_library_item_collections_collection
                ON library_item_collections(collection_id, item_id);

            CREATE TABLE source_references (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                url             TEXT NOT NULL,
                page_title      TEXT NOT NULL DEFAULT '',
                site_name       TEXT NOT NULL DEFAULT '',
                quote_text      TEXT NOT NULL DEFAULT '',
                location_info   TEXT NOT NULL DEFAULT '',
                collected_at_ms INTEGER NOT NULL,
                library_item_id INTEGER NULL REFERENCES library_items(id) ON DELETE SET NULL
            );
            CREATE INDEX idx_source_references_library_item
                ON source_references(library_item_id, collected_at_ms DESC, id DESC);
            CREATE INDEX idx_source_references_url ON source_references(url);

            CREATE TABLE workspaces (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                name          TEXT NOT NULL,
                description   TEXT NOT NULL DEFAULT '',
                icon          TEXT NULL,
                archived      INTEGER NOT NULL DEFAULT 0 CHECK (archived IN (0, 1)),
                created_at_ms INTEGER NOT NULL,
                updated_at_ms INTEGER NOT NULL
            );
            CREATE INDEX idx_workspaces_archived_updated
                ON workspaces(archived, updated_at_ms DESC, id DESC);

            CREATE TABLE tasks (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                workspace_id  INTEGER NOT NULL REFERENCES workspaces(id) ON DELETE CASCADE,
                title         TEXT NOT NULL,
                description   TEXT NOT NULL DEFAULT '',
                status        TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open','in_progress','done','archived')),
                created_at_ms INTEGER NOT NULL,
                updated_at_ms INTEGER NOT NULL
            );
            CREATE INDEX idx_tasks_workspace_status
                ON tasks(workspace_id, status, updated_at_ms DESC, id DESC);

            CREATE TABLE task_memories (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id             INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                title               TEXT NOT NULL,
                content             TEXT NOT NULL,
                memory_type         TEXT NOT NULL CHECK (memory_type IN ('research_result','decision','user_rule','fact','progress','open_question')),
                source_reference_id INTEGER NULL REFERENCES source_references(id) ON DELETE SET NULL,
                importance          INTEGER NOT NULL DEFAULT 0 CHECK (importance BETWEEN 0 AND 5),
                user_confirmed      INTEGER NOT NULL DEFAULT 0 CHECK (user_confirmed IN (0, 1)),
                created_at_ms       INTEGER NOT NULL,
                updated_at_ms       INTEGER NOT NULL
            );
            CREATE INDEX idx_task_memories_task_updated
                ON task_memories(task_id, updated_at_ms DESC, id DESC);

            CREATE TABLE task_memory_links (
                memory_id    INTEGER NOT NULL REFERENCES task_memories(id) ON DELETE CASCADE,
                target_task_id INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                created_at_ms INTEGER NOT NULL,
                PRIMARY KEY (memory_id, target_task_id)
            );
            CREATE INDEX idx_task_memory_links_target
                ON task_memory_links(target_task_id, memory_id);

            CREATE TABLE conversations (
                id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                title              TEXT NOT NULL,
                workspace_id       INTEGER NULL REFERENCES workspaces(id) ON DELETE SET NULL,
                task_id            INTEGER NULL REFERENCES tasks(id) ON DELETE SET NULL,
                archived           INTEGER NOT NULL DEFAULT 0 CHECK (archived IN (0, 1)),
                deleted_at_ms      INTEGER NULL,
                created_at_ms      INTEGER NOT NULL,
                updated_at_ms      INTEGER NOT NULL,
                last_message_at_ms INTEGER NOT NULL
            );
            CREATE INDEX idx_conversations_visible
                ON conversations(deleted_at_ms, archived, last_message_at_ms DESC, id DESC);
            CREATE INDEX idx_conversations_workspace ON conversations(workspace_id, id);
            CREATE INDEX idx_conversations_task ON conversations(task_id, id);

            CREATE TABLE messages (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_id INTEGER NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                role            TEXT NOT NULL CHECK (role IN ('system','user','assistant','tool')),
                body            TEXT NOT NULL DEFAULT '',
                status          TEXT NOT NULL DEFAULT 'complete' CHECK (status IN ('pending','complete','error','cancelled')),
                visibility      TEXT NOT NULL DEFAULT 'user' CHECK (visibility IN ('user','internal')),
                position        INTEGER NOT NULL,
                created_at_ms   INTEGER NOT NULL,
                updated_at_ms   INTEGER NOT NULL
            );
            CREATE INDEX idx_messages_conversation
                ON messages(conversation_id, position, id);
            CREATE INDEX idx_messages_visible
                ON messages(conversation_id, visibility, position, id);

            CREATE TABLE message_parts (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id    INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                kind          TEXT NOT NULL CHECK (kind IN ('text','code','image','source','citation','tool_result','error','attachment')),
                text          TEXT NOT NULL DEFAULT '',
                reference     TEXT NULL,
                metadata_json TEXT NULL,
                position      INTEGER NOT NULL
            );
            CREATE INDEX idx_message_parts_message
                ON message_parts(message_id, position, id);

            CREATE TABLE message_sources (
                message_id          INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                source_reference_id INTEGER NOT NULL REFERENCES source_references(id) ON DELETE CASCADE,
                PRIMARY KEY (message_id, source_reference_id)
            );
            CREATE INDEX idx_message_sources_source
                ON message_sources(source_reference_id, message_id);

            CREATE VIRTUAL TABLE library_fts USING fts5(
                title, description, user_note,
                content='library_items', content_rowid='id',
                tokenize='unicode61'
            );
            CREATE TRIGGER library_items_ai AFTER INSERT ON library_items BEGIN
                INSERT INTO library_fts(rowid, title, description, user_note)
                VALUES (new.id, new.title, new.description, new.user_note);
            END;
            CREATE TRIGGER library_items_ad AFTER DELETE ON library_items BEGIN
                INSERT INTO library_fts(library_fts, rowid, title, description, user_note)
                VALUES ('delete', old.id, old.title, old.description, old.user_note);
            END;
            CREATE TRIGGER library_items_au AFTER UPDATE ON library_items BEGIN
                INSERT INTO library_fts(library_fts, rowid, title, description, user_note)
                VALUES ('delete', old.id, old.title, old.description, old.user_note);
                INSERT INTO library_fts(rowid, title, description, user_note)
                VALUES (new.id, new.title, new.description, new.user_note);
            END;

            CREATE VIRTUAL TABLE tag_fts USING fts5(
                name,
                content='tags', content_rowid='id',
                tokenize='unicode61'
            );
            CREATE TRIGGER tags_ai AFTER INSERT ON tags BEGIN
                INSERT INTO tag_fts(rowid, name) VALUES (new.id, new.name);
            END;
            CREATE TRIGGER tags_ad AFTER DELETE ON tags BEGIN
                INSERT INTO tag_fts(tag_fts, rowid, name) VALUES ('delete', old.id, old.name);
            END;
            CREATE TRIGGER tags_au AFTER UPDATE ON tags BEGIN
                INSERT INTO tag_fts(tag_fts, rowid, name) VALUES ('delete', old.id, old.name);
                INSERT INTO tag_fts(rowid, name) VALUES (new.id, new.name);
            END;

            CREATE VIRTUAL TABLE collection_fts USING fts5(
                name, description,
                content='collections', content_rowid='id',
                tokenize='unicode61'
            );
            CREATE TRIGGER collections_ai AFTER INSERT ON collections BEGIN
                INSERT INTO collection_fts(rowid, name, description)
                VALUES (new.id, new.name, new.description);
            END;
            CREATE TRIGGER collections_ad AFTER DELETE ON collections BEGIN
                INSERT INTO collection_fts(collection_fts, rowid, name, description)
                VALUES ('delete', old.id, old.name, old.description);
            END;
            CREATE TRIGGER collections_au AFTER UPDATE ON collections BEGIN
                INSERT INTO collection_fts(collection_fts, rowid, name, description)
                VALUES ('delete', old.id, old.name, old.description);
                INSERT INTO collection_fts(rowid, name, description)
                VALUES (new.id, new.name, new.description);
            END;

            CREATE VIRTUAL TABLE conversation_fts USING fts5(
                title,
                content='conversations', content_rowid='id',
                tokenize='unicode61'
            );
            CREATE TRIGGER conversations_ai AFTER INSERT ON conversations BEGIN
                INSERT INTO conversation_fts(rowid, title) VALUES (new.id, new.title);
            END;
            CREATE TRIGGER conversations_ad AFTER DELETE ON conversations BEGIN
                INSERT INTO conversation_fts(conversation_fts, rowid, title)
                VALUES ('delete', old.id, old.title);
            END;
            CREATE TRIGGER conversations_au AFTER UPDATE ON conversations BEGIN
                INSERT INTO conversation_fts(conversation_fts, rowid, title)
                VALUES ('delete', old.id, old.title);
                INSERT INTO conversation_fts(rowid, title) VALUES (new.id, new.title);
            END;

            CREATE VIRTUAL TABLE message_fts USING fts5(
                body,
                content='messages', content_rowid='id',
                tokenize='unicode61'
            );
            CREATE TRIGGER messages_ai AFTER INSERT ON messages BEGIN
                INSERT INTO message_fts(rowid, body) VALUES (new.id, new.body);
            END;
            CREATE TRIGGER messages_ad AFTER DELETE ON messages BEGIN
                INSERT INTO message_fts(message_fts, rowid, body) VALUES ('delete', old.id, old.body);
            END;
            CREATE TRIGGER messages_au AFTER UPDATE ON messages BEGIN
                INSERT INTO message_fts(message_fts, rowid, body) VALUES ('delete', old.id, old.body);
                INSERT INTO message_fts(rowid, body) VALUES (new.id, new.body);
            END;

            CREATE VIRTUAL TABLE workspace_fts USING fts5(
                name, description,
                content='workspaces', content_rowid='id',
                tokenize='unicode61'
            );
            CREATE TRIGGER workspaces_ai AFTER INSERT ON workspaces BEGIN
                INSERT INTO workspace_fts(rowid, name, description)
                VALUES (new.id, new.name, new.description);
            END;
            CREATE TRIGGER workspaces_ad AFTER DELETE ON workspaces BEGIN
                INSERT INTO workspace_fts(workspace_fts, rowid, name, description)
                VALUES ('delete', old.id, old.name, old.description);
            END;
            CREATE TRIGGER workspaces_au AFTER UPDATE ON workspaces BEGIN
                INSERT INTO workspace_fts(workspace_fts, rowid, name, description)
                VALUES ('delete', old.id, old.name, old.description);
                INSERT INTO workspace_fts(rowid, name, description)
                VALUES (new.id, new.name, new.description);
            END;

            CREATE VIRTUAL TABLE task_fts USING fts5(
                title, description,
                content='tasks', content_rowid='id',
                tokenize='unicode61'
            );
            CREATE TRIGGER tasks_ai AFTER INSERT ON tasks BEGIN
                INSERT INTO task_fts(rowid, title, description)
                VALUES (new.id, new.title, new.description);
            END;
            CREATE TRIGGER tasks_ad AFTER DELETE ON tasks BEGIN
                INSERT INTO task_fts(task_fts, rowid, title, description)
                VALUES ('delete', old.id, old.title, old.description);
            END;
            CREATE TRIGGER tasks_au AFTER UPDATE ON tasks BEGIN
                INSERT INTO task_fts(task_fts, rowid, title, description)
                VALUES ('delete', old.id, old.title, old.description);
                INSERT INTO task_fts(rowid, title, description)
                VALUES (new.id, new.title, new.description);
            END;

            CREATE VIRTUAL TABLE task_memory_fts USING fts5(
                title, content,
                content='task_memories', content_rowid='id',
                tokenize='unicode61'
            );
            CREATE TRIGGER task_memories_ai AFTER INSERT ON task_memories BEGIN
                INSERT INTO task_memory_fts(rowid, title, content)
                VALUES (new.id, new.title, new.content);
            END;
            CREATE TRIGGER task_memories_ad AFTER DELETE ON task_memories BEGIN
                INSERT INTO task_memory_fts(task_memory_fts, rowid, title, content)
                VALUES ('delete', old.id, old.title, old.content);
            END;
            CREATE TRIGGER task_memories_au AFTER UPDATE ON task_memories BEGIN
                INSERT INTO task_memory_fts(task_memory_fts, rowid, title, content)
                VALUES ('delete', old.id, old.title, old.content);
                INSERT INTO task_memory_fts(rowid, title, content)
                VALUES (new.id, new.title, new.content);
            END;
        "#,
    },
    Migration {
        version: 4,
        sql: r#"
            CREATE TABLE downloads (
                id             INTEGER PRIMARY KEY,
                filename       TEXT NOT NULL,
                url            TEXT NOT NULL,
                path           TEXT NOT NULL,
                mime_type      TEXT NOT NULL DEFAULT '',
                total_bytes    INTEGER NOT NULL DEFAULT 0,
                received_bytes INTEGER NOT NULL DEFAULT 0,
                state          TEXT NOT NULL,
                error          TEXT NOT NULL DEFAULT '',
                created_at_ms  INTEGER NOT NULL,
                updated_at_ms  INTEGER NOT NULL
            );
            CREATE INDEX idx_downloads_created ON downloads(created_at_ms DESC, id DESC);
        "#,
    },
];

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn applies_and_reapplies_migrations_idempotently() {
        let conn = Connection::open_in_memory().unwrap();
        let version = run_migrations(&conn, PROFILE_MIGRATIONS).unwrap();
        assert_eq!(version, 4);
        let again = run_migrations(&conn, PROFILE_MIGRATIONS).unwrap();
        assert_eq!(again, 4);
        let count: u32 = conn
            .query_row("SELECT COUNT(*) FROM schema_migrations", [], |row| {
                row.get(0)
            })
            .unwrap();
        assert_eq!(count, 4);
    }

    #[test]
    fn upgrades_a_v1_database_to_v3() {
        let conn = Connection::open_in_memory().unwrap();
        assert_eq!(run_migrations(&conn, &PROFILE_MIGRATIONS[..1]).unwrap(), 1);
        assert_eq!(run_migrations(&conn, PROFILE_MIGRATIONS).unwrap(), 4);
        for table in ["sessions", "library_items", "conversations", "workspaces"] {
            let count: u32 = conn
                .query_row(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?1",
                    [table],
                    |row| row.get(0),
                )
                .unwrap();
            assert_eq!(count, 1, "{table}");
        }
    }

    #[test]
    fn upgrades_a_v2_database_to_v3() {
        let conn = Connection::open_in_memory().unwrap();
        assert_eq!(run_migrations(&conn, &PROFILE_MIGRATIONS[..2]).unwrap(), 2);
        assert_eq!(run_migrations(&conn, PROFILE_MIGRATIONS).unwrap(), 4);
        let fts: u32 = conn
            .query_row(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='library_fts'",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(fts, 1);
    }

    #[test]
    fn upgrades_a_root_v1_database_with_profile_icons() {
        let conn = Connection::open_in_memory().unwrap();
        assert_eq!(run_migrations(&conn, &ROOT_MIGRATIONS[..1]).unwrap(), 1);
        conn.execute(
            "INSERT INTO profiles(id, name, is_default, created_at_ms, updated_at_ms)
             VALUES ('p1', 'Personal', 1, 1, 1)",
            [],
        )
        .unwrap();
        assert_eq!(run_migrations(&conn, ROOT_MIGRATIONS).unwrap(), 2);
        let icon: Option<String> = conn
            .query_row("SELECT icon FROM profiles WHERE id = 'p1'", [], |row| {
                row.get(0)
            })
            .unwrap();
        assert_eq!(icon, None);
    }

    #[test]
    fn failed_migration_rolls_back_and_keeps_version() {
        let conn = Connection::open_in_memory().unwrap();
        run_migrations(&conn, ROOT_MIGRATIONS).unwrap();

        let bad = [
            Migration {
                version: 3,
                sql: "CREATE TABLE one(id INTEGER);",
            },
            Migration {
                version: 4,
                sql: "CREATE TABLE should_rollback(id INTEGER); THIS IS NOT SQL;",
            },
        ];
        let error = run_migrations(&conn, &bad).unwrap_err();
        assert!(matches!(error, StoreError::Migration { version: 4, .. }));

        let version: u32 = conn
            .query_row(
                "SELECT COALESCE(MAX(version), 0) FROM schema_migrations",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(version, 3);
        let exists: u32 = conn
            .query_row(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='should_rollback'",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(exists, 0);
    }
}
