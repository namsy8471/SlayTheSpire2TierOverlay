//! Port of AdvisorCommand.cs: how to start the helper.

use std::path::{Path, PathBuf};

/// The helper exe plus optional leading arguments (e.g. a wrapper). The overlay appends [`AdvisorCommand::live_args`].
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct AdvisorCommand {
    /// Full path of the exe to start.
    pub file_name: PathBuf,
    /// Arguments in front of `live` (from the `combatAdvisorCommand` setting).
    pub leading_args: Vec<String>,
    /// Where it came from, for the log: "setting" or "default".
    pub source: &'static str,
}

/// The flag that asks the helper to exit when its stdin closes (the host keeps stdin open for the helper's life).
pub const EXIT_ON_STDIN_EOF: &str = "--exit-on-stdin-eof";

impl AdvisorCommand {
    /// The default place, relative to the overlay exe's folder.
    pub fn default_relative_path() -> PathBuf {
        Path::new("advisor").join("sts2-advisor.exe")
    }

    /// The protocol's arguments for following the live game. `data`: the helper's data folder (decide policies).
    ///
    /// Deliberate difference from `ADVISOR_PROTOCOL.md` §5: no `--protocol 2`. The Rust helper does not know that flag
    /// (it would exit); it answers v2 `decide` lines whenever they come.
    pub fn live_args(save_path: &str, log_path: &str, data: Option<&str>) -> Vec<String> {
        let mut a: Vec<String> = vec!["live".into(), "--save".into(), save_path.into(), "--log".into(), log_path.into(), EXIT_ON_STDIN_EOF.into()];
        if let Some(d) = data {
            a.push("--data".into());
            a.push(d.into());
        }
        a
    }

    /// The whole argument list of the process: leading args, then `args`.
    pub fn command_args(&self, args: &[String]) -> Vec<String> {
        self.leading_args.iter().cloned().chain(args.iter().cloned()).collect()
    }

    /// Finds the helper: (1) `setting` (a full exe path, optionally quoted, plus extra leading args), (2) the default
    /// place next to the overlay exe. No command: the feature is off. The warning says why a setting was not used.
    pub fn locate(setting: Option<&str>, base_dir: &Path, file_exists: &dyn Fn(&Path) -> bool) -> (Option<AdvisorCommand>, Option<String>) {
        let mut warning = None;
        if let Some(setting) = setting.filter(|s| !s.trim().is_empty()) {
            let parts = split_command_line(setting);
            if parts.is_empty() {
                warning = Some("combatAdvisorCommand is empty".to_string());
            } else if !is_fully_qualified(&parts[0]) {
                warning = Some("combatAdvisorCommand must start with a full path to an exe".to_string());
            } else if !file_exists(Path::new(&parts[0])) {
                warning = Some("combatAdvisorCommand points to a file that does not exist".to_string());
            } else {
                return (Some(AdvisorCommand { file_name: PathBuf::from(&parts[0]), leading_args: parts[1..].to_vec(), source: "setting" }), None);
            }
        }
        let fallback = base_dir.join(Self::default_relative_path());
        let found = file_exists(&fallback).then(|| AdvisorCommand { file_name: fallback, leading_args: Vec::new(), source: "default" });
        (found, warning)
    }
}

/// Path.IsPathFullyQualified on Windows: `X:\...`, `X:/...` or a UNC/device path. A rooted Unix path also counts
/// (development on Linux).
pub fn is_fully_qualified(p: &str) -> bool {
    let b = p.as_bytes();
    if b.len() >= 2 && (b[0] == b'\\' || b[0] == b'/') && (b[1] == b'\\' || b[1] == b'/') {
        return true;
    }
    if b.len() >= 3 && b[0].is_ascii_alphabetic() && b[1] == b':' && (b[2] == b'\\' || b[2] == b'/') {
        return true;
    }
    !cfg!(windows) && p.starts_with('/')
}

/// Splits a command line: whitespace separates, double quotes group (and are removed), `""` inside quotes is a
/// literal quote. Backslashes are plain characters.
pub fn split_command_line(line: &str) -> Vec<String> {
    let c: Vec<char> = line.chars().collect();
    let mut args = Vec::new();
    let mut cur = String::new();
    let mut in_quotes = false;
    let mut has_arg = false;
    let mut i = 0;
    while i < c.len() {
        let ch = c[i];
        if ch == '"' {
            if in_quotes && i + 1 < c.len() && c[i + 1] == '"' {
                cur.push('"');
                i += 1;
            } else {
                in_quotes = !in_quotes;
            }
            has_arg = true;
        } else if ch.is_whitespace() && !in_quotes {
            if has_arg {
                args.push(std::mem::take(&mut cur));
            }
            cur.clear();
            has_arg = false;
        } else {
            cur.push(ch);
            has_arg = true;
        }
        i += 1;
    }
    if has_arg {
        args.push(cur);
    }
    args
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn splits_like_the_csharp_helper() {
        assert_eq!(split_command_line(r#""C:\Program Files\x\a.exe" C:\y\b.dll  "" "say ""hi""""#), vec![
            r"C:\Program Files\x\a.exe".to_string(),
            r"C:\y\b.dll".into(),
            "".into(),
            r#"say "hi""#.into()
        ]);
        assert!(split_command_line("   ").is_empty());
    }

    #[test]
    fn locate_prefers_a_valid_setting_and_falls_back() {
        let exists = |p: &Path| p.to_string_lossy().contains("ok");
        let base = Path::new("/base-ok");
        let (c, w) = AdvisorCommand::locate(Some(r#""C:\ok\h.exe" --x"#), base, &exists);
        assert_eq!(c.as_ref().map(|c| c.source), Some("setting"));
        assert_eq!(c.unwrap().leading_args, vec!["--x".to_string()]);
        assert!(w.is_none());
        let (c, w) = AdvisorCommand::locate(Some("relative.exe"), base, &exists);
        assert_eq!(c.map(|c| c.source), Some("default"));
        assert_eq!(w.as_deref(), Some("combatAdvisorCommand must start with a full path to an exe"));
        let (c, w) = AdvisorCommand::locate(Some(r"C:\nope\h.exe"), Path::new("/nothing"), &exists);
        assert!(c.is_none());
        assert_eq!(w.as_deref(), Some("combatAdvisorCommand points to a file that does not exist"));
    }
}
