//! SQLite-backed local storage for AMARE (Phase 1.6).
//!
//! Design summary:
//! - A small root database stores the profile registry/default flag.
//! - Each profile has its own database file under `profiles/<id>/amare.db`.
//! - Settings, bookmarks, history, sessions, secrets, Library, AI conversations,
//!   and Workspace/Task memories are isolated by file and by repository
//!   connection. This is stronger than filtering one shared table by a
//!   `profile_id` column.
//! - Repository APIs expose `amare_core` domain types, never SQL rows.

pub mod backup;
mod db;
mod error;
pub mod maintenance;
mod migrations;
mod models;
mod repositories;
pub mod search;

pub use backup::{
    BackupCredential, BackupData, BackupEncryption, BackupError, BackupFile, BackupFileKind,
    BackupManager, BackupManifest, BackupOptions, BackupPolicy, BackupProfile, BackupRecord,
    BackupScope, RestoreMode, RestoreOptions, RestoreReport,
};
pub use error::StoreError;
pub use maintenance::{IntegrityReport, check_database, check_store};
pub use repositories::bookmarks::FolderScope;
pub use search::SearchRepository;

use amare_core::ProfileId;
use db::{open_connection, profile_db_path, schema_version, sqlite_version, validate_profile_id};
use migrations::{PROFILE_MIGRATIONS, ROOT_MIGRATIONS, run_migrations};
use repositories::bookmarks::BookmarkRepository;
use repositories::conversations::ConversationRepository;
pub use repositories::downloads::DownloadRecord;
use repositories::downloads::DownloadRepository;
use repositories::history::HistoryRepository;
use repositories::library::{LibraryRepository, SourceRepository};
use repositories::profiles::ProfileRepository;
use repositories::secrets::SecretRepository;
use repositories::sessions::SessionRepository;
use repositories::settings::SettingsRepository;
use repositories::workspaces::{TaskMemoryRepository, TaskRepository, WorkspaceRepository};
use rusqlite::Connection;
use std::fs;
use std::path::{Path, PathBuf};

pub(crate) use db::now_ms;

/// Root store: profile registry and profile database lifecycle.
pub struct Store {
    root_dir: PathBuf,
    conn: Connection,
}

impl Store {
    pub fn open(root_dir: impl AsRef<Path>) -> Result<Self, StoreError> {
        let root_dir = root_dir.as_ref().to_path_buf();
        fs::create_dir_all(&root_dir)?;
        let conn = open_connection(&root_dir.join("amare.db"))?;
        run_migrations(&conn, ROOT_MIGRATIONS)?;
        Ok(Self { root_dir, conn })
    }

    pub fn root_dir(&self) -> &Path {
        &self.root_dir
    }

    pub fn schema_version(&self) -> Result<u32, StoreError> {
        schema_version(&self.conn)
    }

    pub fn sqlite_version(&self) -> String {
        sqlite_version()
    }

    pub(crate) fn root_connection(&self) -> &Connection {
        &self.conn
    }

    pub fn profiles(&self) -> ProfileRepository<'_> {
        ProfileRepository::new(&self.conn)
    }

    /// Create a profile and its isolated database.
    ///
    /// If profile database initialization fails, the profile registry row and
    /// the directory are cleaned up before the error is returned.
    pub fn create_profile(
        &self,
        id: &ProfileId,
        name: &str,
        is_default: bool,
    ) -> Result<amare_core::Profile, StoreError> {
        validate_profile_id(id.as_str())?;
        let tombstone = profile_tombstone_path(&self.root_dir, id.as_str());
        if tombstone.exists() {
            return Err(StoreError::ProfileDeleting(id.to_string()));
        }
        let profile = self.profiles().create(id, name, is_default)?;
        let path = profile_db_path(&self.root_dir, id.as_str());
        let profile_dir = path.parent().unwrap_or(&self.root_dir).to_path_buf();
        match ProfileStore::open_at(&path, id.clone()) {
            Ok(store) => {
                drop(store);
                Ok(profile)
            }
            Err(error) => {
                let _ = self.profiles().delete(id);
                let _ = fs::remove_dir_all(profile_dir);
                Err(error)
            }
        }
    }

    pub fn open_profile(&self, id: &ProfileId) -> Result<ProfileStore, StoreError> {
        validate_profile_id(id.as_str())?;
        if profile_tombstone_path(&self.root_dir, id.as_str()).exists() {
            return Err(StoreError::ProfileDeleting(id.to_string()));
        }
        let profile = self.profiles().get_required(id)?;
        validate_profile_id(profile.id.as_str())?;
        let path = profile_db_path(&self.root_dir, profile.id.as_str());
        ProfileStore::open_at(&path, profile.id)
    }

    pub fn delete_profile(&self, id: &ProfileId) -> Result<(), StoreError> {
        validate_profile_id(id.as_str())?;
        let dir = self.root_dir.join("profiles").join(id.as_str());
        let tombstone = profile_tombstone_path(&self.root_dir, id.as_str());
        let had_dir = dir.exists();
        if had_dir {
            if tombstone.exists() {
                fs::remove_dir_all(&tombstone)?;
            }
            // Move out of the active namespace first. If the registry delete
            // fails, restore it; if it succeeds, leftover tombstone data cannot
            // be opened as a normal profile.
            fs::rename(&dir, &tombstone)?;
        }
        if let Err(error) = self.profiles().delete(id) {
            if had_dir {
                let _ = fs::rename(&tombstone, &dir);
            }
            return Err(error);
        }
        if tombstone.exists() {
            fs::remove_dir_all(&tombstone)?;
        }
        Ok(())
    }

    pub fn profile_db_path(&self, id: &ProfileId) -> Result<PathBuf, StoreError> {
        validate_profile_id(id.as_str())?;
        Ok(profile_db_path(&self.root_dir, id.as_str()))
    }

    pub fn is_profile_deleting(&self, id: &ProfileId) -> bool {
        validate_profile_id(id.as_str()).is_ok()
            && profile_tombstone_path(&self.root_dir, id.as_str()).exists()
    }
}

