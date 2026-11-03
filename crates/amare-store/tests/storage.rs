use amare_core::{
    BookmarkUpdate, NewBookmark, NewHistoryVisit, NewSessionTab, ProfileId, SettingValue,
};
use amare_store::{FolderScope, ProfileStore, Store, StoreError};
use std::thread;
use tempfile::tempdir;

fn setup() -> (tempfile::TempDir, Store, ProfileId) {
    let dir = tempdir().unwrap();
    let store = Store::open(dir.path()).unwrap();
    let id = ProfileId::new("p1");
    store.create_profile(&id, "Personal", true).unwrap();
    (dir, store, id)
}

#[test]
fn creates_root_and_profile_dbs_and_reopens() {
    let (dir, store, id) = setup();
    assert_eq!(store.schema_version().unwrap(), 2);
    let path = store.profile_db_path(&id).unwrap();
    assert!(path.exists());
    assert!(dir.path().join("amare.db").exists());
    drop(store);

    let reopened = Store::open(dir.path()).unwrap();
    let profile = reopened.profiles().get_required(&id).unwrap();
    assert_eq!(profile.name, "Personal");
    assert!(profile.is_default);
    let profile_store = reopened.open_profile(&id).unwrap();
    assert_eq!(profile_store.schema_version().unwrap(), 3);
    assert_eq!(profile_store.profile_id(), &id);
    let raw = rusqlite::Connection::open(&path).unwrap();
    let journal: String = raw
        .query_row("PRAGMA journal_mode", [], |row| row.get(0))
        .unwrap();
    assert_eq!(journal.to_ascii_lowercase(), "wal");
}

#[test]
fn migrations_are_idempotent_and_settings_persist() {
    let (dir, store, id) = setup();
    {
        let profile = store.open_profile(&id).unwrap();
        profile
            .settings()
            .set("theme", "appearance", SettingValue::Text("dark".into()))
            .unwrap();
        assert_eq!(profile.schema_version().unwrap(), 3);
    }
    drop(store);

    let reopened = Store::open(dir.path()).unwrap();
    let profile = reopened.open_profile(&id).unwrap();
    assert_eq!(profile.schema_version().unwrap(), 3);
    let setting = profile.settings().get("theme").unwrap().unwrap();
    assert_eq!(setting.value, SettingValue::Text("dark".into()));
}

#[test]
fn settings_crud_type_preservation_and_search() {
    let (_dir, store, id) = setup();
    let profile = store.open_profile(&id).unwrap();
    let settings = profile.settings();

    settings
        .set("text", "general", SettingValue::Text("hello".into()))
        .unwrap();
    settings
        .set("integer", "general", SettingValue::Integer(42))
        .unwrap();
    settings
        .set("float", "general", SettingValue::Float(1.5))
        .unwrap();
    settings
        .set("bool", "general", SettingValue::Boolean(true))
        .unwrap();
    settings
        .set("json", "advanced", SettingValue::Json("{\"a\":1}".into()))
        .unwrap();

    assert_eq!(
        settings.get("integer").unwrap().unwrap().value,
        SettingValue::Integer(42)
    );
    assert_eq!(
        settings.get("float").unwrap().unwrap().value,
        SettingValue::Float(1.5)
    );
    assert_eq!(
        settings.get("bool").unwrap().unwrap().value,
        SettingValue::Boolean(true)
    );
    assert_eq!(
        settings.get("json").unwrap().unwrap().value,
        SettingValue::Json("{\"a\":1}".into())
    );

    let page = settings.list(Some("general"), 10, 0).unwrap();
    assert_eq!(page.total, 4);
    assert_eq!(page.items.len(), 4);

    let search = settings.search("hello", 10, 0).unwrap();
    assert_eq!(search.total, 1);
    assert_eq!(search.items[0].key, "text");

    assert!(settings.delete("text").unwrap());
    assert!(settings.get("text").unwrap().is_none());
    assert!(matches!(
        settings.set("bad", "general", SettingValue::Json("{".into())),
        Err(StoreError::InvalidInput(_))
    ));
}

