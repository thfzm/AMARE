use amare_core::ProfileId;
use amare_security::{InMemoryKeyProtector, KeyFile, KeyProtectionError, KeyProtector, encrypt};
use amare_services::vault::{VaultBackupSummary, VaultEntry, VaultError, VaultService};
use amare_store::Store;
use rusqlite::{Connection, params};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use tempfile::TempDir;

const PROFILE: &str = "p1";
const KEY_CONTEXT: &str = "amare.winui.password-vault.key.v1:p1";
const VAULT_KEY: &str = "password-vault";

fn fixture(profile: &str) -> (TempDir, PathBuf, ProfileId, Arc<InMemoryKeyProtector>) {
    let dir = tempfile::tempdir().unwrap();
    let root = dir.path().to_path_buf();
    let id = ProfileId::new(profile);
    {
        let store = Store::open(&root).unwrap();
        store.create_profile(&id, "Personal", true).unwrap();
    }
    let protector = Arc::new(InMemoryKeyProtector::random());
    (dir, root, id, protector)
}

fn service(
    root: &Path,
    profile: &ProfileId,
    protector: &Arc<InMemoryKeyProtector>,
) -> VaultService {
    VaultService::new(root, profile.clone(), protector.clone()).unwrap()
}

fn db_path(root: &Path, profile: &ProfileId) -> PathBuf {
    Store::open(root).unwrap().profile_db_path(profile).unwrap()
}

fn read_ciphertext(root: &Path, profile: &ProfileId) -> Vec<u8> {
    let conn = Connection::open(db_path(root, profile)).unwrap();
    conn.query_row(
        "SELECT ciphertext FROM secrets WHERE key = ?1",
        params![VAULT_KEY],
        |row| row.get(0),
    )
    .unwrap()
}

fn write_ciphertext(root: &Path, profile: &ProfileId, ciphertext: &[u8]) {
    let conn = Connection::open(db_path(root, profile)).unwrap();
    let changed = conn
        .execute(
            "UPDATE secrets SET ciphertext = ?1 WHERE key = ?2",
            params![ciphertext, VAULT_KEY],
        )
        .unwrap();
    assert_eq!(changed, 1);
}

fn aad(profile: &str) -> Vec<u8> {
    format!("amare.winui.password-vault.v1:{profile}").into_bytes()
}

fn entry_view_count(service: &VaultService) -> usize {
    service.list_entries().unwrap().len()
}

fn create(service: &VaultService, origin: &str, user: &str, password: &str) -> VaultEntry {
    service
        .create_entry(origin, user, password, "Example")
        .unwrap()
}

#[test]
fn initialize_creates_key_and_empty_vault_without_schema_change() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    let status = vault.initialize().unwrap();
    assert!(status.initialized);
    assert!(status.unlocked);
    assert_eq!(status.entry_count, 0);
    assert_eq!(status.generation, 1);
    assert!(vault.key_path().exists(), "key file must be created");
    assert!(read_ciphertext(&root, &profile).len() > 24);

    let store = Store::open(&root).unwrap();
    let profile_store = store.open_profile(&profile).unwrap();
    assert_eq!(profile_store.schema_version().unwrap(), 5);
    let conn = Connection::open(db_path(&root, &profile)).unwrap();
    let vault_tables: u32 = conn
        .query_row(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'vault%'",
            [],
            |row| row.get(0),
        )
        .unwrap();
    assert_eq!(vault_tables, 0, "no new schema table is introduced");
}

#[test]
fn status_reports_existing_vault_before_unlock() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    {
        let vault = service(&root, &profile, &protector);
        vault.initialize().unwrap();
        create(&vault, "https://example.com", "alice", "secret");
    }

    let reopened = service(&root, &profile, &protector);
    let status = reopened.status().unwrap();
    assert!(status.initialized, "persisted vault must be detected");
    assert!(!status.unlocked, "reopened vault must stay locked");
    assert_eq!(status.entry_count, 0);
    assert!(status.key_present);

    let unlocked = reopened.unlock().unwrap();
    assert!(unlocked.unlocked);
    assert_eq!(unlocked.entry_count, 1);
}

