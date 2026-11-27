//! Standalone backup/restore helper for the WinUI shell.
//!
//! Usage:
//!   amare-backup create  --root DIR [--out FILE] [--profiles a,b]
//!                        [--include-root] [--include-vault] [--password-stdin]
//!   amare-backup verify  --root DIR --backup FILE [--password-stdin]
//!   amare-backup restore --root DIR --backup FILE [--password-stdin]
//!                        [--mode full|profile|mapped] [--map src:tgt]
//!                        [--parent-pid N] [--app-exe PATH] [--result FILE]
//!
//! Passwords are read from stdin only and are never written to disk or logs.

use amare_core::ProfileId;
use amare_security::KeyProtector;
use amare_services::vault::{VaultService, validate_export};
use amare_store::{
    BackupCredential, BackupData, BackupEncryption, BackupError, BackupManager, BackupManifest,
    BackupOptions, RestoreMode, RestoreOptions, Store,
};
use fs2::FileExt;
use rusqlite::Connection;
use serde_json::{Value, json};
use std::collections::{BTreeMap, HashMap};
use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use zeroize::Zeroizing;

fn main() {
    let mut args = std::env::args().skip(1);
    let command = args.next().unwrap_or_default();
    let options = parse_args(args.collect());
    let result = match command.as_str() {
        "create" => create(&options),
        "verify" => verify(&options),
        "restore" => restore(&options),
        "recover" => recover(&options),
        _ => Err("usage: amare-backup create|verify|restore|recover [options]".to_string()),
    };
    let (code, payload) = match result {
        Ok(value) => (0, value),
        Err(error) => (1, json!({"status": "error", "error": error})),
    };
    write_result(&options, &payload);
    let parent_pid = values(&options, "parent-pid")
        .first()
        .and_then(|value| value.parse::<u32>().ok())
        .unwrap_or(0);
    if command == "restore"
        && !flag(&options, "no-relaunch")
        && (parent_pid == 0 || !process_alive(parent_pid))
    {
        if let Some(exe) = optional_path(&options, "app-exe") {
            let _ = std::process::Command::new(&exe)
                .current_dir(exe.parent().unwrap_or(Path::new(".")))
                .spawn();
        }
    }
    std::process::exit(code);
}

#[derive(serde::Serialize, serde::Deserialize)]
struct RestoreJournal {
    target_root: String,
    recovery: String,
    mode: RestoreMode,
    status: String,
    backup: String,
    started_at_ms: i64,
    #[serde(default)]
    app_backup: String,
    #[serde(default)]
    app_root: String,
    #[serde(default)]
    staging: String,
}

const APP_FILES: [&str; 4] = [
    "downloads.json",
    "app-preferences.json",
    "update-preferences.json",
    "ai-api-settings.json",
];

fn journal_path(root: &Path) -> PathBuf {
    root.join("backups").join("restore-journal.json")
}

fn write_journal(root: &Path, journal: &RestoreJournal) -> Result<(), String> {
    let path = journal_path(root);
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).map_err(|error| error.to_string())?;
    }
    let temp = path.with_extension("tmp");
    fs::write(
        &temp,
        serde_json::to_vec_pretty(journal).map_err(|error| error.to_string())?,
    )
    .map_err(|error| error.to_string())?;
    fs::rename(&temp, &path).map_err(|error| error.to_string())?;
    Ok(())
}

fn read_journal(root: &Path) -> Result<Option<RestoreJournal>, String> {
    let path = journal_path(root);
    if !path.exists() {
        return Ok(None);
    }
    let bytes = fs::read(&path).map_err(|error| error.to_string())?;
    serde_json::from_slice(&bytes)
        .map(Some)
        .map_err(|error| error.to_string())
}

