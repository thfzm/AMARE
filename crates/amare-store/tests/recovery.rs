use amare_core::{NewBookmark, NewHistoryVisit, NewSessionTab, ProfileId, SettingValue};
use amare_store::{
    BackupCredential, BackupManager, BackupOptions, FolderScope, RestoreMode, RestoreOptions,
    Store, check_database,
};
use std::fs;
use tempfile::tempdir;

fn seed(root: &std::path::Path) -> (Store, ProfileId) {
    let store = Store::open(root).unwrap();
    let id = ProfileId::new("recovery");
    store.create_profile(&id, "Recovery", true).unwrap();
    {
        let profile = store.open_profile(&id).unwrap();
        profile
            .settings()
            .set("theme", "appearance", SettingValue::Text("dark".into()))
            .unwrap();
        profile
            .bookmarks()
            .create(NewBookmark {
                title: "Recovery bookmark".into(),
                url: "https://recovery.example".into(),
                folder_id: None,
                position: None,
            })
            .unwrap();
        profile
            .history()
            .record_visit(NewHistoryVisit {
                url: "https://recovery.example".into(),
                title: "Recovery history".into(),
                visited_at_ms: 1000,
            })
            .unwrap();
        profile
            .sessions()
            .create(
                "Recovery session",
                &[NewSessionTab {
                    url: "https://recovery.example".into(),
                    title: "Recovery tab".into(),
                    pinned: false,
                    group_id: None,
                    position: None,
                }],
                Some(0),
            )
            .unwrap();
    }
    (store, id)
}

#[test]
fn wal_checkpoint_and_reopen_preserve_user_data() {
    let dir = tempdir().unwrap();
    let (store, id) = seed(dir.path());
    let path = store.profile_db_path(&id).unwrap();
    assert!(path.exists());
    assert!(check_database(&path).ok);
    drop(store);

    let raw = rusqlite::Connection::open(&path).unwrap();
    let journal: String = raw
        .query_row("PRAGMA journal_mode", [], |row| row.get(0))
        .unwrap();
    assert_eq!(journal.to_ascii_lowercase(), "wal");
    let checkpoint: (i64, i64, i64) = raw
        .query_row("PRAGMA wal_checkpoint(TRUNCATE)", [], |row| {
            Ok((row.get(0)?, row.get(1)?, row.get(2)?))
        })
        .unwrap();
    assert_eq!(checkpoint.0, 0);
    drop(raw);

    let reopened = Store::open(dir.path()).unwrap();
    let profile = reopened.open_profile(&id).unwrap();
    assert_eq!(
        profile.settings().get("theme").unwrap().unwrap().value,
        SettingValue::Text("dark".into())
    );
    assert_eq!(
        profile
            .bookmarks()
            .list(FolderScope::All, 10, 0)
            .unwrap()
            .total,
        1
    );
    assert_eq!(profile.history().list(10, 0).unwrap().total, 1);
    assert_eq!(profile.sessions().list(10, 0).unwrap().total, 1);
}

#[test]
fn corrupt_copy_is_detected_and_preserved() {
    let dir = tempdir().unwrap();
    let (store, id) = seed(dir.path());
    let path = store.profile_db_path(&id).unwrap();
    let copy = path.with_file_name("corrupt-copy.db");
    fs::copy(&path, &copy).unwrap();
    fs::write(&copy, b"not a sqlite database").unwrap();
    let report = check_database(&copy);
    assert!(!report.ok);
    assert!(copy.exists());
    assert!(check_database(&path).ok);
}

#[test]
fn missing_db_is_restored_from_backup_without_touching_source() {
    let source = tempdir().unwrap();
    let target = tempdir().unwrap();
    let (store, id) = seed(source.path());
    let manager = BackupManager::new(source.path()).unwrap();
    let record = manager
        .create_backup(&store, &BackupOptions::default())
        .unwrap();
    let backup = manager.backup_dir().join(&record.file_name);
    let profile_path = store.profile_db_path(&id).unwrap();
    drop(store);
    fs::remove_file(&profile_path).unwrap();
    assert!(!profile_path.exists());

    manager
        .restore(
            &backup,
            target.path(),
            &RestoreOptions {
                credential: BackupCredential::None,
                mode: RestoreMode::Full,
            },
        )
        .unwrap();
    let restored = Store::open(target.path()).unwrap();
    let profile = restored.open_profile(&id).unwrap();
    assert_eq!(
        profile.settings().get("theme").unwrap().unwrap().value,
        SettingValue::Text("dark".into())
    );
    assert_eq!(
        profile
            .bookmarks()
            .list(FolderScope::All, 10, 0)
            .unwrap()
            .total,
        1
    );
    assert_eq!(profile.history().list(10, 0).unwrap().total, 1);
    assert_eq!(profile.sessions().list(10, 0).unwrap().total, 1);
}

#[test]
fn future_schema_copy_is_rejected_and_source_is_unchanged() {
    let dir = tempdir().unwrap();
    let copy_root = tempdir().unwrap();
    let (store, id) = seed(dir.path());
    let source_profile = store.profile_db_path(&id).unwrap();
    let copy_profile = copy_root
        .path()
        .join("profiles")
        .join(id.as_str())
        .join("amare.db");
    fs::create_dir_all(copy_profile.parent().unwrap()).unwrap();
    drop(store);
    fs::copy(
        dir.path().join("amare.db"),
        copy_root.path().join("amare.db"),
    )
    .unwrap();
    fs::copy(&source_profile, &copy_profile).unwrap();
    let raw = rusqlite::Connection::open(&copy_profile).unwrap();
    raw.execute(
        "INSERT INTO schema_migrations(version, applied_at_ms) VALUES (999, 0)",
        [],
    )
    .unwrap();
    drop(raw);
    assert!(
        amare_store::check_store(copy_root.path())
            .iter()
            .any(|report| !report.ok)
    );
    assert!(check_database(&source_profile).ok);
    let reopened = Store::open(dir.path()).unwrap();
    assert_eq!(
        reopened
            .open_profile(&id)
            .unwrap()
            .history()
            .list(10, 0)
            .unwrap()
            .total,
        1
    );
}
