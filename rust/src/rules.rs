//! Per-application rules, keyed by executable path (the previous
//! project's limit semantics: one shared limit per app across all PIDs).
//! Stored as JSON in the user data dir, written atomically (same
//! hardening as the previous project's SettingsStore: a corrupt file is
//! quarantined, never silently trusted).

use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::PathBuf;

#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
pub struct Rule {
    pub exe_path: String,
    pub name: String,
    /// KB/s (binary). None = unlimited.
    pub limit_down_kbps: Option<u32>,
    pub limit_up_kbps: Option<u32>,
    pub blocked: bool,
}

impl Rule {
    pub fn has_limit(&self) -> bool {
        self.limit_down_kbps.is_some() || self.limit_up_kbps.is_some()
    }
    pub fn is_empty(&self) -> bool {
        !self.has_limit() && !self.blocked
    }
}

pub fn user_data_dir() -> PathBuf {
    // Windows: %APPDATA%\FluxLimiter ; elsewhere: ~/.fluxlimiter
    let dir = match std::env::var_os("APPDATA") {
        Some(appdata) => PathBuf::from(appdata).join("FluxLimiter"),
        None => PathBuf::from(std::env::var("HOME").unwrap_or_else(|_| ".".into()))
            .join(".fluxlimiter"),
    };
    let _ = std::fs::create_dir_all(&dir);
    dir
}

#[derive(Debug, Default)]
pub struct RulesStore {
    pub path: PathBuf,
    rules: BTreeMap<String, Rule>, // key: lowercased exe path
}

impl RulesStore {
    pub fn load_default() -> Self {
        Self::load(user_data_dir().join("rules.json"))
    }

    pub fn load(path: PathBuf) -> Self {
        let mut store = Self { path, rules: BTreeMap::new() };
        if let Ok(text) = std::fs::read_to_string(&store.path) {
            match serde_json::from_str::<BTreeMap<String, Rule>>(&text) {
                Ok(rules) => store.rules = rules,
                Err(_) => {
                    // quarantine a corrupt file instead of crashing / trusting it
                    let bad = store.path.with_extension("json.corrupt");
                    let _ = std::fs::rename(&store.path, bad);
                }
            }
        }
        store
    }

    pub fn save(&self) {
        // atomic write: temp file + rename, like the previous project
        let tmp = self.path.with_extension("json.tmp");
        if let Ok(text) = serde_json::to_string_pretty(&self.rules) {
            if std::fs::write(&tmp, text).is_ok() {
                let _ = std::fs::rename(&tmp, &self.path);
            }
        }
    }

    pub fn get_or_default(&self, exe_path: &str, name: &str) -> Rule {
        self.rules
            .get(&exe_path.to_ascii_lowercase())
            .cloned()
            .unwrap_or_else(|| Rule {
                exe_path: exe_path.to_string(),
                name: name.to_string(),
                ..Default::default()
            })
    }

    /// An all-empty rule deletes the entry (protocol v1 semantics).
    pub fn set(&mut self, rule: Rule) {
        let key = rule.exe_path.to_ascii_lowercase();
        if rule.is_empty() {
            self.rules.remove(&key);
        } else {
            self.rules.insert(key, rule);
        }
        self.save();
    }

    pub fn all(&self) -> Vec<Rule> {
        self.rules.values().cloned().collect()
    }
}
