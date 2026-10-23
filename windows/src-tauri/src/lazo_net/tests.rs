// The engine against real engines: two Rust ones in this process, and one Rust against classic
// Lazo's C# engine (engine/bin/Lazo.Engine.exe, built by engine/build.ps1) over real sockets.
// Discovery by broadcast cannot be tried between two copies on one computer (the announcement insists
// on the same transfer port), so each side registers the other by hand, as engine/test-engine.mjs does.

use std::net::Ipv4Addr;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use serde_json::{json, Value};
use tokio::sync::mpsc;
use tokio::time::timeout;

use super::engine::Engine;
use super::host::{Emit, Host};
use super::identity::new_guid;

const WAIT: Duration = Duration::from_secs(20);

/// A private address of this computer: where the engines reach each other.
fn local_ip() -> Option<Ipv4Addr> {
    if_addrs::get_if_addrs().ok()?.into_iter().find_map(|i| match i.addr {
        if_addrs::IfAddr::V4(v4)
            if i.oper_status == if_addrs::IfOperStatus::Up && !v4.ip.is_loopback() && {
                let b = v4.ip.octets();
                b[0] == 10 || (b[0] == 172 && (16..=31).contains(&b[1])) || (b[0] == 192 && b[1] == 168)
            } =>
        {
            Some(v4.ip)
        }
        _ => None,
    })
}

fn temp_dir(name: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("lazo-net-test-{name}-{}", new_guid()));
    std::fs::create_dir_all(&dir).unwrap();
    dir
}

struct Node {
    engine: Arc<Engine>,
    host: Arc<Host>,
    messages: mpsc::UnboundedReceiver<Value>,
    dir: PathBuf,
    transfer_port: u16,
}

async fn node(name: &str, discovery: u16, transfer: u16) -> Node {
    let dir = temp_dir(name);
    let (tx, rx) = mpsc::unbounded_channel();
    let emit: Emit = Arc::new(move |text| {
        let _ = tx.send(serde_json::from_str::<Value>(&text).unwrap());
    });
    let (engine, host) = super::build(dir.join("profile"), Some(dir.join("in")), discovery, transfer, emit, true);
    engine.set_name_override(&format!("Equipo {name}"));
    engine.start().await.expect("the test ports are free");
    Node { engine, host, messages: rx, dir, transfer_port: transfer }
}

impl Node {
    /// The first message that satisfies `matches`, skipping the others.
    async fn next(&mut self, what: &str, matches: impl Fn(&Value) -> bool) -> Value {
        let found = timeout(WAIT, async {
            loop {
                let message = self.messages.recv().await.expect("the node is alive");
                if matches(&message) {
                    return message;
                }
            }
        })
        .await;
        found.unwrap_or_else(|_| panic!("timed out waiting for {what}"))
    }

    async fn command(&self, value: Value) {
        self.host.handle(&value.to_string()).await;
    }
}

fn kind(t: &'static str) -> impl Fn(&Value) -> bool {
    move |m| m["type"] == t
}

/// A send waits for the other side to answer its offer, so it runs on its own while the test answers.
fn spawn_send(from: &Node, token: &str, peer_id: &str, path: &Path) -> tokio::task::JoinHandle<()> {
    let host = from.host.clone();
    let message = json!({ "cmd": "send", "token": token, "peerId": peer_id, "path": path }).to_string();
    tokio::spawn(async move { host.handle(&message).await })
}

fn payload(len: usize) -> Vec<u8> {
    (0..len).map(|i| (i * 31 + 7) as u8).collect()
}

async fn introduce(a: &Node, b: &Node, ip: Ipv4Addr) {
    a.engine.add_peer(b.engine.self_id(), "Equipo b", ip, b.transfer_port, b.engine.fingerprint());
    b.engine.add_peer(a.engine.self_id(), "Equipo a", ip, a.transfer_port, a.engine.fingerprint());
}

