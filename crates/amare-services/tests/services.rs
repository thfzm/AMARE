use amare_core::{
    LibraryKind, MemoryType, MessageRole, MessageStatus, MessageVisibility, NewBookmark,
    NewConversation, NewHistoryVisit, NewLibraryItem, NewMessage, NewTask, NewTaskMemory,
    NewWorkspace, ProfileId, SearchKind, SettingValue, TaskStatus,
};
use amare_services::{
    ProfileContext, ProfileService, SearchRequest, SearchService, SearchSort, ServiceError,
    SettingGroup, SettingSource, SettingsService, definition, definitions,
};
use amare_store::{
    BackupCredential, BackupManager, BackupOptions, RestoreMode, RestoreOptions, Store,
};
use std::fs;
use tempfile::tempdir;

fn setup() -> (tempfile::TempDir, ProfileService, ProfileContext) {
    let dir = tempdir().unwrap();
    let service = ProfileService::open(dir.path()).unwrap();
    let context = service.initialize().unwrap();
    (dir, service, context)
}

fn library_item(title: &str) -> NewLibraryItem {
    NewLibraryItem {
        title: title.into(),
        description: format!("{title} description"),
        kind: LibraryKind::Research,
        original_url: Some("https://example.com/article".into()),
        source_name: Some("Example".into()),
        image_ref: None,
        user_note: String::new(),
        favorite: false,
    }
}

fn seed_search_data(service: &ProfileService, context: &ProfileContext) {
    let profile = service.profile_store(context).unwrap();
    profile
        .library()
        .create(&library_item("Needle library"))
        .unwrap();
    profile.library().create_tag("Needle tag").unwrap();
    profile
        .library()
        .create_collection("Needle collection", "")
        .unwrap();
    let conversation = profile
        .conversations()
        .create(&NewConversation {
            title: "Needle conversation".into(),
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
                body: "Needle message".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::User,
                parts: Vec::new(),
            },
        )
        .unwrap();
    let workspace = profile
        .workspaces()
        .create(&NewWorkspace {
            name: "Needle workspace".into(),
            description: String::new(),
            icon: None,
        })
        .unwrap();
    let task = profile
        .tasks()
        .create(&NewTask {
            workspace_id: workspace.id,
            title: "Needle task".into(),
            description: String::new(),
            status: TaskStatus::Open,
        })
        .unwrap();
    profile
        .task_memories()
        .create(&NewTaskMemory {
            task_id: task.id,
            title: "Needle memory".into(),
            content: "Needle content".into(),
            memory_type: MemoryType::Fact,
            source_reference_id: None,
            importance: 2,
            user_confirmed: true,
        })
        .unwrap();
    profile
        .bookmarks()
        .create(NewBookmark {
            title: "Needle bookmark".into(),
            url: "https://example.com/needle".into(),
            folder_id: None,
            position: None,
        })
        .unwrap();
    profile
        .history()
        .record_visit(NewHistoryVisit {
            url: "https://example.com/needle-history".into(),
            title: "Needle history".into(),
            visited_at_ms: 1_000,
        })
        .unwrap();
}

// ---------------------------------------------------------------------------
// ProfileService
// ---------------------------------------------------------------------------

#[test]
fn open_without_profiles_has_no_active_context() {
    let dir = tempdir().unwrap();
    let service = ProfileService::open(dir.path()).unwrap();
    assert!(service.current_context().unwrap().is_none());
    assert!(service.active_profile().unwrap().is_none());
    assert_eq!(service.list(10, 0).unwrap().total, 0);
}

#[test]
fn initialize_creates_default_active_profile() {
    let (_dir, service, context) = setup();
    let profile = service.active_profile().unwrap().unwrap();
    assert_eq!(profile.name, "Personal");
    assert!(profile.is_default);
    assert_eq!(profile.id, context.profile_id);
    assert_eq!(service.list(10, 0).unwrap().total, 1);
    assert!(context.generation() >= 1);
}

#[test]
fn create_rename_icon_and_default_profile() {
    let (_dir, service, _context) = setup();
    let work = ProfileId::new("work");
    let created = service
        .create_profile(&work, "Work", Some("asset:briefcase"), false)
        .unwrap();
    assert_eq!(created.icon.as_deref(), Some("asset:briefcase"));
    assert!(!created.is_default);

    let renamed = service.rename_profile(&work, "Work profile").unwrap();
    assert_eq!(renamed.name, "Work profile");
    let icon = service
        .set_profile_icon(&work, Some("asset:office"))
        .unwrap();
    assert_eq!(icon.icon.as_deref(), Some("asset:office"));
    assert!(service.set_default_profile(&work).unwrap().is_default);
    assert!(
        !service
            .get(&ProfileId::new("default"))
            .unwrap()
            .unwrap()
            .is_default
    );
}

