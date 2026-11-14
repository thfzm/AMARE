#![recursion_limit = "512"]

use std::cell::RefCell;
use std::collections::BTreeMap;
use std::sync::Mutex;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use amare_core::{Bookmark, HistoryEntry, SettingValue};
use gpui::{
    App, Application, Bounds, Context, FocusHandle, Focusable, KeyBinding, Render, SharedString,
    StatefulInteractiveElement, Timer, Window, WindowAppearance, WindowBounds, WindowControlArea,
    WindowOptions, actions, div, point, prelude::*, px, rgb, size,
};
use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use serde::{Deserialize, Serialize};
use zeroize::Zeroize;

#[cfg(target_os = "windows")]
use windows::Win32::{
    Foundation::POINT,
    UI::{
        Input::KeyboardAndMouse::{GetAsyncKeyState, SetFocus, VK_LBUTTON},
        WindowsAndMessaging::GetCursorPos,
    },
};

mod library;
mod password_vault;
mod text_input;
mod webview;

use text_input::AddressInput;

#[cfg(target_os = "windows")]
use windows::Win32::Foundation::HWND;

actions!(
    amare,
    [
        FocusAddress,
        ReloadPage,
        BackPage,
        ForwardPage,
        SubmitAddress,
        CancelAddress,
        NewTab,
        CloseTab,
        NewWindow,
        NextTab,
        PreviousTab,
        ToggleBookmark,
        ToggleBookmarksPanel,
        ToggleHistoryPanel,
        ToggleDownloadsPanel,
        ToggleSettings,
        TogglePasswords,
        CapturePage,
    ]
);

const TOOLBAR_HEIGHT: f32 = 88.0;
const TAB_START_X: f32 = 12.0;
const TAB_SLOT: f32 = 124.0;
const PANEL_WIDTH: f32 = 360.0;
const PASSWORD_PANEL_WIDTH: f32 = 470.0;

#[derive(Clone, Copy, PartialEq, Eq)]
enum LibraryPanel {
    None,
    Bookmarks,
    History,
    Downloads,
    Settings,
    Passwords,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum SettingsCategory {
    Appearance,
    Startup,
    Search,
    Downloads,
    Privacy,
    Advanced,
    Permissions,
}

impl SettingsCategory {
    const ALL: [Self; 7] = [
        Self::Appearance,
        Self::Startup,
        Self::Search,
        Self::Downloads,
        Self::Privacy,
        Self::Advanced,
        Self::Permissions,
    ];

    fn label(self) -> &'static str {
        match self {
            Self::Appearance => "외관",
            Self::Startup => "시작 및 세션",
            Self::Search => "검색",
            Self::Downloads => "다운로드",
            Self::Privacy => "개인정보 및 보안",
            Self::Advanced => "고급",
            Self::Permissions => "사이트 권한",
        }
    }

    fn key(self) -> &'static str {
        match self {
            Self::Appearance => "appearance",
            Self::Startup => "startup",
            Self::Search => "search",
            Self::Downloads => "downloads",
            Self::Privacy => "privacy",
            Self::Advanced => "advanced",
            Self::Permissions => "permissions",
        }
    }
}

struct SettingsInputs {
    search: gpui::Entity<AddressInput>,
    home_page: gpui::Entity<AddressInput>,
    new_tab_url: gpui::Entity<AddressInput>,
    download_path: gpui::Entity<AddressInput>,
    font: gpui::Entity<AddressInput>,
}

fn create_settings_inputs(cx: &mut App, settings: &library::BrowserSettings) -> SettingsInputs {
    SettingsInputs {
        search: cx.new(|cx| AddressInput::new_with_placeholder("", "설정 검색", cx)),
        home_page: cx.new(|cx| {
            AddressInput::new_with_placeholder(settings.home_page.clone(), "시작 페이지 URL", cx)
        }),
        new_tab_url: cx.new(|cx| {
            AddressInput::new_with_placeholder(settings.new_tab_url.clone(), "새 탭 URL", cx)
        }),
        download_path: cx.new(|cx| {
            AddressInput::new_with_placeholder(
                settings.download_path.clone(),
                "비우면 사용자 Downloads 폴더",
                cx,
            )
        }),
        font: cx
            .new(|cx| AddressInput::new_with_placeholder(settings.ui_font.clone(), "Segoe UI", cx)),
    }
}

fn effective_dark(theme: &str, window: &Window) -> bool {
    match theme {
        "dark" => true,
        "light" => false,
        _ => matches!(
            window.appearance(),
            WindowAppearance::Dark | WindowAppearance::VibrantDark
        ),
    }
}

fn apply_platform_settings(settings: &library::BrowserSettings) {
    #[cfg(target_os = "windows")]
    {
        let path = settings.download_path.trim();
        webview::set_download_directory_override(if path.is_empty() {
            None
        } else {
            Some(std::path::PathBuf::from(path))
        });
    }
    #[cfg(not(target_os = "windows"))]
    let _ = settings;
}

static NEXT_WINDOW_ID: AtomicU64 = AtomicU64::new(1);

#[derive(Clone, PartialEq, Serialize, Deserialize)]
struct SessionTabSnapshot {
    url: String,
    title: String,
    #[serde(default)]
    scroll_x: i64,
    #[serde(default)]
    scroll_y: i64,
}

#[derive(Clone, PartialEq, Serialize, Deserialize)]
struct SessionWindowSnapshot {
    window_id: u64,
    x: f32,
    y: f32,
    width: f32,
    height: f32,
    maximized: bool,
    active: usize,
    tabs: Vec<SessionTabSnapshot>,
    #[serde(default)]
    saved_at_ms: i64,
}

thread_local! {
    static SESSION_WINDOWS: RefCell<BTreeMap<u64, SessionWindowSnapshot>> = const { RefCell::new(BTreeMap::new()) };
    static SESSION_CLOSED_WINDOWS: RefCell<BTreeMap<u64, SessionWindowSnapshot>> = const { RefCell::new(BTreeMap::new()) };
    static STARTUP_SESSION_ERROR: RefCell<Option<String>> = const { RefCell::new(None) };
}

#[derive(Clone)]
struct RestoreProgress {
    total: usize,
    finished: usize,
    failed: usize,
    visible_until: Instant,
}

#[derive(Clone)]
struct TabDragInfo {
    id: u64,
}

struct DropRequest {
    serial: u64,
    source_window_id: u64,
    tab_id: u64,
    x: i32,
    y: i32,
}

static DROP_REQUEST: Mutex<Option<DropRequest>> = Mutex::new(None);
static DROP_SERIAL: AtomicU64 = AtomicU64::new(1);

struct TabDragPreview {
    id: u64,
}

impl Render for TabDragPreview {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
            .px_3()
            .h(px(26.0))
            .rounded_md()
            .bg(rgb(0x2f7df6))
            .text_color(rgb(0xffffff))
            .text_sm()
            .child(format!("tab {}", self.id))
    }
}

#[cfg(target_os = "windows")]
fn start_drop_watch(source_window_id: u64, tab_id: u64) {
    let serial = DROP_SERIAL.fetch_add(1, Ordering::Relaxed);
    std::thread::spawn(move || {
        loop {
            std::thread::sleep(std::time::Duration::from_millis(15));
            let down = unsafe { GetAsyncKeyState(VK_LBUTTON.0 as i32) } < 0;
            if !down {
                let mut point = POINT::default();
                if unsafe { GetCursorPos(&mut point) }.is_ok()
                    && DROP_SERIAL.load(Ordering::Relaxed) == serial
                {
                    if let Ok(mut request) = DROP_REQUEST.lock() {
                        *request = Some(DropRequest {
                            serial,
                            source_window_id,
                            tab_id,
                            x: point.x,
                            y: point.y,
                        });
                    }
                }
                break;
            }
        }
    });
}

struct TabState {
    id: u64,
    title: SharedString,
    url: SharedString,
    loading: bool,
    error: Option<SharedString>,
    can_go_back: bool,
    can_go_forward: bool,
    favicon: Option<SharedString>,
    last_active: u64,
    history_url: String,
    scroll_x: i64,
    scroll_y: i64,
    restored: bool,
    #[cfg(target_os = "windows")]
    pending_scroll: Option<(i64, i64)>,
    #[cfg(target_os = "windows")]
    webview: Option<webview::WebViewHost>,
}

struct PendingLogin {
    source: String,
    origin: String,
    username: String,
    password: String,
}

impl Drop for PendingLogin {
    fn drop(&mut self) {
        self.password.zeroize();
    }
}

struct PasswordUi {
    vault: password_vault::PasswordVault,
    query: String,
    search: gpui::Entity<AddressInput>,
    origin: gpui::Entity<AddressInput>,
    username: gpui::Entity<AddressInput>,
    password: gpui::Entity<AddressInput>,
    password_masked: bool,
    form_open: bool,
    editing: Option<u64>,
    reveal: Option<u64>,
    confirm_delete: Option<u64>,
    pending: Option<PendingLogin>,
    error: Option<String>,
    conflict: Option<String>,
    info: Option<String>,
    vault_generation: u64,
    lock_epoch: u64,
    recovery_path: Option<String>,
}

fn create_password_ui(cx: &mut App) -> PasswordUi {
    PasswordUi {
        vault: password_vault::PasswordVault::new(),
        query: String::new(),
        search: cx
            .new(|cx| AddressInput::new_with_placeholder("", "사이트 또는 사용자 이름 검색", cx)),
        origin: cx.new(|cx| AddressInput::new_with_placeholder("", "https://example.com", cx)),
        username: cx.new(|cx| AddressInput::new_with_placeholder("", "사용자 이름", cx)),
        password: cx.new(|cx| {
            let mut input = AddressInput::new_with_placeholder("", "비밀번호", cx);
            input.set_masked(true, cx);
            input
        }),
        password_masked: true,
        form_open: false,
        editing: None,
        reveal: None,
        confirm_delete: None,
        pending: None,
        error: None,
        conflict: None,
        info: None,
        vault_generation: 0,
        lock_epoch: password_vault::lock_state().0,
        recovery_path: None,
    }
}

struct AmareApp {
    tabs: Vec<TabState>,
    active: usize,
    next_tab_id: u64,
    window_id: u64,
    activity_counter: u64,
    address_input: gpui::Entity<AddressInput>,
    search_input: gpui::Entity<AddressInput>,
    panel: LibraryPanel,
    bookmarks: Vec<Bookmark>,
    history: Vec<HistoryEntry>,
    downloads: Vec<amare_store::DownloadRecord>,
    history_query: String,
    confirm_clear_history: bool,
    library_error: Option<String>,
    focus_handle: FocusHandle,
    #[cfg(target_os = "windows")]
    parent: Option<HWND>,
    #[cfg(target_os = "windows")]
    webview_env: Option<std::sync::Arc<webview::WebViewEnvironment>>,
    #[cfg(target_os = "windows")]
    webview_error: Option<String>,
    status_poll_started: bool,
    last_session_save: Instant,
    settings: library::BrowserSettings,
    settings_generation: u64,
    settings_category: SettingsCategory,
    settings_search: gpui::Entity<AddressInput>,
    settings_home_page: gpui::Entity<AddressInput>,
    settings_new_tab: gpui::Entity<AddressInput>,
    settings_download_path: gpui::Entity<AddressInput>,
    settings_font: gpui::Entity<AddressInput>,
    settings_confirm_reset: bool,
    settings_error: Option<String>,
    settings_last_text_apply: Instant,
    runtime_version: String,
    restore_progress: Option<RestoreProgress>,
    session_summary: Option<library::SessionSummary>,
    session_error: Option<String>,
    password_ui: PasswordUi,
    confirm_clear_site_data: bool,
    site_data_cleared: bool,
    capture_count: u64,
    last_capture_path: Option<String>,
    last_capture_error: Option<String>,
    last_capture_size: Option<(u32, u32)>,
    last_auto_capture_source: String,
    diagnostics: Vec<String>,
    next_input_id: u64,
    pending_input_id: Option<u64>,
    layout_bounds: Option<serde_json::Value>,
}

impl AmareApp {
    fn diagnostics_enabled() -> bool {
        std::env::var("AMARE_GPUI_DIAGNOSTICS").ok().as_deref() == Some("1")
    }

    fn trace_event(&mut self, message: impl Into<String>) {
        if !Self::diagnostics_enabled() {
            return;
        }
        let elapsed = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis();
        let message = message.into();
        self.diagnostics
            .push(format!("{elapsed} window={} {message}", self.window_id));
        if self.diagnostics.len() > 256 {
            self.diagnostics.remove(0);
        }
    }

    fn begin_input(&mut self, kind: &str) -> u64 {
        let id = self.next_input_id;
        self.next_input_id += 1;
        self.pending_input_id = Some(id);
        self.trace_event(format!("input={id} {kind}"));
        id
    }

    fn record_layout(&mut self, bounds: Vec<gpui::Bounds<gpui::Pixels>>, window: &Window) {
        if !Self::diagnostics_enabled() {
            return;
        }
        let names = [
            "tab_strip",
            "new_tab",
            "new_window",
            "window_min",
            "window_max",
            "window_close",
        ];
        let mut children = serde_json::Map::new();
        for (index, name) in names.iter().enumerate() {
            if let Some(bounds) = bounds.get(index) {
                children.insert(
                    (*name).to_string(),
                    serde_json::json!({
                        "x": f32::from(bounds.origin.x),
                        "y": f32::from(bounds.origin.y),
                        "width": f32::from(bounds.size.width),
                        "height": f32::from(bounds.size.height),
                    }),
                );
            }
        }
        let window_bounds = window.bounds();
        self.layout_bounds = Some(serde_json::json!({
            "window": {
                "x": f32::from(window_bounds.origin.x),
                "y": f32::from(window_bounds.origin.y),
                "width": f32::from(window_bounds.size.width),
                "height": f32::from(window_bounds.size.height),
            },
            "scale_factor": window.scale_factor(),
            "toolbar_children": children,
        }));
    }

    fn trace_action(&mut self, action: &str) {
        let input = self.pending_input_id.take();
        self.trace_event(format!("action={action} input={input:?}"));
    }

    fn new(window: &mut Window, cx: &mut App) -> Self {
        let (_, settings) = library::browser_settings_cached()
            .unwrap_or_else(|_| (0, library::BrowserSettings::default()));
        let start_url =
            std::env::var("AMARE_GPUI_START_URL").unwrap_or_else(|_| settings.home_page.clone());
        Self::new_with_url(window, cx, start_url, true, false)
    }

    fn new_with_url(
        window: &mut Window,
        cx: &mut App,
        start_url: String,
        run_self_test: bool,
        defer_webview: bool,
    ) -> Self {
        let focus_handle = cx.focus_handle();
        focus_handle.focus(window);
        let (settings_generation, settings) = library::browser_settings_cached()
            .unwrap_or_else(|_| (0, library::BrowserSettings::default()));
        let settings_inputs = create_settings_inputs(cx, &settings);
        let runtime_version = runtime_version();

        #[cfg(target_os = "windows")]
        let parent = window
            .window_handle()
            .ok()
            .map(|handle| handle.as_raw())
            .and_then(|handle| match handle {
                RawWindowHandle::Win32(handle) => Some(HWND(handle.hwnd.get() as *mut _)),
                _ => None,
            });
        #[cfg(target_os = "windows")]
        let (webview_env, mut webview_error) = match parent {
            Some(_) => match webview::shared_environment() {
                Ok(env) => (Some(env), None),
                Err(error) => (None, Some(error)),
            },
            None => (None, Some("Windows HWND unavailable".to_string())),
        };

        #[cfg(target_os = "windows")]
        let first_webview = if defer_webview {
            None
        } else {
            match (webview_env.as_ref(), parent) {
                (Some(env), Some(parent)) => match webview::WebViewHost::create(env, parent) {
                    Ok(webview) => Some(webview),
                    Err(error) => {
                        webview_error = Some(error);
                        None
                    }
                },
                _ => None,
            }
        };
        #[cfg(not(target_os = "windows"))]
        let first_webview = ();

        let id = 1;
        let next_tab_id = 2;
        #[cfg(target_os = "windows")]
        let first_error = webview_error.clone().map(Into::into);
        #[cfg(not(target_os = "windows"))]
        let first_error: Option<SharedString> = None;

        let tabs = vec![TabState {
            id,
            title: "New Tab".into(),
            url: start_url.clone().into(),
            loading: defer_webview,
            error: first_error,
            can_go_back: false,
            can_go_forward: false,
            favicon: None,
            last_active: 1,
            history_url: String::new(),
            scroll_x: 0,
            scroll_y: 0,
            restored: false,
            #[cfg(target_os = "windows")]
            pending_scroll: None,
            #[cfg(target_os = "windows")]
            webview: first_webview,
        }];

        let address_input = cx.new(|cx| AddressInput::new(start_url.clone(), cx));
        let search_input = cx.new(|cx| AddressInput::new("", cx));
        let bookmarks = library::bookmarks().unwrap_or_default();
        let history = library::history("").unwrap_or_default();
        let downloads = library::downloads().unwrap_or_default();
        let app = Self {
            tabs,
            active: 0,
            next_tab_id,
            window_id: NEXT_WINDOW_ID.fetch_add(1, Ordering::Relaxed),
            activity_counter: 1,
            address_input,
            search_input,
            panel: LibraryPanel::None,
            bookmarks,
            history,
            downloads,
            history_query: String::new(),
            confirm_clear_history: false,
            library_error: None,
            focus_handle,
            #[cfg(target_os = "windows")]
            parent,
            #[cfg(target_os = "windows")]
            webview_env,
            #[cfg(target_os = "windows")]
            webview_error,
            status_poll_started: false,
            last_session_save: Instant::now(),
            settings,
            settings_generation,
            settings_category: SettingsCategory::Appearance,
            settings_search: settings_inputs.search,
            settings_home_page: settings_inputs.home_page,
            settings_new_tab: settings_inputs.new_tab_url,
            settings_download_path: settings_inputs.download_path,
            settings_font: settings_inputs.font,
            settings_confirm_reset: false,
            settings_error: None,
            settings_last_text_apply: Instant::now(),
            runtime_version,
            restore_progress: None,
            session_summary: library::session_summary().ok().flatten(),
            session_error: STARTUP_SESSION_ERROR.with(|error| error.borrow().clone()),
            password_ui: create_password_ui(cx),
            confirm_clear_site_data: false,
            site_data_cleared: false,
            capture_count: 0,
            last_capture_path: None,
            last_capture_error: None,
            last_capture_size: None,
            last_auto_capture_source: String::new(),
            diagnostics: Vec::new(),
            next_input_id: 1,
            pending_input_id: None,
            layout_bounds: None,
        };
        apply_platform_settings(&app.settings);

        #[cfg(target_os = "windows")]
        if !defer_webview
            && run_self_test
            && std::env::var("AMARE_GPUI_SELF_TEST").ok().as_deref() == Some("1")
        {
            if let Some(view) = app.active_webview() {
                let _ = view.run_self_test();
            }
        }

        #[cfg(target_os = "windows")]
        if !defer_webview && let Some(webview) = app.active_webview() {
            webview.navigate(&start_url);
            webview.set_active(true);
        }

        let address_focus = app.address_input.read(cx).focus_handle(cx);
        window.focus(&address_focus);
        app
    }

