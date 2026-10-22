// Lazo's network engine in Rust: discovery on the local network, the LAZO3 secure channel, files
// and chat. It speaks the same wire protocol as classic Lazo (lazo/src/NetworkEngine.cs) and the
// Mac's LazoCore (docs/protocolo-seguro.md), so computers with any of the three see and serve each
// other. It knows nothing about windows or Tauri: what happens is reported through the `Sink`.

use std::collections::HashMap;
use std::io;
use std::net::{IpAddr, Ipv4Addr, SocketAddr, SocketAddrV4};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream, UdpSocket};
use tokio::sync::oneshot;
use tokio::time::timeout;

use super::crypto;
use super::identity::{self, DeviceIdentity, PairingStore};
use super::secure::{self, Session, MAX_PLAIN};
use super::text::{clean_label, clean_name, safe_file_name, unique_destination};

pub const DISCOVERY_PORT: u16 = 48351;
pub const TRANSFER_PORT: u16 = 48352;
const MAX_FILE_SIZE: i64 = 20 * 1024 * 1024 * 1024;
const STATUS_KNOWN: u8 = 1;
const STATUS_MISMATCH: u8 = 2;
const HANDSHAKE: Duration = Duration::from_secs(10);
const IDLE: Duration = Duration::from_secs(120);
/// How long an offer waits for somebody to accept it.
const OFFER_WAIT: Duration = Duration::from_secs(60);

pub const CHAT_KIND_TEXT: u8 = 1;

/// A person on the network, as the engine knows them.
#[derive(Clone, Debug)]
pub struct Peer {
    pub id: String,
    pub name: String,
    pub photo: String,
    pub address: Ipv4Addr,
    pub port: u16,
    pub fingerprint: String,
    pub paired: bool,
    pub verified: bool,
    pub identity_changed: bool,
    pub sharing: Option<String>,
    seen: Instant,
    sharing_seen: Instant,
    /// Registered by hand (tests): never expires.
    manual: bool,
}

#[derive(Clone, Debug)]
pub struct OfferInfo {
    pub id: String,
    pub peer_id: String,
    pub sender: String,
    pub file_name: String,
    pub size: i64,
    pub preview: Vec<u8>,
}

/// What the engine tells whoever listens.
#[derive(Clone, Debug)]
pub enum Event {
    Peers(Vec<Peer>),
    Offer(OfferInfo),
    ReceiveProgress { id: String, value: f64 },
    /// `path` is None when the file did not arrive; `message` says why.
    ReceiveFinished { id: String, message: String, path: Option<String> },
    Chat { peer_id: String, name: String, text: String },
    Signal { peer_id: String, name: String, kind: u8, body: String },
}

pub type Sink = Arc<dyn Fn(Event) + Send + Sync>;

pub struct Engine {
    profile: PathBuf,
    identity: DeviceIdentity,
    pairings: PairingStore,
    receive_dir: Mutex<PathBuf>,
    discovery_port: u16,
    transfer_port: u16,
    peers: Mutex<HashMap<String, Peer>>,
    offers: Mutex<HashMap<String, oneshot::Sender<bool>>>,
    active_receives: AtomicUsize,
    running: AtomicBool,
    sink: Sink,
    /// Replaces the account name (tests); otherwise name.txt or the Windows account.
    name_override: Mutex<Option<String>>,
}

fn err(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::Other, message.to_string())
}

impl Engine {
    pub fn new(
        profile: PathBuf,
        receive_dir: Option<PathBuf>,
        discovery_port: u16,
        transfer_port: u16,
        sink: Sink,
    ) -> Arc<Engine> {
        let id = identity::load_id(&profile);
        let identity = DeviceIdentity::load(&profile, &id);
        let receive = receive_dir
            .or_else(|| saved_receive_dir(&profile))
            .unwrap_or_else(default_receive_dir);
        Arc::new(Engine {
            pairings: PairingStore::new(&profile),
            profile,
            identity,
            receive_dir: Mutex::new(receive),
            discovery_port,
            transfer_port,
            peers: Mutex::new(HashMap::new()),
            offers: Mutex::new(HashMap::new()),
            active_receives: AtomicUsize::new(0),
            running: AtomicBool::new(false),
            sink,
            name_override: Mutex::new(None),
        })
    }

    pub fn self_id(&self) -> &str {
        &self.identity.id
    }