#[test]
fn create_rejects_invalid_profile_ids() {
    let (_dir, service, _context) = setup();
    let result = service.create_profile(&ProfileId::new("../escape"), "Bad", None, false);
    assert!(matches!(
        result,
        Err(ServiceError::Store(amare_store::StoreError::InvalidInput(
            _
        )))
    ));
    assert!(matches!(
        service.set_profile_icon(&ProfileId::new("default"), Some(" ")),
        Err(ServiceError::InvalidInput(_))
    ));
}

#[test]
fn switch_changes_generation_and_stale_context_is_rejected() {
    let (_dir, service, first) = setup();
    service
        .create_profile(&ProfileId::new("work"), "Work", None, false)
        .unwrap();
    let second = service.switch_profile(&ProfileId::new("work")).unwrap();
    assert_ne!(first.generation(), second.generation());
    assert!(matches!(
        service.profile_store(&first),
        Err(ServiceError::ProfileSwitched)
    ));
    let settings = SettingsService::new(&service);
    assert!(matches!(
        settings.get(&first, "theme"),
        Err(ServiceError::ProfileSwitched)
    ));
    let search = SearchService::new(&service);
    assert!(matches!(
        search.search(&first, &SearchRequest::new("x")),
        Err(ServiceError::ProfileSwitched)
    ));
}

#[test]
fn profile_store_isolates_settings_and_library_data() {
    let (_dir, service, first) = setup();
    let settings = SettingsService::new(&service);
    settings
        .set(&first, "theme", SettingValue::Text("dark".into()))
        .unwrap();
    service
        .profile_store(&first)
        .unwrap()
        .library()
        .create(&library_item("only in first"))
        .unwrap();

    service
        .create_profile(&ProfileId::new("second"), "Second", None, false)
        .unwrap();
    let second = service.switch_profile(&ProfileId::new("second")).unwrap();
    assert_eq!(
        settings.get(&second, "theme").unwrap().source,
        SettingSource::Default
    );
    assert_eq!(
        service
            .profile_store(&second)
            .unwrap()
            .library()
            .list(10, 0)
            .unwrap()
            .total,
        0
    );

    let first_again = service.switch_profile(&first.profile_id).unwrap();
    assert_eq!(
        settings.get(&first_again, "theme").unwrap().value,
        SettingValue::Text("dark".into())
    );
    assert_eq!(
        service
            .profile_store(&first_again)
            .unwrap()
            .library()
            .list(10, 0)
            .unwrap()
            .total,
        1
    );
}

#[test]
fn delete_last_profile_is_rejected() {
    let (_dir, service, context) = setup();
    assert!(matches!(
        service.delete_profile(&context.profile_id),
        Err(ServiceError::LastProfile)
    ));
    assert_eq!(service.list(10, 0).unwrap().total, 1);
}

#[test]
fn delete_non_active_profile_keeps_active_context() {
    let (_dir, service, context) = setup();
    let work = ProfileId::new("work");
    service.create_profile(&work, "Work", None, false).unwrap();
    service.delete_profile(&work).unwrap();
    assert_eq!(service.current_context().unwrap().unwrap(), context);
    assert_eq!(service.list(10, 0).unwrap().total, 1);
}

#[test]
fn delete_active_profile_promotes_default_and_invalidates_context() {
    let (_dir, service, _context) = setup();
    let work = ProfileId::new("work");
    service.create_profile(&work, "Work", None, false).unwrap();
    let work_context = service.switch_profile(&work).unwrap();
    service.delete_profile(&work).unwrap();
    let active = service.active_profile().unwrap().unwrap();
    assert_eq!(active.id, ProfileId::new("default"));
    assert!(matches!(
        service.profile_store(&work_context),
        Err(ServiceError::ProfileSwitched)
    ));
}