    fn new_with_seed(window: &mut Window, cx: &mut App, seed: TabState) -> Self {
        let focus_handle = cx.focus_handle();
        focus_handle.focus(window);
        let (settings_generation, settings) = library::browser_settings_cached()
            .unwrap_or_else(|_| (0, library::BrowserSettings::default()));
        let settings_inputs = create_settings_inputs(cx, &settings);
        let runtime_version = runtime_version();
        #[cfg(target_os = "windows")]
        let parent = window
            .window_handle()
            .ok()
            .map(|handle| handle.as_raw())
            .and_then(|handle| match handle {
                RawWindowHandle::Win32(handle) => Some(HWND(handle.hwnd.get() as *mut _)),
                _ => None,
            });
        #[cfg(target_os = "windows")]
        let webview_env = webview::shared_environment().ok();
        let address = seed.url.to_string();
        let mut app = Self {
            tabs: vec![seed],
            active: 0,
            next_tab_id: 2,
            window_id: NEXT_WINDOW_ID.fetch_add(1, Ordering::Relaxed),
            activity_counter: 1,
            address_input: cx.new(|cx| AddressInput::new(address.clone(), cx)),
            search_input: cx.new(|cx| AddressInput::new("", cx)),
            panel: LibraryPanel::None,
            bookmarks: library::bookmarks().unwrap_or_default(),
            history: library::history("").unwrap_or_default(),
            downloads: library::downloads().unwrap_or_default(),
            history_query: String::new(),
            confirm_clear_history: false,
            library_error: None,
            focus_handle,
            #[cfg(target_os = "windows")]
            parent,
            #[cfg(target_os = "windows")]
            webview_env,
            #[cfg(target_os = "windows")]
            webview_error: None,
            status_poll_started: false,
            last_session_save: Instant::now(),
            settings,
            settings_generation,
            settings_category: SettingsCategory::Appearance,
            settings_search: settings_inputs.search,
            settings_home_page: settings_inputs.home_page,
            settings_new_tab: settings_inputs.new_tab_url,
            settings_download_path: settings_inputs.download_path,
            settings_font: settings_inputs.font,
            settings_confirm_reset: false,
            settings_error: None,
            settings_last_text_apply: Instant::now(),
            runtime_version,
            restore_progress: None,
            session_summary: library::session_summary().ok().flatten(),
            session_error: STARTUP_SESSION_ERROR.with(|error| error.borrow().clone()),
            password_ui: create_password_ui(cx),
            confirm_clear_site_data: false,
            site_data_cleared: false,
            capture_count: 0,
            last_capture_path: None,
            last_capture_error: None,
            last_capture_size: None,
            last_auto_capture_source: String::new(),
            diagnostics: Vec::new(),
            next_input_id: 1,
            pending_input_id: None,
            layout_bounds: None,
        };
        apply_platform_settings(&app.settings);
        #[cfg(target_os = "windows")]
        if let (Some(parent), Some(webview)) = (app.parent, app.active_webview_mut()) {
            if webview.reparent(parent).is_ok() {
                webview.set_active(true);
                webview.focus();
            }
        }
        app
    }

    fn new_restored(window: &mut Window, cx: &mut App, snapshot: SessionWindowSnapshot) -> Self {
        let focus_handle = cx.focus_handle();
        focus_handle.focus(window);
        let (settings_generation, settings) = library::browser_settings_cached()
            .unwrap_or_else(|_| (0, library::BrowserSettings::default()));
        let settings_inputs = create_settings_inputs(cx, &settings);
        let runtime_version = runtime_version();
        #[cfg(target_os = "windows")]
        let parent = window
            .window_handle()
            .ok()
            .map(|handle| handle.as_raw())
            .and_then(|handle| match handle {
                RawWindowHandle::Win32(handle) => Some(HWND(handle.hwnd.get() as *mut _)),
                _ => None,
            });
        #[cfg(target_os = "windows")]
        let webview_env = webview::shared_environment().ok();
        let mut tabs = Vec::new();
        for (index, restored) in snapshot.tabs.iter().enumerate() {
            #[cfg(target_os = "windows")]
            let webview = match (webview_env.as_ref(), parent) {
                (Some(env), Some(parent)) => webview::WebViewHost::create(env, parent).ok(),
                _ => None,
            };
            #[cfg(not(target_os = "windows"))]
            let webview = ();
            #[cfg(target_os = "windows")]
            let restore_error = if webview.is_none() {
                Some("WebView2 controller unavailable for restored tab".into())
            } else {
                None
            };
            #[cfg(not(target_os = "windows"))]
            let restore_error: Option<SharedString> = None;
            let pending_scroll = if restored.scroll_x != 0 || restored.scroll_y != 0 {
                Some((restored.scroll_x, restored.scroll_y))
            } else {
                None
            };
            let tab = TabState {
                id: index as u64 + 1,
                title: restored.title.clone().into(),
                url: restored.url.clone().into(),
                loading: true,
                error: restore_error,
                can_go_back: false,
                can_go_forward: false,
                favicon: None,
                last_active: index as u64 + 1,
                history_url: String::new(),
                scroll_x: restored.scroll_x,
                scroll_y: restored.scroll_y,
                restored: true,
                #[cfg(target_os = "windows")]
                pending_scroll,
                #[cfg(target_os = "windows")]
                webview,
            };
            #[cfg(target_os = "windows")]
            if let Some(view) = &tab.webview {
                view.navigate(&restored.url);
                view.set_active(false);
            }
            tabs.push(tab);
        }
        if tabs.is_empty() {
            tabs.push(TabState {
                id: 1,
                title: "New Tab".into(),
                url: "about:blank".into(),
                loading: false,
                error: None,
                can_go_back: false,
                can_go_forward: false,
                favicon: None,
                last_active: 1,
                history_url: String::new(),
                scroll_x: 0,
                scroll_y: 0,
                restored: false,
                #[cfg(target_os = "windows")]
                pending_scroll: None,
                #[cfg(target_os = "windows")]
                webview: None,
            });
        }
        let active = snapshot.active.min(tabs.len() - 1);
        let address = tabs[active].url.to_string();
        let address_input = cx.new(|cx| AddressInput::new(address.clone(), cx));
        let search_input = cx.new(|cx| AddressInput::new("", cx));
        let bookmarks = library::bookmarks().unwrap_or_default();
        let history = library::history("").unwrap_or_default();
        let downloads = library::downloads().unwrap_or_default();
        let mut app = Self {
            tabs,
            active,
            next_tab_id: snapshot.tabs.len() as u64 + 1,
            window_id: NEXT_WINDOW_ID.fetch_add(1, Ordering::Relaxed),
            activity_counter: snapshot.active as u64 + 1,
            address_input,
            search_input,
            panel: LibraryPanel::None,
            bookmarks,
            history,
            downloads,
            history_query: String::new(),
            confirm_clear_history: false,
            library_error: None,
            focus_handle,
            #[cfg(target_os = "windows")]
            parent,
            #[cfg(target_os = "windows")]
            webview_env,
            #[cfg(target_os = "windows")]
            webview_error: None,
            status_poll_started: false,
            last_session_save: Instant::now(),
            settings,
            settings_generation,
            settings_category: SettingsCategory::Appearance,
            settings_search: settings_inputs.search,
            settings_home_page: settings_inputs.home_page,
            settings_new_tab: settings_inputs.new_tab_url,
            settings_download_path: settings_inputs.download_path,
            settings_font: settings_inputs.font,
            settings_confirm_reset: false,
            settings_error: None,
            settings_last_text_apply: Instant::now(),
            runtime_version,
            restore_progress: None,
            session_summary: library::session_summary().ok().flatten(),
            session_error: STARTUP_SESSION_ERROR.with(|error| error.borrow().clone()),
            password_ui: create_password_ui(cx),
            confirm_clear_site_data: false,
            site_data_cleared: false,
            capture_count: 0,
            last_capture_path: None,
            last_capture_error: None,
            last_capture_size: None,
            last_auto_capture_source: String::new(),
            diagnostics: Vec::new(),
            next_input_id: 1,
            pending_input_id: None,
            layout_bounds: None,
        };
        apply_platform_settings(&app.settings);
        app.restore_progress = Some(RestoreProgress {
            total: app.tabs.len(),
            finished: 0,
            failed: app.tabs.iter().filter(|tab| tab.error.is_some()).count(),
            visible_until: Instant::now() + Duration::from_secs(8),
        });
        #[cfg(target_os = "windows")]
        if let Some(webview) = app.active_webview() {
            webview.set_active(true);
        }
        let address_focus = app.address_input.read(cx).focus_handle(cx);
        window.focus(&address_focus);
        app
    }

    fn start_status_poll(&mut self, cx: &mut Context<Self>) {
        cx.spawn(async move |this, cx| {
            loop {
                Timer::after(Duration::from_millis(250)).await;
                if this.upgrade().is_none() {
                    break;
                }
                this.update(cx, |app, cx| {
                    app.maybe_auto_capture(cx);
                    app.write_status(cx);
                    cx.notify();
                });
            }
        })
        .detach();
    }

    fn write_status(&mut self, cx: &mut Context<Self>) {
        let path = if let Ok(directory) = std::env::var("AMARE_GPUI_STATUS_DIR") {
            let directory = std::path::PathBuf::from(directory);
            if std::fs::create_dir_all(&directory).is_err() {
                return;
            }
            directory.join(format!(
                "status-{}-{}.json",
                std::process::id(),
                self.window_id
            ))
        } else {
            let Ok(path) = std::env::var("AMARE_GPUI_STATUS") else {
                return;
            };
            match std::env::var("AMARE_GPUI_STATUS_WINDOW") {
                Ok(expected) => {
                    if expected.parse::<u64>().ok() != Some(self.window_id) {
                        return;
                    }
                }
                Err(_) if self.window_id != 1 => return,
                Err(_) => {}
            }
            std::path::PathBuf::from(path)
        };
        #[cfg(target_os = "windows")]
        if let Some(webview) = self.active_webview() {
            webview.ensure_ime();
        }
        let input = self.address_input.read(cx);
        let address = input.content();
        let marked = input
            .marked_range()
            .map(|range| serde_json::json!([range.start, range.end]));
        let selected = input.selected_range();
        let active = self.active_tab();
        #[cfg(target_os = "windows")]
        let (source, title) = self
            .active_webview()
            .map(|webview| (webview.source(), webview.title()))
            .unwrap_or_default();
        #[cfg(not(target_os = "windows"))]
        let (source, title) = (String::new(), String::new());
        #[cfg(target_os = "windows")]
        let ime_context = self
            .active_webview()
            .map(|webview| webview.ime_context())
            .unwrap_or(false);
        #[cfg(not(target_os = "windows"))]
        let ime_context = false;
        let source_window_id = self.window_id;
        let bookmarks = self
            .bookmarks
            .iter()
            .map(|bookmark| serde_json::json!({"id": bookmark.id, "title": bookmark.title, "url": bookmark.url}))
            .collect::<Vec<_>>();
        let history = self
            .history
            .iter()
            .map(|entry| serde_json::json!({"id": entry.id, "title": entry.title, "url": entry.url, "visited_at_ms": entry.last_visited_at_ms, "visit_count": entry.visit_count}))
            .collect::<Vec<_>>();
        let panel = match self.panel {
            LibraryPanel::None => "none",
            LibraryPanel::Bookmarks => "bookmarks",
            LibraryPanel::History => "history",
            LibraryPanel::Downloads => "downloads",
            LibraryPanel::Settings => "settings",
            LibraryPanel::Passwords => "passwords",
        };
        let downloads = self
            .downloads
            .iter()
            .map(|item| {
                serde_json::json!({
                    "id": item.id, "filename": item.filename, "url": item.url, "path": item.path,
                    "total_bytes": item.total_bytes, "received_bytes": item.received_bytes,
                    "state": item.state, "error": item.error
                })
            })
            .collect::<Vec<_>>();
        let tabs = self
            .tabs
            .iter()
            .map(|tab| {
                serde_json::json!({
                    "id": tab.id,
                    "title": tab.title,
                    "url": tab.url,
                    "loading": tab.loading,
                    "error": tab.error,
                    "can_go_back": tab.can_go_back,
                    "can_go_forward": tab.can_go_forward,
                    "last_active": tab.last_active,
                    "scroll_x": tab.scroll_x,
                    "scroll_y": tab.scroll_y,
                    "restored": tab.restored
                })
            })
            .collect::<Vec<_>>();
        let settings = serde_json::json!({
            "theme": self.settings.theme,
            "ui_density": self.settings.ui_density,
            "ui_font": self.settings.ui_font,
            "ui_font_size": self.settings.ui_font_size,
            "tab_width": self.settings.tab_width,
            "restore_session": self.settings.restore_session,
            "save_session_on_exit": self.settings.save_session_on_exit,
            "home_page": self.settings.home_page,
            "new_tab_url": self.settings.new_tab_url,
            "engine": self.settings.engine,
            "address_behavior": self.settings.address_behavior,
            "download_path": self.settings.download_path,
            "download_panel_auto_open": self.settings.download_panel_auto_open,
            "save_history": self.settings.save_history
        });
        let settings_query = self.settings_search.read(cx).content();
        let password_entries = self
            .password_ui
            .vault
            .entries()
            .iter()
            .map(|entry| {
                serde_json::json!({
                    "id": entry.id,
                    "origin": entry.origin,
                    "username": entry.username,
                    "title": entry.title,
                    "revision": entry.revision
                })
            })
            .collect::<Vec<_>>();
        let password_query = self.password_ui.search.read(cx).content();
        let password_form_origin = self.password_ui.origin.read(cx).content();
        let password_form_username = self.password_ui.username.read(cx).content();
        let password_form_length = self.password_ui.password.read(cx).content().len();
        let password_current_origin = self
            .active_tab()
            .and_then(|tab| password_vault::normalize_origin(&tab.url).ok());
        let password_site_matches = password_current_origin
            .as_deref()
            .map(|origin| self.password_ui.vault.entries_for_origin(origin).len())
            .unwrap_or(0);
        let pending_login = self.password_ui.pending.as_ref().map(|pending| {
            serde_json::json!({
                "origin": pending.origin,
                "username": pending.username,
                "source": pending.source
            })
        });
        #[cfg(target_os = "windows")]
        let window_handle = self.parent.map(|handle| handle.0 as usize).unwrap_or(0);
        #[cfg(not(target_os = "windows"))]
        let window_handle = 0usize;
        let (lock_epoch, globally_locked) = password_vault::lock_state();
        let payload = serde_json::json!({
            "address": address,
            "marked_range": marked,
            "selected_range": [selected.start, selected.end],
            "source": source,
            "title": title,
            "window_id": self.window_id,
            "window_handle": window_handle,
            "active_tab": active.map(|tab| tab.id),
            "panel": panel,
            "history_query": self.history_query,
            "bookmarks": bookmarks,
            "history": history,
            "downloads": downloads,
            "tabs": tabs,
            "settings": settings,
            "settings_category": self.settings_category.key(),
            "settings_query": settings_query,
            "settings_error": self.settings_error,
            "settings_generation": self.settings_generation,
            "site_data_cleared": self.site_data_cleared,
            "confirm_clear_site_data": self.confirm_clear_site_data,
            "diagnostics": self.diagnostics,
            "layout_bounds": self.layout_bounds,
            "pending_input_id": self.pending_input_id,
            "capture_count": self.capture_count,
            "last_capture_path": self.last_capture_path,
            "last_capture_error": self.last_capture_error,
            "last_capture_width": self.last_capture_size.map(|size| size.0),
            "last_capture_height": self.last_capture_size.map(|size| size.1),
            "password_vault": {
                "unlocked": self.password_ui.vault.is_unlocked(),
                "entries": password_entries,
                "query": password_query,
                "form_open": self.password_ui.form_open,
                "editing": self.password_ui.editing,
                "reveal": self.password_ui.reveal,
                "confirm_delete": self.password_ui.confirm_delete,
                "pending": pending_login,
                "current_origin": password_current_origin,
                "site_matches": password_site_matches,
                "form_origin": password_form_origin,
                "form_username": password_form_username,
                "form_password_len": password_form_length,
                "error": self.password_ui.error,
                "conflict": self.password_ui.conflict,
                "tombstones": self.password_ui.vault.tombstone_count(),
                "lock_epoch": lock_epoch,
                "globally_locked": globally_locked
            },
            "session_summary": self.session_summary.as_ref().map(|summary| serde_json::json!({
                "windows": summary.windows,
                "tabs": summary.tabs,
                "saved_at_ms": summary.saved_at_ms
            })),
            "session_error": self.session_error,
            "restore_progress": self.restore_progress.as_ref().map(|progress| serde_json::json!({
                "total": progress.total,
                "finished": progress.finished,
                "failed": progress.failed
            })),
            "ime_context": ime_context,
            "pid": std::process::id()
        });
        if let Some(parent) = std::path::Path::new(&path).parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        let _ = std::fs::write(
            path,
            serde_json::to_vec_pretty(&payload).unwrap_or_default(),
        );
    }

    fn active_tab(&self) -> Option<&TabState> {
        self.tabs.get(self.active)
    }

    fn active_tab_mut(&mut self) -> Option<&mut TabState> {
        self.tabs.get_mut(self.active)
    }

    #[cfg(target_os = "windows")]
    fn active_webview(&self) -> Option<&webview::WebViewHost> {
        self.active_tab().and_then(|tab| tab.webview.as_ref())
    }

    #[cfg(target_os = "windows")]
    fn active_webview_mut(&mut self) -> Option<&mut webview::WebViewHost> {
        self.tabs
            .get_mut(self.active)
            .and_then(|tab| tab.webview.as_mut())
    }

    fn set_address(&mut self, value: impl Into<SharedString>, cx: &mut Context<Self>) {
        let value = value.into();
        self.address_input
            .update(cx, |input, cx| input.set_content(value, cx));
    }

    fn address_value(&self, cx: &App) -> String {
        self.address_input.read(cx).content()
    }

    fn focus_address_input(&self, window: &mut Window, cx: &mut App) {
        let handle = self.address_input.read(cx).focus_handle(cx);
        window.focus(&handle);
    }

    fn refresh_bookmarks(&mut self) {
        match library::bookmarks() {
            Ok(bookmarks) => {
                self.bookmarks = bookmarks;
                self.library_error = None;
            }
            Err(error) => self.library_error = Some(error),
        }
    }

    fn refresh_history(&mut self) {
        match library::history(&self.history_query) {
            Ok(history) => {
                self.history = history;
                self.library_error = None;
            }
            Err(error) => self.library_error = Some(error),
        }
    }

    fn open_library_panel(
        &mut self,
        panel: LibraryPanel,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.panel = if self.panel == panel {
            LibraryPanel::None
        } else {
            panel
        };
        self.confirm_clear_history = false;
        if self.panel == LibraryPanel::Bookmarks {
            self.refresh_bookmarks();
        } else if self.panel == LibraryPanel::History {
            self.search_input
                .update(cx, |input, cx| input.set_content("", cx));
            self.history_query.clear();
            self.refresh_history();
            let handle = self.search_input.read(cx).focus_handle(cx);
            window.focus(&handle);
        } else if self.panel == LibraryPanel::Downloads {
            self.downloads = library::downloads().unwrap_or_default();
        }
        cx.notify();
    }

    fn focus_native_window(&self) {
        #[cfg(target_os = "windows")]
        if let Some(parent) = self.parent {
            unsafe {
                let _ = SetFocus(Some(parent));
            }
        }
    }

    fn open_passwords(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.panel = if self.panel == LibraryPanel::Passwords {
            LibraryPanel::None
        } else {
            LibraryPanel::Passwords
        };
        self.password_ui.confirm_delete = None;
        self.password_ui.error = None;
        self.focus_native_window();
        if self.password_ui.vault.is_unlocked() {
            let handle = self.password_ui.search.read(cx).focus_handle(cx);
            window.focus(&handle);
        }
        cx.notify();
    }

    fn unlock_password_vault(&mut self, cx: &mut Context<Self>) {
        match self.password_ui.vault.unlock() {
            Ok(()) => {
                password_vault::clear_global_lock();
                self.password_ui.lock_epoch = password_vault::lock_state().0;
                self.password_ui.vault_generation = password_vault::generation();
                self.password_ui.error = None;
                self.password_ui.conflict = None;
                self.password_ui.recovery_path = None;
                self.password_ui.info = Some("보안 저장소 잠금을 해제했습니다.".into());
            }
            Err(error) => {
                self.password_ui.recovery_path = None;
                self.password_ui.error = Some(format!(
                    "{error} 원본 vault는 삭제하거나 덮어쓰지 않았습니다. 다시 시도하거나 vault 백업 후 복구를 진행하세요."
                ));
            }
        }
        cx.notify();
    }

