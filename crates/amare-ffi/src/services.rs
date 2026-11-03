//! JSON-based C ABI for the shared AMARE service layer.
//!
//! The JSON envelope keeps the C ABI small and versionable:
//! `{"version":1,"method":"profile.list","params":{...}}`.
//! Strings returned through `out_json` are owned by the caller and must be
//! released with `amare_string_free`.

use crate::{AMARE_ERR_INTERNAL, set_last_error};
use amare_core::{NewSessionTab, ProfileId, SearchKind, SettingValue};
use amare_services::{
    ProfileContext, ProfileService, SearchPage, SearchRequest, SearchSort, ServiceError,
    SettingGroup, SettingSource, SettingsService,
};
use serde_json::{Value, json};
use std::ffi::c_char;
use std::sync::Mutex;

pub const AMARE_SERVICES_ABI_VERSION: u32 = 1;

pub const AMARE_ERR_NOT_FOUND: i32 = 4;
pub const AMARE_ERR_PROFILE_SWITCHED: i32 = 5;
pub const AMARE_ERR_NO_ACTIVE_PROFILE: i32 = 6;
pub const AMARE_ERR_LAST_PROFILE: i32 = 7;
pub const AMARE_ERR_SETTINGS: i32 = 8;
pub const AMARE_ERR_SEARCH: i32 = 9;
pub const AMARE_ERR_STORE: i32 = 10;
pub const AMARE_ERR_JSON: i32 = 11;
pub const AMARE_ERR_UNSUPPORTED: i32 = 12;

pub struct AmareServices {
    profiles: Mutex<ProfileService>,
}

impl AmareServices {
    fn new(root_dir: &str) -> Result<Self, ServiceError> {
        Ok(Self {
            profiles: Mutex::new(ProfileService::open(root_dir)?),
        })
    }

    fn profiles(&self) -> Result<std::sync::MutexGuard<'_, ProfileService>, CallFailure> {
        self.profiles
            .lock()
            .map_err(|_| CallFailure::internal("service lock poisoned"))
    }
}

#[derive(Debug)]
struct CallFailure {
    code: i32,
    message: String,
}

impl CallFailure {
    fn invalid(message: impl Into<String>) -> Self {
        Self {
            code: crate::AMARE_ERR_INVALID_ARGUMENT,
            message: message.into(),
        }
    }

    fn unsupported(message: impl Into<String>) -> Self {
        Self {
            code: AMARE_ERR_UNSUPPORTED,
            message: message.into(),
        }
    }

    fn internal(message: impl Into<String>) -> Self {
        Self {
            code: AMARE_ERR_INTERNAL,
            message: message.into(),
        }
    }
}

impl From<amare_store::StoreError> for CallFailure {
    fn from(error: amare_store::StoreError) -> Self {
        let code = match &error {
            amare_store::StoreError::NotFound(_)
            | amare_store::StoreError::ProfileNotFound(_)
            | amare_store::StoreError::ProfileDeleting(_) => AMARE_ERR_NOT_FOUND,
            _ => AMARE_ERR_STORE,
        };
        Self {
            code,
            message: error.to_string(),
        }
    }
}

impl From<ServiceError> for CallFailure {
    fn from(error: ServiceError) -> Self {
        let code = match &error {
            ServiceError::Store(store) => match store {
                amare_store::StoreError::NotFound(_)
                | amare_store::StoreError::ProfileNotFound(_)
                | amare_store::StoreError::ProfileDeleting(_) => AMARE_ERR_NOT_FOUND,
                _ => AMARE_ERR_STORE,
            },
            ServiceError::NoActiveProfile => AMARE_ERR_NO_ACTIVE_PROFILE,
            ServiceError::ProfileSwitched => AMARE_ERR_PROFILE_SWITCHED,
            ServiceError::LastProfile => AMARE_ERR_LAST_PROFILE,
            ServiceError::UnknownSetting(_)
            | ServiceError::InvalidSettingType(_)
            | ServiceError::SettingOutOfRange(_)
            | ServiceError::SensitiveSetting(_)
            | ServiceError::DuplicateSetting(_) => AMARE_ERR_SETTINGS,
            ServiceError::EmptyQuery
            | ServiceError::QueryTooLong { .. }
            | ServiceError::InvalidLimit { .. }
            | ServiceError::OffsetTooLarge { .. } => AMARE_ERR_SEARCH,
            ServiceError::InvalidInput(_) => crate::AMARE_ERR_INVALID_ARGUMENT,
        };
        Self {
            code,
            message: error.to_string(),
        }
    }
}