#[test]
fn bookmarks_folders_crud_hierarchy_and_cycle_prevention() {
    let (_dir, store, id) = setup();
    let profile = store.open_profile(&id).unwrap();
    let repo = profile.bookmarks();

    let root = repo.create_folder("Root", None, None).unwrap();
    let child = repo.create_folder("Child", Some(root.id), None).unwrap();
    let bookmark = repo
        .create(NewBookmark {
            title: "Example".into(),
            url: "https://example.com".into(),
            folder_id: Some(child.id),
            position: None,
        })
        .unwrap();
    assert_eq!(bookmark.folder_id, Some(child.id));

    let page = repo.list(FolderScope::Folder(child.id), 10, 0).unwrap();
    assert_eq!(page.total, 1);
    assert_eq!(page.items[0].url, "https://example.com");

    let found = repo.search("example", 10, 0).unwrap();
    assert_eq!(found.total, 1);

    let updated = repo
        .update(
            bookmark.id,
            BookmarkUpdate {
                title: "Updated".into(),
                url: "https://example.com/updated".into(),
                folder_id: Some(root.id),
                position: 0,
            },
        )
        .unwrap();
    assert_eq!(updated.title, "Updated");

    let moved = repo
        .update_folder(child.id, "Child moved", Some(root.id), 1)
        .unwrap();
    assert_eq!(moved.parent_id, Some(root.id));

    assert!(matches!(
        repo.update_folder(root.id, "Root", Some(child.id), 0),
        Err(StoreError::InvalidInput(_))
    ));

    assert!(repo.delete_folder(root.id).unwrap());
    let orphaned = repo.get(bookmark.id).unwrap().unwrap();
    assert_eq!(orphaned.folder_id, None);
    assert!(repo.get_folder(child.id).unwrap().is_none());
}

#[test]
fn bookmark_foreign_key_and_pagination() {
    let (_dir, store, id) = setup();
    let profile = store.open_profile(&id).unwrap();
    let repo = profile.bookmarks();
    assert!(matches!(
        repo.create(NewBookmark {
            title: "Bad".into(),
            url: "https://bad.example".into(),
            folder_id: Some(9999),
            position: None,
        }),
        Err(StoreError::NotFound(_))
    ));

    let items: Vec<_> = (0..5)
        .map(|index| NewBookmark {
            title: format!("B{index}"),
            url: format!("https://example.com/{index}"),
            folder_id: None,
            position: None,
        })
        .collect();
    repo.create_many(&items).unwrap();
    let page = repo.list(FolderScope::Root, 2, 0).unwrap();
    assert_eq!(page.total, 5);
    assert_eq!(page.items.len(), 2);
    let page2 = repo.list(FolderScope::Root, 2, 2).unwrap();
    assert_eq!(page2.items.len(), 2);
}

#[test]
fn history_aggregates_visit_count_and_date_range() {
    let (_dir, store, id) = setup();
    let profile = store.open_profile(&id).unwrap();
    let repo = profile.history();
    repo.record_visits(&[
        NewHistoryVisit {
            url: "https://a.example".into(),
            title: "A1".into(),
            visited_at_ms: 1000,
        },
        NewHistoryVisit {
            url: "https://a.example".into(),
            title: "A2".into(),
            visited_at_ms: 1500,
        },
        NewHistoryVisit {
            url: "https://b.example".into(),
            title: "B".into(),
            visited_at_ms: 2000,
        },
    ])
    .unwrap();

    let page = repo.list(10, 0).unwrap();
    assert_eq!(page.total, 2);
    let a = page
        .items
        .iter()
        .find(|entry| entry.url == "https://a.example")
        .unwrap();
    assert_eq!(a.visit_count, 2);
    assert_eq!(a.first_visited_at_ms, 1000);
    assert_eq!(a.last_visited_at_ms, 1500);
    assert_eq!(a.title, "A2");

    let range = repo.search(None, Some(1200), Some(1800), 10, 0).unwrap();
    assert_eq!(range.total, 1);
    assert_eq!(range.items[0].url, "https://a.example");

    let query = repo.search(Some("b.example"), None, None, 10, 0).unwrap();
    assert_eq!(query.total, 1);
    assert_eq!(query.items[0].url, "https://b.example");

    assert_eq!(repo.delete_range(1000, 1500).unwrap(), 2);
    assert_eq!(repo.list(10, 0).unwrap().total, 1);
}

