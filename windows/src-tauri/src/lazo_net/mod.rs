// Lazo's engine inside the island (Windows): the network, the secure channel, files and chat, in
// Rust, speaking the same LAZO3 protocol as classic Lazo and the Mac. Replaces the C# program the
// island used to start next to itself.
//
// - crypto, secure, identity, text: the protocol's pieces, each tested on its own.
// - engine: discovery, connections, files and chat; knows nothing about Tauri.
// - host: the JSON messages of docs/engine-protocol.md, over Tauri's event and command channel.

// Some pieces wait for what comes next (verification code, signals, screen sharing): they are built and tested already.
#![allow(dead_code)]

pub mod crypto;
pub mod engine;
pub mod host;
pub mod identity;
pub mod secure;
pub mod text;

use std::sync::{Arc, Mutex};

use tauri::{AppHandle, Emitter, Manager, State};

use engine::{Engine, DISCOVERY_PORT, TRANSFER_PORT};
use host::Host;

/// The event the page listens to: each payload is one JSON message from the engine.
const EVENT: &str = "lazo-engine";

#[derive(Default)]
pub struct LazoNet {
    host: Mutex<Option<Arc<Host>>>,
    /// Why the engine is not there, for the page.
    error: Mutex<String>,
}

/// An engine and the host that turns its events into messages for `emit`.
pub fn build(
    profile: std::path::PathBuf,
    receive: Option<std::path::PathBuf>,
    discovery_port: u16,
    transfer_port: u16,
    emit: host::Emit,
    test_mode: bool,
) -> (Arc<Engine>, Arc<Host>) {
    // The host needs the engine and the engine needs a sink that reaches the host: the sink goes first.
    let slot: Arc<Mutex<Option<std::sync::Weak<Host>>>> = Arc::new(Mutex::new(None));
    let sink_slot = slot.clone();
    let sink: engine::Sink = Arc::new(move |event| {
        let host = sink_slot.lock().unwrap().as_ref().and_then(|w| w.upgrade());
        if let Some(host) = host {
            host.on_event(event);
        }
    });
    let engine = Engine::new(profile, receive, discovery_port, transfer_port, sink);
    let host = Host::new(engine.clone(), emit, test_mode);
    *slot.lock().unwrap() = Some(Arc::downgrade(&host));
    (engine, host)
}

/// Starts the engine. Nothing is shown here: the page asks for its state when it opens.
pub fn start(app: &AppHandle) {
    let app = app.clone();
    tauri::async_runtime::spawn(async move {
        let emit_app = app.clone();
        let emit: host::Emit = Arc::new(move |message| {
            let _ = emit_app.emit(EVENT, message);
        });
        let (engine, host) = build(crate::platform::config_dir(), None, DISCOVERY_PORT, TRANSFER_PORT, emit, false);
        let state = app.state::<LazoNet>();
        match engine.start().await {
            Ok(()) => {
                crate::log::line("Lazo engine started (Rust)");
                *state.host.lock().unwrap() = Some(host);
            }
            Err(e) => {
                let message = if e.kind() == std::io::ErrorKind::AddrInUse || e.raw_os_error() == Some(10048) {
                    "Classic Lazo is open: close it so the island can use the network.".to_string()
                } else {
                    format!("The Lazo engine could not start ({e}).")
                };
                crate::log::line(format!("Lazo engine: {message}"));
                *state.error.lock().unwrap() = message;
            }
        }
    });
}

/// The page sends one JSON command. Errs with the reason while the engine is not running, so the
/// page can show it and try again.
#[tauri::command]
pub async fn lazo_engine_send(state: State<'_, LazoNet>, message: String) -> Result<(), String> {
    let host = state.host.lock().unwrap().clone();
    match host {
        Some(host) => {
            // The page asks for the state when it connects: worth one line in the log.
            if message.contains("\"state\"") {
                crate::log::line("Lazo page connected to the engine");
            }
            host.handle(&message).await;
            Ok(())
        }
        None => {
            let error = state.error.lock().unwrap().clone();
            Err(if error.is_empty() { "The Lazo engine is starting.".to_string() } else { error })
        }
    }
}

#[cfg(test)]
mod tests;
