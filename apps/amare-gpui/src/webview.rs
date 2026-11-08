#[cfg(target_os = "windows")]
use std::sync::mpsc;

#[cfg(target_os = "windows")]
use webview2_com::{
    CreateCoreWebView2ControllerCompletedHandler, CreateCoreWebView2EnvironmentCompletedHandler,
    ExecuteScriptCompletedHandler, NavigationCompletedEventHandler, take_pwstr, wait_with_pump,
    Microsoft::Web::WebView2::Win32::{
        CreateCoreWebView2Environment, ICoreWebView2, ICoreWebView2Controller,
    },
};
#[cfg(target_os = "windows")]
use windows::{
    core::HSTRING,
    Win32::{
        Foundation::{E_POINTER, HWND, RECT},
        System::Com::{CoInitializeEx, COINIT_APARTMENTTHREADED},
    },
};

#[cfg(target_os = "windows")]
pub struct WebViewHost {
    controller: ICoreWebView2Controller,
    webview: ICoreWebView2,
}

#[cfg(target_os = "windows")]
impl WebViewHost {
    pub fn create(parent: HWND) -> std::result::Result<Self, String> {
        unsafe {
            let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
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
            webview.Navigate(&HSTRING::from("https://example.com/"))
                .map_err(|error| format!("WebView2 navigate: {error:?}"))?;
            if self_test {
                let _ = wait_with_pump(initial_rx);
                let _ = webview.remove_NavigationCompleted(token);
            }
            Ok(Self { controller, webview })
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
            "success": has_content && back_ok,
            "source": source,
            "title": title,
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

    fn source(&self) -> String {
        unsafe {
            let mut value = windows::core::PWSTR::null();
            if self.webview.Source(&mut value).is_err() { return String::new(); }
            take_pwstr(value)
        }
    }

    fn title(&self) -> String {
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
}

#[cfg(target_os = "windows")]
impl Drop for WebViewHost {
    fn drop(&mut self) {
        unsafe {
            let _ = self.controller.Close();
        }
    }
}