#[test]
fn sessions_save_restore_replace_and_rollback() {
    let (_dir, store, id) = setup();
    let profile = store.open_profile(&id).unwrap();
    let repo = profile.sessions();
    let tabs = vec![
        NewSessionTab {
            url: "https://a.example".into(),
            title: "A".into(),
            pinned: true,
            group_id: None,
            position: None,
        },
        NewSessionTab {
            url: "https://b.example".into(),
            title: "B".into(),
            pinned: false,
            group_id: Some("g1".into()),
            position: None,
        },
    ];
    let state = repo.create("Work", &tabs, Some(1)).unwrap();
    assert_eq!(state.tabs.len(), 2);
    assert_eq!(state.session.active_tab_id, Some(state.tabs[1].id));

    let restored = repo.restore(state.session.id).unwrap();
    assert_eq!(restored.tabs.len(), 2);
    assert_eq!(restored.session.name, "Work");

    let replacement = vec![NewSessionTab {
        url: "https://c.example".into(),
        title: "C".into(),
        pinned: false,
        group_id: None,
        position: None,
    }];
    let replaced = repo
        .replace_tabs(state.session.id, &replacement, Some(0))
        .unwrap();
    assert_eq!(replaced.tabs.len(), 1);
    assert_eq!(replaced.tabs[0].url, "https://c.example");
    assert_eq!(replaced.session.active_tab_id, Some(replaced.tabs[0].id));

    let before = repo.get_tabs(state.session.id).unwrap();
    assert!(matches!(
        repo.replace_tabs(state.session.id, &replacement, Some(99)),
        Err(StoreError::InvalidInput(_))
    ));
    let after = repo.get_tabs(state.session.id).unwrap();
    assert_eq!(before.len(), after.len());
    assert_eq!(before[0].url, after[0].url);

    assert!(repo.delete(state.session.id).unwrap());
    assert!(repo.get(state.session.id).unwrap().is_none());
}

#[test]
fn profiles_are_isolated_and_default_is_exclusive() {
    let dir = tempdir().unwrap();
    let store = Store::open(dir.path()).unwrap();
    let p1 = ProfileId::new("p1");
    let p2 = ProfileId::new("p2");
    store.create_profile(&p1, "Personal", true).unwrap();
    store.create_profile(&p2, "Work", true).unwrap();

    assert!(!store.profiles().get_required(&p1).unwrap().is_default);
    assert!(store.profiles().get_required(&p2).unwrap().is_default);
    assert_eq!(store.profiles().default_profile().unwrap().unwrap().id, p2);

    {
        let s1 = store.open_profile(&p1).unwrap();
        s1.settings()
            .set("only-p1", "general", SettingValue::Text("secret".into()))
            .unwrap();
        s1.bookmarks()
            .create(NewBookmark {
                title: "P1".into(),
                url: "https://p1.example".into(),
                folder_id: None,
                position: None,
            })
            .unwrap();
    }
    {
        let s2 = store.open_profile(&p2).unwrap();
        assert!(s2.settings().get("only-p1").unwrap().is_none());
        assert_eq!(
            s2.bookmarks().list(FolderScope::All, 10, 0).unwrap().total,
            0
        );
    }

    store.delete_profile(&p2).unwrap();
    assert!(store.open_profile(&p2).is_err());
    assert!(!dir.path().join("profiles").join("p2").exists());
    assert!(store.profiles().get_required(&p1).unwrap().is_default);
}