    pub fn fingerprint(&self) -> &str {
        &self.identity.fingerprint
    }

    pub fn receive_dir(&self) -> PathBuf {
        self.receive_dir.lock().unwrap().clone()
    }

    pub fn set_name_override(&self, name: &str) {
        *self.name_override.lock().unwrap() = Some(name.to_string());
    }

    /// The name other people see: name.txt, else the Windows account's display name.
    pub fn display_name(&self) -> String {
        if let Some(name) = self.name_override.lock().unwrap().clone() {
            return clean_name(&name);
        }
        let custom = std::fs::read_to_string(self.profile.join("name.txt")).ok().map(|t| clean_name(&t)).filter(|n| !n.is_empty());
        let name = custom
            .or_else(|| crate::platform::user_full_name().map(|n| clean_name(&n)).filter(|n| !n.is_empty()))
            .or_else(|| std::env::var("USERNAME").ok().map(|n| clean_name(&n)).filter(|n| !n.is_empty()));
        name.unwrap_or_else(|| "Lazo".to_string())
    }

    /// The profile photo as base64 JPEG, or empty when there is none (shared %APPDATA%Lazoprofile.jpg).
    pub fn photo(&self) -> String {
        use base64::Engine as _;
        match std::fs::read(self.profile.join("profile.jpg")) {
            Ok(bytes) if !bytes.is_empty() && bytes.len() <= 9000 => base64::engine::general_purpose::STANDARD.encode(bytes),
            _ => String::new(),
        }
    }

    // ── Start ───────────────────────────────────────────────────────────────