fn recover(options: &HashMap<String, Vec<String>>) -> Result<Value, String> {
    let root = required_path(options, "root")?;
    let _lock = acquire_operation_lock(&root)?;
    let manager = BackupManager::new(&root).map_err(|error| error.to_string())?;
    let Some(journal) = read_journal(&root)? else {
        return Ok(json!({"status": "ok", "recovered": false}));
    };
    if journal.status == "succeeded" {
        let _ = fs::remove_file(journal_path(&root));
        return Ok(json!({"status": "ok", "recovered": false}));
    }
    if journal.status == "preparing" || journal.status == "prepared" {
        if !journal.staging.is_empty() {
            let _ = fs::remove_dir_all(&journal.staging);
        }
        if !journal.app_backup.is_empty() {
            let _ = fs::remove_dir_all(&journal.app_backup);
        }
        let _ = fs::remove_file(journal_path(&root));
        return Ok(json!({"status": "ok", "recovered": false}));
    }
    let recovery = PathBuf::from(&journal.recovery);
    if !recovery.exists() {
        // The crash happened before old data was moved; there is nothing to
        // roll back. Keep the target untouched.
        let _ = fs::remove_file(journal_path(&root));
        return Ok(json!({"status": "ok", "recovered": false}));
    }
    manager
        .revert_restore(&journal.target_root, &recovery, &journal.mode)
        .map_err(|error| format!("restore recovery failed: {error}"))?;
    if !journal.app_backup.is_empty() && !journal.app_root.is_empty() {
        let app_backup = PathBuf::from(&journal.app_backup);
        if app_backup.exists() {
            restore_app_backup(Path::new(&journal.app_root), &app_backup)?;
            let _ = fs::remove_dir_all(app_backup);
        }
    }
    let _ = fs::remove_file(journal_path(&root));
    Ok(json!({"status": "ok", "recovered": true}))
}

fn create(options: &HashMap<String, Vec<String>>) -> Result<Value, String> {
    let root = required_path(options, "root")?;
    let _lock = acquire_operation_lock(&root)?;
    let store = Store::open(&root).map_err(|error| error.to_string())?;
    let manager = BackupManager::new(&root).map_err(|error| error.to_string())?;
    let password = read_password(options, "password-stdin")?;
    let include_vault = flag(options, "include-vault");
    if include_vault && password.is_empty() {
        return Err("a Vault backup password is required".into());
    }
    let include_root = flag(options, "include-root");
    let profiles = if include_root {
        None
    } else {
        let ids = values(options, "profiles")
            .into_iter()
            .flat_map(|value| value.split(',').map(str::to_string).collect::<Vec<_>>())
            .filter(|value| !value.trim().is_empty())
            .map(|value| ProfileId::new(value.trim().to_string()))
            .collect::<Vec<_>>();
        if ids.is_empty() {
            return Err("--profiles is required for a profile backup".into());
        }
        Some(ids)
    };
    let selected = match &profiles {
        Some(ids) => ids.clone(),
        None => store
            .profiles()
            .list(500, 0)
            .map_err(|error| error.to_string())?
            .items
            .into_iter()
            .map(|profile| profile.id)
            .collect(),
    };
    let mut vault_exports = BTreeMap::new();
    if include_vault {
        for profile in &selected {
            let key_path = root.join("vault").join(format!("{}.key", profile.as_str()));
            if !key_path.exists() {
                return Err(format!(
                    "Vault is not initialized for profile {}; exclude Vault or initialize it first",
                    profile.as_str()
                ));
            }
            let vault = VaultService::new(&root, profile.clone(), protector()?)
                .map_err(|error| format!("vault open failed: {error}"))?;
            vault
                .initialize()
                .map_err(|error| format!("vault unlock failed: {error}"))?;
            let payload = vault
                .export_backup(&password)
                .map_err(|error| format!("vault export failed: {error}"))?;
            vault.lock();
            vault_exports.insert(profile.as_str().to_string(), payload);
        }
    }
    let encryption = if password.is_empty() {
        BackupEncryption::None
    } else {
        BackupEncryption::Password(password.to_string())
    };
    let backup_options = BackupOptions {
        include_root,
        profiles,
        data: BackupData {
            // New backups never embed the DPAPI-bound ciphertext without its
            // key. Vault data is embedded only as a password-protected export.
            secrets: false,
            ..BackupData::default()
        },
        encryption,
        compression: true,
    };
    let destination = optional_path(options, "out");
    let app_files = match optional_path(options, "app-root") {
        Some(app_root) => read_app_files(&app_root)?,
        None => BTreeMap::new(),
    };
    let record = manager
        .create_backup_to(
            &store,
            &backup_options,
            destination.as_deref(),
            &vault_exports,
            &app_files,
        )
        .map_err(|error| error.to_string())?;
    let path = destination.unwrap_or_else(|| manager.backup_dir().join(&record.file_name));
    Ok(json!({
        "status": "ok",
        "path": path,
        "record": record,
        "sha256": record.sha256,
        "size_bytes": record.size_bytes,
        "contains_vault": record.contains_vault,
    }))
}

