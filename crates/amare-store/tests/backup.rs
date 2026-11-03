use amare_core::{
    LibraryKind, MemoryType, MessageRole, MessageStatus, MessageVisibility, NewBookmark,
    NewConversation, NewHistoryVisit, NewLibraryItem, NewMessage, NewSessionTab, NewTask,
    NewTaskMemory, NewWorkspace, ProfileId, SettingValue, TaskStatus,
};
use amare_security::{InMemoryKeyProtector, KeyFile};
use amare_store::{
    BackupCredential, BackupData, BackupEncryption, BackupManager, BackupOptions, BackupPolicy,
    FolderScope, RestoreMode, RestoreOptions, Store, check_database, check_store,
};
use std::fs;
use tempfile::tempdir;

fn seed_store(root: &std::path::Path) -> (Store, ProfileId, ProfileId, [u8; 32]) {
    let store = Store::open(root).unwrap();
    let p1 = ProfileId::new("p1");
    let p2 = ProfileId::new("p2");
    store.create_profile(&p1, "Personal", true).unwrap();
    store.create_profile(&p2, "Work", false).unwrap();
    let key = [5u8; 32];
    for (id, suffix) in [(&p1, "one"), (&p2, "two")] {
        let profile = store.open_profile(id).unwrap();
        profile
            .settings()
            .set("theme", "appearance", SettingValue::Text(suffix.into()))
            .unwrap();
        profile
            .bookmarks()
            .create(NewBookmark {
                title: format!("Bookmark {suffix}"),
                url: format!("https://{suffix}.example"),
                folder_id: None,
                position: None,
            })
            .unwrap();
        profile
            .history()
            .record_visit(NewHistoryVisit {
                url: format!("https://{suffix}.example"),
                title: format!("History {suffix}"),
                visited_at_ms: 1000,
            })
            .unwrap();
        profile
            .sessions()
            .create(
                &format!("Session {suffix}"),
                &[NewSessionTab {
                    url: format!("https://{suffix}.example"),
                    title: format!("Tab {suffix}"),
                    pinned: false,
                    group_id: None,
                    position: None,
                }],
                Some(0),
            )
            .unwrap();
        profile
            .secrets()
            .set("api.key", "ai", suffix.as_bytes(), &key)
            .unwrap();
        let item = profile
            .library()
            .create(&NewLibraryItem {
                title: format!("Library {suffix}"),
                description: "Phase 1.6 library data".into(),
                kind: LibraryKind::Research,
                original_url: Some(format!("https://{suffix}.example/library")),
                source_name: Some("Example".into()),
                image_ref: None,
                user_note: String::new(),
                favorite: false,
            })
            .unwrap();
        profile
            .sources()
            .create(&amare_core::NewSourceReference {
                url: format!("https://{suffix}.example/source"),
                page_title: "Source".into(),
                site_name: "Example".into(),
                quote_text: "evidence".into(),
                location_info: String::new(),
                library_item_id: Some(item.id),
            })
            .unwrap();
        let conversation = profile
            .conversations()
            .create(&NewConversation {
                title: format!("Conversation {suffix}"),
                workspace_id: None,
                task_id: None,
            })
            .unwrap();
        profile
            .conversations()
            .add_message(
                conversation.id,
                &NewMessage {
                    role: MessageRole::User,
                    body: format!("Message {suffix}"),
                    status: MessageStatus::Complete,
                    visibility: MessageVisibility::User,
                    parts: Vec::new(),
                },
            )
            .unwrap();
        let workspace = profile
            .workspaces()
            .create(&NewWorkspace {
                name: format!("Workspace {suffix}"),
                description: "Phase 1.6 workspace data".into(),
                icon: None,
            })
            .unwrap();
        let task = profile
            .tasks()
            .create(&NewTask {
                workspace_id: workspace.id,
                title: format!("Task {suffix}"),
                description: String::new(),
                status: TaskStatus::Open,
            })
            .unwrap();
        profile
            .task_memories()
            .create(&NewTaskMemory {
                task_id: task.id,
                title: format!("Memory {suffix}"),
                content: "Durable task memory".into(),
                memory_type: MemoryType::Fact,
                source_reference_id: None,
                importance: 2,
                user_confirmed: true,
            })
            .unwrap();
        profile
            .conversations()
            .update(
                conversation.id,
                &format!("Conversation {suffix}"),
                Some(workspace.id),
                Some(task.id),
            )
            .unwrap();
    }
    (store, p1, p2, key)
}

