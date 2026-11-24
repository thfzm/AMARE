//! Independent ABI 2 entry points for the password vault.
//!
//! ABI 1 (`amare_services_*`) is deliberately untouched. ABI 2 uses a separate
//! handle, separate symbols and a separate JSON contract. The only shared
//! convention is UTF-8 C strings and `amare_string_free`.

use crate::{AMARE_OK, clear_last_error, guard, read_cstr, set_last_error};
use amare_core::ProfileId;
use amare_security::KeyProtector;
use amare_services::vault::{VaultError, VaultService};
use amare_services::{ProfileContext, ProfileService, ServiceError};
use serde_json::{Value, json};
use std::collections::HashMap;
use std::ffi::c_char;
use std::sync::{Arc, Mutex};

pub const AMARE_SERVICES_ABI2_VERSION: u32 = 2;

pub const AMARE_ERR_VAULT_LOCKED: i32 = 20;
pub const AMARE_ERR_VAULT_CORRUPT: i32 = 21;
pub const AMARE_ERR_VAULT_AUTH: i32 = 22;
pub const AMARE_ERR_VAULT_CONFLICT: i32 = 23;
pub const AMARE_ERR_VAULT_UNSUPPORTED: i32 = 24;
pub const AMARE_ERR_VAULT_KEY_UNAVAILABLE: i32 = 25;
pub const AMARE_ERR_VAULT_INVALID_JSON: i32 = 26;
pub const AMARE_ERR_VAULT_INTERNAL: i32 = 27;
pub const AMARE_ERR_VAULT_INVALID_ARGUMENT: i32 = 28;
pub const AMARE_ERR_VAULT_PROFILE: i32 = 29;
pub const AMARE_ERR_VAULT_NOT_FOUND: i32 = 30;
pub const AMARE_ERR_VAULT_ORIGIN_MISMATCH: i32 = 31;

const MAX_ABI2_REQUEST: usize = 32 * 1024 * 1024;

pub struct AmareVaultServices {
    profiles: Arc<Mutex<ProfileService>>,
    vaults: Mutex<HashMap<String, Arc<VaultService>>>,
    vault_generations: Mutex<HashMap<String, u64>>,
    protector: Arc<dyn KeyProtector>,
}

impl AmareVaultServices {
    fn new(root_dir: &str) -> Result<Self, VaultFailure> {
        let protector = default_protector()?;
        Self::new_with_protector(root_dir, protector)
    }

    fn new_with_protector(
        root_dir: &str,
        protector: Arc<dyn KeyProtector>,
    ) -> Result<Self, VaultFailure> {
        let profiles = crate::shared::profile_service(root_dir).map_err(VaultFailure::from)?;
        profiles
            .lock()
            .map_err(|_| VaultFailure::internal("profile service poisoned"))?
            .initialize()
            .map_err(VaultFailure::from)?;
        Ok(Self {
            profiles,
            vaults: Mutex::new(HashMap::new()),
            vault_generations: Mutex::new(HashMap::new()),
            protector,
        })
    }

