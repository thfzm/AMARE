use amare_core::{NewSessionTab, NewSessionWindow, ProfileId};
use amare_store::{ProfileStore, Store};
use rusqlite::{Connection, params};
use std::sync::{Arc, Barrier};
use std::thread;
use tempfile::tempdir;

fn setup() -> (tempfile::TempDir, Store, ProfileId) {
    let dir = tempdir().unwrap();
    let store = Store::open(dir.path()).unwrap();
    let id = ProfileId::new("p1");
    store.create_profile(&id, "Personal", true).unwrap();
    (dir, store, id)
}

fn tabs(prefix: &str, count: usize) -> Vec<NewSessionTab> {
    (1..=count)
        .map(|index| NewSessionTab {
            url: format!("https://{prefix}{index}.example"),
            title: format!("{prefix}{index}"),
            pinned: index == 2,
            group_id: None,
            position: None,
        })
        .collect()
}

fn window(id: &str, ordinal: i64, tab_count: usize) -> NewSessionWindow {
    NewSessionWindow {
        window_id: id.into(),
        ordinal,
        active_position: if tab_count == 0 { None } else { Some(0) },
        bounds_x: Some(10.0),
        bounds_y: Some(20.0),
        bounds_width: Some(1280.0),
        bounds_height: Some(720.0),
        maximized: false,
        tabs: tabs(id, tab_count),
    }
}

#[test]
fn two_windows_save_and_restore_independently() {
    let (dir, store, id) = setup();
    let session_id;
    {
        let profile = store.open_profile(&id).unwrap();
        let repo = profile.sessions();
        let a = repo
            .save_window(None, "AMARE session", &window("a", 0, 3))
            .unwrap();
        session_id = a.session.id;
        let b = repo
            .save_window(Some(session_id), "AMARE session", &window("b", 1, 2))
            .unwrap();
        assert_eq!(b.windows.len(), 2);
        assert_eq!(b.windows[0].window_id, "a");
        assert_eq!(b.windows[0].tabs.len(), 3);
        assert_eq!(b.windows[1].window_id, "b");
        assert_eq!(b.windows[1].tabs.len(), 2);
        assert_eq!(repo.list(10, 0).unwrap().total, 1);
    }

    let reopened = Store::open(dir.path()).unwrap();
    let profile = reopened.open_profile(&id).unwrap();
    let latest = profile.sessions().latest_windows().unwrap().unwrap();
    assert_eq!(latest.session.id, session_id);
    assert_eq!(latest.windows.len(), 2);
    assert_eq!(latest.windows[0].tabs.len(), 3);
    assert_eq!(latest.windows[1].tabs.len(), 2);
    assert_eq!(
        latest.windows[0].tabs[1].url, "https://a2.example",
        "tab order/url preserved"
    );
    assert!(latest.windows[0].tabs[1].pinned);
    assert_eq!(latest.windows[0].bounds_width, Some(1280.0));
    assert_eq!(latest.windows[1].bounds_height, Some(720.0));
}

#[test]
fn save_order_reversed_preserves_both_windows() {
    let (_dir, store, id) = setup();
    let profile = store.open_profile(&id).unwrap();
    let repo = profile.sessions();
    let b = repo
        .save_window(None, "AMARE session", &window("b", 1, 2))
        .unwrap();
    let a = repo
        .save_window(Some(b.session.id), "AMARE session", &window("a", 0, 3))
        .unwrap();
    assert_eq!(a.windows.len(), 2);
    assert_eq!(a.windows[0].window_id, "a");
    assert_eq!(a.windows[0].tabs.len(), 3);
    assert_eq!(a.windows[1].window_id, "b");
    assert_eq!(a.windows[1].tabs.len(), 2);
}

