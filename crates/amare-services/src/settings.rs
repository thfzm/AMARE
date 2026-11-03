use crate::context::ProfileContext;
use crate::error::ServiceError;
use crate::profile::ProfileService;
use amare_core::{SettingEntry, SettingValue};
use amare_store::ProfileStore;
use std::collections::BTreeMap;
use std::sync::LazyLock;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SettingGroup {
    Appearance,
    Browsing,
    Search,
    Privacy,
    Security,
    Downloads,
    Tabs,
    Performance,
    Ai,
    Agent,
    Notifications,
    Updates,
}

impl SettingGroup {
    pub const ALL: [SettingGroup; 12] = [
        Self::Appearance,
        Self::Browsing,
        Self::Search,
        Self::Privacy,
        Self::Security,
        Self::Downloads,
        Self::Tabs,
        Self::Performance,
        Self::Ai,
        Self::Agent,
        Self::Notifications,
        Self::Updates,
    ];

    pub fn as_str(self) -> &'static str {
        match self {
            Self::Appearance => "appearance",
            Self::Browsing => "browsing",
            Self::Search => "search",
            Self::Privacy => "privacy",
            Self::Security => "security",
            Self::Downloads => "downloads",
            Self::Tabs => "tabs",
            Self::Performance => "performance",
            Self::Ai => "ai",
            Self::Agent => "agent",
            Self::Notifications => "notifications",
            Self::Updates => "updates",
        }
    }

    pub fn parse(value: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|group| group.as_str() == value)
    }
}

#[derive(Debug, Clone)]
pub enum ValueRule {
    TextChoices(&'static [&'static str]),
    Text { max_len: usize },
    Boolean,
    IntegerRange { min: i64, max: i64 },
    FloatRange { min: f64, max: f64 },
    Json,
}

#[derive(Debug, Clone)]
pub struct SettingDefinition {
    pub key: &'static str,
    pub group: SettingGroup,
    pub default: SettingValue,
    pub rule: ValueRule,
    pub description: &'static str,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SettingSource {
    Default,
    User,
}

#[derive(Debug, Clone, PartialEq)]
pub struct ResolvedSetting {
    pub key: String,
    pub group: SettingGroup,
    pub value: SettingValue,
    pub source: SettingSource,
    pub updated_at_ms: Option<i64>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct SettingChange {
    pub key: String,
    pub group: SettingGroup,
    pub old: Option<SettingValue>,
    pub new: SettingValue,
    pub source: SettingSource,
}

#[derive(Debug, Clone, Default, PartialEq)]
pub struct SettingsChangeSet {
    pub changes: Vec<SettingChange>,
}

impl SettingsChangeSet {
    pub fn changed_count(&self) -> usize {
        self.changes.len()
    }

    pub fn is_empty(&self) -> bool {
        self.changes.is_empty()
    }
}

static DEFINITIONS: LazyLock<Vec<SettingDefinition>> = LazyLock::new(|| {
    vec![
        text_choice(
            "theme",
            SettingGroup::Appearance,
            "system",
            &["system", "light", "dark"],
            "UI theme preference; consumers decide when it is applied.",
        ),
        text(
            "home_page",
            SettingGroup::Browsing,
            "about:blank",
            8192,
            "Home page target; browser shell applies this policy.",
        ),
        text_choice(
            "engine",
            SettingGroup::Search,
            "duckduckgo",
            &["duckduckgo", "google", "bing"],
            "Default search provider identifier.",
        ),
        boolean(
            "telemetry",
            SettingGroup::Privacy,
            false,
            "AMARE has no telemetry server; when false nothing is sent.",
        ),
        boolean(
            "https_only",
            SettingGroup::Security,
            true,
            "HTTPS-only policy flag. Enforcement belongs to the browser layer.",
        ),
        boolean(
            "ask_where_to_save",
            SettingGroup::Downloads,
            true,
            "Download location prompt policy.",
        ),
        boolean(
            "restore_session",
            SettingGroup::Tabs,
            true,
            "Session restore policy.",
        ),
        boolean(
            "background_throttling",
            SettingGroup::Performance,
            true,
            "Background tab throttling policy.",
        ),
        text_choice(
            "default_mode",
            SettingGroup::Ai,
            "web_ai",
            &["web_ai", "api"],
            "Default AI surface; no AI transport is implemented here.",
        ),
        boolean(
            "enabled",
            SettingGroup::Agent,
            false,
            "Agent opt-in flag; execution is implemented in a later phase.",
        ),
        boolean(
            "enabled",
            SettingGroup::Notifications,
            false,
            "Notification permission policy.",
        ),
        text_choice(
            "channel",
            SettingGroup::Updates,
            "stable",
            &["stable", "beta"],
            "Update channel policy.",
        ),
    ]
});

fn text(
    key: &'static str,
    group: SettingGroup,
    default: &'static str,
    max_len: usize,
    description: &'static str,
) -> SettingDefinition {
    SettingDefinition {
        key,
        group,
        default: SettingValue::Text(default.into()),
        rule: ValueRule::Text { max_len },
        description,
    }
}

fn text_choice(
    key: &'static str,
    group: SettingGroup,
    default: &'static str,
    choices: &'static [&'static str],
    description: &'static str,
) -> SettingDefinition {
    SettingDefinition {
        key,
        group,
        default: SettingValue::Text(default.into()),
        rule: ValueRule::TextChoices(choices),
        description,
    }
}

