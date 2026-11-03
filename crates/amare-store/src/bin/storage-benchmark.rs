use amare_core::{NewBookmark, NewHistoryVisit, NewSessionTab, ProfileId};
use amare_store::{FolderScope, Store};
use serde_json::json;
use std::fs;
use std::path::PathBuf;
use std::time::Instant;

fn main() {
    let output = parse_output();
    let root = unique_temp_dir("amare-store-bench");
    let result = run(&root);
    let _ = fs::remove_dir_all(&root);
    match result {
        Ok(value) => {
            let text = serde_json::to_string_pretty(&value).unwrap();
            if let Some(path) = output {
                if let Some(parent) = path.parent() {
                    let _ = fs::create_dir_all(parent);
                }
                fs::write(&path, &text).unwrap();
                println!("storage benchmark written to {}", path.display());
            } else {
                println!("{text}");
            }
        }
        Err(error) => {
            eprintln!("storage benchmark failed: {error}");
            std::process::exit(1);
        }
    }
}

fn run(root: &PathBuf) -> Result<serde_json::Value, Box<dyn std::error::Error>> {
    let started = Instant::now();
    let setup_started = Instant::now();
    let store = Store::open(root)?;
    let profile_id = ProfileId::new("bench");
    store.create_profile(&profile_id, "Benchmark", true)?;
    let profile = store.open_profile(&profile_id)?;
    let setup_ms = setup_started.elapsed().as_millis() as u64;

    let bookmarks: Vec<_> = (0..1000)
        .map(|index| NewBookmark {
            title: format!("Bookmark {index}"),
            url: format!("https://example.com/bookmark/{index}"),
            folder_id: None,
            position: None,
        })
        .collect();
    let bookmarks_started = Instant::now();
    profile.bookmarks().create_many(&bookmarks)?;
    let bookmarks_ms = bookmarks_started.elapsed().as_millis() as u64;

    let bookmarks_list_started = Instant::now();
    let bookmark_page = profile.bookmarks().list(FolderScope::All, 100, 0)?;
    let bookmarks_list_ms = bookmarks_list_started.elapsed().as_millis() as u64;

    let visits: Vec<_> = (0..10_000)
        .map(|index| NewHistoryVisit {
            url: format!("https://example.com/history/{}", index % 2000),
            title: format!("History {index}"),
            visited_at_ms: 1_700_000_000_000 + index as i64,
        })
        .collect();
    let history_started = Instant::now();
    profile.history().record_visits(&visits)?;
    let history_ms = history_started.elapsed().as_millis() as u64;

    let history_search_started = Instant::now();
    let history_page = profile
        .history()
        .search(Some("history/"), None, None, 100, 0)?;
    let history_search_ms = history_search_started.elapsed().as_millis() as u64;

    let tabs: Vec<_> = (0..30)
        .map(|index| NewSessionTab {
            url: format!("https://example.com/session-tab/{index}"),
            title: format!("Tab {index}"),
            pinned: index == 0,
            group_id: (index % 5 == 0).then(|| format!("group-{}", index % 3)),
            position: None,
        })
        .collect();
    let sessions_started = Instant::now();
    let mut session_ids = Vec::new();
    for index in 0..10 {
        let state = profile
            .sessions()
            .create(&format!("Session {index}"), &tabs, Some(0))?;
        session_ids.push(state.session.id);
    }
    let sessions_ms = sessions_started.elapsed().as_millis() as u64;

    let restore_started = Instant::now();
    let restored = profile.sessions().restore(session_ids[0])?;
    let restore_ms = restore_started.elapsed().as_millis() as u64;

    Ok(json!({
        "environment": {
            "os": std::env::consts::OS,
            "arch": std::env::consts::ARCH,
            "rust": env!("CARGO_PKG_VERSION"),
            "sqlite": profile.sqlite_version(),
            "parallelism": std::thread::available_parallelism().map(|n| n.get()).unwrap_or(0)
        },
        "dataset": {
            "bookmarks": 1000,
            "history_visits": 10_000,
            "sessions": 10,
            "session_tabs": 300
        },
        "timings_ms": {
            "setup_and_migrations": setup_ms,
            "total_run": started.elapsed().as_millis() as u64,
            "bookmarks_insert_1000": bookmarks_ms,
            "bookmarks_list_100": bookmarks_list_ms,
            "history_insert_10000": history_ms,
            "history_search": history_search_ms,
            "sessions_create_10x30": sessions_ms,
            "session_restore": restore_ms
        },
        "counts": {
            "bookmarks_total": bookmark_page.total,
            "history_distinct_urls": history_page.total,
            "session_tabs_restored": restored.tabs.len()
        },
        "note": "Runner timing is not a substitute for the target i5-7500/16GB PC."
    }))
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
