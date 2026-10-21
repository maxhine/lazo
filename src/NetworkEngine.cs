using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lazo
{
    internal sealed class Peer
    {
        public Guid Id;
        public string Name;
        public string Photo = "";
        public IPAddress Address;
        public int Port;
        public DateTime SeenUtc;
        /// <summary>Huella de la clave que anuncia el equipo (sin verificar hasta conectar).</summary>
        public string Fingerprint = "";
        /// <summary>Conocido (ya se conectaron) y con la misma huella que la guardada.</summary>
        public bool Paired;
        /// <summary>Además, las dos personas confirmaron el código de verificación.</summary>
        public bool Verified;
        /// <summary>Conocido antes, pero ahora anuncia otra clave: no se le envía nada.</summary>
        public bool IdentityChanged;
        /// <summary>Qué está compartiendo ahora (anuncio LAZO3P), o null si no comparte.</summary>
        public string Sharing;
        public DateTime SharingSeenUtc;
        public override string ToString() { return Name + "  /  " + Address; }
    }

    internal sealed class ScreenInvite
    {
        public Guid Id;
        public string Sender;
        public string Source;
        public IPAddress Address;
    }

    /// <summary>Destino de una transmisión de pantalla aceptada; lee hasta que termina.</summary>
    internal interface IScreenSink
    {
        void Run(Stream stream);
    }

    internal sealed class Offer
    {
        public Guid Id;
        public Guid PeerId;
        public string Sender;
        public IPAddress Address;
        public string FileName;
        public long Size;
        public byte[] Preview;
    }

    /// <summary>Verificación opcional: las dos personas comparan el mismo código de 6 cifras.</summary>
    internal sealed class PairingRequest
    {
        public string PeerId;
        public string PeerName;
        public string Code;
        public string Fingerprint;
        /// <summary>true si la pidió el otro equipo.</summary>
        public bool Incoming;
        /// <summary>Decisión del otro equipo; termina en false si cancela o se corta.</summary>
        public Task<bool> Remote;
    }

    /// <summary>Conexión cifrada y autenticada con un compañero emparejado.</summary>
    internal sealed class SecureConnection : IDisposable
    {
        public TcpClient Client;
        public SecureStream Stream;
        public string PeerId;
        internal byte[] Transcript;
        internal string Fingerprint;

        public void Dispose()
        {
            try { Stream.Dispose(); } catch { }
            try { Client.Close(); } catch { }
        }
    }

    /// <summary>Un equipo conocido se presenta con otra clave: no se le envía nada.</summary>
    internal sealed class IdentityChangedException : InvalidOperationException
    {
        public IdentityChangedException(string message) : base(message) { }
    }

    internal sealed class NetworkEngine : IDisposable
    {
        public const int DiscoveryPort = 48351;
        public const int TransferPort = 48352;
        private const long MaxFileSize = 20L * 1024 * 1024 * 1024;
        private const byte StatusKnown = 1, StatusMismatch = 2;
        private static readonly Guid DownloadsId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        private readonly string _profile;
        private readonly Guid _id;
        private readonly DeviceIdentity _identity;
        public readonly PairingStore Pairings;
        /// <summary>Control remoto con contraseña de este equipo (anfitrión): contraseña guardada, activación y bloqueos.</summary>
        public readonly RemoteAccess Remote;
        public Guid SelfId { get { return _id; } }
        public string SelfFingerprint { get { return _identity.Fingerprint; } }
        public event Action<Guid, string, string> ChatReceived;
        public event Action<Guid, string, byte, string> ChatSignal;
        public const byte ChatKindText = 1;
        public const byte ChatKindTyping = 2;
        public const byte ChatKindPresence = 3;
        public const byte ChatKindNudge = 4;
        /// <summary>Eliminar un mensaje para todos: id, o grupo e id en dos líneas.</summary>
        public const byte ChatKindDelete = 5;
        /// <summary>Mensaje de grupo: grupo, id y texto, cada uno en su línea.</summary>
        public const byte ChatKindGroupMessage = 6;
        /// <summary>Alta o cambio de un grupo (lista de integrantes).</summary>
        public const byte ChatKindGroupInfo = 7;
        /// <summary>Alguien sale de un grupo: «grupo».</summary>
        public const byte ChatKindGroupLeave = 8;
        private volatile string _receiveDirectory;
        private readonly int _discoveryPort;
        private readonly int _transferPort;
        private readonly object _peerLock = new object();
        private readonly Dictionary<Guid, Peer> _peers = new Dictionary<Guid, Peer>();
        private readonly object _pairLock = new object();
        private readonly HashSet<string> _pairingOpen = new HashSet<string>();
        private readonly Dictionary<string, List<DateTime>> _pairingAsked = new Dictionary<string, List<DateTime>>();
        private UdpClient _udp;
        private TcpListener _listener;
        private volatile bool _running;
        private int _activeReceives;

        public event Action<List<Peer>> PeersChanged;
        public event Func<Offer, Task<bool>> OfferReceived;
        public event Action<Guid, double> ReceiveProgress;
        public event Action<Guid, string, string> ReceiveFinished;
        /// <summary>Verificación: pide a la persona que compare el código. Devuelve true si pulsa «Coincide».</summary>
        public event Func<PairingRequest, Task<bool>> PairingPrompt;

        public NetworkEngine(string receiveDirectory = null, int discoveryPort = DiscoveryPort, int transferPort = TransferPort, string profileDirectory = null)
        {
            _discoveryPort = discoveryPort;
            _transferPort = transferPort;
            _profile = profileDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo");
            _receiveDirectory = receiveDirectory ?? SavedReceiveDirectory() ?? DefaultReceiveDirectory();
            _id = LoadId(_profile);
            _identity = DeviceIdentity.Load(_profile, _id);
            Pairings = new PairingStore(_profile);
            Remote = new RemoteAccess(_profile);
            Pairings.Changed += () => { lock (_peerLock) PublishPeers(); };
        }

        public void Start()
        {
            try
            {
                _udp = new UdpClient(AddressFamily.InterNetwork);
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
                _udp.EnableBroadcast = true;
                _listener = new TcpListener(IPAddress.Any, _transferPort);
                _listener.Start(8);
            }
            catch
            {
                if (_udp != null) _udp.Close();
                if (_listener != null) _listener.Stop();
                throw;
            }
            _running = true;
            Task.Run((Func<Task>)ListenDiscovery);
            Task.Run((Action)BroadcastLoop);
            Task.Run((Func<Task>)ListenTransfers);
        }

        // ── Descubrimiento ───────────────────────────────────────────────

        private async Task ListenDiscovery()
        {
            while (_running)
            {
                try
                {
                    UdpReceiveResult packet = await _udp.ReceiveAsync().ConfigureAwait(false);
                    IPEndPoint remote = packet.RemoteEndPoint;
                    byte[] data = packet.Buffer;
                    if (data.Length > 16500 || !IsLocalSubnet(remote.Address)) continue;
                    string[] fields = Encoding.UTF8.GetString(data).Split('|');
                    Guid id;
                    int port;
                    if (fields.Length == 3 && fields[0] == "LAZO3A" && Guid.TryParse(fields[1], out id))
                    {
                        if (fields[2].Length > 16000 || !IsBase64(fields[2])) continue;
                        lock (_peerLock)
                        {
                            Peer peer;
                            if (_peers.TryGetValue(id, out peer) && peer.Address.Equals(remote.Address) && peer.Photo != fields[2])
                            {
                                peer.Photo = fields[2];
                                PublishPeers();
                            }
                        }
                        continue;
                    }
                    if (fields.Length == 3 && fields[0] == "LAZO3P" && Guid.TryParse(fields[1], out id))
                    {
                        // Está compartiendo (o dejó de hacerlo, si el texto viene vacío). Solo sirve para listarlo:
                        // unirse pasa por el canal cifrado y lo decide quien comparte.
                        string sharing = CleanLabel(fields[2], 120);
                        lock (_peerLock)
                        {
                            Peer peer;
                            if (!_peers.TryGetValue(id, out peer) || !peer.Address.Equals(remote.Address)) continue;
                            string now = sharing.Length == 0 ? null : sharing;
                            peer.SharingSeenUtc = DateTime.UtcNow;
                            if (peer.Sharing != now) { peer.Sharing = now; PublishPeers(); }
                        }
                        continue;
                    }
                    if (data.Length > 600 || fields.Length != 5 || fields[0] != "LAZO3" ||
                        !Guid.TryParse(fields[1], out id) || id == _id ||
                        !int.TryParse(fields[3], out port) || port != _transferPort ||
                        fields[4].Length != 64 || !fields[4].All(Uri.IsHexDigit)) continue;
                    string name = CleanLabel(fields[2]);
                    if (name.Length == 0) continue;
                    string fingerprint = fields[4].ToLowerInvariant();
                    lock (_peerLock)
                    {
                        Peer previous;
                        bool changed = !_peers.TryGetValue(id, out previous) || previous.Name != name ||
                                       !previous.Address.Equals(remote.Address) || previous.Port != port ||
                                       previous.Fingerprint != fingerprint;
                        _peers[id] = new Peer
                        {
                            Id = id, Name = name, Address = remote.Address, Port = port, SeenUtc = DateTime.UtcNow,
                            Photo = previous == null ? "" : previous.Photo, Fingerprint = fingerprint,
                            Sharing = previous == null ? null : previous.Sharing,
                            SharingSeenUtc = previous == null ? DateTime.MinValue : previous.SharingSeenUtc
                        };
                        if (changed) PublishPeers();
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (!_running) return; }
                catch { /* Paquete de red no válido: se ignora. */ }
            }
        }

        private static bool IsBase64(string value)
        {
            if (value.Length == 0) return true;
            try { Convert.FromBase64String(value); return true; }
            catch (FormatException) { return false; }
        }

        private void BroadcastLoop()
        {
            while (_running)
            {
                try
                {
                    byte[] payload = Encoding.UTF8.GetBytes("LAZO3|" + _id.ToString("D") + "|" + Label() + "|" + _transferPort + "|" + _identity.Fingerprint);
                    byte[] photo = Encoding.UTF8.GetBytes("LAZO3A|" + _id.ToString("D") + "|" + ProfilePhoto.Current);
                    foreach (IPAddress address in BroadcastAddresses())
                    {
                        _udp.Send(payload, payload.Length, new IPEndPoint(address, _discoveryPort));
                        _udp.Send(photo, photo.Length, new IPEndPoint(address, _discoveryPort));
                    }
                    string sharing = _sharing;
                    if (sharing != null) AnnounceSharing(sharing);
                    lock (_peerLock)
                    {
                        Guid[] stale = _peers.Where(p => (DateTime.UtcNow - p.Value.SeenUtc).TotalSeconds > 9)
                            .Select(p => p.Key).ToArray();
                        foreach (Guid key in stale) _peers.Remove(key);
                        // Quien dejó de anunciar que comparte (se cerró de golpe, por ejemplo) sale de la lista.
                        bool ended = false;
                        foreach (Peer peer in _peers.Values)
                            if (peer.Sharing != null && (DateTime.UtcNow - peer.SharingSeenUtc).TotalSeconds > 8) { peer.Sharing = null; ended = true; }
                        if (stale.Length > 0 || ended) PublishPeers();
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { /* Firewall o adaptador desconectado. Se reintenta. */ }
                for (int i = 0; i < 25 && _running; i++) Thread.Sleep(100);
            }
        }

        private volatile string _sharing;

        /// <summary>
        /// Anuncia a la red qué se está compartiendo (null: ya no). Los demás lo ven en su panel y pueden pedir unirse.
        /// </summary>
        public void SetSharing(string label)
        {
            label = label == null ? null : CleanLabel(label, 120);
            if (label != null && label.Length == 0) label = "Pantalla";
            _sharing = label;
            AnnounceSharing(label ?? "");
        }

        private void AnnounceSharing(string label)
        {
            if (_udp == null) return;
            try
            {
                byte[] packet = Encoding.UTF8.GetBytes("LAZO3P|" + _id.ToString("D") + "|" + label);
                foreach (IPAddress address in BroadcastAddresses())
                    _udp.Send(packet, packet.Length, new IPEndPoint(address, _discoveryPort));
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        /// <summary>Llamar con _peerLock tomado.</summary>
        private void PublishPeers()
        {
            foreach (Peer peer in _peers.Values)
            {
                PairedDevice paired = Pairings.Get(peer.Id.ToString("D"));
                peer.Paired = paired != null && paired.Fingerprint == peer.Fingerprint;
                peer.Verified = peer.Paired && paired.Verified;
                peer.IdentityChanged = paired != null && paired.Fingerprint != peer.Fingerprint;
            }
            Action<List<Peer>> handler = PeersChanged;
            if (handler != null) handler(_peers.Values.OrderBy(p => p.Name).ToList());
        }

        private static IEnumerable<IPAddress> BroadcastAddresses()
        {
            HashSet<string> seen = new HashSet<string>();
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation entry in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (entry.Address.AddressFamily != AddressFamily.InterNetwork || entry.IPv4Mask == null ||
                        !IsPrivate(entry.Address)) continue;
                    byte[] ip = entry.Address.GetAddressBytes();
                    byte[] mask = entry.IPv4Mask.GetAddressBytes();
                    byte[] broadcast = new byte[4];
                    for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    IPAddress result = new IPAddress(broadcast);
                    if (seen.Add(result.ToString())) yield return result;
                }
            }
        }

        private static bool IsPrivate(IPAddress address)
        {
            byte[] b = address.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168));
        }

        private static bool IsLocalSubnet(IPAddress address)
        {
            if (!IsPrivate(address)) return false;
            byte[] target = address.GetAddressBytes();
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                foreach (UnicastIPAddressInformation entry in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (entry.Address.AddressFamily != AddressFamily.InterNetwork || entry.IPv4Mask == null) continue;
                    byte[] local = entry.Address.GetAddressBytes();
                    byte[] mask = entry.IPv4Mask.GetAddressBytes();
                    bool match = true;
                    for (int i = 0; i < 4; i++) if ((target[i] & mask[i]) != (local[i] & mask[i])) match = false;
                    if (match) return true;
                }
            }
            return false;
        }

        public List<Peer> Snapshot()
        {
            lock (_peerLock) return _peers.Values.OrderBy(item => item.Name).ToList();
        }

        // ── Servidor: saludo, emparejamiento y operaciones ───────────────

        private async Task ListenTransfers()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    Task handled = Task.Run(() => HandleConnection(client));
                }
                catch (SocketException) { if (!_running) return; }
                catch (ObjectDisposedException) { return; }
            }
        }

        private async Task HandleConnection(TcpClient client)
        {
            using (client)
            {
                SecureSession session = null;
                try
                {
                    IPAddress address = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                    if (!IsLocalSubnet(address)) return;
                    client.ReceiveTimeout = SecureSession.HandshakeTimeoutMs;
                    client.SendTimeout = 30000;
                    NetworkStream network = client.GetStream();
                    byte[] magic = SecureSession.ReadExactly(network, 5);
                    if (Encoding.ASCII.GetString(magic) != "LAZO3") return;
                    session = SecureSession.Accept(network, _identity);
                    PairedDevice known = Pairings.Get(session.PeerId);
                    SecureStream stream = session.Stream;
                    if (known != null && known.Fingerprint != session.PeerFingerprint)
                    {
                        stream.WriteByte(StatusMismatch);
                        stream.Flush();
                        return;
                    }
                    if (known == null)
                    {
                        // Primer contacto: se recuerda su clave sin preguntar (como SSH), pero solo si coincide con lo que
                        // ese equipo anuncia desde esa misma dirección. Así nadie puede adelantarse con el identificador
                        // de un compañero (se ve en los anuncios) y quedar guardado en su lugar.
                        FirstContact contact = CheckFirstContact(session.PeerId, session.PeerFingerprint, address);
                        if (contact == FirstContact.Conflict)
                        {
                            stream.WriteByte(StatusMismatch);
                            stream.Flush();
                            return;
                        }
                        if (contact == FirstContact.Confirmed) Pairings.Set(session.PeerId, session.PeerFingerprint, NameOf(session.PeerId));
                    }
                    stream.WriteByte(StatusKnown);
                    stream.Flush();
                    client.ReceiveTimeout = 120000;
                    BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true);
                    BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true);
                    byte op = reader.ReadByte();
                    if (op == (byte)'P')
                    {
                        // Verificación opcional con código: solo marca el equipo como verificado.
                        string name = CleanLabel(ReadText(reader, 80));
                        if (name.Length > 0) await VerifyIncomingAsync(session, name).ConfigureAwait(false);
                        return;
                    }
                    Guid peerId = Guid.Parse(session.PeerId);
                    switch (op)
                    {
                        case (byte)'F': await ReceiveFile(client, stream, reader, writer, address, peerId).ConfigureAwait(false); break;
                        case (byte)'C': ReadChat(reader, writer, peerId); break;
                        case (byte)'D': ReadSignal(reader, writer, peerId); break;
                        case (byte)'S': await ReadScreen(client, stream, reader, writer, address, peerId).ConfigureAwait(false); break;
                        case (byte)'J': ReadJoin(reader, writer, peerId); break;
                        case (byte)'Q': ReadShareRequest(reader, writer, peerId); break;
                        case (byte)'W': await ReadRemoteAccess(client, stream, reader, writer, peerId).ConfigureAwait(false); break;
                    }
                }
                catch { /* Conexión no válida, cortada o rechazada. */ }
                finally
                {
                    if (session != null) try { session.Stream.Dispose(); } catch { }
                }
            }
        }

        private enum FirstContact { Confirmed, Unconfirmed, Conflict }

        /// <summary>
        /// Contrasta un equipo desconocido con su anuncio. Huella anunciada distinta: suplantación, se rechaza.
        /// Misma huella y misma dirección: se recuerda. Sin anuncio u otra dirección: se atiende sin recordarlo.
        /// </summary>
        private FirstContact CheckFirstContact(string id, string fingerprint, IPAddress address)
        {
            Guid guid;
            Peer peer;
            lock (_peerLock)
            {
                if (!Guid.TryParse(id, out guid) || !_peers.TryGetValue(guid, out peer)) return FirstContact.Unconfirmed;
                if (peer.Fingerprint != fingerprint) return FirstContact.Conflict;
                return peer.Address.Equals(address) ? FirstContact.Confirmed : FirstContact.Unconfirmed;
            }
        }

        private string NameOf(string id)
        {
            Guid guid;
            Peer peer;
            lock (_peerLock) return Guid.TryParse(id, out guid) && _peers.TryGetValue(guid, out peer) ? peer.Name : "Equipo";
        }

        private async Task<bool> VerifyIncomingAsync(SecureSession session, string name)
        {
            lock (_pairLock)
            {
                List<DateTime> asked;
                if (!_pairingAsked.TryGetValue(session.PeerId, out asked)) _pairingAsked[session.PeerId] = asked = new List<DateTime>();
                asked.RemoveAll(t => (DateTime.UtcNow - t).TotalMinutes > 5);
                if (_pairingOpen.Contains(session.PeerId) || asked.Count >= 3)
                {
                    try { session.Stream.WriteByte(0); session.Stream.Flush(); } catch { }
                    return false;
                }
                asked.Add(DateTime.UtcNow);
                _pairingOpen.Add(session.PeerId);
            }
            try
            {
                bool ok = await ExchangePairingAsync(session, name, true).ConfigureAwait(false);
                if (ok) Pairings.MarkVerified(session.PeerId, name);
                return ok;
            }
            finally
            {
                lock (_pairLock) _pairingOpen.Remove(session.PeerId);
            }
        }

        /// <summary>Muestra el código, envía la decisión propia y espera la del otro equipo.</summary>
        private async Task<bool> ExchangePairingAsync(SecureSession session, string name, bool incoming)
        {
            SecureStream stream = session.Stream;
            Task<int> remoteByte = Task.Run(() => { try { return stream.ReadByte(); } catch { return -1; } });
            Task<bool> remote = remoteByte.ContinueWith(t => t.Result == 1);
            PairingRequest request = new PairingRequest
            {
                PeerId = session.PeerId, PeerName = name, Code = session.Sas,
                Fingerprint = session.PeerFingerprint, Incoming = incoming, Remote = remote
            };
            bool mine = false;
            Func<PairingRequest, Task<bool>> handler = PairingPrompt;
            if (handler != null)
            {
                Task<bool> local = handler(request);
                if (await Task.WhenAny(local, Task.Delay(90000)).ConfigureAwait(false) == local) mine = local.Result;
            }
            try { stream.WriteByte(mine ? (byte)1 : (byte)0); stream.Flush(); }
            catch { return false; }
            if (!mine) return false;
            if (await Task.WhenAny(remote, Task.Delay(95000)).ConfigureAwait(false) != remote) return false;
            return remote.Result;
        }

        // ── Cliente: abrir una conexión segura ───────────────────────────

        /// <summary>
        /// Conecta y comprueba la identidad del otro equipo. La primera vez se recuerda su clave;
        /// después, si cambia, se rechaza. Lanza una excepción con un mensaje para la persona si no puede.
        /// </summary>
        private async Task<SecureConnection> OpenAsync(Peer peer, int connectTimeoutMs = 7000)
        {
            if (peer == null || peer.Address == null) throw new InvalidOperationException("El compañero no está conectado.");
            if (!IsLocalSubnet(peer.Address)) throw new InvalidOperationException("El compañero ya no está en la misma subred.");
            string expected = peer.Id.ToString("D");
            PairedDevice known = Pairings.Get(expected);
            TcpClient client = new TcpClient(AddressFamily.InterNetwork);
            SecureSession session = null;
            try
            {
                Task connect = client.ConnectAsync(peer.Address, peer.Port);
                if (await Task.WhenAny(connect, Task.Delay(connectTimeoutMs)).ConfigureAwait(false) != connect)
                    throw new TimeoutException("El compañero no respondió.");
                await connect.ConfigureAwait(false);
                client.ReceiveTimeout = SecureSession.HandshakeTimeoutMs;
                client.SendTimeout = 30000;
                NetworkStream network = client.GetStream();
                session = await Task.Run(() => SecureSession.Connect(network, _identity)).ConfigureAwait(false);
                if (session.PeerId != expected) throw new InvalidOperationException("Respondió un equipo distinto del esperado.");
                if (known != null && known.Fingerprint != session.PeerFingerprint)
                    throw new IdentityChangedException("La identidad de " + peer.Name + " cambió. Si reinstaló Lazo, olvídalo en Ajustes › Equipos conocidos.");
                // Primer contacto: la clave debe ser la que anuncia ese equipo.
                bool announced = !string.IsNullOrEmpty(peer.Fingerprint);
                if (known == null && announced && peer.Fingerprint != session.PeerFingerprint)
                    throw new IdentityChangedException("Respondió en nombre de " + peer.Name + " un equipo con otra identidad. No se envió nada.");
                int status = session.Stream.ReadByte();
                if (status == StatusMismatch)
                    throw new IdentityChangedException(peer.Name + " tiene guardada otra identidad de este equipo. Debe olvidarlo en sus Ajustes.");
                if (status != StatusKnown) throw new EndOfStreamException();
                if (known == null && announced) Pairings.Set(expected, session.PeerFingerprint, peer.Name);
                client.ReceiveTimeout = 120000;
                return new SecureConnection { Client = client, Stream = session.Stream, PeerId = session.PeerId, Transcript = session.Transcript, Fingerprint = session.PeerFingerprint };
            }
            catch
            {
                if (session != null) try { session.Stream.Dispose(); } catch { }
                client.Close();
                throw;
            }
        }

        /// <summary>
        /// Verificación opcional: las dos personas comparan un código de 6 cifras. Si coincide, nadie se
        /// interpuso en la red y el equipo queda marcado como verificado. Devuelve false si se cancela o no coincide.
        /// </summary>
        public async Task<bool> VerifyAsync(Peer peer)
        {
            using (SecureConnection connection = await OpenAsync(peer).ConfigureAwait(false))
            {
                BinaryWriter writer = new BinaryWriter(connection.Stream, Encoding.UTF8, true);
                writer.Write((byte)'P');
                WriteText(writer, Label(), 80);
                writer.Flush();
                SecureSession session = new SecureSession { Stream = connection.Stream, PeerId = connection.PeerId, Transcript = connection.Transcript, PeerFingerprint = connection.Fingerprint };
                bool ok = await ExchangePairingAsync(session, peer.Name, false).ConfigureAwait(false);
                if (ok) Pairings.MarkVerified(connection.PeerId, peer.Name);
                return ok;
            }
        }

        // ── Chat y señales ───────────────────────────────────────────────

        public async Task SendChatAsync(Peer peer, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > 2000) text = text.Substring(0, 2000);
            using (SecureConnection connection = await OpenAsync(peer).ConfigureAwait(false))
            {
                BinaryWriter writer = new BinaryWriter(connection.Stream, Encoding.UTF8, true);
                writer.Write((byte)'C');
                WriteText(writer, Label(), 80);
                WriteText(writer, text, 4000);
                writer.Flush();
                if (await ReadByteAsync(connection.Stream, 7000).ConfigureAwait(false) != 1) throw new IOException("No se pudo entregar el mensaje.");
            }
        }

        private void ReadChat(BinaryReader reader, BinaryWriter writer, Guid id)
        {
            string name = CleanLabel(ReadText(reader, 80));
            string body = ReadText(reader, 4000).Trim();
            if (name.Length == 0 || body.Length == 0 || body.Length > 2000) return;
            writer.Write((byte)1);
            writer.Flush();
            Action<Guid, string, string> handler = ChatReceived;
            if (handler != null) handler(id, name, body);
        }

        /// <summary>Escribiendo, presencia y zumbido: solo a equipos con los que ya hubo contacto.</summary>
        public async Task SendSignalAsync(Peer peer, byte kind, string body)
        {
            if (peer == null || peer.Address == null || !peer.Paired) return;
            body = body ?? "";
            if (body.Length > 2000) body = body.Substring(0, 2000);
            SecureConnection connection;
            try { connection = await OpenAsync(peer, 4000).ConfigureAwait(false); }
            catch { return; }
            using (connection)
            {
                BinaryWriter writer = new BinaryWriter(connection.Stream, Encoding.UTF8, true);
                writer.Write((byte)'D');
                WriteText(writer, Label(), 80);
                writer.Write(kind);
                WriteText(writer, body, 4000);
                writer.Flush();
                await ReadByteAsync(connection.Stream, 4000).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Como <see cref="SendSignalAsync"/> pero avisa si no se entregó (lanza): los mensajes de grupo y los
        /// borrados no deben perderse en silencio. No exige haber hablado antes con el equipo.
        /// </summary>
        public async Task SendReliableSignalAsync(Peer peer, byte kind, string body)
        {
            if (peer == null || peer.Address == null) throw new IOException("Equipo desconectado.");
            body = body ?? "";
            if (body.Length > 3900) body = body.Substring(0, 3900);
            using (SecureConnection connection = await OpenAsync(peer, 4000).ConfigureAwait(false))
            {
                BinaryWriter writer = new BinaryWriter(connection.Stream, Encoding.UTF8, true);
                writer.Write((byte)'D');
                WriteText(writer, Label(), 80);
                writer.Write(kind);
                WriteText(writer, body, 4000);
                writer.Flush();
                if (await ReadByteAsync(connection.Stream, 5000).ConfigureAwait(false) != 1) throw new IOException("No se entregó.");
            }
        }

        private void ReadSignal(BinaryReader reader, BinaryWriter writer, Guid id)
        {
            string name = CleanLabel(ReadText(reader, 80));
            byte kind = reader.ReadByte();
            string body = ReadText(reader, 4000);
            if (name.Length == 0) return;
            writer.Write((byte)1);
            writer.Flush();
            if (kind == ChatKindText)
            {
                Action<Guid, string, string> text = ChatReceived;
                if (text != null && body.Trim().Length > 0) text(id, name, body.Trim());
                return;
            }
            Action<Guid, string, byte, string> signal = ChatSignal;
            if (signal != null) signal(id, name, kind, body ?? "");
        }

        // ── Pantalla compartida ──────────────────────────────────────────

        /// <summary>
        /// Invita a un compañero a ver la pantalla. Devuelve la conexión abierta si acepta;
        /// a partir de ahí el emisor escribe fotogramas en ella.
        /// </summary>
        public async Task<SecureConnection> OpenScreenAsync(Peer peer, string source)
        {
            SecureConnection connection = await OpenAsync(peer).ConfigureAwait(false);
            try
            {
                connection.Client.NoDelay = true;
                connection.Client.SendBufferSize = 1 << 20;
                connection.Client.SendTimeout = 6000;
                connection.Client.ReceiveTimeout = 0;
                BinaryWriter writer = new BinaryWriter(connection.Stream, Encoding.UTF8, true);
                writer.Write((byte)'S');
                WriteText(writer, Label(), 80);
                WriteText(writer, CleanLabel(source ?? "", 400), 1600);
                writer.Flush();
                if (await ReadByteAsync(connection.Stream, 65000).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException(peer.Name + " no aceptó ver la pantalla.");
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private async Task ReadScreen(TcpClient client, SecureStream stream, BinaryReader reader, BinaryWriter writer, IPAddress address, Guid id)
        {
            string name = CleanLabel(ReadText(reader, 80));
            string source = CleanLabel(ReadText(reader, 1600), 400);
            if (name.Length == 0) return;
            Func<ScreenInvite, Task<IScreenSink>> handler = ScreenOffered;
            IScreenSink viewer = handler == null ? null :
                await handler(new ScreenInvite { Id = id, Sender = name, Source = source, Address = address }).ConfigureAwait(false);
            if (viewer == null) { writer.Write((byte)0); writer.Flush(); return; }
            writer.Write((byte)1);
            writer.Flush();
            client.NoDelay = true;
            client.ReceiveBufferSize = 1 << 20;
            client.ReceiveTimeout = 15000;
            viewer.Run(stream);
        }

        public event Func<ScreenInvite, Task<IScreenSink>> ScreenOffered;

        /// <summary>
        /// Alguien pide unirse a la pantalla que comparto: identidad comprobada y nombre. Devuelve si la solicitud
        /// se atiende (hay transmisión en curso); quien comparte decide después y, si acepta, le envía la invitación.
        /// </summary>
        public event Func<Guid, string, bool> JoinRequested;

        /// <summary>
        /// Pide a un compañero que está compartiendo su pantalla que nos deje verla. Devuelve false si ya no comparte.
        /// Si acepta, llega una invitación normal de pantalla compartida.
        /// </summary>
        public async Task<bool> RequestJoinAsync(Peer peer)
        {
            using (SecureConnection connection = await OpenAsync(peer).ConfigureAwait(false))
            {
                BinaryWriter writer = new BinaryWriter(connection.Stream, Encoding.UTF8, true);
                writer.Write((byte)'J');
                WriteText(writer, Label(), 80);
                writer.Flush();
                try { return await ReadByteAsync(connection.Stream, 7000).ConfigureAwait(false) == 1; }
                catch (EndOfStreamException)
                {
                    throw new InvalidOperationException(peer.Name + " tiene una versión de Lazo que no admite solicitudes para unirse.");
                }
            }
        }

        /// <summary>Alguien me pide que comparta mi pantalla: identidad comprobada y nombre. Devuelve si se atiende.</summary>
        public event Func<Guid, string, bool> ShareRequested;

        /// <summary>
        /// Pide a un compañero que comparta su pantalla. Si acepta, elige qué compartir y llega una invitación normal.
        /// </summary>
        public async Task<bool> RequestShareAsync(Peer peer)
        {
            using (SecureConnection connection = await OpenAsync(peer).ConfigureAwait(false))
            {
                BinaryWriter writer = new BinaryWriter(connection.Stream, Encoding.UTF8, true);
                writer.Write((byte)'Q');
                WriteText(writer, Label(), 80);
                writer.Flush();
                try { return await ReadByteAsync(connection.Stream, 7000).ConfigureAwait(false) == 1; }
                catch (EndOfStreamException)
                {
                    throw new InvalidOperationException(peer.Name + " tiene una versión de Lazo que no admite pedirle que comparta.");
                }
            }
        }

        private void ReadShareRequest(BinaryReader reader, BinaryWriter writer, Guid id)
        {
            string name = CleanLabel(ReadText(reader, 80));
            if (name.Length == 0) return;
            Func<Guid, string, bool> handler = ShareRequested;
            bool taken = handler != null && handler(id, name);
            writer.Write((byte)(taken ? 1 : 0));
            writer.Flush();
        }

        // ── Control remoto con contraseña (docs/control-remoto.md) ───────────

        /// <summary>
        /// Anfitrión: alguien entró con la contraseña correcta. Recibe el identificador y el nombre del visor y la conexión
        /// ya autenticada, que desde ahora es la transmisión de pantalla (este equipo es el emisor). La tarea que devuelve
        /// debe terminar cuando el visor se vaya o la transmisión acabe: hasta entonces la conexión sigue abierta.
        /// </summary>
        public event Func<Guid, string, SecureConnection, Task> RemoteAccessAccepted;

        /// <summary>Anfitrión: devuelve true si ya hay otra sesión (de pantalla o remota) y no se puede aceptar una nueva.</summary>
        public Func<bool> RemoteBusy;

        private async Task ReadRemoteAccess(TcpClient client, SecureStream stream, BinaryReader reader, BinaryWriter writer, Guid peerId)
        {
            string name = CleanLabel(ReadText(reader, 80));
            if (name.Length == 0) return;
            string viewerId = peerId.ToString("D").ToLowerInvariant();
            Func<bool> busy = RemoteBusy;
            Func<Guid, string, SecureConnection, Task> handler = RemoteAccessAccepted;
            // Desactivado, bloqueado u ocupado: se cierra sin dar pistas de la contraseña.
            if (handler == null || !Remote.Enabled || Remote.IsLocked(viewerId) || (busy != null && busy()))
            {
                writer.Write((byte)0);
                writer.Flush();
                return;
            }
            RemoteChallenge challenge = Remote.NewChallenge();
            writer.Write((byte)1);
            writer.Write(challenge.Salt);
            writer.Write(challenge.Iterations);
            writer.Write(challenge.Nonce);
            writer.Flush();
            byte[] proof = reader.ReadBytes(RemoteAccess.KeyLength);
            if (proof.Length != RemoteAccess.KeyLength) throw new EndOfStreamException();
            if (!Remote.Verify(challenge, proof, viewerId, _id.ToString("D").ToLowerInvariant()))
            {
                Remote.RegisterFailure(viewerId);
                await Task.Delay(1000).ConfigureAwait(false);
                writer.Write((byte)0);
                writer.Flush();
                return;
            }
            Remote.RegisterSuccess(viewerId);
            writer.Write((byte)1);
            writer.Flush();
            // A partir de aquí la conexión es la transmisión: este equipo emite y el visor mira, como al aceptar una invitación.
            client.NoDelay = true;
            client.SendBufferSize = 1 << 20;
            client.SendTimeout = 6000;
            client.ReceiveTimeout = 0;
            SecureConnection connection = new SecureConnection { Client = client, Stream = stream, PeerId = peerId.ToString("D") };
            await handler(peerId, name, connection).ConfigureAwait(false);
        }

        /// <summary>
        /// Visor: entra al equipo de <paramref name="peer"/> con su contraseña de control remoto. Devuelve la conexión ya
        /// autenticada, por la que llega la transmisión de pantalla (se usa como la de una invitación aceptada); si algo
        /// falla lanza una excepción con un mensaje para la persona.
        /// </summary>
        public async Task<SecureConnection> ConnectRemoteAsync(Peer peer, string password)
        {
            if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("Escribe la contraseña de control remoto.");
            SecureConnection connection = await OpenAsync(peer).ConfigureAwait(false);
            try
            {
                connection.Client.ReceiveTimeout = 20000;
                BinaryWriter writer = new BinaryWriter(connection.Stream, Encoding.UTF8, true);
                BinaryReader reader = new BinaryReader(connection.Stream, Encoding.UTF8, true);
                writer.Write((byte)'W');
                WriteText(writer, Label(), 80);
                writer.Flush();
                byte status;
                try { status = await ReadByteAsync(connection.Stream, 10000).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    // Las versiones sin control remoto no conocen la operación y cortan la conexión.
                    if (ex is EndOfStreamException || ex is IOException) throw new InvalidOperationException(peer.Name + " tiene una versión de Lazo sin control remoto.");
                    throw;
                }
                if (status != 1) throw new InvalidOperationException("El control remoto de " + peer.Name + " está desactivado, bloqueado u ocupado.");
                byte[] salt = reader.ReadBytes(RemoteAccess.SaltLength);
                int iterations = reader.ReadInt32();
                byte[] nonce = reader.ReadBytes(RemoteAccess.NonceLength);
                if (salt.Length != RemoteAccess.SaltLength || nonce.Length != RemoteAccess.NonceLength) throw new EndOfStreamException();
                if (iterations < 50000 || iterations > 1000000) throw new InvalidDataException("El otro equipo pidió una derivación de contraseña no válida.");
                string viewerId = _id.ToString("D").ToLowerInvariant();
                string hostId = peer.Id.ToString("D").ToLowerInvariant();
                byte[] proof = await Task.Run(() => RemoteAccess.ComputeProof(password, salt, iterations, nonce, viewerId, hostId)).ConfigureAwait(false);
                writer.Write(proof);
                writer.Flush();
                if (await ReadByteAsync(connection.Stream, 15000).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("Contraseña incorrecta.");
                // Como un visor de pantalla compartida.
                connection.Client.NoDelay = true;
                connection.Client.ReceiveBufferSize = 1 << 20;
                connection.Client.ReceiveTimeout = 15000;
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private void ReadJoin(BinaryReader reader, BinaryWriter writer, Guid id)
        {
            string name = CleanLabel(ReadText(reader, 80));
            if (name.Length == 0) return;
            Func<Guid, string, bool> handler = JoinRequested;
            bool taken = handler != null && handler(id, name);
            writer.Write((byte)(taken ? 1 : 0));
            writer.Flush();
        }

        // ── Archivos ─────────────────────────────────────────────────────

        private static readonly SemaphoreSlim _sendSlots = new SemaphoreSlim(8);

        public async Task SendAsync(Peer peer, string path, Action<double> progress)
        {
            await _sendSlots.WaitAsync();
            try { await SendOneAsync(peer, path, progress); }
            finally { _sendSlots.Release(); }
        }

        private async Task SendOneAsync(Peer peer, string path, Action<double> progress)
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("El archivo ya no existe.");
            if (info.Length > MaxFileSize) throw new InvalidOperationException("Límite: 20 GB por archivo.");
            using (SecureConnection connection = await OpenAsync(peer).ConfigureAwait(false))
            {
                connection.Client.ReceiveTimeout = 120000;
                connection.Client.SendTimeout = 30000;
                SecureStream stream = connection.Stream;
                BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true);
                byte[] preview = await Task.Run(() => ImagePreview(path)).ConfigureAwait(false);
                writer.Write((byte)'F');
                WriteText(writer, Label(), 80);
                WriteText(writer, info.Name, 255 * 4);
                writer.Write(info.Length);
                writer.Write(preview == null ? 0 : preview.Length);
                if (preview != null && preview.Length > 0) writer.Write(preview);
                writer.Flush();
                byte reply = await ReadByteAsync(stream, 120000).ConfigureAwait(false);
                if (reply != 1) throw new InvalidOperationException("El destinatario rechazó la transferencia o está ocupado.");
                byte[] buffer = new byte[SecureStream.MaxPlain];
                long sent = 0;
                byte[] hash;
                using (FileStream file = info.OpenRead())
                using (SHA256 sha = SHA256.Create())
                {
                    int count;
                    while ((count = await file.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    {
                        await Task.Run(() => stream.Write(buffer, 0, count)).ConfigureAwait(false);
                        sha.TransformBlock(buffer, 0, count, null, 0);
                        sent += count;
                        if (progress != null) progress(info.Length == 0 ? 1 : (double)sent / info.Length);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    hash = sha.Hash;
                }
                stream.Write(hash, 0, hash.Length);
                stream.Flush();
                if (await ReadByteAsync(stream, 120000).ConfigureAwait(false) != 1) throw new IOException("El destinatario no pudo guardar el archivo.");
            }
        }

        private async Task ReceiveFile(TcpClient client, SecureStream stream, BinaryReader reader, BinaryWriter writer, IPAddress address, Guid peerId)
        {
            Guid offerId = Guid.Empty;
            string temp = null;
            bool accepted = false;
            bool ownsReceive = false;
            try
            {
                string sender = CleanLabel(ReadText(reader, 80));
                string name = SafeFileName(ReadText(reader, 255 * 4));
                long size = reader.ReadInt64();
                int previewLength = reader.ReadInt32();
                if (sender.Length == 0 || name.Length == 0 || size < 0 || size > MaxFileSize ||
                    previewLength < 0 || previewLength > 48000) return;
                byte[] preview = previewLength == 0 ? null : reader.ReadBytes(previewLength);
                if (preview != null && preview.Length != previewLength) return;
                offerId = Guid.NewGuid();
                if (Interlocked.Increment(ref _activeReceives) > 8 || !HasRoom(_receiveDirectory, size))
                {
                    Interlocked.Decrement(ref _activeReceives);
                    writer.Write((byte)0); writer.Flush(); return;
                }
                ownsReceive = true;
                Offer offer = new Offer { Id = offerId, PeerId = peerId, Sender = sender, Address = address, FileName = name, Size = size, Preview = preview };
                Func<Offer, Task<bool>> handler = OfferReceived;
                bool allow = handler != null && await handler(offer);
                if (!allow) { writer.Write((byte)0); writer.Flush(); return; }
                accepted = true;
                writer.Write((byte)1); writer.Flush();
                client.ReceiveTimeout = 30000;
                string folder = _receiveDirectory;
                Directory.CreateDirectory(folder);
                temp = Path.Combine(folder, "." + offerId.ToString("N") + ".part");
                byte[] buffer = new byte[SecureStream.MaxPlain];
                long received = 0;
                byte[] actual;
                using (FileStream file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (SHA256 sha = SHA256.Create())
                {
                    while (received < size)
                    {
                        int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, size - received));
                        if (count <= 0) throw new EndOfStreamException();
                        file.Write(buffer, 0, count);
                        sha.TransformBlock(buffer, 0, count, null, 0);
                        received += count;
                        Action<Guid, double> progress = ReceiveProgress;
                        if (progress != null) progress(offerId, size == 0 ? 1 : (double)received / size);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    actual = sha.Hash;
                }
                byte[] expected = reader.ReadBytes(32);
                if (expected.Length != 32 || !LazoCrypto.FixedEquals(actual, expected)) throw new InvalidDataException("El archivo no superó la verificación de integridad.");
                string destination = UniqueDestination(folder, name);
                File.Move(temp, destination);
                temp = null;
                MarkOfTheWeb.Apply(destination);
                writer.Write((byte)1); writer.Flush();
                Action<Guid, string, string> finished = ReceiveFinished;
                if (finished != null) finished(offerId, "Archivo recibido", destination);
            }
            catch (Exception ex)
            {
                if (accepted)
                {
                    Action<Guid, string, string> finished = ReceiveFinished;
                    if (finished != null) finished(offerId, "Error: " + ex.Message, null);
                }
            }
            finally
            {
                if (temp != null) try { File.Delete(temp); } catch { }
                if (ownsReceive) Interlocked.Decrement(ref _activeReceives);
            }
        }

        /// <summary>Hay espacio para el archivo y un margen de 256 MB.</summary>
        private static bool HasRoom(string folder, long size)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(folder));
                return new DriveInfo(root).AvailableFreeSpace > size + 256L * 1024 * 1024;
            }
            catch { return true; }
        }

        private static async Task<byte> ReadByteAsync(Stream stream, int timeoutMs)
        {
            Task<int> read = Task.Run(() => stream.ReadByte());
            if (await Task.WhenAny(read, Task.Delay(timeoutMs)).ConfigureAwait(false) != read)
                throw new TimeoutException("El destinatario no respondió.");
            if (read.Result < 0) throw new EndOfStreamException();
            return (byte)read.Result;
        }

        private static Guid LoadId(string folder)
        {
            try
            {
                string path = Path.Combine(folder, "device.id");
                Guid parsed;
                if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out parsed)) return parsed;
                parsed = Guid.NewGuid();
                Directory.CreateDirectory(folder);
                File.WriteAllText(path, parsed.ToString("D"));
                return parsed;
            }
            catch { return Guid.NewGuid(); }
        }

        private static byte[] ImagePreview(string path)
        {
            try
            {
                string ext = Path.GetExtension(path);
                if (string.IsNullOrEmpty(ext)) return null;
                ext = ext.ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".bmp" && ext != ".gif" &&
                    ext != ".tif" && ext != ".tiff") return null;
                if (new FileInfo(path).Length > 25L * 1024 * 1024) return null;
                using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (System.Drawing.Image source = System.Drawing.Image.FromStream(input, false, false))
                {
                    int edge = 160;
                    int width = source.Width;
                    int height = source.Height;
                    if (width <= 0 || height <= 0) return null;
                    if (width > edge || height > edge)
                    {
                        double scale = Math.Min((double)edge / width, (double)edge / height);
                        width = Math.Max(1, (int)Math.Round(width * scale));
                        height = Math.Max(1, (int)Math.Round(height * scale));
                    }
                    using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(width, height))
                    using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
                    using (MemoryStream output = new MemoryStream())
                    {
                        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                        graphics.Clear(System.Drawing.Color.White);
                        graphics.DrawImage(source, 0, 0, width, height);
                        System.Drawing.Imaging.ImageCodecInfo codec = null;
                        System.Drawing.Imaging.ImageCodecInfo[] codecs = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders();
                        for (int i = 0; i < codecs.Length; i++)
                            if (codecs[i].MimeType == "image/jpeg") codec = codecs[i];
                        if (codec == null) bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Jpeg);
                        else
                        {
                            using (System.Drawing.Imaging.EncoderParameters parameters = new System.Drawing.Imaging.EncoderParameters(1))
                            {
                                parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                                    System.Drawing.Imaging.Encoder.Quality, 68L);
                                bitmap.Save(output, codec, parameters);
                            }
                        }
                        if (output.Length == 0 || output.Length > 48000) return null;
                        return output.ToArray();
                    }
                }
            }
            catch { return null; }
        }

        private static string ReadText(BinaryReader reader, int maxBytes)
        {
            int length = reader.ReadUInt16();
            if (length > maxBytes) throw new InvalidDataException("Campo demasiado largo.");
            byte[] value = reader.ReadBytes(length);
            if (value.Length != length) throw new EndOfStreamException();
            return new UTF8Encoding(false, true).GetString(value);
        }

        private static void WriteText(BinaryWriter writer, string value, int maxBytes)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
            if (bytes.Length > maxBytes) throw new InvalidOperationException("Texto demasiado largo.");
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static string Label()
        {
            string name = Identity.Current;
            if (string.IsNullOrWhiteSpace(name)) name = Environment.UserName;
            if (string.IsNullOrWhiteSpace(name)) name = "Lazo";
            return CleanLabel(name);
        }

        private static string CleanLabel(string value)
        {
            return CleanLabel(value, 60);
        }

        private static string CleanLabel(string value, int max)
        {
            return new string(value.Where(c => !char.IsControl(c) && c != '|' && !IsBidiControl(c)).Take(max).ToArray()).Trim();
        }

        /// <summary>Caracteres que invierten el sentido del texto y permiten disfrazar nombres («gpj.exe»).</summary>
        private static bool IsBidiControl(char c)
        {
            return (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩') || c == '‎' || c == '‏' || c == '؜';
        }

        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        internal static string SafeFileName(string name)
        {
            if (name == null) return "";
            name = name.TrimEnd(' ', '.');
            if (name.Length == 0 || name.Length > 200 || name != Path.GetFileName(name) || name == "." || name == "..") return "";
            if (name.Any(c => char.IsControl(c) || IsBidiControl(c) || c == ':' || Path.GetInvalidFileNameChars().Contains(c))) return "";
            string stem = name.Split('.')[0].Trim().ToUpperInvariant();
            if (ReservedNames.Contains(stem)) return "";
            if (name.StartsWith(".") && name.EndsWith(".part")) return "";
            return name;
        }

        private static string UniqueDestination(string folder, string name)
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            string result = Path.Combine(folder, name);
            for (int i = 1; File.Exists(result); i++) result = Path.Combine(folder, stem + " (" + i + ")" + ext);
            return result;
        }

        /// <summary>Carpeta donde se guarda lo recibido. Se elige en Ajustes.</summary>
        public string ReceiveDirectory { get { return _receiveDirectory; } }

        public bool UsesDefaultReceiveDirectory
        {
            get { return string.Equals(Path.GetFullPath(_receiveDirectory).TrimEnd('\\'), Path.GetFullPath(DefaultReceiveDirectory()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>Cambia la carpeta de recepción (null: la predeterminada, Descargas\Lazo). Lo que ya llega sigue donde iba.</summary>
        public void SetReceiveDirectory(string folder)
        {
            string path = Path.Combine(_profile, "receive-folder.txt");
            if (string.IsNullOrWhiteSpace(folder))
            {
                _receiveDirectory = DefaultReceiveDirectory();
                try { File.Delete(path); } catch { }
                return;
            }
            folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(folder);
            _receiveDirectory = folder;
            Directory.CreateDirectory(_profile);
            File.WriteAllText(path, folder, Encoding.UTF8);
        }

        private string SavedReceiveDirectory()
        {
            try
            {
                string path = Path.Combine(_profile, "receive-folder.txt");
                if (!File.Exists(path)) return null;
                string folder = File.ReadAllText(path, Encoding.UTF8).Trim();
                return folder.Length > 0 && Path.IsPathRooted(folder) ? folder : null;
            }
            catch { return null; }
        }

        internal static string DefaultReceiveDirectory()
        {
            return Path.Combine(GetDownloadsPath(), "Lazo");
        }

        private static string GetDownloadsPath()
        {
            IntPtr pointer = IntPtr.Zero;
            try
            {
                Guid id = DownloadsId;
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out pointer) == 0 && pointer != IntPtr.Zero)
                    return Marshal.PtrToStringUni(pointer);
            }
            catch { }
            finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        public void Dispose()
        {
            _running = false;
            if (_udp != null) _udp.Close();
            if (_listener != null) _listener.Stop();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
    }

    /// <summary>
    /// Marca lo recibido como descargado de otro equipo (Zone.Identifier, zona Internet), igual que un navegador:
    /// SmartScreen y la Vista protegida de Office lo revisan antes de ejecutarlo.
    /// </summary>
    internal static class MarkOfTheWeb
    {
        public static void Apply(string path)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            try
            {
                IntPtr handle = CreateFileW(path + ":Zone.Identifier", 0x40000000, 0, IntPtr.Zero, 2, 0x80, IntPtr.Zero);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
                try
                {
                    byte[] data = Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet\r\n");
                    uint written;
                    WriteFile(handle, data, (uint)data.Length, out written, IntPtr.Zero);
                }
                finally { CloseHandle(handle); }
            }
            catch { }
        }

        /// <summary>Extensiones que ejecutan código al abrirse: se piden confirmación y nunca se abren con un clic.</summary>
        public static bool IsRisky(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".exe": case ".com": case ".scr": case ".pif": case ".bat": case ".cmd": case ".ps1": case ".psm1":
                case ".vbs": case ".vbe": case ".js": case ".jse": case ".wsf": case ".wsh": case ".hta": case ".msi":
                case ".msp": case ".lnk": case ".url": case ".reg": case ".cpl": case ".jar": case ".appref-ms":
                case ".application": case ".msc": case ".scf": case ".inf": case ".iso": case ".img": case ".vhd": case ".vhdx":
                case ".docm": case ".xlsm": case ".pptm": case ".chm": case ".settingcontent-ms": case ".library-ms":
                    return true;
            }
            return false;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr handle, byte[] buffer, uint count, out uint written, IntPtr overlapped);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