#[test]
fn create_update_delete_keep_generation_and_revision_monotonic() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    vault.initialize().unwrap();

    let created = create(&vault, "https://example.com/path", "alice", "secret-1");
    assert_eq!(created.origin, "https://example.com");
    assert_eq!(created.revision, 1);
    assert_eq!(vault.status().unwrap().generation, 2);
    assert_eq!(entry_view_count(&vault), 1);
    assert_eq!(vault.get_entry(created.id).unwrap().password, "secret-1");

    let updated = vault
        .update_entry(
            created.id,
            1,
            "https://example.com",
            "alice",
            "secret-2",
            "Updated",
        )
        .unwrap();
    assert_eq!(updated.revision, 2);
    assert_eq!(vault.status().unwrap().generation, 3);
    assert_eq!(vault.get_entry(created.id).unwrap().password, "secret-2");

    let revision = vault.delete_entry(created.id, 2).unwrap();
    assert_eq!(revision, 3);
    assert_eq!(vault.status().unwrap().generation, 4);
    assert_eq!(vault.status().unwrap().entry_count, 0);
    assert_eq!(vault.status().unwrap().tombstone_count, 1);
    assert!(matches!(
        vault.get_entry(created.id),
        Err(VaultError::NotFound)
    ));

    vault.unlock().unwrap();
    assert_eq!(vault.status().unwrap().entry_count, 0);
    assert_eq!(vault.status().unwrap().tombstone_count, 1);
}

#[test]
fn locked_vault_rejects_reads_and_writes() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    vault.initialize().unwrap();
    let created = create(&vault, "https://example.com", "alice", "secret");
    vault.lock();
    let status = vault.status().unwrap();
    assert!(!status.unlocked);
    assert_eq!(status.entry_count, 0);
    assert!(matches!(vault.list_entries(), Err(VaultError::Locked)));
    assert!(matches!(
        vault.get_entry(created.id),
        Err(VaultError::Locked)
    ));
    assert!(matches!(
        vault.create_entry("https://example.com", "bob", "pw", "B"),
        Err(VaultError::Locked)
    ));
    assert!(matches!(
        vault.export_backup("backup-password"),
        Err(VaultError::Locked)
    ));
}

#[test]
fn update_and_delete_reject_stale_revision_without_mutation() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    vault.initialize().unwrap();
    let created = create(&vault, "https://example.com", "alice", "secret");
    vault
        .update_entry(
            created.id,
            1,
            "https://example.com",
            "alice",
            "secret-2",
            "Two",
        )
        .unwrap();

    assert!(matches!(
        vault.update_entry(
            created.id,
            1,
            "https://example.com",
            "alice",
            "stale",
            "Stale"
        ),
        Err(VaultError::Conflict {
            expected: 1,
            actual: 2
        })
    ));
    assert_eq!(vault.get_entry(created.id).unwrap().password, "secret-2");

    assert!(matches!(
        vault.delete_entry(created.id, 1),
        Err(VaultError::Conflict {
            expected: 1,
            actual: 2
        })
    ));
    assert_eq!(entry_view_count(&vault), 1);
}

#[test]
fn origin_is_normalized_and_invalid_origins_are_rejected() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    vault.initialize().unwrap();
    let created = create(&vault, "https://Bücher.example:443/login", "alice", "pw");
    assert_eq!(created.origin, "https://xn--bcher-kva.example");
    assert!(matches!(
        vault.create_entry("javascript:alert(1)", "a", "p", "t"),
        Err(VaultError::InvalidOrigin)
    ));
    assert!(matches!(
        vault.create_entry("https://user:pass@example.com", "a", "p", "t"),
        Err(VaultError::InvalidOrigin)
    ));
}

#[test]
fn wrong_key_does_not_change_existing_vault() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    vault.initialize().unwrap();
    create(&vault, "https://example.com", "alice", "secret");
    vault.lock();
    let before = read_ciphertext(&root, &profile);

    let wrong_key = [9u8; 32];
    KeyFile::new(vault.key_path())
        .store(protector.as_ref(), KEY_CONTEXT, &wrong_key)
        .unwrap();
    let key_before = std::fs::read(vault.key_path()).unwrap();

    assert!(matches!(vault.unlock(), Err(VaultError::Auth)));
    assert_eq!(read_ciphertext(&root, &profile), before);
    assert_eq!(std::fs::read(vault.key_path()).unwrap(), key_before);
}

#[test]
fn ciphertext_and_tag_tamper_are_detected_without_overwrite() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    vault.initialize().unwrap();
    create(&vault, "https://example.com", "alice", "secret");
    vault.lock();
    let before = read_ciphertext(&root, &profile);

    let mut middle = before.clone();
    middle[24 + 1] ^= 1;
    write_ciphertext(&root, &profile, &middle);
    assert!(matches!(vault.unlock(), Err(VaultError::Auth)));
    assert_eq!(read_ciphertext(&root, &profile), middle);

    let mut tag = before.clone();
    let last = tag.len() - 1;
    tag[last] ^= 1;
    write_ciphertext(&root, &profile, &tag);
    assert!(matches!(vault.unlock(), Err(VaultError::Auth)));
    assert_eq!(read_ciphertext(&root, &profile), tag);
}

