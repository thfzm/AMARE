use amare_core::{
    LibraryKind, LibraryStatus, LibraryUpdate, MemoryType, MessagePartKind, MessageRole,
    MessageStatus, MessageVisibility, NewConversation, NewLibraryItem, NewMessage, NewMessagePart,
    NewSourceReference, NewTask, NewTaskMemory, NewWorkspace, ProfileId, SearchKind, TaskStatus,
};
use amare_store::{Store, StoreError};
use tempfile::tempdir;

fn setup() -> (tempfile::TempDir, Store) {
    let dir = tempdir().unwrap();
    let store = Store::open(dir.path()).unwrap();
    store
        .create_profile(&ProfileId::new("p1"), "Personal", true)
        .unwrap();
    (dir, store)
}

fn library_item(title: &str) -> NewLibraryItem {
    NewLibraryItem {
        title: title.into(),
        description: format!("{title} description"),
        kind: LibraryKind::WebPage,
        original_url: Some("https://example.com/article".into()),
        source_name: Some("Example".into()),
        image_ref: None,
        user_note: String::new(),
        favorite: false,
    }
}

#[test]
fn library_items_crud_fts_archive_and_purge() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let library = profile.library();

    let item = library.create(&library_item("Ownership research")).unwrap();
    assert_eq!(item.status, LibraryStatus::Active);
    let found = library.search("ownership", 10, 0).unwrap();
    assert_eq!(found.total, 1);
    assert_eq!(found.items[0].id, item.id);

    library
        .update(
            item.id,
            &LibraryUpdate {
                title: "Ownership research updated".into(),
                description: "A revised summary".into(),
                kind: LibraryKind::Research,
                original_url: Some("https://example.com/updated".into()),
                source_name: Some("Example".into()),
                image_ref: None,
                user_note: "keep".into(),
                favorite: true,
                status: LibraryStatus::Active,
                sort_order: 3,
            },
        )
        .unwrap();
    assert_eq!(library.search("ownership", 10, 0).unwrap().total, 1);
    assert_eq!(library.search("revised", 10, 0).unwrap().total, 1);
    assert!(library.search("ownership", 10, 0).unwrap().items[0].favorite);

    library
        .set_status(item.id, LibraryStatus::Archived)
        .unwrap();
    assert_eq!(library.list(10, 0).unwrap().total, 0);
    assert_eq!(
        library
            .list_by_status(LibraryStatus::Archived, 10, 0)
            .unwrap()
            .total,
        1
    );

    library.trash(item.id).unwrap();
    assert_eq!(library.search("ownership", 10, 0).unwrap().total, 0);
    assert!(library.purge(item.id).unwrap());
    assert!(library.get(item.id).unwrap().is_none());
}

#[test]
fn tags_collections_and_collection_listing() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let library = profile.library();
    let item = library.create(&library_item("Indexing notes")).unwrap();

    let tag = library.create_tag("  Research   Notes ").unwrap();
    assert_eq!(tag.name, "Research Notes");
    let same_tag = library.create_tag("research notes").unwrap();
    assert_eq!(same_tag.id, tag.id);
    library.attach_tag(item.id, tag.id).unwrap();
    library.attach_tag(item.id, tag.id).unwrap();
    assert_eq!(library.tags_for_item(item.id).unwrap().len(), 1);
    assert_eq!(library.list_tags().unwrap().len(), 1);

    let collection = library.create_collection("Reading", "Later").unwrap();
    library.add_to_collection(item.id, collection.id).unwrap();
    assert_eq!(library.collections_for_item(item.id).unwrap().len(), 1);
    assert_eq!(
        library
            .items_in_collection(collection.id, 10, 0)
            .unwrap()
            .items[0]
            .id,
        item.id
    );
    assert_eq!(
        profile.search().search("reading", 10).unwrap()[0].kind,
        SearchKind::Collection
    );

    library.delete_collection(collection.id).unwrap();
    assert!(library.collections_for_item(item.id).unwrap().is_empty());
    library.detach_tag(item.id, tag.id).unwrap();
    assert!(library.tags_for_item(item.id).unwrap().is_empty());
}

#[test]
fn library_source_references_survive_item_purge() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let library = profile.library();
    let sources = profile.sources();
    let item = library.create(&library_item("Cited article")).unwrap();
    let source = sources
        .create(&NewSourceReference {
            url: "https://example.com/source".into(),
            page_title: "Source title".into(),
            site_name: "Example".into(),
            quote_text: "quoted evidence".into(),
            location_info: "paragraph 2".into(),
            library_item_id: Some(item.id),
        })
        .unwrap();
    assert_eq!(
        sources.list_for_library_item(item.id, 10, 0).unwrap().total,
        1
    );

    library.purge(item.id).unwrap();
    let preserved = sources.get(source.id).unwrap().unwrap();
    assert_eq!(preserved.quote_text, "quoted evidence");
    assert_eq!(preserved.library_item_id, None);
}