    fn dispatch(&self, request: &Value) -> Result<Value, VaultFailure> {
        let object = request
            .as_object()
            .ok_or_else(|| VaultFailure::invalid("request must be a JSON object"))?;
        let version = object.get("version").and_then(Value::as_u64).unwrap_or(0);
        if version != AMARE_SERVICES_ABI2_VERSION as u64 {
            return Err(VaultFailure::new(
                AMARE_ERR_VAULT_UNSUPPORTED,
                "unsupported ABI 2 request version",
            ));
        }
        let method = object
            .get("method")
            .and_then(Value::as_str)
            .ok_or_else(|| VaultFailure::invalid("missing method"))?;
        let params = object.get("params").cloned().unwrap_or_else(|| json!({}));
        let context = required_context(&params)?;
        let vault = self.vault_for(&context)?;

        match method {
            "vault.initialize" => Ok(json!(vault.initialize().map_err(VaultFailure::from)?)),
            "vault.unlock" => Ok(json!(vault.unlock().map_err(VaultFailure::from)?)),
            "vault.lock" => {
                vault.lock();
                Ok(json!(vault.status().map_err(VaultFailure::from)?))
            }
            "vault.status" => Ok(json!(vault.status().map_err(VaultFailure::from)?)),
            "vault.list" => Ok(json!({
                "entries": vault.list_entries().map_err(VaultFailure::from)?
            })),
            "vault.get" => {
                let id = required_u64(&params, "id")?;
                Ok(json!(vault.get_entry(id).map_err(VaultFailure::from)?))
            }
            "vault.get_for_fill" => {
                let id = required_u64(&params, "id")?;
                let origin = required_str(&params, "origin")?;
                Ok(json!(
                    vault
                        .get_for_fill(&origin, id)
                        .map_err(VaultFailure::from)?
                ))
            }
            "vault.create" => {
                let origin = required_str(&params, "origin")?;
                let username = required_str(&params, "username")?;
                let password = required_str(&params, "password")?;
                let title = optional_str(&params, "title")?.unwrap_or_default();
                let entry = vault
                    .create_entry(&origin, &username, &password, &title)
                    .map_err(VaultFailure::from)?;
                Ok(json!({ "entry": entry.view() }))
            }
            "vault.update" => {
                let id = required_u64(&params, "id")?;
                let expected_revision = required_u64(&params, "expected_revision")?;
                let origin = required_str(&params, "origin")?;
                let username = required_str(&params, "username")?;
                let password = required_str(&params, "password")?;
                let title = optional_str(&params, "title")?.unwrap_or_default();
                let entry = vault
                    .update_entry(id, expected_revision, &origin, &username, &password, &title)
                    .map_err(VaultFailure::from)?;
                Ok(json!({ "entry": entry.view() }))
            }
            "vault.delete" => {
                let id = required_u64(&params, "id")?;
                let expected_revision = required_u64(&params, "expected_revision")?;
                let revision = vault
                    .delete_entry(id, expected_revision)
                    .map_err(VaultFailure::from)?;
                Ok(json!({ "revision": revision }))
            }
            "vault.export_backup" => {
                let password = required_str(&params, "password")?;
                let payload = vault.export_backup(&password).map_err(VaultFailure::from)?;
                Ok(json!({
                    "format": 1,
                    "payload_hex": hex::encode(payload)
                }))
            }
            "vault.validate_backup" => {
                let password = required_str(&params, "password")?;
                let payload = decode_hex(&required_str(&params, "payload_hex")?)?;
                Ok(json!(
                    vault
                        .validate_backup(&payload, &password)
                        .map_err(VaultFailure::from)?
                ))
            }
            "vault.import_backup" => {
                let password = required_str(&params, "password")?;
                let payload = decode_hex(&required_str(&params, "payload_hex")?)?;
                Ok(json!(
                    vault
                        .import_backup(&payload, &password)
                        .map_err(VaultFailure::from)?
                ))
            }
            _ => Err(VaultFailure::new(
                AMARE_ERR_VAULT_UNSUPPORTED,
                "unknown vault method",
            )),
        }
    }

    fn vault_for(&self, context: &ProfileContext) -> Result<Arc<VaultService>, VaultFailure> {
        // Validate the profile generation before consulting the vault cache:
        // a stale context must never reach a cached unlocked vault.
        let root = {
            let profiles = self
                .profiles
                .lock()
                .map_err(|_| VaultFailure::internal("profile service poisoned"))?;
            // The ABI 2 handle initializes its own ProfileService, so a
            // caller context from the standard ABI 1 initialize flow matches
            // this generation. Profile switches must recreate the ABI 2 handle
            // (documented limitation) or a future shared-context API.
            profiles
                .profile_store(context)
                .map_err(VaultFailure::from)?;
            profiles.root_dir().map_err(VaultFailure::from)?
        };
        let key = context.profile_id.as_str().to_string();
        if let Some(vault) = self
            .vaults
            .lock()
            .map_err(|_| VaultFailure::internal("vault cache poisoned"))?
            .get(&key)
            .cloned()
        {
            let mut generations = self
                .vault_generations
                .lock()
                .map_err(|_| VaultFailure::internal("vault generation cache poisoned"))?;
            if generations.get(&key).copied() != Some(context.generation) {
                // A profile switch/generation change must not retain an
                // unlocked vault for the previous context.
                vault.lock();
                generations.insert(key, context.generation);
            }
            return Ok(vault);
        }
        let vault = Arc::new(
            VaultService::new(root, context.profile_id.clone(), self.protector.clone())
                .map_err(VaultFailure::from)?,
        );
        let mut vaults = self
            .vaults
            .lock()
            .map_err(|_| VaultFailure::internal("vault cache poisoned"))?;
        let entry = vaults.entry(key.clone()).or_insert_with(|| vault.clone());
        let cached = entry.clone();
        drop(vaults);
        self.vault_generations
            .lock()
            .map_err(|_| VaultFailure::internal("vault generation cache poisoned"))?
            .insert(key, context.generation);
        Ok(cached)
    }
}