#[tokio::test]
async fn two_rust_engines_chat_and_move_files() {
    let Some(ip) = local_ip() else {
        eprintln!("no private IPv4 address on this computer: skipped");
        return;
    };
    let mut a = node("a", 49551, 49552).await;
    let mut b = node("b", 49553, 49554).await;
    introduce(&a, &b, ip).await;
    let (id_a, id_b) = (a.engine.self_id().to_string(), b.engine.self_id().to_string());

    // Chat, with accents and an emoji.
    b.command(json!({ "cmd": "chat", "peerId": id_a, "text": "hola desde la isla, ñandú 🎀" })).await;
    let chat = a.next("a chat message", kind("chat")).await;
    assert_eq!(chat["text"], "hola desde la isla, ñandú 🎀");
    assert_eq!(chat["peerId"], id_b);
    assert_eq!(chat["name"], "Equipo b");
    assert_eq!(b.next("the delivery report", kind("chatSent")).await["ok"], true);

    // A buzz: a hears who sent it; b is told it arrived.
    b.command(json!({ "cmd": "buzz", "peerId": id_a })).await;
    let buzz = a.next("a buzz", kind("signal")).await;
    assert_eq!(buzz["kind"], 4);
    assert_eq!(buzz["peerId"], id_b);
    assert_eq!(buzz["name"], "Equipo b");
    assert_eq!(b.next("the buzz report", kind("buzzSent")).await["ok"], true);

    // A file from b to a, accepted: identical bytes arrive, with a progress report on the way.
    let data = payload(3 * 1024 * 1024 + 123);
    let source = b.dir.join("muestra.bin");
    std::fs::write(&source, &data).unwrap();
    let sending = spawn_send(&b, "t1", &id_a, &source);
    let offer = a.next("an offer", kind("offer")).await;
    assert_eq!(offer["fileName"], "muestra.bin");
    assert_eq!(offer["size"], data.len());
    assert_eq!(offer["peerId"], id_b);
    a.command(json!({ "cmd": "offerDecision", "id": offer["id"], "accept": true })).await;
    let finished = a.next("the file", kind("receiveFinished")).await;
    let saved = PathBuf::from(finished["path"].as_str().unwrap());
    assert_eq!(std::fs::read(&saved).unwrap(), data, "the file arrived identical");
    assert!(saved.starts_with(a.dir.join("in")));
    let done = b.next("the send report", kind("sendDone")).await;
    assert_eq!(done["ok"], true, "{done}");
    sending.await.unwrap();
    #[cfg(windows)]
    {
        let stream = std::fs::read_to_string(format!("{}:Zone.Identifier", saved.display())).unwrap();
        assert!(stream.contains("ZoneId=3"), "marked as downloaded from another computer");
    }

    // The same name again does not overwrite.
    let second_send = spawn_send(&b, "t2", &id_a, &source);
    let again = a.next("a second offer", |m| m["type"] == "offer").await;
    a.command(json!({ "cmd": "offerDecision", "id": again["id"], "accept": true })).await;
    let second = a.next("the second file", kind("receiveFinished")).await;
    assert!(second["path"].as_str().unwrap().ends_with("muestra (1).bin"));
    second_send.await.unwrap();

    // A refusal saves nothing and the sender is told.
    let third_send = spawn_send(&b, "t3", &id_a, &source);
    let third = a.next("a third offer", |m| m["type"] == "offer").await;
    a.command(json!({ "cmd": "offerDecision", "id": third["id"], "accept": false })).await;
    let refused = b.next("the refusal", |m| m["type"] == "sendDone" && m["token"] == "t3").await;
    assert_eq!(refused["ok"], false);
    third_send.await.unwrap();

    // Both remember each other after the first contact.
    let paired = std::fs::read_to_string(a.dir.join("profile").join("paired.txt")).unwrap();
    assert!(paired.contains(&id_b), "{paired}");
    let hello = a.host.hello();
    assert_eq!(hello["version"], 1);
    assert_eq!(hello["fingerprint"].as_str().unwrap().len(), 64);
}

#[tokio::test]
async fn a_computer_that_changed_its_key_is_not_sent_anything() {
    let Some(ip) = local_ip() else { return };
    let a = node("c", 49561, 49562).await;
    let mut b = node("d", 49563, 49564).await;
    introduce(&a, &b, ip).await;
    // b is known by a with a fingerprint that is not b's real one.
    a.engine.add_peer(b.engine.self_id(), "Equipo d", ip, b.transfer_port, &"a".repeat(64));
    let source = a.dir.join("x.txt");
    std::fs::write(&source, "x").unwrap();
    a.command(json!({ "cmd": "send", "token": "t", "peerId": b.engine.self_id(), "path": source })).await;
    // The nothing-sent rule: b never even sees an offer.
    assert!(timeout(Duration::from_millis(500), b.next("an offer", kind("offer"))).await.is_err());
}

#[tokio::test]
async fn unknown_people_and_missing_files_fail_with_a_message() {
    let Some(_) = local_ip() else { return };
    let mut a = node("e", 49571, 49572).await;
    a.command(json!({ "cmd": "send", "token": "t", "peerId": "00000000-0000-4000-8000-000000000000", "path": "C:/no-existe.bin" })).await;
    let done = a.next("the report", kind("sendDone")).await;
    assert_eq!(done["ok"], false);
    assert!(done["message"].as_str().unwrap().contains("ya no está disponible"));
    a.command(json!({ "cmd": "nada" })).await;
    assert!(a.next("an error", kind("error")).await["message"].as_str().unwrap().contains("desconocida"));
    a.command(json!({ "cmd": "ping" })).await;
    a.next("the pong", kind("pong")).await;
}