fn verify(options: &HashMap<String, Vec<String>>) -> Result<Value, String> {
    let root = required_path(options, "root")?;
    let backup = required_path(options, "backup")?;
    let manager = BackupManager::new(&root).map_err(|error| error.to_string())?;
    if !sidecar_present(&backup) {
        return Ok(json!({
            "status": "Corrupt",
            "error": "backup .sha256 sidecar is missing; copy it together with the backup file",
        }));
    }
    let password = read_password(options, "password-stdin")?;
    let credential = credential(&password);
    match manager.decode_files(&backup, &credential) {
        Ok((manifest, files)) => {
            let mut vault_summaries = Vec::new();
            for profile in &manifest.vault_profiles {
                let path = format!("vault/{profile}.amarevault");
                let bytes = files
                    .get(&path)
                    .ok_or_else(|| format!("missing vault export: {path}"))?;
                if password.is_empty() {
                    return Ok(json!({
                        "status": "InvalidPassword",
                        "error": "Vault backup password is required",
                        "manifest": manifest,
                    }));
                }
                let summary = match validate_export(bytes, &password) {
                    Ok(summary) => summary,
                    Err(error) => {
                        return Ok(json!({
                            "status": vault_status(&error),
                            "error": error.to_string(),
                        }));
                    }
                };
                vault_summaries.push(json!({
                    "profile": profile,
                    "format": summary.format,
                    "generation": summary.generation,
                    "entry_count": summary.entry_count,
                    "tombstone_count": summary.tombstone_count,
                }));
            }
            let warning = manifest.contains_secrets && !manifest.contains_vault;
            Ok(json!({
                "status": if warning { "ValidWithWarnings" } else { "Valid" },
                "warnings": if warning {
                    vec!["This legacy backup contains DPAPI-bound Vault ciphertext without a portable Vault export."]
                } else {
                    Vec::<&str>::new()
                },
                "manifest": manifest,
                "vault": vault_summaries,
            }))
        }
        Err(error) => Ok(json!({
            "status": verification_status(&error),
            "error": error.to_string(),
        })),
    }
}

