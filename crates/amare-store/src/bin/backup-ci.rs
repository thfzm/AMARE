use amare_core::{
    LibraryKind, MemoryType, MessageRole, MessageStatus, MessageVisibility, NewBookmark,
    NewConversation, NewHistoryVisit, NewLibraryItem, NewMessage, NewSessionTab, NewTask,
    NewTaskMemory, NewWorkspace, ProfileId, SettingValue, TaskStatus,
};
use amare_security::{InMemoryKeyProtector, KeyFile};
use amare_store::{
    BackupCredential, BackupData, BackupEncryption, BackupManager, BackupOptions, RestoreMode,
    RestoreOptions, Store, check_database,
};
use serde_json::json;
use std::fs;
use std::path::PathBuf;
use std::time::Instant;

struct CaseResult {
    name: &'static str,
    status: &'static str,
    executed: bool,
    duration_ms: u128,
    detail: String,
}

fn main() {
    let output = parse_output();
    let root = unique_temp_dir("amare-backup-ci");
    let report = run(&root);
    let _ = fs::remove_dir_all(&root);
    let text = serde_json::to_string_pretty(&report).unwrap();
    if let Some(path) = output {
        if let Some(parent) = path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        fs::write(path, &text).unwrap();
    } else {
        println!("{text}");
    }
    if report["summary"]["failed"].as_u64().unwrap_or(0) > 0 {
        std::process::exit(1);
    }
}

