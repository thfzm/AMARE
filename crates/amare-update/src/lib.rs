//! Transactional update prototype (Phase 0.5 logic-level validation).
//!
//! This is **not** a Windows installer. It validates the platform-independent
//! update flow so the Windows installer/updater can reuse the same rules:
//! signature -> hash -> version -> backup -> replace -> failure restore/rollback.
//!
//! The app directory and user data directory are separate. User data is never
//! modified by this module.

use amare_security::{sha256_hex, update_allowed, verify_payload};
use std::fs;
use std::path::{Path, PathBuf};

#[derive(Debug, thiserror::Error)]
pub enum UpdateError {
    #[error("io error: {0}")]
    Io(#[from] std::io::Error),
    #[error("invalid signature")]
    InvalidSignature,
    #[error("payload hash mismatch")]
    HashMismatch,
    #[error("downgrade or invalid version: {0}")]
    InvalidVersion(String),
    #[error("missing file: {0}")]
    MissingFile(PathBuf),
    #[error("simulated install failure after replace")]
    SimulatedInstallFailure,
}

pub struct UpdateRequest<'a> {
    pub app_dir: &'a Path,
    pub data_dir: &'a Path,
    pub package_dir: &'a Path,
    pub public_key: &'a [u8; 32],
    pub version: &'a str,
    pub expected_sha256: &'a str,
    pub signature: &'a [u8],
    /// Test hook: fail after the new app directory has been put in place.
    pub fail_after_replace: bool,
}

/// Apply a signed update package to `app_dir`.
///
/// `package_dir` must contain `payload.bin` and `VERSION`. On success a
/// `*.backup` directory remains next to `app_dir` for explicit rollback.
pub fn apply_update(request: UpdateRequest<'_>) -> Result<PathBuf, UpdateError> {
    let payload_path = request.package_dir.join("payload.bin");
    if !payload_path.exists() {
        return Err(UpdateError::MissingFile(payload_path));
    }
    let payload = fs::read(&payload_path)?;
    let actual_hash = sha256_hex(&payload);
    if actual_hash != request.expected_sha256 {
        return Err(UpdateError::HashMismatch);
    }

    let signed_payload = format!("{}\n{}", request.version, request.expected_sha256);
    if !verify_payload(
        request.public_key,
        signed_payload.as_bytes(),
        request.signature,
    )
    .map_err(|_| UpdateError::InvalidSignature)?
    {
        return Err(UpdateError::InvalidSignature);
    }

    let current_version = read_version(request.app_dir)?;
    if !update_allowed(&current_version, request.version)
        .map_err(|_| UpdateError::InvalidVersion(request.version.to_string()))?
    {
        return Err(UpdateError::InvalidVersion(request.version.to_string()));
    }

    // User data is intentionally untouched.
    if !request.data_dir.exists() {
        fs::create_dir_all(request.data_dir)?;
    }

    let backup_dir = sibling(request.app_dir, "backup");
    let staging_dir = sibling(request.app_dir, "staging");
    remove_dir_if_exists(&backup_dir)?;
    remove_dir_if_exists(&staging_dir)?;

    copy_dir(request.app_dir, &backup_dir)?;
    copy_dir(request.package_dir, &staging_dir)?;

    fs::remove_dir_all(request.app_dir)?;
    fs::rename(&staging_dir, request.app_dir)?;

    if request.fail_after_replace {
        // Simulate a crash/failure after replacement, then restore the backup.
        let _ = fs::remove_dir_all(request.app_dir);
        fs::rename(&backup_dir, request.app_dir)?;
        return Err(UpdateError::SimulatedInstallFailure);
    }

    Ok(backup_dir)
}

/// Restore the previous app directory from a backup produced by `apply_update`.
pub fn rollback(app_dir: &Path, backup_dir: &Path) -> Result<(), UpdateError> {
    if !backup_dir.exists() {
        return Err(UpdateError::MissingFile(backup_dir.to_path_buf()));
    }
    if app_dir.exists() {
        fs::remove_dir_all(app_dir)?;
    }
    fs::rename(backup_dir, app_dir)?;
    Ok(())
}