    /// Opens the ports and starts discovery and the listener. Call once, from inside the runtime.
    pub async fn start(self: &Arc<Self>) -> io::Result<()> {
        let udp = bind_discovery(self.discovery_port)?;
        let listener = TcpListener::bind(SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::UNSPECIFIED, self.transfer_port))).await?;
        self.running.store(true, Ordering::SeqCst);
        let udp = Arc::new(udp);
        tokio::spawn(self.clone().listen_discovery(udp.clone()));
        tokio::spawn(self.clone().broadcast_loop(udp));
        tokio::spawn(self.clone().listen_transfers(listener));
        Ok(())
    }

    pub fn stop(&self) {
        self.running.store(false, Ordering::SeqCst);
        for (_, decision) in self.offers.lock().unwrap().drain() {
            let _ = decision.send(false);
        }
    }

    pub fn is_running(&self) -> bool {
        self.running.load(Ordering::SeqCst)
    }

    // ── Peers ───────────────────────────────────────────────────────────────

    pub fn peers(&self) -> Vec<Peer> {
        let mut list: Vec<Peer> = self.peers.lock().unwrap().values().cloned().collect();
        list.sort_by(|a, b| a.name.cmp(&b.name));
        list
    }

    pub fn peer(&self, id: &str) -> Option<Peer> {
        self.peers.lock().unwrap().get(&id.to_ascii_lowercase()).cloned()
    }

    /// Registers a computer by hand (tests): two engines on one machine share the transfer port the
    /// announcement insists on, so they cannot find each other by broadcast.
    pub fn add_peer(&self, id: &str, name: &str, address: Ipv4Addr, port: u16, fingerprint: &str) {
        let mut peers = self.peers.lock().unwrap();
        peers.insert(
            id.to_ascii_lowercase(),
            Peer {
                id: id.to_ascii_lowercase(),
                name: clean_name(name),
                photo: String::new(),
                address,
                port,
                fingerprint: fingerprint.to_ascii_lowercase(),
                paired: false,
                verified: false,
                identity_changed: false,
                sharing: None,
                seen: Instant::now(),
                sharing_seen: Instant::now(),
                manual: true,
            },
        );
        self.publish(&mut peers);
    }

    /// Fills in what the pairing store knows and tells the listeners. Holds the peers lock.
    fn publish(&self, peers: &mut HashMap<String, Peer>) {
        for peer in peers.values_mut() {
            let known = self.pairings.get(&peer.id);
            peer.paired = known.as_ref().map(|k| k.fingerprint == peer.fingerprint).unwrap_or(false);
            peer.verified = peer.paired && known.as_ref().map(|k| k.verified).unwrap_or(false);
            peer.identity_changed = known.as_ref().map(|k| k.fingerprint != peer.fingerprint).unwrap_or(false);
        }
        let mut list: Vec<Peer> = peers.values().cloned().collect();
        list.sort_by(|a, b| a.name.cmp(&b.name));
        (self.sink)(Event::Peers(list));
    }

    pub fn republish(&self) {
        let mut peers = self.peers.lock().unwrap();
        self.publish(&mut peers);
    }

    // ── Discovery ───────────────────────────────────────────────────────────

    async fn listen_discovery(self: Arc<Self>, udp: Arc<UdpSocket>) {
        let mut buffer = vec![0u8; 16_600];
        while self.is_running() {
            let Ok((n, from)) = udp.recv_from(&mut buffer).await else {
                if !self.is_running() {
                    return;
                }
                continue;
            };
            let IpAddr::V4(remote) = from.ip() else { continue };
            if n > 16_500 || !is_local_subnet(remote) {
                continue;
            }
            let Ok(text) = std::str::from_utf8(&buffer[..n]) else { continue };
            self.handle_announcement(text, remote);
        }
    }

    fn handle_announcement(&self, text: &str, remote: Ipv4Addr) {
        let fields: Vec<&str> = text.split('|').collect();
        match fields.as_slice() {
            ["LAZO3A", id, photo] if identity::is_guid(id) => {
                if photo.len() > 16_000 || !is_base64(photo) {
                    return;
                }
                let mut peers = self.peers.lock().unwrap();
                if let Some(peer) = peers.get_mut(&id.to_ascii_lowercase()) {
                    if peer.address == remote && peer.photo != *photo {
                        peer.photo = photo.to_string();
                        self.publish(&mut peers);
                    }
                }
            }
            ["LAZO3P", id, label] if identity::is_guid(id) => {
                // Sharing now (or stopped, if empty). It only serves to list them: joining goes through the channel.
                let sharing = clean_label(label, 120);
                let mut peers = self.peers.lock().unwrap();
                let Some(peer) = peers.get_mut(&id.to_ascii_lowercase()) else { return };
                if peer.address != remote {
                    return;
                }
                let now = if sharing.is_empty() { None } else { Some(sharing) };
                peer.sharing_seen = Instant::now();
                if peer.sharing != now {
                    peer.sharing = now;
                    self.publish(&mut peers);
                }
            }
            ["LAZO3", id, name, port, fingerprint]
                if text.len() <= 600 && identity::is_guid(id) && fingerprint.len() == 64 && fingerprint.chars().all(|c| c.is_ascii_hexdigit()) =>
            {
                let id = id.to_ascii_lowercase();
                let Ok(port) = port.parse::<u16>() else { return };
                if id == self.identity.id || port != self.transfer_port {
                    return;
                }
                let name = clean_label(name, 60);
                if name.is_empty() {
                    return;
                }
                let fingerprint = fingerprint.to_ascii_lowercase();
                let mut peers = self.peers.lock().unwrap();
                let previous = peers.get(&id).cloned();
                let changed = previous
                    .as_ref()
                    .map(|p| p.name != name || p.address != remote || p.port != port || p.fingerprint != fingerprint)
                    .unwrap_or(true);
                peers.insert(
                    id.clone(),
                    Peer {
                        id,
                        name,
                        photo: previous.as_ref().map(|p| p.photo.clone()).unwrap_or_default(),
                        address: remote,
                        port,
                        fingerprint,
                        paired: false,
                        verified: false,
                        identity_changed: false,
                        sharing: previous.as_ref().and_then(|p| p.sharing.clone()),
                        seen: Instant::now(),
                        sharing_seen: previous.as_ref().map(|p| p.sharing_seen).unwrap_or_else(Instant::now),
                        manual: false,
                    },
                );
                if changed {
                    self.publish(&mut peers);
                }
            }
            _ => {}
        }
    }

    async fn broadcast_loop(self: Arc<Self>, udp: Arc<UdpSocket>) {
        while self.is_running() {
            let announce = format!(
                "LAZO3|{}|{}|{}|{}",
                self.identity.id,
                self.display_name(),
                self.transfer_port,
                self.identity.fingerprint
            );
            let photo = format!("LAZO3A|{}|{}", self.identity.id, self.photo());
            for address in broadcast_addresses() {
                let target = SocketAddr::V4(SocketAddrV4::new(address, self.discovery_port));
                let _ = udp.send_to(announce.as_bytes(), target).await;
                let _ = udp.send_to(photo.as_bytes(), target).await;
            }
            {
                let mut peers = self.peers.lock().unwrap();
                let before = peers.len();
                peers.retain(|_, p| p.manual || p.seen.elapsed() <= Duration::from_secs(9));
                let mut ended = false;
                for peer in peers.values_mut() {
                    if peer.sharing.is_some() && peer.sharing_seen.elapsed() > Duration::from_secs(8) {
                        peer.sharing = None;
                        ended = true;
                    }
                }
                if peers.len() != before || ended {
                    self.publish(&mut peers);
                }
            }
            for _ in 0..25 {
                if !self.is_running() {
                    return;
                }
                tokio::time::sleep(Duration::from_millis(100)).await;
            }
        }
    }

    // ── Server: handshake and operations ────────────────────────────────────

    async fn listen_transfers(self: Arc<Self>, listener: TcpListener) {
        while self.is_running() {
            match listener.accept().await {
                Ok((client, from)) => {
                    let engine = self.clone();
                    tokio::spawn(async move {
                        let _ = engine.handle_connection(client, from).await;
                    });
                }
                Err(_) => {
                    if !self.is_running() {
                        return;
                    }
                }
            }
        }
    }

    async fn handle_connection(self: Arc<Self>, mut client: TcpStream, from: SocketAddr) -> io::Result<()> {
        let IpAddr::V4(address) = from.ip() else { return Ok(()) };
        if !is_local_subnet(address) {
            return Ok(());
        }
        let _ = client.set_nodelay(true);
        let mut magic = [0u8; 5];
        timeout(HANDSHAKE, client.read_exact(&mut magic)).await.map_err(|_| err("timeout"))??;
        if &magic != b"LAZO3" {
            return Ok(());
        }
        let mut session = timeout(HANDSHAKE, secure::accept(client, &self.identity)).await.map_err(|_| err("timeout"))??;
        let known = self.pairings.get(&session.peer_id);
        if let Some(known) = &known {
            if known.fingerprint != session.peer_fingerprint {
                session.writer.write_u8(STATUS_MISMATCH).await?;
                session.writer.flush().await?;
                return Ok(());
            }
        } else {
            // First contact: the key is kept without asking (like SSH), but only if it is what that computer
            // announces from this same address, so nobody can take a colleague's identifier and be remembered in their place.
            match self.check_first_contact(&session.peer_id, &session.peer_fingerprint, address) {
                FirstContact::Conflict => {
                    session.writer.write_u8(STATUS_MISMATCH).await?;
                    session.writer.flush().await?;
                    return Ok(());
                }
                FirstContact::Confirmed => {
                    let name = self.name_of(&session.peer_id);
                    self.pairings.set(&session.peer_id, &session.peer_fingerprint, &name);
                    self.republish();
                }
                FirstContact::Unconfirmed => {}
            }
        }
        session.writer.write_u8(STATUS_KNOWN).await?;
        session.writer.flush().await?;
        let op = timeout(IDLE, session.reader.read_u8()).await.map_err(|_| err("timeout"))??;
        let peer_id = session.peer_id.clone();
        match op {
            b'F' => self.receive_file(session, peer_id).await,
            b'C' => self.read_chat(session, peer_id).await,
            b'D' => self.read_signal(session, peer_id).await,
            // Verification code, screen, join, share request and remote access: not in this engine yet.
            _ => Ok(()),
        }
    }

    fn check_first_contact(&self, id: &str, fingerprint: &str, address: Ipv4Addr) -> FirstContact {
        let peers = self.peers.lock().unwrap();
        match peers.get(id) {
            None => FirstContact::Unconfirmed,
            Some(peer) if peer.fingerprint != fingerprint => FirstContact::Conflict,
            Some(peer) if peer.address == address => FirstContact::Confirmed,
            Some(_) => FirstContact::Unconfirmed,
        }
    }

    fn name_of(&self, id: &str) -> String {
        self.peers.lock().unwrap().get(id).map(|p| p.name.clone()).unwrap_or_else(|| "Equipo".to_string())
    }

    // ── Client: open a secure connection ────────────────────────────────────

    /// Connects and checks who answers. The first time the key is kept; afterwards, if it changes, it is refused.
    /// Errors carry a message for the person.
    async fn open(&self, peer: &Peer, connect_timeout: Duration) -> io::Result<Session> {
        if !is_local_subnet(peer.address) {
            return Err(err("El compañero ya no está en la misma subred."));
        }
        let expected = peer.id.clone();
        let known = self.pairings.get(&expected);
        let tcp = timeout(connect_timeout, TcpStream::connect(SocketAddr::V4(SocketAddrV4::new(peer.address, peer.port))))
            .await
            .map_err(|_| err("El compañero no respondió."))??;
        let _ = tcp.set_nodelay(true);
        let mut session = timeout(HANDSHAKE, secure::connect(tcp, &self.identity)).await.map_err(|_| err("El compañero no respondió."))??;
        if session.peer_id != expected {
            return Err(err("Respondió un equipo distinto del esperado."));
        }
        if let Some(known) = &known {
            if known.fingerprint != session.peer_fingerprint {
                return Err(err(&format!(
                    "La identidad de {} cambió. Si reinstaló Lazo, olvídalo en Ajustes › Equipos conocidos.",
                    peer.name
                )));
            }
        }
        let announced = !peer.fingerprint.is_empty();
        if known.is_none() && announced && peer.fingerprint != session.peer_fingerprint {
            return Err(err(&format!(
                "Respondió en nombre de {} un equipo con otra identidad. No se envió nada.",
                peer.name
            )));
        }
        let status = timeout(HANDSHAKE, session.reader.read_u8()).await.map_err(|_| err("El compañero no respondió."))??;
        if status == STATUS_MISMATCH {
            return Err(err(&format!(
                "{} tiene guardada otra identidad de este equipo. Debe olvidarlo en sus Ajustes.",
                peer.name
            )));
        }
        if status != STATUS_KNOWN {
            return Err(io::ErrorKind::UnexpectedEof.into());
        }
        if known.is_none() && announced {
            self.pairings.set(&expected, &session.peer_fingerprint, &peer.name);
            self.republish();
        }
        Ok(session)
    }

    fn find(&self, peer_id: &str) -> io::Result<Peer> {
        self.peer(peer_id).ok_or_else(|| err("Esa persona ya no está disponible."))
    }

    // ── Chat and signals ────────────────────────────────────────────────────

    pub async fn send_chat(&self, peer_id: &str, text: &str) -> io::Result<()> {
        let peer = self.find(peer_id)?;
        let mut text = text.trim().to_string();
        if text.is_empty() {
            return Ok(());
        }
        if text.chars().count() > 2000 {
            text = text.chars().take(2000).collect();
        }
        let mut session = self.open(&peer, Duration::from_secs(7)).await?;
        session.writer.write_u8(b'C').await?;
        session.writer.write_text(&self.display_name(), 80).await?;
        session.writer.write_text(&text, 4000).await?;
        session.writer.flush().await?;
        match timeout(Duration::from_secs(7), session.reader.read_u8()).await {
            Ok(Ok(1)) => Ok(()),
            _ => Err(err("No se pudo entregar el mensaje.")),
        }
    }

    /// Typing, presence, buzz and the rest. `reliable` reports failure instead of dropping the signal.
    pub async fn send_signal(&self, peer_id: &str, kind: u8, body: &str, reliable: bool) -> io::Result<()> {
        let peer = self.find(peer_id)?;
        if !reliable && !peer.paired {
            return Ok(());
        }
        let limit = if reliable { 3900 } else { 2000 };
        let body: String = body.chars().take(limit).collect();
        let mut session = match self.open(&peer, Duration::from_secs(4)).await {
            Ok(s) => s,
            Err(e) if reliable => return Err(e),
            Err(_) => return Ok(()),
        };
        session.writer.write_u8(b'D').await?;
        session.writer.write_text(&self.display_name(), 80).await?;
        session.writer.write_u8(kind).await?;
        session.writer.write_text(&body, 4000).await?;
        session.writer.flush().await?;
        match timeout(Duration::from_secs(5), session.reader.read_u8()).await {
            Ok(Ok(1)) => Ok(()),
            _ if reliable => Err(err("No se entregó.")),
            _ => Ok(()),
        }
    }

    async fn read_chat(&self, mut session: Session, peer_id: String) -> io::Result<()> {
        let name = clean_label(&session.reader.read_text(80).await?, 60);
        let body = session.reader.read_text(4000).await?.trim().to_string();
        if name.is_empty() || body.is_empty() || body.chars().count() > 2000 {
            return Ok(());
        }
        session.writer.write_u8(1).await?;
        session.writer.flush().await?;
        (self.sink)(Event::Chat { peer_id, name, text: body });
        Ok(())
    }

    async fn read_signal(&self, mut session: Session, peer_id: String) -> io::Result<()> {
        let name = clean_label(&session.reader.read_text(80).await?, 60);
        let kind = session.reader.read_u8().await?;
        let body = session.reader.read_text(4000).await?;
        if name.is_empty() {
            return Ok(());
        }
        session.writer.write_u8(1).await?;
        session.writer.flush().await?;
        if kind == CHAT_KIND_TEXT {
            let text = body.trim().to_string();
            if !text.is_empty() {
                (self.sink)(Event::Chat { peer_id, name, text });
            }
            return Ok(());
        }
        (self.sink)(Event::Signal { peer_id, name, kind, body });
        Ok(())
    }

    // ── Files ───────────────────────────────────────────────────────────────

    /// Sends a file. `progress` gets 0…1 as it goes.
    pub async fn send_file(&self, peer_id: &str, path: &Path, progress: impl Fn(f64) + Send) -> io::Result<()> {
        let peer = self.find(peer_id)?;
        let meta = tokio::fs::metadata(path).await.map_err(|_| err("El archivo ya no existe."))?;
        if !meta.is_file() {
            return Err(err("El archivo ya no existe."));
        }
        let size = meta.len() as i64;
        if size > MAX_FILE_SIZE {
            return Err(err("Límite: 20 GB por archivo."));
        }
        let name = path.file_name().map(|n| n.to_string_lossy().to_string()).unwrap_or_default();
        let mut session = self.open(&peer, Duration::from_secs(7)).await?;
        session.writer.write_u8(b'F').await?;
        session.writer.write_text(&self.display_name(), 80).await?;
        session.writer.write_text(&name, 255 * 4).await?;
        session.writer.write_i64(size).await?;
        session.writer.write_i32(0).await?; // no preview
        session.writer.flush().await?;
        let reply = timeout(IDLE, session.reader.read_u8()).await.map_err(|_| err("El destinatario no respondió."))??;
        if reply != 1 {
            return Err(err("El destinatario rechazó la transferencia o está ocupado."));
        }
        let mut file = tokio::fs::File::open(path).await?;
        let mut buffer = vec![0u8; MAX_PLAIN];
        let mut sent: i64 = 0;
        let mut sha = sha2::Sha256::default();
        use sha2::Digest;
        loop {
            let count = file.read(&mut buffer).await?;
            if count == 0 {
                break;
            }
            session.writer.write_all(&buffer[..count]).await?;
            sha.update(&buffer[..count]);
            sent += count as i64;
            progress(if size == 0 { 1.0 } else { sent as f64 / size as f64 });
        }
        let hash: [u8; 32] = sha.finalize().into();
        session.writer.write_all(&hash).await?;
        session.writer.flush().await?;
        match timeout(IDLE, session.reader.read_u8()).await {
            Ok(Ok(1)) => Ok(()),
            _ => Err(err("El destinatario no pudo guardar el archivo.")),
        }
    }

    /// The person's answer to an offer.
    pub fn decide_offer(&self, id: &str, accept: bool) {
        if let Some(decision) = self.offers.lock().unwrap().remove(id) {
            let _ = decision.send(accept);
        }
    }

    async fn receive_file(&self, mut session: Session, peer_id: String) -> io::Result<()> {
        let sender = clean_label(&session.reader.read_text(80).await?, 60);
        let name = safe_file_name(&session.reader.read_text(255 * 4).await?);
        let size = session.reader.read_i64().await?;
        let preview_length = session.reader.read_i32().await?;
        if sender.is_empty() || name.is_empty() || size < 0 || size > MAX_FILE_SIZE || !(0..=48_000).contains(&preview_length) {
            return Ok(());
        }
        let preview = session.reader.read_bytes(preview_length as usize).await?;
        let offer_id = identity::new_guid();
        let folder = self.receive_dir();
        if self.active_receives.fetch_add(1, Ordering::SeqCst) >= 8 || !has_room(&folder, size) {
            self.active_receives.fetch_sub(1, Ordering::SeqCst);
            session.writer.write_u8(0).await?;
            session.writer.flush().await?;
            return Ok(());
        }
        let outcome = self.receive_accepted(&mut session, &peer_id, &offer_id, sender, name, size, preview, &folder).await;
        self.active_receives.fetch_sub(1, Ordering::SeqCst);
        outcome
    }

    #[allow(clippy::too_many_arguments)]
    async fn receive_accepted(
        &self,
        session: &mut Session,
        peer_id: &str,
        offer_id: &str,
        sender: String,
        name: String,
        size: i64,
        preview: Vec<u8>,
        folder: &Path,
    ) -> io::Result<()> {
        let (decide, decision) = oneshot::channel();
        self.offers.lock().unwrap().insert(offer_id.to_string(), decide);
        (self.sink)(Event::Offer(OfferInfo {
            id: offer_id.to_string(),
            peer_id: peer_id.to_string(),
            sender,
            file_name: name.clone(),
            size,
            preview,
        }));
        let allow = matches!(timeout(OFFER_WAIT, decision).await, Ok(Ok(true)));
        self.offers.lock().unwrap().remove(offer_id);
        if !allow {
            session.writer.write_u8(0).await?;
            session.writer.flush().await?;
            return Ok(());
        }
        session.writer.write_u8(1).await?;
        session.writer.flush().await?;
        let finish = |message: String, path: Option<String>| {
            (self.sink)(Event::ReceiveFinished { id: offer_id.to_string(), message, path });
        };
        let result = self.write_incoming(session, offer_id, &name, size, folder).await;
        match result {
            Ok(destination) => {
                // The channel may have closed after the last byte: the file is safe either way.
                let _ = session.writer.write_u8(1).await;
                let _ = session.writer.flush().await;
                finish("Archivo recibido".to_string(), Some(destination.to_string_lossy().to_string()));
            }
            Err(e) => finish(format!("Error: {e}"), None),
        }
        Ok(())
    }

    async fn write_incoming(&self, session: &mut Session, offer_id: &str, name: &str, size: i64, folder: &Path) -> io::Result<PathBuf> {
        use sha2::Digest;
        tokio::fs::create_dir_all(folder).await?;
        let temp = folder.join(format!(".{}.part", offer_id.replace('-', "")));
        let outcome = async {
            let mut file = tokio::fs::OpenOptions::new().write(true).create_new(true).open(&temp).await?;
            let mut buffer = vec![0u8; MAX_PLAIN];
            let mut received: i64 = 0;
            let mut sha = sha2::Sha256::default();
            while received < size {
                let want = buffer.len().min((size - received) as usize);
                let count = timeout(Duration::from_secs(30), session.reader.read(&mut buffer[..want]))
                    .await
                    .map_err(|_| err("Se cortó la conexión."))??;
                if count == 0 {
                    return Err(io::ErrorKind::UnexpectedEof.into());
                }
                file.write_all(&buffer[..count]).await?;
                sha.update(&buffer[..count]);
                received += count as i64;
                (self.sink)(Event::ReceiveProgress {
                    id: offer_id.to_string(),
                    value: if size == 0 { 1.0 } else { received as f64 / size as f64 },
                });
            }
            file.flush().await?;
            drop(file);
            let actual: [u8; 32] = sha.finalize().into();
            let expected = session.reader.read_bytes(32).await?;
            if !crypto::fixed_eq(&actual, &expected) {
                return Err(err("El archivo no superó la verificación de integridad."));
            }
            let destination = unique_destination(folder, name);
            tokio::fs::rename(&temp, &destination).await?;
            mark_of_the_web(&destination);
            Ok(destination)
        }
        .await;
        if outcome.is_err() {
            let _ = tokio::fs::remove_file(&temp).await;
        }
        outcome
    }
}

