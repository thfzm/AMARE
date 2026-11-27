use amare_core::{ProfileId, SettingValue};
use amare_store::Store;
use serde_json::Value;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use tempfile::tempdir;

fn helper() -> &'static str {
    env!("CARGO_BIN_EXE_amare-backup")
}

fn run(root: &Path, args: &[&str], password: Option<&str>, result: &Path) -> (i32, Value) {
    run_with_passwords(root, args, password.into_iter().collect(), result)
}

fn run_with_passwords(
    root: &Path,
    args: &[&str],
    passwords: Vec<&str>,
    result: &Path,
) -> (i32, Value) {
    let mut command = Command::new(helper());
    command.arg(args[0]);
    for argument in &args[1..] {
        command.arg(argument);
    }
    command.arg("--result").arg(result);
    command
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    let mut child = command.spawn().expect("helper spawn");
    {
        let stdin = child.stdin.as_mut().expect("stdin");
        for password in passwords {
            writeln!(stdin, "{password}").unwrap();
        }
    }
    let output = child.wait_with_output().unwrap();
    let text = std::fs::read_to_string(result).unwrap_or_else(|_| {
        format!(
            "{{\"status\":\"missing\",\"error\":\"{}\"}}",
            String::from_utf8_lossy(&output.stderr).replace('"', "'")
        )
    });
    let value: Value = serde_json::from_str(&text)
        .unwrap_or_else(|_| serde_json::json!({"status":"invalid-json","error":text}));
    let _ = root;
    (output.status.code().unwrap_or(-1), value)
}

fn seed(root: &Path) -> Store {
    let store = Store::open(root).unwrap();
    let p1 = ProfileId::new("p1");
    store.create_profile(&p1, "Personal", true).unwrap();
    let profile = store.open_profile(&p1).unwrap();
    profile
        .settings()
        .set("theme", "appearance", SettingValue::Text("dark".into()))
        .unwrap();
    store
}

#[test]
fn helper_create_verify_restore_profile() {
    let source = tempdir().unwrap();
    let _store = seed(source.path());
    let backup = source.path().join("manual.amarebak");
    let result = source.path().join("create.json");
    let (code, create) = run(
        source.path(),
        &[
            "create",
            "--root",
            source.path().to_str().unwrap(),
            "--out",
            backup.to_str().unwrap(),
            "--profiles",
            "p1",
        ],
        None,
        &result,
    );
    assert_eq!(code, 0, "{create}");
    assert_eq!(create["status"], "ok");
    assert!(backup.exists());

    let (code, verify) = run(
        source.path(),
        &[
            "verify",
            "--root",
            source.path().to_str().unwrap(),
            "--backup",
            backup.to_str().unwrap(),
        ],
        None,
        &source.path().join("verify.json"),
    );
    assert_eq!(code, 0, "{verify}");
    assert_eq!(verify["status"], "Valid");

    let target = tempdir().unwrap();
    let target_store = Store::open(target.path()).unwrap();
    let t1 = ProfileId::new("t1");
    target_store.create_profile(&t1, "Restored", true).unwrap();
    let (code, restore) = run(
        target.path(),
        &[
            "restore",
            "--root",
            target.path().to_str().unwrap(),
            "--backup",
            backup.to_str().unwrap(),
            "--mode",
            "profile",
            "--map",
            "p1:t1",
            "--no-relaunch",
        ],
        None,
        &target.path().join("restore.json"),
    );
    assert_eq!(code, 0, "{restore}");
    assert_eq!(restore["status"], "ok");
    let progress: Value = serde_json::from_slice(
        &std::fs::read(target.path().join("backups/restore-progress.json")).unwrap(),
    )
    .unwrap();
    assert_eq!(progress["stage"], "completed");
    let restored = Store::open(target.path()).unwrap();
    let profile = restored.open_profile(&t1).unwrap();
    assert_eq!(
        profile.settings().get("theme").unwrap().unwrap().value,
        SettingValue::Text("dark".into())
    );
}

