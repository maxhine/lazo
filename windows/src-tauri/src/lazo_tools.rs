// Lazo's own tools — point clouds to ReCap, Xpace, Descanso Visual — are the classic Lazo program's,
// as they are: they work, so they are not rewritten. The island opens each one in its own window by
// starting that program in tool mode (`Lazo.exe --tool=<name>`, lazo/src/Program.cs): no network, no
// tray icon, no shortcuts, so it does not compete with the island's engine for the ports.

use std::path::PathBuf;

/// The tools the classic program knows how to open alone.
const TOOLS: [&str; 3] = ["cloud", "xpace", "eyecare"];

/// The classic Lazo program: given by `LAZO_CLASSIC`, bundled next to the island, built in the
/// repository, or installed.
fn classic_exe(app: &tauri::AppHandle) -> Option<PathBuf> {
    use tauri::Manager;
    let mut candidates: Vec<PathBuf> = Vec::new();
    if let Ok(over) = std::env::var("LAZO_CLASSIC") {
        candidates.push(PathBuf::from(over));
    }
    if let Ok(dir) = app.path().resource_dir() {
        candidates.push(dir.join("classic").join("Lazo.exe"));
    }
    candidates.push(PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../lazo/bin/Lazo.exe"));
    if let Some(files) = std::env::var_os("ProgramFiles") {
        candidates.push(PathBuf::from(files).join("Lazo").join("Lazo.exe"));
    }
    candidates.into_iter().find(|p| p.is_file())
}

/// Opens a tool in its window. Starting one that is already open brings that one forward.
#[tauri::command]
pub fn lazo_tool_open(app: tauri::AppHandle, tool: String) -> Result<(), String> {
    if !TOOLS.contains(&tool.as_str()) {
        return Err(format!("Unknown tool: {tool}"));
    }
    let exe = classic_exe(&app).ok_or_else(|| crate::i18n::t("The Lazo tools were not found."))?;
    std::process::Command::new(&exe)
        .arg(format!("--tool={tool}"))
        .spawn()
        .map(|_| ())
        .map_err(|e| format!("{}: {e}", exe.display()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_the_known_tools_open() {
        assert_eq!(TOOLS, ["cloud", "xpace", "eyecare"]);
    }
}
