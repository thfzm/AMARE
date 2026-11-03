use amare_core::{
    BookmarkUpdate, LibraryKind, MemoryType, MessageRole, MessageStatus, MessageVisibility,
    NewBookmark, NewConversation, NewHistoryVisit, NewLibraryItem, NewMessage, NewSessionTab,
    NewTask, NewTaskMemory, NewWorkspace, ProfileId, SearchKind, SettingValue, TaskStatus,
};
use amare_store::{FolderScope, ProfileStore, Store};
use serde_json::json;
use std::fs;
use std::path::PathBuf;
use std::time::Instant;

#[derive(Debug)]
struct CaseResult {
    name: &'static str,
    status: &'static str,
    executed: bool,
    duration_ms: u128,
    detail: String,
}

fn main() {
    let output = parse_output();
    let root = unique_temp_dir("amare-store-ci");
    let report = run(&root);
    let _ = fs::remove_dir_all(&root);

    let text = serde_json::to_string_pretty(&report).unwrap();
    if let Some(path) = output {
        if let Some(parent) = path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        fs::write(&path, &text).unwrap();
        println!("storage CI written to {}", path.display());
    } else {
        println!("{text}");
    }
    if report["summary"]["failed"].as_u64().unwrap_or(0) > 0 {
        std::process::exit(1);
    }
}