#[derive(Debug)]
struct VaultFailure {
    code: i32,
    message: String,
}

impl VaultFailure {
    fn new(code: i32, message: impl Into<String>) -> Self {
        Self {
            code,
            message: message.into(),
        }
    }

    fn invalid(message: impl Into<String>) -> Self {
        Self::new(AMARE_ERR_VAULT_INVALID_ARGUMENT, message)
    }

    fn internal(message: impl Into<String>) -> Self {
        Self::new(AMARE_ERR_VAULT_INTERNAL, message)
    }
}

impl From<VaultError> for VaultFailure {
    fn from(error: VaultError) -> Self {
        if let VaultError::Store(store) = error {
            return Self::from(store);
        }
        let code = match &error {
            VaultError::Locked => AMARE_ERR_VAULT_LOCKED,
            VaultError::Corrupt | VaultError::BackupCorrupt => AMARE_ERR_VAULT_CORRUPT,
            VaultError::Auth | VaultError::BackupAuth => AMARE_ERR_VAULT_AUTH,
            VaultError::Conflict { .. } => AMARE_ERR_VAULT_CONFLICT,
            VaultError::UnsupportedFormat(_) | VaultError::BackupUnsupported(_) => {
                AMARE_ERR_VAULT_UNSUPPORTED
            }
            VaultError::KeyMissing | VaultError::KeyUnavailable => AMARE_ERR_VAULT_KEY_UNAVAILABLE,
            VaultError::NotFound => AMARE_ERR_VAULT_NOT_FOUND,
            VaultError::OriginMismatch => AMARE_ERR_VAULT_ORIGIN_MISMATCH,
            VaultError::InvalidProfile => AMARE_ERR_VAULT_PROFILE,
            VaultError::InvalidOrigin | VaultError::InvalidInput(_) => {
                AMARE_ERR_VAULT_INVALID_ARGUMENT
            }
            VaultError::Store(_) => unreachable!("store errors are handled above"),
        };
        Self::new(code, error_message(&error))
    }
}

impl From<ServiceError> for VaultFailure {
    fn from(error: ServiceError) -> Self {
        match error {
            ServiceError::Store(store) => Self::from(store),
            ServiceError::ProfileSwitched | ServiceError::NoActiveProfile => {
                Self::new(AMARE_ERR_VAULT_PROFILE, "profile context is not active")
            }
            ServiceError::InvalidInput(_) => {
                Self::new(AMARE_ERR_VAULT_INVALID_ARGUMENT, "invalid profile input")
            }
            _ => Self::new(AMARE_ERR_VAULT_INTERNAL, "profile service error"),
        }
    }
}

impl From<amare_store::StoreError> for VaultFailure {
    fn from(error: amare_store::StoreError) -> Self {
        let code = match &error {
            amare_store::StoreError::NotFound(_) => AMARE_ERR_VAULT_NOT_FOUND,
            amare_store::StoreError::ProfileNotFound(_)
            | amare_store::StoreError::ProfileDeleting(_) => AMARE_ERR_VAULT_PROFILE,
            amare_store::StoreError::Conflict { .. } => AMARE_ERR_VAULT_CONFLICT,
            amare_store::StoreError::Security(_) => AMARE_ERR_VAULT_AUTH,
            amare_store::StoreError::InvalidInput(_) => AMARE_ERR_VAULT_INVALID_ARGUMENT,
            _ => AMARE_ERR_VAULT_INTERNAL,
        };
        let message = match code {
            AMARE_ERR_VAULT_AUTH => "vault authentication failed".to_string(),
            AMARE_ERR_VAULT_CONFLICT => "vault conflict".to_string(),
            AMARE_ERR_VAULT_NOT_FOUND => "vault entry not found".to_string(),
            AMARE_ERR_VAULT_PROFILE => "profile is not available".to_string(),
            _ => "vault storage error".to_string(),
        };
        Self::new(code, message)
    }
}

