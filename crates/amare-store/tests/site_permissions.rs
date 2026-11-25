use amare_core::{ProfileId, SitePermissionDecision, SitePermissionKind, SitePermissionScope};
use amare_store::normalize_site_origin;
use amare_store::{Store, StoreError};
use tempfile::tempdir;

fn setup() -> (tempfile::TempDir, Store, ProfileId) {
    let dir = tempdir().unwrap();
    let store = Store::open(dir.path()).unwrap();
    let id = ProfileId::new("p1");
    store.create_profile(&id, "Personal", true).unwrap();
    (dir, store, id)
}

#[test]
fn permission_crud_and_origin_normalization() {
    let (_dir, store, id) = setup();
    let profile = store.open_profile(&id).unwrap();
    let permissions = profile.site_permissions();

    assert!(permissions.list().unwrap().is_empty());
    let created = permissions
        .set(
            "https://EXAMPLE.com:443/path",
            SitePermissionKind::Microphone,
            SitePermissionDecision::Allow,
            SitePermissionScope::Persistent,
            0,
        )
        .unwrap();
    assert_eq!(created.origin, "https://example.com");
    assert_eq!(created.revision, 1);
    assert_eq!(created.scope, SitePermissionScope::Persistent);

    let found = permissions
        .get("https://example.com", SitePermissionKind::Microphone)
        .unwrap()
        .unwrap();
    assert_eq!(found, created);

    permissions
        .set(
            "https://example.com:444",
            SitePermissionKind::Camera,
            SitePermissionDecision::Deny,
            SitePermissionScope::Persistent,
            0,
        )
        .unwrap();
    assert_eq!(permissions.list().unwrap().len(), 2);

    let updated = permissions
        .set(
            "https://example.com",
            SitePermissionKind::Microphone,
            SitePermissionDecision::Deny,
            SitePermissionScope::Persistent,
            1,
        )
        .unwrap();
    assert_eq!(updated.revision, 2);
    assert_eq!(updated.decision, SitePermissionDecision::Deny);
    assert!(matches!(
        permissions.set(
            "https://example.com",
            SitePermissionKind::Microphone,
            SitePermissionDecision::Allow,
            SitePermissionScope::Persistent,
            1,
        ),
        Err(StoreError::Conflict {
            expected: Some(1),
            actual: Some(2)
        })
    ));
    assert!(matches!(
        permissions.set(
            "https://example.com",
            SitePermissionKind::Microphone,
            SitePermissionDecision::Allow,
            SitePermissionScope::Persistent,
            0,
        ),
        Err(StoreError::Conflict {
            expected: Some(0),
            actual: Some(2)
        })
    ));

    assert!(matches!(
        permissions.delete("https://example.com", SitePermissionKind::Microphone, 1),
        Err(StoreError::Conflict {
            expected: Some(1),
            actual: Some(2)
        })
    ));
    permissions
        .delete("https://example.com", SitePermissionKind::Microphone, 2)
        .unwrap();
    assert!(
        permissions
            .get("https://example.com", SitePermissionKind::Microphone)
            .unwrap()
            .is_none()
    );

    assert_eq!(
        permissions.clear_origin("https://example.com:444").unwrap(),
        1
    );
    assert_eq!(permissions.clear_all().unwrap(), 0);
}

#[test]
fn permission_origin_validation_is_exact() {
    assert_eq!(
        normalize_site_origin("https://example.com:443").unwrap(),
        "https://example.com"
    );
    assert_eq!(
        normalize_site_origin("https://bücher.example").unwrap(),
        "https://xn--bcher-kva.example"
    );
    assert_ne!(
        normalize_site_origin("https://example.com").unwrap(),
        normalize_site_origin("https://www.example.com").unwrap()
    );
    assert_ne!(
        normalize_site_origin("https://example.com").unwrap(),
        normalize_site_origin("https://example.com:444").unwrap()
    );
    for invalid in [
        "http://example.com",
        "https://user:pass@example.com",
        "https://example.com.",
        "https://example.com ",
        "https://example.com.\t",
        "data:text/html,x",
        "file:///etc/passwd",
        "javascript:alert(1)",
        "",
        "not a url",
    ] {
        assert!(
            normalize_site_origin(invalid).is_err(),
            "expected rejection: {invalid:?}"
        );
    }
}

#[test]
fn permission_profile_isolation() {
    let (_dir, store, first_id) = setup();
    let second_id = ProfileId::new("p2");
    store.create_profile(&second_id, "Work", false).unwrap();
    let first = store.open_profile(&first_id).unwrap();
    let second = store.open_profile(&second_id).unwrap();

    first
        .site_permissions()
        .set(
            "https://example.com",
            SitePermissionKind::Camera,
            SitePermissionDecision::Allow,
            SitePermissionScope::Persistent,
            0,
        )
        .unwrap();
    assert_eq!(first.site_permissions().list().unwrap().len(), 1);
    assert!(second.site_permissions().list().unwrap().is_empty());
    second
        .site_permissions()
        .set(
            "https://example.com",
            SitePermissionKind::Camera,
            SitePermissionDecision::Deny,
            SitePermissionScope::Persistent,
            0,
        )
        .unwrap();
    assert_eq!(
        first.site_permissions().list().unwrap()[0].decision,
        SitePermissionDecision::Allow
    );
    assert_eq!(
        second.site_permissions().list().unwrap()[0].decision,
        SitePermissionDecision::Deny
    );
}

#[test]
fn permission_scope_kind_and_reset() {
    let (_dir, store, id) = setup();
    let profile = store.open_profile(&id).unwrap();
    let permissions = profile.site_permissions();
    for kind in SitePermissionKind::ALL {
        permissions
            .set(
                "https://example.com",
                *kind,
                SitePermissionDecision::Allow,
                SitePermissionScope::Persistent,
                0,
            )
            .unwrap();
    }
    assert_eq!(
        permissions.list().unwrap().len(),
        SitePermissionKind::ALL.len()
    );
    assert!(matches!(
        permissions.set(
            "https://example.com",
            SitePermissionKind::Camera,
            SitePermissionDecision::Allow,
            SitePermissionScope::Session,
            0,
        ),
        Err(StoreError::InvalidInput(_))
    ));

    profile.reset_user_data().unwrap();
    assert!(permissions.list().unwrap().is_empty());
}

#[test]
fn permission_conflict_for_missing_or_stale_rows() {
    let (_dir, store, id) = setup();
    let profile = store.open_profile(&id).unwrap();
    let permissions = profile.site_permissions();
    assert!(matches!(
        permissions.set(
            "https://example.com",
            SitePermissionKind::Camera,
            SitePermissionDecision::Allow,
            SitePermissionScope::Persistent,
            9,
        ),
        Err(StoreError::Conflict {
            expected: Some(9),
            actual: None
        })
    ));
    assert!(matches!(
        permissions.delete("https://example.com", SitePermissionKind::Camera, 0),
        Err(StoreError::NotFound(_))
    ));
}