#[test]
fn conversations_messages_parts_and_internal_visibility() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let conversations = profile.conversations();
    let conversation = conversations
        .create(&NewConversation {
            title: "New chat".into(),
            workspace_id: None,
            task_id: None,
        })
        .unwrap();
    assert!(matches!(
        conversations.create(&NewConversation {
            title: "".into(),
            workspace_id: None,
            task_id: None,
        }),
        Err(StoreError::InvalidInput(_))
    ));

    let user = conversations
        .add_message(
            conversation.id,
            &NewMessage {
                role: MessageRole::User,
                body: "Find primary sources about ownership".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::User,
                parts: vec![NewMessagePart {
                    kind: MessagePartKind::Text,
                    text: "Find primary sources about ownership".into(),
                    reference: None,
                    metadata_json: None,
                }],
            },
        )
        .unwrap();
    assert_eq!(user.position, 0);
    assert_eq!(user.parts.len(), 1);

    let internal = conversations
        .add_message(
            conversation.id,
            &NewMessage {
                role: MessageRole::System,
                body: "policy marker".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::Internal,
                parts: Vec::new(),
            },
        )
        .unwrap();
    let visible = conversations
        .list_messages(conversation.id, false, 10, 0)
        .unwrap();
    assert_eq!(visible.total, 1);
    assert_eq!(visible.items[0].id, user.id);
    let all = conversations
        .list_messages(conversation.id, true, 10, 0)
        .unwrap();
    assert_eq!(all.total, 2);
    assert!(all.items.iter().any(|message| message.id == internal.id));

    let updated = conversations
        .update_message(
            user.id,
            "Updated question about primary sources",
            MessageStatus::Complete,
        )
        .unwrap();
    assert_eq!(updated.body, "Updated question about primary sources");
    conversations
        .update(conversation.id, "Primary sources", None, None)
        .unwrap();
    assert_eq!(conversations.search("primary", 10, 0).unwrap().total, 1);
}

#[test]
fn conversation_soft_delete_restore_and_purge() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let conversations = profile.conversations();
    let conversation = conversations
        .create(&NewConversation {
            title: "Temporary chat".into(),
            workspace_id: None,
            task_id: None,
        })
        .unwrap();
    conversations
        .add_message(
            conversation.id,
            &NewMessage {
                role: MessageRole::User,
                body: "temporary body".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::User,
                parts: Vec::new(),
            },
        )
        .unwrap();

    conversations.delete(conversation.id).unwrap();
    assert!(
        conversations
            .get(conversation.id)
            .unwrap()
            .unwrap()
            .deleted_at_ms
            .is_some()
    );
    assert_eq!(conversations.list(true, 10, 0).unwrap().total, 0);
    assert_eq!(conversations.search("temporary", 10, 0).unwrap().total, 0);
    assert!(matches!(
        conversations.add_message(
            conversation.id,
            &NewMessage {
                role: MessageRole::User,
                body: "no".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::User,
                parts: Vec::new(),
            }
        ),
        Err(StoreError::InvalidInput(_))
    ));

    conversations.restore(conversation.id).unwrap();
    assert_eq!(conversations.list(true, 10, 0).unwrap().total, 1);
    assert!(conversations.purge(conversation.id).unwrap());
    assert!(conversations.get(conversation.id).unwrap().is_none());
}

#[test]
fn message_sources_attach_and_survive_conversation_purge() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let conversations = profile.conversations();
    let sources = profile.sources();
    let conversation = conversations
        .create(&NewConversation {
            title: "Cited chat".into(),
            workspace_id: None,
            task_id: None,
        })
        .unwrap();
    let message = conversations
        .add_message(
            conversation.id,
            &NewMessage {
                role: MessageRole::Assistant,
                body: "Answer with citation".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::User,
                parts: Vec::new(),
            },
        )
        .unwrap();
    let source = sources
        .create(&NewSourceReference {
            url: "https://example.com/cited".into(),
            page_title: "Cited".into(),
            site_name: "Example".into(),
            quote_text: "evidence".into(),
            location_info: "p. 1".into(),
            library_item_id: None,
        })
        .unwrap();
    conversations.attach_source(message.id, source.id).unwrap();
    assert_eq!(
        conversations.sources_for_message(message.id).unwrap()[0].id,
        source.id
    );
    conversations.detach_source(message.id, source.id).unwrap();
    assert!(
        conversations
            .sources_for_message(message.id)
            .unwrap()
            .is_empty()
    );

    conversations.attach_source(message.id, source.id).unwrap();
    conversations.purge(conversation.id).unwrap();
    assert_eq!(sources.get(source.id).unwrap().unwrap().url, source.url);
}