#[test]
fn closing_one_window_keeps_the_other() {
    let (dir, store, id) = setup();
    let session_id;
    {
        let profile = store.open_profile(&id).unwrap();
        let repo = profile.sessions();
        let a = repo
            .save_window(None, "AMARE session", &window("a", 0, 3))
            .unwrap();
        session_id = a.session.id;
        repo.save_window(Some(session_id), "AMARE session", &window("b", 1, 2))
            .unwrap();
        assert!(repo.delete_window(session_id, "a").unwrap());
        let latest = repo.latest_windows().unwrap().unwrap();
        assert_eq!(latest.windows.len(), 1);
        assert_eq!(latest.windows[0].window_id, "b");
        assert_eq!(latest.windows[0].tabs.len(), 2);
    }

    let reopened = Store::open(dir.path()).unwrap();
    let latest = reopened
        .open_profile(&id)
        .unwrap()
        .sessions()
        .latest_windows()
        .unwrap()
        .unwrap();
    assert_eq!(latest.windows.len(), 1);
    assert_eq!(latest.windows[0].window_id, "b");
}

#[test]
fn three_windows_survive_every_save_order() {
    let (_dir, store, id) = setup();
    let profile = store.open_profile(&id).unwrap();
    let repo = profile.sessions();
    let orders = [
        ["a", "b", "c"],
        ["a", "c", "b"],
        ["b", "a", "c"],
        ["b", "c", "a"],
        ["c", "a", "b"],
        ["c", "b", "a"],
    ];
    for (index, order) in orders.iter().enumerate() {
        let mut session_id = None;
        for id in order.iter() {
            let (ordinal, count) = match *id {
                "a" => (0, 1),
                "b" => (1, 2),
                _ => (2, 3),
            };
            let state = repo
                .save_window(
                    session_id,
                    &format!("order-{index}"),
                    &window(id, ordinal, count),
                )
                .unwrap();
            session_id = Some(state.session.id);
        }
        let restored = repo.restore_windows(session_id.unwrap()).unwrap();
        assert_eq!(restored.windows.len(), 3, "order {index}");
        assert_eq!(restored.windows[0].window_id, "a");
        assert_eq!(restored.windows[0].tabs.len(), 1);
        assert_eq!(restored.windows[1].window_id, "b");
        assert_eq!(restored.windows[1].tabs.len(), 2);
        assert_eq!(restored.windows[2].window_id, "c");
        assert_eq!(restored.windows[2].tabs.len(), 3);
    }
}

#[test]
fn repeated_save_replaces_only_the_target_window() {
    let (_dir, store, id) = setup();
    let profile = store.open_profile(&id).unwrap();
    let repo = profile.sessions();
    let a = repo
        .save_window(None, "AMARE session", &window("a", 0, 3))
        .unwrap();
    repo.save_window(Some(a.session.id), "AMARE session", &window("b", 1, 2))
        .unwrap();

    let mut changed = window("a", 0, 1);
    changed.active_position = Some(0);
    changed.tabs[0].url = "https://a-final.example".into();
    changed.tabs[0].title = "A final".into();
    let state = repo
        .save_window(Some(a.session.id), "AMARE session", &changed)
        .unwrap();
    let window_a = state.windows.iter().find(|w| w.window_id == "a").unwrap();
    let window_b = state.windows.iter().find(|w| w.window_id == "b").unwrap();
    assert_eq!(window_a.tabs.len(), 1);
    assert_eq!(window_a.tabs[0].url, "https://a-final.example");
    assert_eq!(window_b.tabs.len(), 2, "other window untouched");
    assert_eq!(window_b.tabs[0].url, "https://b1.example");
}