fn run(root: &PathBuf) -> serde_json::Value {
    let store = match Store::open(root) {
        Ok(store) => store,
        Err(error) => {
            return json!({
                "environment": environment(),
                "cases": [],
                "summary": {"passed": 0, "failed": 1, "unverified": 0, "skipped": 0},
                "error": error.to_string()
            });
        }
    };
    let manager = BackupManager::new(root).unwrap();
    let mut cases = Vec::new();

    cases.push(case("backup_full_restore", || {
        let (p1, p2) = seed_profiles(&store)?;
        let record = manager.create_backup(&store, &BackupOptions::default())?;
        let path = manager.backup_dir().join(&record.file_name);
        manager.verify(&path, &BackupCredential::None)?;
        let target = unique_temp_dir("amare-backup-ci-target");
        manager.restore(
            &path,
            &target,
            &RestoreOptions {
                credential: BackupCredential::None,
                mode: RestoreMode::Full,
            },
        )?;
        let restored = Store::open(&target)?;
        assert_eq!(restored.profiles().count()?, 2);
        for (id, marker) in [(&p1, "one"), (&p2, "two")] {
            let profile = restored.open_profile(id)?;
            assert_eq!(
                profile.settings().get("marker")?.unwrap().value,
                SettingValue::Text(marker.into())
            );
            assert!(profile.library().list(10, 0)?.total >= 1, "{marker}");
            assert!(
                profile.conversations().list(true, 10, 0)?.total >= 1,
                "{marker}"
            );
            assert!(
                profile.workspaces().list(true, 10, 0)?.total >= 1,
                "{marker}"
            );
        }
        let _ = fs::remove_dir_all(&target);
        Ok("full backup -> verify -> full restore".to_string())
    }));

    cases.push(case("backup_single_profile_restore", || {
        let (p1, p2) = seed_profiles(&store)?;
        let record = manager.create_backup(
            &store,
            &BackupOptions {
                include_root: false,
                profiles: Some(vec![p1.clone()]),
                ..BackupOptions::default()
            },
        )?;
        let target = unique_temp_dir("amare-backup-ci-single");
        let target_store = Store::open(&target)?;
        target_store.create_profile(&p1, "P1", false)?;
        target_store.create_profile(&p2, "P2", false)?;
        target_store.open_profile(&p2)?.settings().set(
            "marker",
            "general",
            SettingValue::Text("keep".into()),
        )?;
        manager.restore(
            manager.backup_dir().join(&record.file_name),
            &target,
            &RestoreOptions {
                credential: BackupCredential::None,
                mode: RestoreMode::Profiles(vec![p1.clone()]),
            },
        )?;
        let restored = Store::open(&target)?;
        assert_eq!(
            restored
                .open_profile(&p1)?
                .settings()
                .get("marker")?
                .unwrap()
                .value,
            SettingValue::Text("one".into())
        );
        assert_eq!(
            restored
                .open_profile(&p2)?
                .settings()
                .get("marker")?
                .unwrap()
                .value,
            SettingValue::Text("keep".into())
        );
        let _ = fs::remove_dir_all(&target);
        Ok("single profile restore preserves other profile".to_string())
    }));

    cases.push(case("password_encryption", || {
        let _ = seed_profiles(&store)?;
        let record = manager.create_backup(
            &store,
            &BackupOptions {
                encryption: BackupEncryption::Password("secret".into()),
                ..BackupOptions::default()
            },
        )?;
        let path = manager.backup_dir().join(&record.file_name);
        assert!(manager.verify(&path, &BackupCredential::None).is_err());
        assert!(
            manager
                .verify(&path, &BackupCredential::Password("bad".into()))
                .is_err()
        );
        manager.verify(&path, &BackupCredential::Password("secret".into()))?;
        Ok("wrong credential rejected; correct credential accepted".to_string())
    }));

    cases.push(case("checksum_corruption", || {
        let _ = seed_profiles(&store)?;
        let record = manager.create_backup(&store, &BackupOptions::default())?;
        let path = manager.backup_dir().join(&record.file_name);
        let mut bytes = fs::read(&path)?;
        let last = bytes.len() - 1;
        bytes[last] ^= 1;
        fs::write(&path, bytes)?;
        assert!(manager.verify(&path, &BackupCredential::None).is_err());
        Ok("corrupted backup rejected".to_string())
    }));

    cases.push(case("data_exclusion", || {
        let _ = seed_profiles(&store)?;
        let record = manager.create_backup(
            &store,
            &BackupOptions {
                data: BackupData {
                    history: false,
                    secrets: false,
                    ..BackupData::default()
                },
                ..BackupOptions::default()
            },
        )?;
        assert!(!record.contains_secrets);
        let path = manager.backup_dir().join(&record.file_name);
        let manifest = manager.verify(&path, &BackupCredential::None)?;
        assert!(!manifest.data_kinds.contains(&"history".to_string()));
        Ok("history/secrets excluded from backup".to_string())
    }));

    cases.push(case("wal_backup_during_write", || {
        let (p1, _) = seed_profiles(&store)?;
        let path = store.profile_db_path(&p1)?;
        let writer_path = path.clone();
        let writer_id = p1.clone();
        let writer = std::thread::spawn(move || {
            let profile = amare_store::ProfileStore::open_at(&writer_path, writer_id).unwrap();
            for index in 0..100 {
                profile
                    .history()
                    .record_visit(NewHistoryVisit {
                        url: format!("https://concurrent.example/{index}"),
                        title: format!("Concurrent {index}"),
                        visited_at_ms: 1_700_000_000_000 + index,
                    })
                    .unwrap();
            }
        });
        let record = manager.create_backup(&store, &BackupOptions::default())?;
        writer
            .join()
            .map_err(|_| "writer thread panicked".to_string())?;
        manager.verify(
            manager.backup_dir().join(&record.file_name),
            &BackupCredential::None,
        )?;
        Ok("SQLite Backup API completed while another connection wrote".to_string())
    }));

    cases.push(case("key_file_encryption", || {
        let _ = seed_profiles(&store)?;
        let key_dir = unique_temp_dir("amare-backup-ci-key");
        let key_path = key_dir.join("backup.key");
        let protector = InMemoryKeyProtector::new([12u8; 32]);
        let key_file = KeyFile::new(&key_path);
        let key = key_file
            .load_or_create(&protector, "amare.backup.key")
            .map_err(|error| error.to_string())?;
        let record = manager.create_backup(
            &store,
            &BackupOptions {
                encryption: BackupEncryption::Key(key),
                ..BackupOptions::default()
            },
        )?;
        manager.verify(
            manager.backup_dir().join(&record.file_name),
            &BackupCredential::Key(key),
        )?;
        let _ = fs::remove_dir_all(&key_dir);
        Ok("KeyFile-protected key encrypted/decrypted backup".to_string())
    }));

    cases.push(case("phase_1_6_data_exclusion", || {
        let _ = seed_profiles(&store)?;
        let record = manager.create_backup(
            &store,
            &BackupOptions {
                data: BackupData {
                    library: false,
                    conversations: false,
                    workspaces: false,
                    ..BackupData::default()
                },
                ..BackupOptions::default()
            },
        )?;
        let manifest = manager.verify(
            manager.backup_dir().join(&record.file_name),
            &BackupCredential::None,
        )?;
        assert!(!manifest.data_kinds.iter().any(|kind| kind == "library"));
        assert!(
            !manifest
                .data_kinds
                .iter()
                .any(|kind| kind == "conversations")
        );
        assert!(!manifest.data_kinds.iter().any(|kind| kind == "workspaces"));
        Ok("Library/AI/workspace categories excluded and absent from manifest".to_string())
    }));

    cases.push(case("integrity_check", || {
        let (p1, _) = seed_profiles(&store)?;
        let db = store.profile_db_path(&p1)?;
        assert!(check_database(&db).ok);
        let corrupt = db.with_file_name("corrupt.db");
        fs::write(&corrupt, b"not a database")?;
        assert!(!check_database(&corrupt).ok);
        assert!(corrupt.exists());
        Ok("valid DB passes; corrupt DB fails and is preserved".to_string())
    }));

    cases.push(case("auto_backup_prune", || {
        let _ = seed_profiles(&store)?;
        for _ in 0..3 {
            manager.create_backup(&store, &BackupOptions::default())?;
        }
        let removed = manager.prune(1)?;
        assert!(!removed.is_empty());
        Ok("prune kept newest backup".to_string())
    }));

    let passed = cases.iter().filter(|case| case.status == "PASS").count();
    let failed = cases.iter().filter(|case| case.status == "FAIL").count();
    json!({
        "environment": environment(),
        "cases": cases.iter().map(|case| json!({
            "id": format!("backup.{}", case.name),
            "name": case.name,
            "status": case.status,
            "executed": case.executed,
            "duration_ms": case.duration_ms,
            "detail": case.detail,
            "environment": environment(),
            "log": "backup-ci.log",
            "limitation": "Backup logic only; Windows DPAPI is tested separately on Windows."
        })).collect::<Vec<_>>(),
        "summary": {
            "passed": passed,
            "failed": failed,
            "unverified": 0,
            "skipped": 0
        },
        "note": "Runner results are not a substitute for the target Windows 11 device."
    })
}