fn error_message(error: &VaultError) -> String {
    match error {
        VaultError::Locked => "vault is locked".into(),
        VaultError::Corrupt | VaultError::BackupCorrupt => "vault data is corrupt".into(),
        VaultError::Auth | VaultError::BackupAuth => "vault authentication failed".into(),
        VaultError::Conflict { .. } => "vault revision conflict".into(),
        VaultError::UnsupportedFormat(_) | VaultError::BackupUnsupported(_) => {
            "unsupported vault format".into()
        }
        VaultError::KeyMissing => "vault key file is missing".into(),
        VaultError::KeyUnavailable => "vault key is unavailable".into(),
        VaultError::NotFound => "vault entry not found".into(),
        VaultError::OriginMismatch => "origin does not match the stored entry".into(),
        VaultError::InvalidProfile => "invalid profile".into(),
        VaultError::InvalidOrigin => "invalid origin".into(),
        VaultError::InvalidInput(_) => "invalid vault input".into(),
        VaultError::Store(_) => "vault storage error".into(),
    }
}

fn default_protector() -> Result<Arc<dyn KeyProtector>, VaultFailure> {
    #[cfg(windows)]
    {
        Ok(Arc::new(amare_platform::WindowsDpapiKeyProtector))
    }
    #[cfg(not(windows))]
    {
        Err(VaultFailure::new(
            AMARE_ERR_VAULT_UNSUPPORTED,
            "vault key protection is only available on Windows",
        ))
    }
}

fn required_context(params: &Value) -> Result<ProfileContext, VaultFailure> {
    let context = params
        .get("context")
        .ok_or_else(|| VaultFailure::invalid("missing context"))?;
    Ok(ProfileContext {
        profile_id: ProfileId::new(required_str(context, "profile_id")?),
        generation: context
            .get("generation")
            .and_then(Value::as_u64)
            .ok_or_else(|| VaultFailure::invalid("missing context generation"))?,
    })
}

fn required_str(value: &Value, name: &str) -> Result<String, VaultFailure> {
    value
        .get(name)
        .and_then(Value::as_str)
        .map(str::to_string)
        .ok_or_else(|| VaultFailure::invalid(format!("missing string field: {name}")))
}

fn optional_str(value: &Value, name: &str) -> Result<Option<String>, VaultFailure> {
    match value.get(name) {
        None | Some(Value::Null) => Ok(None),
        Some(value) => value
            .as_str()
            .map(|value| Some(value.to_string()))
            .ok_or_else(|| VaultFailure::invalid(format!("{name} must be a string or null"))),
    }
}

fn required_u64(value: &Value, name: &str) -> Result<u64, VaultFailure> {
    value
        .get(name)
        .and_then(Value::as_u64)
        .ok_or_else(|| VaultFailure::invalid(format!("missing u64 field: {name}")))
}

fn decode_hex(value: &str) -> Result<Vec<u8>, VaultFailure> {
    hex::decode(value).map_err(|_| VaultFailure::invalid("payload_hex is not valid hex"))
}

/// # Safety
/// `root_dir` must be a valid NUL-terminated UTF-8 string; `out` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn amare_services_abi2_create(
    root_dir: *const c_char,
    out: *mut *mut AmareVaultServices,
) -> i32 {
    guard(|| {
        if out.is_null() {
            set_last_error("null ABI 2 services output pointer");
            return AMARE_ERR_VAULT_INVALID_ARGUMENT;
        }
        unsafe {
            *out = std::ptr::null_mut();
        }
        let root_dir = match unsafe { read_cstr(root_dir) } {
            Ok(value) if !value.trim().is_empty() => value,
            Ok(_) => {
                set_last_error("root directory is empty");
                return AMARE_ERR_VAULT_INVALID_ARGUMENT;
            }
            Err(_) => {
                set_last_error("invalid UTF-8 ABI 2 root path");
                return AMARE_ERR_VAULT_INVALID_ARGUMENT;
            }
        };
        match AmareVaultServices::new(root_dir) {
            Ok(services) => {
                unsafe {
                    *out = Box::into_raw(Box::new(services));
                }
                clear_last_error();
                AMARE_OK
            }
            Err(error) => {
                set_last_error(error.message);
                error.code
            }
        }
    })
}

