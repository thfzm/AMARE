#[cfg(target_os = "windows")]
use std::{
    cell::RefCell,
    sync::{Arc, Mutex},
};

#[cfg(target_os = "windows")]
use std::sync::{
    atomic::{AtomicU64, Ordering},
    mpsc,
};

#[cfg(target_os = "windows")]
use webview2_com::{
    AcceleratorKeyPressedEventHandler, AddScriptToExecuteOnDocumentCreatedCompletedHandler,
    ClearBrowsingDataCompletedHandler, CreateCoreWebView2ControllerCompletedHandler,
    CreateCoreWebView2EnvironmentCompletedHandler, DownloadStartingEventHandler,
    ExecuteScriptCompletedHandler,
    Microsoft::Web::WebView2::Win32::{
        COREWEBVIEW2_BROWSING_DATA_KINDS_ALL_SITE, COREWEBVIEW2_DOWNLOAD_STATE_COMPLETED,
        COREWEBVIEW2_DOWNLOAD_STATE_IN_PROGRESS, COREWEBVIEW2_DOWNLOAD_STATE_INTERRUPTED,
        COREWEBVIEW2_KEY_EVENT_KIND_KEY_DOWN, COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC,
        CreateCoreWebView2Environment, ICoreWebView2, ICoreWebView2_4, ICoreWebView2_13,
        ICoreWebView2Controller, ICoreWebView2DownloadOperation, ICoreWebView2Environment,
        ICoreWebView2Profile2, ICoreWebView2Profile6,
    },
    NavigationCompletedEventHandler, NavigationStartingEventHandler,
    NewWindowRequestedEventHandler, WebMessageReceivedEventHandler, take_pwstr, wait_with_pump,
};
#[cfg(target_os = "windows")]
use windows::{
    Win32::{
        Foundation::{E_POINTER, HWND, LPARAM, RECT, WPARAM},
        System::{
            Com::{COINIT_APARTMENTTHREADED, CoInitializeEx},
            Threading::GetCurrentThreadId,
        },
        UI::{
            Input::{
                Ime::{
                    HIMC, IACE_DEFAULT, IME_CONVERSION_MODE, IME_SENTENCE_MODE,
                    ImmAssociateContext, ImmAssociateContextEx, ImmCreateContext, ImmGetContext,
                    ImmReleaseContext, ImmSetConversionStatus, ImmSetOpenStatus,
                },
                KeyboardAndMouse::{
                    GetKeyState, GetKeyboardLayout, SetFocus, VK_B, VK_CONTROL, VK_D, VK_H, VK_L,
                    VK_LEFT, VK_MENU, VK_N, VK_R, VK_RIGHT, VK_SHIFT, VK_T, VK_TAB, VK_W,
                },
            },
            WindowsAndMessaging::{PostMessageW, WM_KEYDOWN, WM_KEYUP},
        },
    },
    core::{HSTRING, Interface},
};

#[cfg(target_os = "windows")]
#[derive(Default)]
pub struct TabSignals {
    pub loading: bool,
    pub error: Option<String>,
}

#[cfg(target_os = "windows")]
pub struct WebViewEnvironment {
    environment: ICoreWebView2Environment,
}

#[cfg(target_os = "windows")]
thread_local! {
    static SHARED_ENVIRONMENT: RefCell<Option<Arc<WebViewEnvironment>>> = const { RefCell::new(None) };
}

#[cfg(target_os = "windows")]
pub fn runtime_version() -> String {
    use webview2_com::Microsoft::Web::WebView2::Win32::GetAvailableCoreWebView2BrowserVersionString;
    use windows::{
        Win32::System::Com::CoTaskMemFree,
        core::{PCWSTR, PWSTR},
    };
    unsafe {
        let mut version = PWSTR::null();
        if GetAvailableCoreWebView2BrowserVersionString(PCWSTR::null(), &mut version).is_ok() {
            let text = version.to_string().unwrap_or_default();
            CoTaskMemFree(Some(version.0 as *const _));
            return text;
        }
    }
    "unknown".to_string()
}