    fn lock_password_vault(&mut self, cx: &mut Context<Self>) {
        self.password_ui.lock_epoch = password_vault::request_global_lock();
        self.password_ui.vault.lock();
        self.password_ui.pending = None;
        self.password_ui.form_open = false;
        self.password_ui.editing = None;
        self.password_ui.reveal = None;
        self.password_ui.confirm_delete = None;
        self.password_ui.error = None;
        self.password_ui.conflict = None;
        self.password_ui.info = Some("모든 창의 보안 저장소를 잠갔습니다.".into());
        cx.notify();
    }

    fn sync_vault_lock(&mut self, cx: &mut Context<Self>) {
        let (epoch, locked) = password_vault::lock_state();
        if epoch == self.password_ui.lock_epoch {
            return;
        }
        self.password_ui.lock_epoch = epoch;
        if locked {
            self.password_ui.vault.lock();
            self.password_ui.pending = None;
            self.password_ui.form_open = false;
            self.password_ui.editing = None;
            self.password_ui.reveal = None;
            self.password_ui.confirm_delete = None;
            self.password_ui.error = None;
            self.password_ui.conflict = None;
            self.password_ui.info = Some("다른 창에서 보안 저장소를 잠갔습니다.".into());
            cx.notify();
        }
    }

    fn clear_site_data(&mut self, cx: &mut Context<Self>) {
        if !self.confirm_clear_site_data {
            self.confirm_clear_site_data = true;
            self.site_data_cleared = false;
            cx.notify();
            return;
        }
        #[cfg(target_os = "windows")]
        {
            let result = self
                .active_webview()
                .ok_or_else(|| "WebView2 controller unavailable".to_string())
                .and_then(|webview| webview.clear_site_data());
            match result {
                Ok(()) => {
                    self.settings_error = None;
                    self.site_data_cleared = true;
                    self.password_ui.info = Some(
                        "모든 사이트의 쿠키/사이트 데이터를 삭제했습니다. 열린 로그인 세션이 종료될 수 있습니다."
                            .into(),
                    );
                }
                Err(error) => self.settings_error = Some(error),
            }
        }
        #[cfg(not(target_os = "windows"))]
        {
            self.settings_error =
                Some("이 플랫폼에서는 WebView2 사이트 데이터 삭제를 지원하지 않습니다.".into());
        }
        self.confirm_clear_site_data = false;
        cx.notify();
    }

    fn backup_password_vault(&mut self, cx: &mut Context<Self>) {
        match self.password_ui.vault.backup() {
            Ok(path) => {
                self.password_ui.recovery_path = Some(path.display().to_string());
                self.password_ui.info = Some("원본 vault를 recovery 폴더에 백업했습니다.".into());
                self.password_ui.error = None;
            }
            Err(error) => self.password_ui.error = Some(error),
        }
        cx.notify();
    }

    fn sync_password_vault(&mut self, cx: &mut Context<Self>) {
        if self.panel != LibraryPanel::Passwords
            || !self.password_ui.vault.is_unlocked()
            || self.password_ui.form_open
            || self.password_ui.pending.is_some()
        {
            return;
        }
        let generation = password_vault::generation();
        if generation == self.password_ui.vault_generation {
            return;
        }
        match self.password_ui.vault.unlock() {
            Ok(()) => {
                self.password_ui.vault_generation = generation;
                self.password_ui.info = Some("다른 창의 보안 저장소 변경을 반영했습니다.".into());
                self.password_ui.error = None;
                self.password_ui.conflict = None;
            }
            Err(error) => {
                self.password_ui.error = Some(error);
            }
        }
        cx.notify();
    }

    fn begin_password_add(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.password_ui.form_open = true;
        self.password_ui.conflict = None;
        self.focus_native_window();
        self.password_ui.editing = None;
        self.password_ui.confirm_delete = None;
        let origin = self
            .active_tab()
            .and_then(|tab| password_vault::normalize_origin(&tab.url).ok())
            .unwrap_or_default();
        self.password_ui
            .origin
            .update(cx, |input, cx| input.set_content(origin, cx));
        self.password_ui
            .username
            .update(cx, |input, cx| input.set_content("", cx));
        self.password_ui
            .password
            .update(cx, |input, cx| input.set_content("", cx));
        self.password_ui.password.update(cx, |input, cx| {
            input.set_masked(self.password_ui.password_masked, cx)
        });
        let handle = self.password_ui.origin.read(cx).focus_handle(cx);
        window.focus(&handle);
        cx.notify();
    }

    fn begin_password_edit(&mut self, id: u64, cx: &mut Context<Self>) {
        let Some(entry) = self
            .password_ui
            .vault
            .entries()
            .iter()
            .find(|entry| entry.id == id)
            .cloned()
        else {
            return;
        };
        self.password_ui.form_open = true;
        self.password_ui.editing = Some(id);
        self.password_ui.conflict = None;
        self.password_ui.confirm_delete = None;
        self.password_ui
            .origin
            .update(cx, |input, cx| input.set_content(entry.origin.clone(), cx));
        self.password_ui.username.update(cx, |input, cx| {
            input.set_content(entry.username.clone(), cx)
        });
        self.password_ui.password.update(cx, |input, cx| {
            input.set_content(entry.password.clone(), cx)
        });
        cx.notify();
    }

    fn save_password_form(&mut self, cx: &mut Context<Self>) {
        if !self.password_ui.vault.is_unlocked() {
            self.password_ui.error = Some("보안 저장소가 잠겨 있습니다.".into());
            cx.notify();
            return;
        }
        let origin = self.password_ui.origin.read(cx).content();
        let username = self.password_ui.username.read(cx).content();
        let mut password = self.password_ui.password.read(cx).content();
        let title = self
            .active_tab()
            .map(|tab| tab.title.to_string())
            .unwrap_or_default();
        let result = self.password_ui.vault.upsert(
            self.password_ui.editing,
            &origin,
            &username,
            &password,
            &title,
        );
        password.zeroize();
        match result {
            Ok(_) => match self.password_ui.vault.save() {
                Ok(()) => {
                    self.password_ui.vault_generation = password_vault::generation();
                    self.password_ui.form_open = false;
                    self.password_ui.editing = None;
                    self.password_ui.error = None;
                    self.password_ui.conflict = None;
                    self.password_ui.info = Some("로그인 정보를 저장했습니다.".into());
                    self.password_ui
                        .password
                        .update(cx, |input, cx| input.set_content("", cx));
                }
                Err(error) => self.handle_password_conflict(error, cx),
            },
            Err(error) => self.handle_password_conflict(error, cx),
        }
        cx.notify();
    }

    fn handle_password_conflict(&mut self, error: String, cx: &mut Context<Self>) {
        if !error.starts_with("충돌: ") {
            self.password_ui.error = Some(error);
            return;
        }
        if self.password_ui.vault.unlock().is_ok() {
            self.password_ui.vault_generation = password_vault::generation();
        }
        self.password_ui.form_open = false;
        self.password_ui.editing = None;
        self.password_ui.reveal = None;
        self.password_ui.confirm_delete = None;
        self.password_ui.pending = None;
        self.password_ui.password.update(cx, |input, cx| {
            input.set_content("", cx);
        });
        self.password_ui.error = Some(format!("{error} 최신 상태를 다시 불러왔습니다."));
        self.password_ui.conflict = Some(error);
        cx.notify();
    }

    fn delete_password_entry(&mut self, id: u64, cx: &mut Context<Self>) {
        if self.password_ui.confirm_delete != Some(id) {
            self.password_ui.confirm_delete = Some(id);
            cx.notify();
            return;
        }
        if !self.password_ui.vault.is_unlocked() {
            self.password_ui.error = Some("보안 저장소가 잠겨 있습니다.".into());
            cx.notify();
            return;
        }
        if self.password_ui.vault.delete(id) {
            match self.password_ui.vault.save() {
                Ok(()) => {
                    self.password_ui.vault_generation = password_vault::generation();
                    self.password_ui.info = Some("로그인 정보를 삭제했습니다.".into());
                    self.password_ui.error = None;
                    self.password_ui.conflict = None;
                }
                Err(error) => self.handle_password_conflict(error, cx),
            }
        }
        self.password_ui.confirm_delete = None;
        self.password_ui.reveal = None;
        cx.notify();
    }

    fn fill_password_entry(&mut self, id: u64, cx: &mut Context<Self>) {
        if !self.settings.password_autofill_enabled {
            self.password_ui.error = Some("설정에서 비밀번호 자동 완성이 꺼져 있습니다.".into());
            cx.notify();
            return;
        }
        #[cfg(target_os = "windows")]
        {
            let Some(tab_url) = self.active_tab().map(|tab| tab.url.to_string()) else {
                return;
            };
            let Some(entry) = self
                .password_ui
                .vault
                .entries()
                .iter()
                .find(|entry| entry.id == id)
                .cloned()
            else {
                return;
            };
            if !password_vault::same_origin(&tab_url, &entry.origin) {
                self.password_ui.error = Some("현재 사이트 origin과 일치하지 않습니다.".into());
                cx.notify();
                return;
            }
            let filled = self
                .active_webview()
                .map(|webview| {
                    let navigation_id = webview.navigation_id();
                    webview.fill_login(
                        &entry.origin,
                        &entry.username,
                        &entry.password,
                        navigation_id,
                    )
                })
                .unwrap_or(false);
            if filled {
                self.password_ui.info = Some(
                    "현재 사이트의 로그인 폼에 입력했습니다. 자동 제출은 하지 않았습니다.".into(),
                );
                self.password_ui.error = None;
            } else {
                self.password_ui.error =
                    Some("로그인 폼을 찾지 못했거나 origin 검증에 실패했습니다.".into());
            }
        }
        #[cfg(not(target_os = "windows"))]
        {
            let _ = id;
            self.password_ui.error = Some("이 플랫폼에서는 자동 완성을 지원하지 않습니다.".into());
        }
        cx.notify();
    }

    fn save_pending_login(&mut self, cx: &mut Context<Self>) {
        let Some(pending) = self.password_ui.pending.as_ref() else {
            return;
        };
        if !self.password_ui.vault.is_unlocked() {
            self.password_ui.error = Some("저장하려면 보안 저장소 잠금을 해제해야 합니다.".into());
            cx.notify();
            return;
        }
        let origin = pending.origin.clone();
        let username = pending.username.clone();
        let mut password = pending.password.clone();
        let title = self
            .active_tab()
            .map(|tab| tab.title.to_string())
            .unwrap_or_default();
        let result = self
            .password_ui
            .vault
            .upsert(None, &origin, &username, &password, &title);
        password.zeroize();
        match result {
            Ok(_) => match self.password_ui.vault.save() {
                Ok(()) => {
                    self.password_ui.vault_generation = password_vault::generation();
                    self.password_ui.pending = None;
                    self.password_ui.info = Some("로그인 정보를 저장했습니다.".into());
                    self.password_ui.error = None;
                    self.password_ui.conflict = None;
                }
                Err(error) => self.handle_password_conflict(error, cx),
            },
            Err(error) => self.handle_password_conflict(error, cx),
        }
        cx.notify();
    }

    fn skip_pending_login(&mut self, cx: &mut Context<Self>) {
        self.password_ui.pending = None;
        self.password_ui.info = Some("로그인 정보 저장을 건너뛰었습니다.".into());
        cx.notify();
    }

    fn poll_login_captures(&mut self, cx: &mut Context<Self>) {
        #[cfg(target_os = "windows")]
        {
            let mut captures = Vec::new();
            if !self.settings.password_save_prompt_enabled {
                for tab in &self.tabs {
                    if let Some(webview) = &tab.webview {
                        let _ = webview.drain_login_captures();
                    }
                }
                return;
            }
            for tab in &self.tabs {
                if let Some(webview) = &tab.webview {
                    captures.extend(webview.drain_login_captures());
                }
            }
            for capture in captures {
                let Some(origin) = password_vault::normalize_origin(&capture.source).ok() else {
                    continue;
                };
                let Some(tab) = self
                    .tabs
                    .iter()
                    .find(|tab| password_vault::same_origin(&tab.url, &origin))
                else {
                    continue;
                };
                let same_page = password_vault::same_origin(&tab.url, &capture.source);
                if !same_page {
                    continue;
                }
                let pending = PendingLogin {
                    source: capture.source.clone(),
                    origin,
                    username: capture.username.clone(),
                    password: capture.password.clone(),
                };
                if let Some(old) = self.password_ui.pending.replace(pending) {
                    drop(old);
                }
                self.panel = LibraryPanel::Passwords;
                self.password_ui.error = None;
                self.password_ui.info =
                    Some("로그인 제출을 감지했습니다. 저장 여부를 확인해 주세요.".into());
                cx.notify();
                break;
            }
        }
        #[cfg(not(target_os = "windows"))]
        let _ = cx;
    }

    fn open_settings(&mut self, cx: &mut Context<Self>) {
        self.panel = if self.panel == LibraryPanel::Settings {
            LibraryPanel::None
        } else {
            LibraryPanel::Settings
        };
        self.settings_category = SettingsCategory::Appearance;
        self.settings_confirm_reset = false;
        self.settings_summary_refresh();
        self.settings_search
            .update(cx, |input, cx| input.set_content("", cx));
        cx.notify();
    }

    fn settings_summary_refresh(&mut self) {
        self.session_summary = library::session_summary().ok().flatten();
    }

    fn start_new_session(&mut self, cx: &mut Context<Self>) {
        match library::clear_session() {
            Ok(()) => {
                self.session_summary = None;
                self.session_error = None;
                self.update_setting("restore_session", SettingValue::Boolean(false), cx);
            }
            Err(error) => self.settings_error = Some(error),
        }
        cx.notify();
    }

    fn update_setting(&mut self, key: &'static str, value: SettingValue, cx: &mut Context<Self>) {
        match library::set_browser_setting(key, value) {
            Ok(settings) => {
                self.settings = settings;
                if let Ok((generation, _)) = library::browser_settings_cached() {
                    self.settings_generation = generation;
                }
                apply_platform_settings(&self.settings);
                self.settings_error = None;
                self.settings_last_text_apply = Instant::now();
            }
            Err(error) => self.settings_error = Some(error),
        }
        cx.notify();
    }

    fn reset_settings(&mut self, cx: &mut Context<Self>) {
        if !self.settings_confirm_reset {
            self.settings_confirm_reset = true;
            cx.notify();
            return;
        }
        match library::reset_browser_settings() {
            Ok(settings) => {
                self.settings = settings;
                if let Ok((generation, _)) = library::browser_settings_cached() {
                    self.settings_generation = generation;
                }
                self.refresh_settings_inputs(cx);
                apply_platform_settings(&self.settings);
                self.settings_confirm_reset = false;
                self.settings_error = None;
            }
            Err(error) => self.settings_error = Some(error),
        }
        cx.notify();
    }

    fn refresh_settings_inputs(&mut self, cx: &mut Context<Self>) {
        let settings = self.settings.clone();
        self.settings_home_page.update(cx, |input, cx| {
            if input.content() != settings.home_page {
                input.set_content(settings.home_page.clone(), cx);
            }
        });
        self.settings_new_tab.update(cx, |input, cx| {
            if input.content() != settings.new_tab_url {
                input.set_content(settings.new_tab_url.clone(), cx);
            }
        });
        self.settings_download_path.update(cx, |input, cx| {
            if input.content() != settings.download_path {
                input.set_content(settings.download_path.clone(), cx);
            }
        });
        self.settings_font.update(cx, |input, cx| {
            if input.content() != settings.ui_font {
                input.set_content(settings.ui_font.clone(), cx);
            }
        });
    }

    fn sync_settings(&mut self, cx: &mut Context<Self>) {
        if let Ok((generation, settings)) = library::browser_settings_cached()
            && generation != self.settings_generation
        {
            self.settings = settings;
            self.settings_generation = generation;
            self.refresh_settings_inputs(cx);
            apply_platform_settings(&self.settings);
        }
    }

    fn sync_settings_text(&mut self, force: bool, cx: &mut Context<Self>) {
        if self.panel != LibraryPanel::Settings
            || (!force && self.settings_last_text_apply.elapsed() < Duration::from_millis(350))
        {
            return;
        }
        self.settings_last_text_apply = Instant::now();
        let home_page = self
            .settings_home_page
            .read(cx)
            .content()
            .trim()
            .to_string();
        let new_tab_url = self.settings_new_tab.read(cx).content().trim().to_string();
        let download_path = self
            .settings_download_path
            .read(cx)
            .content()
            .trim()
            .to_string();
        let font = self.settings_font.read(cx).content().trim().to_string();
        if home_page != self.settings.home_page {
            self.update_setting(
                "home_page",
                SettingValue::Text(if home_page.is_empty() {
                    "about:blank".into()
                } else {
                    home_page
                }),
                cx,
            );
        }
        if new_tab_url != self.settings.new_tab_url {
            self.update_setting(
                "new_tab_url",
                SettingValue::Text(if new_tab_url.is_empty() {
                    "about:blank".into()
                } else {
                    new_tab_url
                }),
                cx,
            );
        }
        if font != self.settings.ui_font {
            self.update_setting(
                "ui_font",
                SettingValue::Text(if font.is_empty() {
                    "Segoe UI".into()
                } else {
                    font
                }),
                cx,
            );
        }
        if download_path != self.settings.download_path {
            if download_path.is_empty() {
                self.update_setting("download_path", SettingValue::Text(String::new()), cx);
            } else if std::path::Path::new(&download_path).is_absolute()
                && std::fs::create_dir_all(&download_path).is_ok()
            {
                self.update_setting("download_path", SettingValue::Text(download_path), cx);
            } else {
                self.settings_error =
                    Some("다운로드 경로는 존재하거나 생성 가능한 절대 경로여야 합니다.".into());
                cx.notify();
            }
        }
    }

    fn search_engine_url(&self, query: &str) -> String {
        match self.settings.engine.as_str() {
            "google" => format!("https://www.google.com/search?q={}", url_encode(query)),
            "bing" => format!("https://www.bing.com/search?q={}", url_encode(query)),
            _ => format!("https://duckduckgo.com/?q={}", url_encode(query)),
        }
    }

    fn toggle_active_bookmark(&mut self, cx: &mut Context<Self>) {
        let Some(tab) = self.active_tab() else { return };
        let title = tab.title.to_string();
        let url = tab.url.to_string();
        if url.is_empty() || url.starts_with("about:") {
            return;
        }
        match library::toggle_bookmark(&title, &url) {
            Ok((_added, bookmarks)) => {
                self.bookmarks = bookmarks;
                self.library_error = None;
            }
            Err(error) => self.library_error = Some(error),
        }
        cx.notify();
    }

    fn navigate_to(&mut self, url: String, cx: &mut Context<Self>) {
        self.set_address(url, cx);
        self.navigate(cx);
        self.panel = LibraryPanel::None;
        cx.notify();
    }

    fn delete_bookmark(&mut self, id: i64, cx: &mut Context<Self>) {
        match library::delete_bookmark(id) {
            Ok(bookmarks) => {
                self.bookmarks = bookmarks;
                self.library_error = None;
            }
            Err(error) => self.library_error = Some(error),
        }
        cx.notify();
    }

    fn delete_history(&mut self, url: String, cx: &mut Context<Self>) {
        match library::delete_history(&url) {
            Ok(history) => {
                self.history = history;
                self.library_error = None;
            }
            Err(error) => self.library_error = Some(error),
        }
        cx.notify();
    }

    fn clear_history(&mut self, cx: &mut Context<Self>) {
        if !self.confirm_clear_history {
            self.confirm_clear_history = true;
            cx.notify();
            return;
        }
        match library::clear_history() {
            Ok(_) => {
                self.history.clear();
                self.confirm_clear_history = false;
                self.library_error = None;
            }
            Err(error) => self.library_error = Some(error),
        }
        cx.notify();
    }

    fn toggle_bookmark_action(
        &mut self,
        _: &ToggleBookmark,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.toggle_active_bookmark(cx);
    }

    fn toggle_bookmarks_panel_action(
        &mut self,
        _: &ToggleBookmarksPanel,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.open_library_panel(LibraryPanel::Bookmarks, window, cx);
    }