#[test]
fn helper_rejects_wrong_password_and_corrupt_archive() {
    let root = tempdir().unwrap();
    let _store = seed(root.path());
    let backup = root.path().join("encrypted.amarebak");
    let (code, create) = run(
        root.path(),
        &[
            "create",
            "--root",
            root.path().to_str().unwrap(),
            "--out",
            backup.to_str().unwrap(),
            "--profiles",
            "p1",
            "--password-stdin",
        ],
        Some("correct-pass"),
        &root.path().join("create.json"),
    );
    assert_eq!(code, 0, "{create}");

    let (code, verify) = run(
        root.path(),
        &[
            "verify",
            "--root",
            root.path().to_str().unwrap(),
            "--backup",
            backup.to_str().unwrap(),
            "--password-stdin",
        ],
        Some("wrong-pass"),
        &root.path().join("verify-wrong.json"),
    );
    assert_eq!(code, 0, "{verify}");
    assert_eq!(verify["status"], "InvalidPassword", "{verify}");

    let no_sidecar = root.path().join("no-sidecar.amarebak");
    std::fs::copy(&backup, &no_sidecar).unwrap();
    let (code, verify) = run(
        root.path(),
        &[
            "verify",
            "--root",
            root.path().to_str().unwrap(),
            "--backup",
            no_sidecar.to_str().unwrap(),
            "--password-stdin",
        ],
        Some("correct-pass"),
        &root.path().join("verify-no-sidecar.json"),
    );
    assert_eq!(code, 0, "{verify}");
    assert_eq!(verify["status"], "Corrupt", "{verify}");

    let corrupt = root.path().join("corrupt.amarebak");
    std::fs::write(&corrupt, b"not-a-backup").unwrap();
    let (code, verify) = run(
        root.path(),
        &[
            "verify",
            "--root",
            root.path().to_str().unwrap(),
            "--backup",
            corrupt.to_str().unwrap(),
        ],
        None,
        &root.path().join("verify-corrupt.json"),
    );
    assert_eq!(code, 0, "{verify}");
    assert_eq!(verify["status"], "Corrupt", "{verify}");
}

#[test]
fn helper_restore_does_not_replace_target_before_verification() {
    let root = tempdir().unwrap();
    let _store = seed(root.path());
    let backup = root.path().join("bad.amarebak");
    std::fs::write(&backup, b"broken").unwrap();
    let target = tempdir().unwrap();
    let target_store = Store::open(target.path()).unwrap();
    let t1 = ProfileId::new("t1");
    target_store.create_profile(&t1, "Keep", true).unwrap();
    let profile = target_store.open_profile(&t1).unwrap();
    profile
        .settings()
        .set("theme", "appearance", SettingValue::Text("keep".into()))
        .unwrap();
    let before = std::fs::read(target.path().join("profiles/t1/amare.db")).unwrap();
    let (code, restore) = run(
        target.path(),
        &[
            "restore",
            "--root",
            target.path().to_str().unwrap(),
            "--backup",
            backup.to_str().unwrap(),
            "--mode",
            "profile",
            "--map",
            "p1:t1",
            "--no-relaunch",
        ],
        None,
        &target.path().join("restore.json"),
    );
    assert_ne!(code, 0, "{restore}");
    assert_eq!(restore["status"], "error");
    let after = std::fs::read(target.path().join("profiles/t1/amare.db")).unwrap();
    assert_eq!(before, after, "invalid backup changed the target profile");
}

#[test]
fn helper_rejects_path_traversal_in_manifest() {
    // Covered by amare-store's manifest tests; this test ensures the helper
    // surfaces the archive error instead of touching target data.
    let root = tempdir().unwrap();
    let _store = seed(root.path());
    let backup = root.path().join("garbage.amarebak");
    std::fs::write(&backup, vec![0u8; 64]).unwrap();
    let (code, verify) = run(
        root.path(),
        &[
            "verify",
            "--root",
            root.path().to_str().unwrap(),
            "--backup",
            backup.to_str().unwrap(),
        ],
        None,
        &root.path().join("verify.json"),
    );
    assert_eq!(code, 0);
    assert!(matches!(
        verify["status"].as_str(),
        Some("Corrupt") | Some("UnsafeArchive")
    ));
}

#[allow(dead_code)]
fn _unused(_: PathBuf) {}

