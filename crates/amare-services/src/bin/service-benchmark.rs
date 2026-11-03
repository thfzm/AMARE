use amare_core::{
    LibraryKind, NewBookmark, NewHistoryVisit, NewLibraryItem, NewWorkspace, ProfileId, SearchKind,
    SettingValue,
};
use amare_services::{ProfileService, SearchRequest, SearchService, SettingsService};
use serde_json::json;
use std::fs;
use std::path::PathBuf;
use std::time::Instant;

struct Metric {
    name: &'static str,
    iterations: u64,
    total_us: u64,
}

impl Metric {
    fn to_json(&self) -> serde_json::Value {
        json!({
            "name": self.name,
            "iterations": self.iterations,
            "total_ms": self.total_us as f64 / 1000.0,
            "avg_us": self.total_us as f64 / self.iterations as f64,
            "metric_scope": "Linux host service-layer measurement; not target hardware"
        })
    }
}

fn main() {
    let output = parse_output();
    let root = unique_temp_dir("amare-service-benchmark");
    let report = run(&root);
    let _ = fs::remove_dir_all(&root);
    let text = serde_json::to_string_pretty(&report).unwrap();
    if let Some(path) = output {
        if let Some(parent) = path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        fs::write(&path, &text).unwrap();
        println!("service benchmark written to {}", path.display());
    } else {
        println!("{text}");
    }
}

fn run(root: &PathBuf) -> serde_json::Value {
    let service = match ProfileService::open(root) {
        Ok(service) => service,
        Err(error) => {
            return json!({"environment": environment(), "metrics": [], "error": error.to_string()});
        }
    };
    let context = match service.initialize() {
        Ok(context) => context,
        Err(error) => {
            return json!({"environment": environment(), "metrics": [], "error": error.to_string()});
        }
    };
    let work = ProfileId::new("bench-work");
    service
        .create_profile(&work, "Benchmark", None, false)
        .unwrap();

    {
        let profile = service.profile_store(&context).unwrap();
        for index in 0..500 {
            profile
                .library()
                .create(&NewLibraryItem {
                    title: format!("Bench needle {index}"),
                    description: "Benchmark library item".into(),
                    kind: LibraryKind::Note,
                    original_url: Some(format!("https://example.com/{index}")),
                    source_name: None,
                    image_ref: None,
                    user_note: String::new(),
                    favorite: false,
                })
                .unwrap();
            profile
                .bookmarks()
                .create(NewBookmark {
                    title: format!("Bench needle bookmark {index}"),
                    url: format!("https://bench.example/{index}"),
                    folder_id: None,
                    position: None,
                })
                .unwrap();
            profile
                .history()
                .record_visit(NewHistoryVisit {
                    url: format!("https://bench.example/{index}"),
                    title: format!("Bench needle history {index}"),
                    visited_at_ms: 1_700_000_000_000 + index,
                })
                .unwrap();
        }
        profile
            .workspaces()
            .create(&NewWorkspace {
                name: "Bench workspace".into(),
                description: String::new(),
                icon: None,
            })
            .unwrap();
    }

    let search = SearchService::new(&service);
    let settings = SettingsService::new(&service);
    let mut metrics = Vec::new();

    metrics.push(measure("search_all_kinds_50", 50, || {
        let page = search
            .search(&context, &SearchRequest::new("needle").with_page(50, 0))
            .unwrap();
        assert!(page.total >= 1);
    }));

    metrics.push(measure("search_typed_filter_50", 50, || {
        let page = search
            .search(
                &context,
                &SearchRequest::new("needle")
                    .with_kinds([SearchKind::LibraryItem, SearchKind::Bookmark])
                    .with_page(50, 0),
            )
            .unwrap();
        assert_eq!(page.total, 1_000);
    }));

    metrics.push(measure("settings_atomic_two_keys_100", 100, || {
        settings
            .set_many(
                &context,
                &[
                    ("theme", SettingValue::Text("dark".into())),
                    ("engine", SettingValue::Text("google".into())),
                ],
            )
            .unwrap();
    }));

    let mut generation = context.generation();
    let mut profile_id = context.profile_id.clone();
    metrics.push(measure("profile_switch_100", 100, || {
        profile_id = if profile_id.as_str() == "default" {
            work.clone()
        } else {
            ProfileId::new("default")
        };
        let next = service.switch_profile(&profile_id).unwrap();
        assert!(next.generation() > generation);
        generation = next.generation();
    }));

    json!({
        "environment": environment(),
        "dataset": {
            "library_items": 500,
            "bookmarks": 500,
            "history_visits": 500,
            "profiles": 2
        },
        "metrics": metrics.iter().map(Metric::to_json).collect::<Vec<_>>(),
        "note": "Measurements are from the Linux host; target Windows/Android hardware is not measured."
    })
}

fn measure(name: &'static str, iterations: u64, mut operation: impl FnMut()) -> Metric {
    let started = Instant::now();
    for _ in 0..iterations {
        operation();
    }
    Metric {
        name,
        iterations,
        total_us: started.elapsed().as_micros() as u64,
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