#[test]
fn wrong_format_and_wrong_aad_are_rejected() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    vault.initialize().unwrap();
    create(&vault, "https://example.com", "alice", "secret");
    vault.lock();

    let key = KeyFile::new(vault.key_path())
        .load(protector.as_ref(), KEY_CONTEXT)
        .unwrap();
    let wrong_format = br#"{"format":2,"generation":1,"entries":[],"tombstones":[]}"#;
    write_ciphertext(
        &root,
        &profile,
        &encrypt(&key, wrong_format, &aad(PROFILE)).unwrap(),
    );
    assert!(matches!(
        vault.unlock(),
        Err(VaultError::UnsupportedFormat(2))
    ));

    let wrong_profile = "amare.winui.password-vault.v1:p2".to_string();
    write_ciphertext(
        &root,
        &profile,
        &encrypt(&key, wrong_format, wrong_profile.as_bytes()).unwrap(),
    );
    assert!(matches!(vault.unlock(), Err(VaultError::Auth)));
}

#[test]
fn existing_key_without_vault_initializes_empty_without_new_key() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    KeyFile::new(vault.key_path())
        .load_or_create(protector.as_ref(), KEY_CONTEXT)
        .unwrap();
    let before = std::fs::read(vault.key_path()).unwrap();
    let status = vault.initialize().unwrap();
    assert_eq!(status.entry_count, 0);
    assert_eq!(std::fs::read(vault.key_path()).unwrap(), before);
}

#[test]
fn missing_and_corrupt_key_files_are_preserved() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    vault.initialize().unwrap();
    create(&vault, "https://example.com", "alice", "secret");
    vault.lock();
    let before = read_ciphertext(&root, &profile);

    let key_path = vault.key_path().to_path_buf();
    std::fs::remove_file(&key_path).unwrap();
    assert!(matches!(vault.unlock(), Err(VaultError::KeyMissing)));
    assert_eq!(read_ciphertext(&root, &profile), before);

    std::fs::write(&key_path, b"not-a-key").unwrap();
    assert!(matches!(vault.unlock(), Err(VaultError::KeyUnavailable)));
    assert_eq!(read_ciphertext(&root, &profile), before);
}

struct FailingProtector;

impl KeyProtector for FailingProtector {
    fn protect(&self, _: &str, _: &[u8]) -> Result<Vec<u8>, KeyProtectionError> {
        Err(KeyProtectionError::Failed)
    }

    fn unprotect(&self, _: &str, _: &[u8]) -> Result<Vec<u8>, KeyProtectionError> {
        Err(KeyProtectionError::InvalidMaterial)
    }
}

#[test]
fn dpapi_failure_is_reported_without_changing_vault() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    vault.initialize().unwrap();
    create(&vault, "https://example.com", "alice", "secret");
    vault.lock();
    let before = read_ciphertext(&root, &profile);

    let failing = VaultService::new(
        &root,
        profile.clone(),
        Arc::new(FailingProtector) as Arc<dyn KeyProtector>,
    )
    .unwrap();
    assert!(matches!(failing.unlock(), Err(VaultError::KeyUnavailable)));
    assert_eq!(read_ciphertext(&root, &profile), before);
}

#[test]
fn cas_rejects_stale_writer_and_allows_reload() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let first = service(&root, &profile, &protector);
    first.initialize().unwrap();
    let second = service(&root, &profile, &protector);
    second.initialize().unwrap();

    create(&first, "https://example.com", "alice", "secret-a");
    assert!(matches!(
        second.create_entry("https://example.com", "bob", "secret-b", "B"),
        Err(VaultError::Conflict { .. })
    ));
    assert_eq!(second.list_entries().unwrap().len(), 0);

    second.unlock().unwrap();
    second
        .create_entry("https://example.com", "bob", "secret-b", "B")
        .unwrap();
    assert_eq!(first.list_entries().unwrap().len(), 1);
    assert_eq!(second.list_entries().unwrap().len(), 2);
}

