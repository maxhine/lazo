// Lazo's tools — Xpace, the point clouds to ReCap, Descanso Visual — keep the classic program's own logic:
// it works, so it is not rewritten. Two ways to reach it from the island:
//
// - `lazo_tools_send`: the island's own interface. The classic logic runs in `Lazo.Engine.exe --tools-only`
//   (engine/, no network) and the island draws the tool inside itself. The messages of docs/engine-protocol.md
//   ("xpace.scan", …) go to it through this module, which keeps the program running and relays what it says
//   to the page as the `lazo-tools` event.
// - `lazo_tool_open`: a tool not embedded yet opens in the classic program's own window
//   (`Lazo.exe --tool=<name>`, lazo/src/Program.cs): no network, no tray icon, no shortcuts.

use std::path::PathBuf;
use std::time::{Duration, Instant};

use serde::Serialize;
use tauri::{AppHandle, Emitter, Manager, State};

/// The tools the classic program knows how to open alone.
const TOOLS: [&str; 3] = ["cloud", "xpace", "eyecare"];

fn first_file(candidates: Vec<PathBuf>) -> Option<PathBuf> {
    candidates.into_iter().find(|p| p.is_file())
}

/// The classic Lazo program: given by `LAZO_CLASSIC`, bundled next to the island, built in the
/// repository, or installed.
fn classic_exe(app: &AppHandle) -> Option<PathBuf> {
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
    first_file(candidates)
}

