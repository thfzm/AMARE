use amare_core::{Bookmark, HistoryEntry, NewBookmark, NewHistoryVisit, SettingValue};
use amare_services::{ProfileContext, ProfileService, ResolvedSetting, SettingsService};
use amare_store::{DownloadRecord, FolderScope};
use std::{
    cell::RefCell,
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicU64, Ordering},
    },
};

thread_local! {
    static LIBRARY: RefCell<Option<(Arc<ProfileService>, ProfileContext)>> = const { RefCell::new(None) };
    static SETTINGS_CACHE: RefCell<Option<(u64, BrowserSettings)>> = const { RefCell::new(None) };
}

static SETTINGS_GENERATION: AtomicU64 = AtomicU64::new(1);

#[derive(Clone, Debug, PartialEq)]
pub struct BrowserSettings {
    pub theme: String,
    pub ui_density: String,
    pub ui_font: String,
    pub ui_font_size: i64,
    pub tab_width: i64,
    pub restore_session: bool,
    pub save_session_on_exit: bool,
    pub home_page: String,
    pub new_tab_url: String,
    pub engine: String,
    pub address_behavior: String,
    pub download_path: String,
    pub download_panel_auto_open: bool,
    pub save_history: bool,
}

impl Default for BrowserSettings {
    fn default() -> Self {
        Self {
            theme: "system".into(),
            ui_density: "comfortable".into(),
            ui_font: "Segoe UI".into(),
            ui_font_size: 16,
            tab_width: 120,
            restore_session: true,
            save_session_on_exit: true,
            home_page: "https://example.com/".into(),
            new_tab_url: "about:blank".into(),
            engine: "duckduckgo".into(),
            address_behavior: "smart".into(),
            download_path: String::new(),
            download_panel_auto_open: false,
            save_history: true,
        }
    }
}

const SETTINGS_KEYS: &[&str] = &[
    "theme",
    "ui_density",
    "ui_font",
    "ui_font_size",
    "tab_width",
    "restore_session",
    "save_session_on_exit",
    "home_page",
    "new_tab_url",
    "engine",
    "address_behavior",
    "download_path",
    "download_panel_auto_open",
    "save_history",
];

fn text_setting(values: &[ResolvedSetting], key: &str, default: &str, choices: &[&str]) -> String {
    values
        .iter()
        .find(|value| value.key == key)
        .and_then(|value| match &value.value {
            SettingValue::Text(text) if choices.is_empty() || choices.contains(&text.as_str()) => {
                Some(text.clone())
            }
            _ => None,
        })
        .unwrap_or_else(|| default.to_string())
}

fn bool_setting(values: &[ResolvedSetting], key: &str, default: bool) -> bool {
    values
        .iter()
        .find(|value| value.key == key)
        .and_then(|value| match &value.value {
            SettingValue::Boolean(value) => Some(*value),
            _ => None,
        })
        .unwrap_or(default)
}

fn int_setting(values: &[ResolvedSetting], key: &str, default: i64, min: i64, max: i64) -> i64 {
    values
        .iter()
        .find(|value| value.key == key)
        .and_then(|value| match &value.value {
            SettingValue::Integer(value) if *value >= min && *value <= max => Some(*value),
            _ => None,
        })
        .unwrap_or(default)
}

impl BrowserSettings {
    fn from_resolved(values: &[ResolvedSetting]) -> Self {
        let defaults = Self::default();
        Self {
            theme: text_setting(
                values,
                "theme",
                &defaults.theme,
                &["system", "light", "dark"],
            ),
            ui_density: text_setting(
                values,
                "ui_density",
                &defaults.ui_density,
                &["comfortable", "compact"],
            ),
            ui_font: text_setting(values, "ui_font", &defaults.ui_font, &[]),
            ui_font_size: int_setting(values, "ui_font_size", defaults.ui_font_size, 12, 20),
            tab_width: int_setting(values, "tab_width", defaults.tab_width, 90, 180),
            restore_session: bool_setting(values, "restore_session", defaults.restore_session),
            save_session_on_exit: bool_setting(
                values,
                "save_session_on_exit",
                defaults.save_session_on_exit,
            ),
            home_page: text_setting(values, "home_page", &defaults.home_page, &[]),
            new_tab_url: text_setting(values, "new_tab_url", &defaults.new_tab_url, &[]),
            engine: text_setting(
                values,
                "engine",
                &defaults.engine,
                &["duckduckgo", "google", "bing"],
            ),
            address_behavior: text_setting(
                values,
                "address_behavior",
                &defaults.address_behavior,
                &["smart", "search"],
            ),
            download_path: text_setting(values, "download_path", &defaults.download_path, &[]),
            download_panel_auto_open: bool_setting(
                values,
                "download_panel_auto_open",
                defaults.download_panel_auto_open,
            ),
            save_history: bool_setting(values, "save_history", defaults.save_history),
        }
    }
}