fn restore(options: &HashMap<String, Vec<String>>) -> Result<Value, String> {
    let root = required_path(options, "root")?;
    let _lock = acquire_operation_lock(&root)?;
    let backup = required_path(options, "backup")?;
    let parent_pid = values(options, "parent-pid")
        .first()
        .and_then(|value| value.parse::<u32>().ok())
        .unwrap_or(0);
    let manager = BackupManager::new(&root).map_err(|error| error.to_string())?;
    if !sidecar_present(&backup) {
        return Err(
            "backup .sha256 sidecar is missing; copy it together with the backup file".into(),
        );
    }
    let password = read_password(options, "password-stdin")?;
    let credential = credential(&password);
    let (manifest, files) = manager
        .decode_files(&backup, &credential)
        .map_err(|error| error.to_string())?;
    let mode = parse_mode(options)?;
    if let Some(previous) = read_journal(&root)? {
        if previous.status != "succeeded" {
            let recovery = PathBuf::from(&previous.recovery);
            if recovery.exists() {
                manager
                    .revert_restore(&previous.target_root, &recovery, &previous.mode)
                    .map_err(|error| format!("previous restore recovery failed: {error}"))?;
            }
            if !previous.app_backup.is_empty() && !previous.app_root.is_empty() {
                let app_backup = PathBuf::from(&previous.app_backup);
                if app_backup.exists() {
                    let _ = restore_app_backup(Path::new(&previous.app_root), &app_backup);
                    let _ = fs::remove_dir_all(app_backup);
                }
            }
            let _ = fs::remove_file(journal_path(&root));
        }
    }
    let target_root = root.clone();
    let mut staging = target_root.with_file_name(format!(
        ".{}.restore-{}-{}",
        target_root
            .file_name()
            .and_then(|name| name.to_str())
            .unwrap_or("amare"),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis(),
        std::process::id()
    ));
    let mut recovery = target_root.with_file_name(format!(
        "{}.restore-recovery-{}-{}",
        target_root
            .file_name()
            .and_then(|name| name.to_str())
            .unwrap_or("amare"),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis(),
        std::process::id()
    ));
    if staging.exists() {
        staging = staging.with_extension("a");
    }
    if recovery.exists() {
        recovery = recovery.with_extension("a");
    }
    let app_root = optional_path(options, "app-root");
    let app_payloads = if app_root.is_some() {
        app_payloads(&files)
    } else {
        BTreeMap::new()
    };
    let app_backup_dir = match &app_root {
        Some(path) => backup_app_files(path, app_payloads.keys())?,
        None => PathBuf::new(),
    };
    let mut journal = RestoreJournal {
        target_root: target_root.to_string_lossy().to_string(),
        recovery: recovery.to_string_lossy().to_string(),
        mode: mode.clone(),
        status: "preparing".into(),
        backup: backup.to_string_lossy().to_string(),
        started_at_ms: std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis() as i64,
        app_backup: app_backup_dir.to_string_lossy().to_string(),
        app_root: app_root
            .as_ref()
            .map(|path| path.to_string_lossy().to_string())
            .unwrap_or_default(),
        staging: staging.to_string_lossy().to_string(),
    };
    write_journal(&root, &journal)?;
    write_progress(
        &root,
        "staging",
        "Preparing and validating backup staging",
        true,
    );
    let target_ids = restore_target_ids(&mode);
    let vault_payloads = vault_payloads(&manifest, &files)?;

    // Preserve existing Vault ciphertext when the backup does not contain a
    // Vault export. The key file is left untouched.
    let preserved = if vault_payloads.is_empty() {
        preserve_existing_secrets(&root, &target_ids)?
    } else {
        BTreeMap::new()
    };
    let old_keys = backup_vault_keys(&root, &target_ids)?;
    let prepared = match manager.prepare_restore(
        &backup,
        &root,
        &RestoreOptions {
            credential,
            mode: mode.clone(),
        },
        &staging,
        &recovery,
    ) {
        Ok(prepared) => prepared,
        Err(error) => {
            let _ = fs::remove_dir_all(&staging);
            let _ = fs::remove_file(journal_path(&root));
            if let Some(app_root) = &app_root {
                let _ = restore_app_backup(app_root, &app_backup_dir);
            }
            write_progress(&root, "failed", &format!("Staging failed: {error}"), false);
            return Err(format!("backup preparation failed: {error}"));
        }
    };
    journal.status = "prepared".into();
    write_journal(&root, &journal)?;
    write_progress(&root, "staged", "Validated staging is ready to apply", true);
    wait_for_parent_exit(parent_pid, 60)?;
    journal.status = "applying".into();
    write_journal(&root, &journal)?;
    write_progress(&root, "applying", "Applying the validated restore", false);
    let report = match manager.apply_prepared_restore(&root, &prepared) {
        Ok(report) => report,
        Err(error) => {
            if let Some(app_root) = &app_root {
                let _ = restore_app_backup(app_root, &app_backup_dir);
            }
            write_progress(&root, "failed", &format!("Apply failed: {error}"), false);
            return Err(format!("backup apply failed: {error}"));
        }
    };
    write_progress(
        &root,
        "finalizing",
        "Restoring application files and Vault",
        false,
    );
    let mut warnings = Vec::new();
    if let (Some(app_root), false) = (&app_root, app_payloads.is_empty()) {
        if let Err(error) = restore_app_files(app_root, &app_payloads) {
            if let Some(recovery) = &report.recovery_dir {
                let _ = manager.revert_restore(&root, recovery, &mode);
            }
            let _ = restore_app_backup(app_root, &app_backup_dir);
            let _ = fs::remove_file(journal_path(&root));
            return Err(format!("application files restore failed: {error}"));
        }
    }
    if !preserved.is_empty() {
        if let Err(error) = restore_preserved_secrets(&root, &preserved) {
            if let Some(recovery) = &report.recovery_dir {
                let _ = manager.revert_restore(&root, recovery, &mode);
            }
            let _ = fs::remove_file(journal_path(&root));
            let _ = restore_vault_keys(&root, &old_keys);
            if let Some(app_root) = &app_root {
                let _ = restore_app_backup(app_root, &app_backup_dir);
            }
            return Err(format!("preserving existing Vault failed: {error}"));
        }
        warnings.push("Vault was not in the backup; the existing Vault was preserved.");
    }
    if !vault_payloads.is_empty() {
        if password.is_empty() {
            if let Some(recovery) = &report.recovery_dir {
                let _ = manager.revert_restore(&root, recovery, &mode);
            }
            let _ = fs::remove_file(journal_path(&root));
            if let Some(app_root) = &app_root {
                let _ = restore_app_backup(app_root, &app_backup_dir);
            }
            return Err("Vault backup password is required".into());
        }
        if let Err(error) = import_vaults(&root, &mode, &vault_payloads, &password) {
            let _ = restore_vault_keys(&root, &old_keys);
            if let Some(recovery) = &report.recovery_dir {
                let _ = manager.revert_restore(&root, recovery, &mode);
            }
            let _ = fs::remove_file(journal_path(&root));
            if let Some(app_root) = &app_root {
                let _ = restore_app_backup(app_root, &app_backup_dir);
            }
            return Err(format!("Vault restore failed: {error}"));
        }
        warnings.push("Vault was restored from the encrypted export and is locked.");
    }
    let _ = fs::remove_dir_all(&app_backup_dir);
    if let Some(profile) = values(options, "open-profile").first() {
        let path = root.join("backups").join("open-profile.json");
        let temp = path.with_extension("tmp");
        let value = json!({"profile_id": profile});
        if fs::write(&temp, serde_json::to_vec(&value).unwrap_or_default()).is_ok() {
            let _ = fs::rename(temp, path);
        }
    }
    let mut completed = journal;
    completed.status = "succeeded".into();
    let _ = write_journal(&root, &completed);
    write_progress(
        &root,
        "completed",
        "Restore completed; restarting AMARE",
        false,
    );
    let result = json!({
        "status": "ok",
        "restored_profiles": report.restored_profiles,
        "recovery_dir": report.recovery_dir,
        "verified": report.verified,
        "vault_imported": vault_payloads.keys().collect::<Vec<_>>(),
        "warnings": warnings,
    });
    // Publish the result before relaunching so the new app can display it.
    write_result(options, &result);
    // Relaunch is handled by main() for both success and failure so a failed
    // restore never leaves the user without a running application.
    Ok(result)
}

