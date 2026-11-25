use crate::db::{now_ms, write_transaction};
use crate::error::StoreError;
use amare_core::{
    ProfileId, SitePermission, SitePermissionDecision, SitePermissionKind, SitePermissionScope,
};
use rusqlite::{Connection, OptionalExtension, Row, params};
use url::Url;

/// Canonical HTTPS origin for site permissions. Scheme, hostname and port are
/// exact; there is no subdomain inheritance. HTTP, userinfo, trailing dots and
/// opaque origins are rejected.
pub fn normalize_site_origin(value: &str) -> Result<String, StoreError> {
    if value.is_empty() || value.trim() != value {
        return Err(StoreError::InvalidInput("invalid permission origin".into()));
    }
    let parsed = Url::parse(value)
        .map_err(|_| StoreError::InvalidInput("invalid permission origin".into()))?;
    if parsed.scheme() != "https" || !parsed.username().is_empty() || parsed.password().is_some() {
        return Err(StoreError::InvalidInput(
            "permission origin must be HTTPS without userinfo".into(),
        ));
    }
    let host = parsed
        .host_str()
        .ok_or_else(|| StoreError::InvalidInput("permission origin has no host".into()))?;
    if host.is_empty() || host.ends_with('.') {
        return Err(StoreError::InvalidInput(
            "permission origin has an invalid host".into(),
        ));
    }
    let origin = parsed.origin().ascii_serialization();
    if origin == "null" || origin.is_empty() {
        return Err(StoreError::InvalidInput(
            "permission origin is opaque".into(),
        ));
    }
    Ok(origin)
}

pub struct SitePermissionRepository<'a> {
    conn: &'a Connection,
    profile_id: &'a ProfileId,
}

impl<'a> SitePermissionRepository<'a> {
    pub(crate) fn new(conn: &'a Connection, profile_id: &'a ProfileId) -> Self {
        Self { conn, profile_id }
    }

    pub fn list(&self) -> Result<Vec<SitePermission>, StoreError> {
        let mut statement = self.conn.prepare(
            "SELECT origin, permission_kind, decision, scope, revision,
                    created_at_ms, updated_at_ms
             FROM site_permissions
             WHERE profile_id = ?1
             ORDER BY origin ASC, permission_kind ASC",
        )?;
        let rows = statement.query_map(params![self.profile_id.as_str()], |row| {
            permission_from_row(row, self.profile_id)
        })?;
        rows.collect::<Result<Vec<_>, _>>()
            .map_err(StoreError::from)
    }

    pub fn get(
        &self,
        origin: &str,
        kind: SitePermissionKind,
    ) -> Result<Option<SitePermission>, StoreError> {
        let origin = normalize_site_origin(origin)?;
        self.get_canonical(&origin, kind)
    }

    /// Insert or update one permission using optimistic concurrency.
    ///
    /// `expected_revision == 0` means "insert only". A positive value must match
    /// the existing row's revision. Session decisions are never stored.
    pub fn set(
        &self,
        origin: &str,
        kind: SitePermissionKind,
        decision: SitePermissionDecision,
        scope: SitePermissionScope,
        expected_revision: i64,
    ) -> Result<SitePermission, StoreError> {
        if scope != SitePermissionScope::Persistent {
            return Err(StoreError::InvalidInput(
                "session permissions must stay in memory".into(),
            ));
        }
        if expected_revision < 0 {
            return Err(StoreError::InvalidInput(
                "expected permission revision is invalid".into(),
            ));
        }
        let origin = normalize_site_origin(origin)?;
        let now = now_ms();
        let tx = write_transaction(self.conn)?;
        let existing: Option<(i64, i64)> = tx
            .query_row(
                "SELECT revision, created_at_ms
                 FROM site_permissions
                 WHERE profile_id = ?1 AND origin = ?2 AND permission_kind = ?3",
                params![self.profile_id.as_str(), origin, kind.as_str()],
                |row| Ok((row.get(0)?, row.get(1)?)),
            )
            .optional()?;
        let (revision, created_at_ms) = match (existing, expected_revision) {
            (None, 0) => (1, now),
            (None, expected) => {
                return Err(StoreError::Conflict {
                    expected: Some(expected),
                    actual: None,
                });
            }
            (Some((current, _)), 0) => {
                return Err(StoreError::Conflict {
                    expected: Some(0),
                    actual: Some(current),
                });
            }
            (Some((current, created)), expected) if current == expected => (current + 1, created),
            (Some((current, _)), expected) => {
                return Err(StoreError::Conflict {
                    expected: Some(expected),
                    actual: Some(current),
                });
            }
        };
        tx.execute(
            "INSERT INTO site_permissions
                (profile_id, origin, permission_kind, decision, scope, revision,
                 created_at_ms, updated_at_ms)
             VALUES (?1, ?2, ?3, ?4, 'persistent', ?5, ?6, ?7)
             ON CONFLICT(profile_id, origin, permission_kind) DO UPDATE SET
                decision = excluded.decision,
                revision = excluded.revision,
                updated_at_ms = excluded.updated_at_ms",
            params![
                self.profile_id.as_str(),
                origin,
                kind.as_str(),
                decision.as_str(),
                revision,
                created_at_ms,
                now
            ],
        )?;
        tx.commit()?;
        Ok(SitePermission {
            profile_id: self.profile_id.clone(),
            origin,
            kind,
            decision,
            scope: SitePermissionScope::Persistent,
            revision,
            created_at_ms,
            updated_at_ms: now,
        })
    }