enum FirstContact {
    Confirmed,
    Unconfirmed,
    Conflict,
}

// ── Network helpers ───────────────────────────────────────────────────────────

fn is_private(address: Ipv4Addr) -> bool {
    let b = address.octets();
    b[0] == 10 || (b[0] == 172 && (16..=31).contains(&b[1])) || (b[0] == 192 && b[1] == 168)
}

fn local_interfaces() -> Vec<if_addrs::Ifv4Addr> {
    if_addrs::get_if_addrs()
        .unwrap_or_default()
        .into_iter()
        .filter(|i| i.oper_status == if_addrs::IfOperStatus::Up)
        .filter_map(|i| match i.addr {
            if_addrs::IfAddr::V4(v4) => Some(v4),
            _ => None,
        })
        .collect()
}

/// A private address on one of this computer's own subnets.
pub fn is_local_subnet(address: Ipv4Addr) -> bool {
    if !is_private(address) {
        return false;
    }
    let target = u32::from(address);
    local_interfaces().iter().any(|i| {
        let mask = u32::from(i.netmask);
        (target & mask) == (u32::from(i.ip) & mask)
    })
}

fn broadcast_addresses() -> Vec<Ipv4Addr> {
    let mut seen = Vec::new();
    for i in local_interfaces() {
        if i.ip.is_loopback() || !is_private(i.ip) {
            continue;
        }
        let broadcast = Ipv4Addr::from(u32::from(i.ip) | !u32::from(i.netmask));
        if !seen.contains(&broadcast) {
            seen.push(broadcast);
        }
    }
    seen
}