#[test]
fn tombstoned_profile_is_hidden_and_blocks_access_until_finalized() {
    let (dir, service, context) = setup();
    service
        .create_profile(&ProfileId::new("work"), "Work", None, false)
        .unwrap();
    let profile_dir = dir.path().join("profiles").join("work");
    let tombstone = dir.path().join("profiles").join("work.deleting");
    fs::rename(&profile_dir, &tombstone).unwrap();

    assert!(service.get(&ProfileId::new("work")).unwrap().is_none());
    assert!(
        service
            .list(10, 0)
            .unwrap()
            .items
            .iter()
            .all(|p| p.id.as_str() != "work")
    );
    assert!(matches!(
        service.switch_profile(&ProfileId::new("work")),
        Err(ServiceError::Store(
            amare_store::StoreError::ProfileDeleting(_)
        ))
    ));
    assert!(matches!(
        service.create_profile(&ProfileId::new("work"), "Again", None, false),
        Err(ServiceError::Store(
            amare_store::StoreError::ProfileDeleting(_)
        ))
    ));

    service.delete_profile(&ProfileId::new("work")).unwrap();
    assert!(!tombstone.exists());
    assert!(service.get(&ProfileId::new("work")).unwrap().is_none());
    assert_eq!(service.current_context().unwrap().unwrap(), context);
}

#[test]
fn initialize_repairs_missing_default_profile_flag() {
    let dir = tempdir().unwrap();
    let service = ProfileService::open(dir.path()).unwrap();
    let first = ProfileId::new("first");
    let second = ProfileId::new("second");
    service
        .create_profile(&first, "First", None, false)
        .unwrap();
    service
        .create_profile(&second, "Second", None, false)
        .unwrap();
    let context = service.initialize().unwrap();
    assert_eq!(context.profile_id, first);
    assert!(service.get(&first).unwrap().unwrap().is_default);
    assert!(!service.get(&second).unwrap().unwrap().is_default);
}

#[test]
fn reset_profile_clears_data_but_keeps_identity() {
    let (_dir, service, context) = setup();
    let profile = service.profile_store(&context).unwrap();
    profile.library().create(&library_item("reset me")).unwrap();
    profile
        .settings()
        .set("theme", "appearance", SettingValue::Text("dark".into()))
        .unwrap();
    drop(profile);

    service.reset_profile(&context).unwrap();
    let profile = service.profile_store(&context).unwrap();
    assert_eq!(profile.library().list(10, 0).unwrap().total, 0);
    assert!(profile.settings().get("theme").unwrap().is_none());
    assert_eq!(
        service.active_profile().unwrap().unwrap().id,
        context.profile_id
    );
    assert_eq!(
        service
            .profile_store(&context)
            .unwrap()
            .schema_version()
            .unwrap(),
        3
    );
}

#[test]
fn list_paginates_profiles() {
    let (_dir, service, _context) = setup();
    for index in 1..4 {
        service
            .create_profile(&ProfileId::new(format!("p{index}")), "P", None, false)
            .unwrap();
    }
    let page = service.list(2, 1).unwrap();
    assert_eq!(page.total, 4);
    assert_eq!(page.items.len(), 2);
    assert_eq!(page.limit, 2);
    assert_eq!(page.offset, 1);
}

// ---------------------------------------------------------------------------
// SettingsService
// ---------------------------------------------------------------------------

#[test]
fn settings_defaults_and_groups_are_resolved() {
    let (_dir, service, context) = setup();
    let settings = SettingsService::new(&service);
    let theme = settings.get(&context, "theme").unwrap();
    assert_eq!(theme.source, SettingSource::Default);
    assert_eq!(theme.value, SettingValue::Text("system".into()));
    assert_eq!(theme.group, SettingGroup::Appearance);
    assert_eq!(
        settings
            .list_group(&context, SettingGroup::Appearance)
            .unwrap()
            .len(),
        1
    );
    assert_eq!(
        settings.list_all(&context).unwrap().len(),
        definitions().len()
    );
    for group in SettingGroup::ALL {
        assert!(definitions().iter().any(|def| def.group == group));
    }
}

#[test]
fn settings_set_marks_user_value_and_persists() {
    let (_dir, service, context) = setup();
    let settings = SettingsService::new(&service);
    let changes = settings
        .set(&context, "theme", SettingValue::Text("dark".into()))
        .unwrap();
    assert_eq!(changes.changed_count(), 1);
    assert_eq!(changes.changes[0].old, None);
    assert_eq!(changes.changes[0].new, SettingValue::Text("dark".into()));
    let resolved = settings.get(&context, "theme").unwrap();
    assert_eq!(resolved.source, SettingSource::User);
    assert_eq!(resolved.value, SettingValue::Text("dark".into()));
    drop(service);

    let service = ProfileService::open(_dir.path()).unwrap();
    let context = service.current_context().unwrap().unwrap();
    let settings = SettingsService::new(&service);
    assert_eq!(
        settings.get(&context, "theme").unwrap().value,
        SettingValue::Text("dark".into())
    );
}