#[test]
fn creates_full_backup_and_restores_everything() {
    let source = tempdir().unwrap();
    let target = tempdir().unwrap();
    let (store, p1, p2, key) = seed_store(source.path());
    let manager = BackupManager::new(source.path()).unwrap();
    let record = manager
        .create_backup(&store, &BackupOptions::default())
        .unwrap();
    let backup = manager.backup_dir().join(&record.file_name);
    assert!(backup.exists());
    assert_eq!(record.profiles.len(), 2);
    assert!(record.contains_secrets);

    let manifest = manager.verify(&backup, &BackupCredential::None).unwrap();
    assert_eq!(manifest.profiles.len(), 2);
    assert!(manifest.files.iter().any(|file| file.path == "amare.db"));
    assert_eq!(manager.list().unwrap().len(), 1);

    let report = manager
        .restore(
            &backup,
            target.path(),
            &RestoreOptions {
                credential: BackupCredential::None,
                mode: RestoreMode::Full,
            },
        )
        .unwrap();
    assert!(report.verified);
    assert_eq!(report.restored_profiles.len(), 2);

    let restored = Store::open(target.path()).unwrap();
    assert_eq!(restored.profiles().count().unwrap(), 2);
    for (id, suffix) in [(&p1, "one"), (&p2, "two")] {
        let profile = restored.open_profile(id).unwrap();
        assert_eq!(
            profile.settings().get("theme").unwrap().unwrap().value,
            SettingValue::Text(suffix.into())
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
        assert_eq!(
            profile.secrets().get("api.key", &key).unwrap().unwrap(),
            suffix.as_bytes()
        );
        assert_eq!(profile.library().list(10, 0).unwrap().total, 1);
        assert_eq!(profile.conversations().list(true, 10, 0).unwrap().total, 1);
        assert_eq!(profile.workspaces().list(true, 10, 0).unwrap().total, 1);
        assert_eq!(profile.tasks().list(1, true, 10, 0).unwrap().total, 1);
        assert_eq!(
            profile
                .task_memories()
                .list_for_task(1, true, 10, 0)
                .unwrap()
                .total,
            1
        );
    }
}

#[test]
fn single_profile_backup_and_restore_preserve_other_profiles() {
    let source = tempdir().unwrap();
    let target = tempdir().unwrap();
    let (store, p1, p2, _) = seed_store(source.path());
    let manager = BackupManager::new(source.path()).unwrap();
    let record = manager
        .create_backup(
            &store,
            &BackupOptions {
                include_root: false,
                profiles: Some(vec![p1.clone()]),
                ..BackupOptions::default()
            },
        )
        .unwrap();
    assert_eq!(record.profiles, vec!["p1".to_string()]);

    let target_store = Store::open(target.path()).unwrap();
    target_store
        .create_profile(&p1, "Target p1", false)
        .unwrap();
    target_store
        .create_profile(&p2, "Target p2", false)
        .unwrap();
    target_store
        .open_profile(&p1)
        .unwrap()
        .settings()
        .set(
            "theme",
            "appearance",
            SettingValue::Text("target-old".into()),
        )
        .unwrap();
    target_store
        .open_profile(&p2)
        .unwrap()
        .settings()
        .set("theme", "appearance", SettingValue::Text("p2-old".into()))
        .unwrap();

    let backup = manager.backup_dir().join(&record.file_name);
    manager
        .restore(
            &backup,
            target.path(),
            &RestoreOptions {
                credential: BackupCredential::None,
                mode: RestoreMode::Profiles(vec![p1.clone()]),
            },
        )
        .unwrap();

    let restored = Store::open(target.path()).unwrap();
    assert_eq!(
        restored
            .open_profile(&p1)
            .unwrap()
            .settings()
            .get("theme")
            .unwrap()
            .unwrap()
            .value,
        SettingValue::Text("one".into())
    );
    assert_eq!(
        restored
            .open_profile(&p2)
            .unwrap()
            .settings()
            .get("theme")
            .unwrap()
            .unwrap()
            .value,
        SettingValue::Text("p2-old".into())
    );
}

#[test]
fn data_exclusion_is_reflected_in_backup() {
    let source = tempdir().unwrap();
    let target = tempdir().unwrap();
    let (store, p1, _, _) = seed_store(source.path());
    let manager = BackupManager::new(source.path()).unwrap();
    let record = manager
        .create_backup(
            &store,
            &BackupOptions {
                data: BackupData {
                    history: false,
                    secrets: false,
                    ..BackupData::default()
                },
                ..BackupOptions::default()
            },
        )
        .unwrap();
    assert!(!record.contains_secrets);
    let backup = manager.backup_dir().join(&record.file_name);
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
    let profile = restored.open_profile(&p1).unwrap();
    assert_eq!(profile.history().list(10, 0).unwrap().total, 0);
    assert!(profile.secrets().list_keys(None).unwrap().is_empty());
    assert_eq!(
        profile
            .bookmarks()
            .list(FolderScope::All, 10, 0)
            .unwrap()
            .total,
        1
    );
}

#[test]
fn password_encryption_requires_correct_credential_and_checksum() {
    let source = tempdir().unwrap();
    let target = tempdir().unwrap();
    let (store, _p1, _, _) = seed_store(source.path());
    let manager = BackupManager::new(source.path()).unwrap();
    let record = manager
        .create_backup(
            &store,
            &BackupOptions {
                encryption: BackupEncryption::Password("correct horse".into()),
                ..BackupOptions::default()
            },
        )
        .unwrap();
    let backup = manager.backup_dir().join(&record.file_name);
    assert!(record.encrypted);
    assert!(manager.verify(&backup, &BackupCredential::None).is_err());
    assert!(
        manager
            .verify(&backup, &BackupCredential::Password("wrong".into()))
            .is_err()
    );
    let manifest = manager
        .verify(&backup, &BackupCredential::Password("correct horse".into()))
        .unwrap();
    assert!(manifest.encryption.starts_with("password"));

    let mut bytes = fs::read(&backup).unwrap();
    let last = bytes.len() - 1;
    bytes[last] ^= 1;
    fs::write(&backup, &bytes).unwrap();
    assert!(
        manager
            .verify(&backup, &BackupCredential::Password("correct horse".into()))
            .is_err()
    );

    // Target is untouched by a failed verification/restore.
    fs::write(backup.with_extension("bad"), b"not a backup").unwrap();
    let _ = manager.restore(
        backup.with_extension("bad"),
        target.path(),
        &RestoreOptions {
            credential: BackupCredential::None,
            mode: RestoreMode::Full,
        },
    );
    assert!(!target.path().join("amare.db").exists());
}

#[test]
fn key_file_can_encrypt_backup_without_plaintext_key() {
    let source = tempdir().unwrap();
    let target = tempdir().unwrap();
    let (store, _p1, _, _) = seed_store(source.path());
    let manager = BackupManager::new(source.path()).unwrap();
    let key_dir = tempdir().unwrap();
    let key_path = key_dir.path().join("backup.key");
    let protector = InMemoryKeyProtector::new([11u8; 32]);
    let key_file = KeyFile::new(&key_path);
    let key = key_file
        .load_or_create(&protector, "amare.backup.key")
        .unwrap();
    assert!(
        !fs::read(&key_path)
            .unwrap()
            .windows(32)
            .any(|window| window == key)
    );

    let record = manager
        .create_backup(
            &store,
            &BackupOptions {
                encryption: BackupEncryption::Key(key),
                ..BackupOptions::default()
            },
        )
        .unwrap();
    let backup = manager.backup_dir().join(&record.file_name);
    manager
        .restore(
            &backup,
            target.path(),
            &RestoreOptions {
                credential: BackupCredential::Key(key),
                mode: RestoreMode::Full,
            },
        )
        .unwrap();
    assert!(
        Store::open(target.path())
            .unwrap()
            .profiles()
            .count()
            .unwrap()
            >= 1
    );
}

#[test]
fn unverified_failures_do_not_replace_existing_target() {
    let source = tempdir().unwrap();
    let target = tempdir().unwrap();
    let (store, p1, _, _) = seed_store(source.path());
    let manager = BackupManager::new(source.path()).unwrap();
    let target_store = Store::open(target.path()).unwrap();
    target_store.create_profile(&p1, "Target", false).unwrap();
    target_store
        .open_profile(&p1)
        .unwrap()
        .settings()
        .set("keep", "general", SettingValue::Text("original".into()))
        .unwrap();

    let record = manager
        .create_backup(&store, &BackupOptions::default())
        .unwrap();
    let backup = manager.backup_dir().join(&record.file_name);
    let mut bytes = fs::read(&backup).unwrap();
    let last = bytes.len() - 1;
    bytes[last] ^= 1;
    fs::write(&backup, bytes).unwrap();

    let result = manager.restore(
        &backup,
        target.path(),
        &RestoreOptions {
            credential: BackupCredential::None,
            mode: RestoreMode::Full,
        },
    );
    assert!(result.is_err());
    let reopened = Store::open(target.path()).unwrap();
    assert_eq!(
        reopened
            .open_profile(&p1)
            .unwrap()
            .settings()
            .get("keep")
            .unwrap()
            .unwrap()
            .value,
        SettingValue::Text("original".into())
    );
}

#[test]
fn list_delete_prune_and_auto_policy() {
    let source = tempdir().unwrap();
    let (store, _, _, _) = seed_store(source.path());
    let manager = BackupManager::new(source.path()).unwrap();
    for _ in 0..3 {
        manager
            .create_backup(&store, &BackupOptions::default())
            .unwrap();
    }
    let mut records = manager.list().unwrap();
    assert_eq!(records.len(), 3);
    records.sort_by_key(|record| record.created_at_ms);
    let removed = manager.prune(2).unwrap();
    assert_eq!(removed.len(), 1);
    assert_eq!(manager.list().unwrap().len(), 2);

    let first = manager.list().unwrap()[0].clone();
    manager
        .delete(manager.backup_dir().join(&first.file_name))
        .unwrap();
    assert_eq!(manager.list().unwrap().len(), 1);

    let policy = BackupPolicy {
        interval_ms: 0,
        keep: 1,
    };
    let record = manager
        .auto_backup_if_due(&store, &BackupOptions::default(), &policy)
        .unwrap();
    assert!(record.is_some());
    let no_duplicate = manager
        .auto_backup_if_due(
            &store,
            &BackupOptions::default(),
            &BackupPolicy {
                interval_ms: 60_000,
                keep: 1,
            },
        )
        .unwrap();
    assert!(no_duplicate.is_none());
}

#[test]
fn integrity_checks_preserve_database_files() {
    let source = tempdir().unwrap();
    let (store, p1, _, _) = seed_store(source.path());
    assert!(check_store(source.path()).iter().all(|report| report.ok));
    assert!(check_database(store.profile_db_path(&p1).unwrap()).ok);
    drop(store);

    let corrupt_path = source.path().join("profiles/p1/corrupt.db");
    fs::write(&corrupt_path, b"not a sqlite database").unwrap();
    let report = check_database(&corrupt_path);
    assert!(!report.ok);
    assert!(corrupt_path.exists());
    assert!(report.exists);
}

#[test]
fn future_schema_backup_is_rejected_before_target_changes() {
    let source = tempdir().unwrap();
    let target = tempdir().unwrap();
    let (store, p1, _, _) = seed_store(source.path());
    let manager = BackupManager::new(source.path()).unwrap();
    let record = manager
        .create_backup(
            &store,
            &BackupOptions {
                profiles: Some(vec![p1.clone()]),
                ..BackupOptions::default()
            },
        )
        .unwrap();
    let backup = manager.backup_dir().join(&record.file_name);
    let mut bytes = fs::read(&backup).unwrap();
    bytes[8] = 99;
    bytes[9] = 0;
    fs::write(&backup, bytes).unwrap();
    let result = manager.restore(
        &backup,
        target.path(),
        &RestoreOptions {
            credential: BackupCredential::None,
            mode: RestoreMode::Full,
        },
    );
    assert!(result.is_err());
    assert!(!target.path().join("amare.db").exists());
}

#[test]
fn phase_1_6_backup_exclusion_and_manifest_kinds() {
    let source = tempdir().unwrap();
    let target = tempdir().unwrap();
    let (store, p1, _, _) = seed_store(source.path());
    let manager = BackupManager::new(source.path()).unwrap();
    let record = manager
        .create_backup(
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
        )
        .unwrap();
    for kind in ["settings", "bookmarks", "history", "sessions", "secrets"] {
        assert!(
            record.data_kinds.iter().any(|value| value == kind),
            "{kind}"
        );
    }
    for kind in ["library", "conversations", "workspaces"] {
        assert!(
            !record.data_kinds.iter().any(|value| value == kind),
            "{kind}"
        );
    }

    let backup = manager.backup_dir().join(&record.file_name);
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
    let profile = restored.open_profile(&p1).unwrap();
    assert_eq!(profile.library().list(10, 0).unwrap().total, 0);
    assert_eq!(
        profile
            .sources()
            .list_for_library_item(1, 10, 0)
            .unwrap()
            .total,
        0
    );
    assert!(profile.sources().get(1).unwrap().is_none());
    assert_eq!(profile.conversations().list(true, 10, 0).unwrap().total, 0);
    assert_eq!(profile.workspaces().list(true, 10, 0).unwrap().total, 0);
    assert_eq!(profile.tasks().list(1, true, 10, 0).unwrap().total, 0);
    assert_eq!(
        profile
            .bookmarks()
            .list(FolderScope::All, 10, 0)
            .unwrap()
            .total,
        1
    );
}

#[test]
fn parser_rejects_oversized_container_and_manifest_lengths() {
    let source = tempdir().unwrap();
    let (store, _, _, _) = seed_store(source.path());
    let manager = BackupManager::new(source.path()).unwrap();
    let record = manager
        .create_backup(
            &store,
            &BackupOptions {
                compression: false,
                ..BackupOptions::default()
            },
        )
        .unwrap();
    let backup = manager.backup_dir().join(&record.file_name);
    let bytes = fs::read(&backup).unwrap();
    fs::remove_file(format!("{}.sha256", backup.display())).unwrap();

    let mut oversized_container = bytes.clone();
    oversized_container[12..20].copy_from_slice(&(3_u64 * 1024 * 1024 * 1024).to_le_bytes());
    fs::write(&backup, &oversized_container).unwrap();
    assert!(manager.verify(&backup, &BackupCredential::None).is_err());

    let mut oversized_manifest = bytes;
    oversized_manifest[20..24].copy_from_slice(&(5_u32 * 1024 * 1024).to_le_bytes());
    fs::write(&backup, &oversized_manifest).unwrap();
    assert!(manager.verify(&backup, &BackupCredential::None).is_err());
}

#[test]
fn library_exclusion_keeps_ai_citations_when_conversations_are_included() {
    let source = tempdir().unwrap();
    let target = tempdir().unwrap();
    let (store, p1, _, _) = seed_store(source.path());
    let manager = BackupManager::new(source.path()).unwrap();
    let record = manager
        .create_backup(
            &store,
            &BackupOptions {
                data: BackupData {
                    library: false,
                    ..BackupData::default()
                },
                ..BackupOptions::default()
            },
        )
        .unwrap();
    let backup = manager.backup_dir().join(&record.file_name);
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
    let profile = restored.open_profile(&p1).unwrap();
    assert_eq!(profile.library().list(10, 0).unwrap().total, 0);
    assert_eq!(profile.library().list_tags().unwrap().len(), 0);
    let source = profile.sources().get_required(1).unwrap();
    assert_eq!(source.library_item_id, None);
    assert_eq!(profile.conversations().list(true, 10, 0).unwrap().total, 1);
}

#[test]
fn profile_icon_survives_full_backup_and_restore() {
    let source = tempdir().unwrap();
    let target = tempdir().unwrap();
    let store = Store::open(source.path()).unwrap();
    let id = ProfileId::new("p-icon");
    store.create_profile(&id, "Icon Profile", true).unwrap();
    store
        .profiles()
        .set_icon(&id, Some("asset:icon/research"))
        .unwrap();
    let manager = BackupManager::new(source.path()).unwrap();
    let record = manager
        .create_backup(&store, &BackupOptions::default())
        .unwrap();
    manager
        .restore(
            manager.backup_dir().join(&record.file_name),
            target.path(),
            &RestoreOptions {
                credential: BackupCredential::None,
                mode: RestoreMode::Full,
            },
        )
        .unwrap();
    let restored = Store::open(target.path()).unwrap();
    let profile = restored.profiles().get_required(&id).unwrap();
    assert_eq!(profile.icon.as_deref(), Some("asset:icon/research"));
}