fn bind_discovery(port: u16) -> io::Result<UdpSocket> {
    use socket2::{Domain, Protocol, Socket, Type};
    let socket = Socket::new(Domain::IPV4, Type::DGRAM, Some(Protocol::UDP))?;
    socket.set_reuse_address(true)?;
    socket.set_broadcast(true)?;
    socket.set_nonblocking(true)?;
    socket.bind(&SocketAddr::V4(SocketAddrV4::new(Ipv4Addr::UNSPECIFIED, port)).into())?;
    UdpSocket::from_std(socket.into())
}

fn is_base64(value: &str) -> bool {
    use base64::Engine as _;
    value.is_empty() || base64::engine::general_purpose::STANDARD.decode(value).is_ok()
}

// ── Files and folders ─────────────────────────────────────────────────────────

fn saved_receive_dir(profile: &Path) -> Option<PathBuf> {
    let text = std::fs::read_to_string(profile.join("receive-folder.txt")).ok()?;
    let path = PathBuf::from(text.trim());
    (path.is_absolute()).then_some(path)
}

/// Downloads\Lazo.
pub fn default_receive_dir() -> PathBuf {
    downloads_dir().join("Lazo")
}

fn downloads_dir() -> PathBuf {
    #[cfg(windows)]
    {
        use windows::Win32::UI::Shell::{FOLDERID_Downloads, SHGetKnownFolderPath, KF_FLAG_DEFAULT};
        // SAFETY: the returned string belongs to COM and is freed once it is copied.
        unsafe {
            if let Ok(p) = SHGetKnownFolderPath(&FOLDERID_Downloads, KF_FLAG_DEFAULT, None) {
                let path = p.to_string().ok().map(PathBuf::from);
                windows::Win32::System::Com::CoTaskMemFree(Some(p.as_ptr() as *const _));
                if let Some(path) = path {
                    return path;
                }
            }
        }
    }
    std::env::var_os("USERPROFILE")
        .or_else(|| std::env::var_os("HOME"))
        .map(|h| PathBuf::from(h).join("Downloads"))
        .unwrap_or_else(|| PathBuf::from("Downloads"))
}