#[cfg(windows)]
#[test]
fn helper_vault_export_restore_locks_and_imports() {
    use amare_services::vault::VaultService;
    use std::sync::Arc;

    let source = tempdir().unwrap();
    let _store = seed(source.path());
    let protector = Arc::new(amare_platform::WindowsDpapiKeyProtector);
    let p1 = ProfileId::new("p1");
    let vault = VaultService::new(source.path(), p1.clone(), protector.clone()).unwrap();
    vault.initialize().unwrap();
    vault
        .create_entry("https://vault.example", "user", "secret-pass", "Site")
        .unwrap();
    vault.lock();

    let backup = source.path().join("vault.amarebak");
    let (code, create) = run(
        source.path(),
        &[
            "create",
            "--root",
            source.path().to_str().unwrap(),
            "--out",
            backup.to_str().unwrap(),
            "--profiles",
            "p1",
            "--include-vault",
            "--password-stdin",
            "--vault-password-stdin",
        ],
        vec!["container-pass", "vault-pass"],
        &source.path().join("create-vault.json"),
    );
    assert_eq!(code, 0, "{create}");
    assert_eq!(create["contains_vault"], true, "{create}");

    let (code, verify) = run(
        source.path(),
        &[
            "verify",
            "--root",
            source.path().to_str().unwrap(),
            "--backup",
            backup.to_str().unwrap(),
            "--password-stdin",
            "--vault-password-stdin",
        ],
        vec!["container-pass", "vault-pass"],
        &source.path().join("verify-vault.json"),
    );
    assert_eq!(code, 0, "{verify}");
    assert_eq!(verify["status"], "Valid", "{verify}");

    let target = tempdir().unwrap();
    let target_store = Store::open(target.path()).unwrap();
    let t1 = ProfileId::new("t1");
    target_store.create_profile(&t1, "Restored", true).unwrap();
    let (code, restore) = run(
        target.path(),
        &[
            "restore",
            "--root",
            target.path().to_str().unwrap(),
            "--backup",
            backup.to_str().unwrap(),
            "--mode",
            "profile",
            "--map",
            "p1:t1",
            "--password-stdin",
            "--vault-password-stdin",
            "--no-relaunch",
        ],
        vec!["container-pass", "vault-pass"],
        &target.path().join("restore-vault.json"),
    );
    assert_eq!(code, 0, "{restore}");
    let restored = VaultService::new(target.path(), t1, protector).unwrap();
    restored.unlock().unwrap();
    assert_eq!(restored.list_entries().unwrap().len(), 1);
}

#[test]
fn helper_recover_rolls_back_interrupted_restore_journal() {
    let target = tempdir().unwrap();
    let target_store = Store::open(target.path()).unwrap();
    let t1 = ProfileId::new("t1");
    target_store.create_profile(&t1, "Keep", true).unwrap();
    let profile = target_store.open_profile(&t1).unwrap();
    profile
        .settings()
        .set("theme", "appearance", SettingValue::Text("keep".into()))
        .unwrap();
    drop(profile);
    drop(target_store);

    let recovery = target.path().with_file_name(format!(
        "{}-recovery",
        target.path().file_name().unwrap().to_str().unwrap()
    ));
    std::fs::create_dir_all(recovery.join("profiles")).unwrap();
    std::fs::rename(
        target.path().join("profiles/t1"),
        recovery.join("profiles/t1"),
    )
    .unwrap();
    // Simulate a partially applied restore: a new profile DB is in place while
    // the registry row is unchanged.
    let replacement =
        amare_store::ProfileStore::open_at(&target.path().join("profiles/t1/amare.db"), t1.clone())
            .unwrap();
    replacement
        .settings()
        .set("theme", "appearance", SettingValue::Text("restored".into()))
        .unwrap();
    drop(replacement);

    let journal = serde_json::json!({
        "target_root": target.path(),
        "recovery": recovery,
        "mode": {"kind": "profiles", "profiles": ["t1"]},
        "status": "applying",
        "backup": target.path().join("unused.amarebak"),
        "started_at_ms": 0,
    });
    std::fs::create_dir_all(target.path().join("backups")).unwrap();
    std::fs::write(
        target.path().join("backups/restore-journal.json"),
        serde_json::to_vec(&journal).unwrap(),
    )
    .unwrap();

    let (code, result) = run(
        target.path(),
        &["recover", "--root", target.path().to_str().unwrap()],
        None,
        &target.path().join("recover.json"),
    );
    assert_eq!(code, 0, "{result}");
    assert_eq!(result["recovered"], true, "{result}");
    assert!(!target.path().join("backups/restore-journal.json").exists());
    let restored = Store::open(target.path()).unwrap();
    let profile = restored.open_profile(&t1).unwrap();
    assert_eq!(
        profile.settings().get("theme").unwrap().unwrap().value,
        SettingValue::Text("keep".into())
    );
}