#[test]
fn private_keys_are_rejected_from_messages() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let conversations = profile.conversations();
    let conversation = conversations
        .create(&NewConversation {
            title: "Key test".into(),
            workspace_id: None,
            task_id: None,
        })
        .unwrap();
    let result = conversations.add_message(
        conversation.id,
        &NewMessage {
            role: MessageRole::User,
            body: "-----BEGIN PRIVATE KEY-----\nabc".into(),
            status: MessageStatus::Complete,
            visibility: MessageVisibility::User,
            parts: Vec::new(),
        },
    );
    assert!(matches!(result, Err(StoreError::InvalidInput(_))));
}

#[test]
fn workspaces_tasks_and_memories_crud() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let workspaces = profile.workspaces();
    let tasks = profile.tasks();
    let memories = profile.task_memories();

    let workspace = workspaces
        .create(&NewWorkspace {
            name: "Research".into(),
            description: "Long-term research".into(),
            icon: Some("book".into()),
        })
        .unwrap();
    let task = tasks
        .create(&NewTask {
            workspace_id: workspace.id,
            title: "Collect sources".into(),
            description: "Find primary sources".into(),
            status: TaskStatus::Open,
        })
        .unwrap();
    let memory = memories
        .create(&NewTaskMemory {
            task_id: task.id,
            title: "Primary source rule".into(),
            content: "Prefer primary sources for ownership claims.".into(),
            memory_type: MemoryType::UserRule,
            source_reference_id: None,
            importance: 4,
            user_confirmed: true,
        })
        .unwrap();

    assert_eq!(
        memories.list_for_task(task.id, false, 10, 0).unwrap().total,
        1
    );
    assert_eq!(workspaces.search("research", 10, 0).unwrap().total, 1);
    assert_eq!(tasks.search("sources", 10, 0).unwrap().total, 1);
    assert_eq!(memories.search("primary", 10, 0).unwrap().total, 1);

    let updated = tasks
        .update(
            task.id,
            "Collected sources",
            "Sources organized",
            TaskStatus::Done,
        )
        .unwrap();
    assert_eq!(updated.status, TaskStatus::Done);
    assert_eq!(
        memories
            .update(
                memory.id,
                "Primary source rule",
                "Prefer primary sources and cite them.",
                MemoryType::UserRule,
                5,
                true,
            )
            .unwrap()
            .importance,
        5
    );

    assert!(tasks.delete(task.id).unwrap());
    assert!(memories.get(memory.id).unwrap().is_none());
    assert!(tasks.get(task.id).unwrap().is_none());
}

#[test]
fn task_memory_sharing_is_explicit_and_reversible() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let workspaces = profile.workspaces();
    let tasks = profile.tasks();
    let memories = profile.task_memories();
    let workspace = workspaces
        .create(&NewWorkspace {
            name: "Workspace".into(),
            description: String::new(),
            icon: None,
        })
        .unwrap();
    let first = tasks
        .create(&NewTask {
            workspace_id: workspace.id,
            title: "First".into(),
            description: String::new(),
            status: TaskStatus::Open,
        })
        .unwrap();
    let second = tasks
        .create(&NewTask {
            workspace_id: workspace.id,
            title: "Second".into(),
            description: String::new(),
            status: TaskStatus::Open,
        })
        .unwrap();
    let memory = memories
        .create(&NewTaskMemory {
            task_id: first.id,
            title: "Shared fact".into(),
            content: "Only explicit shares are visible.".into(),
            memory_type: MemoryType::Fact,
            source_reference_id: None,
            importance: 2,
            user_confirmed: false,
        })
        .unwrap();

    assert_eq!(
        memories
            .list_for_task(second.id, true, 10, 0)
            .unwrap()
            .total,
        0
    );
    assert!(memories.share_with_task(memory.id, second.id).unwrap());
    assert!(!memories.share_with_task(memory.id, second.id).unwrap());
    assert_eq!(
        memories
            .list_for_task(second.id, true, 10, 0)
            .unwrap()
            .total,
        1
    );
    assert_eq!(
        memories
            .list_for_task(second.id, false, 10, 0)
            .unwrap()
            .total,
        0
    );
    assert!(matches!(
        memories.share_with_task(memory.id, first.id),
        Err(StoreError::InvalidInput(_))
    ));
    memories.unshare_from_task(memory.id, second.id).unwrap();
    assert_eq!(
        memories
            .list_for_task(second.id, true, 10, 0)
            .unwrap()
            .total,
        0
    );
}