    fn toggle_history_panel_action(
        &mut self,
        _: &ToggleHistoryPanel,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.open_library_panel(LibraryPanel::History, window, cx);
    }

    fn toggle_downloads_panel_action(
        &mut self,
        _: &ToggleDownloadsPanel,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.open_library_panel(LibraryPanel::Downloads, window, cx);
    }

    fn toggle_settings_action(
        &mut self,
        _: &ToggleSettings,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.open_settings(cx);
    }

    fn toggle_passwords_action(
        &mut self,
        _: &TogglePasswords,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.open_passwords(window, cx);
    }

    fn refresh_tabs(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        #[cfg(target_os = "windows")]
        let mut history_changed = false;
        #[cfg(not(target_os = "windows"))]
        let mut history_changed = false;
        let mut restore_finished = 0usize;
        let mut restore_failed = 0usize;
        for tab in &mut self.tabs {
            let was_loading = tab.loading;
            let Some(webview) = &tab.webview else {
                if tab.restored && tab.loading {
                    tab.loading = false;
                    tab.error = Some("WebView2 controller unavailable".into());
                    restore_finished += 1;
                    restore_failed += 1;
                }
                continue;
            };
            let source = webview.source();
            if !source.is_empty() {
                tab.url = source.clone().into();
                let title = webview.title();
                tab.title = if title.is_empty() {
                    title_for(&source).into()
                } else {
                    title.into()
                };
                let key = history_key(&source);
                if self.settings.save_history
                    && !key.is_empty()
                    && tab.history_url != key
                    && !tab.title.is_empty()
                {
                    let now = SystemTime::now()
                        .duration_since(UNIX_EPOCH)
                        .unwrap_or_default()
                        .as_millis() as i64;
                    if library::record_history(&tab.title, &source, now).is_ok() {
                        tab.history_url = key;
                        history_changed = true;
                    }
                }
            }
            tab.can_go_back = webview.can_go_back();
            tab.can_go_forward = webview.can_go_forward();
            let (loading, error) = webview.signals();
            tab.loading = loading;
            tab.error = error.map(Into::into);
            if tab.restored && was_loading && !tab.loading {
                restore_finished += 1;
                if tab.error.is_some() {
                    restore_failed += 1;
                }
            }
            #[cfg(target_os = "windows")]
            if tab.restored && !tab.loading {
                if let Some((x, y)) = tab.pending_scroll.take() {
                    webview.scroll_to(x, y);
                    tab.scroll_x = x;
                    tab.scroll_y = y;
                } else if let Some((x, y)) = webview.scroll_position() {
                    tab.scroll_x = x;
                    tab.scroll_y = y;
                }
            }
        }
        if restore_finished > 0 {
            if let Some(progress) = &mut self.restore_progress {
                progress.finished = (progress.finished + restore_finished).min(progress.total);
                progress.failed += restore_failed;
                progress.visible_until = Instant::now() + Duration::from_secs(6);
            }
        }
        if history_changed {
            self.history = library::history(&self.history_query).unwrap_or_default();
        }
        if self.panel == LibraryPanel::History {
            let query = self.search_input.read(cx).content();
            if query != self.history_query {
                self.history_query = query;
                match library::history(&self.history_query) {
                    Ok(history) => {
                        self.history = history;
                        self.library_error = None;
                    }
                    Err(error) => self.library_error = Some(error),
                }
            }
        }
        #[cfg(not(target_os = "windows"))]
        let _ = &self.tabs;

        let focused = self.address_input.read(cx).is_focused(window);
        if !focused {
            let source = self
                .active_tab()
                .map(|tab| tab.url.to_string())
                .unwrap_or_default();
            if !source.is_empty() && self.address_value(cx) != source {
                self.set_address(source, cx);
            }
        }
    }

    fn navigate(&mut self, cx: &mut Context<Self>) {
        let input = self.address_value(cx);
        let url = match normalize_url(&input, &self.settings) {
            Some(url) => url,
            None => self.search_engine_url(&input),
        };
        let tab = self.active_tab_mut();
        let Some(tab) = tab else { return };
        tab.url = url.clone().into();
        tab.title = title_for(&url).into();
        tab.loading = true;
        tab.error = None;
        self.set_address(url.clone(), cx);
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.tabs[self.active].webview {
            webview.navigate(&url);
            webview.focus();
        }
    }

    #[cfg(target_os = "windows")]
    fn attach_initial_webview(&mut self, url: String, window: &mut Window, cx: &mut Context<Self>) {
        let (webview, error) = self.create_webview();
        let has_error = error.is_some();
        if let Some(tab) = self.tabs.first_mut() {
            tab.webview = webview;
            tab.error = error.map(Into::into);
            tab.loading = !has_error;
        }
        if let Some(webview) = self.active_webview() {
            webview.navigate(&url);
            webview.set_active(true);
            self.sync_webview_bounds(window);
            webview.focus();
        }
        self.trace_event(format!(
            "attach_initial_webview url={url} error={has_error}"
        ));
        cx.notify();
    }

    fn create_webview(&self) -> (Option<webview::WebViewHost>, Option<String>) {
        #[cfg(target_os = "windows")]
        {
            match (self.webview_env.as_ref(), self.parent) {
                (Some(env), Some(parent)) => match webview::WebViewHost::create(env, parent) {
                    Ok(webview) => (Some(webview), None),
                    Err(error) => (None, Some(error)),
                },
                _ => (None, self.webview_error.clone()),
            }
        }
        #[cfg(not(target_os = "windows"))]
        (None, None)
    }

    fn new_tab(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let url = std::env::var("AMARE_GPUI_NEW_TAB_URL")
            .unwrap_or_else(|_| self.settings.new_tab_url.clone());
        self.new_tab_with_url(url, window, cx, true);
    }

    fn new_tab_with_url(
        &mut self,
        url: String,
        window: &mut Window,
        cx: &mut Context<Self>,
        focus_address: bool,
    ) {
        let id = self.next_tab_id;
        self.next_tab_id += 1;
        self.activity_counter += 1;
        self.tabs.push(TabState {
            id,
            title: "New Tab".into(),
            url: url.clone().into(),
            loading: true,
            error: None,
            can_go_back: false,
            can_go_forward: false,
            favicon: None,
            last_active: self.activity_counter,
            history_url: String::new(),
            scroll_x: 0,
            scroll_y: 0,
            restored: false,
            #[cfg(target_os = "windows")]
            pending_scroll: None,
            #[cfg(target_os = "windows")]
            webview: None,
        });
        self.active = self.tabs.len() - 1;
        self.trace_event(format!(
            "new_tab id={id} tabs={} active={} url={url}",
            self.tabs.len(),
            self.active
        ));
        self.set_address(url.clone(), cx);
        // Publish the new tab count before the deferred controller creation so a
        // slow WebView2 attach cannot hide the tab from session/status readers.
        self.write_status(cx);
        // WebView2 controller creation can take long enough to block the GPUI
        // event loop when several tabs are opened back-to-back. Register the
        // tab immediately and attach its controller after the current effect
        // cycle so Ctrl+T/button clicks always update the tab strip first.
        cx.defer_in(window, move |this, window, cx| {
            let (webview, error) = this.create_webview();
            let Some(index) = this.tabs.iter().position(|tab| tab.id == id) else {
                return;
            };
            this.tabs[index].error = error.map(Into::into);
            #[cfg(target_os = "windows")]
            {
                this.tabs[index].webview = webview;
                if let Some(view) = &this.tabs[index].webview {
                    view.navigate(&url);
                    view.set_active(this.active_tab().map(|tab| tab.id) == Some(id));
                    if !focus_address {
                        view.focus();
                    }
                }
            }
            #[cfg(not(target_os = "windows"))]
            {
                let _ = webview;
                this.tabs[index].error = error.map(Into::into);
            }
            if focus_address && this.active_tab().map(|tab| tab.id) == Some(id) {
                this.focus_address_input(window, cx);
            }
            cx.notify();
        });
        cx.notify();
    }

    fn process_popup_requests(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        #[cfg(target_os = "windows")]
        {
            let mut urls = Vec::new();
            for tab in &self.tabs {
                if let Some(webview) = &tab.webview {
                    urls.extend(webview.drain_popup_requests());
                }
            }
            for url in urls {
                self.new_tab_with_url(url, window, cx, false);
            }
        }
        #[cfg(not(target_os = "windows"))]
        let _ = (window, cx);
    }

    fn open_new_window(&mut self, cx: &mut Context<Self>) {
        let url = std::env::var("AMARE_GPUI_NEW_WINDOW_URL")
            .unwrap_or_else(|_| self.settings.home_page.clone());
        let url_for_log = url.clone();
        let (width, height) = automation_window_size();
        let bounds = Bounds::centered(None, size(px(width), px(height)), cx);
        let result = cx.open_window(
            WindowOptions {
                window_bounds: Some(WindowBounds::Windowed(bounds)),
                titlebar: Some(gpui::TitlebarOptions {
                    title: Some("AMARE".into()),
                    appears_transparent: true,
                    ..Default::default()
                }),
                ..Default::default()
            },
            move |window, cx| {
                // Do not create the first WebView2 inside the window-creation
                // callback. Controller creation pumps Win32 messages and blocked
                // the second window before it could render or write its status.
                cx.new(|cx| AmareApp::new_with_url(window, cx, url, false, true))
            },
        );
        self.trace_event(format!(
            "open_new_window requested_url={url_for_log} result={}",
            match &result {
                Ok(_) => "ok".to_string(),
                Err(error) => format!("err:{error:?}"),
            }
        ));
        if let Ok(handle) = result {
            let url = url_for_log.clone();
            // Let the new window draw and write its status before WebView2
            // controller creation, which pumps Win32 messages and can block.
            cx.spawn(async move |_this, cx| {
                Timer::after(Duration::from_millis(400)).await;
                let _ = handle.update(cx, |app, window, cx| {
                    window.activate_window();
                    app.attach_initial_webview(url, window, cx);
                });
            })
            .detach();
        }
        cx.activate(true);
    }

    fn select_tab(&mut self, index: usize, window: &mut Window, cx: &mut Context<Self>) {
        if index >= self.tabs.len() || index == self.active {
            return;
        }
        #[cfg(target_os = "windows")]
        if let Some(webview) = self.active_webview() {
            webview.set_active(false);
        }
        self.active = index;
        self.activity_counter += 1;
        let activity = self.activity_counter;
        let url = self
            .active_tab()
            .map(|tab| tab.url.to_string())
            .unwrap_or_default();
        if let Some(tab) = self.active_tab_mut() {
            tab.last_active = activity;
        }
        self.set_address(url, cx);
        #[cfg(target_os = "windows")]
        if let Some(webview) = self.active_webview() {
            webview.set_active(true);
            webview.focus();
        }
        let _ = window;
        cx.notify();
    }

    fn select_tab_by_id(&mut self, id: u64, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(index) = self.tabs.iter().position(|tab| tab.id == id) {
            self.select_tab(index, window, cx);
        }
    }

    fn close_tab(&mut self, id: u64, window: &mut Window, cx: &mut Context<Self>) {
        let Some(index) = self.tabs.iter().position(|tab| tab.id == id) else {
            return;
        };
        if self.tabs.len() == 1 {
            // Closing the last tab must always leave one usable tab with a live
            // WebView2 controller. Quitting here left the user on a dead tab
            // whose controller had already been deactivated.
            #[cfg(target_os = "windows")]
            if let Some(webview) = &self.tabs[index].webview {
                webview.set_active(false);
            }
            let old = self.tabs.remove(0);
            drop(old);
            self.new_tab(window, cx);
            return;
        }
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.tabs[index].webview {
            webview.set_active(false);
        }
        self.tabs.remove(index);
        if self.active >= self.tabs.len() {
            self.active = self.tabs.len() - 1;
        } else if index < self.active {
            self.active -= 1;
        }
        self.activity_counter += 1;
        let activity = self.activity_counter;
        let url = self
            .active_tab()
            .map(|tab| tab.url.to_string())
            .unwrap_or_default();
        if let Some(tab) = self.active_tab_mut() {
            tab.last_active = activity;
        }
        self.set_address(url, cx);
        #[cfg(target_os = "windows")]
        if let Some(webview) = self.active_webview() {
            webview.set_active(true);
            webview.set_bounds(0, 0, 1, 1);
            webview.focus();
        }
        cx.notify();
    }

    fn close_active_tab(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(tab) = self.active_tab() {
            let id = tab.id;
            self.close_tab(id, window, cx);
        }
    }

    fn focus_address(&mut self, _: &FocusAddress, window: &mut Window, cx: &mut Context<Self>) {
        self.address_input
            .update(cx, |input, cx| input.select_all_text(cx));
        self.focus_address_input(window, cx);
        cx.notify();
    }

    fn submit_address(&mut self, _: &SubmitAddress, _window: &mut Window, cx: &mut Context<Self>) {
        if self.panel == LibraryPanel::Settings {
            self.sync_settings_text(true, cx);
            cx.notify();
            return;
        }
        self.navigate(cx);
        cx.notify();
    }

    fn cancel_address(&mut self, _: &CancelAddress, _window: &mut Window, cx: &mut Context<Self>) {
        let url = self
            .active_tab()
            .map(|tab| tab.url.to_string())
            .unwrap_or_default();
        self.set_address(url, cx);
        #[cfg(target_os = "windows")]
        if let Some(webview) = self.active_webview() {
            webview.focus();
        }
        cx.notify();
    }

    fn reload_page(&mut self, _: &ReloadPage, _window: &mut Window, cx: &mut Context<Self>) {
        #[cfg(target_os = "windows")]
        if let Some(webview) = self.active_webview() {
            webview.reload();
            webview.focus();
        }
        cx.notify();
    }

    fn go_back(&mut self, _: &BackPage, _window: &mut Window, cx: &mut Context<Self>) {
        #[cfg(target_os = "windows")]
        if let Some(webview) = self.active_webview() {
            webview.back();
            webview.focus();
        }
        cx.notify();
    }

    fn go_forward(&mut self, _: &ForwardPage, _window: &mut Window, cx: &mut Context<Self>) {
        #[cfg(target_os = "windows")]
        if let Some(webview) = self.active_webview() {
            webview.forward();
            webview.focus();
        }
        cx.notify();
    }

    fn new_tab_action(&mut self, _: &NewTab, window: &mut Window, cx: &mut Context<Self>) {
        self.trace_action("NewTab");
        self.new_tab(window, cx);
    }

    fn close_tab_action(&mut self, _: &CloseTab, window: &mut Window, cx: &mut Context<Self>) {
        self.close_active_tab(window, cx);
    }

    fn new_window_action(&mut self, _: &NewWindow, _window: &mut Window, cx: &mut Context<Self>) {
        self.trace_action("NewWindow");
        self.open_new_window(cx);
    }

    fn maybe_auto_capture(&mut self, cx: &mut Context<Self>) {
        let Ok(needle) = std::env::var("AMARE_GPUI_AUTO_CAPTURE_URL") else {
            return;
        };
        #[cfg(target_os = "windows")]
        let source = self
            .active_webview()
            .map(|webview| webview.source())
            .unwrap_or_default();
        #[cfg(not(target_os = "windows"))]
        let source = String::new();
        if source.is_empty() || !source.contains(&needle) || source == self.last_auto_capture_source
        {
            return;
        }
        self.last_auto_capture_source = source;
        self.capture_active_page(cx);
    }

    fn capture_page_action(
        &mut self,
        _: &CapturePage,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.capture_active_page(cx);
    }

    fn capture_active_page(&mut self, cx: &mut Context<Self>) {
        let Some(tab_id) = self.active_tab().map(|tab| tab.id) else {
            self.last_capture_error = Some("활성 탭이 없습니다.".into());
            cx.notify();
            return;
        };
        #[cfg(target_os = "windows")]
        let result = self
            .active_webview()
            .ok_or_else(|| "WebView2 controller unavailable".to_string())
            .and_then(|webview| webview.capture_preview_png());
        #[cfg(not(target_os = "windows"))]
        let result: Result<Vec<u8>, String> =
            Err("이 플랫폼에서는 CapturePreview를 지원하지 않습니다.".into());
        match result {
            Ok(bytes) => {
                let directory = std::env::var("AMARE_GPUI_CAPTURE_DIR")
                    .map(std::path::PathBuf::from)
                    .unwrap_or_else(|_| std::env::temp_dir().join("amare-gpui-captures"));
                if let Err(error) = std::fs::create_dir_all(&directory) {
                    self.last_capture_error = Some(format!("캡처 폴더 생성 실패: {error}"));
                    cx.notify();
                    return;
                }
                let sequence = self.capture_count.saturating_add(1);
                let path = directory.join(format!(
                    "page-w{}-tab{}-{}.png",
                    self.window_id, tab_id, sequence
                ));
                match std::fs::write(&path, &bytes) {
                    Ok(()) => {
                        self.capture_count = sequence;
                        self.last_capture_path = Some(path.display().to_string());
                        self.last_capture_error = None;
                        self.last_capture_size = png_dimensions(&bytes);
                        self.password_ui.info = Some(format!(
                            "CapturePreview 저장: {} ({} bytes)",
                            path.display(),
                            bytes.len()
                        ));
                    }
                    Err(error) => {
                        self.last_capture_error = Some(format!("캡처 저장 실패: {error}"));
                    }
                }
            }
            Err(error) => {
                self.last_capture_error = Some(error);
            }
        }
        cx.notify();
    }

    fn next_tab_action(&mut self, _: &NextTab, window: &mut Window, cx: &mut Context<Self>) {
        if self.tabs.len() > 1 {
            let next = (self.active + 1) % self.tabs.len();
            self.select_tab(next, window, cx);
        }
    }

    fn previous_tab_action(
        &mut self,
        _: &PreviousTab,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.tabs.len() > 1 {
            let previous = if self.active == 0 {
                self.tabs.len() - 1
            } else {
                self.active - 1
            };
            self.select_tab(previous, window, cx);
        }
    }

    fn move_tab_before(&mut self, dragged_id: u64, target_id: u64, cx: &mut Context<Self>) {
        if dragged_id == target_id {
            return;
        }
        let Some(from) = self.tabs.iter().position(|tab| tab.id == dragged_id) else {
            return;
        };
        let Some(mut target) = self.tabs.iter().position(|tab| tab.id == target_id) else {
            return;
        };
        let active_id = self.active_tab().map(|tab| tab.id);
        let tab = self.tabs.remove(from);
        if from < target {
            target -= 1;
        }
        self.tabs.insert(target.min(self.tabs.len()), tab);
        if let Some(active_id) = active_id {
            if let Some(index) = self.tabs.iter().position(|tab| tab.id == active_id) {
                self.active = index;
            }
        }
        cx.notify();
    }

    fn poll_drop_request(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        #[cfg(target_os = "windows")]
        {
            let request = if let Ok(mut slot) = DROP_REQUEST.lock() {
                match slot.as_ref() {
                    Some(request) if request.source_window_id == self.window_id => slot.take(),
                    _ => None,
                }
            } else {
                None
            };
            let Some(request) = request else { return };
            let bounds = window.bounds();
            let inside = request.x as f32 >= f32::from(bounds.origin.x)
                && request.x as f32 <= f32::from(bounds.origin.x) + f32::from(bounds.size.width)
                && request.y as f32 >= f32::from(bounds.origin.y)
                && request.y as f32 <= f32::from(bounds.origin.y) + f32::from(bounds.size.height);
            if inside {
                return;
            }
            let Some(index) = self.tabs.iter().position(|tab| tab.id == request.tab_id) else {
                return;
            };
            let tab = self.tabs.remove(index);
            let active_id = self.active_tab().map(|tab| tab.id);
            if self.active >= self.tabs.len() {
                self.active = self.tabs.len().saturating_sub(1);
            }
            if let Some(active_id) = active_id {
                if let Some(index) = self.tabs.iter().position(|tab| tab.id == active_id) {
                    self.active = index;
                }
            }
            let seed = std::rc::Rc::new(std::cell::RefCell::new(Some(tab)));
            let slot = seed.clone();
            let (width, height) = automation_window_size();
            let options = WindowOptions {
                window_bounds: Some(WindowBounds::Windowed(Bounds::centered(
                    None,
                    size(px(width), px(height)),
                    cx,
                ))),
                titlebar: Some(gpui::TitlebarOptions {
                    title: Some("AMARE".into()),
                    appears_transparent: true,
                    ..Default::default()
                }),
                ..Default::default()
            };
            let result = cx.open_window(options, move |window, cx| {
                let seed = slot.borrow_mut().take().expect("detached tab seed");
                cx.new(|cx| AmareApp::new_with_seed(window, cx, seed))
            });
            if result.is_err() {
                if let Some(tab) = seed.borrow_mut().take() {
                    self.tabs.insert(index.min(self.tabs.len()), tab);
                }
            } else if self.tabs.is_empty() {
                self.new_tab(window, cx);
            } else {
                let url = self
                    .active_tab()
                    .map(|tab| tab.url.to_string())
                    .unwrap_or_default();
                self.set_address(url, cx);
            }
            cx.notify();
        }
        #[cfg(not(target_os = "windows"))]
        let _ = (window, cx);
    }