/// # Safety
/// `root_dir` must be a valid NUL-terminated UTF-8 string; `out` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn amare_services_create(
    root_dir: *const c_char,
    out: *mut *mut AmareServices,
) -> i32 {
    crate::guard(|| {
        if out.is_null() {
            set_last_error("null services output pointer");
            return crate::AMARE_ERR_INVALID_ARGUMENT;
        }
        unsafe {
            *out = std::ptr::null_mut();
        }
        let root_dir = match unsafe { crate::read_cstr(root_dir) } {
            Ok(value) if !value.trim().is_empty() => value,
            Ok(_) => {
                set_last_error("root directory is empty");
                return crate::AMARE_ERR_INVALID_ARGUMENT;
            }
            Err(code) => return code,
        };
        match AmareServices::new(root_dir) {
            Ok(services) => {
                unsafe {
                    *out = Box::into_raw(Box::new(services));
                }
                crate::clear_last_error();
                crate::AMARE_OK
            }
            Err(error) => {
                let failure = CallFailure::from(error);
                set_last_error(failure.message);
                failure.code
            }
        }
    })
}

/// # Safety
/// `services` must be a handle from `amare_services_create` and not used again.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn amare_services_destroy(services: *mut AmareServices) {
    if services.is_null() {
        return;
    }
    unsafe {
        drop(Box::from_raw(services));
    }
}

/// Returns the service ABI version. This is a static value and must not be freed.
#[unsafe(no_mangle)]
pub extern "C" fn amare_services_abi_version() -> u32 {
    AMARE_SERVICES_ABI_VERSION
}

/// Execute one JSON request.
///
/// # Safety
/// `services` must be a valid handle. `request_json` must be a valid
/// NUL-terminated UTF-8 string. `out_json` must be writable. On success the
/// returned string must be released with `amare_string_free`; on error it is
/// set to null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn amare_services_call(
    services: *mut AmareServices,
    request_json: *const c_char,
    out_json: *mut *mut c_char,
) -> i32 {
    crate::guard(|| {
        if services.is_null() {
            set_last_error("null services handle");
            return crate::AMARE_ERR_INVALID_ARGUMENT;
        }
        if out_json.is_null() {
            set_last_error("null output pointer");
            return crate::AMARE_ERR_INVALID_ARGUMENT;
        }
        unsafe {
            *out_json = std::ptr::null_mut();
        }
        let request = match unsafe { crate::read_cstr(request_json) } {
            Ok(value) => value,
            Err(code) => return code,
        };
        if request.len() > 2 * 1024 * 1024 {
            set_last_error("service request exceeds 2 MiB");
            return crate::AMARE_ERR_INVALID_ARGUMENT;
        }
        let request: Value = match serde_json::from_str(request) {
            Ok(value) => value,
            Err(error) => {
                set_last_error(format!("invalid request JSON: {error}"));
                return AMARE_ERR_JSON;
            }
        };
        // SAFETY: handle checked above; caller must not destroy it concurrently.
        let services = unsafe { &*services };
        match dispatch(services, &request) {
            Ok(result) => crate::write_out_string(out_json, result.to_string()),
            Err(failure) => {
                set_last_error(failure.message);
                failure.code
            }
        }
    })
}