#[test]
fn concurrent_writers_do_not_lose_data() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let first = Arc::new(service(&root, &profile, &protector));
    first.initialize().unwrap();
    let second = Arc::new(service(&root, &profile, &protector));
    second.initialize().unwrap();

    let a = {
        let first = first.clone();
        std::thread::spawn(move || {
            first
                .create_entry("https://a.example", "alice", "secret-a", "A")
                .map(|_| ())
        })
    };
    let b = {
        let second = second.clone();
        std::thread::spawn(move || {
            second
                .create_entry("https://b.example", "bob", "secret-b", "B")
                .map(|_| ())
        })
    };
    let results = [a.join().unwrap(), b.join().unwrap()];
    let successes = results.iter().filter(|result| result.is_ok()).count();
    let conflicts = results
        .iter()
        .filter(|result| matches!(result, Err(VaultError::Conflict { .. })))
        .count();
    assert_eq!(successes + conflicts, 2);
    assert!(successes >= 1);
    first.unlock().unwrap();
    assert_eq!(first.list_entries().unwrap().len(), successes);
}

#[test]
fn profile_isolation_and_keys_are_per_profile() {
    let (_dir, root, p1, protector) = fixture(PROFILE);
    let p2 = ProfileId::new("p2");
    Store::open(&root)
        .unwrap()
        .create_profile(&p2, "Work", false)
        .unwrap();
    let first = service(&root, &p1, &protector);
    let second = service(&root, &p2, &protector);
    first.initialize().unwrap();
    second.initialize().unwrap();

    create(&first, "https://one.example", "one", "pw1");
    create(&second, "https://two.example", "two", "pw2");
    assert_eq!(first.list_entries().unwrap().len(), 1);
    assert_eq!(second.list_entries().unwrap().len(), 1);
    assert_ne!(first.key_path(), second.key_path());
    assert_eq!(
        first.list_entries().unwrap()[0].origin,
        "https://one.example"
    );
    assert_eq!(
        second.list_entries().unwrap()[0].origin,
        "https://two.example"
    );
}

#[test]
fn export_import_and_validate_backup() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    vault.initialize().unwrap();
    let created = create(&vault, "https://example.com", "alice", "secret");
    let backup = vault.export_backup("backup-password").unwrap();
    let summary: VaultBackupSummary = vault.validate_backup(&backup, "backup-password").unwrap();
    assert_eq!(summary.format, 1);
    assert_eq!(summary.entry_count, 1);

    vault.delete_entry(created.id, 1).unwrap();
    assert_eq!(vault.list_entries().unwrap().len(), 0);
    let status = vault.import_backup(&backup, "backup-password").unwrap();
    assert_eq!(status.entry_count, 1);
    assert_eq!(
        vault.list_entries().unwrap()[0].origin,
        "https://example.com"
    );

    let before = read_ciphertext(&root, &profile);
    assert!(matches!(
        vault.import_backup(&backup, "wrong-password"),
        Err(VaultError::BackupAuth)
    ));
    assert!(matches!(
        vault.validate_backup(&backup, "wrong-password"),
        Err(VaultError::BackupAuth)
    ));
    assert_eq!(read_ciphertext(&root, &profile), before);

    let mut corrupt = backup.clone();
    let last = corrupt.len() - 1;
    corrupt[last] ^= 1;
    assert!(matches!(
        vault.import_backup(&corrupt, "backup-password"),
        Err(VaultError::BackupAuth)
    ));
    assert_eq!(read_ciphertext(&root, &profile), before);
}

#[test]
fn fill_lookup_rejects_origin_mismatch() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    vault.initialize().unwrap();
    let created = create(&vault, "https://example.com", "alice", "secret");
    assert_eq!(
        vault
            .get_for_fill("https://example.com/login", created.id)
            .unwrap()
            .password,
        "secret"
    );
    assert!(matches!(
        vault.get_for_fill("https://www.example.com/login", created.id),
        Err(VaultError::OriginMismatch)
    ));
    assert!(matches!(
        vault.get_for_fill("http://example.com/login", created.id),
        Err(VaultError::OriginMismatch)
    ));
}

#[test]
fn simultaneous_first_initialize_uses_one_key_file() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let first = Arc::new(service(&root, &profile, &protector));
    let second = Arc::new(service(&root, &profile, &protector));
    let a = {
        let first = first.clone();
        std::thread::spawn(move || first.initialize())
    };
    let b = {
        let second = second.clone();
        std::thread::spawn(move || second.initialize())
    };
    assert!(a.join().unwrap().is_ok());
    assert!(b.join().unwrap().is_ok());
    assert!(first.key_path().exists());
    first.lock();
    first.unlock().unwrap();
    second.lock();
    second.unlock().unwrap();
    assert_eq!(first.list_entries().unwrap().len(), 0);
}

