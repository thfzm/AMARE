use std::time::Duration;

use gpui::{
    App, Application, Bounds, Context, FocusHandle, Focusable, KeyBinding, Render, SharedString, Timer,
    Window, WindowBounds, WindowOptions, actions, div, prelude::*, px, rgb, size,
};
use raw_window_handle::{HasWindowHandle, RawWindowHandle};

mod text_input;
mod webview;

use text_input::AddressInput;

actions!(
    amare,
    [
        FocusAddress,
        ReloadPage,
        BackPage,
        ForwardPage,
        SubmitAddress,
        CancelAddress,
    ]
);

const TOOLBAR_HEIGHT: f32 = 88.0;

struct TabState {
    title: SharedString,
    url: SharedString,
}

struct AmareApp {
    tabs: Vec<TabState>,
    active: usize,
    address_input: gpui::Entity<AddressInput>,
    focus_handle: FocusHandle,
    #[cfg(target_os = "windows")]
    webview: Option<webview::WebViewHost>,
    #[cfg(target_os = "windows")]
    webview_error: Option<String>,
    dark: bool,
    status_poll_started: bool,
}

impl AmareApp {
    fn new(window: &mut Window, cx: &mut App) -> Self {
        let focus_handle = cx.focus_handle();
        focus_handle.focus(window);

        #[cfg(target_os = "windows")]
        let self_test = std::env::var("AMARE_GPUI_SELF_TEST").ok().as_deref() == Some("1");
        #[cfg(target_os = "windows")]
        let (webview, webview_error) = match window.window_handle().ok().map(|handle| handle.as_raw()) {
            Some(RawWindowHandle::Win32(handle)) => {
                let hwnd = windows::Win32::Foundation::HWND(handle.hwnd.get() as *mut _);
                match webview::WebViewHost::create(hwnd) {
                    Ok(view) => (Some(view), None),
                    Err(error) => (None, Some(error)),
                }
            }
            _ => (None, Some("Windows HWND unavailable".to_string())),
        };

        #[cfg(target_os = "windows")]
        if self_test {
            if let Some(view) = &webview {
                let _ = view.run_self_test();
            }
        }

        let start_url = std::env::var("AMARE_GPUI_START_URL")
            .unwrap_or_else(|_| "https://example.com/".to_string());
        let address_input = cx.new(|cx| AddressInput::new(start_url.clone(), cx));
        let app = Self {
            tabs: vec![TabState {
                title: "New Tab".into(),
                url: start_url.into(),
            }],
            active: 0,
            address_input,
            focus_handle,
            #[cfg(target_os = "windows")]
            webview,
            #[cfg(target_os = "windows")]
            webview_error,
            dark: false,
            status_poll_started: false,
        };
        let address_focus = app.address_input.read(cx).focus_handle(cx);
        window.focus(&address_focus);
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
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.webview {
            webview.ensure_ime();
        }
        let input = self.address_input.read(cx);
        let length = input.content().len();
        let address = input.content();
        let marked = input
            .marked_range()
            .map(|range| serde_json::json!([range.start, range.end]));
        let selected = input.selected_range();
        #[cfg(target_os = "windows")]
        let (source, title) = self
            .webview
            .as_ref()
            .map(|webview| (webview.source(), webview.title()))
            .unwrap_or_default();
        #[cfg(not(target_os = "windows"))]
        let (source, title) = (String::new(), String::new());
        let payload = serde_json::json!({
            "address": address,
            "address_len": length,
            "marked_range": marked,
            "selected_range": [selected.start, selected.end],
            "source": source,
            "title": title,
            "ime_context": self.webview.as_ref().map(|webview| webview.ime_context()).unwrap_or(false),
            "pid": std::process::id()
        });
        if let Some(parent) = std::path::Path::new(&path).parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        let _ = std::fs::write(path, serde_json::to_vec_pretty(&payload).unwrap_or_default());
    }

    fn set_address(&mut self, value: impl Into<SharedString>, cx: &mut Context<Self>) {
        let value = value.into();
        self.address_input
            .update(cx, |input, cx| input.set_content(value, cx));
    }

    fn address_value(&self, cx: &App) -> String {
        self.address_input.read(cx).content()
    }

    fn refresh_from_webview(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        #[cfg(target_os = "windows")]
        let Some(webview) = &self.webview else {
            return;
        };
        #[cfg(not(target_os = "windows"))]
        return;
        #[cfg(target_os = "windows")]
        {
            let source = webview.source();
            if source.is_empty() {
                return;
            }
            let title = webview.title();
            if let Some(tab) = self.tabs.get_mut(self.active) {
                if tab.url.as_ref() != source {
                    tab.url = source.clone().into();
                    tab.title = if title.is_empty() {
                        title_for(&source).into()
                    } else {
                        title.into()
                    };
                }
            }
            let focused = self.address_input.read(cx).is_focused(window);
            if !focused && self.address_value(cx) != source {
                self.set_address(source, cx);
            }
        }
    }

