#[cfg(target_os = "windows")]
use std::sync::mpsc;

#[cfg(target_os = "windows")]
use webview2_com::{
    CreateCoreWebView2ControllerCompletedHandler, CreateCoreWebView2EnvironmentCompletedHandler,
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
            webview.Navigate(&HSTRING::from("https://example.com/"))
                .map_err(|error| format!("WebView2 navigate: {error:?}"))?;
            Ok(Self { controller, webview })
        }
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