/// A single profile database. Each instance owns one SQLite connection.
pub struct ProfileStore {
    profile_id: ProfileId,
    path: PathBuf,
    conn: Connection,
}

impl ProfileStore {
    /// Open a profile database at an explicit path.
    ///
    /// Public so tests and platform adapters can create independent connections
    /// for concurrency checks. Prefer `Store::open_profile` in application code.
    pub fn open_at(path: &Path, profile_id: ProfileId) -> Result<Self, StoreError> {
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent)?;
        }
        let conn = open_connection(path)?;
        run_migrations(&conn, PROFILE_MIGRATIONS)?;
        Ok(Self {
            profile_id,
            path: path.to_path_buf(),
            conn,
        })
    }

    pub fn profile_id(&self) -> &ProfileId {
        &self.profile_id
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn schema_version(&self) -> Result<u32, StoreError> {
        schema_version(&self.conn)
    }

    pub fn sqlite_version(&self) -> String {
        sqlite_version()
    }

    /// Clears user data in this profile while preserving the profile registry
    /// row, database file, and schema version. Runs in one transaction.
    pub fn reset_user_data(&self) -> Result<(), StoreError> {
        let tx = db::write_transaction(&self.conn)?;
        tx.execute_batch(
            "DELETE FROM message_sources;
             DELETE FROM message_parts;
             DELETE FROM messages;
             DELETE FROM conversations;
             DELETE FROM task_memory_links;
             DELETE FROM task_memories;
             DELETE FROM tasks;
             DELETE FROM workspaces;
             DELETE FROM library_item_collections;
             DELETE FROM library_item_tags;
             DELETE FROM source_references;
             DELETE FROM library_items;
             DELETE FROM tags;
             DELETE FROM collections;
             DELETE FROM session_tabs;
             DELETE FROM sessions;
             DELETE FROM history_visits;
             DELETE FROM bookmarks;
             DELETE FROM bookmark_folders;
             DELETE FROM secrets;
             DELETE FROM settings;",
        )?;
        tx.commit()?;
        Ok(())
    }

    pub fn settings(&self) -> SettingsRepository<'_> {
        SettingsRepository::new(&self.conn)
    }

    pub fn bookmarks(&self) -> BookmarkRepository<'_> {
        BookmarkRepository::new(&self.conn, &self.profile_id)
    }

    pub fn history(&self) -> HistoryRepository<'_> {
        HistoryRepository::new(&self.conn, &self.profile_id)
    }

    pub fn downloads(&self) -> DownloadRepository<'_> {
        DownloadRepository::new(&self.conn, &self.profile_id)
    }

    pub fn sessions(&self) -> SessionRepository<'_> {
        SessionRepository::new(&self.conn, &self.profile_id)
    }

    pub fn secrets(&self) -> SecretRepository<'_> {
        SecretRepository::new(&self.conn)
    }

    pub fn library(&self) -> LibraryRepository<'_> {
        LibraryRepository::new(&self.conn)
    }

    pub fn sources(&self) -> SourceRepository<'_> {
        SourceRepository::new(&self.conn)
    }

    pub fn conversations(&self) -> ConversationRepository<'_> {
        ConversationRepository::new(&self.conn)
    }

    pub fn workspaces(&self) -> WorkspaceRepository<'_> {
        WorkspaceRepository::new(&self.conn)
    }

    pub fn tasks(&self) -> TaskRepository<'_> {
        TaskRepository::new(&self.conn)
    }

    pub fn task_memories(&self) -> TaskMemoryRepository<'_> {
        TaskMemoryRepository::new(&self.conn)
    }

    pub fn search(&self) -> SearchRepository<'_> {
        SearchRepository::new(&self.conn)
    }

    pub(crate) fn connection(&self) -> &Connection {
        &self.conn
    }
}

fn profile_tombstone_path(root_dir: &Path, id: &str) -> PathBuf {
    root_dir.join("profiles").join(format!("{id}.deleting"))
}
