#[cfg(target_os = "windows")]
use std::sync::mpsc;

#[cfg(target_os = "windows")]
use webview2_com::{
    AcceleratorKeyPressedEventHandler, CreateCoreWebView2ControllerCompletedHandler,
    CreateCoreWebView2EnvironmentCompletedHandler, ExecuteScriptCompletedHandler,
    NavigationCompletedEventHandler, take_pwstr, wait_with_pump,
    Microsoft::Web::WebView2::Win32::{
        COREWEBVIEW2_KEY_EVENT_KIND_KEY_DOWN, CreateCoreWebView2Environment, ICoreWebView2,
        ICoreWebView2Controller,
    },
};
#[cfg(target_os = "windows")]
use windows::{
    core::HSTRING,
    Win32::{
        Foundation::{E_POINTER, HWND, LPARAM, RECT, WPARAM},
        System::Com::{CoInitializeEx, COINIT_APARTMENTTHREADED},
        UI::{
            Input::{
                Ime::{
                    HIMC, IACE_DEFAULT, ImmAssociateContext, ImmAssociateContextEx,
                    ImmCreateContext, ImmGetContext,
                },
                KeyboardAndMouse::{
                    ActivateKeyboardLayout, GetKeyState, KLF_ACTIVATE, KLF_SETFORPROCESS,
                    LoadKeyboardLayoutW, SetFocus, VK_CONTROL, VK_L, VK_LEFT, VK_MENU, VK_R,
                    VK_RIGHT,
                },
            },
            WindowsAndMessaging::{
                FindWindowExW, PostMessageW, WM_KEYDOWN, WM_KEYUP,
            },
        },
    },
};

#[cfg(target_os = "windows")]
pub fn activate_korean_layout() {
    unsafe {
        if let Ok(hkl) = LoadKeyboardLayoutW(
            windows::core::w!("00000412"),
            KLF_ACTIVATE,
        ) {
            let _ = ActivateKeyboardLayout(hkl, KLF_SETFORPROCESS);
        }
    }
}

pub struct WebViewHost {
    controller: ICoreWebView2Controller,
    webview: ICoreWebView2,
    parent: HWND,
    accelerator_token: i64,
}

#[cfg(target_os = "windows")]
fn write_status(webview: &ICoreWebView2, path: &str) {
    let source = unsafe {
        let mut value = windows::core::PWSTR::null();
        if webview.Source(&mut value).is_err() { String::new() } else { take_pwstr(value) }
    };
    let title = unsafe {
        let mut value = windows::core::PWSTR::null();
        if webview.DocumentTitle(&mut value).is_err() { String::new() } else { take_pwstr(value) }
    };
    let payload = serde_json::json!({
        "source": source,
        "title": title,
        "pid": std::process::id()
    });
    if let Some(parent) = std::path::Path::new(path).parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    let _ = std::fs::write(path, serde_json::to_vec_pretty(&payload).unwrap_or_default());
}