fn read_version(app_dir: &Path) -> Result<String, UpdateError> {
    let path = app_dir.join("VERSION");
    if !path.exists() {
        return Err(UpdateError::MissingFile(path));
    }
    Ok(fs::read_to_string(path)?.trim().to_string())
}

fn sibling(path: &Path, suffix: &str) -> PathBuf {
    let name = path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("amare-app");
    path.with_file_name(format!("{name}.{suffix}"))
}

fn remove_dir_if_exists(path: &Path) -> Result<(), UpdateError> {
    if path.exists() {
        fs::remove_dir_all(path)?;
    }
    Ok(())
}

fn copy_dir(from: &Path, to: &Path) -> Result<(), UpdateError> {
    fs::create_dir_all(to)?;
    for entry in fs::read_dir(from)? {
        let entry = entry?;
        let file_type = entry.file_type()?;
        let target = to.join(entry.file_name());
        if file_type.is_dir() {
            copy_dir(&entry.path(), &target)?;
        } else if file_type.is_file() {
            fs::copy(entry.path(), target)?;
        } else {
            return Err(UpdateError::Io(std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                "symlinks are not allowed in update packages",
            )));
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use amare_security::{generate_signing_key, sign_payload, verifying_key};
    use std::fs;
    use tempfile::tempdir;

    fn write_app(dir: &Path, version: &str, marker: &str) {
        fs::create_dir_all(dir).unwrap();
        fs::write(dir.join("VERSION"), version).unwrap();
        fs::write(dir.join("marker.txt"), marker).unwrap();
    }

    fn write_package(dir: &Path, version: &str, payload: &[u8]) -> String {
        fs::create_dir_all(dir).unwrap();
        fs::write(dir.join("VERSION"), version).unwrap();
        fs::write(dir.join("payload.bin"), payload).unwrap();
        sha256_hex(payload)
    }

    #[test]
    fn applies_signed_update_and_preserves_user_data() {
        let root = tempdir().unwrap();
        let app = root.path().join("app");
        let data = root.path().join("data");
        let package = root.path().join("package");
        write_app(&app, "0.1.0", "old");
        fs::create_dir_all(&data).unwrap();
        fs::write(data.join("profile.db"), "user-data").unwrap();

        let payload = b"new-app-binary";
        let hash = write_package(&package, "0.1.1", payload);
        let signing = generate_signing_key();
        let public = verifying_key(&signing).unwrap();
        let signed = format!("0.1.1\n{hash}");
        let signature = sign_payload(&signing, signed.as_bytes());

        let backup = apply_update(UpdateRequest {
            app_dir: &app,
            data_dir: &data,
            package_dir: &package,
            public_key: &public,
            version: "0.1.1",
            expected_sha256: &hash,
            signature: &signature,
            fail_after_replace: false,
        })
        .unwrap();

        assert_eq!(
            fs::read_to_string(app.join("VERSION")).unwrap().trim(),
            "0.1.1"
        );
        assert_eq!(
            fs::read_to_string(data.join("profile.db")).unwrap(),
            "user-data"
        );
        assert!(backup.exists());
    }

    #[test]
    fn rejects_bad_signature_before_touching_app() {
        let root = tempdir().unwrap();
        let app = root.path().join("app");
        let data = root.path().join("data");
        let package = root.path().join("package");
        write_app(&app, "0.1.0", "old");
        fs::create_dir_all(&data).unwrap();
        let hash = write_package(&package, "0.1.1", b"new");
        let signing = generate_signing_key();
        let public = verifying_key(&signing).unwrap();
        let signature = sign_payload(&signing, b"wrong-payload");

        let result = apply_update(UpdateRequest {
            app_dir: &app,
            data_dir: &data,
            package_dir: &package,
            public_key: &public,
            version: "0.1.1",
            expected_sha256: &hash,
            signature: &signature,
            fail_after_replace: false,
        });
        assert!(matches!(result, Err(UpdateError::InvalidSignature)));
        assert_eq!(
            fs::read_to_string(app.join("VERSION")).unwrap().trim(),
            "0.1.0"
        );
    }

    #[test]
    fn rejects_hash_mismatch() {
        let root = tempdir().unwrap();
        let app = root.path().join("app");
        let data = root.path().join("data");
        let package = root.path().join("package");
        write_app(&app, "0.1.0", "old");
        fs::create_dir_all(&data).unwrap();
        let hash = write_package(&package, "0.1.1", b"new");
        let signing = generate_signing_key();
        let public = verifying_key(&signing).unwrap();
        let signature = sign_payload(&signing, format!("0.1.1\n{hash}").as_bytes());

        let result = apply_update(UpdateRequest {
            app_dir: &app,
            data_dir: &data,
            package_dir: &package,
            public_key: &public,
            version: "0.1.1",
            expected_sha256: &"0".repeat(64),
            signature: &signature,
            fail_after_replace: false,
        });
        assert!(matches!(result, Err(UpdateError::HashMismatch)));
    }

    #[test]
    fn rejects_downgrade() {
        let root = tempdir().unwrap();
        let app = root.path().join("app");
        let data = root.path().join("data");
        let package = root.path().join("package");
        write_app(&app, "0.2.0", "old");
        fs::create_dir_all(&data).unwrap();
        let hash = write_package(&package, "0.1.0", b"old");
        let signing = generate_signing_key();
        let public = verifying_key(&signing).unwrap();
        let signature = sign_payload(&signing, format!("0.1.0\n{hash}").as_bytes());

        let result = apply_update(UpdateRequest {
            app_dir: &app,
            data_dir: &data,
            package_dir: &package,
            public_key: &public,
            version: "0.1.0",
            expected_sha256: &hash,
            signature: &signature,
            fail_after_replace: false,
        });
        assert!(matches!(result, Err(UpdateError::InvalidVersion(_))));
    }

    #[test]
    fn simulated_install_failure_restores_app_and_preserves_data() {
        let root = tempdir().unwrap();
        let app = root.path().join("app");
        let data = root.path().join("data");
        let package = root.path().join("package");
        write_app(&app, "0.1.0", "old");
        fs::create_dir_all(&data).unwrap();
        fs::write(data.join("profile.db"), "user-data").unwrap();
        let hash = write_package(&package, "0.1.1", b"new");
        let signing = generate_signing_key();
        let public = verifying_key(&signing).unwrap();
        let signature = sign_payload(&signing, format!("0.1.1\n{hash}").as_bytes());

        let result = apply_update(UpdateRequest {
            app_dir: &app,
            data_dir: &data,
            package_dir: &package,
            public_key: &public,
            version: "0.1.1",
            expected_sha256: &hash,
            signature: &signature,
            fail_after_replace: true,
        });
        assert!(matches!(result, Err(UpdateError::SimulatedInstallFailure)));
        assert_eq!(
            fs::read_to_string(app.join("VERSION")).unwrap().trim(),
            "0.1.0"
        );
        assert_eq!(
            fs::read_to_string(data.join("profile.db")).unwrap(),
            "user-data"
        );
    }

    #[test]
    fn explicit_rollback_restores_previous_version() {
        let root = tempdir().unwrap();
        let app = root.path().join("app");
        let data = root.path().join("data");
        let package = root.path().join("package");
        write_app(&app, "0.1.0", "old");
        fs::create_dir_all(&data).unwrap();
        let hash = write_package(&package, "0.1.1", b"new");
        let signing = generate_signing_key();
        let public = verifying_key(&signing).unwrap();
        let signature = sign_payload(&signing, format!("0.1.1\n{hash}").as_bytes());

        let backup = apply_update(UpdateRequest {
            app_dir: &app,
            data_dir: &data,
            package_dir: &package,
            public_key: &public,
            version: "0.1.1",
            expected_sha256: &hash,
            signature: &signature,
            fail_after_replace: false,
        })
        .unwrap();
        assert_eq!(
            fs::read_to_string(app.join("VERSION")).unwrap().trim(),
            "0.1.1"
        );

        rollback(&app, &backup).unwrap();
        assert_eq!(
            fs::read_to_string(app.join("VERSION")).unwrap().trim(),
            "0.1.0"
        );
    }
}