#[test]
fn reopen_after_drop_restores_last_committed_windows() {
    let (dir, store, id) = setup();
    let session_id;
    {
        let profile = store.open_profile(&id).unwrap();
        let repo = profile.sessions();
        let a = repo
            .save_window(None, "AMARE session", &window("a", 0, 2))
            .unwrap();
        session_id = a.session.id;
        repo.save_window(Some(session_id), "AMARE session", &window("b", 1, 4))
            .unwrap();
        // Simulate an abrupt process exit: no delete/close path is called.
    }
    let reopened = Store::open(dir.path()).unwrap();
    let latest = reopened
        .open_profile(&id)
        .unwrap()
        .sessions()
        .latest_windows()
        .unwrap()
        .unwrap();
    assert_eq!(latest.session.id, session_id);
    assert_eq!(latest.windows.len(), 2);
    assert_eq!(latest.windows[0].tabs.len(), 2);
    assert_eq!(latest.windows[1].tabs.len(), 4);
}

#[test]
fn failed_save_keeps_previous_window_data() {
    let (_dir, store, id) = setup();
    let profile = store.open_profile(&id).unwrap();
    let repo = profile.sessions();
    let a = repo
        .save_window(None, "AMARE session", &window("a", 0, 2))
        .unwrap();
    let mut invalid = window("a", 0, 1);
    invalid.active_position = Some(99);
    assert!(
        repo.save_window(Some(a.session.id), "AMARE session", &invalid)
            .is_err()
    );
    let latest = repo.latest_windows().unwrap().unwrap();
    assert_eq!(latest.windows[0].tabs.len(), 2);
    assert_eq!(latest.windows[0].tabs[0].url, "https://a1.example");
}

#[test]
fn concurrent_writers_do_not_lose_windows() {
    let (_dir, store, id) = setup();
    let path = store.profile_db_path(&id).unwrap();
    let session_id;
    {
        let profile = store.open_profile(&id).unwrap();
        session_id = profile
            .sessions()
            .save_window(None, "AMARE session", &window("seed", 0, 1))
            .unwrap()
            .session
            .id;
    }

    let barrier = Arc::new(Barrier::new(3));
    let mut handles = Vec::new();
    for (window_id, ordinal, count) in [("a", 1, 3), ("b", 2, 2)] {
        let path = path.clone();
        let barrier = barrier.clone();
        handles.push(thread::spawn(move || {
            let profile = ProfileStore::open_at(&path, ProfileId::new("p1")).unwrap();
            barrier.wait();
            profile
                .sessions()
                .save_window(
                    Some(session_id),
                    "AMARE session",
                    &window(window_id, ordinal, count),
                )
                .unwrap();
        }));
    }
    barrier.wait();
    for handle in handles {
        handle.join().unwrap();
    }

    let latest = store
        .open_profile(&id)
        .unwrap()
        .sessions()
        .latest_windows()
        .unwrap()
        .unwrap();
    assert_eq!(latest.windows.len(), 3);
    assert_eq!(
        latest
            .windows
            .iter()
            .find(|window| window.window_id == "a")
            .unwrap()
            .tabs
            .len(),
        3
    );
    assert_eq!(
        latest
            .windows
            .iter()
            .find(|window| window.window_id == "b")
            .unwrap()
            .tabs
            .len(),
        2
    );
}

fn create_v4_database(path: &std::path::Path) {
    let conn = Connection::open(path).unwrap();
    conn.execute_batch(
        "CREATE TABLE schema_migrations (
            version INTEGER PRIMARY KEY,
            applied_at_ms INTEGER NOT NULL
        );
        CREATE TABLE sessions (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            name           TEXT NOT NULL,
            active_tab_id  INTEGER NULL,
            created_at_ms  INTEGER NOT NULL,
            updated_at_ms  INTEGER NOT NULL
        );
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
        CREATE INDEX idx_sessions_updated ON sessions(updated_at_ms DESC);
        CREATE INDEX idx_session_tabs_session ON session_tabs(session_id, position, id);",
    )
    .unwrap();
    for version in 1..=4 {
        conn.execute(
            "INSERT INTO schema_migrations(version, applied_at_ms) VALUES (?1, ?2)",
            params![version, 1000 + version],
        )
        .unwrap();
    }
    conn.execute(
        "INSERT INTO sessions(id, name, active_tab_id, created_at_ms, updated_at_ms)
         VALUES (7, 'Legacy single window', 2, 100, 200)",
        [],
    )
    .unwrap();
    conn.execute(
        "INSERT INTO session_tabs(session_id, url, title, position, pinned, group_id, last_accessed_at_ms)
         VALUES (7, 'https://legacy1.example', 'Legacy 1', 0, 0, NULL, 100),
                (7, 'https://legacy2.example', 'Legacy 2', 1, 1, NULL, 101)",
        [],
    )
    .unwrap();
    conn.pragma_update(None, "user_version", 4).unwrap();
}