#[test]
fn settings_set_many_is_atomic_and_reports_changes() {
    let (_dir, service, context) = setup();
    let settings = SettingsService::new(&service);
    let changes = settings
        .set_many(
            &context,
            &[
                ("theme", SettingValue::Text("light".into())),
                ("engine", SettingValue::Text("google".into())),
            ],
        )
        .unwrap();
    assert_eq!(changes.changed_count(), 2);
    assert_eq!(
        settings.get(&context, "engine").unwrap().value,
        SettingValue::Text("google".into())
    );
}

#[test]
fn settings_atomic_failure_writes_nothing() {
    let (_dir, service, context) = setup();
    let settings = SettingsService::new(&service);
    let result = settings.set_many(
        &context,
        &[
            ("theme", SettingValue::Text("dark".into())),
            ("engine", SettingValue::Text("not-a-search-engine".into())),
        ],
    );
    assert!(matches!(result, Err(ServiceError::SettingOutOfRange(_))));
    assert_eq!(
        settings.get(&context, "theme").unwrap().source,
        SettingSource::Default
    );
}

#[test]
fn settings_reject_duplicate_keys_in_one_change() {
    let (_dir, service, context) = setup();
    let settings = SettingsService::new(&service);
    let result = settings.set_many(
        &context,
        &[
            ("theme", SettingValue::Text("dark".into())),
            ("theme", SettingValue::Text("light".into())),
        ],
    );
    assert!(matches!(result, Err(ServiceError::DuplicateSetting(_))));
}

#[test]
fn settings_reject_sensitive_and_unknown_keys() {
    let (_dir, service, context) = setup();
    let settings = SettingsService::new(&service);
    for key in ["api_key", "password", "auth_token", "private_key"] {
        assert!(matches!(
            settings.set(&context, key, SettingValue::Text("x".into())),
            Err(ServiceError::SensitiveSetting(_))
        ));
    }
    assert!(matches!(
        settings.set(&context, "unknown.key", SettingValue::Text("x".into())),
        Err(ServiceError::UnknownSetting(_))
    ));
}

#[test]
fn settings_validate_type_choice_and_rule() {
    let (_dir, service, context) = setup();
    let settings = SettingsService::new(&service);
    assert!(matches!(
        settings.set(&context, "theme", SettingValue::Integer(1)),
        Err(ServiceError::InvalidSettingType(_))
    ));
    assert!(matches!(
        settings.set(&context, "theme", SettingValue::Text("purple".into())),
        Err(ServiceError::SettingOutOfRange(_))
    ));
    assert!(matches!(
        settings.set(&context, "telemetry", SettingValue::Text("false".into())),
        Err(ServiceError::InvalidSettingType(_))
    ));
}

#[test]
fn settings_reset_key_group_and_all() {
    let (_dir, service, context) = setup();
    let settings = SettingsService::new(&service);
    settings
        .set(&context, "theme", SettingValue::Text("dark".into()))
        .unwrap();
    settings
        .set(&context, "engine", SettingValue::Text("google".into()))
        .unwrap();
    let reset = settings.reset_key(&context, "theme").unwrap();
    assert_eq!(reset.changes[0].new, SettingValue::Text("system".into()));
    assert_eq!(
        settings.get(&context, "theme").unwrap().source,
        SettingSource::Default
    );
    assert_eq!(
        settings.get(&context, "engine").unwrap().source,
        SettingSource::User
    );

    settings
        .reset_group(&context, SettingGroup::Search)
        .unwrap();
    assert_eq!(
        settings.get(&context, "engine").unwrap().source,
        SettingSource::Default
    );
    settings
        .set(&context, "theme", SettingValue::Text("light".into()))
        .unwrap();
    let all = settings.reset_all(&context).unwrap();
    assert_eq!(all.changed_count(), definitions().len());
    assert_eq!(
        settings.get(&context, "theme").unwrap().source,
        SettingSource::Default
    );
}

