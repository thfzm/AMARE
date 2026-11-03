//! Private database row models. Repository APIs expose `amare_core` domain
//! types instead of these structs.

use amare_core::{
    Bookmark, BookmarkFolder, HistoryEntry, Profile, ProfileId, Session, SessionTab, SettingEntry,
    SettingValue,
};

pub(crate) struct ProfileRow {
    pub id: String,
    pub name: String,
    pub icon: Option<String>,
    pub is_default: bool,
    pub created_at_ms: i64,
    pub updated_at_ms: i64,
}

impl ProfileRow {
    pub fn into_domain(self) -> Profile {
        Profile {
            id: ProfileId::new(self.id),
            name: self.name,
            icon: self.icon,
            is_default: self.is_default,
            created_at_ms: self.created_at_ms,
            updated_at_ms: self.updated_at_ms,
        }
    }
}

pub(crate) struct SettingRow {
    pub key: String,
    pub group_name: String,
    pub value_type: String,
    pub value_text: String,
    pub updated_at_ms: i64,
}

impl SettingRow {
    pub fn into_domain(self) -> Result<SettingEntry, String> {
        let value = match self.value_type.as_str() {
            "text" => SettingValue::Text(self.value_text),
            "integer" => SettingValue::Integer(
                self.value_text
                    .parse()
                    .map_err(|_| "invalid integer setting".to_string())?,
            ),
            "float" => SettingValue::Float(
                self.value_text
                    .parse()
                    .map_err(|_| "invalid float setting".to_string())?,
            ),
            "boolean" => SettingValue::Boolean(match self.value_text.as_str() {
                "1" | "true" => true,
                "0" | "false" => false,
                _ => return Err("invalid boolean setting".to_string()),
            }),
            "json" => SettingValue::Json(self.value_text),
            other => return Err(format!("unknown setting type: {other}")),
        };
        Ok(SettingEntry {
            key: self.key,
            group: self.group_name,
            value,
            updated_at_ms: self.updated_at_ms,
        })
    }
}

pub(crate) struct BookmarkRow {
    pub id: i64,
    pub title: String,
    pub url: String,
    pub folder_id: Option<i64>,
    pub position: i64,
    pub created_at_ms: i64,
    pub updated_at_ms: i64,
}

impl BookmarkRow {
    pub fn into_domain(self, profile_id: &ProfileId) -> Bookmark {
        Bookmark {
            id: self.id,
            profile_id: profile_id.clone(),
            title: self.title,
            url: self.url,
            folder_id: self.folder_id,
            position: self.position,
            created_at_ms: self.created_at_ms,
            updated_at_ms: self.updated_at_ms,
        }
    }
}

pub(crate) struct FolderRow {
    pub id: i64,
    pub parent_id: Option<i64>,
    pub name: String,
    pub position: i64,
    pub created_at_ms: i64,
    pub updated_at_ms: i64,
}

impl FolderRow {
    pub fn into_domain(self, profile_id: &ProfileId) -> BookmarkFolder {
        BookmarkFolder {
            id: self.id,
            profile_id: profile_id.clone(),
            parent_id: self.parent_id,
            name: self.name,
            position: self.position,
            created_at_ms: self.created_at_ms,
            updated_at_ms: self.updated_at_ms,
        }
    }
}

pub(crate) struct HistoryRow {
    pub id: i64,
    pub url: String,
    pub title: String,
    pub first_visited_at_ms: i64,
    pub last_visited_at_ms: i64,
    pub visit_count: i64,
}

impl HistoryRow {
    pub fn into_domain(self, profile_id: &ProfileId) -> HistoryEntry {
        HistoryEntry {
            id: self.id,
            profile_id: profile_id.clone(),
            url: self.url,
            title: self.title,
            first_visited_at_ms: self.first_visited_at_ms,
            last_visited_at_ms: self.last_visited_at_ms,
            visit_count: self.visit_count.max(0) as u64,
        }
    }
}

pub(crate) struct SessionRow {
    pub id: i64,
    pub name: String,
    pub active_tab_id: Option<i64>,
    pub created_at_ms: i64,
    pub updated_at_ms: i64,
}

impl SessionRow {
    pub fn into_domain(self, profile_id: &ProfileId) -> Session {
        Session {
            id: self.id,
            profile_id: profile_id.clone(),
            name: self.name,
            active_tab_id: self.active_tab_id,
            created_at_ms: self.created_at_ms,
            updated_at_ms: self.updated_at_ms,
        }
    }
}

pub(crate) struct SessionTabRow {
    pub id: i64,
    pub session_id: i64,
    pub url: String,
    pub title: String,
    pub position: i64,
    pub pinned: bool,
    pub group_id: Option<String>,
    pub last_accessed_at_ms: i64,
}

impl SessionTabRow {
    pub fn into_domain(self) -> SessionTab {
        SessionTab {
            id: self.id,
            session_id: self.session_id,
            url: self.url,
            title: self.title,
            position: self.position,
            pinned: self.pinned,
            group_id: self.group_id,
            last_accessed_at_ms: self.last_accessed_at_ms,
        }
    }
}