/// What classic Lazo and the Mac broadcast, as a unicast packet from this computer's own address.
async fn announce(to_port: u16, from: Ipv4Addr, text: &str) {
    let socket = tokio::net::UdpSocket::bind((from, 0)).await.unwrap();
    socket.send_to(text.as_bytes(), (from, to_port)).await.unwrap();
}

#[tokio::test]
async fn announcements_make_people_appear_and_disappear_by_the_protocols_rules() {
    let Some(ip) = local_ip() else { return };
    let mut a = node("f", 49591, 49592).await;
    let other = new_guid();
    let fingerprint = "b".repeat(64);
    // Well formed: listed, under the name given.
    announce(49591, ip, &format!("LAZO3|{other}|Luis|49592|{fingerprint}")).await;
    let peers = a.next("the announced person", |m| m["type"] == "peers" && m["peers"].as_array().map(|p| !p.is_empty()).unwrap_or(false)).await;
    assert_eq!(peers["peers"][0]["name"], "Luis");
    assert_eq!(peers["peers"][0]["id"], other);
    assert_eq!(peers["peers"][0]["paired"], false);
    // A photo and a sharing label ride on their own packets.
    announce(49591, ip, &format!("LAZO3A|{other}|aGVsbG8=")).await;
    let with_photo = a.next("the photo", |m| m["type"] == "peers" && m["peers"][0]["hasPhoto"] == true).await;
    assert_eq!(with_photo["peers"][0]["name"], "Luis");
    announce(49591, ip, &format!("LAZO3P|{other}|Pantalla de Luis")).await;
    a.next("the sharing label", |m| m["type"] == "peers" && m["peers"][0]["sharing"] == "Pantalla de Luis").await;
    // What the protocol says to ignore: a wrong port, a short fingerprint, itself, and an invalid photo.
    let ignored = new_guid();
    announce(49591, ip, &format!("LAZO3|{ignored}|Otro|49999|{fingerprint}")).await;
    announce(49591, ip, &format!("LAZO3|{ignored}|Otro|49592|abc")).await;
    announce(49591, ip, &format!("LAZO3|{}|Yo|49592|{fingerprint}", a.engine.self_id())).await;
    announce(49591, ip, &format!("LAZO3A|{other}|no es base64!!")).await;
    tokio::time::sleep(Duration::from_millis(400)).await;
    assert_eq!(a.engine.peers().len(), 1, "only the well formed announcement counted");
}

// ── Against classic Lazo's engine ─────────────────────────────────────────────

struct Csharp {
    child: std::process::Child,
    socket: tokio_tungstenite::WebSocketStream<tokio::net::TcpStream>,
    dir: PathBuf,
}

fn csharp_exe() -> Option<PathBuf> {
    let exe = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../engine/bin/Lazo.Engine.exe");
    exe.is_file().then_some(exe)
}

async fn start_csharp(exe: &Path, discovery: u16, transfer: u16, port: u16) -> Csharp {
    use std::process::{Command, Stdio};
    let dir = temp_dir("csharp");
    let info = dir.join("engine.json");
    let child = Command::new(exe)
        .args(["--allow-parallel", "--ports", &format!("{discovery},{transfer}"), "--port", &port.to_string()])
        .arg("--profile").arg(dir.join("profile"))
        .arg("--info").arg(&info)
        .arg("--receive").arg(dir.join("in"))
        .arg("--exit-on-stdin-close")
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .expect("the C# engine starts");
    let mut token = String::new();
    for _ in 0..100 {
        if let Ok(text) = std::fs::read_to_string(&info) {
            if let Ok(v) = serde_json::from_str::<Value>(&text) {
                token = v["token"].as_str().unwrap_or_default().to_string();
                break;
            }
        }
        tokio::time::sleep(Duration::from_millis(100)).await;
    }
    assert!(!token.is_empty(), "the C# engine wrote its info file");
    let tcp = tokio::net::TcpStream::connect(("127.0.0.1", port)).await.unwrap();
    let (socket, _) = tokio_tungstenite::client_async(format!("ws://127.0.0.1:{port}/ws?t={token}"), tcp).await.unwrap();
    let mut csharp = Csharp { child, socket, dir };
    // hello and peers come first.
    let hello = csharp.next(|m| m["type"] == "hello").await;
    assert_eq!(hello["version"], 1);
    csharp
}

