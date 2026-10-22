// What the network may put on this computer's screen and disk, and the rules that keep it harmless:
// labels without control or direction-switching characters, and file names Windows will not misread.
// Ported from CleanLabel and SafeFileName in lazo/src/NetworkEngine.cs.

use std::path::{Path, PathBuf};

/// Characters that flip the direction of text and let a name pretend to be another ("gpj.exe").
fn is_bidi_control(c: char) -> bool {
    matches!(c, '\u{202A}'..='\u{202E}' | '\u{2066}'..='\u{2069}' | '\u{200E}' | '\u{200F}' | '\u{061C}')
}

pub fn clean_label(value: &str, max: usize) -> String {
    value
        .chars()
        .filter(|c| !c.is_control() && *c != '|' && !is_bidi_control(*c))
        .take(max)
        .collect::<String>()
        .trim()
        .to_string()
}

pub fn clean_name(value: &str) -> String {
    clean_label(value, 60)
}

const RESERVED: [&str; 22] = [
    "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3",
    "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
];

/// A file name that is only a name: no path, no reserved device name, nothing invisible. "" if it is not.
pub fn safe_file_name(name: &str) -> String {
    let name = name.trim_end_matches([' ', '.']);
    let invalid = |c: char| c.is_control() || is_bidi_control(c) || ":*?\"<>|/\\".contains(c);
    if name.is_empty() || name.chars().count() > 200 || name == "." || name == ".." || name.chars().any(invalid) {
        return String::new();
    }
    let stem = name.split('.').next().unwrap_or("").trim().to_ascii_uppercase();
    if RESERVED.contains(&stem.as_str()) {
        return String::new();
    }
    if name.starts_with('.') && name.ends_with(".part") {
        return String::new();
    }
    name.to_string()
}

/// `folder\name`, or `folder\stem (1).ext`, `(2)`… when it already exists.
pub fn unique_destination(folder: &Path, name: &str) -> PathBuf {
    let candidate = folder.join(name);
    if !candidate.exists() {
        return candidate;
    }
    let path = Path::new(name);
    let stem = path.file_stem().map(|s| s.to_string_lossy().to_string()).unwrap_or_default();
    let ext = path.extension().map(|e| format!(".{}", e.to_string_lossy())).unwrap_or_default();
    let mut i = 1;
    loop {
        let next = folder.join(format!("{stem} ({i}){ext}"));
        if !next.exists() {
            return next;
        }
        i += 1;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn labels_lose_what_could_disguise_them() {
        assert_eq!(clean_label("  Ana|Pérez\u{202E}gpj.exe\n", 60), "AnaPérezgpj.exe");
        assert_eq!(clean_label("abcdef", 3), "abc");
        assert_eq!(clean_label("\u{0007}\u{200F}", 60), "");
    }

    #[test]
    fn only_plain_file_names_pass() {
        for ok in ["plano.pdf", "nube 2.las", "a.b.c", ".gitignore"] {
            assert_eq!(safe_file_name(ok), ok, "{ok}");
        }
        for bad in ["", ".", "..", "a/b", "a\\b", "C:evil", "con", "NUL.txt", "lpt1.dat", "x\u{202E}fdp.exe", "a?b", ".abc.part", &"x".repeat(201)] {
            assert_eq!(safe_file_name(bad), "", "{bad}");
        }
        assert_eq!(safe_file_name("informe. "), "informe", "trailing dots and spaces are dropped as Windows does");
    }

    #[test]
    fn a_name_in_use_gets_a_number() {
        let dir = std::env::temp_dir().join(format!("lazo-text-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(dir.join("a.txt"), "x").unwrap();
        assert_eq!(unique_destination(&dir, "b.txt"), dir.join("b.txt"));
        assert_eq!(unique_destination(&dir, "a.txt"), dir.join("a (1).txt"));
        std::fs::write(dir.join("a (1).txt"), "x").unwrap();
        assert_eq!(unique_destination(&dir, "a.txt"), dir.join("a (2).txt"));
        let _ = std::fs::remove_dir_all(&dir);
    }
}