fn boolean(
    key: &'static str,
    group: SettingGroup,
    default: bool,
    description: &'static str,
) -> SettingDefinition {
    SettingDefinition {
        key,
        group,
        default: SettingValue::Boolean(default),
        rule: ValueRule::Boolean,
        description,
    }
}

pub fn definitions() -> &'static [SettingDefinition] {
    DEFINITIONS.as_slice()
}

pub fn definition(key: &str) -> Option<&'static SettingDefinition> {
    DEFINITIONS.iter().find(|definition| definition.key == key)
}

/// Stateless policy service over a profile's SettingsRepository.
#[derive(Clone, Copy)]
pub struct SettingsService<'a> {
    profiles: &'a ProfileService,
}

impl<'a> SettingsService<'a> {
    pub fn new(profiles: &'a ProfileService) -> Self {
        Self { profiles }
    }

    pub fn get(
        &self,
        context: &ProfileContext,
        key: &str,
    ) -> Result<ResolvedSetting, ServiceError> {
        let profile = self.profiles.profile_store(context)?;
        self.resolve(&profile, key)
    }

    pub fn list_group(
        &self,
        context: &ProfileContext,
        group: SettingGroup,
    ) -> Result<Vec<ResolvedSetting>, ServiceError> {
        let profile = self.profiles.profile_store(context)?;
        self.list_resolved(&profile, Some(group))
    }

    pub fn list_all(&self, context: &ProfileContext) -> Result<Vec<ResolvedSetting>, ServiceError> {
        let profile = self.profiles.profile_store(context)?;
        self.list_resolved(&profile, None)
    }

    pub fn set(
        &self,
        context: &ProfileContext,
        key: &str,
        value: SettingValue,
    ) -> Result<SettingsChangeSet, ServiceError> {
        self.set_many(context, &[(key, value)])
    }

    /// Validates every key/value first, then writes all changes atomically.
    pub fn set_many(
        &self,
        context: &ProfileContext,
        entries: &[(&str, SettingValue)],
    ) -> Result<SettingsChangeSet, ServiceError> {
        let profile = self.profiles.profile_store(context)?;
        let mut seen = BTreeMap::new();
        let mut validated = Vec::with_capacity(entries.len());
        let mut changes = Vec::with_capacity(entries.len());
        for (key, value) in entries {
            reject_sensitive_key(key)?;
            let definition =
                definition(key).ok_or_else(|| ServiceError::UnknownSetting((*key).to_string()))?;
            if seen.insert(*key, ()).is_some() {
                return Err(ServiceError::DuplicateSetting((*key).to_string()));
            }
            validate_value(definition, value)?;
            let old = profile.settings().get(key)?.map(|entry| entry.value);
            changes.push(SettingChange {
                key: (*key).to_string(),
                group: definition.group,
                old,
                new: value.clone(),
                source: SettingSource::User,
            });
            validated.push((definition.key, definition.group.as_str(), value.clone()));
        }
        profile.settings().set_many(&validated)?;
        Ok(SettingsChangeSet { changes })
    }

    pub fn reset_key(
        &self,
        context: &ProfileContext,
        key: &str,
    ) -> Result<SettingsChangeSet, ServiceError> {
        let profile = self.profiles.profile_store(context)?;
        let definition =
            definition(key).ok_or_else(|| ServiceError::UnknownSetting(key.to_string()))?;
        let old = profile.settings().get(key)?.map(|entry| entry.value);
        profile.settings().delete(key)?;
        Ok(SettingsChangeSet {
            changes: vec![SettingChange {
                key: key.to_string(),
                group: definition.group,
                old,
                new: definition.default.clone(),
                source: SettingSource::Default,
            }],
        })
    }

    pub fn reset_group(
        &self,
        context: &ProfileContext,
        group: SettingGroup,
    ) -> Result<SettingsChangeSet, ServiceError> {
        let profile = self.profiles.profile_store(context)?;
        let old_values = self.user_values(&profile, Some(group))?;
        profile.settings().delete_group(group.as_str())?;
        let mut changes = Vec::new();
        for definition in DEFINITIONS.iter().filter(|def| def.group == group) {
            changes.push(SettingChange {
                key: definition.key.to_string(),
                group,
                old: old_values
                    .get(definition.key)
                    .map(|entry| entry.value.clone()),
                new: definition.default.clone(),
                source: SettingSource::Default,
            });
        }
        Ok(SettingsChangeSet { changes })
    }

