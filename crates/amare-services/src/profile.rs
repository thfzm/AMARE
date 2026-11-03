use crate::context::ProfileContext;
use crate::error::ServiceError;
use amare_core::{Page, Profile, ProfileId};
use amare_store::{ProfileStore, Store, StoreError};
use std::path::Path;
use std::sync::Mutex;

#[derive(Debug, Clone, Default)]
struct ActiveProfile {
    profile_id: Option<ProfileId>,
    generation: u64,
}

/// Owns the profile registry and the single logical "active profile" pointer.
///
/// The service never caches a database connection across calls. Every access
/// opens a short-lived [`ProfileStore`], so a profile switch cannot redirect an
/// already-open connection to another profile's data.
pub struct ProfileService {
    store: Mutex<Store>,
    active: Mutex<ActiveProfile>,
}

impl ProfileService {
    pub fn open(root_dir: impl AsRef<Path>) -> Result<Self, ServiceError> {
        let store = Store::open(root_dir)?;
        let active_id = match store.profiles().default_profile()? {
            Some(profile) if !store.is_profile_deleting(&profile.id) => Some(profile.id),
            _ => store
                .profiles()
                .list(10, 0)?
                .items
                .into_iter()
                .find(|profile| !store.is_profile_deleting(&profile.id))
                .map(|profile| profile.id),
        };
        Ok(Self {
            store: Mutex::new(store),
            active: Mutex::new(ActiveProfile {
                profile_id: active_id,
                generation: 1,
            }),
        })
    }

    pub fn root_dir(&self) -> Result<std::path::PathBuf, ServiceError> {
        Ok(self.lock_store()?.root_dir().to_path_buf())
    }

    /// Ensures a default profile exists, repairs a missing default flag, and
    /// makes the resulting default profile active.
    pub fn initialize(&self) -> Result<ProfileContext, ServiceError> {
        let store = self.lock_store()?;
        let mut active = self.lock_active()?;
        let profiles = store.profiles();
        if profiles.count()? == 0 {
            let id = ProfileId::new("default");
            store.create_profile(&id, "Personal", true)?;
        }
        let default_available = profiles
            .default_profile()?
            .is_some_and(|profile| !store.is_profile_deleting(&profile.id));
        if !default_available {
            let first = profiles
                .list(10, 0)?
                .items
                .into_iter()
                .find(|profile| !store.is_profile_deleting(&profile.id))
                .ok_or(ServiceError::NoActiveProfile)?;
            profiles.set_default(&first.id)?;
        }
        let profile = profiles
            .default_profile()?
            .ok_or(ServiceError::NoActiveProfile)?;
        active.profile_id = Some(profile.id.clone());
        active.generation = active.generation.saturating_add(1);
        Ok(ProfileContext {
            profile_id: profile.id,
            generation: active.generation,
        })
    }

    pub fn list(&self, limit: u32, offset: u64) -> Result<Page<Profile>, ServiceError> {
        let store = self.lock_store()?;
        let mut all = Vec::new();
        let mut page_offset = 0u64;
        loop {
            let page = store.profiles().list(500, page_offset)?;
            let fetched = page.items.len();
            all.extend(
                page.items
                    .into_iter()
                    .filter(|profile| !store.is_profile_deleting(&profile.id)),
            );
            if fetched < 500 {
                break;
            }
            page_offset += fetched as u64;
        }
        let total = all.len() as u64;
        let limit = limit.clamp(1, 500);
        let items = all
            .into_iter()
            .skip(offset as usize)
            .take(limit as usize)
            .collect();
        Ok(Page {
            items,
            total,
            limit,
            offset,
        })
    }

    pub fn get(&self, id: &ProfileId) -> Result<Option<Profile>, ServiceError> {
        let store = self.lock_store()?;
        if store.is_profile_deleting(id) {
            return Ok(None);
        }
        Ok(store.profiles().get(id)?)
    }

    pub fn exists(&self, id: &ProfileId) -> Result<bool, ServiceError> {
        Ok(self.get(id)?.is_some())
    }

    pub fn create_profile(
        &self,
        id: &ProfileId,
        name: &str,
        icon: Option<&str>,
        is_default: bool,
    ) -> Result<Profile, ServiceError> {
        validate_icon(icon)?;
        let store = self.lock_store()?;
        let mut active = self.lock_active()?;
        store.create_profile(id, name, is_default)?;
        if let Some(icon) = icon {
            if let Err(error) = store.profiles().set_icon(id, Some(icon)) {
                let _ = store.delete_profile(id);
                return Err(error.into());
            }
        }
        let profile = store.profiles().get_required(id)?;
        if active.profile_id.is_none() {
            active.profile_id = Some(profile.id.clone());
            active.generation = active.generation.saturating_add(1);
        }
        Ok(profile)
    }

    pub fn rename_profile(&self, id: &ProfileId, name: &str) -> Result<Profile, ServiceError> {
        let store = self.lock_store()?;
        ensure_available(&store, id)?;
        store.profiles().rename(id, name)?;
        Ok(store.profiles().get_required(id)?)
    }