#[test]
fn settings_are_isolated_between_profiles() {
    let (_dir, service, first) = setup();
    service
        .create_profile(&ProfileId::new("second"), "Second", None, false)
        .unwrap();
    let settings = SettingsService::new(&service);
    settings
        .set(&first, "theme", SettingValue::Text("dark".into()))
        .unwrap();
    let second = service.switch_profile(&ProfileId::new("second")).unwrap();
    assert_eq!(
        settings.get(&second, "theme").unwrap().source,
        SettingSource::Default
    );
    let first_again = service.switch_profile(&first.profile_id).unwrap();
    assert_eq!(
        settings.get(&first_again, "theme").unwrap().value,
        SettingValue::Text("dark".into())
    );
}

#[test]
fn settings_concurrent_writes_use_wal() {
    use std::thread;
    let (_dir, service, context) = setup();
    let settings = SettingsService::new(&service);
    thread::scope(|scope| {
        let context_a = context.clone();
        let context_b = context.clone();
        scope.spawn(move || {
            settings
                .set(&context_a, "theme", SettingValue::Text("dark".into()))
                .unwrap();
        });
        scope.spawn(move || {
            settings
                .set(&context_b, "engine", SettingValue::Text("google".into()))
                .unwrap();
        });
    });
    assert_eq!(
        settings.get(&context, "theme").unwrap().value,
        SettingValue::Text("dark".into())
    );
    assert_eq!(
        settings.get(&context, "engine").unwrap().value,
        SettingValue::Text("google".into())
    );
}

#[test]
fn setting_definition_lookup_is_stable() {
    assert_eq!(definition("theme").unwrap().group, SettingGroup::Appearance);
    assert!(definition("missing").is_none());
    assert_eq!(SettingGroup::parse("privacy"), Some(SettingGroup::Privacy));
    assert_eq!(SettingGroup::parse("unknown"), None);
}

// ---------------------------------------------------------------------------
// SearchService
// ---------------------------------------------------------------------------

#[test]
fn search_returns_all_ten_supported_kinds_with_ids() {
    let (_dir, service, context) = setup();
    seed_search_data(&service, &context);
    let page = SearchService::new(&service)
        .search(&context, &SearchRequest::new("needle"))
        .unwrap();
    assert_eq!(page.total, 10);
    assert_eq!(page.items.len(), 10);
    assert!(page.items.iter().all(|hit| hit.id > 0));
    let kinds: std::collections::BTreeSet<_> = page
        .items
        .iter()
        .map(|hit| format!("{:?}", hit.kind))
        .collect();
    for kind in [
        SearchKind::LibraryItem,
        SearchKind::Tag,
        SearchKind::Collection,
        SearchKind::Conversation,
        SearchKind::Message,
        SearchKind::Workspace,
        SearchKind::Task,
        SearchKind::TaskMemory,
        SearchKind::Bookmark,
        SearchKind::History,
    ] {
        assert!(kinds.contains(&format!("{:?}", kind)), "{kind:?}");
    }
}

#[test]
fn search_filters_by_result_kind() {
    let (_dir, service, context) = setup();
    seed_search_data(&service, &context);
    let page = SearchService::new(&service)
        .search(
            &context,
            &SearchRequest::new("needle")
                .with_kinds([SearchKind::LibraryItem, SearchKind::Bookmark]),
        )
        .unwrap();
    assert_eq!(page.total, 2);
    assert_eq!(page.items.len(), 2);
    assert!(
        page.items
            .iter()
            .all(|hit| matches!(hit.kind, SearchKind::LibraryItem | SearchKind::Bookmark))
    );
}

#[test]
fn search_includes_bookmarks_and_history() {
    let (_dir, service, context) = setup();
    seed_search_data(&service, &context);
    let page = SearchService::new(&service)
        .search(
            &context,
            &SearchRequest::new("needle").with_kinds([SearchKind::Bookmark, SearchKind::History]),
        )
        .unwrap();
    assert_eq!(page.total, 2);
    assert!(
        page.items
            .iter()
            .any(|hit| hit.kind == SearchKind::Bookmark)
    );
    assert!(page.items.iter().any(|hit| hit.kind == SearchKind::History));
}

#[test]
fn search_pagination_total_and_slice() {
    let (_dir, service, context) = setup();
    seed_search_data(&service, &context);
    let page = SearchService::new(&service)
        .search(&context, &SearchRequest::new("needle").with_page(3, 2))
        .unwrap();
    assert_eq!(page.total, 10);
    assert_eq!(page.items.len(), 3);
    assert_eq!(page.limit, 3);
    assert_eq!(page.offset, 2);
}