#[test]
fn deleting_workspace_cascades_tasks_and_clears_conversation_links() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let workspaces = profile.workspaces();
    let tasks = profile.tasks();
    let conversations = profile.conversations();
    let workspace = workspaces
        .create(&NewWorkspace {
            name: "Delete me".into(),
            description: String::new(),
            icon: None,
        })
        .unwrap();
    let task = tasks
        .create(&NewTask {
            workspace_id: workspace.id,
            title: "Child".into(),
            description: String::new(),
            status: TaskStatus::Open,
        })
        .unwrap();
    let conversation = conversations
        .create(&NewConversation {
            title: "Linked".into(),
            workspace_id: Some(workspace.id),
            task_id: Some(task.id),
        })
        .unwrap();

    workspaces.delete(workspace.id).unwrap();
    let linked = conversations.get(conversation.id).unwrap().unwrap();
    assert_eq!(linked.workspace_id, None);
    assert_eq!(linked.task_id, None);
    assert!(tasks.get(task.id).unwrap().is_none());
}

#[test]
fn unified_search_returns_original_ids_and_types() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let library = profile.library();
    let conversations = profile.conversations();
    let workspaces = profile.workspaces();
    let tasks = profile.tasks();
    let memories = profile.task_memories();

    let item = library
        .create(&library_item("Unique ownership notes"))
        .unwrap();
    let conversation = conversations
        .create(&NewConversation {
            title: "Ownership conversation".into(),
            workspace_id: None,
            task_id: None,
        })
        .unwrap();
    conversations
        .add_message(
            conversation.id,
            &NewMessage {
                role: MessageRole::User,
                body: "Discuss ownership evidence".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::User,
                parts: Vec::new(),
            },
        )
        .unwrap();
    let workspace = workspaces
        .create(&NewWorkspace {
            name: "Ownership workspace".into(),
            description: String::new(),
            icon: None,
        })
        .unwrap();
    let task = tasks
        .create(&NewTask {
            workspace_id: workspace.id,
            title: "Ownership task".into(),
            description: String::new(),
            status: TaskStatus::Open,
        })
        .unwrap();
    let memory = memories
        .create(&NewTaskMemory {
            task_id: task.id,
            title: "Ownership memory".into(),
            content: "Ownership needs two sources.".into(),
            memory_type: MemoryType::ResearchResult,
            source_reference_id: None,
            importance: 3,
            user_confirmed: false,
        })
        .unwrap();

    let hits = profile.search().search("ownership", 50).unwrap();
    assert!(
        hits.iter()
            .any(|hit| hit.kind == SearchKind::LibraryItem && hit.id == item.id)
    );
    assert!(
        hits.iter()
            .any(|hit| hit.kind == SearchKind::Conversation && hit.id == conversation.id)
    );
    assert!(
        hits.iter()
            .any(|hit| hit.kind == SearchKind::Message && hit.id > 0)
    );
    assert!(
        hits.iter()
            .any(|hit| hit.kind == SearchKind::Workspace && hit.id == workspace.id)
    );
    assert!(
        hits.iter()
            .any(|hit| hit.kind == SearchKind::Task && hit.id == task.id)
    );
    assert!(
        hits.iter()
            .any(|hit| hit.kind == SearchKind::TaskMemory && hit.id == memory.id)
    );
    assert!(hits.iter().all(|hit| hit.id > 0));
    assert!(profile.search().search("???", 10).unwrap().is_empty());
}

#[test]
fn phase_1_6_data_is_isolated_between_profiles() {
    let dir = tempdir().unwrap();
    let store = Store::open(dir.path()).unwrap();
    let first = ProfileId::new("p1");
    let second = ProfileId::new("p2");
    store.create_profile(&first, "First", true).unwrap();
    store.create_profile(&second, "Second", false).unwrap();
    {
        let profile = store.open_profile(&first).unwrap();
        profile
            .library()
            .create(&library_item("Only in first"))
            .unwrap();
        profile
            .conversations()
            .create(&NewConversation {
                title: "First chat".into(),
                workspace_id: None,
                task_id: None,
            })
            .unwrap();
        profile
            .workspaces()
            .create(&NewWorkspace {
                name: "First workspace".into(),
                description: String::new(),
                icon: None,
            })
            .unwrap();
    }
    {
        let profile = store.open_profile(&second).unwrap();
        assert_eq!(profile.library().list(10, 0).unwrap().total, 0);
        assert_eq!(profile.conversations().list(true, 10, 0).unwrap().total, 0);
        assert_eq!(profile.workspaces().list(true, 10, 0).unwrap().total, 0);
        assert!(profile.search().search("only", 10).unwrap().is_empty());
    }
}

