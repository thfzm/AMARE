use amare_core::{Bookmark, HistoryEntry, NewBookmark, NewHistoryVisit, ProfileContext};
use amare_services::ProfileService;
use amare_store::FolderScope;
use std::{cell::RefCell, path::PathBuf, sync::Arc};

thread_local! {
    static LIBRARY: RefCell<Option<(Arc<ProfileService>, ProfileContext)>> = const { RefCell::new(None) };
}

fn data_root() -> PathBuf {
    if let Ok(path) = std::env::var("AMARE_GPUI_DATA_DIR") {
        return PathBuf::from(path);
    }
    if let Ok(appdata) = std::env::var("APPDATA") {
        return PathBuf::from(appdata).join("AMARE");
    }
    std::env::temp_dir().join("amare-gpui")
}

fn with_library<R>(f: impl FnOnce(&ProfileService, &ProfileContext) -> Result<R, String>) -> Result<R, String> {
    LIBRARY.with(|cell| {
        if cell.borrow().is_none() {
            let service = Arc::new(ProfileService::open(data_root()).map_err(|e| e.to_string())?);
            let context = service.initialize().map_err(|e| e.to_string())?;
            *cell.borrow_mut() = Some((service, context));
        }
        let (service, context) = cell.borrow().as_ref().expect("library initialized");
        f(service, context)
    })
}

pub fn bookmarks() -> Result<Vec<Bookmark>, String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        let page = profile.bookmarks().list(FolderScope::All, 500, 0).map_err(|e| e.to_string())?;
        Ok(page.items)
    })
}

pub fn toggle_bookmark(title: &str, url: &str) -> Result<(bool, Vec<Bookmark>), String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        let page = profile.bookmarks().list(FolderScope::All, 500, 0).map_err(|e| e.to_string())?;
        let existing = page.items.iter().find(|bookmark| bookmark.url == url).cloned();
        if let Some(bookmark) = existing {
            profile.bookmarks().delete(bookmark.id).map_err(|e| e.to_string())?;
            let page = profile.bookmarks().list(FolderScope::All, 500, 0).map_err(|e| e.to_string())?;
            return Ok((false, page.items));
        }
        let title = title.chars().take(512).collect::<String>();
        let url = url.chars().take(4096).collect::<String>();
        profile
            .bookmarks()
            .create(NewBookmark { title, url, folder_id: None, position: None })
            .map_err(|e| e.to_string())?;
        let page = profile.bookmarks().list(FolderScope::All, 500, 0).map_err(|e| e.to_string())?;
        Ok((true, page.items))
    })
}

pub fn delete_bookmark(id: i64) -> Result<Vec<Bookmark>, String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        profile.bookmarks().delete(id).map_err(|e| e.to_string())?;
        let page = profile.bookmarks().list(FolderScope::All, 500, 0).map_err(|e| e.to_string())?;
        Ok(page.items)
    })
}

pub fn history(query: &str) -> Result<Vec<HistoryEntry>, String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        let query = query.trim();
        let page = profile
            .history()
            .search(if query.is_empty() { None } else { Some(query) }, None, None, 500, 0)
            .map_err(|e| e.to_string())?;
        Ok(page.items)
    })
}

pub fn record_history(title: &str, url: &str, visited_at_ms: i64) -> Result<(), String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        profile
            .history()
            .record_visit(NewHistoryVisit {
                url: url.chars().take(4096).collect(),
                title: title.chars().take(1024).collect(),
                visited_at_ms,
            })
            .map_err(|e| e.to_string())
    })
}

pub fn delete_history(url: &str) -> Result<Vec<HistoryEntry>, String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        profile.history().delete_url(url).map_err(|e| e.to_string())?;
        let page = profile.history().search(None, None, None, 500, 0).map_err(|e| e.to_string())?;
        Ok(page.items)
    })
}

pub fn clear_history() -> Result<Vec<HistoryEntry>, String> {
    with_library(|service, context| {
        let profile = service.profile_store(context).map_err(|e| e.to_string())?;
        profile.history().clear().map_err(|e| e.to_string())?;
        let page = profile.history().search(None, None, None, 500, 0).map_err(|e| e.to_string())?;
        Ok(page.items)
    })
}