    pub fn reset_all(&self, context: &ProfileContext) -> Result<SettingsChangeSet, ServiceError> {
        let profile = self.profiles.profile_store(context)?;
        let old_values = self.user_values(&profile, None)?;
        profile.settings().delete_all()?;
        let changes = DEFINITIONS
            .iter()
            .map(|definition| SettingChange {
                key: definition.key.to_string(),
                group: definition.group,
                old: old_values
                    .get(definition.key)
                    .map(|entry| entry.value.clone()),
                new: definition.default.clone(),
                source: SettingSource::Default,
            })
            .collect();
        Ok(SettingsChangeSet { changes })
    }

    fn resolve(&self, profile: &ProfileStore, key: &str) -> Result<ResolvedSetting, ServiceError> {
        let definition =
            definition(key).ok_or_else(|| ServiceError::UnknownSetting(key.to_string()))?;
        let entry = profile.settings().get(key)?;
        Ok(match entry {
            Some(entry) => ResolvedSetting {
                key: entry.key,
                group: definition.group,
                value: entry.value,
                source: SettingSource::User,
                updated_at_ms: Some(entry.updated_at_ms),
            },
            None => ResolvedSetting {
                key: definition.key.to_string(),
                group: definition.group,
                value: definition.default.clone(),
                source: SettingSource::Default,
                updated_at_ms: None,
            },
        })
    }

    fn list_resolved(
        &self,
        profile: &ProfileStore,
        group: Option<SettingGroup>,
    ) -> Result<Vec<ResolvedSetting>, ServiceError> {
        let user_values = self.user_values(profile, group)?;
        Ok(DEFINITIONS
            .iter()
            .filter(|definition| group.is_none_or(|group| definition.group == group))
            .map(|definition| match user_values.get(definition.key) {
                Some(entry) => ResolvedSetting {
                    key: entry.key.clone(),
                    group: definition.group,
                    value: entry.value.clone(),
                    source: SettingSource::User,
                    updated_at_ms: Some(entry.updated_at_ms),
                },
                None => ResolvedSetting {
                    key: definition.key.to_string(),
                    group: definition.group,
                    value: definition.default.clone(),
                    source: SettingSource::Default,
                    updated_at_ms: None,
                },
            })
            .collect())
    }

    fn user_values(
        &self,
        profile: &ProfileStore,
        group: Option<SettingGroup>,
    ) -> Result<BTreeMap<String, SettingEntry>, ServiceError> {
        let page = profile
            .settings()
            .list(group.map(SettingGroup::as_str), 500, 0)?;
        Ok(page
            .items
            .into_iter()
            .map(|entry| (entry.key.clone(), entry))
            .collect())
    }
}

fn reject_sensitive_key(key: &str) -> Result<(), ServiceError> {
    let lower = key.to_ascii_lowercase();
    const SENSITIVE: &[&str] = &[
        "password",
        "passwd",
        "secret",
        "token",
        "api_key",
        "apikey",
        "credential",
        "private_key",
    ];
    if SENSITIVE.iter().any(|part| lower.contains(part)) {
        return Err(ServiceError::SensitiveSetting(key.to_string()));
    }
    Ok(())
}

fn validate_value(
    definition: &SettingDefinition,
    value: &SettingValue,
) -> Result<(), ServiceError> {
    match (&definition.rule, value) {
        (ValueRule::TextChoices(choices), SettingValue::Text(text)) => {
            if choices.contains(&text.as_str()) {
                Ok(())
            } else {
                Err(ServiceError::SettingOutOfRange(format!(
                    "{} must be one of {:?}",
                    definition.key, choices
                )))
            }
        }
        (ValueRule::Text { max_len }, SettingValue::Text(text)) => {
            if text.len() <= *max_len {
                Ok(())
            } else {
                Err(ServiceError::SettingOutOfRange(format!(
                    "{} must be <= {max_len} bytes",
                    definition.key
                )))
            }
        }
        (ValueRule::Boolean, SettingValue::Boolean(_)) => Ok(()),
        (ValueRule::IntegerRange { min, max }, SettingValue::Integer(value)) => {
            if value >= min && value <= max {
                Ok(())
            } else {
                Err(ServiceError::SettingOutOfRange(format!(
                    "{} must be between {min} and {max}",
                    definition.key
                )))
            }
        }
        (ValueRule::FloatRange { min, max }, SettingValue::Float(value)) => {
            if value.is_finite() && *value >= *min && *value <= *max {
                Ok(())
            } else {
                Err(ServiceError::SettingOutOfRange(format!(
                    "{} must be between {min} and {max}",
                    definition.key
                )))
            }
        }
        (ValueRule::Json, SettingValue::Json(text)) => {
            serde_json::from_str::<serde_json::Value>(text)
                .map(|_| ())
                .map_err(|_| ServiceError::InvalidSettingType(definition.key.to_string()))
        }
        _ => Err(ServiceError::InvalidSettingType(definition.key.to_string())),
    }
}