#[cfg(target_os = "windows")]
pub fn shared_environment() -> std::result::Result<Arc<WebViewEnvironment>, String> {
    SHARED_ENVIRONMENT.with(|cell| {
        if let Some(environment) = cell.borrow().as_ref() {
            return Ok(environment.clone());
        }
        let environment = Arc::new(WebViewEnvironment::create()?);
        *cell.borrow_mut() = Some(environment.clone());
        Ok(environment)
    })
}

#[cfg(target_os = "windows")]
impl WebViewEnvironment {
    pub fn create() -> std::result::Result<Self, String> {
        unsafe {
            let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        }
        let environment = {
            let (tx, rx) = mpsc::channel();
            CreateCoreWebView2EnvironmentCompletedHandler::wait_for_async_operation(
                Box::new(|handler| unsafe {
                    CreateCoreWebView2Environment(&handler)
                        .map_err(webview2_com::Error::WindowsError)
                }),
                Box::new(move |error, environment| {
                    error?;
                    tx.send(environment.ok_or_else(|| windows::core::Error::from(E_POINTER)))
                        .map_err(|_| windows::core::Error::from(E_POINTER))?;
                    Ok(())
                }),
            )
            .map_err(|error| format!("WebView2 environment: {error:?}"))?;
            rx.recv()
                .map_err(|_| "WebView2 environment channel closed".to_string())?
        }
        .map_err(|error| format!("WebView2 environment: {error:?}"))?;
        Ok(Self { environment })
    }
}

#[cfg(target_os = "windows")]
#[derive(Clone, Debug)]
pub struct DownloadSnapshot {
    pub id: u64,
    pub filename: String,
    pub url: String,
    pub path: String,
    pub mime_type: String,
    pub total_bytes: i64,
    pub received_bytes: i64,
    pub state: String,
    pub error: String,
}

#[cfg(target_os = "windows")]
struct DownloadState {
    id: u64,
    operation: ICoreWebView2DownloadOperation,
    filename: String,
    url: String,
    path: String,
    mime_type: String,
    total_bytes: i64,
    received_bytes: i64,
    state: String,
    error: String,
}

#[cfg(target_os = "windows")]
static NEXT_DOWNLOAD_ID: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);

#[cfg(target_os = "windows")]
static DOWNLOAD_DIRECTORY_OVERRIDE: Mutex<Option<std::path::PathBuf>> = Mutex::new(None);

#[cfg(target_os = "windows")]
pub fn set_download_directory_override(path: Option<std::path::PathBuf>) {
    if let Ok(mut override_path) = DOWNLOAD_DIRECTORY_OVERRIDE.lock() {
        *override_path = path;
    }
}

#[cfg(target_os = "windows")]
fn download_directory() -> std::path::PathBuf {
    if let Ok(path) = std::env::var("AMARE_GPUI_DOWNLOAD_DIR") {
        let path = std::path::PathBuf::from(path);
        let _ = std::fs::create_dir_all(&path);
        return path;
    }
    if let Ok(override_path) = DOWNLOAD_DIRECTORY_OVERRIDE.lock()
        && let Some(path) = override_path.as_ref()
    {
        let _ = std::fs::create_dir_all(path);
        return path.clone();
    }
    if let Ok(profile) = std::env::var("USERPROFILE") {
        let path = std::path::PathBuf::from(profile).join("Downloads");
        let _ = std::fs::create_dir_all(&path);
        return path;
    }
    std::env::temp_dir().join("amare-downloads")
}

#[cfg(target_os = "windows")]
fn sanitize_filename(name: &str) -> String {
    let mut name: String = name
        .chars()
        .map(|ch| match ch {
            '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*' => '_',
            c if c.is_control() => '_',
            c => c,
        })
        .take(180)
        .collect();
    name = name.trim().trim_end_matches('.').to_string();
    let stem = name.split('.').next().unwrap_or("").to_ascii_lowercase();
    if name.is_empty()
        || matches!(
            stem.as_str(),
            "con"
                | "prn"
                | "aux"
                | "nul"
                | "com1"
                | "com2"
                | "com3"
                | "com4"
                | "com5"
                | "com6"
                | "com7"
                | "com8"
                | "com9"
                | "lpt1"
                | "lpt2"
                | "lpt3"
                | "lpt4"
                | "lpt5"
                | "lpt6"
                | "lpt7"
                | "lpt8"
                | "lpt9"
        )
    {
        name = format!("download-{name}");
    }
    name
}