fn dispatch(services: &AmareServices, request: &Value) -> Result<Value, CallFailure> {
    let object = request
        .as_object()
        .ok_or_else(|| CallFailure::invalid("request must be a JSON object"))?;
    let version = object.get("version").and_then(Value::as_u64).unwrap_or(1);
    if version != AMARE_SERVICES_ABI_VERSION as u64 {
        return Err(CallFailure::unsupported(format!(
            "unsupported request version: {version}"
        )));
    }
    let method = object
        .get("method")
        .and_then(Value::as_str)
        .ok_or_else(|| CallFailure::invalid("missing method"))?;
    let params = object.get("params").cloned().unwrap_or_else(|| json!({}));
    let profiles = services.profiles()?;

    match method {
        "profile.initialize" => Ok(context_json(&profiles.initialize()?)),
        "profile.list" => {
            let limit = optional_u32(&params, "limit", 50)?;
            let offset = optional_u64(&params, "offset", 0)?;
            let page = profiles.list(limit, offset)?;
            Ok(json!({
                "profiles": page.items,
                "total": page.total,
                "limit": page.limit,
                "offset": page.offset
            }))
        }
        "profile.active" => Ok(match profiles.active_profile()? {
            Some(profile) => json!(profile),
            None => Value::Null,
        }),
        "profile.current_context" => Ok(match profiles.current_context()? {
            Some(context) => context_json(&context),
            None => Value::Null,
        }),
        "profile.exists" => {
            let id = ProfileId::new(required_str(&params, "id")?);
            Ok(json!({ "exists": profiles.exists(&id)? }))
        }
        "profile.create" => {
            let id = ProfileId::new(required_str(&params, "id")?);
            let name = required_str(&params, "name")?;
            let icon = optional_str(&params, "icon")?;
            let is_default = optional_bool(&params, "is_default", false)?;
            Ok(json!(profiles.create_profile(
                &id,
                &name,
                icon.as_deref(),
                is_default
            )?))
        }
        "profile.rename" => {
            let id = ProfileId::new(required_str(&params, "id")?);
            let name = required_str(&params, "name")?;
            Ok(json!(profiles.rename_profile(&id, &name)?))
        }
        "profile.set_icon" => {
            let id = ProfileId::new(required_str(&params, "id")?);
            let icon = optional_str(&params, "icon")?;
            Ok(json!(profiles.set_profile_icon(&id, icon.as_deref())?))
        }
        "profile.set_default" => {
            let id = ProfileId::new(required_str(&params, "id")?);
            Ok(json!(profiles.set_default_profile(&id)?))
        }
        "profile.switch" => {
            let id = ProfileId::new(required_str(&params, "id")?);
            Ok(context_json(&profiles.switch_profile(&id)?))
        }
        "profile.delete" => {
            let id = ProfileId::new(required_str(&params, "id")?);
            profiles.delete_profile(&id)?;
            Ok(json!({ "deleted": true }))
        }
        "profile.reset" => {
            let context = required_context(&params)?;
            profiles.reset_profile(&context)?;
            Ok(json!({ "reset": true }))
        }
        "settings.get" => {
            let context = required_context(&params)?;
            let key = required_str(&params, "key")?;
            let settings = SettingsService::new(&profiles);
            Ok(resolved_json(&settings.get(&context, &key)?))
        }
        "settings.list_group" => {
            let context = required_context(&params)?;
            let group = parse_group(&required_str(&params, "group")?)?;
            let settings = SettingsService::new(&profiles);
            Ok(json!({
                "settings": settings
                    .list_group(&context, group)?
                    .iter()
                    .map(resolved_json)
                    .collect::<Vec<_>>()
            }))
        }
        "settings.list_all" => {
            let context = required_context(&params)?;
            let settings = SettingsService::new(&profiles);
            Ok(json!({
                "settings": settings
                    .list_all(&context)?
                    .iter()
                    .map(resolved_json)
                    .collect::<Vec<_>>()
            }))
        }
        "settings.set" => {
            let context = required_context(&params)?;
            let key = required_str(&params, "key")?;
            let value = parse_setting_value(
                params
                    .get("value")
                    .ok_or_else(|| CallFailure::invalid("missing value"))?,
            )?;
            let settings = SettingsService::new(&profiles);
            Ok(changes_json(&settings.set(&context, &key, value)?))
        }
        "settings.set_many" => {
            let context = required_context(&params)?;
            let entries = params
                .get("entries")
                .and_then(Value::as_array)
                .ok_or_else(|| CallFailure::invalid("entries must be an array"))?;
            let mut parsed = Vec::with_capacity(entries.len());
            let mut keys = Vec::with_capacity(entries.len());
            for entry in entries {
                let key = required_str(entry, "key")?;
                let value = parse_setting_value(
                    entry
                        .get("value")
                        .ok_or_else(|| CallFailure::invalid("missing setting value"))?,
                )?;
                keys.push(key);
                parsed.push(value);
            }
            let refs: Vec<(&str, SettingValue)> =
                keys.iter().map(String::as_str).zip(parsed).collect();
            let settings = SettingsService::new(&profiles);
            Ok(changes_json(&settings.set_many(&context, &refs)?))
        }
        "settings.reset_key" => {
            let context = required_context(&params)?;
            let key = required_str(&params, "key")?;
            let settings = SettingsService::new(&profiles);
            Ok(changes_json(&settings.reset_key(&context, &key)?))
        }
        "settings.reset_group" => {
            let context = required_context(&params)?;
            let group = parse_group(&required_str(&params, "group")?)?;
            let settings = SettingsService::new(&profiles);
            Ok(changes_json(&settings.reset_group(&context, group)?))
        }
        "settings.reset_all" => {
            let context = required_context(&params)?;
            let settings = SettingsService::new(&profiles);
            Ok(changes_json(&settings.reset_all(&context)?))
        }
        "search.query" => {
            let context = required_context(&params)?;
            let query = required_str(&params, "query")?;
            let mut request = SearchRequest::new(query)
                .with_page(
                    optional_u32(&params, "limit", 20)?,
                    optional_u64(&params, "offset", 0)?,
                )
                .with_sort(parse_sort(
                    params
                        .get("sort")
                        .and_then(Value::as_str)
                        .unwrap_or("relevance"),
                )?);
            if let Some(kinds) = params.get("kinds") {
                let kinds = kinds
                    .as_array()
                    .ok_or_else(|| CallFailure::invalid("kinds must be an array"))?
                    .iter()
                    .map(|value| {
                        value
                            .as_str()
                            .ok_or_else(|| CallFailure::invalid("kind must be a string"))
                            .and_then(parse_search_kind)
                    })
                    .collect::<Result<Vec<_>, _>>()?;
                request = request.with_kinds(kinds);
            }
            let search = amare_services::SearchService::new(&profiles);
            Ok(search_page_json(&search.search(&context, &request)?))
        }
        "session.save" => {
            let context = required_context(&params)?;
            let profile = profiles.profile_store(&context)?;
            let tabs = params
                .get("tabs")
                .and_then(Value::as_array)
                .ok_or_else(|| CallFailure::invalid("tabs must be an array"))?;
            let mut new_tabs = Vec::with_capacity(tabs.len());
            for tab in tabs {
                new_tabs.push(NewSessionTab {
                    url: required_str(tab, "url")?,
                    title: optional_str(tab, "title")?.unwrap_or_default(),
                    pinned: optional_bool(tab, "pinned", false)?,
                    group_id: optional_str(tab, "group_id")?,
                    position: None,
                });
            }
            let active_position = params
                .get("active_position")
                .and_then(Value::as_u64)
                .map(|value| value as usize);
            let state = match params.get("session_id").and_then(Value::as_i64) {
                Some(session_id) => {
                    profile
                        .sessions()
                        .replace_tabs(session_id, &new_tabs, active_position)?
                }
                None => profile.sessions().create(
                    params
                        .get("name")
                        .and_then(Value::as_str)
                        .unwrap_or("AMARE session"),
                    &new_tabs,
                    active_position,
                )?,
            };
            Ok(json!(state))
        }
        "session.latest" => {
            let context = required_context(&params)?;
            let profile = profiles.profile_store(&context)?;
            let page = profile.sessions().list(1, 0)?;
            Ok(match page.items.first() {
                Some(session) => json!(profile.sessions().restore(session.id)?),
                None => Value::Null,
            })
        }
        "session.delete" => {
            let context = required_context(&params)?;
            let id = params
                .get("session_id")
                .and_then(Value::as_i64)
                .ok_or_else(|| CallFailure::invalid("missing session_id"))?;
            let profile = profiles.profile_store(&context)?;
            Ok(json!({ "deleted": profile.sessions().delete(id)? }))
        }
        "history.top" => {
            let context = required_context(&params)?;
            let limit = optional_u32(&params, "limit", 6)?;
            let profile = profiles.profile_store(&context)?;
            let page = profile.history().search(None, None, None, limit, 0)?;
            Ok(json!({
                "items": page.items,
                "total": page.total
            }))
        }
        other => Err(CallFailure::unsupported(format!(
            "unsupported method: {other}"
        ))),
    }
}