#[test]
fn invalid_inputs_and_sql_injection_are_safe() {
    let (_dir, store, id) = setup();
    let profile = store.open_profile(&id).unwrap();
    let repo = profile.bookmarks();
    assert!(matches!(
        repo.create(NewBookmark {
            title: "".into(),
            url: "https://x.example".into(),
            folder_id: None,
            position: None,
        }),
        Err(StoreError::InvalidInput(_))
    ));
    assert!(matches!(
        profile
            .settings()
            .set("", "general", SettingValue::Text("x".into())),
        Err(StoreError::InvalidInput(_))
    ));
    let malicious = "x'); DROP TABLE bookmarks; --";
    repo.create(NewBookmark {
        title: malicious.into(),
        url: "https://safe.example".into(),
        folder_id: None,
        position: None,
    })
    .unwrap();
    assert_eq!(repo.search("'; DROP TABLE", 10, 0).unwrap().total, 0);
    assert_eq!(repo.list(FolderScope::All, 10, 0).unwrap().total, 1);
}

#[test]
fn concurrent_writers_use_wal_and_busy_timeout() {
    let (_dir, store, id) = setup();
    drop(store.open_profile(&id).unwrap());
    let path = store.profile_db_path(&id).unwrap();
    let mut handles = Vec::new();
    for worker in 0..2 {
        let path = path.clone();
        let id = id.clone();
        handles.push(thread::spawn(move || {
            let profile = ProfileStore::open_at(&path, id).unwrap();
            for index in 0..50 {
                profile
                    .bookmarks()
                    .create(NewBookmark {
                        title: format!("w{worker}-{index}"),
                        url: format!("https://example.com/w{worker}/{index}"),
                        folder_id: None,
                        position: None,
                    })
                    .unwrap();
            }
        }));
    }
    for handle in handles {
        handle.join().unwrap();
    }
    let profile = store.open_profile(&id).unwrap();
    let page = profile.bookmarks().list(FolderScope::All, 500, 0).unwrap();
    assert_eq!(page.total, 100);
}

#[test]
fn encrypted_secret_round_trip_and_wrong_key() {
    let (dir, store, id) = setup();
    let path = store.profile_db_path(&id).unwrap();
    let key = [9u8; 32];
    {
        let profile = store.open_profile(&id).unwrap();
        profile
            .secrets()
            .set("api.key", "ai", b"super-secret", &key)
            .unwrap();
        assert_eq!(
            profile.secrets().get("api.key", &key).unwrap().unwrap(),
            b"super-secret"
        );
        assert_eq!(
            profile.secrets().list_keys(Some("ai")).unwrap(),
            vec!["api.key"]
        );
        assert!(matches!(
            profile.secrets().get("api.key", &[8u8; 32]),
            Err(StoreError::Security(_))
        ));
    }

    let raw = rusqlite::Connection::open(&path).unwrap();
    let ciphertext: Vec<u8> = raw
        .query_row(
            "SELECT ciphertext FROM secrets WHERE key = 'api.key'",
            [],
            |row| row.get(0),
        )
        .unwrap();
    assert_ne!(ciphertext, b"super-secret");
    drop(raw);

    let reopened = Store::open(dir.path()).unwrap();
    let profile = reopened.open_profile(&id).unwrap();
    assert_eq!(
        profile.secrets().get("api.key", &key).unwrap().unwrap(),
        b"super-secret"
    );
}

#[test]
fn rejects_unsafe_profile_ids() {
    let dir = tempdir().unwrap();
    let store = Store::open(dir.path()).unwrap();
    for bad in ["../escape", "a/b", ".", "..", "space id"] {
        let id = ProfileId::new(bad);
        assert!(matches!(
            store.create_profile(&id, "Bad", false),
            Err(StoreError::InvalidInput(_))
        ));
    }
}