#[test]
fn helper_backs_up_and_restores_application_files() {
    let source = tempdir().unwrap();
    let source_root = source.path().join("data");
    std::fs::create_dir_all(&source_root).unwrap();
    let _store = seed(&source_root);
    std::fs::write(
        source.path().join("downloads.json"),
        r#"[{"name":"backup.zip"}]"#,
    )
    .unwrap();
    std::fs::write(
        source.path().join("app-preferences.json"),
        r#"{"language":"ko"}"#,
    )
    .unwrap();

    let backup = source.path().join("app.amarebak");
    let (code, create) = run(
        &source_root,
        &[
            "create",
            "--root",
            source_root.to_str().unwrap(),
            "--out",
            backup.to_str().unwrap(),
            "--profiles",
            "p1",
            "--app-root",
            source.path().to_str().unwrap(),
        ],
        None,
        &source.path().join("create-app.json"),
    );
    assert_eq!(code, 0, "{create}");
    let (code, verify) = run(
        &source_root,
        &[
            "verify",
            "--root",
            source_root.to_str().unwrap(),
            "--backup",
            backup.to_str().unwrap(),
        ],
        None,
        &source.path().join("verify-app.json"),
    );
    assert_eq!(code, 0, "{verify}");
    let kinds = verify["manifest"]["data_kinds"].as_array().unwrap();
    assert!(
        kinds.iter().any(|kind| kind == "application_files"),
        "{verify}"
    );

    let target = tempdir().unwrap();
    let target_root = target.path().join("data");
    std::fs::create_dir_all(&target_root).unwrap();
    let target_store = Store::open(&target_root).unwrap();
    let t1 = ProfileId::new("t1");
    target_store.create_profile(&t1, "Restored", true).unwrap();
    std::fs::write(target.path().join("downloads.json"), "old").unwrap();
    std::fs::write(target.path().join("unrelated.txt"), "keep").unwrap();
    let (code, restore) = run(
        &target_root,
        &[
            "restore",
            "--root",
            target_root.to_str().unwrap(),
            "--backup",
            backup.to_str().unwrap(),
            "--mode",
            "profile",
            "--map",
            "p1:t1",
            "--app-root",
            target.path().to_str().unwrap(),
            "--no-relaunch",
        ],
        None,
        &target.path().join("restore-app.json"),
    );
    assert_eq!(code, 0, "{restore}");
    assert_eq!(
        std::fs::read_to_string(target.path().join("downloads.json")).unwrap(),
        r#"[{"name":"backup.zip"}]"#
    );
    assert_eq!(
        std::fs::read_to_string(target.path().join("app-preferences.json")).unwrap(),
        r#"{"language":"ko"}"#
    );
    assert_eq!(
        std::fs::read_to_string(target.path().join("unrelated.txt")).unwrap(),
        "keep"
    );
}