pub fn browser_settings_cached() -> Result<(u64, BrowserSettings), String> {
    let generation = SETTINGS_GENERATION.load(Ordering::Relaxed);
    if let Some((cached_generation, settings)) = SETTINGS_CACHE.with(|cache| cache.borrow().clone())
        && cached_generation == generation
    {
        return Ok((generation, settings));
    }
    let settings = browser_settings()?;
    SETTINGS_CACHE.with(|cache| {
        *cache.borrow_mut() = Some((generation, settings.clone()));
    });
    Ok((generation, settings))
}

pub fn browser_settings() -> Result<BrowserSettings, String> {
    with_library(|service, context| {
        let settings = SettingsService::new(service);
        let values = settings
            .list_all(context)
            .map_err(|error| error.to_string())?;
        Ok(BrowserSettings::from_resolved(&values))
    })
}

pub fn set_browser_setting(key: &str, value: SettingValue) -> Result<BrowserSettings, String> {
    let settings = with_library(|service, context| {
        let settings = SettingsService::new(service);
        settings
            .set(context, key, value)
            .map_err(|error| error.to_string())?;
        let values = settings
            .list_all(context)
            .map_err(|error| error.to_string())?;
        Ok(BrowserSettings::from_resolved(&values))
    })?;
    SETTINGS_GENERATION.fetch_add(1, Ordering::Relaxed);
    SETTINGS_CACHE.with(|cache| *cache.borrow_mut() = None);
    Ok(settings)
}

pub fn reset_browser_settings() -> Result<BrowserSettings, String> {
    let settings = with_library(|service, context| {
        let settings = SettingsService::new(service);
        for key in SETTINGS_KEYS {
            settings
                .reset_key(context, key)
                .map_err(|error| error.to_string())?;
        }
        let values = settings
            .list_all(context)
            .map_err(|error| error.to_string())?;
        Ok(BrowserSettings::from_resolved(&values))
    })?;
    SETTINGS_GENERATION.fetch_add(1, Ordering::Relaxed);
    SETTINGS_CACHE.with(|cache| *cache.borrow_mut() = None);
    Ok(settings)
}

pub fn data_root_display() -> String {
    data_root().display().to_string()
}

pub fn data_root_path() -> PathBuf {
    data_root()
}

fn data_root() -> PathBuf {
    if let Ok(path) = std::env::var("AMARE_GPUI_DATA_DIR") {
        return PathBuf::from(path);
    }
    if let Ok(appdata) = std::env::var("APPDATA") {
        return PathBuf::from(appdata).join("AMARE");
    }
    std::env::temp_dir().join("amare-gpui")
}

fn with_library<R>(
    f: impl FnOnce(&ProfileService, &ProfileContext) -> Result<R, String>,
) -> Result<R, String> {
    LIBRARY.with(|cell| {
        if cell.borrow().is_none() {
            let service = Arc::new(ProfileService::open(data_root()).map_err(|e| e.to_string())?);
            let context = service.initialize().map_err(|e| e.to_string())?;
            *cell.borrow_mut() = Some((service, context));
        }
        let binding = cell.borrow();
        let (service, context) = binding.as_ref().expect("library initialized");
        f(service, context)
    })
}

pub fn bookmarks() -> Result<Vec<Bookmark>, String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        let page = profile
            .bookmarks()
            .list(FolderScope::All, 500, 0)
            .map_err(|e| e.to_string())?;
        Ok(page.items)
    })
}

pub fn toggle_bookmark(title: &str, url: &str) -> Result<(bool, Vec<Bookmark>), String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        let page = profile
            .bookmarks()
            .list(FolderScope::All, 500, 0)
            .map_err(|e| e.to_string())?;
        let existing = page
            .items
            .iter()
            .find(|bookmark| bookmark.url == url)
            .cloned();
        if let Some(bookmark) = existing {
            profile
                .bookmarks()
                .delete(bookmark.id)
                .map_err(|e| e.to_string())?;
            let page = profile
                .bookmarks()
                .list(FolderScope::All, 500, 0)
                .map_err(|e| e.to_string())?;
            return Ok((false, page.items));
        }
        let title = title.chars().take(512).collect::<String>();
        let url = url.chars().take(4096).collect::<String>();
        profile
            .bookmarks()
            .create(NewBookmark {
                title,
                url,
                folder_id: None,
                position: None,
            })
            .map_err(|e| e.to_string())?;
        let page = profile
            .bookmarks()
            .list(FolderScope::All, 500, 0)
            .map_err(|e| e.to_string())?;
        Ok((true, page.items))
    })
}