#[test]
fn search_sorts_by_title() {
    let (_dir, service, context) = setup();
    seed_search_data(&service, &context);
    let page = SearchService::new(&service)
        .search(
            &context,
            &SearchRequest::new("needle")
                .with_kinds([SearchKind::LibraryItem, SearchKind::Bookmark])
                .with_sort(SearchSort::TitleAsc),
        )
        .unwrap();
    assert_eq!(page.items[0].kind, SearchKind::Bookmark);
    assert_eq!(page.items[0].title, "Needle bookmark");
}

#[test]
fn search_sorts_by_updated_time() {
    let (_dir, service, context) = setup();
    seed_search_data(&service, &context);
    let page = SearchService::new(&service)
        .search(
            &context,
            &SearchRequest::new("needle")
                .with_kinds([SearchKind::Bookmark, SearchKind::History])
                .with_sort(SearchSort::UpdatedDesc),
        )
        .unwrap();
    assert_eq!(page.items[0].kind, SearchKind::Bookmark);
}

#[test]
fn search_rejects_empty_and_too_long_query() {
    let (_dir, service, context) = setup();
    let search = SearchService::new(&service);
    assert!(matches!(
        search.search(&context, &SearchRequest::new("   ")),
        Err(ServiceError::EmptyQuery)
    ));
    assert!(matches!(
        search.search(&context, &SearchRequest::new("x".repeat(513))),
        Err(ServiceError::QueryTooLong { .. })
    ));
}

#[test]
fn search_validates_limit_and_offset() {
    let (_dir, service, context) = setup();
    let search = SearchService::new(&service);
    assert!(matches!(
        search.search(&context, &SearchRequest::new("x").with_page(0, 0)),
        Err(ServiceError::InvalidLimit { .. })
    ));
    assert!(matches!(
        search.search(&context, &SearchRequest::new("x").with_page(101, 0)),
        Err(ServiceError::InvalidLimit { .. })
    ));
    assert!(matches!(
        search.search(&context, &SearchRequest::new("x").with_page(10, 401)),
        Err(ServiceError::OffsetTooLarge { .. })
    ));
}

#[test]
fn search_handles_special_characters_as_literal_text() {
    let (_dir, service, context) = setup();
    service
        .profile_store(&context)
        .unwrap()
        .library()
        .create(&library_item("OR NOT NEAR( alpha"))
        .unwrap();
    let search = SearchService::new(&service);
    let page = search
        .search(
            &context,
            &SearchRequest::new("OR NOT NEAR(").with_kinds([SearchKind::LibraryItem]),
        )
        .unwrap();
    assert_eq!(page.total, 1);
    assert!(search.search(&context, &SearchRequest::new("\"")).is_ok());
}

#[test]
fn search_excludes_trashed_deleted_internal_and_archived_records() {
    let (_dir, service, context) = setup();
    let profile = service.profile_store(&context).unwrap();
    let item = profile
        .library()
        .create(&library_item("softneedle library"))
        .unwrap();
    profile.library().trash(item.id).unwrap();
    let conversation = profile
        .conversations()
        .create(&NewConversation {
            title: "softneedle conversation".into(),
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
                body: "softneedle message".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::User,
                parts: Vec::new(),
            },
        )
        .unwrap();
    profile.conversations().delete(conversation.id).unwrap();

    let workspace = profile
        .workspaces()
        .create(&NewWorkspace {
            name: "softneedle workspace".into(),
            description: String::new(),
            icon: None,
        })
        .unwrap();
    profile
        .workspaces()
        .set_archived(workspace.id, true)
        .unwrap();
    let task = profile
        .tasks()
        .create(&NewTask {
            workspace_id: workspace.id,
            title: "softneedle archived task".into(),
            description: String::new(),
            status: TaskStatus::Archived,
        })
        .unwrap();
    profile
        .task_memories()
        .create(&NewTaskMemory {
            task_id: task.id,
            title: "softneedle memory".into(),
            content: "x".into(),
            memory_type: MemoryType::Fact,
            source_reference_id: None,
            importance: 1,
            user_confirmed: false,
        })
        .unwrap();
    let internal_conversation = profile
        .conversations()
        .create(&NewConversation {
            title: "internal".into(),
            workspace_id: None,
            task_id: None,
        })
        .unwrap();
    profile
        .conversations()
        .add_message(
            internal_conversation.id,
            &NewMessage {
                role: MessageRole::System,
                body: "softneedle internal".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::Internal,
                parts: Vec::new(),
            },
        )
        .unwrap();
    drop(profile);

    let page = SearchService::new(&service)
        .search(&context, &SearchRequest::new("softneedle"))
        .unwrap();
    assert_eq!(page.total, 0);
    assert!(page.items.is_empty());
}