    fn setting_row(
        &self,
        title: &str,
        description: &str,
        control: impl IntoElement,
        dark: bool,
    ) -> gpui::Div {
        let muted = if dark { rgb(0xa3a8b8) } else { rgb(0x5d6474) };
        let border = if dark { rgb(0x303543) } else { rgb(0xdde1ea) };
        div()
            .flex()
            .justify_between()
            .items_center()
            .gap_4()
            .px_4()
            .py_3()
            .border_b_1()
            .border_color(border)
            .child(
                div()
                    .flex()
                    .flex_col()
                    .gap_1()
                    .flex_1()
                    .min_w(px(0.0))
                    .child(div().text_sm().child(title.to_string()))
                    .child(
                        div()
                            .text_xs()
                            .text_color(muted)
                            .child(description.to_string()),
                    ),
            )
            .child(control)
    }

    fn toggle_setting_row(
        &self,
        title: &str,
        description: &str,
        key: &'static str,
        value: bool,
        dark: bool,
        cx: &mut Context<Self>,
    ) -> gpui::Div {
        let accent = rgb(0x2f7df6);
        let muted = if dark { rgb(0x303543) } else { rgb(0xdde1ea) };
        let listener = cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
            this.update_setting(key, SettingValue::Boolean(!value), cx);
        });
        let control = div()
            .id(SharedString::from(format!("setting-toggle-{key}")))
            .w(px(40.0))
            .h(px(22.0))
            .rounded_full()
            .bg(if value { accent } else { muted })
            .cursor_pointer()
            .flex()
            .items_center()
            .px(px(3.0))
            .child(
                div()
                    .size(px(16.0))
                    .rounded_full()
                    .bg(rgb(0xffffff))
                    .when(value, |el| el.ml(px(18.0))),
            )
            .on_click(listener);
        self.setting_row(title, description, control, dark)
    }

    fn choice_setting_row(
        &self,
        title: &str,
        description: &str,
        key: &'static str,
        current: &str,
        choices: &'static [(&'static str, &'static str)],
        dark: bool,
        cx: &mut Context<Self>,
    ) -> gpui::Div {
        let accent = rgb(0x2f7df6);
        let background = if dark { rgb(0x111318) } else { rgb(0xf7f8fb) };
        let muted = if dark { rgb(0xa3a8b8) } else { rgb(0x5d6474) };
        let border = if dark { rgb(0x303543) } else { rgb(0xdde1ea) };
        let mut control = div()
            .flex()
            .flex_wrap()
            .justify_end()
            .gap_1()
            .max_w(px(320.0));
        for (value, label) in choices {
            let selected = *value == current;
            let listener = cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                this.update_setting(key, SettingValue::Text((*value).to_string()), cx);
            });
            control = control.child(
                div()
                    .id(SharedString::from(format!("setting-choice-{key}-{value}")))
                    .px_2()
                    .py_1()
                    .rounded_md()
                    .border_1()
                    .border_color(if selected { accent } else { border })
                    .bg(if selected { accent } else { background })
                    .text_color(if selected { rgb(0xffffff) } else { muted })
                    .text_xs()
                    .cursor_pointer()
                    .child(*label)
                    .on_click(listener),
            );
        }
        self.setting_row(title, description, control, dark)
    }

    fn int_choice_setting_row(
        &self,
        title: &str,
        description: &str,
        key: &'static str,
        current: i64,
        choices: &'static [(&'static str, &'static str)],
        dark: bool,
        cx: &mut Context<Self>,
    ) -> gpui::Div {
        let accent = rgb(0x2f7df6);
        let background = if dark { rgb(0x111318) } else { rgb(0xf7f8fb) };
        let muted = if dark { rgb(0xa3a8b8) } else { rgb(0x5d6474) };
        let border = if dark { rgb(0x303543) } else { rgb(0xdde1ea) };
        let current = current.to_string();
        let mut control = div()
            .flex()
            .flex_wrap()
            .justify_end()
            .gap_1()
            .max_w(px(320.0));
        for (value, label) in choices {
            let selected = *value == current;
            let parsed = value.parse::<i64>().unwrap_or_default();
            let listener = cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                this.update_setting(key, SettingValue::Integer(parsed), cx);
            });
            control = control.child(
                div()
                    .id(SharedString::from(format!(
                        "setting-int-choice-{key}-{value}"
                    )))
                    .px_2()
                    .py_1()
                    .rounded_md()
                    .border_1()
                    .border_color(if selected { accent } else { border })
                    .bg(if selected { accent } else { background })
                    .text_color(if selected { rgb(0xffffff) } else { muted })
                    .text_xs()
                    .cursor_pointer()
                    .child(*label)
                    .on_click(listener),
            );
        }
        self.setting_row(title, description, control, dark)
    }

    fn text_setting_row(
        &self,
        title: &str,
        description: &str,
        input: gpui::Entity<AddressInput>,
        dark: bool,
    ) -> gpui::Div {
        let background = if dark { rgb(0x111318) } else { rgb(0xf7f8fb) };
        let border = if dark { rgb(0x303543) } else { rgb(0xdde1ea) };
        let control = div()
            .w(px(300.0))
            .h(px(30.0))
            .rounded_md()
            .bg(background)
            .border_1()
            .border_color(border)
            .child(input);
        self.setting_row(title, description, control, dark)
    }

    fn info_setting_row(
        &self,
        title: &str,
        description: &str,
        value: String,
        dark: bool,
    ) -> gpui::Div {
        let muted = if dark { rgb(0xa3a8b8) } else { rgb(0x5d6474) };
        self.setting_row(
            title,
            description,
            div()
                .max_w(px(360.0))
                .text_xs()
                .text_color(muted)
                .overflow_hidden()
                .child(value),
            dark,
        )
    }

    fn settings_match(query: &str, text: &str) -> bool {
        query.is_empty()
            || query
                .split_whitespace()
                .all(|part| text.to_lowercase().contains(part))
    }

    fn render_settings(&mut self, window: &mut Window, cx: &mut Context<Self>) -> gpui::Div {
        let dark = effective_dark(&self.settings.theme, window);
        let background = if dark { rgb(0x111318) } else { rgb(0xf7f8fb) };
        let surface = if dark { rgb(0x1c1f27) } else { rgb(0xffffff) };
        let muted = if dark { rgb(0xa3a8b8) } else { rgb(0x5d6474) };
        let border = if dark { rgb(0x303543) } else { rgb(0xdde1ea) };
        let accent = rgb(0x2f7df6);
        let query = self
            .settings_search
            .read(cx)
            .content()
            .trim()
            .to_lowercase();
        let search_active = !query.is_empty();
        let selected = self.settings_category;
        let compact = self.settings.ui_density == "compact";
        let narrow = f32::from(window.viewport_size().width) < 820.0;
        let nav_width = if narrow { 0.0 } else { 176.0 };

        let close = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
            this.panel = LibraryPanel::None;
            cx.notify();
        });
        let clear_search = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
            this.settings_search
                .update(cx, |input, cx| input.set_content("", cx));
            cx.notify();
        });

        let mut nav = div().flex().flex_col().gap_1().p_2();
        if narrow {
            nav = nav.flex_row().flex_wrap();
        }
        for category in SettingsCategory::ALL {
            let active = !search_active && selected == category;
            let listener = cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                this.settings_category = category;
                this.settings_search
                    .update(cx, |input, cx| input.set_content("", cx));
                this.settings_confirm_reset = false;
                cx.notify();
            });
            nav = nav.child(
                div()
                    .id(SharedString::from(format!(
                        "settings-nav-{}",
                        category.key()
                    )))
                    .px_3()
                    .py(px(if compact { 6.0 } else { 9.0 }))
                    .rounded_md()
                    .bg(if active { accent } else { background })
                    .text_color(if active { rgb(0xffffff) } else { muted })
                    .text_sm()
                    .cursor_pointer()
                    .child(category.label())
                    .on_click(listener),
            );
        }

        let mut content = div()
            .id("settings-content")
            .flex()
            .flex_col()
            .flex_1()
            .overflow_y_scroll()
            .bg(surface);
        let header = |label: &str| {
            div()
                .px_4()
                .pt_4()
                .pb_2()
                .text_xs()
                .text_color(muted)
                .child(label.to_string())
        };
        if search_active || selected == SettingsCategory::Appearance {
            content = content.child(header("외관"));
            if Self::settings_match(&query, "테마 theme 라이트 다크 시스템") {
                content = content.child(self.choice_setting_row(
                    "테마",
                    "라이트, 다크 또는 Windows 시스템 테마를 따릅니다.",
                    "theme",
                    &self.settings.theme,
                    &[("system", "시스템"), ("light", "라이트"), ("dark", "다크")],
                    dark,
                    cx,
                ));
            }
            if Self::settings_match(&query, "밀도 density compact comfortable") {
                content = content.child(self.choice_setting_row(
                    "UI 밀도",
                    "탭과 설정 화면의 간격을 조절합니다.",
                    "ui_density",
                    &self.settings.ui_density,
                    &[("comfortable", "여유"), ("compact", "촘촘")],
                    dark,
                    cx,
                ));
            }
            if Self::settings_match(&query, "글꼴 font family") {
                content = content.child(self.text_setting_row(
                    "기본 글꼴",
                    "브라우저 UI에 사용할 글꼴 패밀리입니다.",
                    self.settings_font.clone(),
                    dark,
                ));
            }
            if Self::settings_match(&query, "글꼴 크기 font size") {
                content = content.child(self.int_choice_setting_row(
                    "글꼴 크기",
                    "브라우저 UI의 기준 글꼴 크기입니다.",
                    "ui_font_size",
                    self.settings.ui_font_size,
                    &[("14", "14"), ("16", "16"), ("18", "18"), ("20", "20")],
                    dark,
                    cx,
                ));
            }
            if Self::settings_match(&query, "탭 너비 tab width") {
                content = content.child(self.int_choice_setting_row(
                    "탭 너비",
                    "탭 하나의 가로 폭입니다.",
                    "tab_width",
                    self.settings.tab_width,
                    &[
                        ("100", "100"),
                        ("120", "120"),
                        ("140", "140"),
                        ("160", "160"),
                    ],
                    dark,
                    cx,
                ));
            }
        }
        if search_active || selected == SettingsCategory::Startup {
            content = content.child(header("시작 및 세션"));
            if Self::settings_match(&query, "세션 복원 restore session") {
                content = content.child(self.toggle_setting_row(
                    "이전 세션 복원",
                    "다음 실행에서 저장된 창과 탭을 복원합니다.",
                    "restore_session",
                    self.settings.restore_session,
                    dark,
                    cx,
                ));
            }
            if Self::settings_match(&query, "종료 저장 save session exit") {
                content = content.child(self.toggle_setting_row(
                    "종료 시 세션 저장",
                    "앱을 닫을 때 현재 창과 탭 구성을 저장합니다.",
                    "save_session_on_exit",
                    self.settings.save_session_on_exit,
                    dark,
                    cx,
                ));
            }
            if Self::settings_match(&query, "시작 페이지 home page") {
                content = content.child(self.text_setting_row(
                    "시작 페이지",
                    "복원 없이 시작할 때 여는 페이지입니다.",
                    self.settings_home_page.clone(),
                    dark,
                ));
            }
            if Self::settings_match(&query, "새 탭 페이지 new tab") {
                content = content.child(self.text_setting_row(
                    "새 탭 페이지",
                    "새 탭을 만들 때 여는 페이지입니다.",
                    self.settings_new_tab.clone(),
                    dark,
                ));
            }
            if Self::settings_match(&query, "최근 세션 recent session 정보") {
                let summary = self
                    .session_summary
                    .as_ref()
                    .map(|summary| {
                        format!(
                            "{}개 창 · {}개 탭 · 저장 시각 {} ms",
                            summary.windows, summary.tabs, summary.saved_at_ms
                        )
                    })
                    .unwrap_or_else(|| "저장된 세션이 없습니다.".to_string());
                content = content.child(self.info_setting_row(
                    "최근 세션",
                    "SQLite에 저장된 마지막 창·탭 구성입니다.",
                    summary,
                    dark,
                ));
            }
            if Self::settings_match(&query, "복원 상태 restore progress failure") {
                let status = self
                    .restore_progress
                    .as_ref()
                    .map(|progress| {
                        format!(
                            "{}/{} 완료 · {} 실패",
                            progress.finished, progress.total, progress.failed
                        )
                    })
                    .unwrap_or_else(|| {
                        if let Some(error) = &self.session_error {
                            error.clone()
                        } else {
                            "현재 복원 작업 없음".to_string()
                        }
                    });
                content = content.child(self.info_setting_row(
                    "복원 상태",
                    "이 창에서 진행 중이거나 마지막으로 완료된 복원 상태입니다.",
                    status,
                    dark,
                ));
            }
            if Self::settings_match(&query, "새 세션 new session 시작") {
                let new_session = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                    this.start_new_session(cx);
                });
                content = content.child(
                    self.setting_row(
                        "새 세션으로 시작",
                        "저장된 세션을 지우고 다음 실행에서 새 탭으로 시작합니다.",
                        div()
                            .id("settings-new-session")
                            .px_3()
                            .py_2()
                            .rounded_md()
                            .bg(background)
                            .text_color(muted)
                            .text_xs()
                            .cursor_pointer()
                            .child("저장된 세션 지우기")
                            .on_click(new_session),
                        dark,
                    ),
                );
            }
        }
        if search_active || selected == SettingsCategory::Search {
            content = content.child(header("검색 및 주소 표시줄"));
            if Self::settings_match(&query, "검색 엔진 search engine") {
                content = content.child(self.choice_setting_row(
                    "기본 검색 엔진",
                    "주소 표시줄에서 검색할 때 사용할 엔진입니다.",
                    "engine",
                    &self.settings.engine,
                    &[
                        ("duckduckgo", "DuckDuckGo"),
                        ("google", "Google"),
                        ("bing", "Bing"),
                    ],
                    dark,
                    cx,
                ));
            }
            if Self::settings_match(&query, "주소 표시줄 동작 address behavior") {
                content = content.child(self.choice_setting_row(
                    "주소 표시줄 동작",
                    "스마트 모드는 URL을 인식하고, 검색 모드는 비URL 입력을 검색합니다.",
                    "address_behavior",
                    &self.settings.address_behavior,
                    &[("smart", "스마트"), ("search", "검색 우선")],
                    dark,
                    cx,
                ));
            }
        }
        if search_active || selected == SettingsCategory::Downloads {
            content = content.child(header("다운로드"));
            if Self::settings_match(&query, "다운로드 경로 download path") {
                content = content.child(self.text_setting_row(
                    "기본 다운로드 경로",
                    "비우면 사용자 Downloads 폴더를 사용합니다. 절대 경로만 저장됩니다.",
                    self.settings_download_path.clone(),
                    dark,
                ));
            }
            if Self::settings_match(&query, "다운로드 패널 panel") {
                content = content.child(self.toggle_setting_row(
                    "다운로드 시작 시 패널 열기",
                    "새 다운로드가 시작되면 다운로드 패널을 표시합니다.",
                    "download_panel_auto_open",
                    self.settings.download_panel_auto_open,
                    dark,
                    cx,
                ));
            }
            if Self::settings_match(&query, "저장 위치 확인 save as") {
                content = content.child(self.info_setting_row(
                    "저장 위치 확인",
                    "WebView2의 저장 위치 확인 대화상자는 아직 연결하지 않았습니다.",
                    "UNVERIFIED".into(),
                    dark,
                ));
            }
            if Self::settings_match(&query, "완료 알림 notification") {
                content = content.child(self.info_setting_row(
                    "완료 알림",
                    "다운로드 완료 시스템 알림은 아직 구현하지 않았습니다.",
                    "UNVERIFIED".into(),
                    dark,
                ));
            }
        }
        if search_active || selected == SettingsCategory::Privacy {
            content = content.child(header("개인정보 및 보안"));
            if Self::settings_match(&query, "방문 기록 history 저장") {
                content = content.child(self.toggle_setting_row(
                    "방문 기록 저장",
                    "로컬 SQLite에 방문 기록을 저장합니다.",
                    "save_history",
                    self.settings.save_history,
                    dark,
                    cx,
                ));
            }
            if Self::settings_match(&query, "비밀번호 자동 완성 autofill password") {
                content = content.child(self.toggle_setting_row(
                    "비밀번호 자동 완성",
                    "사용자가 선택한 exact-origin 계정만 로그인 폼에 입력합니다.",
                    "password_autofill_enabled",
                    self.settings.password_autofill_enabled,
                    dark,
                    cx,
                ));
            }
            if Self::settings_match(&query, "비밀번호 저장 제안 save prompt") {
                content = content.child(self.toggle_setting_row(
                    "로그인 정보 저장 제안",
                    "로그인 submit 후 저장/업데이트 제안을 표시합니다.",
                    "password_save_prompt_enabled",
                    self.settings.password_save_prompt_enabled,
                    dark,
                    cx,
                ));
            }
            if Self::settings_match(&query, "인증서 경고 certificate") {
                content = content.child(self.info_setting_row(
                    "인증서 경고",
                    "안전하지 않은 인증서 경고를 무력화하는 설정은 제공하지 않습니다.",
                    "항상 경고".into(),
                    dark,
                ));
            }
            if Self::settings_match(&query, "쿠키 사이트 데이터 cookie site data") {
                content = content.child(self.info_setting_row(
                    "쿠키 및 사이트 데이터",
                    "관리 진입점은 아직 구현하지 않았습니다.",
                    "UNVERIFIED".into(),
                    dark,
                ));
            }
            if Self::settings_match(&query, "사이트 권한 permission") {
                content = content.child(self.info_setting_row(
                    "사이트 권한",
                    "관리 진입점은 아직 구현하지 않았습니다.",
                    "UNVERIFIED".into(),
                    dark,
                ));
            }
        }
        if search_active || selected == SettingsCategory::Advanced {
            content = content.child(header("고급"));
            if Self::settings_match(&query, "버전 version 브라우저") {
                content = content.child(self.info_setting_row(
                    "AMARE 버전",
                    "현재 GPUI 셸 버전입니다.",
                    env!("CARGO_PKG_VERSION").to_string(),
                    dark,
                ));
            }
            if Self::settings_match(&query, "webview2 런타임 runtime") {
                content = content.child(self.info_setting_row(
                    "WebView2 런타임",
                    "설치된 WebView2 런타임 버전입니다.",
                    self.runtime_version.clone(),
                    dark,
                ));
            }
            if Self::settings_match(&query, "gpu 하드웨어 가속 hardware acceleration") {
                content = content.child(self.info_setting_row(
                    "하드웨어 가속",
                    "WebView2 기본 GPU 정책을 사용하며 강제로 끄지 않습니다.",
                    "WebView2 기본 정책".into(),
                    dark,
                ));
            }
            if Self::settings_match(&query, "데이터 저장 위치 data path") {
                content = content.child(self.info_setting_row(
                    "데이터 저장 위치",
                    "프로필 SQLite와 WebView2 데이터가 저장되는 위치입니다.",
                    library::data_root_display(),
                    dark,
                ));
            }
            if Self::settings_match(&query, "로그 진단 log diagnostic") {
                let status = std::env::var("AMARE_GPUI_STATUS").unwrap_or_else(|_| {
                    "상태 파일 미설정 · 설정 오류는 이 화면에 표시됩니다.".into()
                });
                content = content.child(self.info_setting_row(
                    "로그 및 진단",
                    "자동화 상태 JSON 경로와 오류 표시 상태입니다.",
                    status,
                    dark,
                ));
            }
            if Self::settings_match(&query, "초기화 reset 기본값") {
                let reset = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                    this.reset_settings(cx);
                });
                let label = if self.settings_confirm_reset {
                    "정말 모든 설정 초기화"
                } else {
                    "모든 설정 초기화"
                };
                content = content.child(
                    self.setting_row(
                        "설정 초기화",
                        "모든 브라우저 설정을 기본값으로 되돌립니다. 세션 데이터는 유지됩니다.",
                        div()
                            .id("settings-reset")
                            .px_3()
                            .py_2()
                            .rounded_md()
                            .bg(if self.settings_confirm_reset {
                                rgb(0xd9534f)
                            } else {
                                background
                            })
                            .text_color(if self.settings_confirm_reset {
                                rgb(0xffffff)
                            } else {
                                muted
                            })
                            .text_xs()
                            .cursor_pointer()
                            .child(label)
                            .on_click(reset),
                        dark,
                    ),
                );
            }
        }

        if search_active || selected == SettingsCategory::Permissions {
            content = content.child(header("사이트 권한"));
            content = content.child(self.info_setting_row(
                "카메라 및 마이크",
                "WebView2의 사이트별 permission 프롬프트를 따릅니다. AMARE 저장/조회 UI는 아직 없습니다.",
                "WebView2 기본 프롬프트".into(),
                dark,
            ));
            content = content.child(self.info_setting_row(
                "알림",
                "사이트 알림은 WebView2 권한 상태를 사용하며 AMARE가 별도로 저장하지 않습니다.",
                "UNVERIFIED".into(),
                dark,
            ));
            content = content.child(self.info_setting_row(
                "위치",
                "위치 권한은 WebView2 기본 프롬프트를 사용합니다.",
                "UNVERIFIED".into(),
                dark,
            ));
            let clear_site = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                this.clear_site_data(cx);
            });
            content = content.child(self.info_setting_row(
                "쿠키 및 사이트 데이터",
                "WebView2 ALL_SITE 삭제 API는 UI 프로세스에서 완료/종료를 안정적으로 검증하지 못했습니다. 0.59.0은 read-only로 유지하며 사이트별 삭제 UI를 제공하지 않습니다.",
                "BLOCKED / read-only".into(),
                dark,
            ));
        }

        let search_box = div()
            .flex()
            .items_center()
            .gap_1()
            .flex_1()
            .max_w(px(420.0))
            .h(px(30.0))
            .px_2()
            .rounded_md()
            .bg(background)
            .border_1()
            .border_color(border)
            .child(self.settings_search.clone())
            .child(
                div()
                    .id("settings-search-clear")
                    .size(px(20.0))
                    .flex()
                    .items_center()
                    .justify_center()
                    .rounded_sm()
                    .text_color(muted)
                    .cursor_pointer()
                    .child("×")
                    .on_click(clear_search),
            );

        let body = if narrow {
            div().flex().flex_col().flex_1().child(nav).child(content)
        } else {
            div()
                .flex()
                .flex_row()
                .flex_1()
                .child(
                    div()
                        .w(px(nav_width))
                        .h_full()
                        .border_r_1()
                        .border_color(border)
                        .child(nav),
                )
                .child(content)
        };

        div()
            .flex()
            .flex_col()
            .size_full()
            .bg(surface)
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_3()
                    .h(px(52.0))
                    .px_4()
                    .border_b_1()
                    .border_color(border)
                    .child(div().text_lg().child("설정"))
                    .child(search_box)
                    .child(
                        div()
                            .id("settings-close")
                            .size(px(28.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .rounded_md()
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .child("×")
                            .on_click(close),
                    ),
            )
            .when_some(self.settings_error.clone(), |el, error| {
                el.child(
                    div()
                        .px_4()
                        .py_2()
                        .bg(rgb(0x5c1f24))
                        .text_color(rgb(0xffd7da))
                        .text_xs()
                        .child(error),
                )
            })
            .child(body)
    }

    fn render_password_panel(&mut self, window: &Window, cx: &mut Context<Self>) -> gpui::Div {
        let dark = effective_dark(&self.settings.theme, window);
        let background = if dark { rgb(0x111318) } else { rgb(0xf7f8fb) };
        let surface = if dark { rgb(0x1c1f27) } else { rgb(0xffffff) };
        let muted = if dark { rgb(0xa3a8b8) } else { rgb(0x5d6474) };
        let border = if dark { rgb(0x303543) } else { rgb(0xdde1ea) };
        let accent = rgb(0x2f7df6);
        let danger = rgb(0xd9534f);
        let unlocked = self.password_ui.vault.is_unlocked();
        let query = self.password_ui.search.read(cx).content().to_lowercase();
        let entries = self.password_ui.vault.entries().to_vec();
        let current_origin = self
            .active_tab()
            .and_then(|tab| password_vault::normalize_origin(&tab.url).ok());
        let site_entries = current_origin
            .as_deref()
            .map(|origin| self.password_ui.vault.entries_for_origin(origin))
            .unwrap_or_default();
        let filtered = entries
            .iter()
            .filter(|entry| {
                query.is_empty()
                    || entry.origin.to_lowercase().contains(&query)
                    || entry.username.to_lowercase().contains(&query)
            })
            .cloned()
            .collect::<Vec<_>>();
        let editing = self.password_ui.editing;
        let form_open = self.password_ui.form_open;
        let password_masked = self.password_ui.password_masked;

        let close = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
            this.panel = LibraryPanel::None;
            cx.notify();
        });
        let unlock = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
            this.unlock_password_vault(cx);
        });
        let lock = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
            this.lock_password_vault(cx);
        });
        let add = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, window, cx| {
            this.begin_password_add(window, cx);
        });
        let save = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
            this.save_password_form(cx);
        });
        let backup_vault = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
            this.backup_password_vault(cx);
        });
        let retry_unlock = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
            this.unlock_password_vault(cx);
        });
        let cancel_form = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
            this.password_ui.form_open = false;
            this.password_ui.editing = None;
            this.password_ui.error = None;
            cx.notify();
        });
        let toggle_password_mask = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
            this.password_ui.password_masked = !this.password_ui.password_masked;
            let masked = this.password_ui.password_masked;
            this.password_ui
                .password
                .update(cx, |input, cx| input.set_masked(masked, cx));
            cx.notify();
        });

        let field = |label: &str, input: gpui::Entity<AddressInput>| {
            div()
                .flex()
                .flex_col()
                .gap_1()
                .child(div().text_xs().text_color(muted).child(label.to_string()))
                .child(
                    div()
                        .h(px(30.0))
                        .rounded_md()
                        .bg(background)
                        .border_1()
                        .border_color(border)
                        .child(input),
                )
        };

        let pending = self.password_ui.pending.as_ref().map(|pending| {
            let save_pending = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                this.save_pending_login(cx);
            });
            let skip_pending = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                this.skip_pending_login(cx);
            });
            (
                pending.origin.clone(),
                pending.username.clone(),
                save_pending,
                skip_pending,
            )
        });
        let pending_card = pending.map(|(origin, username, save_pending, skip_pending)| {
            div()
                .id("password-save-proposal")
                .flex()
                .flex_col()
                .gap_2()
                .p_3()
                .rounded_md()
                .bg(if dark { rgb(0x17304f) } else { rgb(0xe8f1ff) })
                .border_1()
                .border_color(accent)
                .child(
                    div()
                        .text_sm()
                        .text_color(accent)
                        .child("로그인 정보 저장 제안"),
                )
                .child(div().text_xs().text_color(muted).child(origin))
                .child(
                    div()
                        .text_xs()
                        .text_color(muted)
                        .child(format!("사용자: {username}")),
                )
                .child(
                    div()
                        .flex()
                        .gap_2()
                        .child(
                            div()
                                .id("password-save-pending")
                                .px_3()
                                .py_1()
                                .rounded_md()
                                .bg(accent)
                                .text_color(rgb(0xffffff))
                                .text_xs()
                                .cursor_pointer()
                                .child("저장")
                                .on_click(save_pending),
                        )
                        .child(
                            div()
                                .id("password-skip-pending")
                                .px_3()
                                .py_1()
                                .rounded_md()
                                .bg(background)
                                .text_color(muted)
                                .text_xs()
                                .cursor_pointer()
                                .child("저장하지 않기")
                                .on_click(skip_pending),
                        ),
                )
        });

        let form =
            if form_open && unlocked {
                Some(
                    div()
                        .id("password-form")
                        .flex()
                        .flex_col()
                        .gap_2()
                        .p_3()
                        .border_b_1()
                        .border_color(border)
                        .child(
                            div()
                                .flex()
                                .justify_between()
                                .items_center()
                                .child(div().text_sm().text_color(accent).child(
                                    if editing.is_some() {
                                        "로그인 정보 수정"
                                    } else {
                                        "로그인 정보 추가"
                                    },
                                ))
                                .child(
                                    div()
                                        .id("password-form-cancel")
                                        .text_xs()
                                        .text_color(muted)
                                        .cursor_pointer()
                                        .child("취소")
                                        .on_click(cancel_form),
                                ),
                        )
                        .child(field("사이트 origin", self.password_ui.origin.clone()))
                        .child(field("사용자 이름", self.password_ui.username.clone()))
                        .child(field("비밀번호", self.password_ui.password.clone()))
                        .child(
                            div()
                                .flex()
                                .items_center()
                                .justify_between()
                                .child(
                                    div()
                                        .id("password-toggle-mask")
                                        .text_xs()
                                        .text_color(accent)
                                        .cursor_pointer()
                                        .child(if password_masked {
                                            "비밀번호 표시"
                                        } else {
                                            "비밀번호 숨기기"
                                        })
                                        .on_click(toggle_password_mask),
                                )
                                .child(
                                    div()
                                        .id("password-save-form")
                                        .px_3()
                                        .py_1()
                                        .rounded_md()
                                        .bg(accent)
                                        .text_color(rgb(0xffffff))
                                        .text_xs()
                                        .cursor_pointer()
                                        .child("저장")
                                        .on_click(save),
                                ),
                        ),
                )
            } else {
                None
            };

        let site_section = if unlocked {
            let origin = current_origin
                .clone()
                .unwrap_or_else(|| "http/https 사이트가 아닙니다.".into());
            let mut rows = div().flex().flex_col().gap_2();
            if site_entries.is_empty() {
                rows = rows.child(
                    div()
                        .text_xs()
                        .text_color(muted)
                        .child("이 사이트 origin에 저장된 계정이 없습니다."),
                );
            }
            for entry in site_entries {
                let id = entry.id;
                let username = entry.username.clone();
                let fill = cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                    this.fill_password_entry(id, cx);
                });
                rows = rows.child(
                    div()
                        .flex()
                        .items_center()
                        .justify_between()
                        .gap_2()
                        .px_2()
                        .py_1()
                        .rounded_md()
                        .bg(background)
                        .child(div().text_xs().child(username))
                        .child(
                            div()
                                .id(SharedString::from(format!("password-fill-{id}")))
                                .px_2()
                                .py_1()
                                .rounded_sm()
                                .bg(accent)
                                .text_color(rgb(0xffffff))
                                .text_xs()
                                .cursor_pointer()
                                .child("채우기")
                                .on_click(fill),
                        ),
                );
            }
            Some(
                div()
                    .flex()
                    .flex_col()
                    .gap_2()
                    .p_3()
                    .border_b_1()
                    .border_color(border)
                    .child(
                        div()
                            .flex()
                            .justify_between()
                            .child(
                                div()
                                    .text_xs()
                                    .text_color(accent)
                                    .child("현재 사이트 자동 완성"),
                            )
                            .child(div().text_xs().text_color(muted).child(origin)),
                    )
                    .child(rows),
            )
        } else {
            None
        };

        let mut rows = div().flex().flex_col();
        if filtered.is_empty() {
            rows = rows.child(div().p_4().text_sm().text_color(muted).child(if unlocked {
                "저장된 로그인 정보가 없습니다."
            } else {
                "보안 저장소가 잠겨 있습니다."
            }));
        }
        for entry in filtered {
            let id = entry.id;
            let origin = entry.origin.clone();
            let username = entry.username.clone();
            let revealed = self.password_ui.reveal == Some(id);
            let password_text = if revealed {
                entry.password.clone()
            } else {
                entry.masked_password()
            };
            let toggle_reveal = cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                this.password_ui.reveal = if this.password_ui.reveal == Some(id) {
                    None
                } else {
                    Some(id)
                };
                cx.notify();
            });
            let edit = cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                this.begin_password_edit(id, cx);
            });
            let delete = cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                this.delete_password_entry(id, cx);
            });
            let confirm = self.password_ui.confirm_delete == Some(id);
            let cancel_delete = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                this.password_ui.confirm_delete = None;
                cx.notify();
            });
            rows = rows.child(
                div()
                    .id(SharedString::from(format!("password-entry-{id}")))
                    .flex()
                    .flex_col()
                    .gap_1()
                    .p_3()
                    .border_b_1()
                    .border_color(border)
                    .child(
                        div()
                            .flex()
                            .justify_between()
                            .items_center()
                            .child(
                                div()
                                    .text_xs()
                                    .text_color(accent)
                                    .overflow_hidden()
                                    .child(origin),
                            )
                            .child(
                                div()
                                    .id(SharedString::from(format!("password-reveal-{id}")))
                                    .text_xs()
                                    .text_color(muted)
                                    .cursor_pointer()
                                    .child(if revealed { "숨기기" } else { "표시" })
                                    .on_click(toggle_reveal),
                            ),
                    )
                    .child(div().text_sm().child(username))
                    .child(
                        div()
                            .flex()
                            .justify_between()
                            .items_center()
                            .gap_2()
                            .child(div().text_xs().text_color(muted).child(password_text))
                            .child(
                                div()
                                    .flex()
                                    .gap_2()
                                    .child(
                                        div()
                                            .id(SharedString::from(format!("password-edit-{id}")))
                                            .px_2()
                                            .py_1()
                                            .rounded_sm()
                                            .text_xs()
                                            .text_color(accent)
                                            .cursor_pointer()
                                            .child("수정")
                                            .on_click(edit),
                                    )
                                    .child(
                                        div()
                                            .id(SharedString::from(format!("password-delete-{id}")))
                                            .px_2()
                                            .py_1()
                                            .rounded_sm()
                                            .text_xs()
                                            .text_color(if confirm { danger } else { muted })
                                            .cursor_pointer()
                                            .child(if confirm { "정말 삭제" } else { "삭제" })
                                            .on_click(delete),
                                    )
                                    .when(confirm, |el| {
                                        el.child(
                                            div()
                                                .id("password-cancel-delete")
                                                .px_2()
                                                .py_1()
                                                .rounded_sm()
                                                .text_xs()
                                                .text_color(muted)
                                                .cursor_pointer()
                                                .child("취소")
                                                .on_click(cancel_delete),
                                        )
                                    }),
                            ),
                    ),
            );
        }

        let locked_section = if unlocked {
            None
        } else {
            Some(
                div()
                    .flex()
                    .flex_col()
                    .gap_3()
                    .p_4()
                    .child(
                        div()
                            .text_sm()
                            .child("Windows DPAPI로 보호된 로컬 보안 저장소"),
                    )
                    .child(
                        div()
                            .text_xs()
                            .text_color(muted)
                            .child("비밀번호는 SQLite에 저장하지 않으며, 잠금 해제 전에는 복호화하지 않습니다."),
                    )
                    .child(
                        div()
                            .id("password-unlock")
                            .px_3()
                            .py_2()
                            .rounded_md()
                            .bg(accent)
                            .text_color(rgb(0xffffff))
                            .text_sm()
                            .cursor_pointer()
                            .child("잠금 해제")
                            .on_click(unlock),
                    ),
            )
        };

        div()
            .flex()
            .flex_col()
            .w(px(PASSWORD_PANEL_WIDTH))
            .h_full()
            .bg(surface)
            .border_l_1()
            .border_color(border)
            .child(
                div()
                    .flex()
                    .items_center()
                    .justify_between()
                    .h(px(44.0))
                    .px_3()
                    .border_b_1()
                    .border_color(border)
                    .child(div().text_sm().text_color(accent).child("비밀번호 관리자"))
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap_2()
                            .child(
                                div()
                                    .text_xs()
                                    .text_color(if unlocked { accent } else { muted })
                                    .child(if unlocked {
                                        "잠금 해제됨"
                                    } else {
                                        "잠김"
                                    }),
                            )
                            .when(unlocked, |el| {
                                el.child(
                                    div()
                                        .id("password-lock")
                                        .text_xs()
                                        .text_color(muted)
                                        .cursor_pointer()
                                        .child("잠금")
                                        .on_click(lock),
                                )
                            })
                            .child(
                                div()
                                    .id("password-close")
                                    .size(px(24.0))
                                    .flex()
                                    .items_center()
                                    .justify_center()
                                    .rounded_sm()
                                    .hover(|style| style.bg(border))
                                    .cursor_pointer()
                                    .child("×")
                                    .on_click(close),
                            ),
                    ),
            )
            .when_some(locked_section, |el, section| el.child(section))
            .when(unlocked, |el| {
                el.child(
                    div()
                        .flex()
                        .items_center()
                        .gap_2()
                        .p_3()
                        .border_b_1()
                        .border_color(border)
                        .child(
                            div()
                                .flex_1()
                                .h(px(30.0))
                                .rounded_md()
                                .bg(background)
                                .border_1()
                                .border_color(border)
                                .child(self.password_ui.search.clone()),
                        )
                        .child(
                            div()
                                .id("password-add")
                                .px_3()
                                .py_1()
                                .rounded_md()
                                .bg(accent)
                                .text_color(rgb(0xffffff))
                                .text_xs()
                                .cursor_pointer()
                                .child("+ 추가")
                                .on_click(add),
                        ),
                )
            })
            .when_some(pending_card, |el, card| el.child(card))
            .when_some(form, |el, form| el.child(form))
            .when_some(site_section, |el, section| el.child(section))
            .child(
                div()
                    .id("password-list")
                    .flex()
                    .flex_col()
                    .flex_1()
                    .overflow_y_scroll()
                    .child(rows),
            )
            .when_some(self.password_ui.info.clone(), |el, info| {
                el.child(div().p_2().text_xs().text_color(accent).child(info))
            })
            .when_some(self.password_ui.error.clone(), |el, error| {
                el.child(
                    div()
                        .flex()
                        .flex_col()
                        .gap_2()
                        .p_2()
                        .bg(if dark { rgb(0x3a1d21) } else { rgb(0xffe8e8) })
                        .text_xs()
                        .text_color(danger)
                        .child(error)
                        .child(
                            div()
                                .flex()
                                .gap_2()
                                .child(
                                    div()
                                        .id("password-retry-unlock")
                                        .px_2()
                                        .py_1()
                                        .rounded_sm()
                                        .bg(accent)
                                        .text_color(rgb(0xffffff))
                                        .cursor_pointer()
                                        .child("다시 시도")
                                        .on_click(retry_unlock),
                                )
                                .child(
                                    div()
                                        .id("password-backup-vault")
                                        .px_2()
                                        .py_1()
                                        .rounded_sm()
                                        .bg(background)
                                        .text_color(muted)
                                        .cursor_pointer()
                                        .child("vault 백업")
                                        .on_click(backup_vault),
                                ),
                        )
                        .when_some(self.password_ui.recovery_path.clone(), |el, path| {
                            el.child(div().text_xs().text_color(muted).child(path))
                        }),
                )
            })
    }

    fn render_library_panel(&mut self, window: &Window, cx: &mut Context<Self>) -> gpui::Div {
        let dark = effective_dark(&self.settings.theme, window);
        let surface = if dark { rgb(0x1c1f27) } else { rgb(0xffffff) };
        let background = if dark { rgb(0x111318) } else { rgb(0xf7f8fb) };
        let muted = if dark { rgb(0xa3a8b8) } else { rgb(0x5d6474) };
        let border = if dark { rgb(0x303543) } else { rgb(0xdde1ea) };
        let accent = rgb(0x2f7df6);
        let is_history = self.panel == LibraryPanel::History;
        let is_downloads = self.panel == LibraryPanel::Downloads;
        let title = if is_downloads {
            "다운로드"
        } else if is_history {
            "방문 기록"
        } else {
            "북마크"
        };
        let close = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
            this.panel = LibraryPanel::None;
            cx.notify();
        });
        let clear = cx.listener(|this: &mut Self, _: &gpui::ClickEvent, _, cx| {
            this.clear_history(cx);
        });
        let rows = if is_downloads {
            self.downloads
                .iter()
                .map(|item| {
                    let id = item.id;
                    let path = item.path.clone();
                    let cancel =
                        cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                            this.cancel_download(id, cx);
                        });
                    let remove =
                        cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                            this.remove_download(id, cx);
                        });
                    let open_path = path.clone();
                    let open = cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, _| {
                        this.open_download_path(&open_path, false);
                    });
                    let open_folder = path.clone();
                    let folder = cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, _| {
                        this.open_download_path(&open_folder, true);
                    });
                    let percent = if item.total_bytes > 0 {
                        (item.received_bytes as f64 / item.total_bytes as f64 * 100.0) as i64
                    } else {
                        0
                    };
                    div()
                        .id(SharedString::from(format!("download-{id}")))
                        .flex()
                        .flex_col()
                        .gap_1()
                        .px_3()
                        .py_2()
                        .border_b_1()
                        .border_color(border)
                        .child(
                            div()
                                .flex()
                                .justify_between()
                                .items_center()
                                .child(
                                    div()
                                        .text_sm()
                                        .text_color(accent)
                                        .overflow_hidden()
                                        .child(item.filename.clone()),
                                )
                                .child(
                                    div()
                                        .text_xs()
                                        .text_color(muted)
                                        .child(format!("{}%", percent)),
                                ),
                        )
                        .child(
                            div()
                                .text_xs()
                                .text_color(muted)
                                .overflow_hidden()
                                .child(item.url.clone()),
                        )
                        .child(
                            div()
                                .flex()
                                .items_center()
                                .gap_2()
                                .child(
                                    div().h(px(5.0)).flex_1().rounded_full().bg(border).child(
                                        div()
                                            .h(px(5.0))
                                            .w(px((percent.max(0).min(100) as f32 * 2.4)))
                                            .bg(accent),
                                    ),
                                )
                                .child(div().text_xs().text_color(muted).child(item.state.clone())),
                        )
                        .child(
                            div()
                                .flex()
                                .items_center()
                                .gap_2()
                                .child(
                                    div()
                                        .id(SharedString::from(format!("download-cancel-{id}")))
                                        .text_xs()
                                        .text_color(muted)
                                        .cursor_pointer()
                                        .child("취소")
                                        .on_click(cancel),
                                )
                                .child(
                                    div()
                                        .id(SharedString::from(format!("download-remove-{id}")))
                                        .text_xs()
                                        .text_color(muted)
                                        .cursor_pointer()
                                        .child("제거")
                                        .on_click(remove),
                                )
                                .child(
                                    div()
                                        .id(SharedString::from(format!("download-open-{id}")))
                                        .text_xs()
                                        .text_color(accent)
                                        .cursor_pointer()
                                        .child("열기")
                                        .on_click(open),
                                )
                                .child(
                                    div()
                                        .id(SharedString::from(format!("download-folder-{id}")))
                                        .text_xs()
                                        .text_color(muted)
                                        .cursor_pointer()
                                        .child("폴더")
                                        .on_click(folder),
                                ),
                        )
                })
                .collect::<Vec<_>>()
        } else if is_history {
            self.history
                .iter()
                .map(|entry| {
                    let url = entry.url.clone();
                    let open = cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                        this.navigate_to(url.clone(), cx);
                    });
                    let delete_url = entry.url.clone();
                    let delete =
                        cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                            this.delete_history(delete_url.clone(), cx);
                        });
                    div()
                        .id(SharedString::from(format!("history-{}", entry.id)))
                        .flex()
                        .items_center()
                        .justify_between()
                        .gap_2()
                        .px_3()
                        .py_2()
                        .hover(|style| style.bg(border))
                        .cursor_pointer()
                        .on_click(open)
                        .child(
                            div()
                                .flex()
                                .flex_col()
                                .flex_1()
                                .overflow_hidden()
                                .child(
                                    div()
                                        .text_sm()
                                        .text_color(accent)
                                        .overflow_hidden()
                                        .child(entry.title.clone()),
                                )
                                .child(
                                    div()
                                        .text_xs()
                                        .text_color(muted)
                                        .overflow_hidden()
                                        .child(entry.url.clone()),
                                )
                                .child(div().text_xs().text_color(muted).child(format!(
                                    "{}회 · {}",
                                    entry.visit_count, entry.last_visited_at_ms
                                ))),
                        )
                        .child(
                            div()
                                .id(SharedString::from(format!("history-delete-{}", entry.id)))
                                .flex_none()
                                .size(px(20.0))
                                .flex()
                                .items_center()
                                .justify_center()
                                .rounded_sm()
                                .hover(|style| style.bg(surface))
                                .child("×")
                                .on_click(delete),
                        )
                })
                .collect::<Vec<_>>()
        } else {
            self.bookmarks
                .iter()
                .map(|bookmark| {
                    let url = bookmark.url.clone();
                    let open = cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                        this.navigate_to(url.clone(), cx);
                    });
                    let id = bookmark.id;
                    let delete =
                        cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                            this.delete_bookmark(id, cx);
                        });
                    div()
                        .id(SharedString::from(format!("bookmark-{id}")))
                        .flex()
                        .items_center()
                        .justify_between()
                        .gap_2()
                        .px_3()
                        .py_2()
                        .hover(|style| style.bg(border))
                        .cursor_pointer()
                        .on_click(open)
                        .child(
                            div()
                                .flex()
                                .flex_col()
                                .flex_1()
                                .overflow_hidden()
                                .child(
                                    div()
                                        .text_sm()
                                        .text_color(accent)
                                        .overflow_hidden()
                                        .child(bookmark.title.clone()),
                                )
                                .child(
                                    div()
                                        .text_xs()
                                        .text_color(muted)
                                        .overflow_hidden()
                                        .child(bookmark.url.clone()),
                                ),
                        )
                        .child(
                            div()
                                .id(SharedString::from(format!("bookmark-delete-{id}")))
                                .flex_none()
                                .size(px(20.0))
                                .flex()
                                .items_center()
                                .justify_center()
                                .rounded_sm()
                                .hover(|style| style.bg(surface))
                                .child("×")
                                .on_click(delete),
                        )
                })
                .collect::<Vec<_>>()
        };
        let empty = if is_downloads {
            "다운로드가 없습니다."
        } else if is_history {
            "방문 기록이 없습니다."
        } else {
            "북마크가 없습니다."
        };
        let clear_label = if self.confirm_clear_history {
            "정말 전체 삭제"
        } else {
            "전체 기록 삭제"
        };
        div()
            .flex()
            .flex_col()
            .w(px(PANEL_WIDTH))
            .h_full()
            .bg(surface)
            .border_l_1()
            .border_color(border)
            .child(
                div()
                    .flex()
                    .items_center()
                    .justify_between()
                    .h(px(44.0))
                    .px_3()
                    .border_b_1()
                    .border_color(border)
                    .child(div().text_sm().text_color(accent).child(title))
                    .child(
                        div()
                            .id("library-close")
                            .size(px(24.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .rounded_sm()
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .child("×")
                            .on_click(close),
                    ),
            )
            .when(is_history, |el| {
                el.child(
                    div().px_3().py_2().border_b_1().border_color(border).child(
                        div()
                            .h(px(30.0))
                            .rounded_md()
                            .bg(background)
                            .border_1()
                            .border_color(border)
                            .child(self.search_input.clone()),
                    ),
                )
            })
            .child(
                div()
                    .id("library-list")
                    .flex()
                    .flex_col()
                    .flex_1()
                    .overflow_y_scroll()
                    .when(rows.is_empty(), |el| {
                        el.child(div().p_4().text_sm().text_color(muted).child(empty))
                    })
                    .children(rows),
            )
            .when_some(self.library_error.clone(), |el, error| {
                el.child(div().p_2().text_xs().text_color(rgb(0xd9534f)).child(error))
            })
            .when(is_history, |el| {
                el.child(
                    div()
                        .flex()
                        .items_center()
                        .justify_between()
                        .gap_2()
                        .p_3()
                        .border_t_1()
                        .border_color(border)
                        .child(
                            div()
                                .id("history-clear")
                                .px_2()
                                .py_1()
                                .rounded_sm()
                                .bg(if self.confirm_clear_history {
                                    rgb(0xd9534f)
                                } else {
                                    background
                                })
                                .text_sm()
                                .text_color(if self.confirm_clear_history {
                                    rgb(0xffffff)
                                } else {
                                    muted
                                })
                                .cursor_pointer()
                                .child(clear_label)
                                .on_click(clear),
                        ),
                )
            })
    }

    fn refresh_downloads(&mut self) {
        self.downloads = library::downloads().unwrap_or_default();
    }

    fn poll_downloads(&mut self, cx: &mut Context<Self>) {
        #[cfg(target_os = "windows")]
        {
            let mut snapshots = Vec::new();
            let auto_open =
                self.settings.download_panel_auto_open && self.panel == LibraryPanel::None;
            for tab in &self.tabs {
                if let Some(webview) = &tab.webview {
                    snapshots.extend(webview.download_snapshots());
                }
            }
            for snapshot in snapshots {
                let id = snapshot.id as i64;
                let existing = self.downloads.iter().find(|item| item.id == id).cloned();
                let changed = existing
                    .as_ref()
                    .map(|item| {
                        item.received_bytes != snapshot.received_bytes
                            || item.state != snapshot.state
                            || item.total_bytes != snapshot.total_bytes
                    })
                    .unwrap_or(true);
                if changed {
                    let _ = library::upsert_download(
                        id,
                        &snapshot.filename,
                        &snapshot.url,
                        &snapshot.path,
                        &snapshot.mime_type,
                        snapshot.total_bytes,
                        snapshot.received_bytes,
                        &snapshot.state,
                        &snapshot.error,
                    );
                    self.downloads = library::downloads().unwrap_or_default();
                    if auto_open && existing.is_none() {
                        self.panel = LibraryPanel::Downloads;
                    }
                }
            }
        }
        #[cfg(not(target_os = "windows"))]
        let _ = (cx,);
    }

    fn cancel_download(&mut self, id: i64, cx: &mut Context<Self>) {
        #[cfg(target_os = "windows")]
        for tab in &self.tabs {
            if let Some(webview) = &tab.webview {
                if webview.cancel_download(id as u64) {
                    break;
                }
            }
        }
        self.refresh_downloads();
        cx.notify();
    }

    fn remove_download(&mut self, id: i64, cx: &mut Context<Self>) {
        #[cfg(target_os = "windows")]
        for tab in &self.tabs {
            if let Some(webview) = &tab.webview {
                webview.remove_download(id as u64);
            }
        }
        self.downloads = library::delete_download(id).unwrap_or_else(|_| self.downloads.clone());
        cx.notify();
    }

    fn open_download_path(&self, path: &str, folder: bool) {
        let mut command = std::process::Command::new("explorer.exe");
        if folder {
            command.arg(format!("/select,{path}"));
        } else {
            command.arg(path);
        }
        let _ = command.spawn();
    }

    fn update_session(&mut self, window: &Window) {
        let due = self.last_session_save.elapsed() >= Duration::from_secs(2);
        if due {
            #[cfg(target_os = "windows")]
            for tab in &mut self.tabs {
                if tab.loading || tab.error.is_some() {
                    continue;
                }
                if let Some(webview) = &tab.webview {
                    if let Some((x, y)) = webview.scroll_position() {
                        tab.scroll_x = x;
                        tab.scroll_y = y;
                    }
                }
            }
        }
        let bounds = window.bounds();
        let now_ms = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis() as i64;
        let snapshot = SessionWindowSnapshot {
            window_id: self.window_id,
            x: f32::from(bounds.origin.x),
            y: f32::from(bounds.origin.y),
            width: f32::from(bounds.size.width),
            height: f32::from(bounds.size.height),
            maximized: window.is_maximized(),
            active: self.active,
            tabs: self
                .tabs
                .iter()
                .map(|tab| SessionTabSnapshot {
                    url: tab.url.to_string(),
                    title: tab.title.to_string(),
                    scroll_x: tab.scroll_x,
                    scroll_y: tab.scroll_y,
                })
                .collect(),
            saved_at_ms: now_ms,
        };
        let changed = SESSION_WINDOWS.with(|cell| {
            let mut windows = cell.borrow_mut();
            let changed = windows.get(&self.window_id) != Some(&snapshot);
            windows.insert(self.window_id, snapshot);
            changed
        });
        if self.settings.save_session_on_exit
            && (due || (changed && self.last_session_save.elapsed() >= Duration::from_millis(500)))
        {
            persist_session(false);
            SESSION_CLOSED_WINDOWS.with(|cell| cell.borrow_mut().clear());
            self.last_session_save = Instant::now();
        }
    }

    fn session_banner_active(&self) -> bool {
        if self.session_error.is_some() {
            return true;
        }
        self.restore_progress
            .as_ref()
            .map(|progress| {
                progress.finished < progress.total || Instant::now() < progress.visible_until
            })
            .unwrap_or(false)
    }

    fn render_session_banner(&self, dark: bool) -> impl IntoElement {
        let (text, background, foreground) = if let Some(error) = &self.session_error {
            (
                format!("세션 복원 실패 · {error}"),
                rgb(0x5c1f24),
                rgb(0xffd7da),
            )
        } else if let Some(progress) = &self.restore_progress {
            if progress.finished < progress.total {
                (
                    format!("세션 복원 중 · {}/{} 탭", progress.finished, progress.total),
                    if dark { rgb(0x1d3b5c) } else { rgb(0xdce9ff) },
                    if dark { rgb(0xd7e8ff) } else { rgb(0x174a8b) },
                )
            } else if progress.failed > 0 {
                (
                    format!(
                        "세션 복원 완료 · {}개 탭 실패, {}개 탭 복원",
                        progress.failed,
                        progress.total.saturating_sub(progress.failed)
                    ),
                    rgb(0x5c1f24),
                    rgb(0xffd7da),
                )
            } else {
                (
                    format!("세션 복원 완료 · {}개 탭", progress.total),
                    if dark { rgb(0x163d2b) } else { rgb(0xdff5e8) },
                    if dark { rgb(0xc8f0d8) } else { rgb(0x17633a) },
                )
            }
        } else {
            (String::new(), rgb(0x000000), rgb(0xffffff))
        };
        div()
            .id("session-restore-banner")
            .flex()
            .items_center()
            .h(px(30.0))
            .px_4()
            .bg(background)
            .text_color(foreground)
            .text_xs()
            .child(text)
    }

    fn sync_webview_bounds(&self, window: &Window) {
        #[cfg(target_os = "windows")]
        if self.panel == LibraryPanel::Settings {
            if let Some(webview) = self.active_webview() {
                webview.set_active(false);
                webview.set_bounds(0, 0, 1, 1);
            }
            return;
        }
        #[cfg(target_os = "windows")]
        if let Some(webview) = self.active_webview() {
            webview.set_active(true);
            let viewport = window.viewport_size();
            let scale = window.scale_factor();
            let panel = if self.panel == LibraryPanel::None {
                0
            } else if self.panel == LibraryPanel::Passwords {
                (PASSWORD_PANEL_WIDTH * scale).round() as i32
            } else {
                (PANEL_WIDTH * scale).round() as i32
            };
            let width =
                ((f32::from(viewport.width) * scale).round().max(1.0) as i32 - panel).max(1);
            let height = (f32::from(viewport.height) * scale).round().max(1.0) as i32;
            let banner = if self.session_banner_active() {
                30.0
            } else {
                0.0
            };
            let toolbar = ((TOOLBAR_HEIGHT + banner) * scale).round() as i32;
            webview.set_bounds(0, toolbar, width, (height - toolbar).max(1));
        }
    }
}