impl Csharp {
    async fn send(&mut self, value: Value) {
        use futures_util::SinkExt;
        self.socket.send(tokio_tungstenite::tungstenite::Message::text(value.to_string())).await.unwrap();
    }

    async fn next(&mut self, matches: impl Fn(&Value) -> bool) -> Value {
        use futures_util::StreamExt;
        timeout(WAIT, async {
            loop {
                let message = self.socket.next().await.expect("the socket stays open").expect("a readable message");
                if let tokio_tungstenite::tungstenite::Message::Text(text) = message {
                    let value: Value = serde_json::from_str(text.as_str()).unwrap();
                    if matches(&value) {
                        return value;
                    }
                }
            }
        })
        .await
        .expect("timed out waiting for the C# engine")
    }
}

impl Drop for Csharp {
    fn drop(&mut self) {
        drop(self.child.stdin.take());
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

#[tokio::test]
async fn rust_and_csharp_engines_understand_each_other() {
    let (Some(exe), Some(ip)) = (csharp_exe(), local_ip()) else {
        eprintln!("engine/bin/Lazo.Engine.exe is not built (engine/build.ps1) or there is no LAN address: skipped");
        return;
    };
    let mut cs = start_csharp(&exe, 49581, 49582, 49580).await;
    let mut rs = node("rust", 49583, 49584).await;
    cs.send(json!({ "cmd": "state" })).await;
    let cs_hello = cs.next(|m| m["type"] == "hello").await;
    let cs_id = cs_hello["selfId"].as_str().unwrap().to_string();
    let rs_id = rs.engine.self_id().to_string();

    // Each side learns about the other.
    rs.engine.add_peer(&cs_id, "Equipo C#", ip, 49582, cs_hello["fingerprint"].as_str().unwrap());
    cs.send(json!({ "cmd": "addPeer", "id": rs_id, "name": "Equipo rust", "address": ip.to_string(), "port": 49584, "fingerprint": rs.engine.fingerprint() })).await;
    cs.next(|m| m["type"] == "peerAdded").await;

    // Chat both ways.
    rs.command(json!({ "cmd": "chat", "peerId": cs_id, "text": "de Rust a C# ñ" })).await;
    let chat = cs.next(|m| m["type"] == "chat").await;
    assert_eq!(chat["text"], "de Rust a C# ñ");
    assert_eq!(chat["peerId"], rs_id);
    cs.send(json!({ "cmd": "chat", "peerId": rs_id, "text": "de C# a Rust ✓" })).await;
    let back = rs.next("the C# chat", kind("chat")).await;
    assert_eq!(back["text"], "de C# a Rust ✓");
    assert_eq!(back["peerId"], cs_id);

    // A file from Rust to C#.
    let data = payload(2 * 1024 * 1024 + 77);
    let source = rs.dir.join("de-rust.bin");
    std::fs::write(&source, &data).unwrap();
    let sending = spawn_send(&rs, "r1", &cs_id, &source);
    let offer = cs.next(|m| m["type"] == "offer").await;
    assert_eq!(offer["fileName"], "de-rust.bin");
    assert_eq!(offer["size"], data.len());
    cs.send(json!({ "cmd": "offerDecision", "id": offer["id"], "accept": true })).await;
    let finished = cs.next(|m| m["type"] == "receiveFinished").await;
    assert_eq!(std::fs::read(finished["path"].as_str().unwrap()).unwrap(), data, "C# received what Rust sent");
    assert_eq!(rs.next("the send report", kind("sendDone")).await["ok"], true);
    sending.await.unwrap();

    // A file from C# to Rust.
    let data = payload(1024 * 1024 + 5);
    let source = cs.dir.join("de-csharp.bin");
    std::fs::write(&source, &data).unwrap();
    cs.send(json!({ "cmd": "send", "token": "c1", "peerId": rs_id, "path": source })).await;
    let offer = rs.next("the C# offer", kind("offer")).await;
    assert_eq!(offer["fileName"], "de-csharp.bin");
    rs.command(json!({ "cmd": "offerDecision", "id": offer["id"], "accept": true })).await;
    let finished = rs.next("the C# file", kind("receiveFinished")).await;
    assert_eq!(std::fs::read(finished["path"].as_str().unwrap()).unwrap(), data, "Rust received what C# sent");
    assert_eq!(cs.next(|m| m["type"] == "sendDone" && m["token"] == "c1").await["ok"], true);

    // Rust remembered the C# engine's key at first contact, in the file classic Lazo reads.
    let paired = std::fs::read_to_string(rs.dir.join("profile").join("paired.txt")).unwrap();
    assert!(paired.contains(&cs_id), "{paired}");
}