#[test]
fn library_and_memory_validation_rejects_bad_inputs() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let mut bad = library_item("");
    bad.title = "".into();
    assert!(matches!(
        profile.library().create(&bad),
        Err(StoreError::InvalidInput(_))
    ));
    assert!(matches!(
        profile.sources().create(&NewSourceReference {
            url: "  ".into(),
            page_title: String::new(),
            site_name: String::new(),
            quote_text: String::new(),
            location_info: String::new(),
            library_item_id: None,
        }),
        Err(StoreError::InvalidInput(_))
    ));

    let workspace = profile
        .workspaces()
        .create(&NewWorkspace {
            name: "W".into(),
            description: String::new(),
            icon: None,
        })
        .unwrap();
    let task = profile
        .tasks()
        .create(&NewTask {
            workspace_id: workspace.id,
            title: "T".into(),
            description: String::new(),
            status: TaskStatus::Open,
        })
        .unwrap();
    assert!(matches!(
        profile.task_memories().create(&NewTaskMemory {
            task_id: task.id,
            title: "M".into(),
            content: "C".into(),
            memory_type: MemoryType::Fact,
            source_reference_id: None,
            importance: 6,
            user_confirmed: false,
        }),
        Err(StoreError::InvalidInput(_))
    ));
}

#[test]
fn library_pagination_and_favorite_order() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let library = profile.library();
    let mut ids = Vec::new();
    for index in 0..3 {
        ids.push(
            library
                .create(&library_item(&format!("Item {index}")))
                .unwrap()
                .id,
        );
    }
    let favorite = library.get_required(ids[2]).unwrap();
    library
        .update(
            favorite.id,
            &LibraryUpdate {
                title: favorite.title,
                description: favorite.description,
                kind: favorite.kind,
                original_url: favorite.original_url,
                source_name: favorite.source_name,
                image_ref: favorite.image_ref,
                user_note: favorite.user_note,
                favorite: true,
                status: LibraryStatus::Active,
                sort_order: 0,
            },
        )
        .unwrap();
    let page = library.list(2, 0).unwrap();
    assert_eq!(page.total, 3);
    assert_eq!(page.items.len(), 2);
    assert_eq!(page.items[0].id, ids[2]);
    assert_eq!(library.list(2, 2).unwrap().items.len(), 1);
}

#[test]
fn library_archive_and_restore_visibility() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let library = profile.library();
    let item = library.create(&library_item("Archive me")).unwrap();
    library
        .set_status(item.id, LibraryStatus::Archived)
        .unwrap();
    assert_eq!(library.list(10, 0).unwrap().total, 0);
    assert_eq!(library.search("archive", 10, 0).unwrap().total, 1);
    library.set_status(item.id, LibraryStatus::Active).unwrap();
    assert_eq!(library.list(10, 0).unwrap().total, 1);
}

#[test]
fn tag_delete_cascades_and_collection_update() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let library = profile.library();
    let item = library.create(&library_item("Tagged")).unwrap();
    let tag = library.create_tag("One").unwrap();
    let tag2 = library.create_tag("Two").unwrap();
    library.attach_tag(item.id, tag.id).unwrap();
    library.attach_tag(item.id, tag2.id).unwrap();
    assert_eq!(library.tags_for_item(item.id).unwrap().len(), 2);
    library.delete_tag(tag.id).unwrap();
    assert_eq!(library.tags_for_item(item.id).unwrap().len(), 1);

    let collection = library.create_collection("Old", "old").unwrap();
    let updated = library
        .update_collection(collection.id, "New", "new description")
        .unwrap();
    assert_eq!(updated.name, "New");
    assert!(library.delete_collection(collection.id).unwrap());
}