fn context_json(context: &ProfileContext) -> Value {
    json!({
        "profile_id": context.profile_id,
        "generation": context.generation
    })
}

fn required_context(params: &Value) -> Result<ProfileContext, CallFailure> {
    let context = params
        .get("context")
        .ok_or_else(|| CallFailure::invalid("missing context"))?;
    Ok(ProfileContext {
        profile_id: ProfileId::new(required_str(context, "profile_id")?),
        generation: context
            .get("generation")
            .and_then(Value::as_u64)
            .ok_or_else(|| CallFailure::invalid("missing context generation"))?,
    })
}

fn required_str(value: &Value, name: &str) -> Result<String, CallFailure> {
    value
        .get(name)
        .and_then(Value::as_str)
        .map(str::to_string)
        .ok_or_else(|| CallFailure::invalid(format!("missing string field: {name}")))
}

fn optional_str(value: &Value, name: &str) -> Result<Option<String>, CallFailure> {
    match value.get(name) {
        None | Some(Value::Null) => Ok(None),
        Some(value) => value
            .as_str()
            .map(|value| Some(value.to_string()))
            .ok_or_else(|| CallFailure::invalid(format!("{name} must be a string or null"))),
    }
}

fn optional_bool(value: &Value, name: &str, default: bool) -> Result<bool, CallFailure> {
    match value.get(name) {
        None => Ok(default),
        Some(value) => value
            .as_bool()
            .ok_or_else(|| CallFailure::invalid(format!("{name} must be a boolean"))),
    }
}