pub fn delete_bookmark(id: i64) -> Result<Vec<Bookmark>, String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        profile.bookmarks().delete(id).map_err(|e| e.to_string())?;
        let page = profile
            .bookmarks()
            .list(FolderScope::All, 500, 0)
            .map_err(|e| e.to_string())?;
        Ok(page.items)
    })
}

pub fn save_session(json: &str) -> Result<(), String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        profile
            .settings()
            .set(
                "gpui.session",
                "browser",
                SettingValue::Json(json.to_string()),
            )
            .map(|_| ())
            .map_err(|e| e.to_string())
    })
}

#[derive(Clone, Debug)]
pub struct SessionSummary {
    pub windows: usize,
    pub tabs: usize,
    pub saved_at_ms: i64,
}

pub fn session_summary() -> Result<Option<SessionSummary>, String> {
    let Some(json) = load_session()? else {
        return Ok(None);
    };
    let value: serde_json::Value = serde_json::from_str(&json).map_err(|e| e.to_string())?;
    let windows = value.as_array().map(|items| items.len()).unwrap_or(0);
    let tabs = value
        .as_array()
        .map(|items| {
            items
                .iter()
                .map(|window| {
                    window
                        .get("tabs")
                        .and_then(|tabs| tabs.as_array())
                        .map(|tabs| tabs.len())
                        .unwrap_or(0)
                })
                .sum()
        })
        .unwrap_or(0);
    let saved_at_ms = value
        .as_array()
        .map(|items| {
            items
                .iter()
                .filter_map(|window| window.get("saved_at_ms").and_then(|value| value.as_i64()))
                .max()
                .unwrap_or(0)
        })
        .unwrap_or(0);
    Ok(Some(SessionSummary {
        windows,
        tabs,
        saved_at_ms,
    }))
}

pub fn save_corrupt_session(json: &str) -> Result<(), String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        profile
            .settings()
            .set(
                "gpui.session.corrupt",
                "browser",
                SettingValue::Json(json.to_string()),
            )
            .map(|_| ())
            .map_err(|e| e.to_string())
    })
}

pub fn clear_session() -> Result<(), String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        profile
            .settings()
            .delete("gpui.session")
            .map(|_| ())
            .map_err(|e| e.to_string())
    })
}

pub fn load_session() -> Result<Option<String>, String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        let value = profile
            .settings()
            .get("gpui.session")
            .map_err(|e| e.to_string())?;
        Ok(match value {
            Some(entry) => match entry.value {
                SettingValue::Json(json) => Some(json),
                _ => None,
            },
            None => None,
        })
    })
}

pub fn downloads() -> Result<Vec<DownloadRecord>, String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        profile.downloads().list(500, 0).map_err(|e| e.to_string())
    })
}

pub fn upsert_download(
    id: i64,
    filename: &str,
    url: &str,
    path: &str,
    mime_type: &str,
    total_bytes: i64,
    received_bytes: i64,
    state: &str,
    error: &str,
) -> Result<(), String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        profile
            .downloads()
            .upsert(
                id,
                filename,
                url,
                path,
                mime_type,
                total_bytes,
                received_bytes,
                state,
                error,
            )
            .map_err(|e| e.to_string())
    })
}

pub fn delete_download(id: i64) -> Result<Vec<DownloadRecord>, String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        profile.downloads().delete(id).map_err(|e| e.to_string())?;
        profile.downloads().list(500, 0).map_err(|e| e.to_string())
    })
}

pub fn history(query: &str) -> Result<Vec<HistoryEntry>, String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        let query = query.trim();
        let page = profile
            .history()
            .search(
                if query.is_empty() { None } else { Some(query) },
                None,
                None,
                500,
                0,
            )
            .map_err(|e| e.to_string())?;
        Ok(page.items)
    })
}

pub fn record_history(title: &str, url: &str, visited_at_ms: i64) -> Result<(), String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        profile
            .history()
            .record_visit(NewHistoryVisit {
                url: url.chars().take(4096).collect(),
                title: title.chars().take(1024).collect(),
                visited_at_ms,
            })
            .map_err(|e| e.to_string())
    })
}

pub fn delete_history(url: &str) -> Result<Vec<HistoryEntry>, String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        profile
            .history()
            .delete_url(url)
            .map_err(|e| e.to_string())?;
        let page = profile
            .history()
            .search(None, None, None, 500, 0)
            .map_err(|e| e.to_string())?;
        Ok(page.items)
    })
}

pub fn clear_history() -> Result<Vec<HistoryEntry>, String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        profile.history().clear().map_err(|e| e.to_string())?;
        let page = profile
            .history()
            .search(None, None, None, 500, 0)
            .map_err(|e| e.to_string())?;
        Ok(page.items)
    })
}