#[test]
fn source_pagination_and_delete() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let item = profile.library().create(&library_item("Sources")).unwrap();
    for index in 0..3 {
        profile
            .sources()
            .create(&NewSourceReference {
                url: format!("https://example.com/{index}"),
                page_title: format!("Source {index}"),
                site_name: "Example".into(),
                quote_text: String::new(),
                location_info: String::new(),
                library_item_id: Some(item.id),
            })
            .unwrap();
    }
    let page = profile
        .sources()
        .list_for_library_item(item.id, 2, 0)
        .unwrap();
    assert_eq!(page.total, 3);
    assert_eq!(page.items.len(), 2);
    let first = page.items[0].id;
    assert!(profile.sources().delete(first).unwrap());
    assert!(profile.sources().get(first).unwrap().is_none());
}

#[test]
fn conversation_archive_and_validation() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let conversations = profile.conversations();
    let conversation = conversations
        .create(&NewConversation {
            title: "Archive chat".into(),
            workspace_id: None,
            task_id: None,
        })
        .unwrap();
    conversations.set_archived(conversation.id, true).unwrap();
    assert_eq!(conversations.list(false, 10, 0).unwrap().total, 0);
    assert_eq!(conversations.list(true, 10, 0).unwrap().total, 1);
    conversations.set_archived(conversation.id, false).unwrap();
    assert!(matches!(
        conversations.create(&NewConversation {
            title: "   ".into(),
            workspace_id: None,
            task_id: None,
        }),
        Err(StoreError::InvalidInput(_))
    ));
}

#[test]
fn message_parts_preserve_order_and_kinds() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let conversations = profile.conversations();
    let conversation = conversations
        .create(&NewConversation {
            title: "Parts".into(),
            workspace_id: None,
            task_id: None,
        })
        .unwrap();
    let message = conversations
        .add_message(
            conversation.id,
            &NewMessage {
                role: MessageRole::Assistant,
                body: "code and error".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::User,
                parts: vec![
                    NewMessagePart {
                        kind: MessagePartKind::Code,
                        text: "let x = 1;".into(),
                        reference: Some("rust".into()),
                        metadata_json: None,
                    },
                    NewMessagePart {
                        kind: MessagePartKind::Error,
                        text: "warning".into(),
                        reference: None,
                        metadata_json: Some("{\"severity\":\"low\"}".into()),
                    },
                    NewMessagePart {
                        kind: MessagePartKind::Attachment,
                        text: "file".into(),
                        reference: Some("id:1".into()),
                        metadata_json: None,
                    },
                ],
            },
        )
        .unwrap();
    assert_eq!(message.parts.len(), 3);
    assert_eq!(message.parts[0].kind, MessagePartKind::Code);
    assert_eq!(message.parts[0].position, 0);
    assert_eq!(message.parts[1].kind, MessagePartKind::Error);
    assert_eq!(message.parts[2].kind, MessagePartKind::Attachment);
    assert_eq!(
        conversations
            .get_message(message.id)
            .unwrap()
            .unwrap()
            .parts
            .len(),
        3
    );
}

#[test]
fn message_delete_and_purge_cascade_parts_and_links() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let conversations = profile.conversations();
    let conversation = conversations
        .create(&NewConversation {
            title: "Delete messages".into(),
            workspace_id: None,
            task_id: None,
        })
        .unwrap();
    let message = conversations
        .add_message(
            conversation.id,
            &NewMessage {
                role: MessageRole::Assistant,
                body: "delete me".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::User,
                parts: vec![NewMessagePart {
                    kind: MessagePartKind::Text,
                    text: "delete me".into(),
                    reference: None,
                    metadata_json: None,
                }],
            },
        )
        .unwrap();
    let source = profile
        .sources()
        .create(&NewSourceReference {
            url: "https://example.com/delete".into(),
            page_title: String::new(),
            site_name: String::new(),
            quote_text: String::new(),
            location_info: String::new(),
            library_item_id: None,
        })
        .unwrap();
    conversations.attach_source(message.id, source.id).unwrap();
    assert!(conversations.delete_message(message.id).unwrap());
    assert!(conversations.get_message(message.id).unwrap().is_none());
    assert!(profile.sources().get(source.id).unwrap().is_some());

    let message = conversations
        .add_message(
            conversation.id,
            &NewMessage {
                role: MessageRole::Assistant,
                body: "second".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::User,
                parts: Vec::new(),
            },
        )
        .unwrap();
    conversations.attach_source(message.id, source.id).unwrap();
    conversations.purge(conversation.id).unwrap();
    assert!(profile.sources().get(source.id).unwrap().is_some());
}

#[test]
fn workspace_archive_and_restore() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let workspaces = profile.workspaces();
    let workspace = workspaces
        .create(&NewWorkspace {
            name: "Archive workspace".into(),
            description: String::new(),
            icon: None,
        })
        .unwrap();
    workspaces.set_archived(workspace.id, true).unwrap();
    assert_eq!(workspaces.list(false, 10, 0).unwrap().total, 0);
    assert_eq!(workspaces.list(true, 10, 0).unwrap().total, 1);
    workspaces.set_archived(workspace.id, false).unwrap();
    assert_eq!(workspaces.list(false, 10, 0).unwrap().total, 1);
}

