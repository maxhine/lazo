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
        public override string ToString() { return Name + "  /  " + Address; }
    }

    internal sealed class Offer
    {
        public Guid Id;
        public string Sender;
        public IPAddress Address;
        public string FileName;
        public long Size;
        public byte[] Preview;
    }

    internal sealed class NetworkEngine : IDisposable
    {
        public const int DiscoveryPort = 48351;
        public const int TransferPort = 48352;
        private const long MaxFileSize = 20L * 1024 * 1024 * 1024;
        private static readonly Guid DownloadsId = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        private readonly Guid _id = LoadId();
        public Guid SelfId { get { return _id; } }
        public event Action<Guid, string, string> ChatReceived;
        public event Action<Guid, string, byte, string> ChatSignal;
        public const byte ChatKindText = 1;
        public const byte ChatKindTyping = 2;
        public const byte ChatKindPresence = 3;
        public const byte ChatKindNudge = 4;
        private readonly string _receiveDirectory;
        private readonly int _discoveryPort;
        private readonly int _transferPort;
        private readonly object _peerLock = new object();
        private readonly Dictionary<Guid, Peer> _peers = new Dictionary<Guid, Peer>();
        private UdpClient _udp;
        private TcpListener _listener;
        private volatile bool _running;
        private int _activeReceives;

        public event Action<List<Peer>> PeersChanged;
        public event Func<Offer, Task<bool>> OfferReceived;
        public event Action<Guid, double> ReceiveProgress;
        public event Action<Guid, string, string> ReceiveFinished;

        public NetworkEngine(string receiveDirectory = null, int discoveryPort = DiscoveryPort, int transferPort = TransferPort)
        {
            _receiveDirectory = receiveDirectory ?? Path.Combine(GetDownloadsPath(), "Lazo");
            _discoveryPort = discoveryPort;
            _transferPort = transferPort;
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
            Task.Run((Action)ListenDiscovery);
            Task.Run((Action)BroadcastLoop);
            Task.Run((Action)ListenTransfers);
        }

        private void ListenDiscovery()
        {
            while (_running)
            {
                try
                {
                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _udp.Receive(ref remote);
                    if (data.Length > 16500 || !IsLocalSubnet(remote.Address)) continue;
                    string[] fields = Encoding.UTF8.GetString(data).Split('|');
                    Guid id;
                    int port;
                    if (fields.Length == 3 && fields[0] == "LAZOA" && Guid.TryParse(fields[1], out id))
                    {
                        if (fields[2].Length > 16000) continue;
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
                    if (data.Length > 512 || fields.Length != 4 || fields[0] != "LAZO1" ||
                        !Guid.TryParse(fields[1], out id) || id == _id ||
                        !int.TryParse(fields[3], out port) || port != _transferPort) continue;
                    string name = CleanLabel(fields[2]);
                    if (name.Length == 0) continue;
                    lock (_peerLock)
                    {
                        Peer previous;
                        bool changed = !_peers.TryGetValue(id, out previous) || previous.Name != name ||
                                       !previous.Address.Equals(remote.Address) || previous.Port != port;
                        _peers[id] = new Peer { Id = id, Name = name, Address = remote.Address, Port = port, SeenUtc = DateTime.UtcNow, Photo = previous == null ? "" : previous.Photo };
                        if (changed) PublishPeers();
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { if (!_running) return; }
                catch { /* Invalid LAN packet: ignore it. */ }
            }
        }

        private void BroadcastLoop()
        {
            while (_running)
            {
                try
                {
                    byte[] payload = Encoding.UTF8.GetBytes("LAZO1|" + _id + "|" + Label() + "|" + _transferPort);
                    byte[] photo = Encoding.UTF8.GetBytes("LAZOA|" + _id + "|" + ProfilePhoto.Current);
                    foreach (IPAddress address in BroadcastAddresses())
                    {
                        _udp.Send(payload, payload.Length, new IPEndPoint(address, _discoveryPort));
                        _udp.Send(photo, photo.Length, new IPEndPoint(address, _discoveryPort));
                    }
                    lock (_peerLock)
                    {
                        Guid[] stale = _peers.Where(p => (DateTime.UtcNow - p.Value.SeenUtc).TotalSeconds > 9)
                            .Select(p => p.Key).ToArray();
                        foreach (Guid key in stale) _peers.Remove(key);
                        if (stale.Length > 0) PublishPeers();
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { /* Firewall or disconnected adapter. Retry. */ }
                for (int i = 0; i < 25 && _running; i++) Thread.Sleep(100);
            }
        }

        private void PublishPeers()
        {
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

        private void ListenTransfers()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    Task.Run(() => HandleTransfer(client));
                }
                catch (SocketException) { if (!_running) return; }
                catch (ObjectDisposedException) { return; }
            }
        }

        private async Task HandleTransfer(TcpClient client)
        {
            Guid offerId = Guid.Empty;
            string temp = null;
            bool accepted = false;
            bool ownsReceive = false;
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 120000;
                    client.SendTimeout = 30000;
                    IPAddress address = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                    if (!IsLocalSubnet(address)) return;
                    using (NetworkStream stream = client.GetStream())
                    using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
                    using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
                    {
                        byte[] magic = reader.ReadBytes(5);
                        string magicText = Encoding.ASCII.GetString(magic);
                        if (magicText == "LAZOD")
                        {
                            ReadSignal(reader, writer);
                            return;
                        }
                        if (magicText == "LAZOC")
                        {
                            ReadChat(reader, writer);
                            return;
                        }
                        if (magicText != "LAZO2")
                        {
                            if (magicText == "LAZO1") { writer.Write((byte)0); writer.Flush(); }
                            return;
                        }
                        string sender = CleanLabel(ReadText(reader, 80));
                        string name = SafeFileName(ReadText(reader, 255));
                        long size = reader.ReadInt64();
                        int previewLength = reader.ReadInt32();
                        if (sender.Length == 0 || name.Length == 0 || size < 0 || size > MaxFileSize ||
                            previewLength < 0 || previewLength > 48000) return;
                        byte[] preview = previewLength == 0 ? null : reader.ReadBytes(previewLength);
                        if (preview != null && preview.Length != previewLength) return;
                        offerId = Guid.NewGuid();
                        if (Interlocked.Increment(ref _activeReceives) > 8)
                        {
                            Interlocked.Decrement(ref _activeReceives);
                            writer.Write((byte)0); writer.Flush(); return;
                        }
                        ownsReceive = true;
                        Offer offer = new Offer { Id = offerId, Sender = sender, Address = address, FileName = name, Size = size, Preview = preview };
                        Func<Offer, Task<bool>> handler = OfferReceived;
                        bool allow = handler != null && await handler(offer);
                        if (!allow) { writer.Write((byte)0); writer.Flush(); return; }
                        accepted = true;
                        writer.Write((byte)1); writer.Flush();
                        client.ReceiveTimeout = 30000;
                        string folder = _receiveDirectory;
                        Directory.CreateDirectory(folder);
                        temp = Path.Combine(folder, "." + offerId.ToString("N") + ".part");
                        byte[] buffer = new byte[64 * 1024];
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
                        if (expected.Length != 32 || !actual.SequenceEqual(expected)) throw new InvalidDataException("El archivo no superó la verificación de integridad.");
                        string destination = UniqueDestination(folder, name);
                        File.Move(temp, destination);
                        temp = null;
                        writer.Write((byte)1); writer.Flush();
                        Action<Guid, string, string> finished = ReceiveFinished;
                        if (finished != null) finished(offerId, "Archivo recibido", destination);
                    }
                }
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

        private static readonly System.Threading.SemaphoreSlim _sendSlots = new System.Threading.SemaphoreSlim(8);

        public List<Peer> Snapshot()
        {
            lock (_peerLock) return _peers.Values.OrderBy(item => item.Name).ToList();
        }

        public async Task SendChatAsync(Peer peer, string text)
        {
            if (peer == null || peer.Address == null) throw new InvalidOperationException("El compañero no está conectado.");
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > 2000) text = text.Substring(0, 2000);
            if (!IsLocalSubnet(peer.Address)) throw new InvalidOperationException("El compañero ya no está en la misma subred.");
            using (TcpClient client = new TcpClient(AddressFamily.InterNetwork))
            {
                Task connect = client.ConnectAsync(peer.Address, peer.Port);
                if (await Task.WhenAny(connect, Task.Delay(7000)).ConfigureAwait(false) != connect)
                    throw new TimeoutException("El compañero no respondió.");
                await connect.ConfigureAwait(false);
                using (NetworkStream stream = client.GetStream())
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
                using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(Encoding.ASCII.GetBytes("LAZOC"));
                    WriteText(writer, _id.ToString("D"), 40);
                    WriteText(writer, Label());
                    WriteText(writer, text, 4000);
                    writer.Flush();
                    if (await ReadByteAsync(stream, 7000).ConfigureAwait(false) != 1) throw new IOException("No se pudo entregar el mensaje.");
                }
            }
        }

        private void ReadChat(BinaryReader reader, BinaryWriter writer)
        {
            Guid id;
            if (!Guid.TryParse(ReadText(reader, 40), out id)) return;
            string name = CleanLabel(ReadText(reader, 80));
            string body = ReadText(reader, 4000).Trim();
            if (name.Length == 0 || body.Length == 0 || body.Length > 2000) return;
            writer.Write((byte)1);
            writer.Flush();
            Action<Guid, string, string> handler = ChatReceived;
            if (handler != null) handler(id, name, body);
        }

        public async Task SendSignalAsync(Peer peer, byte kind, string body)
        {
            if (peer == null || peer.Address == null) return;
            body = body ?? "";
            if (body.Length > 2000) body = body.Substring(0, 2000);
            if (!IsLocalSubnet(peer.Address)) return;
            using (TcpClient client = new TcpClient(AddressFamily.InterNetwork))
            {
                Task connect = client.ConnectAsync(peer.Address, peer.Port);
                if (await Task.WhenAny(connect, Task.Delay(4000)).ConfigureAwait(false) != connect) return;
                await connect.ConfigureAwait(false);
                using (NetworkStream stream = client.GetStream())
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
                using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(Encoding.ASCII.GetBytes("LAZOD"));
                    WriteText(writer, _id.ToString("D"), 40);
                    WriteText(writer, Label());
                    writer.Write(kind);
                    WriteText(writer, body, 4000);
                    writer.Flush();
                    await ReadByteAsync(stream, 4000).ConfigureAwait(false);
                }
            }
        }

        private void ReadSignal(BinaryReader reader, BinaryWriter writer)
        {
            Guid id;
            if (!Guid.TryParse(ReadText(reader, 40), out id)) return;
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

        private static async Task<byte> ReadByteAsync(Stream stream, int timeoutMs)
        {
            byte[] one = new byte[1];
            Task<int> read = stream.ReadAsync(one, 0, 1);
            if (await Task.WhenAny(read, Task.Delay(timeoutMs)).ConfigureAwait(false) != read)
                throw new TimeoutException("El destinatario no respondió.");
            if (read.Result != 1) throw new EndOfStreamException();
            return one[0];
        }

        private static Guid LoadId()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "device.id");
                Guid parsed;
                if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out parsed)) return parsed;
                parsed = Guid.NewGuid();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, parsed.ToString("D"));
                return parsed;
            }
            catch { return Guid.NewGuid(); }
        }

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
            if (!IsLocalSubnet(peer.Address)) throw new InvalidOperationException("El equipo ya no está en la misma subred.");
            using (TcpClient client = new TcpClient(AddressFamily.InterNetwork))
            {
                Task connect = client.ConnectAsync(peer.Address, peer.Port);
                if (await Task.WhenAny(connect, Task.Delay(7000)).ConfigureAwait(false) != connect)
                    throw new TimeoutException("El equipo no respondió.");
                await connect.ConfigureAwait(false);
                client.ReceiveTimeout = 120000;
                client.SendTimeout = 30000;
                using (NetworkStream stream = client.GetStream())
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
                using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    byte[] preview = await Task.Run(() => ImagePreview(path)).ConfigureAwait(false);
                    writer.Write(Encoding.ASCII.GetBytes("LAZO2"));
                    WriteText(writer, Label());
                    WriteText(writer, info.Name);
                    writer.Write(info.Length);
                    writer.Write(preview == null ? 0 : preview.Length);
                    if (preview != null && preview.Length > 0) writer.Write(preview);
                    writer.Flush();
                    byte reply = await ReadByteAsync(stream, 120000).ConfigureAwait(false);
                    if (reply != 1) throw new InvalidOperationException("El destinatario rechazó la transferencia o está ocupado.");
                    byte[] buffer = new byte[64 * 1024];
                    long sent = 0;
                    byte[] hash;
                    using (FileStream file = info.OpenRead())
                    using (SHA256 sha = SHA256.Create())
                    {
                        int count;
                        while ((count = await file.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                        {
                            await stream.WriteAsync(buffer, 0, count).ConfigureAwait(false);
                            sha.TransformBlock(buffer, 0, count, null, 0);
                            sent += count;
                            if (progress != null) progress(info.Length == 0 ? 1 : (double)sent / info.Length);
                        }
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        hash = sha.Hash;
                    }
                    await stream.WriteAsync(hash, 0, hash.Length).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    if (await ReadByteAsync(stream, 120000).ConfigureAwait(false) != 1) throw new IOException("El destinatario no pudo guardar el archivo.");
                }
            }
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

        private static void WriteText(BinaryWriter writer, string value)
        {
            WriteText(writer, value, 255);
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
            return new string(value.Where(c => !char.IsControl(c) && c != '|').Take(60).ToArray()).Trim();
        }

        private static string SafeFileName(string name)
        {
            if (name != Path.GetFileName(name) || name == "." || name == ".." || name.Length == 0) return "";
            if (name.Any(c => char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c))) return "";
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
}