#[cfg(target_os = "windows")]
impl WebViewHost {
    pub fn create(parent: HWND) -> std::result::Result<Self, String> {
        unsafe {
            let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
            if std::env::var("AMARE_GPUI_KOREAN_IME").ok().as_deref() == Some("1") {
                activate_korean_layout();
            }
            let ime_context = ImmCreateContext();
            if !ime_context.0.is_null() {
                let _ = ImmAssociateContext(parent, ime_context);
            }
        }
        let environment = {
            let (tx, rx) = mpsc::channel();
            CreateCoreWebView2EnvironmentCompletedHandler::wait_for_async_operation(
                Box::new(|handler| unsafe {
                    CreateCoreWebView2Environment(&handler).map_err(webview2_com::Error::WindowsError)
                }),
                Box::new(move |error, environment| {
                    error?;
                    tx.send(environment.ok_or_else(|| windows::core::Error::from(E_POINTER)))
                        .map_err(|_| windows::core::Error::from(E_POINTER))?;
                    Ok(())
                }),
            )
            .map_err(|error| format!("WebView2 environment: {error:?}"))?;
            rx.recv().map_err(|_| "WebView2 environment channel closed".to_string())?
        }
        .map_err(|error| format!("WebView2 environment: {error:?}"))?;

        let controller = {
            let (tx, rx) = mpsc::channel();
            CreateCoreWebView2ControllerCompletedHandler::wait_for_async_operation(
                Box::new(move |handler| unsafe {
                    environment
                        .CreateCoreWebView2Controller(parent, &handler)
                        .map_err(webview2_com::Error::WindowsError)
                }),
                Box::new(move |error, controller| {
                    error?;
                    tx.send(controller.ok_or_else(|| windows::core::Error::from(E_POINTER)))
                        .map_err(|_| windows::core::Error::from(E_POINTER))?;
                    Ok(())
                }),
            )
            .map_err(|error| format!("WebView2 controller: {error:?}"))?;
            rx.recv().map_err(|_| "WebView2 controller channel closed".to_string())?
        }
        .map_err(|error| format!("WebView2 controller: {error:?}"))?;

        unsafe {
            controller.SetIsVisible(true).map_err(|error| format!("WebView2 visible: {error:?}"))?;
            let webview = controller.CoreWebView2().map_err(|error| format!("WebView2 core: {error:?}"))?;
            let self_test = std::env::var("AMARE_GPUI_SELF_TEST").ok().as_deref() == Some("1");
            let mut token = 0;
            let (initial_tx, initial_rx) = mpsc::channel();
            let initial_handler = NavigationCompletedEventHandler::create(Box::new(move |_, _| {
                let _ = initial_tx.send(());
                Ok(())
            }));
            if self_test {
                webview.add_NavigationCompleted(&initial_handler, &mut token)
                    .map_err(|error| format!("initial navigation handler: {error:?}"))?;
            }
            let initial_url = std::env::var("AMARE_GPUI_START_URL")
                .unwrap_or_else(|_| "https://example.com/".to_string());
            webview.Navigate(&HSTRING::from(initial_url))
                .map_err(|error| format!("WebView2 navigate: {error:?}"))?;
            if self_test {
                let _ = wait_with_pump(initial_rx);
                let _ = webview.remove_NavigationCompleted(token);
            }
            if let Ok(status_path) = std::env::var("AMARE_GPUI_STATUS") {
                let status_webview = webview.clone();
                let status_path_for_handler = status_path.clone();
                let status_handler = NavigationCompletedEventHandler::create(Box::new(move |_, _| {
                    write_status(&status_webview, &status_path_for_handler);
                    Ok(())
                }));
                let mut status_token = 0;
                let _ = webview.add_NavigationCompleted(&status_handler, &mut status_token);
                write_status(&webview, &status_path);
            }
            let accelerator_token = {
                let accelerator_parent = parent;
                let accelerator_handler = AcceleratorKeyPressedEventHandler::create(Box::new(
                    move |_controller, args| {
                        let Some(args) = args else { return Ok(()) };
                        let mut kind = COREWEBVIEW2_KEY_EVENT_KIND_KEY_DOWN;
                        args.KeyEventKind(&mut kind)?;
                        if kind == COREWEBVIEW2_KEY_EVENT_KIND_KEY_DOWN {
                            let mut vk = 0u32;
                            args.VirtualKey(&mut vk)?;
                            let control = GetKeyState(VK_CONTROL.0 as i32) < 0;
                            let alt = GetKeyState(VK_MENU.0 as i32) < 0;
                            let forwarded = if control && vk == VK_L.0 as u32 {
                                Some(VK_L.0)
                            } else if control && vk == VK_R.0 as u32 {
                                Some(VK_R.0)
                            } else if alt && vk == VK_LEFT.0 as u32 {
                                Some(VK_LEFT.0)
                            } else if alt && vk == VK_RIGHT.0 as u32 {
                                Some(VK_RIGHT.0)
                            } else {
                                None
                            };
                            if let Some(key) = forwarded {
                                let mut lparam = 0i32;
                                args.KeyEventLParam(&mut lparam)?;
                                unsafe {
                                    let _ = SetFocus(Some(accelerator_parent));
                                    let _ = PostMessageW(
                                        Some(accelerator_parent),
                                        WM_KEYDOWN,
                                        WPARAM(key as usize),
                                        LPARAM(lparam as isize),
                                    );
                                    let _ = PostMessageW(
                                        Some(accelerator_parent),
                                        WM_KEYUP,
                                        WPARAM(key as usize),
                                        LPARAM(lparam as isize),
                                    );
                                }
                                args.SetHandled(true)?;
                            }
                        }
                        Ok(())
                    },
                ));
                let mut token = 0;
                controller.add_AcceleratorKeyPressed(&accelerator_handler, &mut token)
                    .map_err(|error| format!("accelerator handler: {error:?}"))?;
                token
            };
            Ok(Self { controller, webview, parent, accelerator_token })
        }
    }

