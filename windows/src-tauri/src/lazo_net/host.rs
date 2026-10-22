// The engine as the island's page sees it: the JSON messages of docs/engine-protocol.md, one text
// per message, in both directions. The page used to reach the C# engine over a WebSocket; now the
// same messages travel over Tauri's own channel (an event out, a command in), so the page's client
// (src/lazo/engine.ts) did not have to change.

use std::collections::HashMap;
use std::net::Ipv4Addr;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use serde_json::{json, Value};

use super::engine::{Engine, Event, Peer};

pub const PROTOCOL_VERSION: u32 = 1;

/// Receives each message the page should get.
pub type Emit = Arc<dyn Fn(String) + Send + Sync>;

pub struct Host {
    engine: Arc<Engine>,
    emit: Emit,
    /// Enables `addPeer`, for tests with two engines in one computer.
    test_mode: bool,
    /// When the last progress message of each transfer went out: at most about ten per second.
    last_progress: Mutex<HashMap<String, Instant>>,
}

fn peer_json(p: &Peer) -> Value {
    json!({
        "id": p.id, "name": p.name, "address": p.address.to_string(), "paired": p.paired, "verified": p.verified,
        "identityChanged": p.identity_changed, "sharing": p.sharing.clone().unwrap_or_default(), "hasPhoto": !p.photo.is_empty(),
    })
}

fn text(value: &Value, key: &str) -> String {
    match value.get(key) {
        Some(Value::String(s)) => s.clone(),
        Some(Value::Null) | None => String::new(),
        Some(other) => other.to_string(),
    }
}

impl Host {
    pub fn new(engine: Arc<Engine>, emit: Emit, test_mode: bool) -> Arc<Host> {
        Arc::new(Host { engine, emit, test_mode, last_progress: Mutex::new(HashMap::new()) })
    }

    fn send(&self, value: Value) {
        (self.emit)(value.to_string());
    }

    fn should_report(&self, key: &str, value: f64) -> bool {
        let mut last = self.last_progress.lock().unwrap();
        let now = Instant::now();
        if value < 1.0 {
            if let Some(at) = last.get(key) {
                if now.duration_since(*at) < Duration::from_millis(100) {
                    return false;
                }
            }
        }
        last.insert(key.to_string(), now);
        true
    }

    /// An engine event, as the message the page gets.
    pub fn on_event(&self, event: Event) {
        match event {
            Event::Peers(peers) => self.send(json!({ "type": "peers", "peers": peers.iter().map(peer_json).collect::<Vec<_>>() })),
            Event::Offer(o) => self.send(json!({
                "type": "offer", "id": o.id, "peerId": o.peer_id, "sender": o.sender, "fileName": o.file_name, "size": o.size,
                "preview": base64_of(&o.preview),
            })),
            Event::ReceiveProgress { id, value } => {
                if self.should_report(&format!("r:{id}"), value) {
                    self.send(json!({ "type": "receiveProgress", "id": id, "value": value }));
                }
            }
            Event::ReceiveFinished { id, message, path } => {
                self.last_progress.lock().unwrap().remove(&format!("r:{id}"));
                self.send(json!({ "type": "receiveFinished", "id": id, "message": message, "path": path.unwrap_or_default() }));
            }
            Event::Chat { peer_id, name, text } => self.send(json!({ "type": "chat", "peerId": peer_id, "name": name, "text": text })),
            Event::Signal { .. } => {}
        }
    }

    pub fn hello(&self) -> Value {
        json!({
            "type": "hello", "version": PROTOCOL_VERSION, "selfId": self.engine.self_id(), "name": self.engine.display_name(),
            "fingerprint": self.engine.fingerprint(), "receiveDir": self.engine.receive_dir().to_string_lossy(),
            "photo": self.engine.photo(),
        })
    }

    /// One command from the page (a JSON text). Unknown or malformed ones are answered with an error message.
    pub async fn handle(self: &Arc<Self>, message: &str) {
        let Ok(command) = serde_json::from_str::<Value>(message) else { return };
        let cmd = text(&command, "cmd");
        match cmd.as_str() {
            "ping" => self.send(json!({ "type": "pong" })),
            "state" => {
                self.send(self.hello());
                self.send(json!({ "type": "peers", "peers": self.engine.peers().iter().map(peer_json).collect::<Vec<_>>() }));
            }
            "photo" => {
                let id = text(&command, "peerId");
                let data = self.engine.peer(&id).map(|p| p.photo).unwrap_or_default();
                self.send(json!({ "type": "photo", "peerId": id, "data": data }));
            }
            "offerDecision" => {
                let accept = command.get("accept").and_then(Value::as_bool).unwrap_or(false);
                self.engine.decide_offer(&text(&command, "id"), accept);
            }
            "chat" => {
                let peer_id = text(&command, "peerId");
                let result = self.engine.send_chat(&peer_id, &text(&command, "text")).await;
                self.send(json!({
                    "type": "chatSent", "peerId": peer_id, "ok": result.is_ok(),
                    "message": result.err().map(|e| e.to_string()).unwrap_or_default(),
                }));
            }
            "send" => {
                let (token, peer_id, path) = (text(&command, "token"), text(&command, "peerId"), PathBuf::from(text(&command, "path")));
                let host = self.clone();
                let key = format!("s:{token}");
                let result = {
                    let progress_host = self.clone();
                    let (progress_token, progress_key) = (token.clone(), key.clone());
                    self.engine
                        .send_file(&peer_id, &path, move |value| {
                            if progress_host.should_report(&progress_key, value) {
                                progress_host.send(json!({ "type": "sendProgress", "token": progress_token, "value": value }));
                            }
                        })
                        .await
                };
                host.last_progress.lock().unwrap().remove(&key);
                host.send(json!({
                    "type": "sendDone", "token": token, "peerId": peer_id, "ok": result.is_ok(),
                    "message": result.err().map(|e| e.to_string()).unwrap_or_default(),
                }));
            }
            "addPeer" if self.test_mode => {
                let address = text(&command, "address").parse::<Ipv4Addr>();
                let port = text(&command, "port").parse::<u16>();
                match (address, port) {
                    (Ok(address), Ok(port)) => {
                        let id = text(&command, "id");
                        self.engine.add_peer(&id, &text(&command, "name"), address, port, &text(&command, "fingerprint"));
                        self.send(json!({ "type": "peerAdded", "id": id }));
                    }
                    _ => self.send(json!({ "type": "error", "message": "addPeer no disponible." })),
                }
            }
            other => self.send(json!({ "type": "error", "message": format!("Orden desconocida: {other}") })),
        }
    }
}

fn base64_of(bytes: &[u8]) -> String {
    use base64::Engine as _;
    base64::engine::general_purpose::STANDARD.encode(bytes)
}
