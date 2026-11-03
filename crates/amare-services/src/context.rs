use amare_core::ProfileId;

/// A validated snapshot of the active profile.
///
/// The generation is incremented on every profile switch, deletion of the
/// active profile, or external invalidation. Services reject stale contexts so
/// work started for one profile can never be applied to another.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProfileContext {
    pub profile_id: ProfileId,
    pub generation: u64,
}

impl ProfileContext {
    pub fn profile_id(&self) -> &ProfileId {
        &self.profile_id
    }

    pub fn generation(&self) -> u64 {
        self.generation
    }
}