/// The tools program (the classic logic without windows or network): given by `LAZO_TOOLS_ENGINE`,
/// bundled next to the island, or built in the repository.
fn tools_exe(app: &AppHandle) -> Option<PathBuf> {
    let mut candidates: Vec<PathBuf> = Vec::new();
    if let Ok(over) = std::env::var("LAZO_TOOLS_ENGINE") {
        candidates.push(PathBuf::from(over));
    }
    if let Ok(dir) = app.path().resource_dir() {
        candidates.push(dir.join("engine").join("Lazo.Engine.exe"));
    }
    candidates.push(PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../engine/bin/Lazo.Engine.exe"));
    first_file(candidates)
}

/// Opens a tool in the classic program's window. Starting one that is already open brings that one forward.
#[tauri::command]
pub fn lazo_tool_open(app: AppHandle, tool: String) -> Result<(), String> {
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

/// Chooses the classic Lazo as the one to use: it is written down (core.txt, next to the other Lazo files) and the
/// classic program starts a moment after the island has let go of the network ports, which the island then closes.
#[tauri::command]
pub fn lazo_use_classic(app: AppHandle) -> Result<(), String> {
    let exe = classic_exe(&app).ok_or_else(|| crate::i18n::t("The Lazo tools were not found."))?;
    let _ = std::fs::write(crate::platform::config_dir().join("core.txt"), "classic");
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        std::process::Command::new("powershell")
            .args(["-NoProfile", "-WindowStyle", "Hidden", "-Command"])
            .arg(format!("Start-Sleep -Seconds 2; Start-Process -FilePath '{}'", exe.display().to_string().replace('\x27', "''")))
            .creation_flags(0x0800_0000)
            .spawn()
            .map_err(|e| e.to_string())?;
    }
    #[cfg(not(windows))]
    std::process::Command::new(&exe).spawn().map_err(|e| e.to_string())?;
    app.exit(0);
    Ok(())
}

#[derive(Serialize)]
pub struct KnownFolder {
    label: String,
    path: String,
}

/// Where Xpace can start: the drives, and the usual folders of the user's profile.
#[tauri::command]
pub fn lazo_known_folders() -> Vec<KnownFolder> {
    let mut out = Vec::new();
    for letter in b'A'..=b'Z' {
        let path = format!("{}:\\", letter as char);
        if std::path::Path::new(&path).exists() {
            out.push(KnownFolder { label: path.clone(), path });
        }
    }
    if let Some(profile) = std::env::var_os("USERPROFILE").map(PathBuf::from) {
        for name in ["", "Desktop", "Documents", "Downloads", "Pictures", "Videos", "OneDrive"] {
            let path = if name.is_empty() { profile.clone() } else { profile.join(name) };
            if path.is_dir() {
                let label = if name.is_empty() { profile.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default() } else { name.to_string() };
                out.push(KnownFolder { label, path: path.to_string_lossy().to_string() });
            }
        }
    }
    out
}

/// Shows a file or folder in Explorer, selected.
#[tauri::command]
pub fn lazo_reveal(path: String) -> Result<(), String> {
    #[cfg(windows)]
    {
        if path.is_empty() || path.contains('"') {
            return Err("Invalid path.".to_string());
        }
        // explorer.exe takes the whole switch as one argument; a path with spaces needs no extra quoting here.
        return std::process::Command::new("explorer.exe")
            .arg(format!("/select,{path}"))
            .spawn()
            .map(|_| ())
            .map_err(|e| e.to_string());
    }
    #[cfg(not(windows))]
    {
        let _ = path;
        Err("Only on Windows.".to_string())
    }
}

// ── The tools program, kept running and reached by the page ───────────────────

/// What it says goes to the page as this event, one JSON message each.
const EVENT: &str = "lazo-tools";

#[derive(Default)]
pub struct LazoTools {
    conn: tokio::sync::Mutex<Option<Conn>>,
}

struct Conn {
    /// Kept so its standard input stays open: the program exits by itself when this process ends.
    #[allow(dead_code)]
    child: std::process::Child,
    to_program: tokio::sync::mpsc::UnboundedSender<String>,
}

/// Receives each message the program says.
type Emit = std::sync::Arc<dyn Fn(String) + Send + Sync>;

/// Starts the tools program, waits for its port and token (it writes `info` once it listens) and connects to it.
#[cfg(windows)]
async fn start_program(exe: &std::path::Path, info: &std::path::Path, emit: Emit) -> Result<Conn, String> {
    use futures_util::{SinkExt, StreamExt};
    use std::os::windows::process::CommandExt;
    use std::process::{Command, Stdio};
    use tokio_tungstenite::tungstenite::Message;

    let _ = std::fs::remove_file(info);
    let child = Command::new(exe)
        .arg("--tools-only")
        .arg("--exit-on-stdin-close")
        .arg("--info")
        .arg(info)
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        // CREATE_NO_WINDOW: a console program, but nobody should see a console.
        .creation_flags(0x0800_0000)
        .spawn()
        .map_err(|e| format!("{}: {e}", exe.display()))?;

    let deadline = Instant::now() + Duration::from_secs(15);
    let (port, token) = loop {
        if let Ok(text) = std::fs::read_to_string(info) {
            if let Ok(v) = serde_json::from_str::<serde_json::Value>(&text) {
                if let (Some(port), Some(token)) = (v["port"].as_u64(), v["token"].as_str()) {
                    break (port as u16, token.to_string());
                }
            }
        }
        if Instant::now() >= deadline {
            return Err("The Lazo tools took too long to start.".to_string());
        }
        tokio::time::sleep(Duration::from_millis(100)).await;
    };
    let tcp = tokio::net::TcpStream::connect(("127.0.0.1", port)).await.map_err(|e| e.to_string())?;
    let (socket, _) = tokio_tungstenite::client_async(format!("ws://127.0.0.1:{port}/ws?t={token}"), tcp)
        .await
        .map_err(|e| e.to_string())?;
    let (mut write, mut read) = socket.split();
    let (to_program, mut from_page) = tokio::sync::mpsc::unbounded_channel::<String>();
    tokio::spawn(async move {
        while let Some(message) = from_page.recv().await {
            if write.send(Message::text(message)).await.is_err() {
                break;
            }
        }
    });
    tokio::spawn(async move {
        while let Some(Ok(message)) = read.next().await {
            if let Message::Text(text) = message {
                emit(text.to_string());
            }
        }
    });
    Ok(Conn { child, to_program })
}

#[cfg(windows)]
async fn connect(app: &AppHandle) -> Result<Conn, String> {
    let exe = tools_exe(app).ok_or_else(|| crate::i18n::t("The Lazo tools were not found."))?;
    let info = crate::platform::config_dir().join("tools-island.json");
    let relay = app.clone();
    let conn = start_program(&exe, &info, std::sync::Arc::new(move |text| {
        let _ = relay.emit(EVENT, text);
    }))
    .await?;
    crate::log::line(format!("Lazo tools started ({})", exe.display()));
    Ok(conn)
}

#[cfg(not(windows))]
async fn connect(_app: &AppHandle) -> Result<Conn, String> {
    Err("Lazo's tools are for Windows.".to_string())
}

/// The page sends one JSON message to the tools program (started on first use).
#[tauri::command]
pub async fn lazo_tools_send(app: AppHandle, state: State<'_, LazoTools>, message: String) -> Result<(), String> {
    let mut guard = state.conn.lock().await;
    if guard.as_ref().map(|c| c.to_program.is_closed()).unwrap_or(true) {
        *guard = Some(connect(&app).await?);
    }
    guard
        .as_ref()
        .expect("just connected")
        .to_program
        .send(message)
        .map_err(|_| "The Lazo tools stopped.".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_the_known_tools_open() {
        assert_eq!(TOOLS, ["cloud", "xpace", "eyecare"]);
    }

    /// The relay against the real tools program (engine/bin/Lazo.Engine.exe, built by engine/build.ps1): an analysis
    /// of a small folder comes back with exact numbers. Skipped when the program is not built.
    #[cfg(windows)]
    #[tokio::test]
    async fn the_page_reaches_the_classic_xpace_through_the_relay() {
        let exe = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../engine/bin/Lazo.Engine.exe");
        if !exe.is_file() {
            eprintln!("engine/bin/Lazo.Engine.exe is not built: skipped");
            return;
        }
        let dir = std::env::temp_dir().join(format!("lazo-tools-relay-{}", std::process::id()));
        std::fs::create_dir_all(dir.join("sub")).unwrap();
        std::fs::write(dir.join("a.bin"), vec![0u8; 3000]).unwrap();
        std::fs::write(dir.join("sub").join("b.bin"), vec![0u8; 500]).unwrap();

        let (tx, mut rx) = tokio::sync::mpsc::unbounded_channel::<String>();
        let emit: Emit = std::sync::Arc::new(move |text| {
            let _ = tx.send(text);
        });
        // Outside the folder that is analysed, or it would be counted.
        let info = std::env::temp_dir().join(format!("lazo-tools-relay-{}.json", std::process::id()));
        let conn = start_program(&exe, &info, emit).await.expect("the program starts");
        let scan = serde_json::json!({ "cmd": "xpace.scan", "path": dir.to_string_lossy() });
        conn.to_program.send(scan.to_string()).unwrap();
        let done = tokio::time::timeout(Duration::from_secs(20), async {
            loop {
                let text = rx.recv().await.expect("the program keeps talking");
                let v: serde_json::Value = serde_json::from_str(&text).unwrap();
                if v["type"] == "xpace.done" {
                    return v;
                }
            }
        })
        .await
        .expect("the analysis finishes");
        assert_eq!(done["files"], 2);
        assert_eq!(done["bytes"], 3500);
        drop(conn);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn the_drives_and_the_profile_are_offered() {
        let folders = lazo_known_folders();
        assert!(!folders.is_empty());
        assert!(folders.iter().all(|f| !f.path.is_empty() && !f.label.is_empty()));
    }
}