    pub fn set_profile_icon(
        &self,
        id: &ProfileId,
        icon: Option<&str>,
    ) -> Result<Profile, ServiceError> {
        validate_icon(icon)?;
        let store = self.lock_store()?;
        ensure_available(&store, id)?;
        store.profiles().set_icon(id, icon)?;
        Ok(store.profiles().get_required(id)?)
    }

    pub fn set_default_profile(&self, id: &ProfileId) -> Result<Profile, ServiceError> {
        let store = self.lock_store()?;
        ensure_available(&store, id)?;
        store.profiles().set_default(id)?;
        Ok(store.profiles().get_required(id)?)
    }

    /// Deletes a non-last profile. Deleting the active profile invalidates all
    /// outstanding contexts and activates the promoted default/first profile.
    pub fn delete_profile(&self, id: &ProfileId) -> Result<(), ServiceError> {
        let store = self.lock_store()?;
        let mut active = self.lock_active()?;
        let finalizing_tombstone = store.is_profile_deleting(id);
        if !finalizing_tombstone {
            ensure_available(&store, id)?;
            if store.profiles().count()? <= 1 {
                return Err(ServiceError::LastProfile);
            }
        }
        store.delete_profile(id)?;
        if active.profile_id.as_ref() == Some(id) {
            let next = match store.profiles().default_profile()? {
                Some(profile) => Some(profile.id),
                None => store
                    .profiles()
                    .list(1, 0)?
                    .items
                    .into_iter()
                    .next()
                    .map(|p| p.id),
            };
            active.profile_id = next;
            active.generation = active.generation.saturating_add(1);
        }
        Ok(())
    }

    /// Switches the active profile and returns a context that must be presented
    /// by SettingsService/SearchService. The generation invalidates old work.
    pub fn switch_profile(&self, id: &ProfileId) -> Result<ProfileContext, ServiceError> {
        let store = self.lock_store()?;
        let mut active = self.lock_active()?;
        ensure_available(&store, id)?;
        // Validate that the profile database is openable before publishing the
        // switch. This also runs any pending profile migrations.
        drop(store.open_profile(id)?);
        active.profile_id = Some(id.clone());
        active.generation = active.generation.saturating_add(1);
        Ok(ProfileContext {
            profile_id: id.clone(),
            generation: active.generation,
        })
    }

    pub fn current_context(&self) -> Result<Option<ProfileContext>, ServiceError> {
        let active = self.lock_active()?;
        Ok(active.profile_id.clone().map(|profile_id| ProfileContext {
            profile_id,
            generation: active.generation,
        }))
    }

    pub fn active_profile(&self) -> Result<Option<Profile>, ServiceError> {
        let store = self.lock_store()?;
        let mut active = self.lock_active()?;
        let Some(id) = active.profile_id.clone() else {
            return Ok(None);
        };
        if store.is_profile_deleting(&id) {
            active.profile_id = None;
            active.generation = active.generation.saturating_add(1);
            return Ok(None);
        }
        match store.profiles().get(&id)? {
            Some(profile) => Ok(Some(profile)),
            None => {
                active.profile_id = None;
                active.generation = active.generation.saturating_add(1);
                Ok(None)
            }
        }
    }

    pub fn profile_store(&self, context: &ProfileContext) -> Result<ProfileStore, ServiceError> {
        let store = self.lock_store()?;
        let mut active = self.lock_active()?;
        match &active.profile_id {
            Some(id) if id == &context.profile_id && active.generation == context.generation => {}
            Some(_) => return Err(ServiceError::ProfileSwitched),
            None => return Err(ServiceError::NoActiveProfile),
        }
        match store.open_profile(&context.profile_id) {
            Ok(profile) => Ok(profile),
            Err(error) => {
                if matches!(
                    error,
                    StoreError::ProfileNotFound(_) | StoreError::ProfileDeleting(_)
                ) {
                    active.profile_id = None;
                    active.generation = active.generation.saturating_add(1);
                }
                Err(error.into())
            }
        }
    }

    pub fn reset_profile(&self, context: &ProfileContext) -> Result<(), ServiceError> {
        let profile = self.profile_store(context)?;
        profile.reset_user_data()?;
        Ok(())
    }

    fn lock_store(&self) -> Result<std::sync::MutexGuard<'_, Store>, ServiceError> {
        self.store
            .lock()
            .map_err(|_| ServiceError::InvalidInput("profile service lock poisoned".into()))
    }

    fn lock_active(&self) -> Result<std::sync::MutexGuard<'_, ActiveProfile>, ServiceError> {
        self.active
            .lock()
            .map_err(|_| ServiceError::InvalidInput("profile service lock poisoned".into()))
    }
}

fn ensure_available(store: &Store, id: &ProfileId) -> Result<(), ServiceError> {
    if store.is_profile_deleting(id) {
        return Err(StoreError::ProfileDeleting(id.to_string()).into());
    }
    store.profiles().get_required(id)?;
    Ok(())
}

fn validate_icon(icon: Option<&str>) -> Result<(), ServiceError> {
    if let Some(icon) = icon {
        if icon.trim().is_empty() {
            return Err(ServiceError::InvalidInput(
                "profile icon must not be blank".into(),
            ));
        }
        if icon.len() > 1024 {
            return Err(ServiceError::InvalidInput(
                "profile icon must be <= 1024 bytes".into(),
            ));
        }
    }
    Ok(())
}