#[test]
fn search_is_isolated_between_profiles() {
    let (_dir, service, first) = setup();
    seed_search_data(&service, &first);
    service
        .create_profile(&ProfileId::new("second"), "Second", None, false)
        .unwrap();
    let second = service.switch_profile(&ProfileId::new("second")).unwrap();
    let search = SearchService::new(&service);
    assert_eq!(
        search
            .search(&second, &SearchRequest::new("needle"))
            .unwrap()
            .total,
        0
    );
    let first_again = service.switch_profile(&first.profile_id).unwrap();
    assert_eq!(
        search
            .search(&first_again, &SearchRequest::new("needle"))
            .unwrap()
            .total,
        10
    );
}

#[test]
fn search_keeps_kind_separate_when_ids_collide() {
    let (_dir, service, context) = setup();
    let profile = service.profile_store(&context).unwrap();
    profile
        .library()
        .create(&library_item("sharedneedle"))
        .unwrap();
    profile
        .bookmarks()
        .create(NewBookmark {
            title: "sharedneedle".into(),
            url: "https://example.com/shared".into(),
            folder_id: None,
            position: None,
        })
        .unwrap();
    let page = SearchService::new(&service)
        .search(
            &context,
            &SearchRequest::new("sharedneedle")
                .with_kinds([SearchKind::LibraryItem, SearchKind::Bookmark]),
        )
        .unwrap();
    assert_eq!(page.items.len(), 2);
    assert_eq!(page.items[0].id, page.items[1].id);
    assert_ne!(page.items[0].kind, page.items[1].kind);
}

#[test]
fn search_results_reference_existing_profile_rows() {
    let (_dir, service, context) = setup();
    seed_search_data(&service, &context);
    let page = SearchService::new(&service)
        .search(
            &context,
            &SearchRequest::new("needle")
                .with_kinds([SearchKind::LibraryItem, SearchKind::Bookmark]),
        )
        .unwrap();
    let profile = service.profile_store(&context).unwrap();
    for hit in page.items {
        match hit.kind {
            SearchKind::LibraryItem => {
                assert!(profile.library().get(hit.id).unwrap().is_some());
            }
            SearchKind::Bookmark => {
                assert!(profile.bookmarks().get(hit.id).unwrap().is_some());
            }
            _ => panic!("unexpected kind"),
        }
    }
}

// ---------------------------------------------------------------------------
// Backup integration
// ---------------------------------------------------------------------------

#[test]
fn service_created_data_round_trips_through_backup() {
    let (source_dir, service, context) = setup();
    let settings = SettingsService::new(&service);
    settings
        .set(&context, "theme", SettingValue::Text("dark".into()))
        .unwrap();
    service
        .profile_store(&context)
        .unwrap()
        .library()
        .create(&library_item("backup service item"))
        .unwrap();
    service
        .set_profile_icon(&context.profile_id, Some("asset:personal"))
        .unwrap();

    let root = service.root_dir().unwrap();
    let store = Store::open(&root).unwrap();
    let manager = BackupManager::new(&root).unwrap();
    let record = manager
        .create_backup(&store, &BackupOptions::default())
        .unwrap();
    let target = tempdir().unwrap();
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
    drop(store);
    drop(service);

    let restored = ProfileService::open(target.path()).unwrap();
    let context = restored.initialize().unwrap();
    let settings = SettingsService::new(&restored);
    assert_eq!(
        settings.get(&context, "theme").unwrap().value,
        SettingValue::Text("dark".into())
    );
    assert_eq!(
        restored
            .profile_store(&context)
            .unwrap()
            .library()
            .list(10, 0)
            .unwrap()
            .total,
        1
    );
    assert_eq!(
        restored.active_profile().unwrap().unwrap().icon.as_deref(),
        Some("asset:personal")
    );
    drop(source_dir);
    assert!(target.path().exists());
}