fn optional_u32(value: &Value, name: &str, default: u32) -> Result<u32, CallFailure> {
    match value.get(name) {
        None => Ok(default),
        Some(value) => value
            .as_u64()
            .and_then(|value| u32::try_from(value).ok())
            .ok_or_else(|| CallFailure::invalid(format!("{name} must be a u32"))),
    }
}

fn optional_u64(value: &Value, name: &str, default: u64) -> Result<u64, CallFailure> {
    match value.get(name) {
        None => Ok(default),
        Some(value) => value
            .as_u64()
            .ok_or_else(|| CallFailure::invalid(format!("{name} must be a u64"))),
    }
}

fn parse_group(value: &str) -> Result<SettingGroup, CallFailure> {
    SettingGroup::parse(value)
        .ok_or_else(|| CallFailure::invalid(format!("unknown setting group: {value}")))
}

fn parse_sort(value: &str) -> Result<SearchSort, CallFailure> {
    match value {
        "relevance" => Ok(SearchSort::Relevance),
        "title_asc" => Ok(SearchSort::TitleAsc),
        "updated_desc" => Ok(SearchSort::UpdatedDesc),
        _ => Err(CallFailure::invalid(format!(
            "unknown search sort: {value}"
        ))),
    }
}

fn parse_search_kind(value: &str) -> Result<SearchKind, CallFailure> {
    match value {
        "library_item" => Ok(SearchKind::LibraryItem),
        "tag" => Ok(SearchKind::Tag),
        "collection" => Ok(SearchKind::Collection),
        "conversation" => Ok(SearchKind::Conversation),
        "message" => Ok(SearchKind::Message),
        "workspace" => Ok(SearchKind::Workspace),
        "task" => Ok(SearchKind::Task),
        "task_memory" => Ok(SearchKind::TaskMemory),
        "bookmark" => Ok(SearchKind::Bookmark),
        "history" => Ok(SearchKind::History),
        _ => Err(CallFailure::invalid(format!(
            "unknown search kind: {value}"
        ))),
    }
}