fn run(root: &PathBuf) -> serde_json::Value {
    let startup = Instant::now();
    let store = Store::open(root);
    let store = match store {
        Ok(store) => store,
        Err(error) => {
            return json!({
                "environment": environment(),
                "cases": [],
                "summary": {"passed": 0, "failed": 1, "error": error.to_string()},
                "duration_ms": startup.elapsed().as_millis() as u64
            });
        }
    };

    let mut cases = Vec::new();
    cases.push(run_case("db_init_reopen", || {
        let id = ProfileId::new("ci-init");
        store.create_profile(&id, "CI Init", true)?;
        {
            let profile = store.open_profile(&id)?;
            profile
                .settings()
                .set("theme", "appearance", SettingValue::Text("dark".into()))?;
            profile.bookmarks().create(NewBookmark {
                title: "Init".into(),
                url: "https://init.example".into(),
                folder_id: None,
                position: None,
            })?;
        }
        let profile = store.open_profile(&id)?;
        assert_eq!(
            profile.settings().get("theme")?.unwrap().value,
            SettingValue::Text("dark".into())
        );
        assert_eq!(profile.bookmarks().list(FolderScope::All, 10, 0)?.total, 1);
        Ok("profile DB created, reopened, settings/bookmark preserved".to_string())
    }));

    cases.push(run_case("settings_crud", || {
        let id = ProfileId::new("ci-settings");
        store.create_profile(&id, "Settings", false)?;
        let profile = store.open_profile(&id)?;
        let settings = profile.settings();
        settings.set("text", "general", SettingValue::Text("v".into()))?;
        settings.set("num", "general", SettingValue::Integer(7))?;
        settings.set("flag", "general", SettingValue::Boolean(true))?;
        settings.set(
            "json",
            "advanced",
            SettingValue::Json("{\"ok\":true}".into()),
        )?;
        assert_eq!(
            settings.get("num")?.unwrap().value,
            SettingValue::Integer(7)
        );
        assert_eq!(settings.list(Some("general"), 10, 0)?.total, 3);
        assert_eq!(settings.search("v", 10, 0)?.total, 1);
        assert!(settings.delete("text")?);
        Ok("settings CRUD/type preservation/search".to_string())
    }));

    cases.push(run_case("bookmarks_folders_fk", || {
        let id = ProfileId::new("ci-bookmarks");
        store.create_profile(&id, "Bookmarks", false)?;
        let profile = store.open_profile(&id)?;
        let repo = profile.bookmarks();
        let root = repo.create_folder("Root", None, None)?;
        let child = repo.create_folder("Child", Some(root.id), None)?;
        let bookmark = repo.create(NewBookmark {
            title: "Example".into(),
            url: "https://example.com".into(),
            folder_id: Some(child.id),
            position: None,
        })?;
        assert_eq!(repo.list(FolderScope::Folder(child.id), 10, 0)?.total, 1);
        assert!(repo.delete_folder(root.id)?);
        assert_eq!(repo.get(bookmark.id)?.unwrap().folder_id, None);
        let bad = repo.create(NewBookmark {
            title: "Bad".into(),
            url: "https://bad.example".into(),
            folder_id: Some(99999),
            position: None,
        });
        assert!(bad.is_err());
        let updated = repo.update(
            bookmark.id,
            BookmarkUpdate {
                title: "Updated".into(),
                url: "https://example.com/updated".into(),
                folder_id: None,
                position: 0,
            },
        )?;
        assert_eq!(updated.title, "Updated");
        Ok("folder hierarchy, FK set-null, invalid FK rejection, update".to_string())
    }));

    cases.push(run_case("history_search", || {
        let id = ProfileId::new("ci-history");
        store.create_profile(&id, "History", false)?;
        let profile = store.open_profile(&id)?;
        let repo = profile.history();
        repo.record_visits(&[
            NewHistoryVisit {
                url: "https://a.example".into(),
                title: "A".into(),
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
        ])?;
        let page = repo.search(None, Some(1000), Some(1800), 10, 0)?;
        assert_eq!(page.total, 1);
        assert_eq!(page.items[0].visit_count, 2);
        assert_eq!(repo.list(10, 0)?.total, 2);
        Ok("history aggregation/date range/visit count".to_string())
    }));

    cases.push(run_case("sessions_restore", || {
        let id = ProfileId::new("ci-sessions");
        store.create_profile(&id, "Sessions", false)?;
        let profile = store.open_profile(&id)?;
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
                group_id: Some("g".into()),
                position: None,
            },
        ];
        let state = repo.create("Work", &tabs, Some(1))?;
        assert_eq!(state.tabs.len(), 2);
        assert_eq!(state.session.active_tab_id, Some(state.tabs[1].id));
        let restored = repo.restore(state.session.id)?;
        assert_eq!(restored.tabs.len(), 2);
        let before = repo.get_tabs(state.session.id)?.len();
        assert!(
            repo.replace_tabs(state.session.id, &tabs[..1], Some(99))
                .is_err()
        );
        assert_eq!(repo.get_tabs(state.session.id)?.len(), before);
        Ok("session save/restore/active tab/transaction rollback".to_string())
    }));

    cases.push(run_case("profile_isolation", || {
        let p1 = ProfileId::new("ci-iso-1");
        let p2 = ProfileId::new("ci-iso-2");
        store.create_profile(&p1, "Iso 1", false)?;
        store.create_profile(&p2, "Iso 2", false)?;
        {
            let profile = store.open_profile(&p1)?;
            profile
                .settings()
                .set("only-p1", "general", SettingValue::Text("secret".into()))?;
            profile.bookmarks().create(NewBookmark {
                title: "P1".into(),
                url: "https://p1.example".into(),
                folder_id: None,
                position: None,
            })?;
        }
        let profile = store.open_profile(&p2)?;
        assert!(profile.settings().get("only-p1")?.is_none());
        assert_eq!(profile.bookmarks().list(FolderScope::All, 10, 0)?.total, 0);
        Ok("profile DB files are isolated".to_string())
    }));

    cases.push(run_case("sql_injection", || {
        let id = ProfileId::new("ci-injection");
        store.create_profile(&id, "Injection", false)?;
        let profile = store.open_profile(&id)?;
        profile.bookmarks().create(NewBookmark {
            title: "x'); DROP TABLE bookmarks; --".into(),
            url: "https://safe.example".into(),
            folder_id: None,
            position: None,
        })?;
        assert_eq!(profile.bookmarks().list(FolderScope::All, 10, 0)?.total, 1);
        Ok("parameterized SQL preserved the bookmarks table".to_string())
    }));

    cases.push(run_case("concurrent_writers", || {
        let id = ProfileId::new("ci-concurrent");
        store.create_profile(&id, "Concurrent", false)?;
        drop(store.open_profile(&id)?);
        let path = store.profile_db_path(&id)?;
        let mut handles = Vec::new();
        for worker in 0..2 {
            let path = path.clone();
            let profile_id = id.clone();
            handles.push(std::thread::spawn(move || {
                let profile = ProfileStore::open_at(&path, profile_id).unwrap();
                for index in 0..20 {
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
            handle
                .join()
                .map_err(|_| "worker thread panicked".to_string())?;
        }
        let profile = store.open_profile(&id)?;
        assert_eq!(
            profile.bookmarks().list(FolderScope::All, 100, 0)?.total,
            40
        );
        Ok("two connections, WAL + busy timeout, 40 writes".to_string())
    }));

    cases.push(run_case("phase_1_6_library_ai_workspace_fts", || {
        let id = ProfileId::new("ci-phase-1-6");
        store.create_profile(&id, "Phase 1.6", false)?;
        let profile = store.open_profile(&id)?;
        let item = profile.library().create(&NewLibraryItem {
            title: "Ownership research".into(),
            description: "Primary source notes".into(),
            kind: LibraryKind::Research,
            original_url: Some("https://example.com/research".into()),
            source_name: Some("Example".into()),
            image_ref: None,
            user_note: "keep".into(),
            favorite: true,
        })?;
        let tag = profile.library().create_tag("Research Notes")?;
        profile.library().attach_tag(item.id, tag.id)?;
        let collection = profile.library().create_collection("Reading", "")?;
        profile.library().add_to_collection(item.id, collection.id)?;
        let source = profile.sources().create(&amare_core::NewSourceReference {
            url: "https://example.com/source".into(),
            page_title: "Source".into(),
            site_name: "Example".into(),
            quote_text: "evidence".into(),
            location_info: "p.1".into(),
            library_item_id: Some(item.id),
        })?;

        let conversation = profile.conversations().create(&NewConversation {
            title: "Ownership conversation".into(),
            workspace_id: None,
            task_id: None,
        })?;
        let message = profile.conversations().add_message(
            conversation.id,
            &NewMessage {
                role: MessageRole::User,
                body: "Discuss ownership evidence".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::User,
                parts: Vec::new(),
            },
        )?;
        profile.conversations().attach_source(message.id, source.id)?;

        let workspace = profile.workspaces().create(&NewWorkspace {
            name: "Research".into(),
            description: "Long-term work".into(),
            icon: None,
        })?;
        let task = profile.tasks().create(&NewTask {
            workspace_id: workspace.id,
            title: "Collect sources".into(),
            description: String::new(),
            status: TaskStatus::Open,
        })?;
        let memory = profile.task_memories().create(&NewTaskMemory {
            task_id: task.id,
            title: "Primary sources".into(),
            content: "Prefer primary sources.".into(),
            memory_type: MemoryType::UserRule,
            source_reference_id: Some(source.id),
            importance: 4,
            user_confirmed: true,
        })?;

        assert_eq!(profile.library().search("ownership", 10, 0)?.total, 1);
        assert_eq!(profile.conversations().search("ownership", 10, 0)?.total, 1);
        assert_eq!(profile.tasks().search("sources", 10, 0)?.total, 1);
        assert_eq!(profile.task_memories().search("primary", 10, 0)?.total, 1);
        let hits = profile.search().search("ownership", 20)?;
        assert!(hits.iter().any(|hit| hit.kind == SearchKind::LibraryItem && hit.id == item.id));
        assert!(hits.iter().any(|hit| hit.kind == SearchKind::Conversation));
        assert!(hits.iter().any(|hit| hit.kind == SearchKind::Message));
        assert_eq!(
            profile.conversations().sources_for_message(message.id)?.len(),
            1
        );
        assert_eq!(
            profile.task_memories().list_for_task(task.id, false, 10, 0)?.total,
            1
        );
        assert_eq!(memory.id, profile.task_memories().get_required(memory.id)?.id);
        Ok("Library/tags/collections/sources, conversations/messages/parts, workspace/task/memory, unified FTS5".to_string())
    }));

    cases.push(run_case("encrypted_secrets", || {
        let id = ProfileId::new("ci-secrets");
        store.create_profile(&id, "Secrets", false)?;
        let key = [3u8; 32];
        {
            let profile = store.open_profile(&id)?;
            profile
                .secrets()
                .set("api.key", "ai", b"secret-value", &key)?;
            assert_eq!(
                profile.secrets().get("api.key", &key)?.unwrap(),
                b"secret-value"
            );
            assert!(profile.secrets().get("api.key", &[4u8; 32]).is_err());
        }
        let profile = store.open_profile(&id)?;
        assert_eq!(
            profile.secrets().get("api.key", &key)?.unwrap(),
            b"secret-value"
        );
        Ok("XChaCha20-Poly1305 secret storage survives reopen; wrong key rejected".to_string())
    }));

    let total_ms = startup.elapsed().as_millis() as u64;
    let passed = cases.iter().filter(|case| case.status == "PASS").count();
    let failed = cases.iter().filter(|case| case.status == "FAIL").count();
    json!({
        "environment": environment(),
        "cases": cases.iter().map(|case| json!({
            "id": format!("storage.{}", case.name),
            "name": case.name,
            "status": case.status,
            "executed": case.executed,
            "duration_ms": case.duration_ms,
            "detail": case.detail,
            "environment": environment(),
            "log": "storage-ci.log",
            "limitation": "SQLite logic only; WebView2/Windows UI is not covered here."
        })).collect::<Vec<_>>(),
        "summary": {
            "passed": passed,
            "failed": failed,
            "unverified": 0,
            "skipped": 0
        },
        "duration_ms": total_ms,
        "note": "Runner results are not a substitute for the target Windows 11 device."
    })
}

fn run_case(
    name: &'static str,
    test: impl FnOnce() -> Result<String, Box<dyn std::error::Error>>,
) -> CaseResult {
    let started = Instant::now();
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(test)) {
        Ok(Ok(detail)) => CaseResult {
            name,
            status: "PASS",
            executed: true,
            duration_ms: started.elapsed().as_millis(),
            detail,
        },
        Ok(Err(error)) => CaseResult {
            name,
            status: "FAIL",
            executed: true,
            duration_ms: started.elapsed().as_millis(),
            detail: error.to_string(),
        },
        Err(_) => CaseResult {
            name,
            status: "FAIL",
            executed: true,
            duration_ms: started.elapsed().as_millis(),
            detail: "panic".to_string(),
        },
    }
}

fn environment() -> serde_json::Value {
    json!({
        "os": std::env::consts::OS,
        "arch": std::env::consts::ARCH,
        "rust_crate": env!("CARGO_PKG_VERSION"),
        "sqlite": rusqlite::version(),
        "timestamp_ms": std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map(|duration| duration.as_millis() as u64)
            .unwrap_or(0)
    })
}

fn unique_temp_dir(prefix: &str) -> PathBuf {
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or(0);
    std::env::temp_dir().join(format!("{prefix}-{}-{nanos}", std::process::id()))
}

fn parse_output() -> Option<PathBuf> {
    let mut args = std::env::args().skip(1);
    while let Some(arg) = args.next() {
        if arg == "--output" {
            return args.next().map(PathBuf::from);
        }
    }
    None
}