/// # Safety
/// `services` must be a handle from `amare_services_abi2_create`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn amare_services_abi2_destroy(services: *mut AmareVaultServices) {
    if services.is_null() {
        return;
    }
    unsafe {
        drop(Box::from_raw(services));
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn amare_services_abi2_version() -> u32 {
    AMARE_SERVICES_ABI2_VERSION
}

/// # Safety
/// `services` must be a valid ABI 2 handle. `request_json` must be a valid
/// NUL-terminated UTF-8 string. `out_json` must be writable. On success the
/// returned string must be released with `amare_string_free`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn amare_services_abi2_call(
    services: *mut AmareVaultServices,
    request_json: *const c_char,
    out_json: *mut *mut c_char,
) -> i32 {
    guard(|| {
        if services.is_null() {
            set_last_error("null ABI 2 services handle");
            return AMARE_ERR_VAULT_INVALID_ARGUMENT;
        }
        if out_json.is_null() {
            set_last_error("null ABI 2 output pointer");
            return AMARE_ERR_VAULT_INVALID_ARGUMENT;
        }
        unsafe {
            *out_json = std::ptr::null_mut();
        }
        let request = match unsafe { read_cstr(request_json) } {
            Ok(value) => value,
            Err(_) => {
                set_last_error("invalid UTF-8 ABI 2 request");
                return AMARE_ERR_VAULT_INVALID_ARGUMENT;
            }
        };
        if request.len() > MAX_ABI2_REQUEST {
            set_last_error("ABI 2 request exceeds 32 MiB");
            return AMARE_ERR_VAULT_INVALID_ARGUMENT;
        }
        let request: Value = match serde_json::from_str(request) {
            Ok(value) => value,
            Err(_) => {
                set_last_error("invalid ABI 2 request JSON");
                return AMARE_ERR_VAULT_INVALID_JSON;
            }
        };
        let services = unsafe { &*services };
        match services.dispatch(&request) {
            Ok(value) => match serde_json::to_string(&value) {
                Ok(json) => match std::ffi::CString::new(json) {
                    Ok(json) => {
                        unsafe {
                            *out_json = json.into_raw();
                        }
                        clear_last_error();
                        AMARE_OK
                    }
                    Err(_) => {
                        set_last_error("ABI 2 response contained a NUL byte");
                        AMARE_ERR_VAULT_INTERNAL
                    }
                },
                Err(_) => {
                    set_last_error("ABI 2 response serialization failed");
                    AMARE_ERR_VAULT_INTERNAL
                }
            },
            Err(failure) => {
                set_last_error(failure.message);
                failure.code
            }
        }
    })
}

/// Copy the last thread-local ABI 2 error. ABI 1 keeps using
/// `amare_core_last_error`; this alias exists so ABI 2 callers do not need to
/// mix entry points.
///
/// # Safety
/// `out` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn amare_services_abi2_last_error(out: *mut *mut c_char) -> i32 {
    unsafe { crate::amare_core_last_error(out) }
}

#[cfg(test)]
mod tests {
    use super::*;
    use amare_security::InMemoryKeyProtector;
    use amare_store::Store;
    use std::ffi::{CStr, CString};
    use std::ptr;
    use tempfile::tempdir;

    fn context(root: &std::path::Path) -> ProfileContext {
        let id = ProfileId::new("p1");
        Store::open(root)
            .unwrap()
            .create_profile(&id, "Personal", true)
            .unwrap();
        ProfileContext {
            profile_id: id,
            generation: 1,
        }
    }

    fn create_handle(root: &std::path::Path) -> *mut AmareVaultServices {
        let protector = Arc::new(InMemoryKeyProtector::random());
        create_handle_with(root, protector)
    }