#[test]
fn v4_single_window_migrates_to_main_window_without_data_loss() {
    let dir = tempdir().unwrap();
    let path = dir.path().join("legacy.db");
    create_v4_database(&path);
    let profile = ProfileStore::open_at(&path, ProfileId::new("p1")).unwrap();
    assert_eq!(profile.schema_version().unwrap(), 5);
    let latest = profile.sessions().latest_windows().unwrap().unwrap();
    assert_eq!(latest.session.id, 7);
    assert_eq!(latest.windows.len(), 1);
    assert_eq!(latest.windows[0].window_id, "main");
    assert_eq!(latest.windows[0].tabs.len(), 2);
    assert_eq!(latest.windows[0].tabs[1].url, "https://legacy2.example");
    assert_eq!(latest.windows[0].active_tab_id, Some(2));
    assert!(latest.windows[0].tabs[1].pinned);

    let legacy = profile.sessions().restore(7).unwrap();
    assert_eq!(legacy.tabs.len(), 2);
    assert_eq!(legacy.session.name, "Legacy single window");
}

#[test]
fn migration_dry_run_preserves_original_and_copy_integrity() {
    let dir = tempdir().unwrap();
    let original = dir.path().join("original.db");
    let copy = dir.path().join("copy.db");
    create_v4_database(&original);
    std::fs::copy(&original, &copy).unwrap();

    let before = std::fs::read(&original).unwrap();
    let before_hash = amare_security::sha256_hex(&before);
    let copy_before = std::fs::read(&copy).unwrap();
    assert_eq!(before, copy_before, "copy must start identical");

    {
        let profile = ProfileStore::open_at(&copy, ProfileId::new("p1")).unwrap();
        assert_eq!(profile.schema_version().unwrap(), 5);
        let latest = profile.sessions().latest_windows().unwrap().unwrap();
        assert_eq!(latest.windows[0].tabs.len(), 2);
        let check: String = profile
            .sessions()
            .restore_windows(latest.session.id)
            .unwrap()
            .windows[0]
            .tabs[0]
            .url
            .clone();
        assert_eq!(check, "https://legacy1.example");
    }

    let after = std::fs::read(&original).unwrap();
    assert_eq!(before, after, "original fixture bytes must not change");
    assert_eq!(before_hash, amare_security::sha256_hex(&after));

    let conn = Connection::open(&copy).unwrap();
    let integrity: String = conn
        .query_row("PRAGMA integrity_check", [], |row| row.get(0))
        .unwrap();
    assert_eq!(integrity, "ok");
    let foreign_keys: u32 = conn
        .query_row("SELECT COUNT(*) FROM pragma_foreign_key_check", [], |row| {
            row.get(0)
        })
        .unwrap();
    assert_eq!(foreign_keys, 0);
    let window_count: u32 = conn
        .query_row("SELECT COUNT(*) FROM session_windows", [], |row| row.get(0))
        .unwrap();
    assert_eq!(window_count, 1);
    let tab_window: String = conn
        .query_row(
            "SELECT window_id FROM session_tabs WHERE id = 2",
            [],
            |row| row.get(0),
        )
        .unwrap();
    assert_eq!(tab_window, "main");
}
