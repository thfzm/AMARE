//! Agent permission policy (Phase 1 scaffold).
//!
//! The actual planner/executor is Phase 4. This module makes the approval
//! defaults explicit and testable before any browser control is implemented.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AgentMode {
    Auto,
    SemiAuto,
    AlwaysAsk,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PermissionScope {
    CurrentTabRead,
    OtherTabRead,
    WebSearch,
    PageNavigate,
    PageInteract,
    TabManage,
    BookmarkWrite,
    LibraryRead,
    LibraryWrite,
    LibraryDelete,
    Download,
    FileModify,
    PublishSend,
    Payment,
    CredentialUse,
    ExtensionInstall,
}

pub fn requires_approval(scope: PermissionScope, mode: AgentMode) -> bool {
    match mode {
        AgentMode::AlwaysAsk => true,
        AgentMode::Auto => matches!(
            scope,
            PermissionScope::LibraryDelete
                | PermissionScope::FileModify
                | PermissionScope::PublishSend
                | PermissionScope::Payment
                | PermissionScope::CredentialUse
        ),
        AgentMode::SemiAuto => matches!(
            scope,
            PermissionScope::OtherTabRead
                | PermissionScope::TabManage
                | PermissionScope::BookmarkWrite
                | PermissionScope::LibraryRead
                | PermissionScope::LibraryWrite
                | PermissionScope::LibraryDelete
                | PermissionScope::Download
                | PermissionScope::FileModify
                | PermissionScope::PublishSend
                | PermissionScope::Payment
                | PermissionScope::CredentialUse
                | PermissionScope::ExtensionInstall
        ),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn semi_auto_allows_reads_and_navigation() {
        assert!(!requires_approval(
            PermissionScope::CurrentTabRead,
            AgentMode::SemiAuto
        ));
        assert!(!requires_approval(
            PermissionScope::PageNavigate,
            AgentMode::SemiAuto
        ));
        assert!(!requires_approval(
            PermissionScope::WebSearch,
            AgentMode::SemiAuto
        ));
        assert!(!requires_approval(
            PermissionScope::PageInteract,
            AgentMode::SemiAuto
        ));
    }

    #[test]
    fn side_effects_always_require_approval() {
        for scope in [
            PermissionScope::LibraryDelete,
            PermissionScope::FileModify,
            PermissionScope::PublishSend,
            PermissionScope::Payment,
            PermissionScope::CredentialUse,
        ] {
            assert!(requires_approval(scope, AgentMode::SemiAuto));
            assert!(requires_approval(scope, AgentMode::Auto));
        }
    }

    #[test]
    fn always_ask_requires_everything() {
        assert!(requires_approval(
            PermissionScope::CurrentTabRead,
            AgentMode::AlwaysAsk
        ));
    }
}