    fn create_handle_with(
        root: &std::path::Path,
        protector: Arc<dyn KeyProtector>,
    ) -> *mut AmareVaultServices {
        Box::into_raw(Box::new(
            AmareVaultServices::new_with_protector(root.to_str().unwrap(), protector).unwrap(),
        ))
    }

    fn call(handle: *mut AmareVaultServices, value: Value) -> Result<Value, (i32, String)> {
        let request = CString::new(value.to_string()).unwrap();
        let mut out = ptr::null_mut();
        let code = unsafe { amare_services_abi2_call(handle, request.as_ptr(), &mut out) };
        if code != AMARE_OK {
            let mut error = ptr::null_mut();
            unsafe { amare_services_abi2_last_error(&mut error) };
            let message = unsafe {
                if error.is_null() {
                    String::new()
                } else {
                    let text = CStr::from_ptr(error).to_string_lossy().into_owned();
                    crate::amare_string_free(error);
                    text
                }
            };
            return Err((code, message));
        }
        let value = unsafe { CStr::from_ptr(out).to_str().unwrap().to_string() };
        unsafe { crate::amare_string_free(out) };
        Ok(serde_json::from_str(&value).unwrap())
    }

    fn make_request(context: &ProfileContext, method: &str, params: Value) -> Value {
        let mut object = params.as_object().cloned().unwrap_or_default();
        object.insert(
            "context".into(),
            json!({
                "profile_id": context.profile_id.as_str(),
                "generation": context.generation
            }),
        );
        json!({"version":2,"method":method,"params":object})
    }

    #[test]
    fn abi2_version_is_independent() {
        assert_eq!(amare_services_abi2_version(), 2);
        assert_eq!(crate::services::AMARE_SERVICES_ABI_VERSION, 1);
    }

    #[test]
    fn abi2_vault_flow_locked_and_origin_errors() {
        let dir = tempdir().unwrap();
        let context = context(dir.path());
        let service = create_handle(dir.path());
        let initialized = call(
            service,
            make_request(&context, "vault.initialize", json!({})),
        )
        .unwrap();
        assert_eq!(initialized["unlocked"], true);
        let created = call(
            service,
            make_request(
                &context,
                "vault.create",
                json!({
                    "origin": "https://example.com",
                    "username": "한글",
                    "password": "top-secret",
                    "title": "Example"
                }),
            ),
        )
        .unwrap();
        let id = created["entry"]["id"].as_u64().unwrap();
        let list = call(service, make_request(&context, "vault.list", json!({}))).unwrap();
        assert_eq!(list["entries"].as_array().unwrap().len(), 1);
        assert!(list["entries"][0].get("password").is_none());
        let secret = call(
            service,
            make_request(
                &context,
                "vault.get_for_fill",
                json!({"id":id,"origin":"https://example.com"}),
            ),
        )
        .unwrap();
        assert_eq!(secret["password"], "top-secret");
        let mismatch = call(
            service,
            make_request(
                &context,
                "vault.get_for_fill",
                json!({"id":id,"origin":"https://evil.example"}),
            ),
        )
        .unwrap_err();
        assert_eq!(mismatch.0, AMARE_ERR_VAULT_ORIGIN_MISMATCH);
        let locked = call(service, make_request(&context, "vault.lock", json!({}))).unwrap();
        assert_eq!(locked["unlocked"], false);
        let locked_error =
            call(service, make_request(&context, "vault.list", json!({}))).unwrap_err();
        assert_eq!(locked_error.0, AMARE_ERR_VAULT_LOCKED);
        assert!(!locked_error.1.contains("top-secret"));
        unsafe { amare_services_abi2_destroy(service) };
    }