#[test]
fn task_status_filters_and_memory_type_update() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let workspace = profile
        .workspaces()
        .create(&NewWorkspace {
            name: "Tasks".into(),
            description: String::new(),
            icon: None,
        })
        .unwrap();
    let open = profile
        .tasks()
        .create(&NewTask {
            workspace_id: workspace.id,
            title: "Open".into(),
            description: String::new(),
            status: TaskStatus::Open,
        })
        .unwrap();
    let archived = profile
        .tasks()
        .create(&NewTask {
            workspace_id: workspace.id,
            title: "Archived".into(),
            description: String::new(),
            status: TaskStatus::Archived,
        })
        .unwrap();
    assert_eq!(
        profile
            .tasks()
            .list(workspace.id, false, 10, 0)
            .unwrap()
            .total,
        1
    );
    assert_eq!(
        profile
            .tasks()
            .list(workspace.id, true, 10, 0)
            .unwrap()
            .total,
        2
    );
    let memory = profile
        .task_memories()
        .create(&NewTaskMemory {
            task_id: open.id,
            title: "Memory".into(),
            content: "Content".into(),
            memory_type: MemoryType::Fact,
            source_reference_id: None,
            importance: 1,
            user_confirmed: false,
        })
        .unwrap();
    let updated = profile
        .task_memories()
        .update(
            memory.id,
            "Memory",
            "Content",
            MemoryType::Decision,
            1,
            true,
        )
        .unwrap();
    assert_eq!(updated.memory_type, MemoryType::Decision);
    assert!(updated.user_confirmed);
    assert!(profile.tasks().delete(archived.id).unwrap());
}

#[test]
fn task_memory_importance_and_source_cleanup() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let workspace = profile
        .workspaces()
        .create(&NewWorkspace {
            name: "Memory".into(),
            description: String::new(),
            icon: None,
        })
        .unwrap();
    let task = profile
        .tasks()
        .create(&NewTask {
            workspace_id: workspace.id,
            title: "Task".into(),
            description: String::new(),
            status: TaskStatus::Open,
        })
        .unwrap();
    for bad in [-1, 6] {
        assert!(matches!(
            profile.task_memories().create(&NewTaskMemory {
                task_id: task.id,
                title: "M".into(),
                content: "C".into(),
                memory_type: MemoryType::Fact,
                source_reference_id: None,
                importance: bad,
                user_confirmed: false,
            }),
            Err(StoreError::InvalidInput(_))
        ));
    }
    let source = profile
        .sources()
        .create(&NewSourceReference {
            url: "https://example.com/memory".into(),
            page_title: String::new(),
            site_name: String::new(),
            quote_text: String::new(),
            location_info: String::new(),
            library_item_id: None,
        })
        .unwrap();
    let memory = profile
        .task_memories()
        .create(&NewTaskMemory {
            task_id: task.id,
            title: "M".into(),
            content: "C".into(),
            memory_type: MemoryType::Fact,
            source_reference_id: Some(source.id),
            importance: 2,
            user_confirmed: false,
        })
        .unwrap();
    profile.sources().delete(source.id).unwrap();
    assert_eq!(
        profile
            .task_memories()
            .get_required(memory.id)
            .unwrap()
            .source_reference_id,
        None
    );
}

#[test]
fn unified_search_excludes_internal_and_deleted_messages() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let conversation = profile
        .conversations()
        .create(&NewConversation {
            title: "Visibility".into(),
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
                body: "visiblezz".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::User,
                parts: Vec::new(),
            },
        )
        .unwrap();
    profile
        .conversations()
        .add_message(
            conversation.id,
            &NewMessage {
                role: MessageRole::System,
                body: "internalsecret".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::Internal,
                parts: Vec::new(),
            },
        )
        .unwrap();
    let visible = profile.search().search("visiblezz", 10).unwrap();
    assert_eq!(visible.len(), 1);
    assert_eq!(visible[0].kind, SearchKind::Message);
    assert!(
        profile
            .search()
            .search("internalsecret", 10)
            .unwrap()
            .is_empty()
    );
    profile.conversations().delete(conversation.id).unwrap();
    assert!(profile.search().search("visiblezz", 10).unwrap().is_empty());
}