#[cfg(target_os = "windows")]
fn unique_download_path(preferred: Option<&str>) -> std::path::PathBuf {
    let dir = download_directory();
    let name = preferred
        .and_then(|value| std::path::Path::new(value).file_name())
        .and_then(|value| value.to_str())
        .unwrap_or("download");
    let name = sanitize_filename(name);
    let path = std::path::Path::new(&name);
    let stem = path
        .file_stem()
        .and_then(|value| value.to_str())
        .unwrap_or("download");
    let ext = path
        .extension()
        .and_then(|value| value.to_str())
        .unwrap_or("");
    let mut candidate = dir.join(&name);
    let mut index = 1;
    while candidate.exists() {
        let filename = if ext.is_empty() {
            format!("{stem} ({index})")
        } else {
            format!("{stem} ({index}).{ext}")
        };
        candidate = dir.join(filename);
        index += 1;
    }
    candidate
}

#[cfg(target_os = "windows")]
const LOGIN_HOOK_SCRIPT: &str = r#"
(() => {
  if (window.top !== window || window.__amareLoginHook) return;
  if (!/^https?:$/.test(location.protocol)) return;
  window.__amareLoginHook = true;
  document.addEventListener('submit', (event) => {
    try {
      const form = event.target;
      if (!(form instanceof HTMLFormElement)) return;
      const password = form.querySelector('input[type="password"]');
      if (!password || !password.value) return;
      const user = form.querySelector('input[autocomplete="username"], input[type="email"], input[name*="user" i], input[id*="user" i], input[type="text"]');
      window.chrome.webview.postMessage(JSON.stringify({
        type: 'amare-login-submit',
        source: location.href,
        action: form.action || location.href,
        username: user ? String(user.value) : '',
        password: String(password.value)
      }));
    } catch (_) {}
  }, true);
})();
"#;

#[cfg(target_os = "windows")]
#[derive(Clone)]
pub struct LoginCapture {
    pub source: String,
    pub username: String,
    pub password: String,
}

#[cfg(target_os = "windows")]
impl Drop for LoginCapture {
    fn drop(&mut self) {
        use zeroize::Zeroize;
        self.password.zeroize();
    }
}

#[cfg(target_os = "windows")]
pub struct WebViewHost {
    controller: ICoreWebView2Controller,
    webview: ICoreWebView2,
    parent: Arc<Mutex<HWND>>,
    signals: Arc<Mutex<TabSignals>>,
    accelerator_token: i64,
    navigation_starting_token: i64,
    navigation_completed_token: i64,
    new_window_token: i64,
    download_token: i64,
    web_message_token: i64,
    popup_requests: Arc<Mutex<Vec<String>>>,
    downloads: Arc<Mutex<Vec<DownloadState>>>,
    login_captures: Arc<Mutex<Vec<LoginCapture>>>,
    navigation_id: Arc<AtomicU64>,
}