fn parse_mode(options: &HashMap<String, Vec<String>>) -> Result<RestoreMode, String> {
    let mode = values(options, "mode")
        .first()
        .cloned()
        .unwrap_or_else(|| "profile".into());
    match mode.as_str() {
        "full" => Ok(RestoreMode::Full),
        "profile" => {
            let map = parse_map(options)?;
            if map.is_empty() {
                return Err("--map source:target is required for profile restore".into());
            }
            Ok(RestoreMode::ProfilesMapped(map))
        }
        "mapped" => {
            let map = parse_map(options)?;
            if map.is_empty() {
                return Err("--map source:target is required for mapped restore".into());
            }
            Ok(RestoreMode::ProfilesMapped(map))
        }
        _ => Err(format!("unsupported restore mode: {mode}")),
    }
}

fn parse_map(
    options: &HashMap<String, Vec<String>>,
) -> Result<Vec<(ProfileId, ProfileId)>, String> {
    let mut map = Vec::new();
    for value in values(options, "map") {
        for entry in value.split(',') {
            let (source, target) = entry
                .split_once(':')
                .ok_or_else(|| format!("invalid profile map: {entry}"))?;
            map.push((
                ProfileId::new(source.trim().to_string()),
                ProfileId::new(target.trim().to_string()),
            ));
        }
    }
    Ok(map)
}

fn restore_target_ids(mode: &RestoreMode) -> Vec<ProfileId> {
    match mode {
        RestoreMode::Full => Vec::new(),
        RestoreMode::Profiles(ids) => ids.clone(),
        RestoreMode::ProfilesMapped(map) => map.iter().map(|(_, target)| target.clone()).collect(),
    }
}