#[test]
fn search_treats_fts_operators_as_text() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let item = profile
        .library()
        .create(&library_item("OR NOT NEAR( alpha"))
        .unwrap();
    let hits = profile.search().search("OR NOT NEAR(", 10).unwrap();
    assert!(hits.iter().any(|hit| hit.id == item.id));
    assert!(profile.search().search("\"", 10).unwrap().is_empty());
}

#[test]
fn library_fts_removes_stale_terms_after_update() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let library = profile.library();
    let item = library.create(&library_item("alphaunique")).unwrap();
    assert_eq!(library.search("alphaunique", 10, 0).unwrap().total, 1);
    library
        .update(
            item.id,
            &LibraryUpdate {
                title: "betaunique".into(),
                description: String::new(),
                kind: LibraryKind::Note,
                original_url: None,
                source_name: None,
                image_ref: None,
                user_note: String::new(),
                favorite: false,
                status: LibraryStatus::Active,
                sort_order: 0,
            },
        )
        .unwrap();
    assert_eq!(library.search("alphaunique", 10, 0).unwrap().total, 0);
    assert_eq!(library.search("betaunique", 10, 0).unwrap().total, 1);
}

#[test]
fn message_fts_removes_stale_terms_after_update() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let conversations = profile.conversations();
    let conversation = conversations
        .create(&NewConversation {
            title: "FTS update".into(),
            workspace_id: None,
            task_id: None,
        })
        .unwrap();
    let message = conversations
        .add_message(
            conversation.id,
            &NewMessage {
                role: MessageRole::User,
                body: "oldterm".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::User,
                parts: Vec::new(),
            },
        )
        .unwrap();
    conversations
        .update_message(message.id, "newterm", MessageStatus::Complete)
        .unwrap();
    assert!(profile.search().search("oldterm", 10).unwrap().is_empty());
    assert_eq!(profile.search().search("newterm", 10).unwrap().len(), 1);
}

#[test]
fn conversation_restore_returns_to_search() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let conversations = profile.conversations();
    let conversation = conversations
        .create(&NewConversation {
            title: "restoreunique".into(),
            workspace_id: None,
            task_id: None,
        })
        .unwrap();
    assert_eq!(
        conversations.search("restoreunique", 10, 0).unwrap().total,
        1
    );
    conversations.delete(conversation.id).unwrap();
    assert_eq!(
        conversations.search("restoreunique", 10, 0).unwrap().total,
        0
    );
    conversations.restore(conversation.id).unwrap();
    assert_eq!(
        conversations.search("restoreunique", 10, 0).unwrap().total,
        1
    );
}

#[test]
fn collection_listing_excludes_trashed_items() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let library = profile.library();
    let first = library
        .create(&library_item("First collection item"))
        .unwrap();
    let second = library
        .create(&library_item("Second collection item"))
        .unwrap();
    let collection = library.create_collection("Mixed", "").unwrap();
    library.add_to_collection(first.id, collection.id).unwrap();
    library.add_to_collection(second.id, collection.id).unwrap();
    assert_eq!(
        library
            .items_in_collection(collection.id, 10, 0)
            .unwrap()
            .total,
        2
    );
    library.trash(second.id).unwrap();
    let page = library.items_in_collection(collection.id, 10, 0).unwrap();
    assert_eq!(page.total, 1);
    assert_eq!(page.items[0].id, first.id);
}

#[test]
fn private_keys_are_rejected_from_message_parts_and_updates() {
    let (_dir, store) = setup();
    let profile = store.open_profile(&ProfileId::new("p1")).unwrap();
    let conversations = profile.conversations();
    let conversation = conversations
        .create(&NewConversation {
            title: "Part key test".into(),
            workspace_id: None,
            task_id: None,
        })
        .unwrap();
    let part_key = "-----BEGIN RSA PRIVATE KEY-----\nabc";
    assert!(matches!(
        conversations.add_message(
            conversation.id,
            &NewMessage {
                role: MessageRole::Tool,
                body: "tool output".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::Internal,
                parts: vec![NewMessagePart {
                    kind: MessagePartKind::ToolResult,
                    text: part_key.into(),
                    reference: None,
                    metadata_json: None,
                }],
            },
        ),
        Err(StoreError::InvalidInput(_))
    ));
    let message = conversations
        .add_message(
            conversation.id,
            &NewMessage {
                role: MessageRole::Assistant,
                body: "safe".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::User,
                parts: Vec::new(),
            },
        )
        .unwrap();
    assert!(matches!(
        conversations.update_message(message.id, part_key, MessageStatus::Error),
        Err(StoreError::InvalidInput(_))
    ));
}