    pub fn run_self_test(&self) -> Result<(), String> {
        let result = self.run_self_test_inner();
        if let Ok(path) = std::env::var("AMARE_GPUI_RESULT") {
            let payload = match &result {
                Ok(value) => value.clone(),
                Err(error) => serde_json::json!({
                    "success": false,
                    "error": error,
                    "source": self.source(),
                    "title": self.title()
                }),
            };
            if let Some(parent) = std::path::Path::new(&path).parent() {
                let _ = std::fs::create_dir_all(parent);
            }
            let _ = std::fs::write(path, serde_json::to_vec_pretty(&payload).unwrap_or_default());
        }
        result.map(|_| ())
    }

    fn run_self_test_inner(&self) -> Result<serde_json::Value, String> {
        let started = std::time::Instant::now();
        self.navigate_and_wait("https://example.com/", 20)?;
        let source = self.source();
        let title = self.title();
        let script = self.execute_script("document.body ? document.body.innerText : ''")?;
        let has_content = script.to_lowercase().contains("example domain");
        let page_ok = source.contains("example.com") && title.eq_ignore_ascii_case("Example Domain");
        self.navigate_and_wait("https://example.com/?amare=gpui", 20)?;
        self.wait_for_action(|| unsafe {
            self.webview.GoBack().map_err(|error| format!("GoBack: {error:?}"))
        })?;
        let back_source = self.source();
        let back_ok = back_source.contains("example.com");
        self.wait_for_action(|| unsafe {
            self.webview.Reload().map_err(|error| format!("Reload: {error:?}"))
        })?;
        Ok(serde_json::json!({
            "success": page_ok && back_ok,
            "source": source,
            "title": title,
            "script_result": script,
            "back_source": back_source,
            "has_example_domain_content": has_content,
            "back_navigation": back_ok,
            "elapsed_ms": started.elapsed().as_millis() as u64
        }))
    }

    fn navigate_and_wait(&self, url: &str, timeout_seconds: u64) -> Result<(), String> {
        let (tx, rx) = mpsc::channel();
        let handler = NavigationCompletedEventHandler::create(Box::new(move |_, _| {
            let _ = tx.send(());
            Ok(())
        }));
        let mut token = 0;
        unsafe {
            self.webview.add_NavigationCompleted(&handler, &mut token)
                .map_err(|error| format!("add_NavigationCompleted: {error:?}"))?;
            self.webview.Navigate(&HSTRING::from(url))
                .map_err(|error| format!("Navigate: {error:?}"))?;
        }
        let result = wait_with_pump(rx);
        unsafe {
            let _ = self.webview.remove_NavigationCompleted(token);
        }
        result.map_err(|_| "navigation did not complete".to_string())?;
        let _ = timeout_seconds;
        Ok(())
    }