impl Render for AmareApp {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        if !self.status_poll_started {
            self.status_poll_started = true;
            self.start_status_poll(cx);
        }
        self.poll_drop_request(window, cx);
        self.poll_downloads(cx);
        self.poll_login_captures(cx);
        self.sync_vault_lock(cx);
        self.sync_password_vault(cx);
        self.process_popup_requests(window, cx);
        self.refresh_tabs(window, cx);
        self.update_session(window);
        self.sync_settings(cx);
        self.sync_settings_text(false, cx);
        window.set_rem_size(px(self.settings.ui_font_size as f32));
        self.sync_webview_bounds(window);
        let dark = effective_dark(&self.settings.theme, window);
        let background = if dark { rgb(0x111318) } else { rgb(0xf7f8fb) };
        let surface = if dark { rgb(0x1c1f27) } else { rgb(0xffffff) };
        let muted = if dark { rgb(0xa3a8b8) } else { rgb(0x5d6474) };
        let border = if dark { rgb(0x303543) } else { rgb(0xdde1ea) };
        let accent = rgb(0x2f7df6);
        let can_back = self
            .active_tab()
            .map(|tab| tab.can_go_back)
            .unwrap_or(false);
        let can_forward = self
            .active_tab()
            .map(|tab| tab.can_go_forward)
            .unwrap_or(false);
        let bookmarked = self
            .active_tab()
            .map(|tab| {
                self.bookmarks
                    .iter()
                    .any(|bookmark| bookmark.url == tab.url.as_ref())
            })
            .unwrap_or(false);
        let source_window_id = self.window_id;
        let bookmarks = self
            .bookmarks
            .iter()
            .map(|bookmark| serde_json::json!({"id": bookmark.id, "title": bookmark.title, "url": bookmark.url}))
            .collect::<Vec<_>>();
        let history = self
            .history
            .iter()
            .map(|entry| serde_json::json!({"id": entry.id, "title": entry.title, "url": entry.url, "visited_at_ms": entry.last_visited_at_ms, "visit_count": entry.visit_count}))
            .collect::<Vec<_>>();
        let panel = match self.panel {
            LibraryPanel::None => "none",
            LibraryPanel::Bookmarks => "bookmarks",
            LibraryPanel::History => "history",
            LibraryPanel::Downloads => "downloads",
            LibraryPanel::Settings => "settings",
            LibraryPanel::Passwords => "passwords",
        };
        let downloads = self
            .downloads
            .iter()
            .map(|item| {
                serde_json::json!({
                    "id": item.id, "filename": item.filename, "url": item.url, "path": item.path,
                    "total_bytes": item.total_bytes, "received_bytes": item.received_bytes,
                    "state": item.state, "error": item.error
                })
            })
            .collect::<Vec<_>>();
        let tabs = self
            .tabs
            .iter()
            .map(|tab| {
                let active = tab.id == self.active_tab().map(|active| active.id).unwrap_or(0);
                let id = tab.id;
                let click =
                    cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, window, cx| {
                        this.select_tab_by_id(id, window, cx);
                    });
                let close =
                    cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, window, cx| {
                        this.close_tab(id, window, cx);
                    });
                let drag_info = TabDragInfo { id };
                let drop = cx.listener(move |this: &mut Self, info: &TabDragInfo, _, cx| {
                    this.move_tab_before(info.id, id, cx);
                });
                div()
                    .id(SharedString::from(format!("tab-{id}")))
                    .flex()
                    .items_center()
                    .gap_1()
                    .px_2()
                    .w(px(self.settings.tab_width as f32))
                    .h(px(if self.settings.ui_density == "compact" {
                        30.0
                    } else {
                        34.0
                    }))
                    .rounded_md()
                    .bg(if active { surface } else { background })
                    .border_1()
                    .border_color(if active { border } else { background })
                    .text_sm()
                    .text_color(if active { accent } else { muted })
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap_1()
                            .max_w(px(74.0))
                            .overflow_hidden()
                            .text_ellipsis()
                            .child(if tab.restored {
                                div()
                                    .text_xs()
                                    .text_color(accent)
                                    .child("↻")
                                    .into_any_element()
                            } else {
                                div().into_any_element()
                            })
                            .child(tab.title.clone()),
                    )
                    .child(if tab.error.is_some() {
                        "!"
                    } else if tab.loading {
                        "◌"
                    } else {
                        ""
                    })
                    .on_drag(drag_info, move |info: &TabDragInfo, _, _, cx| {
                        #[cfg(target_os = "windows")]
                        start_drop_watch(source_window_id, info.id);
                        cx.new(|_| TabDragPreview { id: info.id })
                    })
                    .on_drop(drop)
                    .on_click(click)
                    .child(
                        div()
                            .id(SharedString::from(format!("tab-close-{id}")))
                            .flex_none()
                            .flex()
                            .items_center()
                            .justify_center()
                            .size(px(20.0))
                            .rounded_sm()
                            .hover(|style| style.bg(border))
                            .child("×")
                            .on_click(close),
                    )
            })
            .collect::<Vec<_>>();

        div()
            .track_focus(&self.focus_handle)
            .on_mouse_down(gpui::MouseButton::Left, cx.listener(|this: &mut Self, event: &gpui::MouseDownEvent, _, cx| {
                this.trace_event(format!("mouse_down x={:.1} y={:.1}", f32::from(event.position.x), f32::from(event.position.y)));
                cx.notify();
            }))
            .on_key_down(cx.listener(|this: &mut Self, event: &gpui::KeyDownEvent, _, cx| {
                if this.pending_input_id.is_none() {
                    let id = this.next_input_id;
                    this.next_input_id += 1;
                    this.pending_input_id = Some(id);
                    this.trace_event(format!("keydown input={id} keystroke={}", event.keystroke));
                } else {
                    this.trace_event(format!("keydown-held keystroke={}", event.keystroke));
                }
                cx.notify();
            }))
            .on_action(cx.listener(Self::focus_address))
            .on_action(cx.listener(Self::submit_address))
            .on_action(cx.listener(Self::cancel_address))
            .on_action(cx.listener(Self::reload_page))
            .on_action(cx.listener(Self::go_back))
            .on_action(cx.listener(Self::go_forward))
            .on_action(cx.listener(Self::new_tab_action))
            .on_action(cx.listener(Self::close_tab_action))
            .on_action(cx.listener(Self::new_window_action))
            .on_action(cx.listener(Self::capture_page_action))
            .on_action(cx.listener(Self::toggle_bookmark_action))
            .on_action(cx.listener(Self::toggle_bookmarks_panel_action))
            .on_action(cx.listener(Self::toggle_history_panel_action))
            .on_action(cx.listener(Self::toggle_downloads_panel_action))
            .on_action(cx.listener(Self::toggle_settings_action))
            .on_action(cx.listener(Self::toggle_passwords_action))
            .on_action(cx.listener(Self::next_tab_action))
            .on_action(cx.listener(Self::previous_tab_action))
            .flex()
            .flex_col()
            .size_full()
            .bg(background)
            .font_family(self.settings.ui_font.clone())
            .text_color(if dark { rgb(0xf5f7fb) } else { rgb(0x161a22) })
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .h(px(44.0))
                    .px_3()
                    .bg(surface)
                    .border_b_1()
                    .border_color(border)
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap_1()
                            .flex_1()
                            .overflow_hidden()
                            .window_control_area(WindowControlArea::Drag)
                            .child(div().flex().gap_1().children(tabs)),
                    )
                    .child(
                        div()
                            .id("new-tab")
                            .flex_none()
                            .flex()
                            .items_center()
                            .justify_center()
                            .size(px(30.0))
                            .rounded_md()
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .text_lg()
                            .child("+")
                            .on_click(cx.listener(|this: &mut Self, _, window, cx| {
                                this.begin_input("click:new-tab");
                                this.new_tab(window, cx);
                                cx.notify();
                            })),
                    )
                    .child(
                        div()
                            .id("new-window")
                            .flex_none()
                            .flex()
                            .items_center()
                            .justify_center()
                            .size(px(30.0))
                            .rounded_md()
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .text_sm()
                            .child("▣")
                            .on_click(cx.listener(|this: &mut Self, _, _, cx| {
                                this.begin_input("click:new-window");
                                this.open_new_window(cx);
                            })),
                    )
                    .child(
                        div()
                            .id("window-min")
                            .flex_none()
                            .w(px(46.0))
                            .h(px(44.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .window_control_area(WindowControlArea::Min)
                            .child("—"),
                    )
                    .child(
                        div()
                            .id("window-max")
                            .flex_none()
                            .w(px(46.0))
                            .h(px(44.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .window_control_area(WindowControlArea::Max)
                            .child("□"),
                    )
                    .child(
                        div()
                            .id("window-close")
                            .flex_none()
                            .w(px(46.0))
                            .h(px(44.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .hover(|style| style.bg(rgb(0xd9534f)))
                            .cursor_pointer()
                            .window_control_area(WindowControlArea::Close)
                            .child("×"),
                    )
                    .on_children_prepainted({
                        let weak = cx.weak_entity();
                        move |bounds, window, cx| {
                            let _ = weak.update(cx, |this, _cx| {
                                this.record_layout(bounds, window);
                            });
                        }
                    }),
            )
            .child(
                div()
                    .flex()
                    .items_center()
                    .gap_2()
                    .h(px(44.0))
                    .px_3()
                    .bg(surface)
                    .border_b_1()
                    .border_color(border)
                    .child(
                        div()
                            .id("back")
                            .flex_none()
                            .size(px(30.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .rounded_md()
                            .opacity(if can_back { 1.0 } else { 0.35 })
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .child("‹")
                            .on_click(cx.listener(|this: &mut Self, _, window, cx| {
                                if this.active_tab().map(|tab| tab.can_go_back).unwrap_or(false) {
                                    this.go_back(&BackPage, window, cx);
                                }
                                cx.notify();
                            })),
                    )
                    .child(
                        div()
                            .id("forward")
                            .flex_none()
                            .size(px(30.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .rounded_md()
                            .opacity(if can_forward { 1.0 } else { 0.35 })
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .child("›")
                            .on_click(cx.listener(|this: &mut Self, _, window, cx| {
                                if this.active_tab().map(|tab| tab.can_go_forward).unwrap_or(false) {
                                    this.go_forward(&ForwardPage, window, cx);
                                }
                                cx.notify();
                            })),
                    )
                    .child(
                        div()
                            .id("passwords-panel")
                            .flex_none()
                            .size(px(30.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .rounded_md()
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .text_sm()
                            .child("🔑")
                            .on_click(cx.listener(|this: &mut Self, _, window, cx| {
                                this.open_passwords(window, cx);
                            })),
                    )
                    .child(
                        div()
                            .id("address")
                            .flex()
                            .items_center()
                            .flex_1()
                            .h(px(30.0))
                            .rounded_md()
                            .bg(background)
                            .border_1()
                            .border_color(border)
                            .text_sm()
                            .text_color(muted)
                            .child(self.address_input.clone()),
                    )
                    .child(
                        div()
                            .id("downloads-panel")
                            .flex_none()
                            .size(px(30.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .rounded_md()
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .text_sm()
                            .child("↓")
                            .on_click(cx.listener(|this: &mut Self, _, window, cx| {
                                this.open_library_panel(LibraryPanel::Downloads, window, cx);
                            })),
                    )
                    .child(
                        div()
                            .id("bookmark-page")
                            .flex_none()
                            .size(px(30.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .rounded_md()
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .text_lg()
                            .child(if bookmarked { "★" } else { "☆" })
                            .on_click(cx.listener(|this: &mut Self, _, _, cx| {
                                this.toggle_active_bookmark(cx);
                            })),
                    )
                    .child(
                        div()
                            .id("bookmarks-panel")
                            .flex_none()
                            .size(px(30.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .rounded_md()
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .text_sm()
                            .child("★")
                            .on_click(cx.listener(|this: &mut Self, _, window, cx| {
                                this.open_library_panel(LibraryPanel::Bookmarks, window, cx);
                            })),
                    )
                    .child(
                        div()
                            .id("history-panel")
                            .flex_none()
                            .size(px(30.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .rounded_md()
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .text_sm()
                            .child("◷")
                            .on_click(cx.listener(|this: &mut Self, _, window, cx| {
                                this.open_library_panel(LibraryPanel::History, window, cx);
                            })),
                    )
                    .child(
                        div()
                            .id("settings-panel")
                            .flex_none()
                            .size(px(30.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .rounded_md()
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .text_sm()
                            .child("⚙")
                            .on_click(cx.listener(|this: &mut Self, _, _, cx| {
                                this.open_settings(cx);
                            })),
                    )
                    .child(
                        div()
                            .id("theme")
                            .flex_none()
                            .size(px(30.0))
                            .flex()
                            .items_center()
                            .justify_center()
                            .rounded_md()
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .child(if dark { "☾" } else { "☀" })
                            .on_click(cx.listener(|this: &mut Self, _, _, cx| {
                                let next = if this.settings.theme == "dark" {
                                    "light"
                                } else {
                                    "dark"
                                };
                                this.update_setting(
                                    "theme",
                                    SettingValue::Text(next.to_string()),
                                    cx,
                                );
                            })),
                    ),
            )
            .when(self.session_banner_active(), |el| {
                el.child(self.render_session_banner(dark))
            })
            .child(if self.panel == LibraryPanel::Settings {
                self.render_settings(window, cx)
            } else if self.panel == LibraryPanel::Passwords {
                div()
                    .flex()
                    .flex_row()
                    .size_full()
                    .bg(surface)
                    .child(div().flex_1())
                    .child(self.render_password_panel(window, cx))
            } else {
                div()
                    .flex()
                    .flex_row()
                    .size_full()
                    .bg(surface)
                    .child(
                        div()
                            .flex()
                            .flex_col()
                            .flex_1()
                            .when(cfg!(target_os = "windows"), |el| {
                                el.child(
                                    div()
                                        .p_4()
                                        .text_sm()
                                        .text_color(muted)
                                        .child(self.webview_error.clone().unwrap_or_else(|| {
                                            "WebView2 content is hosted in native child windows below the toolbar."
                                                .into()
                                        })),
                                )
                            }),
                    )
                    .when(self.panel != LibraryPanel::None, |el| {
                        el.child(self.render_library_panel(window, cx))
                    })
            })
    }
}

impl Drop for AmareApp {
    fn drop(&mut self) {
        let empty = SESSION_WINDOWS.with(|windows| {
            let mut windows = windows.borrow_mut();
            if let Some(snapshot) = windows.remove(&self.window_id) {
                SESSION_CLOSED_WINDOWS.with(|closed| {
                    closed.borrow_mut().insert(snapshot.window_id, snapshot);
                });
            }
            windows.is_empty()
        });
        if self.settings.save_session_on_exit {
            // Persist immediately while the closing window's snapshot is still
            // in CLOSED_WINDOWS. This preserves all windows even when the
            // process quits before the last Drop runs.
            persist_session(true);
        } else if empty {
            let _ = library::save_session("[]");
        }
    }
}

fn persist_session(include_closed: bool) {
    let mut snapshots =
        SESSION_WINDOWS.with(|cell| cell.borrow().values().cloned().collect::<Vec<_>>());
    if include_closed {
        SESSION_CLOSED_WINDOWS.with(|cell| snapshots.extend(cell.borrow().values().cloned()));
    }
    let payload = serde_json::to_string(&snapshots).ok();
    if let Some(payload) = payload {
        let _ = library::save_session(&payload);
    }
}

fn load_session_windows(
    settings: &library::BrowserSettings,
) -> (Vec<SessionWindowSnapshot>, Option<String>) {
    if !settings.restore_session
        || std::env::var("AMARE_GPUI_RESTORE").ok().as_deref() == Some("0")
        || std::env::var("AMARE_GPUI_SELF_TEST").ok().as_deref() == Some("1")
    {
        return (Vec::new(), None);
    }
    match library::load_session() {
        Ok(Some(json)) => match serde_json::from_str::<Vec<SessionWindowSnapshot>>(&json) {
            Ok(windows) => (windows, None),
            Err(error) => {
                let _ = library::save_corrupt_session(&json);
                (
                    Vec::new(),
                    Some(format!("저장된 세션 데이터가 손상되었습니다 ({error})")),
                )
            }
        },
        Ok(None) => (Vec::new(), None),
        Err(error) => (
            Vec::new(),
            Some(format!("세션을 읽지 못했습니다 ({error})")),
        ),
    }
}

fn normalize_url(input: &str, settings: &library::BrowserSettings) -> Option<String> {
    let value = input.trim();
    if value.is_empty() {
        return Some("about:blank".into());
    }
    if value.contains("://") || value.starts_with("about:") {
        return Some(value.into());
    }
    if settings.address_behavior == "search" {
        return None;
    }
    if value.contains('.') && !value.contains(' ') {
        return Some(format!("https://{value}"));
    }
    None
}

fn runtime_version() -> String {
    #[cfg(target_os = "windows")]
    {
        webview::runtime_version()
    }
    #[cfg(not(target_os = "windows"))]
    {
        "N/A (non-Windows build)".to_string()
    }
}

fn url_encode(value: &str) -> String {
    value
        .bytes()
        .map(|byte| match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                (byte as char).to_string()
            }
            b' ' => "+".into(),
            _ => format!("%{byte:02X}"),
        })
        .collect()
}

fn history_key(url: &str) -> String {
    let key = url.split('#').next().unwrap_or(url).trim();
    if key.is_empty() || key.starts_with("about:") {
        String::new()
    } else {
        key.to_string()
    }
}

fn automation_window_size() -> (f32, f32) {
    if let Ok(value) = std::env::var("AMARE_GPUI_WINDOW_SIZE") {
        if let Some((width, height)) = value.split_once('x') {
            if let (Ok(width), Ok(height)) =
                (width.trim().parse::<f32>(), height.trim().parse::<f32>())
            {
                if (640.0..=4096.0).contains(&width) && (480.0..=4096.0).contains(&height) {
                    return (width, height);
                }
            }
        }
    }
    (1180.0, 760.0)
}

fn png_dimensions(bytes: &[u8]) -> Option<(u32, u32)> {
    const PNG_SIGNATURE: &[u8; 8] = b"\x89PNG\r\n\x1a\n";
    if bytes.len() < 24 || &bytes[..8] != PNG_SIGNATURE || &bytes[12..16] != b"IHDR" {
        return None;
    }
    let width = u32::from_be_bytes(bytes[16..20].try_into().ok()?);
    let height = u32::from_be_bytes(bytes[20..24].try_into().ok()?);
    Some((width, height))
}

fn title_for(url: &str) -> String {
    url.trim_start_matches("https://")
        .trim_start_matches("http://")
        .trim_end_matches('/')
        .split('/')
        .next()
        .unwrap_or("Tab")
        .to_string()
}

fn main() {
    Application::new().run(|cx: &mut App| {
        cx.bind_keys([
            KeyBinding::new("ctrl-l", FocusAddress, None),
            KeyBinding::new("ctrl-r", ReloadPage, None),
            KeyBinding::new("ctrl-t", NewTab, None),
            KeyBinding::new("ctrl-w", CloseTab, None),
            KeyBinding::new("ctrl-n", NewWindow, None),
            KeyBinding::new("ctrl-shift-n", NewWindow, None),
            KeyBinding::new("ctrl-d", ToggleBookmark, None),
            KeyBinding::new("ctrl-shift-b", ToggleBookmarksPanel, None),
            KeyBinding::new("ctrl-shift-h", ToggleHistoryPanel, None),
            KeyBinding::new("ctrl-shift-d", ToggleDownloadsPanel, None),
            KeyBinding::new("ctrl-,", ToggleSettings, None),
            KeyBinding::new("ctrl-shift-p", TogglePasswords, None),
            KeyBinding::new("ctrl-shift-s", CapturePage, None),
            KeyBinding::new("f9", CapturePage, None),
            KeyBinding::new("ctrl-alt-p", CapturePage, None),
            KeyBinding::new("ctrl-tab", NextTab, None),
            KeyBinding::new("ctrl-shift-tab", PreviousTab, None),
            KeyBinding::new("alt-left", BackPage, None),
            KeyBinding::new("alt-right", ForwardPage, None),
            KeyBinding::new("enter", SubmitAddress, None),
            KeyBinding::new("escape", CancelAddress, None),
            KeyBinding::new("backspace", text_input::Backspace, Some("AddressInput")),
            KeyBinding::new("delete", text_input::Delete, Some("AddressInput")),
            KeyBinding::new("left", text_input::Left, Some("AddressInput")),
            KeyBinding::new("right", text_input::Right, Some("AddressInput")),
            KeyBinding::new("shift-left", text_input::SelectLeft, Some("AddressInput")),
            KeyBinding::new("shift-right", text_input::SelectRight, Some("AddressInput")),
            KeyBinding::new("ctrl-a", text_input::SelectAll, Some("AddressInput")),
            KeyBinding::new("ctrl-c", text_input::Copy, Some("AddressInput")),
            KeyBinding::new("ctrl-v", text_input::Paste, Some("AddressInput")),
            KeyBinding::new("ctrl-x", text_input::Cut, Some("AddressInput")),
            KeyBinding::new("home", text_input::Home, Some("AddressInput")),
            KeyBinding::new("end", text_input::End, Some("AddressInput")),
        ]);
        cx.on_window_closed(|cx| {
            if cx.windows().is_empty() {
                cx.quit();
            }
        })
        .detach();
        let (_, startup_settings) = library::browser_settings_cached()
            .unwrap_or_else(|_| (0, library::BrowserSettings::default()));
        let (restored, session_error) = load_session_windows(&startup_settings);
        STARTUP_SESSION_ERROR.with(|error| *error.borrow_mut() = session_error);
        if restored.is_empty() {
            let (width, height) = automation_window_size();
            let window_count = if std::env::var("AMARE_GPUI_OPEN_SECOND_WINDOW")
                .ok()
                .as_deref()
                == Some("1")
            {
                2
            } else {
                1
            };
            for _ in 0..window_count {
                let bounds = Bounds::centered(None, size(px(width), px(height)), cx);
                cx.open_window(
                    WindowOptions {
                        window_bounds: Some(WindowBounds::Windowed(bounds)),
                        titlebar: Some(gpui::TitlebarOptions {
                            title: Some("AMARE".into()),
                            appears_transparent: true,
                            ..Default::default()
                        }),
                        ..Default::default()
                    },
                    |window, cx| cx.new(|cx| AmareApp::new(window, cx)),
                )
                .expect("failed to open AMARE window");
            }
        } else {
            for snapshot in restored {
                let bounds = Bounds::new(
                    point(px(snapshot.x.max(0.0)), px(snapshot.y.max(0.0))),
                    size(
                        px(snapshot.width.max(640.0)),
                        px(snapshot.height.max(480.0)),
                    ),
                );
                let window_bounds = if snapshot.maximized {
                    WindowBounds::Maximized(bounds)
                } else {
                    WindowBounds::Windowed(bounds)
                };
                let _ = cx.open_window(
                    WindowOptions {
                        window_bounds: Some(window_bounds),
                        titlebar: Some(gpui::TitlebarOptions {
                            title: Some("AMARE".into()),
                            appears_transparent: true,
                            ..Default::default()
                        }),
                        ..Default::default()
                    },
                    move |window, cx| cx.new(|cx| AmareApp::new_restored(window, cx, snapshot)),
                );
            }
        }
        cx.activate(true);
    });
}
