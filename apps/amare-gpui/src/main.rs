use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::Duration;

use gpui::{
    App, Application, Bounds, Context, FocusHandle, Focusable, KeyBinding, Render, SharedString,
    Timer, Window, WindowBounds, WindowOptions, actions, div, prelude::*, px, rgb, size,
};
use raw_window_handle::{HasWindowHandle, RawWindowHandle};

#[cfg(target_os = "windows")]
use windows::Win32::{
    Foundation::POINT,
    UI::{
        Input::KeyboardAndMouse::{GetAsyncKeyState, VK_LBUTTON},
        WindowsAndMessaging::GetCursorPos,
    },
};

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
    ]
);

const TOOLBAR_HEIGHT: f32 = 88.0;
const TAB_START_X: f32 = 12.0;
const TAB_SLOT: f32 = 124.0;

static NEXT_WINDOW_ID: AtomicU64 = AtomicU64::new(1);

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
    std::thread::spawn(move || loop {
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
    #[cfg(target_os = "windows")]
    webview: Option<webview::WebViewHost>,
}

struct AmareApp {
    tabs: Vec<TabState>,
    active: usize,
    next_tab_id: u64,
    window_id: u64,
    activity_counter: u64,
    new_tab_url: String,
    address_input: gpui::Entity<AddressInput>,
    focus_handle: FocusHandle,
    #[cfg(target_os = "windows")]
    parent: Option<HWND>,
    #[cfg(target_os = "windows")]
    webview_env: Option<std::sync::Arc<webview::WebViewEnvironment>>,
    #[cfg(target_os = "windows")]
    webview_error: Option<String>,
    dark: bool,
    status_poll_started: bool,
}

impl AmareApp {
    fn new(window: &mut Window, cx: &mut App) -> Self {
        let start_url = std::env::var("AMARE_GPUI_START_URL")
            .unwrap_or_else(|_| "https://example.com/".to_string());
        Self::new_with_url(window, cx, start_url, true)
    }

    fn new_with_url(window: &mut Window, cx: &mut App, start_url: String, run_self_test: bool) -> Self {
        let focus_handle = cx.focus_handle();
        focus_handle.focus(window);
        let new_tab_url = std::env::var("AMARE_GPUI_NEW_TAB_URL")
            .unwrap_or_else(|_| "about:blank".to_string());

        #[cfg(target_os = "windows")]
        let parent = window
            .window_handle()
            .ok()
            .map(|handle| handle.as_raw())
            .and_then(|handle| match handle {
                RawWindowHandle::Win32(handle) => {
                    Some(HWND(handle.hwnd.get() as *mut _))
                }
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
        let first_webview = match (webview_env.as_ref(), parent) {
            (Some(env), Some(parent)) => match webview::WebViewHost::create(env, parent) {
                Ok(webview) => Some(webview),
                Err(error) => {
                    webview_error = Some(error);
                    None
                }
            },
            _ => None,
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
            loading: false,
            error: first_error,
            can_go_back: false,
            can_go_forward: false,
            favicon: None,
            last_active: 1,
            #[cfg(target_os = "windows")]
            webview: first_webview,
        }];

        let address_input = cx.new(|cx| AddressInput::new(start_url.clone(), cx));
        let app = Self {
            tabs,
            active: 0,
            next_tab_id,
            window_id: NEXT_WINDOW_ID.fetch_add(1, Ordering::Relaxed),
            activity_counter: 1,
            new_tab_url,
            address_input,
            focus_handle,
            #[cfg(target_os = "windows")]
            parent,
            #[cfg(target_os = "windows")]
            webview_env,
            #[cfg(target_os = "windows")]
            webview_error,
            dark: false,
            status_poll_started: false,
        };

        #[cfg(target_os = "windows")]
        if run_self_test
            && std::env::var("AMARE_GPUI_SELF_TEST").ok().as_deref() == Some("1")
        {
            if let Some(view) = app.active_webview() {
                let _ = view.run_self_test();
            }
        }

        #[cfg(target_os = "windows")]
        if let Some(webview) = app.active_webview() {
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
        let new_tab_url = std::env::var("AMARE_GPUI_NEW_TAB_URL")
            .unwrap_or_else(|_| "about:blank".to_string());
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
            new_tab_url,
            address_input: cx.new(|cx| AddressInput::new(address, cx)),
            focus_handle,
            #[cfg(target_os = "windows")]
            parent,
            #[cfg(target_os = "windows")]
            webview_env,
            #[cfg(target_os = "windows")]
            webview_error: None,
            dark: false,
            status_poll_started: false,
        };
        #[cfg(target_os = "windows")]
        if let (Some(parent), Some(webview)) = (app.parent, app.active_webview_mut()) {
            if webview.reparent(parent).is_ok() {
                webview.set_active(true);
                webview.focus();
            }
        }
        app
    }

    fn start_status_poll(&mut self, cx: &mut Context<Self>) {
        cx.spawn(async move |this, cx| loop {
            Timer::after(Duration::from_millis(250)).await;
            if this.upgrade().is_none() {
                break;
            }
            this.update(cx, |app, cx| {
                app.write_status(cx);
                cx.notify();
            });
        })
        .detach();
    }

    fn write_status(&mut self, cx: &mut Context<Self>) {
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
                    "last_active": tab.last_active
                })
            })
            .collect::<Vec<_>>();
        let payload = serde_json::json!({
            "address": address,
            "marked_range": marked,
            "selected_range": [selected.start, selected.end],
            "source": source,
            "title": title,
            "window_id": self.window_id,
            "active_tab": active.map(|tab| tab.id),
            "tabs": tabs,
            "ime_context": ime_context,
            "pid": std::process::id()
        });
        if let Some(parent) = std::path::Path::new(&path).parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        let _ = std::fs::write(path, serde_json::to_vec_pretty(&payload).unwrap_or_default());
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
        self.tabs.get_mut(self.active).and_then(|tab| tab.webview.as_mut())
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

    fn refresh_tabs(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        #[cfg(target_os = "windows")]
        for tab in &mut self.tabs {
            let Some(webview) = &tab.webview else {
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
            }
            tab.can_go_back = webview.can_go_back();
            tab.can_go_forward = webview.can_go_forward();
            let (loading, error) = webview.signals();
            tab.loading = loading;
            tab.error = error.map(Into::into);
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
        let url = normalize_url(&self.address_value(cx));
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
        let url = self.new_tab_url.clone();
        self.new_tab_with_url(url, window, cx, true);
    }

    fn new_tab_with_url(
        &mut self,
        url: String,
        window: &mut Window,
        cx: &mut Context<Self>,
        focus_address: bool,
    ) {
        let (webview, error) = self.create_webview();
        let id = self.next_tab_id;
        self.next_tab_id += 1;
        self.activity_counter += 1;
        let error = error.map(Into::into);
        self.tabs.push(TabState {
            id,
            title: "New Tab".into(),
            url: url.clone().into(),
            loading: true,
            error,
            can_go_back: false,
            can_go_forward: false,
            favicon: None,
            last_active: self.activity_counter,
            #[cfg(target_os = "windows")]
            webview,
        });
        self.active = self.tabs.len() - 1;
        self.set_address(url.clone(), cx);
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.tabs[self.active].webview {
            webview.navigate(&url);
            webview.set_active(true);
            if !focus_address {
                webview.focus();
            }
        }
        if focus_address {
            self.focus_address_input(window, cx);
        }
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
            .unwrap_or_else(|_| "https://example.com/".to_string());
        let bounds = Bounds::centered(None, size(px(1180.0), px(760.0)), cx);
        let _ = cx.open_window(
            WindowOptions {
                window_bounds: Some(WindowBounds::Windowed(bounds)),
                titlebar: Some(gpui::TitlebarOptions {
                    title: Some("AMARE".into()),
                    ..Default::default()
                }),
                ..Default::default()
            },
            move |window, cx| cx.new(|cx| AmareApp::new_with_url(window, cx, url, false)),
        );
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
        let url = self.active_tab().map(|tab| tab.url.to_string()).unwrap_or_default();
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

    fn close_tab(&mut self, id: u64, cx: &mut Context<Self>) {
        let Some(index) = self.tabs.iter().position(|tab| tab.id == id) else {
            return;
        };
        if self.tabs.len() == 1 {
            cx.quit();
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
        let url = self.active_tab().map(|tab| tab.url.to_string()).unwrap_or_default();
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

    fn close_active_tab(&mut self, cx: &mut Context<Self>) {
        if let Some(tab) = self.active_tab() {
            let id = tab.id;
            self.close_tab(id, cx);
        }
    }

    fn focus_address(
        &mut self,
        _: &FocusAddress,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.address_input
            .update(cx, |input, cx| input.select_all_text(cx));
        self.focus_address_input(window, cx);
        cx.notify();
    }

    fn submit_address(
        &mut self,
        _: &SubmitAddress,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.navigate(cx);
        cx.notify();
    }

    fn cancel_address(
        &mut self,
        _: &CancelAddress,
        _window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let url = self.active_tab().map(|tab| tab.url.to_string()).unwrap_or_default();
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
        self.new_tab(window, cx);
    }

    fn close_tab_action(&mut self, _: &CloseTab, _window: &mut Window, cx: &mut Context<Self>) {
        self.close_active_tab(cx);
    }

    fn new_window_action(&mut self, _: &NewWindow, _window: &mut Window, cx: &mut Context<Self>) {
        self.open_new_window(cx);
    }

    fn next_tab_action(&mut self, _: &NextTab, window: &mut Window, cx: &mut Context<Self>) {
        if self.tabs.len() > 1 {
            let next = (self.active + 1) % self.tabs.len();
            self.select_tab(next, window, cx);
        }
    }

    fn previous_tab_action(&mut self, _: &PreviousTab, window: &mut Window, cx: &mut Context<Self>) {
        if self.tabs.len() > 1 {
            let previous = if self.active == 0 { self.tabs.len() - 1 } else { self.active - 1 };
            self.select_tab(previous, window, cx);
        }
    }

    fn move_tab_before(&mut self, dragged_id: u64, target_id: u64, cx: &mut Context<Self>) {
        if dragged_id == target_id { return; }
        let Some(from) = self.tabs.iter().position(|tab| tab.id == dragged_id) else { return; };
        let Some(mut target) = self.tabs.iter().position(|tab| tab.id == target_id) else { return; };
        let active_id = self.active_tab().map(|tab| tab.id);
        let tab = self.tabs.remove(from);
        if from < target { target -= 1; }
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
            let options = WindowOptions {
                window_bounds: Some(WindowBounds::Windowed(Bounds::centered(
                    None,
                    size(px(1180.0), px(760.0)),
                    cx,
                ))),
                titlebar: Some(gpui::TitlebarOptions {
                    title: Some("AMARE".into()),
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
                let url = self.active_tab().map(|tab| tab.url.to_string()).unwrap_or_default();
                self.set_address(url, cx);
            }
            cx.notify();
        }
        #[cfg(not(target_os = "windows"))]
        let _ = (window, cx);
    }

    fn sync_webview_bounds(&self, window: &Window) {
        #[cfg(target_os = "windows")]
        if let Some(webview) = self.active_webview() {
            let viewport = window.viewport_size();
            let scale = window.scale_factor();
            let width = (f32::from(viewport.width) * scale).round().max(1.0) as i32;
            let height = (f32::from(viewport.height) * scale).round().max(1.0) as i32;
            let toolbar = (TOOLBAR_HEIGHT * scale).round() as i32;
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
        self.process_popup_requests(window, cx);
        self.refresh_tabs(window, cx);
        self.sync_webview_bounds(window);
        let dark = self.dark;
        let background = if dark { rgb(0x111318) } else { rgb(0xf7f8fb) };
        let surface = if dark { rgb(0x1c1f27) } else { rgb(0xffffff) };
        let muted = if dark { rgb(0xa3a8b8) } else { rgb(0x5d6474) };
        let border = if dark { rgb(0x303543) } else { rgb(0xdde1ea) };
        let accent = rgb(0x2f7df6);
        let can_back = self.active_tab().map(|tab| tab.can_go_back).unwrap_or(false);
        let can_forward = self
            .active_tab()
            .map(|tab| tab.can_go_forward)
            .unwrap_or(false);
        let source_window_id = self.window_id;
        let tabs = self
            .tabs
            .iter()
            .map(|tab| {
                let active = tab.id == self.active_tab().map(|active| active.id).unwrap_or(0);
                let id = tab.id;
                let click = cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, window, cx| {
                    this.select_tab_by_id(id, window, cx);
                });
                let close = cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                    this.close_tab(id, cx);
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
                    .w(px(120.0))
                    .h(px(34.0))
                    .rounded_md()
                    .bg(if active { surface } else { background })
                    .border_1()
                    .border_color(if active { border } else { background })
                    .text_sm()
                    .text_color(if active { accent } else { muted })
                    .child(
                        div()
                            .max_w(px(70.0))
                            .overflow_hidden()
                            .text_ellipsis()
                            .child(tab.title.clone()),
                    )
                    .child(if tab.loading { "◌" } else { "" })
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
            .on_action(cx.listener(Self::focus_address))
            .on_action(cx.listener(Self::submit_address))
            .on_action(cx.listener(Self::cancel_address))
            .on_action(cx.listener(Self::reload_page))
            .on_action(cx.listener(Self::go_back))
            .on_action(cx.listener(Self::go_forward))
            .on_action(cx.listener(Self::new_tab_action))
            .on_action(cx.listener(Self::close_tab_action))
            .on_action(cx.listener(Self::new_window_action))
            .on_action(cx.listener(Self::next_tab_action))
            .on_action(cx.listener(Self::previous_tab_action))
            .flex()
            .flex_col()
            .size_full()
            .bg(background)
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
                                this.open_new_window(cx);
                            })),
                    ),
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
                                this.dark = !this.dark;
                                cx.notify();
                            })),
                    ),
            )
            .child(
                div()
                    .flex()
                    .flex_col()
                    .size_full()
                    .bg(surface)
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
    }
}

fn normalize_url(input: &str) -> String {
    let value = input.trim();
    if value.is_empty() {
        return "about:blank".into();
    }
    if value.contains("://") || value.starts_with("about:") {
        return value.into();
    }
    if value.contains('.') && !value.contains(' ') {
        return format!("https://{value}");
    }
    format!("https://www.google.com/search?q={}", url_encode(value))
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
        let bounds = Bounds::centered(None, size(px(1180.0), px(760.0)), cx);
        cx.open_window(
            WindowOptions {
                window_bounds: Some(WindowBounds::Windowed(bounds)),
                titlebar: Some(gpui::TitlebarOptions {
                    title: Some("AMARE".into()),
                    ..Default::default()
                }),
                ..Default::default()
            },
            |window, cx| cx.new(|cx| AmareApp::new(window, cx)),
        )
        .expect("failed to open AMARE window");
        cx.activate(true);
    });
}
