use amare_core::{
    LibraryKind, MessageRole, MessageStatus, MessageVisibility, NewBookmark, NewConversation,
    NewLibraryItem, NewMessage, ProfileId, SearchKind, SettingValue,
};
use amare_services::{
    ProfileService, SearchRequest, SearchService, ServiceError, SettingSource, SettingsService,
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
    let root = unique_temp_dir("amare-service-ci");
    let report = run(&root);
    let _ = fs::remove_dir_all(&root);
    let text = serde_json::to_string_pretty(&report).unwrap();
    if let Some(path) = output {
        if let Some(parent) = path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        fs::write(&path, &text).unwrap();
        println!("service CI written to {}", path.display());
    } else {
        println!("{text}");
    }
    if report["summary"]["failed"].as_u64().unwrap_or(0) > 0 {
        std::process::exit(1);
    }
}

fn run(root: &PathBuf) -> serde_json::Value {
    let service = match ProfileService::open(root) {
        Ok(service) => service,
        Err(error) => {
            return json!({
                "environment": environment(),
                "cases": [],
                "summary": {"passed": 0, "failed": 1, "unverified": 0, "skipped": 0},
                "error": error.to_string()
            });
        }
    };
    let mut cases = Vec::new();

    cases.push(run_case("profile_lifecycle", || {
        let context = service.initialize()?;
        let work = ProfileId::new("work");
        service.create_profile(&work, "Work", Some("asset:work"), false)?;
        let work_context = service.switch_profile(&work)?;
        assert_ne!(context.generation(), work_context.generation());
        assert!(matches!(
            service.profile_store(&context),
            Err(ServiceError::ProfileSwitched)
        ));
        service.delete_profile(&work)?;
        assert_eq!(
            service.active_profile()?.unwrap().id,
            ProfileId::new("default")
        );
        Ok("initialize/create/switch/stale-context/delete".to_string())
    }));

    cases.push(run_case("settings_policy", || {
        let context = service.current_context()?.ok_or("no active profile")?;
        let settings = SettingsService::new(&service);
        let change = settings.set(&context, "theme", SettingValue::Text("dark".into()))?;
        assert_eq!(change.changed_count(), 1);
        assert_eq!(settings.get(&context, "theme")?.source, SettingSource::User);
        let before = settings.get(&context, "theme")?.value;
        let failed = settings.set_many(
            &context,
            &[
                ("theme", SettingValue::Text("light".into())),
                ("engine", SettingValue::Text("invalid-engine".into())),
            ],
        );
        assert!(failed.is_err());
        assert_eq!(settings.get(&context, "theme")?.value, before);
        assert!(matches!(
            settings.set(&context, "api_key", SettingValue::Text("x".into())),
            Err(ServiceError::SensitiveSetting(_))
        ));
        Ok("defaults/user/atomic-validation/sensitive-key".to_string())
    }));

    cases.push(run_case("search_typed_filters", || {
        let context = service.current_context()?.ok_or("no active profile")?;
        let profile = service.profile_store(&context)?;
        profile.library().create(&NewLibraryItem {
            title: "CI service needle".into(),
            description: String::new(),
            kind: LibraryKind::Note,
            original_url: None,
            source_name: None,
            image_ref: None,
            user_note: String::new(),
            favorite: false,
        })?;
        profile.bookmarks().create(NewBookmark {
            title: "CI service needle".into(),
            url: "https://example.com/ci".into(),
            folder_id: None,
            position: None,
        })?;
        let conversation = profile.conversations().create(&NewConversation {
            title: "CI service needle".into(),
            workspace_id: None,
            task_id: None,
        })?;
        profile.conversations().add_message(
            conversation.id,
            &NewMessage {
                role: MessageRole::User,
                body: "CI service needle".into(),
                status: MessageStatus::Complete,
                visibility: MessageVisibility::User,
                parts: Vec::new(),
            },
        )?;
        let search = SearchService::new(&service);
        let page = search.search(
            &context,
            &SearchRequest::new("needle")
                .with_kinds([SearchKind::LibraryItem, SearchKind::Bookmark]),
        )?;
        assert_eq!(page.total, 2);
        assert!(
            page.items
                .iter()
                .any(|hit| hit.kind == SearchKind::LibraryItem)
        );
        assert!(
            page.items
                .iter()
                .any(|hit| hit.kind == SearchKind::Bookmark)
        );
        Ok("typed FTS + bookmark filter with original IDs".to_string())
    }));

    cases.push(run_case("profile_isolation", || {
        let first = service.current_context()?.ok_or("no active profile")?;
        service.create_profile(&ProfileId::new("isolated"), "Isolated", None, false)?;
        let second = service.switch_profile(&ProfileId::new("isolated"))?;
        let settings = SettingsService::new(&service);
        assert_eq!(
            settings.get(&second, "theme")?.source,
            SettingSource::Default
        );
        let search = SearchService::new(&service);
        assert_eq!(
            search.search(&second, &SearchRequest::new("needle"))?.total,
            0
        );
        let first_again = service.switch_profile(&first.profile_id)?;
        assert!(
            search
                .search(&first_again, &SearchRequest::new("needle"))?
                .total
                >= 2
        );
        Ok("settings/search remain profile-isolated".to_string())
    }));

    let passed = cases.iter().filter(|case| case.status == "PASS").count();
    let failed = cases.iter().filter(|case| case.status == "FAIL").count();
    json!({
        "environment": environment(),
        "cases": cases.iter().map(|case| json!({
            "id": format!("service.{}", case.name),
            "name": case.name,
            "status": case.status,
            "executed": case.executed,
            "duration_ms": case.duration_ms,
            "detail": case.detail,
            "environment": environment(),
            "log": "service-ci.log",
            "limitation": "Rust service logic only; UI/WebView2/AI transport is not covered."
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