/// Room for the file and a 256 MB margin.
fn has_room(folder: &Path, size: i64) -> bool {
    #[cfg(windows)]
    {
        use windows::core::PCWSTR;
        use windows::Win32::Storage::FileSystem::GetDiskFreeSpaceExW;
        // The nearest folder that exists decides which drive is asked.
        let mut probe = folder.to_path_buf();
        while !probe.exists() {
            if !probe.pop() {
                return true;
            }
        }
        let wide: Vec<u16> = probe.to_string_lossy().encode_utf16().chain(std::iter::once(0)).collect();
        let mut free = 0u64;
        // SAFETY: `wide` is null-terminated and outlives the call; `free` is a valid out pointer.
        let ok = unsafe { GetDiskFreeSpaceExW(PCWSTR(wide.as_ptr()), Some(&mut free), None, None) }.is_ok();
        return !ok || free > size as u64 + 256 * 1024 * 1024;
    }
    #[cfg(not(windows))]
    {
        let _ = (folder, size);
        true
    }
}

/// Marks what arrived as downloaded from another computer (Zone.Identifier, Internet zone), as a browser
/// does: SmartScreen and Office's Protected View look at it before it can run.
fn mark_of_the_web(path: &Path) {
    #[cfg(windows)]
    {
        let stream = format!("{}:Zone.Identifier", path.display());
        let _ = std::fs::write(stream, "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet\r\n");
    }
    #[cfg(not(windows))]
    let _ = path;
}