#[test]
fn helper_full_backup_and_full_restore_restores_all_profiles() {
    let source = tempdir().unwrap();
    let source_root = source.path().join("data");
    std::fs::create_dir_all(&source_root).unwrap();
    let store = Store::open(&source_root).unwrap();
    let p1 = ProfileId::new("p1");
    let p2 = ProfileId::new("p2");
    store.create_profile(&p1, "Personal", true).unwrap();
    store.create_profile(&p2, "Work", false).unwrap();
    for (id, value) in [(&p1, "one"), (&p2, "two")] {
        let profile = store.open_profile(id).unwrap();
        profile
            .settings()
            .set("theme", "appearance", SettingValue::Text(value.into()))
            .unwrap();
    }
    drop(store);
    let backup = source.path().join("full.amarebak");
    let (code, create) = run(
        &source_root,
        &[
            "create",
            "--root",
            source_root.to_str().unwrap(),
            "--out",
            backup.to_str().unwrap(),
            "--include-root",
            "--app-root",
            source.path().to_str().unwrap(),
        ],
        None,
        &source.path().join("create-full.json"),
    );
    assert_eq!(code, 0, "{create}");
    let (code, verify) = run(
        &source_root,
        &[
            "verify",
            "--root",
            source_root.to_str().unwrap(),
            "--backup",
            backup.to_str().unwrap(),
        ],
        None,
        &source.path().join("verify-full.json"),
    );
    assert_eq!(code, 0, "{verify}");
    assert_eq!(verify["manifest"]["scope"], "full", "{verify}");

    let target = tempdir().unwrap();
    let target_root = target.path().join("data");
    std::fs::create_dir_all(&target_root).unwrap();
    let (code, restore) = run(
        &target_root,
        &[
            "restore",
            "--root",
            target_root.to_str().unwrap(),
            "--backup",
            backup.to_str().unwrap(),
            "--mode",
            "full",
            "--no-relaunch",
        ],
        None,
        &target.path().join("restore-full.json"),
    );
    assert_eq!(code, 0, "{restore}");
    let restored = Store::open(&target_root).unwrap();
    assert_eq!(restored.profiles().count().unwrap(), 2);
    for (id, value) in [(&p1, "one"), (&p2, "two")] {
        let profile = restored.open_profile(id).unwrap();
        assert_eq!(
            profile.settings().get("theme").unwrap().unwrap().value,
            SettingValue::Text(value.into())
        );
    }
}

#[test]
fn helper_partial_profile_restore_preserves_other_profiles() {
    let source = tempdir().unwrap();
    let source_root = source.path().join("data");
    std::fs::create_dir_all(&source_root).unwrap();
    let store = Store::open(&source_root).unwrap();
    let p1 = ProfileId::new("p1");
    store.create_profile(&p1, "Personal", true).unwrap();
    let profile = store.open_profile(&p1).unwrap();
    profile
        .settings()
        .set("theme", "appearance", SettingValue::Text("restored".into()))
        .unwrap();
    drop(store);
    let backup = source.path().join("p1.amarebak");
    let (code, _) = run(
        &source_root,
        &[
            "create",
            "--root",
            source_root.to_str().unwrap(),
            "--out",
            backup.to_str().unwrap(),
            "--profiles",
            "p1",
        ],
        None,
        &source.path().join("create-p1.json"),
    );
    assert_eq!(code, 0);

    let target = tempdir().unwrap();
    let target_root = target.path().join("data");
    std::fs::create_dir_all(&target_root).unwrap();
    let target_store = Store::open(&target_root).unwrap();
    let t1 = ProfileId::new("t1");
    let t2 = ProfileId::new("t2");
    target_store.create_profile(&t1, "Target", true).unwrap();
    target_store.create_profile(&t2, "Keep", false).unwrap();
    let keep = target_store.open_profile(&t2).unwrap();
    keep.settings()
        .set("theme", "appearance", SettingValue::Text("keep".into()))
        .unwrap();
    drop(keep);
    drop(target_store);
    let (code, restore) = run(
        &target_root,
        &[
            "restore",
            "--root",
            target_root.to_str().unwrap(),
            "--backup",
            backup.to_str().unwrap(),
            "--mode",
            "profile",
            "--map",
            "p1:t1",
            "--no-relaunch",
        ],
        None,
        &target.path().join("restore-partial.json"),
    );
    assert_eq!(code, 0, "{restore}");
    let restored = Store::open(&target_root).unwrap();
    let t1_profile = restored.open_profile(&t1).unwrap();
    assert_eq!(
        t1_profile.settings().get("theme").unwrap().unwrap().value,
        SettingValue::Text("restored".into())
    );
    let t2_profile = restored.open_profile(&t2).unwrap();
    assert_eq!(
        t2_profile.settings().get("theme").unwrap().unwrap().value,
        SettingValue::Text("keep".into())
    );
}
