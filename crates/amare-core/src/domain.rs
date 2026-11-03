//! Platform-independent domain models used by the storage and UI layers.
//!
//! These types intentionally contain no SQLite or UI types. IDs are opaque
//! strings/64-bit values owned by AMARE, not engine handles.

use serde::{Deserialize, Serialize};

pub type TimestampMs = i64;

#[derive(Debug, Clone, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct ProfileId(String);

impl ProfileId {
    pub fn new(value: impl Into<String>) -> Self {
        Self(value.into())
    }

    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl std::fmt::Display for ProfileId {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Profile {
    pub id: ProfileId,
    pub name: String,
    #[serde(default)]
    pub icon: Option<String>,
    pub is_default: bool,
    pub created_at_ms: TimestampMs,
    pub updated_at_ms: TimestampMs,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub enum SettingValue {
    Text(String),
    Integer(i64),
    Float(f64),
    Boolean(bool),
    /// JSON encoded as text. Validation/serialization is the caller's job.
    Json(String),
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SettingEntry {
    pub key: String,
    pub group: String,
    pub value: SettingValue,
    pub updated_at_ms: TimestampMs,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Bookmark {
    pub id: i64,
    pub profile_id: ProfileId,
    pub title: String,
    pub url: String,
    pub folder_id: Option<i64>,
    pub position: i64,
    pub created_at_ms: TimestampMs,
    pub updated_at_ms: TimestampMs,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BookmarkFolder {
    pub id: i64,
    pub profile_id: ProfileId,
    pub parent_id: Option<i64>,
    pub name: String,
    pub position: i64,
    pub created_at_ms: TimestampMs,
    pub updated_at_ms: TimestampMs,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct HistoryEntry {
    pub id: i64,
    pub profile_id: ProfileId,
    pub url: String,
    pub title: String,
    pub first_visited_at_ms: TimestampMs,
    pub last_visited_at_ms: TimestampMs,
    pub visit_count: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Session {
    pub id: i64,
    pub profile_id: ProfileId,
    pub name: String,
    pub active_tab_id: Option<i64>,
    pub created_at_ms: TimestampMs,
    pub updated_at_ms: TimestampMs,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SessionTab {
    pub id: i64,
    pub session_id: i64,
    pub url: String,
    pub title: String,
    pub position: i64,
    pub pinned: bool,
    pub group_id: Option<String>,
    pub last_accessed_at_ms: TimestampMs,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SessionState {
    pub session: Session,
    pub tabs: Vec<SessionTab>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NewBookmark {
    pub title: String,
    pub url: String,
    pub folder_id: Option<i64>,
    /// `None` appends to the end of the folder.
    pub position: Option<i64>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BookmarkUpdate {
    pub title: String,
    pub url: String,
    pub folder_id: Option<i64>,
    pub position: i64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NewSessionTab {
    pub url: String,
    pub title: String,
    pub pinned: bool,
    pub group_id: Option<String>,
    /// `None` appends in the supplied order.
    pub position: Option<i64>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NewHistoryVisit {
    pub url: String,
    pub title: String,
    pub visited_at_ms: TimestampMs,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Page<T> {
    pub items: Vec<T>,
    pub total: u64,
    pub limit: u32,
    pub offset: u64,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn setting_value_preserves_type_variants() {
        let values = [
            SettingValue::Text("v".into()),
            SettingValue::Integer(42),
            SettingValue::Float(1.5),
            SettingValue::Boolean(true),
            SettingValue::Json("{\"a\":1}".into()),
        ];
        assert_eq!(values.len(), 5);
    }
}

// ---------------------------------------------------------------------------
// Phase 1.6: Library, conversations, workspace memory
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum LibraryKind {
    WebPage,
    Link,
    Excerpt,
    Note,
    Image,
    Pdf,
    Document,
    Research,
    Summary,
    Other,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum LibraryStatus {
    Active,
    Archived,
    Trashed,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct LibraryItem {
    pub id: i64,
    pub title: String,
    pub description: String,
    pub kind: LibraryKind,
    pub original_url: Option<String>,
    pub source_name: Option<String>,
    pub image_ref: Option<String>,
    pub user_note: String,
    pub created_at_ms: TimestampMs,
    pub updated_at_ms: TimestampMs,
    pub last_accessed_at_ms: TimestampMs,
    pub favorite: bool,
    pub status: LibraryStatus,
    pub sort_order: i64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NewLibraryItem {
    pub title: String,
    pub description: String,
    pub kind: LibraryKind,
    pub original_url: Option<String>,
    pub source_name: Option<String>,
    pub image_ref: Option<String>,
    pub user_note: String,
    pub favorite: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct LibraryUpdate {
    pub title: String,
    pub description: String,
    pub kind: LibraryKind,
    pub original_url: Option<String>,
    pub source_name: Option<String>,
    pub image_ref: Option<String>,
    pub user_note: String,
    pub favorite: bool,
    pub status: LibraryStatus,
    pub sort_order: i64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Tag {
    pub id: i64,
    pub name: String,
    pub created_at_ms: TimestampMs,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Collection {
    pub id: i64,
    pub name: String,
    pub description: String,
    pub created_at_ms: TimestampMs,
    pub updated_at_ms: TimestampMs,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SourceReference {
    pub id: i64,
    pub url: String,
    pub page_title: String,
    pub site_name: String,
    pub quote_text: String,
    pub location_info: String,
    pub collected_at_ms: TimestampMs,
    pub library_item_id: Option<i64>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NewSourceReference {
    pub url: String,
    pub page_title: String,
    pub site_name: String,
    pub quote_text: String,
    pub location_info: String,
    pub library_item_id: Option<i64>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum MessageRole {
    System,
    User,
    Assistant,
    Tool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum MessageStatus {
    Pending,
    Complete,
    Error,
    Cancelled,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum MessageVisibility {
    /// Shown in the normal conversation transcript.
    User,
    /// Hidden policy/tool plumbing. Kept for audit/replay but not rendered as chat.
    Internal,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum MessagePartKind {
    Text,
    Code,
    Image,
    Source,
    Citation,
    ToolResult,
    Error,
    Attachment,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct MessagePart {
    pub id: i64,
    pub message_id: i64,
    pub kind: MessagePartKind,
    pub text: String,
    pub reference: Option<String>,
    pub metadata_json: Option<String>,
    pub position: i64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NewMessagePart {
    pub kind: MessagePartKind,
    pub text: String,
    pub reference: Option<String>,
    pub metadata_json: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Message {
    pub id: i64,
    pub conversation_id: i64,
    pub role: MessageRole,
    pub body: String,
    pub status: MessageStatus,
    pub visibility: MessageVisibility,
    pub position: i64,
    pub created_at_ms: TimestampMs,
    pub updated_at_ms: TimestampMs,
    pub parts: Vec<MessagePart>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NewMessage {
    pub role: MessageRole,
    pub body: String,
    pub status: MessageStatus,
    pub visibility: MessageVisibility,
    pub parts: Vec<NewMessagePart>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Conversation {
    pub id: i64,
    pub title: String,
    pub workspace_id: Option<i64>,
    pub task_id: Option<i64>,
    pub archived: bool,
    pub deleted_at_ms: Option<TimestampMs>,
    pub created_at_ms: TimestampMs,
    pub updated_at_ms: TimestampMs,
    pub last_message_at_ms: TimestampMs,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NewConversation {
    pub title: String,
    pub workspace_id: Option<i64>,
    pub task_id: Option<i64>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Workspace {
    pub id: i64,
    pub name: String,
    pub description: String,
    pub icon: Option<String>,
    pub archived: bool,
    pub created_at_ms: TimestampMs,
    pub updated_at_ms: TimestampMs,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NewWorkspace {
    pub name: String,
    pub description: String,
    pub icon: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum TaskStatus {
    Open,
    InProgress,
    Done,
    Archived,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Task {
    pub id: i64,
    pub workspace_id: i64,
    pub title: String,
    pub description: String,
    pub status: TaskStatus,
    pub created_at_ms: TimestampMs,
    pub updated_at_ms: TimestampMs,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NewTask {
    pub workspace_id: i64,
    pub title: String,
    pub description: String,
    pub status: TaskStatus,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum MemoryType {
    ResearchResult,
    Decision,
    UserRule,
    Fact,
    Progress,
    OpenQuestion,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TaskMemory {
    pub id: i64,
    pub task_id: i64,
    pub title: String,
    pub content: String,
    pub memory_type: MemoryType,
    pub source_reference_id: Option<i64>,
    pub importance: i64,
    pub user_confirmed: bool,
    pub created_at_ms: TimestampMs,
    pub updated_at_ms: TimestampMs,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NewTaskMemory {
    pub task_id: i64,
    pub title: String,
    pub content: String,
    pub memory_type: MemoryType,
    pub source_reference_id: Option<i64>,
    pub importance: i64,
    pub user_confirmed: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SearchKind {
    LibraryItem,
    Tag,
    Collection,
    Conversation,
    Message,
    Workspace,
    Task,
    TaskMemory,
    Bookmark,
    History,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SearchHit {
    pub kind: SearchKind,
    pub id: i64,
    pub title: String,
    pub snippet: Option<String>,
    #[serde(default)]
    pub updated_at_ms: Option<TimestampMs>,
}
