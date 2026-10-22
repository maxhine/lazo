// Lazo's engine (engine/Lazo.Engine.exe): the same network, encryption, chat and file transfer as
// classic Lazo, without windows. The island starts it next to itself and talks to it from the page
// over a local WebSocket (src/lazo/engine.ts, docs/engine-protocol.md). This module only starts
// it, keeps it alive for as long as the island runs, and hands the page the port and the token.
//
// The engine exits by itself when its standard input closes, which is what happens when the
// island's process ends for any reason, so it is never left behind.

use serde::Serialize;
use std::path::PathBuf;
use std::sync::Mutex;
use std::time::{Duration, Instant};

#[derive(Default)]
pub struct LazoEngine {
    /// The running engine: kept so its standard input stays open.
    #[cfg(windows)]
    child: Mutex<Option<std::process::Child>>,
    #[cfg(not(windows))]
    child: Mutex<Option<()>>,
}

#[derive(Serialize, Clone)]
pub struct EngineInfo {
    pub port: u16,
    pub token: String,
}

/// Where the engine writes its port and token for this run.
fn info_path() -> PathBuf {
    crate::platform::config_dir().join("engine-island.json")
}

/// The engine program: next to the island in an installed build, else the one built in the repository.
fn engine_exe(app: &tauri::AppHandle) -> Option<PathBuf> {
    use tauri::Manager;
    if let Ok(over) = std::env::var("LAZO_ENGINE") {
        let p = PathBuf::from(over);
        if p.is_file() {
            return Some(p);
        }
    }
    let mut candidates = Vec::new();
    if let Ok(dir) = app.path().resource_dir() {
        candidates.push(dir.join("engine").join("Lazo.Engine.exe"));
    }
    candidates.push(PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../engine/bin/Lazo.Engine.exe"));
    candidates.into_iter().find(|p| p.is_file())
}

/// Starts the engine (Windows only; the Mac has its own). Safe to call once at startup: when the
/// program is missing or classic Lazo already runs, it says so in the log and the page shows
/// the Lazo pill as unavailable.
#[cfg(windows)]
pub fn start(app: &tauri::AppHandle) {
    use std::os::windows::process::CommandExt;
    use std::process::{Command, Stdio};
    use tauri::Manager;

    let Some(exe) = engine_exe(app) else {
        crate::log::line("Lazo engine: Lazo.Engine.exe not found, the Lazo pill stays off");
        return;
    };
    let info = info_path();
    let _ = std::fs::remove_file(&info);
    let spawned = Command::new(&exe)
        .arg("--info")
        .arg(&info)
        .arg("--exit-on-stdin-close")
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        // CREATE_NO_WINDOW: a console program, but nobody should see a console.
        .creation_flags(0x0800_0000)
        .spawn();
    match spawned {
        Ok(child) => {
            crate::log::line(format!("Lazo engine started ({})", exe.display()));
            if let Some(state) = app.try_state::<LazoEngine>() {
                *state.child.lock().unwrap() = Some(child);
            }
        }
        Err(e) => crate::log::line(format!("Lazo engine: could not start {}: {e}", exe.display())),
    }
}

#[cfg(not(windows))]
pub fn start(_app: &tauri::AppHandle) {}

/// The page asks for the engine's address: waits for the engine to be ready (it writes its file
/// once it listens), and says why when it will not be.
#[tauri::command]
pub async fn lazo_engine_info(state: tauri::State<'_, LazoEngine>) -> Result<EngineInfo, String> {
    let deadline = Instant::now() + Duration::from_secs(15);
    loop {
        if let Ok(text) = std::fs::read_to_string(info_path()) {
            if let Some(info) = parse_info(&text) {
                return Ok(info);
            }
        }
        #[cfg(windows)]
        {
            let mut guard = state.child.lock().unwrap();
            match guard.as_mut() {
                None => return Err("The Lazo engine did not start.".into()),
                Some(child) => {
                    if let Ok(Some(status)) = child.try_wait() {
                        *guard = None;
                        return Err(if status.code() == Some(2) {
                            "Classic Lazo is open: close it so the island can use the network.".into()
                        } else {
                            format!("The Lazo engine stopped ({status}).")
                        });
                    }
                }
            }
        }
        #[cfg(not(windows))]
        let _ = &state;
        if Instant::now() >= deadline {
            return Err("The Lazo engine took too long to start.".into());
        }
        tokio::time::sleep(Duration::from_millis(150)).await;
    }
}

/// `{"version":1,"port":48360,"token":"…","pid":123}` as the engine writes it.
fn parse_info(text: &str) -> Option<EngineInfo> {
    let v: serde_json::Value = serde_json::from_str(text).ok()?;
    let port = u16::try_from(v.get("port")?.as_u64()?).ok()?;
    let token = v.get("token")?.as_str()?.to_string();
    if token.len() < 16 || !token.chars().all(|c| c.is_ascii_hexdigit()) {
        return None;
    }
    Some(EngineInfo { port, token })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_what_the_engine_writes() {
        let info = parse_info(r#"{"version":1,"port":48360,"token":"0123456789abcdef0123456789abcdef0123456789abcdef","pid":4}"#).unwrap();
        assert_eq!(info.port, 48360);
        assert_eq!(info.token.len(), 48);
    }

    #[test]
    fn refuses_anything_else() {
        assert!(parse_info("").is_none());
        assert!(parse_info("{}").is_none());
        assert!(parse_info(r#"{"port":70000,"token":"0123456789abcdef0123456789abcdef"}"#).is_none());
        assert!(parse_info(r#"{"port":1,"token":"short"}"#).is_none());
        assert!(parse_info(r#"{"port":1,"token":"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"}"#).is_none());
    }
}
