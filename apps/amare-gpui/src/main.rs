use gpui::{
    App, Application, Bounds, Context, FocusHandle, KeyDownEvent, Render, SharedString, Window,
    WindowBounds, WindowOptions, div, prelude::*, px, rgb, size,
};
use raw_window_handle::{HasWindowHandle, RawWindowHandle};

mod webview;

const TOOLBAR_HEIGHT: f32 = 88.0;

struct TabState {
    title: SharedString,
    url: SharedString,
}

struct AmareApp {
    tabs: Vec<TabState>,
    active: usize,
    address: SharedString,
    focus_handle: FocusHandle,
    #[cfg(target_os = "windows")]
    webview: Option<webview::WebViewHost>,
    #[cfg(target_os = "windows")]
    webview_error: Option<String>,
    dark: bool,
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

        Self {
            tabs: vec![TabState {
                title: "New Tab".into(),
                url: "https://example.com/".into(),
            }],
            active: 0,
            address: "https://example.com/".into(),
            focus_handle,
            #[cfg(target_os = "windows")]
            webview,
            #[cfg(target_os = "windows")]
            webview_error,
            dark: false,
        }
    }

    fn active_tab(&self) -> &TabState {
        &self.tabs[self.active.min(self.tabs.len() - 1)]
    }

    fn navigate(&mut self) {
        let url = normalize_url(&self.address);
        if let Some(tab) = self.tabs.get_mut(self.active) {
            tab.url = url.clone().into();
            tab.title = title_for(&url).into();
        }
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.webview {
            webview.navigate(&url);
        }
        self.address = url.into();
    }

    fn new_tab(&mut self) {
        self.tabs.push(TabState {
            title: "New Tab".into(),
            url: "about:blank".into(),
        });
        self.active = self.tabs.len() - 1;
        self.address = "about:blank".into();
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.webview {
            webview.navigate("about:blank");
        }
    }

    fn close_active_tab(&mut self) {
        if self.tabs.len() <= 1 {
            return;
        }
        self.tabs.remove(self.active);
        self.active = self.active.min(self.tabs.len() - 1);
        let url = self.active_tab().url.to_string();
        self.address = url.clone().into();
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.webview {
            webview.navigate(&url);
        }
    }

    fn select_tab(&mut self, index: usize) {
        if index >= self.tabs.len() {
            return;
        }
        self.active = index;
        let url = self.active_tab().url.to_string();
        self.address = url.clone().into();
        #[cfg(target_os = "windows")]
        if let Some(webview) = &self.webview {
            webview.navigate(&url);
        }
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
                    this.select_tab(index);
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
            .on_key_down(cx.listener(|this: &mut Self, event: &KeyDownEvent, window, cx| {
                let key = event.keystroke.key.as_str();
                if event.keystroke.modifiers.control && key.eq_ignore_ascii_case("t") {
                    this.new_tab();
                } else if event.keystroke.modifiers.control && key.eq_ignore_ascii_case("l") {
                    this.address = "".into();
                    this.focus_handle.focus(window);
                } else if event.keystroke.modifiers.control && key.eq_ignore_ascii_case("w") {
                    this.close_active_tab();
                } else if key == "enter" {
                    this.navigate();
                } else if key == "backspace" {
                    let mut address = this.address.to_string();
                    address.pop();
                    this.address = address.into();
                } else if let Some(text) = &event.keystroke.key_char {
                    if !event.keystroke.modifiers.control && !event.keystroke.modifiers.alt {
                        this.address = format!("{}{}", this.address, text).into();
                    }
                }
                cx.notify();
            }))
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
                                this.new_tab();
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
                            .px_3()
                            .rounded_md()
                            .bg(background)
                            .border_1()
                            .border_color(border)
                            .text_sm()
                            .text_color(muted)
                            .child(self.address.clone())
                            .on_click(cx.listener(|this: &mut Self, _, window, cx| {
                                this.focus_handle.focus(window);
                                cx.notify();
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
    Application::new().run(|cx: &mut App| {
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