fn parse_setting_value(value: &Value) -> Result<SettingValue, CallFailure> {
    let kind = value
        .get("type")
        .and_then(Value::as_str)
        .ok_or_else(|| CallFailure::invalid("setting value type is required"))?;
    let value = value
        .get("value")
        .ok_or_else(|| CallFailure::invalid("setting value is required"))?;
    match kind {
        "text" => value
            .as_str()
            .map(|value| SettingValue::Text(value.to_string()))
            .ok_or_else(|| CallFailure::invalid("text setting must be a string")),
        "integer" => value
            .as_i64()
            .map(SettingValue::Integer)
            .ok_or_else(|| CallFailure::invalid("integer setting must be an integer")),
        "float" => value
            .as_f64()
            .map(SettingValue::Float)
            .ok_or_else(|| CallFailure::invalid("float setting must be a number")),
        "boolean" => value
            .as_bool()
            .map(SettingValue::Boolean)
            .ok_or_else(|| CallFailure::invalid("boolean setting must be a bool")),
        "json" => value
            .as_str()
            .map(|value| SettingValue::Json(value.to_string()))
            .ok_or_else(|| CallFailure::invalid("json setting must be a JSON string")),
        _ => Err(CallFailure::invalid(format!(
            "unknown setting value type: {kind}"
        ))),
    }
}

fn setting_value_json(value: &SettingValue) -> Value {
    match value {
        SettingValue::Text(value) => json!({"type":"text","value":value}),
        SettingValue::Integer(value) => json!({"type":"integer","value":value}),
        SettingValue::Float(value) => json!({"type":"float","value":value}),
        SettingValue::Boolean(value) => json!({"type":"boolean","value":value}),
        SettingValue::Json(value) => json!({"type":"json","value":value}),
    }
}

fn resolved_json(value: &amare_services::ResolvedSetting) -> Value {
    json!({
        "key": value.key,
        "group": value.group.as_str(),
        "value": setting_value_json(&value.value),
        "source": match value.source {
            SettingSource::Default => "default",
            SettingSource::User => "user",
        },
        "updated_at_ms": value.updated_at_ms
    })
}

fn changes_json(changes: &amare_services::SettingsChangeSet) -> Value {
    json!({
        "changes": changes.changes.iter().map(|change| json!({
            "key": change.key,
            "group": change.group.as_str(),
            "old": change.old.as_ref().map(setting_value_json),
            "new": setting_value_json(&change.new),
            "source": match change.source {
                SettingSource::Default => "default",
                SettingSource::User => "user",
            }
        })).collect::<Vec<_>>(),
        "changed_count": changes.changed_count()
    })
}