    fn wait_for_action(&self, action: impl FnOnce() -> Result<(), String>) -> Result<(), String> {
        let (tx, rx) = mpsc::channel();
        let handler = NavigationCompletedEventHandler::create(Box::new(move |_, _| {
            let _ = tx.send(());
            Ok(())
        }));
        let mut token = 0;
        unsafe {
            self.webview.add_NavigationCompleted(&handler, &mut token)
                .map_err(|error| format!("add_NavigationCompleted: {error:?}"))?;
        }
        let action_result = action();
        let result = if action_result.is_ok() { wait_with_pump(rx) } else { Ok(()) };
        unsafe {
            let _ = self.webview.remove_NavigationCompleted(token);
        }
        action_result?;
        result.map_err(|_| "navigation did not complete".to_string())
    }

    pub fn source(&self) -> String {
        unsafe {
            let mut value = windows::core::PWSTR::null();
            if self.webview.Source(&mut value).is_err() { return String::new(); }
            take_pwstr(value)
        }
    }

    pub fn title(&self) -> String {
        unsafe {
            let mut value = windows::core::PWSTR::null();
            if self.webview.DocumentTitle(&mut value).is_err() { return String::new(); }
            take_pwstr(value)
        }
    }

    fn execute_script(&self, script: &str) -> Result<String, String> {
        let (tx, rx) = mpsc::channel();
        let webview = self.webview.clone();
        let script = HSTRING::from(script);
        ExecuteScriptCompletedHandler::wait_for_async_operation(
            Box::new(move |handler| unsafe {
                webview.ExecuteScript(&script, &handler)
                    .map_err(webview2_com::Error::WindowsError)
            }),
            Box::new(move |error, result| {
                error?;
                let _ = tx.send(result);
                Ok(())
            }),
        ).map_err(|error| format!("ExecuteScript: {error:?}"))?;
        rx.recv().map_err(|_| "ExecuteScript channel closed".to_string())
    }

    pub fn set_bounds(&self, left: i32, top: i32, width: i32, height: i32) {
        let rect = RECT { left, top, right: width, bottom: height };
        unsafe {
            let _ = self.controller.SetBounds(rect);
        }
    }

    pub fn navigate(&self, url: &str) {
        unsafe {
            let _ = self.webview.Navigate(&HSTRING::from(url));
        }
    }

    pub fn back(&self) {
        unsafe {
            let _ = self.webview.GoBack();
        }
    }

    pub fn forward(&self) {
        unsafe {
            let _ = self.webview.GoForward();
        }
    }

    pub fn reload(&self) {
        unsafe {
            let _ = self.webview.Reload();
        }
    }

    pub fn ensure_ime(&self) {
        unsafe {
            if ImmGetContext(self.parent).0.is_null() {
                let context = ImmCreateContext();
                let created = !context.0.is_null();
                let previous = ImmAssociateContext(self.parent, context);
                let after_create = ImmGetContext(self.parent).0 as usize;
                let _ = ImmAssociateContextEx(self.parent, HIMC::default(), IACE_DEFAULT);
                let after_default = ImmGetContext(self.parent).0 as usize;
                if let Ok(path) = std::env::var("AMARE_GPUI_IME_LOG") {
                    let payload = format!(
                        "parent={:?} created={} context={:?} previous={:?} after_create={} after_default={}\n",
                        self.parent, created, context.0, previous.0, after_create, after_default
                    );
                    let _ = std::fs::OpenOptions::new().create(true).append(true).open(path).and_then(|mut file| std::io::Write::write_all(&mut file, payload.as_bytes()));
                }
            }
        }
    }

    pub fn ime_context(&self) -> bool {
        unsafe { !ImmGetContext(self.parent).0.is_null() }
    }

    pub fn focus(&self) {
        unsafe {
            for class in [
                windows::core::w!("Chrome_WidgetWin_0"),
                windows::core::w!("Chrome_WidgetWin_1"),
            ] {
                if let Ok(child) = FindWindowExW(Some(self.parent), None, class, None) {
                    if !child.is_invalid() {
                        let _ = SetFocus(Some(child));
                        return;
                    }
                }
            }
            let _ = SetFocus(Some(self.parent));
        }
    }
}

#[cfg(target_os = "windows")]
impl Drop for WebViewHost {
    fn drop(&mut self) {
        unsafe {
            let _ = self.controller.Close();
        }
    }
}