    pub fn delete(
        &self,
        origin: &str,
        kind: SitePermissionKind,
        expected_revision: i64,
    ) -> Result<(), StoreError> {
        let origin = normalize_site_origin(origin)?;
        let existing: Option<i64> = self
            .conn
            .query_row(
                "SELECT revision FROM site_permissions
                 WHERE profile_id = ?1 AND origin = ?2 AND permission_kind = ?3",
                params![self.profile_id.as_str(), origin, kind.as_str()],
                |row| row.get(0),
            )
            .optional()?;
        let current =
            existing.ok_or_else(|| StoreError::NotFound("site permission not found".into()))?;
        if current != expected_revision {
            return Err(StoreError::Conflict {
                expected: Some(expected_revision),
                actual: Some(current),
            });
        }
        self.conn.execute(
            "DELETE FROM site_permissions
             WHERE profile_id = ?1 AND origin = ?2 AND permission_kind = ?3",
            params![self.profile_id.as_str(), origin, kind.as_str()],
        )?;
        Ok(())
    }

    pub fn clear_origin(&self, origin: &str) -> Result<usize, StoreError> {
        let origin = normalize_site_origin(origin)?;
        let changed = self.conn.execute(
            "DELETE FROM site_permissions WHERE profile_id = ?1 AND origin = ?2",
            params![self.profile_id.as_str(), origin],
        )?;
        Ok(changed)
    }

    pub fn clear_all(&self) -> Result<usize, StoreError> {
        let changed = self.conn.execute(
            "DELETE FROM site_permissions WHERE profile_id = ?1",
            params![self.profile_id.as_str()],
        )?;
        Ok(changed)
    }

    fn get_canonical(
        &self,
        origin: &str,
        kind: SitePermissionKind,
    ) -> Result<Option<SitePermission>, StoreError> {
        self.conn
            .query_row(
                "SELECT origin, permission_kind, decision, scope, revision,
                        created_at_ms, updated_at_ms
                 FROM site_permissions
                 WHERE profile_id = ?1 AND origin = ?2 AND permission_kind = ?3",
                params![self.profile_id.as_str(), origin, kind.as_str()],
                |row| permission_from_row(row, self.profile_id),
            )
            .optional()
            .map_err(StoreError::from)
    }
}

fn permission_from_row(row: &Row<'_>, profile_id: &ProfileId) -> rusqlite::Result<SitePermission> {
    let origin: String = row.get(0)?;
    let kind_text: String = row.get(1)?;
    let decision_text: String = row.get(2)?;
    let scope_text: String = row.get(3)?;
    let kind = SitePermissionKind::parse(&kind_text).ok_or_else(|| {
        rusqlite::Error::InvalidColumnType(1, "permission_kind".into(), rusqlite::types::Type::Text)
    })?;
    let decision = SitePermissionDecision::parse(&decision_text).ok_or_else(|| {
        rusqlite::Error::InvalidColumnType(2, "decision".into(), rusqlite::types::Type::Text)
    })?;
    let scope = SitePermissionScope::parse(&scope_text).ok_or_else(|| {
        rusqlite::Error::InvalidColumnType(3, "scope".into(), rusqlite::types::Type::Text)
    })?;
    Ok(SitePermission {
        profile_id: profile_id.clone(),
        origin,
        kind,
        decision,
        scope,
        revision: row.get(4)?,
        created_at_ms: row.get(5)?,
        updated_at_ms: row.get(6)?,
    })
}