fn vault_payloads(
    manifest: &BackupManifest,
    files: &BTreeMap<String, Vec<u8>>,
) -> Result<BTreeMap<String, Vec<u8>>, String> {
    let mut payloads = BTreeMap::new();
    for profile in &manifest.vault_profiles {
        let path = format!("vault/{profile}.amarevault");
        let bytes = files
            .get(&path)
            .ok_or_else(|| format!("missing vault export: {path}"))?
            .clone();
        payloads.insert(profile.clone(), bytes);
    }
    Ok(payloads)
}

fn import_vaults(
    root: &Path,
    mode: &RestoreMode,
    payloads: &BTreeMap<String, Vec<u8>>,
    password: &str,
) -> Result<(), String> {
    let targets = match mode {
        RestoreMode::Full => payloads.keys().cloned().collect::<Vec<_>>(),
        RestoreMode::Profiles(ids) => ids.iter().map(|id| id.as_str().to_string()).collect(),
        RestoreMode::ProfilesMapped(map) => map
            .iter()
            .map(|(source, _)| source.as_str().to_string())
            .collect(),
    };
    for source in targets {
        let payload = payloads
            .get(&source)
            .ok_or_else(|| format!("no vault export for profile {source}"))?;
        let target = match mode {
            RestoreMode::ProfilesMapped(map) => map
                .iter()
                .find(|(source_id, _)| source_id.as_str() == source)
                .map(|(_, target)| target.clone())
                .ok_or_else(|| format!("no target mapping for profile {source}"))?,
            _ => ProfileId::new(source.clone()),
        };
        let vault = VaultService::new(root, target.clone(), protector()?)
            .map_err(|error| format!("vault open failed for {}: {error}", target.as_str()))?;
        vault
            .initialize()
            .map_err(|error| format!("vault initialize failed for {}: {error}", target.as_str()))?;
        vault
            .import_backup(payload, password)
            .map_err(|error| format!("vault import failed for {}: {error}", target.as_str()))?;
        vault.lock();
    }
    Ok(())
}

fn preserve_existing_secrets(
    root: &Path,
    profile_ids: &[ProfileId],
) -> Result<BTreeMap<String, PathBuf>, String> {
    let temp = root
        .join("backups")
        .join(format!(".restore-secrets-{}", std::process::id()));
    fs::create_dir_all(&temp).map_err(|error| error.to_string())?;
    let mut preserved = BTreeMap::new();
    for id in profile_ids {
        let db = root.join("profiles").join(id.as_str()).join("amare.db");
        if !db.exists() {
            continue;
        }
        let copy = temp.join(format!("{}.db", id.as_str()));
        let conn = Connection::open(&db).map_err(|error| error.to_string())?;
        conn.execute("VACUUM INTO ?1", [copy.to_string_lossy().to_string()])
            .map_err(|error| {
                format!(
                    "could not snapshot existing Vault for {}: {error}",
                    id.as_str()
                )
            })?;
        preserved.insert(id.as_str().to_string(), copy);
    }
    Ok(preserved)
}

fn restore_preserved_secrets(
    root: &Path,
    preserved: &BTreeMap<String, PathBuf>,
) -> Result<(), String> {
    for (id, copy) in preserved {
        let db = root.join("profiles").join(id).join("amare.db");
        let conn = Connection::open(&db).map_err(|error| error.to_string())?;
        conn.execute(
            "ATTACH DATABASE ?1 AS old",
            [copy.to_string_lossy().to_string()],
        )
        .map_err(|error| error.to_string())?;
        conn.execute(
            "INSERT OR REPLACE INTO secrets SELECT * FROM old.secrets",
            [],
        )
        .map_err(|error| format!("could not preserve Vault for profile {id}: {error}"))?;
        let _ = conn.execute("DETACH DATABASE old", []);
    }
    Ok(())
}

fn backup_vault_keys(
    root: &Path,
    profile_ids: &[ProfileId],
) -> Result<BTreeMap<String, PathBuf>, String> {
    let temp = root
        .join("backups")
        .join(format!(".restore-keys-{}", std::process::id()));
    fs::create_dir_all(&temp).map_err(|error| error.to_string())?;
    let mut keys = BTreeMap::new();
    for id in profile_ids {
        let key = root.join("vault").join(format!("{}.key", id.as_str()));
        if !key.exists() {
            continue;
        }
        let copy = temp.join(format!("{}.key", id.as_str()));
        fs::copy(&key, &copy).map_err(|error| error.to_string())?;
        keys.insert(id.as_str().to_string(), copy);
    }
    Ok(keys)
}