fn search_page_json(page: &SearchPage) -> Value {
    json!({
        "profile_id": page.profile_id,
        "query": page.query,
        "items": page.items,
        "total": page.total,
        "limit": page.limit,
        "offset": page.offset,
        "sort": match page.sort {
            SearchSort::Relevance => "relevance",
            SearchSort::TitleAsc => "title_asc",
            SearchSort::UpdatedDesc => "updated_desc",
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::{CStr, CString};
    use std::ptr;

    fn call(handle: *mut AmareServices, request: Value) -> Result<Value, (i32, String)> {
        let request = CString::new(request.to_string()).unwrap();
        let mut out = ptr::null_mut();
        let code = unsafe { amare_services_call(handle, request.as_ptr(), &mut out) };
        if code != crate::AMARE_OK {
            let mut error = ptr::null_mut();
            unsafe { crate::amare_core_last_error(&mut error) };
            let message = unsafe {
                if error.is_null() {
                    String::new()
                } else {
                    CStr::from_ptr(error).to_string_lossy().into_owned()
                }
            };
            return Err((code, message));
        }
        let value = unsafe { CStr::from_ptr(out) }.to_str().unwrap().to_string();
        unsafe { crate::amare_string_free(out) };
        Ok(serde_json::from_str(&value).unwrap())
    }

    #[test]
    fn service_handle_round_trips_profile_settings_and_search() {
        let dir = tempfile::tempdir().unwrap();
        let root = CString::new(dir.path().to_str().unwrap()).unwrap();
        let mut handle = ptr::null_mut();
        assert_eq!(
            unsafe { amare_services_create(root.as_ptr(), &mut handle) },
            crate::AMARE_OK
        );
        let context = call(handle, json!({"method":"profile.initialize","params":{}})).unwrap();
        let context_json = json!({
            "profile_id": context["profile_id"],
            "generation": context["generation"]
        });
        let settings = call(
            handle,
            json!({
                "method":"settings.set",
                "params":{
                    "context": context_json,
                    "key":"theme",
                    "value":{"type":"text","value":"dark"}
                }
            }),
        )
        .unwrap();
        assert_eq!(settings["changed_count"], 1);
        let resolved = call(
            handle,
            json!({
                "method":"settings.get",
                "params":{"context":context_json,"key":"theme"}
            }),
        )
        .unwrap();
        assert_eq!(resolved["value"]["value"], "dark");
        let search = call(
            handle,
            json!({
                "method":"search.query",
                "params":{"context":context_json,"query":"nothing","limit":5}
            }),
        )
        .unwrap();
        assert_eq!(search["total"], 0);

        // Old generation is rejected after a switch.
        let work = call(
            handle,
            json!({"method":"profile.create","params":{"id":"work","name":"Work"}}),
        )
        .unwrap();
        assert_eq!(work["id"], "work");
        let switched = call(
            handle,
            json!({"method":"profile.switch","params":{"id":"work"}}),
        )
        .unwrap();
        let stale = call(
            handle,
            json!({"method":"settings.get","params":{"context":context_json,"key":"theme"}}),
        );
        assert_eq!(stale.unwrap_err().0, AMARE_ERR_PROFILE_SWITCHED);
        assert!(switched["generation"].as_u64().unwrap() > 1);
        unsafe { amare_services_destroy(handle) };
    }

    #[test]
    fn abi_version_and_null_create_are_safe() {
        assert_eq!(amare_services_abi_version(), AMARE_SERVICES_ABI_VERSION);
        let mut handle: *mut AmareServices = std::ptr::null_mut();
        assert_eq!(
            unsafe { amare_services_create(ptr::null(), &mut handle) },
            crate::AMARE_ERR_INVALID_ARGUMENT
        );
        assert!(handle.is_null());
        unsafe { amare_services_destroy(ptr::null_mut()) };
    }

    #[test]
    fn invalid_json_and_null_handle_are_rejected() {
        let dir = tempfile::tempdir().unwrap();
        let root = CString::new(dir.path().to_str().unwrap()).unwrap();
        let mut handle = ptr::null_mut();
        assert_eq!(
            unsafe { amare_services_create(root.as_ptr(), &mut handle) },
            crate::AMARE_OK
        );
        let bad = CString::new("{not json").unwrap();
        let mut out = ptr::null_mut();
        assert_eq!(
            unsafe { amare_services_call(handle, bad.as_ptr(), &mut out) },
            AMARE_ERR_JSON
        );
        assert!(out.is_null());
        assert_eq!(
            unsafe { amare_services_call(ptr::null_mut(), bad.as_ptr(), &mut out) },
            crate::AMARE_ERR_INVALID_ARGUMENT
        );
        unsafe { amare_services_destroy(handle) };
    }
}
