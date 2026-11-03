use crate::context::ProfileContext;
use crate::error::ServiceError;
use crate::profile::ProfileService;
use amare_core::{ProfileId, SearchHit, SearchKind};
use amare_store::ProfileStore;

const MAX_QUERY_BYTES: usize = 512;
const MAX_PAGE_LIMIT: u32 = 100;
const MAX_PAGE_OFFSET: u64 = 400;
const ALL_KINDS: [SearchKind; 10] = [
    SearchKind::LibraryItem,
    SearchKind::Tag,
    SearchKind::Collection,
    SearchKind::Conversation,
    SearchKind::Message,
    SearchKind::Workspace,
    SearchKind::Task,
    SearchKind::TaskMemory,
    SearchKind::Bookmark,
    SearchKind::History,
];

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SearchSort {
    /// Keep the source/rank order produced by FTS and repository search.
    Relevance,
    TitleAsc,
    UpdatedDesc,
}

#[derive(Debug, Clone)]
pub struct SearchRequest {
    pub query: String,
    pub kinds: Vec<SearchKind>,
    pub limit: u32,
    pub offset: u64,
    pub sort: SearchSort,
}

impl SearchRequest {
    pub fn new(query: impl Into<String>) -> Self {
        Self {
            query: query.into(),
            kinds: Vec::new(),
            limit: 20,
            offset: 0,
            sort: SearchSort::Relevance,
        }
    }

    pub fn with_kinds(mut self, kinds: impl IntoIterator<Item = SearchKind>) -> Self {
        self.kinds = kinds.into_iter().collect();
        self
    }

    pub fn with_page(mut self, limit: u32, offset: u64) -> Self {
        self.limit = limit;
        self.offset = offset;
        self
    }

    pub fn with_sort(mut self, sort: SearchSort) -> Self {
        self.sort = sort;
        self
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SearchPage {
    pub profile_id: ProfileId,
    pub query: String,
    pub items: Vec<SearchHit>,
    pub total: u64,
    pub limit: u32,
    pub offset: u64,
    pub sort: SearchSort,
}

/// Cross-domain search service. It validates the active profile context, then
/// combines FTS5 results with bookmark/history repository search.
#[derive(Clone, Copy)]
pub struct SearchService<'a> {
    profiles: &'a ProfileService,
}

impl<'a> SearchService<'a> {
    pub fn new(profiles: &'a ProfileService) -> Self {
        Self { profiles }
    }

    pub fn search(
        &self,
        context: &ProfileContext,
        request: &SearchRequest,
    ) -> Result<SearchPage, ServiceError> {
        let query = request.query.trim();
        if query.is_empty() {
            return Err(ServiceError::EmptyQuery);
        }
        if query.len() > MAX_QUERY_BYTES {
            return Err(ServiceError::QueryTooLong {
                max: MAX_QUERY_BYTES,
            });
        }
        if !(1..=MAX_PAGE_LIMIT).contains(&request.limit) {
            return Err(ServiceError::InvalidLimit {
                max: MAX_PAGE_LIMIT,
            });
        }
        if request.offset > MAX_PAGE_OFFSET {
            return Err(ServiceError::OffsetTooLarge {
                max: MAX_PAGE_OFFSET,
            });
        }
        let per_source = (request.offset + request.limit as u64) as u32;
        let profile = self.profiles.profile_store(context)?;
        let kinds = selected_kinds(&request.kinds);
        let (mut hits, total) = self.collect(&profile, query, &kinds, per_source)?;
        sort_hits(&mut hits, request.sort);
        let start = request.offset as usize;
        let items = hits
            .into_iter()
            .skip(start)
            .take(request.limit as usize)
            .collect();
        Ok(SearchPage {
            profile_id: context.profile_id.clone(),
            query: query.to_string(),
            items,
            total,
            limit: request.limit,
            offset: request.offset,
            sort: request.sort,
        })
    }

    fn collect(
        &self,
        profile: &ProfileStore,
        query: &str,
        kinds: &[SearchKind],
        per_source: u32,
    ) -> Result<(Vec<SearchHit>, u64), ServiceError> {
        let fts_kinds: Vec<SearchKind> = kinds
            .iter()
            .copied()
            .filter(|kind| !matches!(kind, SearchKind::Bookmark | SearchKind::History))
            .collect();
        let mut hits = profile
            .search()
            .search_kinds(query, &fts_kinds, per_source)?;
        let mut total = profile.search().count_kinds(query, &fts_kinds)?;

        if kinds.contains(&SearchKind::Bookmark) {
            let page = profile.bookmarks().search(query, per_source, 0)?;
            total += page.total;
            hits.extend(page.items.into_iter().map(|bookmark| SearchHit {
                kind: SearchKind::Bookmark,
                id: bookmark.id,
                title: bookmark.title,
                snippet: Some(bookmark.url),
                updated_at_ms: Some(bookmark.updated_at_ms),
            }));
        }
        if kinds.contains(&SearchKind::History) {
            let page = profile
                .history()
                .search(Some(query), None, None, per_source, 0)?;
            total += page.total;
            hits.extend(page.items.into_iter().map(|entry| SearchHit {
                kind: SearchKind::History,
                id: entry.id,
                title: entry.title,
                snippet: Some(entry.url),
                updated_at_ms: Some(entry.last_visited_at_ms),
            }));
        }
        Ok((hits, total))
    }
}

fn selected_kinds(requested: &[SearchKind]) -> Vec<SearchKind> {
    if requested.is_empty() {
        return ALL_KINDS.to_vec();
    }
    let mut out = Vec::new();
    for kind in requested {
        if !out.contains(kind) {
            out.push(*kind);
        }
    }
    out
}

fn sort_hits(hits: &mut [SearchHit], sort: SearchSort) {
    match sort {
        SearchSort::Relevance => {}
        SearchSort::TitleAsc => hits.sort_by(|a, b| {
            a.title
                .to_lowercase()
                .cmp(&b.title.to_lowercase())
                .then_with(|| kind_rank(a.kind).cmp(&kind_rank(b.kind)))
                .then_with(|| a.id.cmp(&b.id))
        }),
        SearchSort::UpdatedDesc => hits.sort_by(|a, b| {
            b.updated_at_ms
                .unwrap_or(0)
                .cmp(&a.updated_at_ms.unwrap_or(0))
                .then_with(|| kind_rank(a.kind).cmp(&kind_rank(b.kind)))
                .then_with(|| a.id.cmp(&b.id))
        }),
    }
}

fn kind_rank(kind: SearchKind) -> u8 {
    match kind {
        SearchKind::LibraryItem => 0,
        SearchKind::Tag => 1,
        SearchKind::Collection => 2,
        SearchKind::Conversation => 3,
        SearchKind::Message => 4,
        SearchKind::Workspace => 5,
        SearchKind::Task => 6,
        SearchKind::TaskMemory => 7,
        SearchKind::Bookmark => 8,
        SearchKind::History => 9,
    }
}
