//! Stable C ABI for the AMARE core and service layer (Phase 2).
//!
//! Conventions:
//! - Return `0` on success; non-zero is an error code.
//! - Strings returned through `out` must be freed with `amare_string_free`.
//! - Rust panics never cross the FFI boundary.
//! - This surface is intentionally small and versioned; extend it only with a
//!   contract/version update.

use std::cell::RefCell;
use std::ffi::{CStr, CString, c_char, c_int};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr;
use std::sync::OnceLock;
use std::sync::atomic::{AtomicU64, Ordering};

mod services;

pub const AMARE_OK: c_int = 0;
pub const AMARE_ERR_INVALID_ARGUMENT: c_int = 1;
pub const AMARE_ERR_PANIC: c_int = 2;
pub const AMARE_ERR_INTERNAL: c_int = 3;

thread_local! {
    static LAST_ERROR: RefCell<Option<CString>> = const { RefCell::new(None) };
}

static NEXT_CORE_ID: AtomicU64 = AtomicU64::new(1);
static VERSION: OnceLock<CString> = OnceLock::new();

/// Opaque core handle owned by the caller.
pub struct AmareCore {
    id: u64,
}

impl AmareCore {
    fn new() -> Self {
        Self {
            id: NEXT_CORE_ID.fetch_add(1, Ordering::Relaxed),
        }
    }

    fn id(&self) -> u64 {
        self.id
    }
}

pub(crate) fn set_last_error(message: impl Into<String>) {
    let message = message.into();
    let cstring = CString::new(message).unwrap_or_else(|_| CString::new("invalid error").unwrap());
    LAST_ERROR.with(|cell| {
        *cell.borrow_mut() = Some(cstring);
    });
}

pub(crate) fn clear_last_error() {
    LAST_ERROR.with(|cell| {
        *cell.borrow_mut() = None;
    });
}

pub(crate) fn guard(f: impl FnOnce() -> c_int) -> c_int {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(code) => code,
        Err(_) => {
            set_last_error("panic caught at FFI boundary");
            AMARE_ERR_PANIC
        }
    }
}

pub(crate) unsafe fn read_cstr<'a>(value: *const c_char) -> Result<&'a str, c_int> {
    if value.is_null() {
        set_last_error("null string pointer");
        return Err(AMARE_ERR_INVALID_ARGUMENT);
    }
    // SAFETY: caller guarantees a valid NUL-terminated string for the duration
    // of this call.
    let bytes = unsafe { CStr::from_ptr(value) };
    bytes.to_str().map_err(|_| {
        set_last_error("string is not valid UTF-8");
        AMARE_ERR_INVALID_ARGUMENT
    })
}

pub(crate) fn write_out_string(out: *mut *mut c_char, value: String) -> c_int {
    if out.is_null() {
        set_last_error("null output pointer");
        return AMARE_ERR_INVALID_ARGUMENT;
    }
    match CString::new(value) {
        Ok(value) => {
            // SAFETY: `out` was checked for null; caller owns the pointer and
            // must release it with `amare_string_free`.
            unsafe {
                *out = value.into_raw();
            }
            AMARE_OK
        }
        Err(_) => {
            set_last_error("string contains an interior NUL byte");
            AMARE_ERR_INVALID_ARGUMENT
        }
    }
}

/// Returns a static core version string. Do not free the returned pointer.
#[unsafe(no_mangle)]
pub extern "C" fn amare_core_version() -> *const c_char {
    VERSION
        .get_or_init(|| CString::new(amare_core::version()).expect("version has no NUL"))
        .as_ptr()
}

/// Create a core handle. `config` may be null in the scaffold.
///
/// # Safety
/// `out` must point to writable storage for one pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn amare_core_create(
    config: *const c_char,
    out: *mut *mut AmareCore,
) -> c_int {
    guard(|| {
        if out.is_null() {
            set_last_error("null core output pointer");
            return AMARE_ERR_INVALID_ARGUMENT;
        }
        if !config.is_null() {
            if let Err(code) = unsafe { read_cstr(config) } {
                return code;
            }
        }
        let core = Box::new(AmareCore::new());
        // SAFETY: `out` is non-null and writable.
        unsafe {
            *out = Box::into_raw(core);
        }
        clear_last_error();
        AMARE_OK
    })
}

/// Destroy a core handle. Null is a no-op.
///
/// # Safety
/// `core` must be a pointer returned by `amare_core_create` and not used again.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn amare_core_destroy(core: *mut AmareCore) {
    if core.is_null() {
        return;
    }
    // SAFETY: contract above.
    unsafe {
        drop(Box::from_raw(core));
    }
}