#[cfg(target_os = "windows")]
impl WebViewHost {
    pub fn create(
        environment: &WebViewEnvironment,
        parent: HWND,
    ) -> std::result::Result<Self, String> {
        unsafe {
            let ime_context = ImmCreateContext();
            if !ime_context.0.is_null() {
                let _ = ImmAssociateContext(parent, ime_context);
            }
            let _ = ImmAssociateContextEx(parent, HIMC::default(), IACE_DEFAULT);
        }

        let controller = {
            let (tx, rx) = mpsc::channel();
            let environment = environment.environment.clone();
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
            rx.recv()
                .map_err(|_| "WebView2 controller channel closed".to_string())?
        }
        .map_err(|error| format!("WebView2 controller: {error:?}"))?;

        unsafe {
            controller
                .SetIsVisible(false)
                .map_err(|error| format!("WebView2 visible: {error:?}"))?;
            let webview = controller
                .CoreWebView2()
                .map_err(|error| format!("WebView2 core: {error:?}"))?;
            if let Ok(webview_13) = webview.cast::<ICoreWebView2_13>() {
                if let Ok(profile) = webview_13.Profile() {
                    if let Ok(profile6) = profile.cast::<ICoreWebView2Profile6>() {
                        let _ = profile6.SetIsPasswordAutosaveEnabled(false);
                        let _ = profile6.SetIsGeneralAutofillEnabled(false);
                    }
                }
            }
            let signals = Arc::new(Mutex::new(TabSignals::default()));

            let starting_signals = signals.clone();
            let starting_navigation_id = Arc::new(AtomicU64::new(1));
            let navigation_id_for_event = starting_navigation_id.clone();
            let navigation_starting =
                NavigationStartingEventHandler::create(Box::new(move |_webview, _args| {
                    navigation_id_for_event.fetch_add(1, Ordering::Relaxed);
                    if let Ok(mut signals) = starting_signals.lock() {
                        signals.loading = true;
                        signals.error = None;
                    }
                    Ok(())
                }));
            let mut navigation_starting_token = 0;
            webview
                .add_NavigationStarting(&navigation_starting, &mut navigation_starting_token)
                .map_err(|error| format!("navigation starting handler: {error:?}"))?;

            let completed_signals = signals.clone();
            let navigation_completed =
                NavigationCompletedEventHandler::create(Box::new(move |_webview, args| {
                    let mut success = true;
                    if let Some(args) = args {
                        let mut value = windows::core::BOOL(0);
                        if args.IsSuccess(&mut value).is_ok() {
                            success = value.as_bool();
                        }
                    }
                    if let Ok(mut signals) = completed_signals.lock() {
                        signals.loading = false;
                        signals.error = if success {
                            None
                        } else {
                            Some("navigation failed".to_string())
                        };
                    }
                    Ok(())
                }));
            let mut navigation_completed_token = 0;
            webview
                .add_NavigationCompleted(&navigation_completed, &mut navigation_completed_token)
                .map_err(|error| format!("navigation completed handler: {error:?}"))?;

            let popup_requests = Arc::new(Mutex::new(Vec::new()));
            let popup_handler = {
                let popup_requests = popup_requests.clone();
                NewWindowRequestedEventHandler::create(Box::new(move |_webview, args| {
                    let Some(args) = args else { return Ok(()) };
                    let mut uri = windows::core::PWSTR::null();
                    args.Uri(&mut uri)?;
                    let url = take_pwstr(uri);
                    if !url.is_empty() {
                        if let Ok(mut requests) = popup_requests.lock() {
                            requests.push(url);
                        }
                    }
                    args.SetHandled(true)?;
                    Ok(())
                }))
            };
            let mut new_window_token = 0;
            webview
                .add_NewWindowRequested(&popup_handler, &mut new_window_token)
                .map_err(|error| format!("new window handler: {error:?}"))?;

            let parent_handle = Arc::new(Mutex::new(parent));
            let downloads = Arc::new(Mutex::new(Vec::new()));
            let download_handler = {
                let downloads = downloads.clone();
                DownloadStartingEventHandler::create(Box::new(move |_webview, args| {
                    let Some(args) = args else { return Ok(()) };
                    let operation = args.DownloadOperation()?;
                    let mut uri = windows::core::PWSTR::null();
                    operation.Uri(&mut uri)?;
                    let url = take_pwstr(uri);
                    let mut default_path = windows::core::PWSTR::null();
                    let _ = args.ResultFilePath(&mut default_path);
                    let default_path = take_pwstr(default_path);
                    let path = unique_download_path(if default_path.is_empty() {
                        None
                    } else {
                        Some(&default_path)
                    });
                    let path_string = path.to_string_lossy().to_string();
                    let path_h = HSTRING::from(path_string.clone());
                    args.SetResultFilePath(windows::core::PCWSTR(path_h.as_ptr()))?;
                    args.SetHandled(true)?;
                    let mut mime = windows::core::PWSTR::null();
                    let _ = operation.MimeType(&mut mime);
                    let mime_type = take_pwstr(mime);
                    let id = NEXT_DOWNLOAD_ID.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                    let filename = path
                        .file_name()
                        .and_then(|value| value.to_str())
                        .unwrap_or("download")
                        .to_string();
                    if let Ok(mut list) = downloads.lock() {
                        list.push(DownloadState {
                            id,
                            operation,
                            filename,
                            url,
                            path: path_string,
                            mime_type,
                            total_bytes: 0,
                            received_bytes: 0,
                            state: "in_progress".into(),
                            error: String::new(),
                        });
                    }
                    Ok(())
                }))
            };
            let mut download_token = 0;
            let download_webview: ICoreWebView2_4 = webview
                .cast()
                .map_err(|error| format!("WebView2 download interface: {error:?}"))?;
            download_webview
                .add_DownloadStarting(&download_handler, &mut download_token)
                .map_err(|error| format!("download handler: {error:?}"))?;

            let login_captures = Arc::new(Mutex::new(Vec::new()));
            let message_login_captures = login_captures.clone();
            let message_handler =
                WebMessageReceivedEventHandler::create(Box::new(move |_webview, args| {
                    let Some(args) = args else { return Ok(()) };
                    let mut message = windows::core::PWSTR::null();
                    if args.TryGetWebMessageAsString(&mut message).is_err() {
                        return Ok(());
                    }
                    let message = take_pwstr(message);
                    let Ok(value) = serde_json::from_str::<serde_json::Value>(&message) else {
                        return Ok(());
                    };
                    if value.get("type").and_then(|v| v.as_str()) != Some("amare-login-submit") {
                        return Ok(());
                    }
                    let source = value.get("source").and_then(|v| v.as_str()).unwrap_or("");
                    let action = value
                        .get("action")
                        .and_then(|v| v.as_str())
                        .unwrap_or(source);
                    let username = value.get("username").and_then(|v| v.as_str()).unwrap_or("");
                    let password = value.get("password").and_then(|v| v.as_str()).unwrap_or("");
                    if !source.starts_with("http://") && !source.starts_with("https://") {
                        return Ok(());
                    }
                    let mut event_source = windows::core::PWSTR::null();
                    if args.Source(&mut event_source).is_err() {
                        return Ok(());
                    }
                    let event_source = take_pwstr(event_source);
                    if !crate::password_vault::same_origin(&event_source, source)
                        || !crate::password_vault::same_origin(&action, source)
                    {
                        return Ok(());
                    }
                    if let Ok(mut captures) = message_login_captures.lock() {
                        captures.push(LoginCapture {
                            source: source.to_string(),
                            username: username.chars().take(512).collect(),
                            password: password.to_string(),
                        });
                    }
                    Ok(())
                }));
            let mut web_message_token = 0;
            webview
                .add_WebMessageReceived(&message_handler, &mut web_message_token)
                .map_err(|error| format!("web message handler: {error:?}"))?;

            let script = HSTRING::from(LOGIN_HOOK_SCRIPT);
            let script_webview = webview.clone();
            let _ = AddScriptToExecuteOnDocumentCreatedCompletedHandler::wait_for_async_operation(
                Box::new(move |handler| unsafe {
                    script_webview
                        .AddScriptToExecuteOnDocumentCreated(&script, &handler)
                        .map_err(webview2_com::Error::WindowsError)
                }),
                Box::new(|error, _| {
                    error?;
                    Ok(())
                }),
            );

            let accelerator_token = {
                let accelerator_parent = parent_handle.clone();
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
                            let shift = GetKeyState(VK_SHIFT.0 as i32) < 0;
                            let forwarded = if control && !shift && vk == VK_TAB.0 as u32 {
                                Some(VK_TAB.0)
                            } else if control && shift && vk == VK_TAB.0 as u32 {
                                Some(VK_TAB.0)
                            } else if control && vk == VK_L.0 as u32 {
                                Some(VK_L.0)
                            } else if control && vk == VK_R.0 as u32 {
                                Some(VK_R.0)
                            } else if control && vk == VK_T.0 as u32 {
                                Some(VK_T.0)
                            } else if control && vk == VK_N.0 as u32 {
                                Some(VK_N.0)
                            } else if control && vk == VK_D.0 as u32 {
                                Some(VK_D.0)
                            } else if control && shift && vk == VK_B.0 as u32 {
                                Some(VK_B.0)
                            } else if control && shift && vk == VK_H.0 as u32 {
                                Some(VK_H.0)
                            } else if control && vk == VK_W.0 as u32 {
                                Some(VK_W.0)
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
                                    let parent = *accelerator_parent
                                        .lock()
                                        .unwrap_or_else(|e| e.into_inner());
                                    let _ = SetFocus(Some(parent));
                                    let _ = PostMessageW(
                                        Some(parent),
                                        WM_KEYDOWN,
                                        WPARAM(key as usize),
                                        LPARAM(lparam as isize),
                                    );
                                    let _ = PostMessageW(
                                        Some(parent),
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
                controller
                    .add_AcceleratorKeyPressed(&accelerator_handler, &mut token)
                    .map_err(|error| format!("accelerator handler: {error:?}"))?;
                token
            };

            Ok(Self {
                controller,
                webview,
                parent: parent_handle,
                signals,
                accelerator_token,
                navigation_starting_token,
                navigation_completed_token,
                new_window_token,
                download_token,
                web_message_token,
                popup_requests,
                downloads,
                login_captures,
                navigation_id: starting_navigation_id,
            })
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
            let _ = std::fs::write(
                path,
                serde_json::to_vec_pretty(&payload).unwrap_or_default(),
            );
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
        let page_ok =
            source.contains("example.com") && title.eq_ignore_ascii_case("Example Domain");
        self.navigate_and_wait("https://example.com/?amare=gpui", 20)?;
        self.wait_for_action(|| unsafe {
            self.webview
                .GoBack()
                .map_err(|error| format!("GoBack: {error:?}"))
        })?;
        let back_source = self.source();
        let back_ok = back_source.contains("example.com");
        self.wait_for_action(|| unsafe {
            self.webview
                .Reload()
                .map_err(|error| format!("Reload: {error:?}"))
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
            self.webview
                .add_NavigationCompleted(&handler, &mut token)
                .map_err(|error| format!("add_NavigationCompleted: {error:?}"))?;
            self.webview
                .Navigate(&HSTRING::from(url))
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
            self.webview
                .add_NavigationCompleted(&handler, &mut token)
                .map_err(|error| format!("add_NavigationCompleted: {error:?}"))?;
        }
        let action_result = action();
        let result = if action_result.is_ok() {
            wait_with_pump(rx)
        } else {
            Ok(())
        };
        unsafe {
            let _ = self.webview.remove_NavigationCompleted(token);
        }
        action_result?;
        result.map_err(|_| "navigation did not complete".to_string())
    }

    pub fn source(&self) -> String {
        unsafe {
            let mut value = windows::core::PWSTR::null();
            if self.webview.Source(&mut value).is_err() {
                return String::new();
            }
            take_pwstr(value)
        }
    }

    pub fn title(&self) -> String {
        unsafe {
            let mut value = windows::core::PWSTR::null();
            if self.webview.DocumentTitle(&mut value).is_err() {
                return String::new();
            }
            take_pwstr(value)
        }
    }

    pub fn can_go_back(&self) -> bool {
        unsafe {
            let mut value = windows::core::BOOL(0);
            self.webview.CanGoBack(&mut value).is_ok() && value.as_bool()
        }
    }

    pub fn can_go_forward(&self) -> bool {
        unsafe {
            let mut value = windows::core::BOOL(0);
            self.webview.CanGoForward(&mut value).is_ok() && value.as_bool()
        }
    }

    pub fn download_snapshots(&self) -> Vec<DownloadSnapshot> {
        let mut out = Vec::new();
        if let Ok(mut downloads) = self.downloads.lock() {
            for download in downloads.iter_mut() {
                let mut total = 0i64;
                let mut received = 0i64;
                let mut state = COREWEBVIEW2_DOWNLOAD_STATE_IN_PROGRESS;
                unsafe {
                    let _ = download.operation.TotalBytesToReceive(&mut total);
                    let _ = download.operation.BytesReceived(&mut received);
                    let _ = download.operation.State(&mut state);
                }
                download.total_bytes = total;
                download.received_bytes = received;
                download.state = if state == COREWEBVIEW2_DOWNLOAD_STATE_COMPLETED {
                    "completed"
                } else if state == COREWEBVIEW2_DOWNLOAD_STATE_INTERRUPTED {
                    "interrupted"
                } else {
                    "in_progress"
                }
                .to_string();
                out.push(DownloadSnapshot {
                    id: download.id,
                    filename: download.filename.clone(),
                    url: download.url.clone(),
                    path: download.path.clone(),
                    mime_type: download.mime_type.clone(),
                    total_bytes: download.total_bytes,
                    received_bytes: download.received_bytes,
                    state: download.state.clone(),
                    error: download.error.clone(),
                });
            }
        }
        out
    }

    pub fn cancel_download(&self, id: u64) -> bool {
        if let Ok(mut downloads) = self.downloads.lock() {
            if let Some(download) = downloads.iter_mut().find(|download| download.id == id) {
                unsafe {
                    let _ = download.operation.Cancel();
                }
                download.state = "canceled".to_string();
                return true;
            }
        }
        false
    }

    pub fn remove_download(&self, id: u64) {
        if let Ok(mut downloads) = self.downloads.lock() {
            downloads.retain(|download| download.id != id);
        }
    }

    pub fn drain_login_captures(&self) -> Vec<LoginCapture> {
        self.login_captures
            .lock()
            .map(|mut captures| captures.drain(..).collect())
            .unwrap_or_default()
    }

    pub fn navigation_id(&self) -> u64 {
        self.navigation_id.load(Ordering::Relaxed)
    }

    pub fn clear_site_data(&self) -> Result<(), String> {
        let profile = unsafe {
            self.webview
                .cast::<ICoreWebView2_13>()
                .and_then(|webview| webview.Profile())
                .map_err(|_| "WebView2 profile unavailable".to_string())?
        };
        let profile = profile
            .cast::<ICoreWebView2Profile2>()
            .map_err(|_| "WebView2 profile data API unavailable".to_string())?;
        let (tx, rx) = mpsc::channel();
        ClearBrowsingDataCompletedHandler::wait_for_async_operation(
            Box::new(move |handler| unsafe {
                profile
                    .ClearBrowsingData(COREWEBVIEW2_BROWSING_DATA_KINDS_ALL_SITE, &handler)
                    .map_err(webview2_com::Error::WindowsError)
            }),
            Box::new(move |error| {
                error?;
                let _ = tx.send(());
                Ok(())
            }),
        )
        .map_err(|error| format!("ClearBrowsingData: {error:?}"))?;
        rx.recv()
            .map_err(|_| "ClearBrowsingData channel closed".to_string())
    }

    pub fn fill_login(
        &self,
        origin: &str,
        username: &str,
        password: &str,
        expected_navigation: u64,
    ) -> bool {
        if self.navigation_id() != expected_navigation {
            return false;
        }
        let Ok(source_origin) = crate::password_vault::normalize_origin(&self.source()) else {
            return false;
        };
        let Ok(expected_origin) = crate::password_vault::normalize_origin(origin) else {
            return false;
        };
        if source_origin != expected_origin {
            return false;
        }
        let username = serde_json::to_string(username).unwrap_or_else(|_| "\"\"".into());
        let password = serde_json::to_string(password).unwrap_or_else(|_| "\"\"".into());
        let script = format!(
            "(() => {{ const p=document.querySelector('input[type=\"password\"]'); if(!p) return false; const u=document.querySelector('input[autocomplete=\"username\"], input[type=\"email\"], input[name*=\"user\" i], input[id*=\"user\" i], input[type=\"text\"]'); if(u) {{ u.value={username}; u.dispatchEvent(new Event('input',{{bubbles:true}})); }} p.value={password}; p.dispatchEvent(new Event('input',{{bubbles:true}})); return true; }})()"
        );
        self.execute_script(&script)
            .map(|result| result.contains("true"))
            .unwrap_or(false)
    }

    pub fn drain_popup_requests(&self) -> Vec<String> {
        self.popup_requests
            .lock()
            .map(|mut requests| requests.drain(..).collect())
            .unwrap_or_default()
    }

    pub fn signals(&self) -> (bool, Option<String>) {
        self.signals
            .lock()
            .map(|signals| (signals.loading, signals.error.clone()))
            .unwrap_or((false, None))
    }

    fn execute_script(&self, script: &str) -> Result<String, String> {
        let (tx, rx) = mpsc::channel();
        let webview = self.webview.clone();
        let script = HSTRING::from(script);
        ExecuteScriptCompletedHandler::wait_for_async_operation(
            Box::new(move |handler| unsafe {
                webview
                    .ExecuteScript(&script, &handler)
                    .map_err(webview2_com::Error::WindowsError)
            }),
            Box::new(move |error, result| {
                error?;
                let _ = tx.send(result);
                Ok(())
            }),
        )
        .map_err(|error| format!("ExecuteScript: {error:?}"))?;
        rx.recv()
            .map_err(|_| "ExecuteScript channel closed".to_string())
    }

    pub fn set_bounds(&self, left: i32, top: i32, width: i32, height: i32) {
        let rect = RECT {
            left,
            top,
            right: width,
            bottom: height,
        };
        unsafe {
            let _ = self.controller.SetBounds(rect);
        }
    }

    pub fn set_active(&self, active: bool) {
        unsafe {
            let _ = self.controller.SetIsVisible(active);
        }
    }

    pub fn navigate(&self, url: &str) {
        if let Ok(mut signals) = self.signals.lock() {
            signals.loading = true;
            signals.error = None;
        }
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

    pub fn scroll_position(&self) -> Option<(i64, i64)> {
        let result = self
            .execute_script("[Math.round(window.scrollX), Math.round(window.scrollY)]")
            .ok()?;
        let values: Vec<i64> = serde_json::from_str(&result).ok()?;
        if values.len() == 2 {
            Some((values[0], values[1]))
        } else {
            None
        }
    }

    pub fn scroll_to(&self, x: i64, y: i64) {
        let _ = self.execute_script(&format!("window.scrollTo({x}, {y})"));
    }

    pub fn focus(&self) {
        unsafe {
            if self
                .controller
                .MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC)
                .is_ok()
            {
                return;
            }
            let parent = *self.parent.lock().unwrap_or_else(|e| e.into_inner());
            let _ = SetFocus(Some(parent));
        }
    }

    pub fn reparent(&mut self, parent: HWND) -> Result<(), String> {
        unsafe {
            self.controller
                .SetParentWindow(parent)
                .map_err(|error| format!("WebView2 SetParentWindow: {error:?}"))?;
            *self.parent.lock().unwrap_or_else(|e| e.into_inner()) = parent;
            let _ = self.controller.NotifyParentWindowPositionChanged();
        }
        Ok(())
    }

    pub fn ensure_ime(&self) {
        unsafe {
            let parent = *self.parent.lock().unwrap_or_else(|e| e.into_inner());
            let korean = (GetKeyboardLayout(GetCurrentThreadId()).0 as u32 & 0xFFFF) == 0x0412;
            if ImmGetContext(parent).0.is_null() {
                let context = ImmCreateContext();
                if !context.0.is_null() {
                    let _ = ImmAssociateContext(parent, context);
                }
                let _ = ImmAssociateContextEx(parent, HIMC::default(), IACE_DEFAULT);
            }
            if korean {
                let context = ImmGetContext(parent);
                if !context.0.is_null() {
                    let _ = ImmSetOpenStatus(context, true);
                    let _ = ImmSetConversionStatus(
                        context,
                        IME_CONVERSION_MODE(0x00000001),
                        IME_SENTENCE_MODE(0),
                    );
                    let _ = ImmReleaseContext(parent, context);
                }
            }
        }
    }

    pub fn ime_context(&self) -> bool {
        let parent = *self.parent.lock().unwrap_or_else(|e| e.into_inner());
        unsafe { !ImmGetContext(parent).0.is_null() }
    }
}

#[cfg(target_os = "windows")]
impl Drop for WebViewHost {
    fn drop(&mut self) {
        unsafe {
            let _ = self
                .webview
                .remove_NavigationStarting(self.navigation_starting_token);
            let _ = self
                .webview
                .remove_NavigationCompleted(self.navigation_completed_token);
            let _ = self
                .webview
                .remove_NewWindowRequested(self.new_window_token);
            let _ = self
                .webview
                .remove_WebMessageReceived(self.web_message_token);
            if let Ok(download_webview) = self.webview.cast::<ICoreWebView2_4>() {
                let _ = download_webview.remove_DownloadStarting(self.download_token);
            }
            let _ = self
                .controller
                .remove_AcceleratorKeyPressed(self.accelerator_token);
            let _ = self.controller.Close();
        }
    }
}
