//! Process-wide ProfileService sharing for ABI 1 and ABI 2.
//!
//! ABI 1 and ABI 2 must observe the same active-profile generation so a context
//! produced by ABI 1 can safely authorize an ABI 2 vault call. The map holds
//! weak references: once all native handles for a root are gone the service is
//! dropped and a fresh one is created on the next open.

use amare_services::{ProfileService, ServiceError};
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{Arc, Mutex, OnceLock, Weak};

static SERVICES: OnceLock<Mutex<HashMap<PathBuf, Weak<Mutex<ProfileService>>>>> = OnceLock::new();

pub(crate) fn profile_service(root_dir: &str) -> Result<Arc<Mutex<ProfileService>>, ServiceError> {
    let path = std::fs::canonicalize(root_dir).unwrap_or_else(|_| PathBuf::from(root_dir));
    let map = SERVICES.get_or_init(|| Mutex::new(HashMap::new()));
    let mut map = map
        .lock()
        .map_err(|_| ServiceError::InvalidInput("profile service registry poisoned".into()))?;
    map.retain(|_, weak| weak.strong_count() > 0);
    if let Some(existing) = map.get(&path).and_then(Weak::upgrade) {
        return Ok(existing);
    }
    let service = Arc::new(Mutex::new(ProfileService::open(&path)?));
    map.insert(path, Arc::downgrade(&service));
    Ok(service)
}