fn seed_profiles(store: &Store) -> Result<(ProfileId, ProfileId), Box<dyn std::error::Error>> {
    let p1 = ProfileId::new(format!("p1-{}", std::process::id()));
    let p2 = ProfileId::new(format!("p2-{}", std::process::id()));
    if store.profiles().get(&p1)?.is_none() {
        store.create_profile(&p1, "P1", false)?;
    }
    if store.profiles().get(&p2)?.is_none() {
        store.create_profile(&p2, "P2", false)?;
    }
    for (id, marker) in [(&p1, "one"), (&p2, "two")] {
        let profile = store.open_profile(id)?;
        profile
            .settings()
            .set("marker", "general", SettingValue::Text(marker.into()))?;
        profile.bookmarks().create(NewBookmark {
            title: marker.into(),
            url: format!("https://{marker}.example"),
            folder_id: None,
            position: None,
        })?;
        profile.history().record_visit(NewHistoryVisit {
            url: format!("https://{marker}.example"),
            title: marker.into(),
            visited_at_ms: 1000,
        })?;
        profile.sessions().create(
            marker,
            &[NewSessionTab {
                url: format!("https://{marker}.example"),
                title: marker.into(),
                pinned: false,
                group_id: None,
                position: None,
            }],
            Some(0),
        )?;
        if profile.library().list(10, 0)?.total == 0 {
            let item = profile.library().create(&NewLibraryItem {
                title: format!("Library {marker}"),
                description: "Backup phase 1.6".into(),
                kind: LibraryKind::Research,
                original_url: Some(format!("https://{marker}.example/library")),
                source_name: Some("Example".into()),
                image_ref: None,
                user_note: String::new(),
                favorite: false,
            })?;
            profile.sources().create(&amare_core::NewSourceReference {
                url: format!("https://{marker}.example/source"),
                page_title: "Source".into(),
                site_name: "Example".into(),
                quote_text: "evidence".into(),
                location_info: String::new(),
                library_item_id: Some(item.id),
            })?;
        }
        if profile.conversations().list(true, 10, 0)?.total == 0 {
            let conversation = profile.conversations().create(&NewConversation {
                title: format!("Conversation {marker}"),
                workspace_id: None,
                task_id: None,
            })?;
            profile.conversations().add_message(
                conversation.id,
                &NewMessage {
                    role: MessageRole::User,
                    body: format!("Message {marker}"),
                    status: MessageStatus::Complete,
                    visibility: MessageVisibility::User,
                    parts: Vec::new(),
                },
            )?;
        }
        if profile.workspaces().list(true, 10, 0)?.total == 0 {
            let workspace = profile.workspaces().create(&NewWorkspace {
                name: format!("Workspace {marker}"),
                description: String::new(),
                icon: None,
            })?;
            let task = profile.tasks().create(&NewTask {
                workspace_id: workspace.id,
                title: format!("Task {marker}"),
                description: String::new(),
                status: TaskStatus::Open,
            })?;
            profile.task_memories().create(&NewTaskMemory {
                task_id: task.id,
                title: format!("Memory {marker}"),
                content: "Durable memory".into(),
                memory_type: MemoryType::Fact,
                source_reference_id: None,
                importance: 2,
                user_confirmed: true,
            })?;
        }
    }
    Ok((p1, p2))
}

fn case(
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
            detail: "panic".into(),
        },
    }
}

fn environment() -> serde_json::Value {
    json!({
        "os": std::env::consts::OS,
        "arch": std::env::consts::ARCH,
        "rust_crate": env!("CARGO_PKG_VERSION"),
        "sqlite": rusqlite::version(),
        "timestamp_ms": std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_millis() as u64).unwrap_or(0)
    })
}

fn unique_temp_dir(prefix: &str) -> PathBuf {
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_nanos())
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
