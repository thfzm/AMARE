//! AI connection policy (Phase 1 scaffold).
//!
//! General chat defaults to a user-selected official web AI site. API access is
//! optional and is the reliable path for Agent tool calling.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AiConnectionMode {
    /// User-selected official web AI site (ChatGPT/Gemini/Claude/...).
    WebAi,
    /// User-configured API provider.
    Api,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProviderKind {
    OpenAiCompatible,
    Anthropic,
    Gemini,
    Ollama,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AiSettings {
    pub mode: AiConnectionMode,
    pub provider: Option<ProviderKind>,
    /// One default API model is shared by chat and Agent; per-task overrides
    /// may be added later.
    pub default_model: Option<String>,
}

impl Default for AiSettings {
    fn default() -> Self {
        Self {
            mode: AiConnectionMode::WebAi,
            provider: None,
            default_model: None,
        }
    }
}

impl AiSettings {
    pub fn agent_available(&self) -> bool {
        self.mode == AiConnectionMode::Api
            && self.provider.is_some()
            && self.default_model.is_some()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_is_web_ai_without_api() {
        let settings = AiSettings::default();
        assert_eq!(settings.mode, AiConnectionMode::WebAi);
        assert!(!settings.agent_available());
    }

    #[test]
    fn agent_requires_api_provider_and_model() {
        let mut settings = AiSettings {
            mode: AiConnectionMode::Api,
            provider: Some(ProviderKind::OpenAiCompatible),
            default_model: None,
        };
        assert!(!settings.agent_available());
        settings.default_model = Some("default-model".into());
        assert!(settings.agent_available());
    }
}