    #[test]
    fn abi2_bad_json_unknown_method_and_invalid_context_are_distinct() {
        let dir = tempdir().unwrap();
        let context = context(dir.path());
        let service = create_handle(dir.path());
        let bad_request = CString::new("{bad json").unwrap();
        let mut out = ptr::null_mut();
        assert_eq!(
            unsafe { amare_services_abi2_call(service, bad_request.as_ptr(), &mut out) },
            AMARE_ERR_VAULT_INVALID_JSON
        );
        assert!(out.is_null());
        let unknown = call(service, make_request(&context, "vault.unknown", json!({})));
        assert_eq!(unknown.unwrap_err().0, AMARE_ERR_VAULT_UNSUPPORTED);
        let wrong_context = json!({
            "version":2,
            "method":"vault.status",
            "params":{"context":{"profile_id":"p1","generation":999}}
        });
        let error = call(service, wrong_context).unwrap_err();
        assert_eq!(error.0, AMARE_ERR_VAULT_PROFILE);
        unsafe { amare_services_abi2_destroy(service) };
    }

    #[test]
    fn abi2_status_detects_persisted_vault_after_recreate() {
        let dir = tempdir().unwrap();
        let context = context(dir.path());
        let protector = Arc::new(InMemoryKeyProtector::random());
        let first = create_handle_with(dir.path(), protector.clone());
        call(first, make_request(&context, "vault.initialize", json!({}))).unwrap();
        call(
            first,
            make_request(
                &context,
                "vault.create",
                json!({
                    "origin": "https://example.com",
                    "username": "reexisting",
                    "password": "top-secret",
                    "title": "Example"
                }),
            ),
        )
        .unwrap();
        unsafe { amare_services_abi2_destroy(first) };

        let second = create_handle_with(dir.path(), protector);
        let status = call(second, make_request(&context, "vault.status", json!({}))).unwrap();
        assert_eq!(status["initialized"], true);
        assert_eq!(status["unlocked"], false);
        assert_eq!(status["entry_count"], 0);
        let unlocked = call(second, make_request(&context, "vault.unlock", json!({}))).unwrap();
        assert_eq!(unlocked["unlocked"], true);
        assert_eq!(unlocked["entry_count"], 1);
        unsafe { amare_services_abi2_destroy(second) };
    }

    #[test]
    fn abi2_conflict_and_backup_flow() {
        let dir = tempdir().unwrap();
        let context = context(dir.path());
        let protector = Arc::new(InMemoryKeyProtector::random());
        let first = create_handle_with(dir.path(), protector.clone());
        let second = create_handle_with(dir.path(), protector);
        call(first, make_request(&context, "vault.initialize", json!({}))).unwrap();
        call(
            second,
            make_request(&context, "vault.initialize", json!({})),
        )
        .unwrap();
        call(
            first,
            make_request(
                &context,
                "vault.create",
                json!({"origin":"https://one.example","username":"a","password":"pw-a"}),
            ),
        )
        .unwrap();
        let conflict = call(
            second,
            make_request(
                &context,
                "vault.create",
                json!({"origin":"https://two.example","username":"b","password":"pw-b"}),
            ),
        )
        .unwrap_err();
        assert_eq!(conflict.0, AMARE_ERR_VAULT_CONFLICT);

        let backup = call(
            first,
            make_request(
                &context,
                "vault.export_backup",
                json!({"password":"backup-pass"}),
            ),
        )
        .unwrap();
        let payload = backup["payload_hex"].as_str().unwrap();
        let summary = call(
            first,
            make_request(
                &context,
                "vault.validate_backup",
                json!({"password":"backup-pass","payload_hex":payload}),
            ),
        )
        .unwrap();
        assert_eq!(summary["entry_count"], 1);
        unsafe { amare_services_abi2_destroy(first) };
        unsafe { amare_services_abi2_destroy(second) };
    }

    #[test]
    fn abi2_null_and_create_platform_contract() {
        let mut handle = ptr::null_mut();
        assert_eq!(
            unsafe { amare_services_abi2_create(ptr::null(), &mut handle) },
            AMARE_ERR_VAULT_INVALID_ARGUMENT
        );
        assert!(handle.is_null());
        let dir = tempdir().unwrap();
        let root = CString::new(dir.path().to_str().unwrap()).unwrap();
        let code = unsafe { amare_services_abi2_create(root.as_ptr(), &mut handle) };
        #[cfg(windows)]
        assert_eq!(code, AMARE_OK);
        #[cfg(not(windows))]
        assert_eq!(code, AMARE_ERR_VAULT_UNSUPPORTED);
        if code == AMARE_OK {
            unsafe { amare_services_abi2_destroy(handle) };
        }
    }
}