/// Ping the core. On success, `out` receives a UTF-8 string that must be freed
/// with `amare_string_free`.
///
/// # Safety
/// `core` must be valid; `input` must be a valid C string; `out` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn amare_core_ping(
    core: *mut AmareCore,
    input: *const c_char,
    out: *mut *mut c_char,
) -> c_int {
    guard(|| {
        if core.is_null() {
            set_last_error("null core handle");
            return AMARE_ERR_INVALID_ARGUMENT;
        }
        if out.is_null() {
            set_last_error("null output pointer");
            return AMARE_ERR_INVALID_ARGUMENT;
        }
        let input = match unsafe { read_cstr(input) } {
            Ok(value) => value,
            Err(code) => return code,
        };
        // SAFETY: non-null checked above; handle is owned by caller.
        let core = unsafe { &*core };
        let reply = format!("pong:{}:{}", core.id(), amare_core::ping(input));
        let code = write_out_string(out, reply);
        if code == AMARE_OK {
            clear_last_error();
        }
        code
    })
}

/// Copy the last thread-local error into `out`. Returns `AMARE_OK` even when no
/// error is present; in that case `*out` is null.
///
/// # Safety
/// `out` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn amare_core_last_error(out: *mut *mut c_char) -> c_int {
    guard(|| {
        if out.is_null() {
            return AMARE_ERR_INVALID_ARGUMENT;
        }
        let message = LAST_ERROR.with(|cell| cell.borrow().clone());
        match message {
            Some(message) => {
                // SAFETY: out is non-null.
                unsafe {
                    *out = message.into_raw();
                }
                AMARE_OK
            }
            None => {
                // SAFETY: out is non-null.
                unsafe {
                    *out = ptr::null_mut();
                }
                AMARE_OK
            }
        }
    })
}

/// Free a string returned by this library. Null is a no-op.
///
/// # Safety
/// `value` must be a pointer returned by this library and not freed again.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn amare_string_free(value: *mut c_char) {
    if value.is_null() {
        return;
    }
    // SAFETY: contract above.
    unsafe {
        drop(CString::from_raw(value));
    }
}

/// Numeric round-trip used by the C# interop smoke test.
#[unsafe(no_mangle)]
pub extern "C" fn amare_core_add(a: i64, b: i64) -> i64 {
    a.saturating_add(b)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::CString;

    #[test]
    fn version_is_available() {
        let ptr = amare_core_version();
        assert!(!ptr.is_null());
        let value = unsafe { CStr::from_ptr(ptr) }.to_str().unwrap();
        assert_eq!(value, amare_core::version());
    }

    #[test]
    fn create_ping_and_destroy() {
        let mut core = ptr::null_mut();
        let code = unsafe { amare_core_create(ptr::null(), &mut core) };
        assert_eq!(code, AMARE_OK);
        assert!(!core.is_null());

        let input = CString::new("hello").unwrap();
        let mut out = ptr::null_mut();
        let code = unsafe { amare_core_ping(core, input.as_ptr(), &mut out) };
        assert_eq!(code, AMARE_OK);
        let reply = unsafe { CStr::from_ptr(out) }.to_str().unwrap().to_string();
        assert!(reply.starts_with("pong:"));
        assert!(reply.ends_with("pong:hello"));
        unsafe { amare_string_free(out) };

        unsafe { amare_core_destroy(core) };
    }

    #[test]
    fn null_input_is_rejected_and_error_is_reported() {
        let mut core = ptr::null_mut();
        assert_eq!(
            unsafe { amare_core_create(ptr::null(), &mut core) },
            AMARE_OK
        );
        let mut out = ptr::null_mut();
        assert_eq!(
            unsafe { amare_core_ping(core, ptr::null(), &mut out) },
            AMARE_ERR_INVALID_ARGUMENT
        );
        assert!(out.is_null());

        let mut error = ptr::null_mut();
        assert_eq!(unsafe { amare_core_last_error(&mut error) }, AMARE_OK);
        assert!(!error.is_null());
        let message = unsafe { CStr::from_ptr(error) }.to_str().unwrap();
        assert!(message.contains("null"));
        unsafe { amare_string_free(error) };

        unsafe { amare_core_destroy(core) };
    }

    #[test]
    fn add_saturates() {
        assert_eq!(amare_core_add(20, 22), 42);
        assert_eq!(amare_core_add(i64::MAX, 1), i64::MAX);
    }
}