fn restore_vault_keys(root: &Path, keys: &BTreeMap<String, PathBuf>) -> Result<(), String> {
    for (id, copy) in keys {
        let key = root.join("vault").join(format!("{id}.key"));
        if let Some(parent) = key.parent() {
            let _ = fs::create_dir_all(parent);
        }
        fs::copy(copy, &key).map_err(|error| error.to_string())?;
    }
    Ok(())
}

fn vault_status(error: &amare_services::vault::VaultError) -> &'static str {
    use amare_services::vault::VaultError;
    match error {
        VaultError::BackupAuth => "InvalidPassword",
        VaultError::BackupUnsupported(_) => "UnsupportedVersion",
        _ => "Corrupt",
    }
}

fn sidecar_present(path: &Path) -> bool {
    PathBuf::from(format!("{}.sha256", path.display())).exists()
}

fn verification_status(error: &BackupError) -> &'static str {
    match error {
        BackupError::UnsupportedSchema(_) => "IncompatibleSchema",
        BackupError::UnsupportedVersion(_) => "UnsupportedVersion",
        BackupError::WrongCredential | BackupError::CredentialRequired => "InvalidPassword",
        BackupError::UnsafePath(_) => "UnsafeArchive",
        BackupError::InvalidFormat(message)
            if message.contains("unsafe") || message.contains("path") =>
        {
            "UnsafeArchive"
        }
        BackupError::Integrity(_) | BackupError::Json(_) | BackupError::Sqlite(_) => "Corrupt",
        _ => "Corrupt",
    }
}

fn credential(password: &str) -> BackupCredential {
    if password.is_empty() {
        BackupCredential::None
    } else {
        BackupCredential::Password(password.to_string())
    }
}

fn read_password(
    options: &HashMap<String, Vec<String>>,
    name: &str,
) -> Result<Zeroizing<String>, String> {
    if !flag(options, name) {
        return Ok(Zeroizing::new(String::new()));
    }
    let mut input = String::new();
    std::io::stdin()
        .read_to_string(&mut input)
        .map_err(|error| error.to_string())?;
    let value = input
        .lines()
        .next()
        .unwrap_or_default()
        .trim_end_matches('\r')
        .to_string();
    Ok(Zeroizing::new(value))
}

fn wait_for_parent_exit(pid: u32, seconds: u64) -> Result<(), String> {
    if pid == 0 {
        return Ok(());
    }
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(seconds);
    while std::time::Instant::now() < deadline {
        if !process_alive(pid) {
            return Ok(());
        }
        std::thread::sleep(std::time::Duration::from_millis(200));
    }
    Err("timed out waiting for the running AMARE process to exit".into())
}

fn process_alive(pid: u32) -> bool {
    if pid == 0 {
        return false;
    }
    #[cfg(windows)]
    {
        let output = std::process::Command::new("tasklist")
            .args(["/FI", &format!("PID eq {pid}"), "/FO", "CSV", "/NH"])
            .output();
        match output {
            Ok(output) => {
                let text = String::from_utf8_lossy(&output.stdout);
                let expected = pid.to_string();
                text.lines().any(|line| {
                    line.split(',')
                        .nth(1)
                        .map(|value| value.trim_matches('"') == expected)
                        .unwrap_or(false)
                })
            }
            Err(_) => false,
        }
    }
    #[cfg(not(windows))]
    {
        Path::new(&format!("/proc/{pid}")).exists()
    }
}

fn protector() -> Result<Arc<dyn KeyProtector>, String> {
    #[cfg(windows)]
    {
        Ok(Arc::new(amare_platform::WindowsDpapiKeyProtector))
    }
    #[cfg(not(windows))]
    {
        Err("Vault key protection is only available on Windows".into())
    }
}

fn read_app_files(app_root: &Path) -> Result<BTreeMap<String, Vec<u8>>, String> {
    let mut files = BTreeMap::new();
    for name in APP_FILES {
        let path = app_root.join(name);
        if path.exists() {
            files.insert(
                name.to_string(),
                fs::read(&path).map_err(|error| error.to_string())?,
            );
        }
    }
    Ok(files)
}