#[test]
fn nonce_is_fresh_and_24_bytes_for_every_save() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    vault.initialize().unwrap();
    create(&vault, "https://one.example", "a", "pw-a");
    let first = read_ciphertext(&root, &profile);
    create(&vault, "https://two.example", "b", "pw-b");
    let second = read_ciphertext(&root, &profile);
    assert!(first.len() > 24 + 16);
    assert!(second.len() > 24 + 16);
    assert_ne!(&first[..24], &second[..24], "nonce must not repeat");
}

#[test]
fn backup_can_move_to_another_profile() {
    let (_dir, root, p1, protector) = fixture(PROFILE);
    let p2 = ProfileId::new("p2");
    Store::open(&root)
        .unwrap()
        .create_profile(&p2, "Work", false)
        .unwrap();
    let source = service(&root, &p1, &protector);
    source.initialize().unwrap();
    create(&source, "https://portable.example", "alice", "secret");
    let backup = source.export_backup("move-me").unwrap();

    let target = service(&root, &p2, &protector);
    target.initialize().unwrap();
    let status = target.import_backup(&backup, "move-me").unwrap();
    assert_eq!(status.entry_count, 1);
    assert_eq!(
        target.list_entries().unwrap()[0].origin,
        "https://portable.example"
    );
}

#[test]
fn a_replaced_key_file_blocks_stale_writer_without_overwriting() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    vault.initialize().unwrap();
    create(&vault, "https://example.com", "alice", "secret");
    let before = read_ciphertext(&root, &profile);
    KeyFile::new(vault.key_path())
        .store(protector.as_ref(), KEY_CONTEXT, &[7u8; 32])
        .unwrap();
    assert!(matches!(
        vault.create_entry("https://example.com", "bob", "pw", "B"),
        Err(VaultError::KeyUnavailable)
    ));
    assert_eq!(read_ciphertext(&root, &profile), before);
}

#[test]
fn a_deleted_key_file_blocks_further_saves_without_overwriting() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    vault.initialize().unwrap();
    create(&vault, "https://example.com", "alice", "secret");
    let before = read_ciphertext(&root, &profile);
    std::fs::remove_file(vault.key_path()).unwrap();
    assert!(matches!(
        vault.create_entry("https://example.com", "bob", "pw", "B"),
        Err(VaultError::KeyMissing)
    ));
    assert_eq!(read_ciphertext(&root, &profile), before);
}

#[cfg(windows)]
#[test]
fn windows_dpapi_vault_roundtrip() {
    use amare_platform::WindowsDpapiKeyProtector;

    let (_dir, root, profile, _) = fixture(PROFILE);
    let protector = Arc::new(WindowsDpapiKeyProtector) as Arc<dyn KeyProtector>;
    let vault = VaultService::new(&root, profile.clone(), protector).unwrap();
    vault.initialize().unwrap();
    create(&vault, "https://dpapi.example", "alice", "secret");
    vault.lock();
    let reopened = VaultService::new(
        &root,
        profile,
        Arc::new(WindowsDpapiKeyProtector) as Arc<dyn KeyProtector>,
    )
    .unwrap();
    reopened.unlock().unwrap();
    assert_eq!(reopened.list_entries().unwrap().len(), 1);
    assert_eq!(
        reopened
            .get_for_fill(
                "https://dpapi.example",
                reopened.list_entries().unwrap()[0].id
            )
            .unwrap()
            .password,
        "secret"
    );
}

#[test]
fn errors_do_not_contain_passwords_or_key_material() {
    let (_dir, root, profile, protector) = fixture(PROFILE);
    let vault = service(&root, &profile, &protector);
    vault.initialize().unwrap();
    create(
        &vault,
        "https://example.com",
        "alice",
        "ultra-secret-password",
    );
    vault.lock();
    let error = vault.list_entries().unwrap_err();
    let rendered = format!("{error:?} {error}");
    assert!(!rendered.contains("ultra-secret-password"));
    assert!(!rendered.contains("password-vault"));
    assert!(!rendered.contains("secret"));
}

#[test]
fn invalid_profile_id_is_rejected_before_any_file_access() {
    let (_dir, root, _profile, protector) = fixture(PROFILE);
    let result = VaultService::new(
        &root,
        ProfileId::new("../p1"),
        protector.clone() as Arc<dyn KeyProtector>,
    );
    assert!(matches!(result, Err(VaultError::InvalidProfile)));
}
