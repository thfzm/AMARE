//! Shared, platform-independent service layer for AMARE.
//!
//! Services enforce profile isolation, active-profile context, settings policy,
//! and cross-domain search. They intentionally do not contain UI/engine/AI code.

mod context;
mod error;
mod profile;
mod search;
mod settings;

pub use context::ProfileContext;
pub use error::ServiceError;
pub use profile::ProfileService;
pub use search::{SearchPage, SearchRequest, SearchService, SearchSort};
pub use settings::{
    ResolvedSetting, SettingChange, SettingDefinition, SettingGroup, SettingSource,
    SettingsChangeSet, SettingsService, ValueRule, definition, definitions,
};