fn app_payloads(files: &BTreeMap<String, Vec<u8>>) -> BTreeMap<String, Vec<u8>> {
    files
        .iter()
        .filter_map(|(path, bytes)| {
            path.strip_prefix("app/")
                .map(|name| (name.to_string(), bytes.clone()))
        })
        .collect()
}

fn backup_app_files<'a>(
    app_root: &Path,
    names: impl Iterator<Item = &'a String>,
) -> Result<PathBuf, String> {
    let directory = app_root
        .join("backups")
        .join(format!(".restore-app-{}", std::process::id()));
    fs::create_dir_all(&directory).map_err(|error| error.to_string())?;
    for name in names {
        let source = app_root.join(name);
        if source.exists() {
            fs::copy(&source, directory.join(name)).map_err(|error| error.to_string())?;
        }
    }
    Ok(directory)
}

fn restore_app_files(app_root: &Path, payloads: &BTreeMap<String, Vec<u8>>) -> Result<(), String> {
    for (name, bytes) in payloads {
        let target = app_root.join(name);
        let temporary = target.with_extension(format!("tmp-{}", std::process::id()));
        fs::write(&temporary, bytes).map_err(|error| error.to_string())?;
        fs::rename(&temporary, &target).map_err(|error| error.to_string())?;
    }
    Ok(())
}

fn restore_app_backup(app_root: &Path, directory: &Path) -> Result<(), String> {
    for name in APP_FILES {
        let source = directory.join(name);
        if source.exists() {
            fs::copy(&source, app_root.join(name)).map_err(|error| error.to_string())?;
        }
    }
    Ok(())
}

fn progress_path(root: &Path) -> PathBuf {
    root.join("backups").join("restore-progress.json")
}

fn write_progress(root: &Path, stage: &str, detail: &str, cancellable: bool) {
    let path = progress_path(root);
    if let Some(parent) = path.parent() {
        let _ = fs::create_dir_all(parent);
    }
    let value = json!({
        "stage": stage,
        "detail": detail,
        "cancellable": cancellable,
        "updated_at_ms": std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis() as i64,
    });
    let temp = path.with_extension("tmp");
    if fs::write(&temp, serde_json::to_vec(&value).unwrap_or_default()).is_ok() {
        let _ = fs::rename(temp, path);
    }
}

fn acquire_operation_lock(root: &Path) -> Result<fs::File, String> {
    let directory = root.join("backups");
    fs::create_dir_all(&directory).map_err(|error| error.to_string())?;
    let path = directory.join(".backup.lock");
    let file = fs::OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(&path)
        .map_err(|error| error.to_string())?;
    file.try_lock_exclusive()
        .map_err(|_| "another backup or restore operation is already running".to_string())?;
    Ok(file)
}

fn write_result(options: &HashMap<String, Vec<String>>, value: &Value) {
    let text = serde_json::to_string_pretty(value).unwrap_or_else(|_| "{}".into());
    if let Some(path) = optional_path(options, "result") {
        if let Some(parent) = path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        let _ = fs::write(path, &text);
    }
    let _ = writeln!(std::io::stdout(), "{text}");
}

fn parse_args(args: Vec<String>) -> HashMap<String, Vec<String>> {
    let mut map: HashMap<String, Vec<String>> = HashMap::new();
    let mut index = 0;
    while index < args.len() {
        let arg = &args[index];
        if let Some(name) = arg.strip_prefix("--") {
            if index + 1 < args.len() && !args[index + 1].starts_with("--") {
                map.entry(name.to_string())
                    .or_default()
                    .push(args[index + 1].clone());
                index += 2;
                continue;
            }
            map.entry(name.to_string()).or_default();
        }
        index += 1;
    }
    map
}

fn flag(options: &HashMap<String, Vec<String>>, name: &str) -> bool {
    options.contains_key(name)
}

fn values(options: &HashMap<String, Vec<String>>, name: &str) -> Vec<String> {
    options.get(name).cloned().unwrap_or_default()
}

fn required_path(options: &HashMap<String, Vec<String>>, name: &str) -> Result<PathBuf, String> {
    optional_path(options, name).ok_or_else(|| format!("--{name} is required"))
}

fn optional_path(options: &HashMap<String, Vec<String>>, name: &str) -> Option<PathBuf> {
    values(options, name).first().map(PathBuf::from)
}