    fn active_tab(&self) -> &TabState {
        &self.tabs[self.active.min(self.tabs.len() - 1)]
    }

    fn navigate(&mut self, cx: &mut Context<Self>) {
        let url = normalize_url(&self.address_value(cx));
        if let Some(tab) = self.tabs.get_mut(self.active) {
            tab.url = url.clone().into();
            tab.title = title_for(&url).into();
        }
        self.set_address(url.clone(), cx);
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.webview {
            webview.navigate(&url);
            webview.focus();
        }
    }

    fn new_tab(&mut self, cx: &mut Context<Self>) {
        self.tabs.push(TabState {
            title: "New Tab".into(),
            url: "about:blank".into(),
        });
        self.active = self.tabs.len() - 1;
        self.set_address("about:blank", cx);
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.webview {
            webview.navigate("about:blank");
        }
    }

    fn close_active_tab(&mut self, cx: &mut Context<Self>) {
        if self.tabs.len() <= 1 {
            return;
        }
        self.tabs.remove(self.active);
        self.active = self.active.min(self.tabs.len() - 1);
        let url = self.active_tab().url.to_string();
        self.set_address(url.clone(), cx);
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.webview {
            webview.navigate(&url);
        }
    }

    fn select_tab(&mut self, index: usize, cx: &mut Context<Self>) {
        if index >= self.tabs.len() {
            return;
        }
        self.active = index;
        let url = self.active_tab().url.to_string();
        self.set_address(url.clone(), cx);
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.webview {
            webview.navigate(&url);
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
        let handle = self.address_input.read(cx).focus_handle(cx);
        window.focus(&handle);
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
        let url = self.active_tab().url.to_string();
        self.set_address(url, cx);
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.webview {
            webview.focus();
        }
        cx.notify();
    }

    fn reload_page(&mut self, _: &ReloadPage, _window: &mut Window, cx: &mut Context<Self>) {
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.webview {
            webview.reload();
            webview.focus();
        }
        cx.notify();
    }

    fn go_back(&mut self, _: &BackPage, _window: &mut Window, cx: &mut Context<Self>) {
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.webview {
            webview.back();
            webview.focus();
        }
        cx.notify();
    }

    fn go_forward(&mut self, _: &ForwardPage, _window: &mut Window, cx: &mut Context<Self>) {
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.webview {
            webview.forward();
            webview.focus();
        }
        cx.notify();
    }

    fn sync_webview_bounds(&self, window: &Window) {
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.webview {
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
        self.refresh_from_webview(window, cx);
        self.sync_webview_bounds(window);
        let dark = self.dark;
        let background = if dark { rgb(0x111318) } else { rgb(0xf7f8fb) };
        let surface = if dark { rgb(0x1c1f27) } else { rgb(0xffffff) };
        let muted = if dark { rgb(0xa3a8b8) } else { rgb(0x5d6474) };
        let border = if dark { rgb(0x303543) } else { rgb(0xdde1ea) };
        let accent = rgb(0x2f7df6);
        let tabs = self
            .tabs
            .iter()
            .enumerate()
            .map(|(index, tab)| {
                let active = index == self.active;
                let click = cx.listener(move |this: &mut Self, _: &gpui::ClickEvent, _, cx| {
                    this.select_tab(index, cx);
                    cx.notify();
                });
                div()
                    .id(SharedString::from(format!("tab-{index}")))
                    .flex()
                    .items_center()
                    .gap_2()
                    .px_3()
                    .h(px(34.0))
                    .rounded_md()
                    .bg(if active { surface } else { background })
                    .border_1()
                    .border_color(if active { border } else { background })
                    .text_sm()
                    .text_color(if active { accent } else { muted })
                    .child(tab.title.clone())
                    .on_click(click)
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
                            .overflow_hidden()
                            .child(div().flex().gap_1().children(tabs))
                    )
                    .child(
                        div()
                            .id("new-tab")
                            .flex()
                            .items_center()
                            .justify_center()
                            .size(px(30.0))
                            .rounded_md()
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .text_lg()
                            .child("+")
                            .on_click(cx.listener(|this: &mut Self, _, _, cx| {
                                this.new_tab(cx);
                                cx.notify();
                            })),
                    )
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
                            .hover(|style| style.bg(border))
                            .cursor_pointer()
                            .child("‹")
                            .on_click(cx.listener(|this: &mut Self, _, _, cx| {
                                #[cfg(target_os = "windows")]
                                if let Some(webview) = &this.webview {
                                    webview.back();
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
                    )
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
                                    "WebView2 content is hosted in the native child window below the toolbar.".into()
                                })),
                        )
                    })
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
    #[cfg(target_os = "windows")]
    if std::env::var("AMARE_GPUI_KOREAN_IME").ok().as_deref() == Some("1") {
        webview::activate_korean_layout();
    }
    Application::new().run(|cx: &mut App| {
        cx.bind_keys([
            KeyBinding::new("ctrl-l", FocusAddress, None),
            KeyBinding::new("ctrl-r", ReloadPage, None),
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
