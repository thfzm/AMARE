use amare_core::{NewBookmark, NewHistoryVisit, NewSessionTab, ProfileId};
use amare_store::{
    BackupCredential, BackupManager, BackupOptions, RestoreMode, RestoreOptions, Store,
};
use serde_json::json;
use std::fs;
use std::path::PathBuf;
use std::time::Instant;

fn main() {
    let output = parse_output();
    let root = unique_temp_dir("amare-backup-bench");
    let target = unique_temp_dir("amare-backup-bench-target");
    let result = run(&root, &target);
    let _ = fs::remove_dir_all(&root);
    let _ = fs::remove_dir_all(&target);
    match result {
        Ok(value) => {
            let text = serde_json::to_string_pretty(&value).unwrap();
            if let Some(path) = output {
                if let Some(parent) = path.parent() {
                    let _ = fs::create_dir_all(parent);
                }
                fs::write(path, &text).unwrap();
            } else {
                println!("{text}");
            }
        }
        Err(error) => {
            eprintln!("backup benchmark failed: {error}");
            std::process::exit(1);
        }
    }
}

fn run(root: &PathBuf, target: &PathBuf) -> Result<serde_json::Value, Box<dyn std::error::Error>> {
    let setup_started = Instant::now();
    let store = Store::open(root)?;
    let mut profile_ids = Vec::new();
    for index in 0..5 {
        let id = ProfileId::new(format!("bench-{index}"));
        store.create_profile(&id, &format!("Profile {index}"), index == 0)?;
        let profile = store.open_profile(&id)?;
        let bookmarks: Vec<_> = (0..1000)
            .map(|bookmark| NewBookmark {
                title: format!("Bookmark {bookmark}"),
                url: format!("https://example.com/{index}/{bookmark}"),
                folder_id: None,
                position: None,
            })
            .collect();
        profile.bookmarks().create_many(&bookmarks)?;
        let visits: Vec<_> = (0..10_000)
            .map(|visit| NewHistoryVisit {
                url: format!("https://example.com/{index}/history/{}", visit % 2000),
                title: format!("History {visit}"),
                visited_at_ms: 1_700_000_000_000 + visit as i64,
            })
            .collect();
        profile.history().record_visits(&visits)?;
        let tabs: Vec<_> = (0..100)
            .map(|tab| NewSessionTab {
                url: format!("https://example.com/{index}/tab/{tab}"),
                title: format!("Tab {tab}"),
                pinned: tab == 0,
                group_id: None,
                position: None,
            })
            .collect();
        profile
            .sessions()
            .create(&format!("Session {index}"), &tabs, Some(0))?;
        profile_ids.push(id);
    }
    let setup_ms = setup_started.elapsed().as_millis() as u64;

    let manager = BackupManager::new(root)?;
    let full_started = Instant::now();
    let full = manager.create_backup(&store, &BackupOptions::default())?;
    let full_ms = full_started.elapsed().as_millis() as u64;

    let single_started = Instant::now();
    let single = manager.create_backup(
        &store,
        &BackupOptions {
            include_root: false,
            profiles: Some(vec![profile_ids[0].clone()]),
            ..BackupOptions::default()
        },
    )?;
    let single_ms = single_started.elapsed().as_millis() as u64;

    let verify_started = Instant::now();
    let full_path = manager.backup_dir().join(&full.file_name);
    manager.verify(&full_path, &BackupCredential::None)?;
    let verify_ms = verify_started.elapsed().as_millis() as u64;

    let restore_started = Instant::now();
    manager.restore(
        &full_path,
        target,
        &RestoreOptions {
            credential: BackupCredential::None,
            mode: RestoreMode::Full,
        },
    )?;
    let restore_ms = restore_started.elapsed().as_millis() as u64;

    let restored = Store::open(target)?;
    let restored_profiles = restored.profiles().count()?;

    Ok(json!({
        "environment": {
            "os": std::env::consts::OS,
            "arch": std::env::consts::ARCH,
            "rust_crate": env!("CARGO_PKG_VERSION"),
            "sqlite": rusqlite::version(),
            "parallelism": std::thread::available_parallelism().map(|n| n.get()).unwrap_or(0)
        },
        "dataset": {
            "profiles": 5,
            "bookmarks_per_profile": 1000,
            "history_per_profile": 10000,
            "tabs_per_profile": 100
        },
        "timings_ms": {
            "setup_and_seed": setup_ms,
            "full_backup": full_ms,
            "single_profile_backup": single_ms,
            "checksum_verify": verify_ms,
            "full_restore": restore_ms
        },
        "sizes": {
            "full_backup_bytes": full.size_bytes,
            "single_profile_backup_bytes": single.size_bytes
        },
        "counts": {
            "restored_profiles": restored_profiles,
            "full_backup_profiles": full.profiles.len()
        },
        "memory": "not_measured",
        "note": "Runner timing is not a substitute for the target i5-7500/16GB PC."
    }))
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
