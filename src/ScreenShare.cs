using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Lazo
{
    /// <summary>
    /// Transmisión de pantalla hacia uno o varios compañeros, por el canal cifrado LAZO3 (docs/protocolo-seguro.md).
    /// Emisor → visor: 'S' ancho alto · 'F' cursorX cursorY n [x y w h len jpeg]… · 'K' · 'E' ·
    /// 'H' versión · 'P' permisos · 'D' rechazo · 'A' len trazo · 'M' len puntero de otro visor (v2).
    /// Visor → emisor: 'H' versión · 'R' permiso · 'A' len trazo · 'I' entrada · 'M' visible x y (v2) · 'Z' ancho alto (v3).
    /// Los paquetes 'M' solo se cruzan cuando los dos lados saludaron con la versión 2 o superior; 'Z' (ajustar la
    /// resolución del monitor al tamaño de la ventana de quien mira, 0 0 = restaurar) con la 3 o superior.
    /// El ritmo lo marca el receptor más lento: no hay colas, así que la latencia se mantiene mínima.
    /// </summary>
    internal sealed class ScreenShareSession
    {
        private sealed class Viewer
        {
            public int Id;
            public string Name;
            public SecureConnection Connection;
            public Stream Stream;
            public readonly object WriteLock = new object();
            public volatile bool Capable;
            public volatile byte Granted;
            /// <summary>Versión del canal de retorno con la que saludó; desde la 2 entiende punteros.</summary>
            public volatile byte Version;
            public bool PointerShown;
            public readonly HashSet<byte> Asked = new HashSet<byte>();
            /// <summary>Entró con la contraseña de control remoto: ya tiene todos los permisos y no hay que preguntar.</summary>
            public bool Remote;
            /// <summary>Se llama una vez cuando este visor se va (o la transmisión termina).</summary>
            public Action OnLeft;
            public int LeftNotified;
        }

        public const byte BackChannelVersion = 3;
        /// <summary>Primera versión del canal de retorno que entiende «ajustar la resolución» ('Z').</summary>
        public const byte ResolutionVersion = 3;
        /// <summary>Primera versión del canal de retorno con punteros colaborativos ('M').</summary>
        public const byte PointerVersion = 2;

        private readonly NetworkEngine _network;
        private readonly object _lock = new object();
        private readonly List<Viewer> _viewers = new List<Viewer>();
        private volatile bool _running = true;
        private int _key = 1;
        private int _pending;
        private int _nextViewer;
        private Thread _worker;
        // Permisos, trazos y entrada remota se procesan en orden en un solo hilo, fuera de la interfaz.
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _control = new System.Collections.Concurrent.BlockingCollection<Action>();
        private readonly AnnotationModel _annotations = new AnnotationModel();
        // Autor de cada trazo: 0 es quien comparte. Quien mira solo puede tocar los suyos.
        private readonly Dictionary<int, int> _owners = new Dictionary<int, int>();
        private readonly RemoteInputInjector _injector;

        public ShareSource Source { get; private set; }
        public double Fps { get; private set; }
        public double Mbps { get; private set; }
        public double WorkMs { get; private set; }
        public double CaptureMs { get; private set; }
        /// <summary>Mientras quien comparte dibuja, la entrada remota se descarta: el clic acabaría en la capa de dibujo.</summary>
        public volatile bool InputPaused;
        public event Action Changed;
        public event Action<string> Notice;
        public event Action Ended;
        /// <summary>Alguien que mira pide un permiso: id del visor, nombre y <see cref="SharePermission"/>.</summary>
        public event Action<int, string, byte> PermissionRequested;
        /// <summary>Alguien dejó de mirar; sus avisos de permiso pendientes ya no sirven.</summary>
        public event Action<int> ViewerLeft;
        /// <summary>Trazo hecho por alguien que mira, ya validado; hay que pintarlo en la superposición.</summary>
        public event Action<AnnotationOp> Annotated;
        /// <summary>Puntero de alguien que dibuja o controla; hay que mostrarlo en la superposición.</summary>
        public event Action<PointerUpdate> PointerMoved;

        public ScreenShareSession(NetworkEngine network, ShareSource source)
        {
            _network = network;
            Source = source;
            _injector = new RemoteInputInjector(source);
            Thread control = new Thread(ControlLoop) { IsBackground = true, Name = "Lazo screen control" };
            control.Start();
        }

        /// <summary>Quién controla ahora el equipo y en qué modo, o null.</summary>
        public string Controller
        {
            get
            {
                lock (_lock)
                {
                    Viewer v = _viewers.FirstOrDefault(x => (x.Granted & SharePermission.Control) != 0);
                    if (v == null) return null;
                    return v.Name + ((v.Granted & SharePermission.FullControl) != 0 ? " (control total)" : " (ventana activa)");
                }
            }
        }

        public bool ControlActive
        {
            get { lock (_lock) return _viewers.Any(v => (v.Granted & SharePermission.Control) != 0); }
        }

        public bool AnyGranted
        {
            get { lock (_lock) return _viewers.Any(v => v.Granted != 0); }
        }

        public string[] ViewerNames()
        {
            lock (_lock) return _viewers.Select(v => v.Name).ToArray();
        }

        public int Pending { get { return Volatile.Read(ref _pending); } }

        public void Invite(Peer peer)
        {
            Interlocked.Increment(ref _pending);
            Raise(Changed);
            Task.Run(async () =>
            {
                try
                {
                    SecureConnection connection = await _network.OpenScreenAsync(peer, Source.Label).ConfigureAwait(false);
                    if (!_running) { connection.Dispose(); return; }
                    Viewer viewer = new Viewer { Id = Interlocked.Increment(ref _nextViewer), Name = peer.Name, Connection = connection, Stream = connection.Stream };
                    lock (_lock) _viewers.Add(viewer);
                    new Thread(() => ReadBack(viewer)) { IsBackground = true, Name = "Lazo screen back-channel" }.Start();
                    Interlocked.Exchange(ref _key, 1);
                    Say(peer.Name + " está viendo tu pantalla");
                }
                catch (Exception ex) { Say(ex.Message); }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                    Raise(Changed);
                }
            });
        }

        /// <summary>
        /// Control remoto con contraseña: adjunta a quien ya entró (conexión autenticada por <see cref="NetworkEngine.ConnectRemoteAsync"/>)
        /// como visor con dibujar, control y control total, sin pedir permiso. Solo uno a la vez. <paramref name="left"/> se llama
        /// una vez cuando esa persona se va o la transmisión termina.
        /// </summary>
        public bool AttachRemote(SecureConnection connection, string name, Action left)
        {
            Viewer viewer;
            lock (_lock)
            {
                if (!_running || _viewers.Any(v => v.Remote)) return false;
                viewer = new Viewer
                {
                    Id = Interlocked.Increment(ref _nextViewer), Name = name, Connection = connection, Stream = connection.Stream,
                    Remote = true, OnLeft = left, Granted = RemoteGranted
                };
                _viewers.Add(viewer);
            }
            _injector.Configure(true);
            new Thread(() => ReadBack(viewer)) { IsBackground = true, Name = "Lazo screen back-channel" }.Start();
            Interlocked.Exchange(ref _key, 1);
            Say("Control remoto: " + name + " entró a este equipo");
            Raise(Changed);
            return true;
        }

        /// <summary>Permisos de quien entra por control remoto: dibujar, controlar y control total.</summary>
        public const byte RemoteGranted = SharePermission.Draw | SharePermission.Control | SharePermission.FullControl;

        /// <summary>Hay alguien dentro por control remoto con contraseña.</summary>
        public bool HasRemote
        {
            get { lock (_lock) return _viewers.Any(v => v.Remote); }
        }

        public void Start()
        {
            _worker = new Thread(Loop) { IsBackground = true, Name = "Lazo screen share", Priority = ThreadPriority.AboveNormal };
            _worker.Start();
        }

        public void Stop() { _running = false; }

        // ── Canal de retorno: permisos, trazos y control remoto ─────────────

        /// <summary>Trazo hecho por quien comparte; ya está pintado en su superposición.</summary>
        public void Annotate(AnnotationOp op)
        {
            Post(() =>
            {
                if (!_annotations.Apply(op)) return;
                Track(op, 0);
                Relay(op, null);
            });
        }

        private void Track(AnnotationOp op, int owner)
        {
            if (op.Kind == AnnotationOp.Begin) _owners[op.Id] = owner;
            else if (op.Kind == AnnotationOp.Erase) _owners.Remove(op.Id);
            else if (op.Kind == AnnotationOp.Clear) _owners.Clear();
        }

        /// <summary>
        /// Respuesta de quien comparte a una petición de permiso. Para el control, <paramref name="full"/>
        /// concede el equipo completo; si no, solo la ventana activa.
        /// </summary>
        public void Answer(int viewerId, byte kind, bool accept, bool full = false)
        {
            Post(() =>
            {
                Viewer viewer = Find(viewerId);
                if (viewer == null) return;
                lock (viewer.Asked) viewer.Asked.Remove(kind);
                if (accept && kind == SharePermission.Control && ControllerOther(viewer) != null) accept = false;
                if (!accept) { Write(viewer, new[] { (byte)'D', kind }); return; }
                byte granted = (byte)(viewer.Granted | kind);
                if (kind == SharePermission.Control)
                {
                    granted = full ? (byte)(granted | SharePermission.FullControl) : (byte)(granted & ~SharePermission.FullControl);
                    _injector.Configure(full);
                }
                SetGranted(viewer, granted);
            });
        }

        /// <summary>Quita el permiso de dibujar y el control a todos.</summary>
        public void RevokeAll()
        {
            Post(() =>
            {
                Viewer[] all;
                lock (_lock) all = _viewers.ToArray();
                foreach (Viewer viewer in all) if (viewer.Granted != 0) SetGranted(viewer, 0);
            });
        }

        private void SetGranted(Viewer viewer, byte granted)
        {
            if ((granted & SharePermission.Control) == 0) granted = (byte)(granted & ~SharePermission.FullControl);
            bool hadControl = (viewer.Granted & SharePermission.Control) != 0;
            viewer.Granted = granted;
            if (hadControl && (granted & SharePermission.Control) == 0) { _injector.ReleaseAll(); RestoreResolutionIfNoController(); }
            if ((granted & (SharePermission.Draw | SharePermission.Control)) == 0) Pointer(viewer, false, 0, 0);
            Write(viewer, new[] { (byte)'P', granted });
            Raise(Changed);
        }

        /// <summary>
        /// Puntero de quien mira: solo se muestra mientras puede dibujar o controlar. Se pinta en la superposición
        /// de quien comparte y se reenvía a los demás visores que lo entienden.
        /// </summary>
        private void Pointer(Viewer viewer, bool visible, ushort x, ushort y)
        {
            if (visible && (viewer.Granted & (SharePermission.Draw | SharePermission.Control)) == 0) return;
            if (!visible && !viewer.PointerShown) return;
            viewer.PointerShown = visible;
            PointerUpdate update = new PointerUpdate
            {
                Id = viewer.Id, Visible = visible, X = x, Y = y,
                Color = (byte)((viewer.Id - 1) % PointerLayer.Palette.Length), Name = viewer.Name
            };
            Action<PointerUpdate> handler = PointerMoved;
            if (handler != null) handler(update);
            byte[] packet = Framed((byte)'M', update.ToBytes());
            Viewer[] targets;
            lock (_lock) targets = _viewers.Where(v => v.Capable && v != viewer && v.Version >= PointerVersion).ToArray();
            foreach (Viewer target in targets) Write(target, packet);
        }

        private Viewer ControllerOther(Viewer except)
        {
            lock (_lock) return _viewers.FirstOrDefault(v => v != except && (v.Granted & SharePermission.Control) != 0);
        }

        private Viewer Find(int id)
        {
            lock (_lock) return _viewers.FirstOrDefault(v => v.Id == id);
        }

        private void Post(Action work)
        {
            try { _control.Add(work); } catch (InvalidOperationException) { }
        }

        private void ControlLoop()
        {
            Native.EnterPerMonitorDpi();
            foreach (Action work in _control.GetConsumingEnumerable())
            {
                try { work(); }
                catch (Exception ex) { Debug.WriteLine("Lazo control: " + ex.Message); }
            }
        }

        /// <summary>Envía un trazo a todos los que entienden el canal de retorno, salvo a quien lo hizo.</summary>
        private void Relay(AnnotationOp op, Viewer except)
        {
            byte[] packet = Framed((byte)'A', op.ToBytes());
            Viewer[] targets;
            lock (_lock) targets = _viewers.Where(v => v.Capable && v != except).ToArray();
            foreach (Viewer viewer in targets) Write(viewer, packet);
        }

        internal static byte[] Framed(byte type, byte[] payload)
        {
            byte[] packet = new byte[3 + payload.Length];
            packet[0] = type;
            packet[1] = (byte)payload.Length;
            packet[2] = (byte)(payload.Length >> 8);
            Buffer.BlockCopy(payload, 0, packet, 3, payload.Length);
            return packet;
        }

        private void Write(Viewer viewer, byte[] packet)
        {
            WriteTo(viewer, packet, packet.Length);
        }

        /// <summary>Un trazo de quien mira: solo puede crear trazos nuevos y cambiar o borrar los suyos.</summary>
        private void ApplyFromViewer(Viewer viewer, AnnotationOp op)
        {
            if ((viewer.Granted & SharePermission.Draw) == 0 || op.Kind == AnnotationOp.Clear) return;
            int owner;
            if (op.Kind != AnnotationOp.Begin && (!_owners.TryGetValue(op.Id, out owner) || owner != viewer.Id)) return;
            if (!_annotations.Apply(op))
            {
                // Rechazado (id repetido o límite): se le quita a quien lo dibujó para que no vea algo que nadie más ve.
                if (op.Kind == AnnotationOp.Begin && !_owners.ContainsKey(op.Id))
                    Write(viewer, Framed((byte)'A', new AnnotationOp { Kind = AnnotationOp.Erase, Id = op.Id }.ToBytes()));
                return;
            }
            Track(op, viewer.Id);
            Relay(op, viewer);
            Action<AnnotationOp> handler = Annotated;
            if (handler != null) handler(op);
        }

        /// <summary>Lee lo que envía cada visor hasta que se cierre la conexión.</summary>
        private void ReadBack(Viewer viewer)
        {
            try
            {
                BinaryReader reader = new BinaryReader(viewer.Stream);
                while (_running)
                {
                    byte type = reader.ReadByte();
                    if (type == (byte)'H')
                    {
                        byte version = reader.ReadByte();
                        DiagLog.Write("transmisión: " + viewer.Name + " saludó con la versión " + version);
                        Post(() =>
                        {
                            if (viewer.Capable) return;
                            viewer.Version = version;
                            // Saludo, permisos actuales y los trazos que ya hay, en ese orden.
                            Write(viewer, new[] { (byte)'H', BackChannelVersion });
                            Write(viewer, new[] { (byte)'P', viewer.Granted });
                            foreach (AnnotationOp op in _annotations.Snapshot()) Write(viewer, Framed((byte)'A', op.ToBytes()));
                            viewer.Capable = true;
                        });
                    }
                    else if (type == (byte)'R')
                    {
                        byte kind = reader.ReadByte();
                        Post(() => Request(viewer, kind));
                    }
                    else if (type == (byte)'A')
                    {
                        int length = reader.ReadUInt16();
                        byte[] data = reader.ReadBytes(length);
                        if (data.Length != length) break;
                        AnnotationOp op = AnnotationOp.Parse(data);
                        if (op == null) continue;
                        Post(() => ApplyFromViewer(viewer, op));
                    }
                    else if (type == (byte)'I')
                    {
                        RemoteInputEvent input;
                        if (!RemoteInputEvent.Read(reader, out input)) break;
                        Post(() =>
                        {
                            if ((viewer.Granted & SharePermission.Control) == 0 || !_running || InputPaused) return;
                            _injector.Apply(input);
                        });
                    }
                    else if (type == (byte)'M')
                    {
                        bool visible = reader.ReadByte() != 0;
                        ushort x = reader.ReadUInt16(), y = reader.ReadUInt16();
                        Post(() => { if (viewer.Version >= PointerVersion) Pointer(viewer, visible, x, y); });
                    }
                    else if (type == (byte)'Z')
                    {
                        int wantWidth = reader.ReadUInt16(), wantHeight = reader.ReadUInt16();
                        Post(() => ApplyResolution(viewer, wantWidth, wantHeight));
                    }
                    else break;
                }
            }
            catch { }
            finally
            {
                Post(() =>
                {
                    if ((viewer.Granted & SharePermission.Control) != 0) _injector.ReleaseAll();
                    Pointer(viewer, false, 0, 0);
                    viewer.Granted = 0;
                    viewer.Capable = false;
                    lock (viewer.Asked) viewer.Asked.Clear();
                    RestoreResolutionIfNoController();
                    Action<int> left = ViewerLeft;
                    if (left != null) left(viewer.Id);
                    NotifyLeft(viewer);
                    Raise(Changed);
                });
            }
        }

        private static void NotifyLeft(Viewer viewer)
        {
            if (Interlocked.Exchange(ref viewer.LeftNotified, 1) == 1) return;
            Action left = viewer.OnLeft;
            if (left != null) { try { left(); } catch { } }
        }

        // ── Ajustar la resolución del monitor compartido al tamaño de la ventana de quien mira ('Z') ──

        private readonly object _displayLock = new object();
        private DisplayResolution _display;
        private DateTime _lastModeChange = DateTime.MinValue;
        private bool _zScheduled;
        private int _zWidth, _zHeight;
        private Viewer _zViewer;

        private void ApplyResolution(Viewer viewer, int width, int height)
        {
            if (Source.IsWindow || viewer.Version < ResolutionVersion || (viewer.Granted & SharePermission.Control) == 0) return;
            if (width == 0 && height == 0) { RestoreResolution(); return; }
            if (width < 320 || height < 200 || width > 16384 || height > 16384) return;
            // Como mucho un cambio por segundo; la última petición no se pierde.
            double wait = 1000 - (DateTime.UtcNow - _lastModeChange).TotalMilliseconds;
            if (wait > 0)
            {
                _zViewer = viewer;
                _zWidth = width;
                _zHeight = height;
                if (!_zScheduled)
                {
                    _zScheduled = true;
                    Task.Delay((int)wait + 25).ContinueWith(t => Post(() =>
                    {
                        _zScheduled = false;
                        if (_zViewer != null && _running) ApplyResolution(_zViewer, _zWidth, _zHeight);
                    }));
                }
                return;
            }
            string mode;
            bool announce = false;
            lock (_displayLock)
            {
                if (_display == null) _display = new DisplayResolution(Source);
                bool before = _display.Changed;
                mode = _display.Fit(width, height);
                announce = !before && _display.Changed;
            }
            _lastModeChange = DateTime.UtcNow;
            if (mode == null) { Say("No se pudo ajustar la resolución del monitor al tamaño de " + viewer.Name + "."); return; }
            Interlocked.Exchange(ref _key, 1);
            if (announce) Say("Resolución ajustada a " + mode + " mientras " + viewer.Name + " controla este equipo");
        }

        /// <summary>Devuelve el monitor a su resolución de siempre (si Lazo la había cambiado).</summary>
        private void RestoreResolution()
        {
            bool restored;
            lock (_displayLock) restored = _display != null && _display.Restore();
            if (!restored) return;
            Interlocked.Exchange(ref _key, 1);
            Say("Resolución restaurada");
        }

        private void RestoreResolutionIfNoController()
        {
            bool any;
            lock (_lock) any = _viewers.Any(v => (v.Granted & SharePermission.Control) != 0);
            if (!any) RestoreResolution();
        }

        private void Request(Viewer viewer, byte kind)
        {
            if ((kind & SharePermission.Release) != 0)
            {
                byte which = (byte)(kind & ~SharePermission.Release);
                if ((viewer.Granted & which) != 0) SetGranted(viewer, (byte)(viewer.Granted & ~which));
                return;
            }
            if (kind != SharePermission.Draw && kind != SharePermission.Control) return;
            if ((viewer.Granted & kind) != 0) { Write(viewer, new[] { (byte)'P', viewer.Granted }); return; }
            if (kind == SharePermission.Control && ControllerOther(viewer) != null)
            {
                Write(viewer, new[] { (byte)'D', kind });
                Say(viewer.Name + " pidió el control, pero ya lo tiene otra persona.");
                return;
            }
            lock (viewer.Asked) if (!viewer.Asked.Add(kind)) return;
            Action<int, string, byte> handler = PermissionRequested;
            if (handler != null) handler(viewer.Id, viewer.Name, kind);
            else Answer(viewer.Id, kind, false);
        }

        private volatile bool _idle;
        private bool _loggedFrame;
        private int _failedCaptures;
        private volatile bool _captureDead;
        private volatile bool _sourceGone;

        // Captura y codificación van en hilos distintos con doble búfer: mientras se codifica un fotograma
        // ya se está capturando el siguiente. La captura queda sincronizada con el refresco de Windows.
        private void Loop()
        {
            Native.EnterPerMonitorDpi();
            timeBeginPeriod(1);
            FrameCapturer[] slots = null;
            SemaphoreSlim[] free = { new SemaphoreSlim(1), new SemaphoreSlim(1) };
            System.Collections.Concurrent.BlockingCollection<int> ready = new System.Collections.Concurrent.BlockingCollection<int>(1);
            Thread grabber = null;
            TileEncoder encoder = new TileEncoder();
            try
            {
                DiagLog.Write("transmisión: bucle iniciado, fuente " + Source.Width + "x" + Source.Height + (Source.IsWindow ? " (ventana)" : " (pantalla)"));
                slots = new[] { new FrameCapturer(Source), new FrameCapturer(Source) };
                grabber = new Thread(() => Grab(slots, free, ready)) { IsBackground = true, Name = "Lazo capture", Priority = ThreadPriority.AboveNormal };
                grabber.Start();
                Stopwatch clock = Stopwatch.StartNew();
                long lastSend = 0, lastChange = 0, statStart = 0, bytes = 0, work = 0;
                int frames = 0, captures = 0, lastX = int.MinValue, lastY = int.MinValue;
                bool hadViewer = false;
                MemoryStream packet = new MemoryStream(1 << 20);
                BinaryWriter writer = new BinaryWriter(packet);
                while (_running)
                {
                    int index;
                    bool got = ready.TryTake(out index, 100);
                    long start = clock.ElapsedMilliseconds;
                    Viewer[] targets;
                    lock (_lock) targets = _viewers.ToArray();
                    if (_sourceGone) { Say("La ventana compartida se cerró."); break; }
                    if (_captureDead)
                    {
                        DiagLog.Write("transmisión: se termina porque no se pudo capturar nunca la pantalla");
                        Say("No se pudo capturar la pantalla de este equipo. Con la sesión desconectada o bloqueada no hay imagen; desbloquéala o vuelve a abrirla.");
                        break;
                    }
                    if (targets.Length == 0)
                    {
                        if (got && index >= 0) free[index].Release();
                        if (Pending == 0 && (hadViewer || start > 1500))
                        {
                            Say(hadViewer ? "Nadie está viendo; se detuvo la transmisión." : "Nadie aceptó la invitación.");
                            break;
                        }
                        continue;
                    }
                    hadViewer = true;
                    if (!got || index < 0)
                    {
                        if (start - lastSend > 1000) { Send(targets, new[] { (byte)'K' }, 1); lastSend = start; }
                        continue;
                    }
                    FrameCapturer capture = slots[index];
                    int cursorX = capture.CursorX, cursorY = capture.CursorY, width = capture.Width, height = capture.Height;
                    bool key = Interlocked.Exchange(ref _key, 0) == 1 || width != encoder.Width || height != encoder.Height;
                    List<EncodedTile> tiles;
                    try { tiles = encoder.Encode(capture, key); }
                    finally { free[index].Release(); }
                    work += clock.ElapsedMilliseconds - start;
                    captures++;
                    bool moved = cursorX != lastX || cursorY != lastY;
                    if (!_loggedFrame && (tiles.Count > 0 || key))
                    {
                        _loggedFrame = true;
                        DiagLog.Write("transmisión: primer fotograma " + width + "x" + height + " con " + tiles.Count + " bloques para " + targets.Length + " visor(es)");
                    }
                    if (tiles.Count > 0 || moved || key)
                    {
                        packet.SetLength(0);
                        if (key)
                        {
                            writer.Write((byte)'S');
                            writer.Write(width);
                            writer.Write(height);
                        }
                        writer.Write((byte)'F');
                        writer.Write(cursorX);
                        writer.Write(cursorY);
                        writer.Write(tiles.Count);
                        foreach (EncodedTile tile in tiles)
                        {
                            writer.Write((ushort)tile.X);
                            writer.Write((ushort)tile.Y);
                            writer.Write((ushort)tile.W);
                            writer.Write((ushort)tile.H);
                            writer.Write(tile.Jpeg.Length);
                            writer.Write(tile.Jpeg);
                        }
                        writer.Flush();
                        Send(targets, packet.GetBuffer(), (int)packet.Length);
                        bytes += packet.Length;
                        lastSend = clock.ElapsedMilliseconds;
                        lastX = cursorX;
                        lastY = cursorY;
                        if (tiles.Count > 0) { frames++; lastChange = lastSend; }
                    }
                    else if (start - lastSend > 1000)
                    {
                        Send(targets, new[] { (byte)'K' }, 1);
                        lastSend = start;
                    }
                    long now = clock.ElapsedMilliseconds;
                    // 60 fps mientras hay movimiento; si todo está quieto, se sondea con menos frecuencia.
                    _idle = now - lastChange > 1500;
                    if (now - statStart >= 1000)
                    {
                        Fps = frames * 1000.0 / (now - statStart);
                        Mbps = bytes * 8.0 / (now - statStart) / 1000.0;
                        WorkMs = captures == 0 ? 0 : (double)work / captures;
                        frames = 0; bytes = 0; work = 0; captures = 0; statStart = now;
                        Raise(Changed);
                    }
                }
            }
            catch (Exception ex) { Say("Se detuvo la transmisión: " + ex.Message); }
            finally
            {
                _running = false;
                _control.CompleteAdding();
                _injector.ReleaseAll();
                RestoreResolution();
                if (grabber != null) grabber.Join(2000);
                timeEndPeriod(1);
                if (slots != null) foreach (FrameCapturer slot in slots) if (slot != null) slot.Dispose();
                encoder.Dispose();
                Viewer[] rest;
                lock (_lock) { rest = _viewers.ToArray(); _viewers.Clear(); }
                foreach (Viewer viewer in rest)
                {
                    try { lock (viewer.WriteLock) { viewer.Stream.Write(new[] { (byte)'E' }, 0, 1); viewer.Stream.Flush(); } } catch { }
                    viewer.Connection.Dispose();
                    NotifyLeft(viewer);
                }
                Raise(Ended);
            }
        }

        private void Grab(FrameCapturer[] slots, SemaphoreSlim[] free, System.Collections.Concurrent.BlockingCollection<int> ready)
        {
            Native.EnterPerMonitorDpi();
            Stopwatch clock = Stopwatch.StartNew();
            int i = 0;
            while (_running)
            {
                long start = clock.ElapsedMilliseconds;
                if (!free[i].Wait(200)) continue;
                bool ok;
                try
                {
                    if (!slots[i].Alive) { _sourceGone = true; free[i].Release(); return; }
                    long t0 = clock.ElapsedMilliseconds;
                    ok = slots[i].Capture();
                    CaptureMs = CaptureMs * 0.9 + (clock.ElapsedMilliseconds - t0) * 0.1;
                }
                catch (Exception ex) { ok = false; if (_failedCaptures == 0) DiagLog.Write("captura: excepción " + ex.GetType().Name + ": " + ex.Message); }
                if (!ok)
                {
                    _failedCaptures++;
                    // Unos 8 s sin poder capturar (pantalla bloqueada, sesión de Escritorio Remoto desconectada…): se termina con aviso
                    // en vez de dejar a quien mira esperando una imagen que no va a llegar.
                    if (_failedCaptures == 100 && !_loggedFrame) _captureDead = true;
                    if (_failedCaptures == 1 || _failedCaptures == 40 || _failedCaptures % 400 == 0)
                        DiagLog.Write("captura: no se pudo capturar el fotograma (" + _failedCaptures + " fallos seguidos; ¿pantalla bloqueada o sin sesión activa?)");
                    free[i].Release();
                }
                else if (_failedCaptures > 0) { DiagLog.Write("captura: volvió a funcionar tras " + _failedCaptures + " fallos"); _failedCaptures = 0; }
                bool posted = false;
                while (_running && !posted) posted = ready.TryAdd(ok ? i : -1, 200);
                if (!posted) { if (ok) free[i].Release(); return; }
                if (ok) i ^= 1;
                int interval = !ok ? 80 : _idle ? 40 : 15;
                int rest = interval - (int)(clock.ElapsedMilliseconds - start);
                if (rest > 0) Thread.Sleep(rest);
            }
        }

        private void Send(Viewer[] targets, byte[] buffer, int length)
        {
            Action<Viewer> write = viewer => WriteTo(viewer, buffer, length);
            if (targets.Length == 1) write(targets[0]);
            else Parallel.ForEach(targets, write);
        }

        /// <summary>Fotogramas y paquetes de control comparten la conexión; cada escritura va entera.</summary>
        private void WriteTo(Viewer viewer, byte[] buffer, int length)
        {
            try { lock (viewer.WriteLock) { viewer.Stream.Write(buffer, 0, length); viewer.Stream.Flush(); } }
            catch
            {
                bool removed;
                lock (_lock) removed = _viewers.Remove(viewer);
                viewer.Connection.Dispose();
                NotifyLeft(viewer);
                if (!removed) return;
                Say(viewer.Name + " dejó de ver tu pantalla");
                Raise(Changed);
            }
        }

        private void Say(string text)
        {
            Action<string> handler = Notice;
            if (handler != null) handler(text);
        }

        private static void Raise(Action handler)
        {
            if (handler != null) handler();
        }

        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
    }

    /// <summary>Recibe la transmisión y la pinta en una ventana. Decodifica en paralelo fuera del hilo de interfaz.</summary>
    internal sealed class ScreenViewer : IScreenSink
    {
        private readonly ScreenViewerView _window;
        private volatile bool _closed;

        public ScreenViewer(ScreenViewerView view)
        {
            _window = view;
        }

        public ScreenViewer(ScreenViewerWindow window) : this(window.View)
        {
        }

        private struct Decoded
        {
            public int X, Y, W, H;
            public byte[] Pixels;
        }

        public void Run(Stream network)
        {
            Dispatcher ui = _window.Dispatcher;
            ScreenBackChannel back = new ScreenBackChannel(network);
            ui.Invoke((Action)(() =>
            {
                _window.Closed += () =>
                {
                    _closed = true;
                    try { network.Close(); } catch { }
                };
                _window.Attach(back);
            }));
            // Las versiones anteriores ignoran el saludo; solo si responden se ofrecen dibujar y controlar.
            back.Send(new[] { (byte)'H', ScreenShareSession.BackChannelVersion });
            string reason = "La transmisión terminó.";
            try
            {
                BinaryReader reader = new BinaryReader(new BufferedStream(network, 1 << 18));
                int width = 0, height = 0;
                while (!_closed)
                {
                    byte type = reader.ReadByte();
                    if (type == (byte)'K') continue;
                    if (type == (byte)'E') break;
                    if (type == (byte)'H')
                    {
                        byte version = reader.ReadByte();
                        ui.BeginInvoke((Action)(() => _window.BackChannelReady(version)));
                        continue;
                    }
                    if (type == (byte)'M')
                    {
                        int length = reader.ReadUInt16();
                        byte[] data = reader.ReadBytes(length);
                        if (data.Length != length) throw new EndOfStreamException();
                        PointerUpdate update = PointerUpdate.Parse(data);
                        if (update != null) ui.BeginInvoke((Action)(() => _window.Pointers.Apply(update)));
                        continue;
                    }
                    if (type == (byte)'P' || type == (byte)'D')
                    {
                        byte value = reader.ReadByte();
                        bool denied = type == (byte)'D';
                        ui.BeginInvoke((Action)(() => { if (denied) _window.Denied(value); else _window.SetGranted(value); }));
                        continue;
                    }
                    if (type == (byte)'A')
                    {
                        int length = reader.ReadUInt16();
                        byte[] data = reader.ReadBytes(length);
                        if (data.Length != length) throw new EndOfStreamException();
                        AnnotationOp op = AnnotationOp.Parse(data);
                        if (op != null) ui.BeginInvoke((Action)(() => _window.Layer.Apply(op)));
                        continue;
                    }
                    if (type == (byte)'S')
                    {
                        width = reader.ReadInt32();
                        height = reader.ReadInt32();
                        if (width < 1 || height < 1 || width > 8192 || height > 8192) throw new InvalidDataException("Tamaño de pantalla no válido.");
                        int w = width, h = height;
                        ui.Invoke((Action)(() => _window.Resize(w, h)), DispatcherPriority.Render);
                        continue;
                    }
                    if (type != (byte)'F') throw new InvalidDataException("Paquete desconocido.");
                    int cx = reader.ReadInt32(), cy = reader.ReadInt32(), count = reader.ReadInt32();
                    if (count < 0 || count > 40000) throw new InvalidDataException("Fotograma no válido.");
                    Decoded[] tiles = new Decoded[count];
                    byte[][] jpegs = new byte[count][];
                    for (int i = 0; i < count; i++)
                    {
                        int x = reader.ReadUInt16(), y = reader.ReadUInt16(), w = reader.ReadUInt16(), h = reader.ReadUInt16();
                        int length = reader.ReadInt32();
                        if (length < 0 || length > 32 * 1024 * 1024 || x + w > width || y + h > height || w == 0 || h == 0)
                            throw new InvalidDataException("Bloque fuera de rango.");
                        jpegs[i] = reader.ReadBytes(length);
                        if (jpegs[i].Length != length) throw new EndOfStreamException();
                        tiles[i] = new Decoded { X = x, Y = y, W = w, H = h };
                    }
                    Parallel.For(0, count, i => tiles[i].Pixels = Decode(jpegs[i], tiles[i].W, tiles[i].H));
                    ui.Invoke((Action)(() => _window.Apply(tiles.Select(t => new ScreenViewerView.Patch { X = t.X, Y = t.Y, W = t.W, H = t.H, Pixels = t.Pixels }).ToArray(), cx, cy, count > 0)),
                        DispatcherPriority.Render);
                }
            }
            catch (Exception ex)
            {
                if (!_closed) reason = ex is IOException || ex is EndOfStreamException ? "Se perdió la conexión." : "Error: " + ex.Message;
            }
            finally
            {
                back.Close();
                if (!_closed)
                {
                    string text = reason;
                    try { ui.BeginInvoke((Action)(() => _window.Ended(text))); } catch { }
                }
            }
        }

        private static byte[] Decode(byte[] jpeg, int w, int h)
        {
            byte[] pixels = new byte[w * h * 4];
            using (MemoryStream input = new MemoryStream(jpeg))
            using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(input))
            {
                if (bitmap.Width != w || bitmap.Height != h) throw new InvalidDataException("Bloque con tamaño inesperado.");
                System.Drawing.Imaging.BitmapData data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                try
                {
                    for (int row = 0; row < h; row++)
                        Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * w * 4, w * 4);
                }
                finally { bitmap.UnlockBits(data); }
            }
            return pixels;
        }
    }

    /// <summary>
    /// Una pantalla compartida que estoy viendo: imagen, dibujo, control y punteros. Va dentro de una ventana
    /// propia (<see cref="ScreenViewerWindow"/>) o como mosaico junto a otras (<see cref="ScreenStageWindow"/>).
    /// </summary>
    internal sealed class ScreenViewerView : Grid
    {
        private enum ViewerMode { Watch, Draw, Control }

        internal struct Patch
        {
            public int X, Y, W, H;
            public byte[] Pixels;
        }

        private readonly Grid _surface;
        private readonly Image _image;
        private readonly System.Windows.Shapes.Path _cursor;
        private readonly TextBlock _status;
        private readonly TextBlock _center;
        private readonly Border _bar;
        private readonly string _title;
        private readonly DispatcherTimer _idle;
        private readonly Button _drawButton;
        private readonly Button _controlButton;
        private readonly Button _fitButton;
        private readonly DispatcherTimer _fitTimer;
        private bool _fit;
        // El ajuste espera a que ya se vea algo: cambiar el monitor remoto durante el arranque puede dejar la imagen en negro.
        private bool _gotFrame, _fitPending;
        private readonly DispatcherTimer _firstFitTimer;
        private readonly AnnotationToolbar _tools;
        private readonly Border _hintBox;
        private readonly TextBlock _hint;
        private readonly DispatcherTimer _hintTimer;
        private readonly HashSet<int> _keysDown = new HashSet<int>();
        private readonly HashSet<byte> _buttonsDown = new HashSet<byte>();
        private ScreenBackChannel _back;
        private byte _sharerVersion;
        private readonly DispatcherTimer _pointerTimer;
        private bool _pointerPending, _pointerShown;
        private ushort _pointerX, _pointerY;
        private byte _granted;
        private byte _asking;
        private ViewerMode _mode;
        private ushort _lastX = ushort.MaxValue, _lastY = ushort.MaxValue;
        private WriteableBitmap _bitmap;
        private int _frames;
        private DateTime _statStart = DateTime.UtcNow;
        private double _fps;
        private bool _closed;

        /// <summary>Quien comparte esta pantalla.</summary>
        public string Sender { get; private set; }
        public bool HasEnded { get; private set; }
        /// <summary>Control remoto con contraseña: en cuanto llega el permiso de control se entra en modo control, sin botón que pulsar.</summary>
        public bool AutoControl;
        /// <summary>Pide ver esta pantalla en grande (F11, el botón de ampliar).</summary>
        public event Action FullRequested;
        /// <summary>Doble clic mientras solo se mira: destacarla entre las demás.</summary>
        public event Action SpotlightRequested;
        /// <summary>Botón de cerrar: quien la contiene la retira.</summary>
        public event Action CloseRequested;
        /// <summary>Se dejó de ver: hay que cortar la conexión.</summary>
        public event Action Closed;
        public event Action EndedChanged;

        public ScreenViewerView(string sender, string source)
        {
            Sender = sender;
            _title = sender + (string.IsNullOrEmpty(source) ? "" : " · " + source);
            Background = Theme.Color("#0F0A0B");
            System.Windows.Documents.TextElement.SetFontFamily(this, Theme.Font);
            Focusable = true;
            ClipToBounds = true;

            Grid root = this;
            _image = new Image { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            _cursor = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M0,0 L0,17 L4.5,12.8 L7.6,19.6 L10.3,18.4 L7.3,11.8 L13,11.8 Z"),
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 1.1,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            Canvas overlay = new Canvas { IsHitTestVisible = false };
            overlay.Children.Add(_cursor);
            Layer = new AnnotationLayer { OnlyMine = true };
            Layer.Local += op => { if (_back != null) _back.Send(ScreenShareSession.Framed((byte)'A', op.ToBytes())); };
            Pointers = new PointerLayer();
            _surface = new Grid();
            _surface.Children.Add(_image);
            _surface.Children.Add(Layer);
            _surface.Children.Add(overlay);
            _surface.Children.Add(Pointers);
            // Vista previa: llega aunque la capa de dibujo marque el movimiento como atendido.
            _surface.PreviewMouseMove += (s, e) => TrackPointer(e);
            _surface.MouseLeave += (s, e) => HidePointer();
            _pointerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _pointerTimer.Tick += (s, e) => FlushPointer();
            _surface.MouseDown += OnSurfaceDown;
            _surface.MouseUp += OnSurfaceUp;
            _surface.MouseMove += OnSurfaceMove;
            _surface.MouseWheel += OnSurfaceWheel;
            _surface.LostMouseCapture += (s, e) => ReleaseButtons();
            root.Children.Add(new Viewbox { Child = _surface, Stretch = Stretch.Uniform });

            _center = Theme.Text("Conectando…", 15, Theme.Color("#C9B4B4"));
            _center.HorizontalAlignment = HorizontalAlignment.Center;
            _center.VerticalAlignment = VerticalAlignment.Center;
            root.Children.Add(_center);

            StackPanel pill = new StackPanel { Orientation = Orientation.Horizontal };
            pill.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Theme.Color("#F25C45"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            _status = Theme.Text(_title, 12, Brushes.White, FontWeights.SemiBold);
            _status.TextWrapping = TextWrapping.NoWrap;
            _status.VerticalAlignment = VerticalAlignment.Center;
            pill.Children.Add(_status);
            _drawButton = BarButton("pen", "Dibujar sobre la pantalla (pide permiso)", OnDrawClick);
            _drawButton.Visibility = Visibility.Collapsed;
            pill.Children.Add(_drawButton);
            _controlButton = BarButton("pointer", "Controlar el equipo (pide permiso)", OnControlClick);
            _controlButton.Visibility = Visibility.Collapsed;
            pill.Children.Add(_controlButton);
            _fitButton = BarButton("fit", "Ajustar la resolución del otro equipo al tamaño de esta ventana", ToggleFit);
            _fitButton.Visibility = Visibility.Collapsed;
            pill.Children.Add(_fitButton);
            pill.Children.Add(BarButton("expand", "Pantalla completa (F11)", () => Raise(FullRequested)));
            pill.Children.Add(BarButton("close", "Dejar de ver", () => Raise(CloseRequested)));
            _tools = new AnnotationToolbar(Layer, "Borrar mis trazos", () => Layer.EraseMine())
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 2),
                Visibility = Visibility.Collapsed
            };
            StackPanel barRows = new StackPanel();
            barRows.Children.Add(pill);
            barRows.Children.Add(_tools);
            _bar = new Border
            {
                Background = Theme.Color("#CC2A0E12"),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(14, 4, 6, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 12, 0, 0),
                Child = barRows
            };
            root.Children.Add(_bar);

            _hint = Theme.Text("", 12.5, Brushes.White, FontWeights.SemiBold);
            _hint.TextWrapping = TextWrapping.NoWrap;
            _hintBox = new Border
            {
                Background = Theme.Color("#E62A0E12"),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14, 7, 14, 7),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 18),
                IsHitTestVisible = false,
                Opacity = 0,
                Child = _hint
            };
            root.Children.Add(_hintBox);
            _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            _hintTimer.Tick += (s, e) => { _hintTimer.Stop(); Fade(_hintBox, 0); };
            PreviewKeyDown += OnPreviewKey;
            PreviewKeyUp += OnPreviewKey;
            // Si el teclado pasa a otra pantalla del mosaico, no se queda ninguna tecla pulsada en esta.
            IsKeyboardFocusWithinChanged += (s, e) => { if (!IsKeyboardFocusWithin) ReleaseKeys(); };

            _idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _idle.Tick += (s, e) => { _idle.Stop(); if (_bitmap != null && _mode != ViewerMode.Draw) Fade(_bar, 0); };
            MouseMove += (s, e) => { Fade(_bar, 1); _idle.Stop(); _idle.Start(); };
            MouseLeftButtonDown += (s, e) =>
            {
                if (_mode == ViewerMode.Watch) Focus();
                if (e.ClickCount == 2 && _mode == ViewerMode.Watch) { Raise(SpotlightRequested); e.Handled = true; }
            };
            KeyDown += (s, e) => { if (e.Key == Key.F11) { Raise(FullRequested); e.Handled = true; } };
            _idle.Start();
            // Con «Ajustar resolución» activo, cada cambio de tamaño de la ventana se pide 500 ms después de que termine.
            _fitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _fitTimer.Tick += (s, e) => { _fitTimer.Stop(); SendFit(); };
            _firstFitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _firstFitTimer.Tick += (s, e) => { _firstFitTimer.Stop(); SendFit(); };
            SizeChanged += (s, e) => { if (_fit) ScheduleFit(); };
        }

        private static void Raise(Action handler)
        {
            if (handler != null) handler();
        }

        /// <summary>Miniatura en la tira del mosaico: sin barra ni avisos, solo la imagen.</summary>
        public bool Compact
        {
            get { return _bar.Visibility != Visibility.Visible; }
            set
            {
                _bar.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
                _hintBox.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
                if (value && _mode != ViewerMode.Watch) SetMode(ViewerMode.Watch);
            }
        }

        /// <summary>La ventana que la contiene dejó de estar activa: se sueltan teclas y botones.</summary>
        public void ReleaseInput()
        {
            ReleaseKeys();
            ReleaseButtons();
        }

        /// <summary>Deja de ver esta pantalla (corta la conexión). Se puede llamar varias veces.</summary>
        public void Close()
        {
            if (_closed) return;
            _closed = true;
            HidePointer();
            ReleaseInput();
            _idle.Stop();
            _pointerTimer.Stop();
            _fitTimer.Stop();
            Raise(Closed);
        }

        private Button BarButton(string glyph, string tip, Action click)
        {
            Button button = new Button
            {
                Content = Icons.Make(glyph, 15, Brushes.White, 2),
                Width = 30,
                Height = 30,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = tip,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent
            };
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(Border));
            frame.Name = "face";
            frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            frame.AppendChild(content);
            template.VisualTree = frame;
            Trigger hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Color("#33FFFFFF"), "face"));
            template.Triggers.Add(hover);
            button.Template = template;
            button.Click += (s, e) => click();
            return button;
        }

        private static void Fade(UIElement element, double to)
        {
            element.BeginAnimation(OpacityProperty, Theme.Animation(element.Opacity, to, 200));
        }

        internal double Fps { get { return _fps; } }
        internal BitmapSource Frame { get { return _bitmap; } }

        public void Resize(int width, int height)
        {
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
            _image.Source = _bitmap;
            _surface.Width = width;
            _surface.Height = height;
            _cursor.LayoutTransform = new ScaleTransform(Math.Max(1, width / 1600.0), Math.Max(1, width / 1600.0));
            Pointers.PointerScale = Math.Max(1, width / 1600.0);
            _center.Visibility = Visibility.Collapsed;
        }

        public void Apply(Patch[] patches, int cursorX, int cursorY, bool changed)
        {
            if (_bitmap == null) return;
            if (!_gotFrame && patches.Length > 0)
            {
                _gotFrame = true;
                DiagLog.Write("visor " + Sender + ": llegó el primer fotograma");
                if (_fitPending) { _fitPending = false; _firstFitTimer.Stop(); _firstFitTimer.Start(); }
            }
            if (patches.Length > 0)
            {
                _bitmap.Lock();
                try
                {
                    IntPtr back = _bitmap.BackBuffer;
                    int stride = _bitmap.BackBufferStride;
                    foreach (Patch patch in patches)
                    {
                        int rowBytes = patch.W * 4;
                        for (int row = 0; row < patch.H; row++)
                            Marshal.Copy(patch.Pixels, row * rowBytes, back + (patch.Y + row) * stride + patch.X * 4, rowBytes);
                        _bitmap.AddDirtyRect(new Int32Rect(patch.X, patch.Y, patch.W, patch.H));
                    }
                }
                finally { _bitmap.Unlock(); }
            }
            // Mientras controlo solo se ve un puntero: el de este equipo. El cursor remoto se oculta.
            if (cursorX >= 0 && cursorY >= 0 && _mode != ViewerMode.Control)
            {
                Canvas.SetLeft(_cursor, cursorX);
                Canvas.SetTop(_cursor, cursorY);
                _cursor.Visibility = Visibility.Visible;
            }
            else _cursor.Visibility = Visibility.Collapsed;
            if (changed) _frames++;
            double elapsed = (DateTime.UtcNow - _statStart).TotalSeconds;
            if (elapsed >= 1)
            {
                _fps = _frames / elapsed;
                _frames = 0;
                _statStart = DateTime.UtcNow;
                _status.Text = _title + "  ·  " + Math.Round(_fps) + " fps";
            }
        }

        internal AnnotationLayer Layer { get; private set; }
        /// <summary>Punteros con nombre de los demás que dibujan o controlan.</summary>
        internal PointerLayer Pointers { get; private set; }

        internal void Attach(ScreenBackChannel back)
        {
            _back = back;
        }

        /// <summary>Quien comparte entiende el canal de retorno: se ofrecen dibujar y controlar.</summary>
        internal void BackChannelReady(byte version)
        {
            if (_back == null) return;
            _sharerVersion = version;
            _drawButton.Visibility = Visibility.Visible;
            _controlButton.Visibility = Visibility.Visible;
            SyncButtons();
        }

        internal void SetGranted(byte granted)
        {
            byte gained = (byte)(granted & ~_granted);
            _granted = granted;
            if (_mode == ViewerMode.Draw && (granted & SharePermission.Draw) == 0) { SetMode(ViewerMode.Watch); Hint("Ya no puedes dibujar."); }
            if (_mode == ViewerMode.Control && (granted & SharePermission.Control) == 0) { SetMode(ViewerMode.Watch); Hint("Ya no controlas el equipo."); }
            // Al conceder lo que se pidió, se entra directamente en ese modo.
            if ((gained & _asking & SharePermission.Control) != 0)
            {
                SetMode(ViewerMode.Control);
                Hint((granted & SharePermission.FullControl) != 0
                    ? "Tienes el control total del equipo. Pulsa el mismo botón para soltarlo."
                    : "Controlas la ventana activa. Pulsa el mismo botón para soltarlo.");
            }
            else if ((gained & _asking & SharePermission.Draw) != 0) { SetMode(ViewerMode.Draw); Hint("Ya puedes dibujar."); }
            else if (AutoControl && (gained & SharePermission.Control) != 0 && _mode != ViewerMode.Control)
            {
                SetMode(ViewerMode.Control);
                Hint(_fit ? "Control remoto activo. La resolución del otro equipo se ajusta a esta ventana." : "Control remoto activo.");
            }
            _asking = (byte)(_asking & ~gained);
            SyncButtons();
        }

        internal void Denied(byte kind)
        {
            _asking = (byte)(_asking & ~kind);
            Hint(kind == SharePermission.Control ? "No se concedió el control." : "No se concedió permiso para dibujar.");
            SyncButtons();
        }

        private void OnDrawClick()
        {
            if ((_granted & SharePermission.Draw) != 0) { SetMode(_mode == ViewerMode.Draw ? ViewerMode.Watch : ViewerMode.Draw); return; }
            Ask(SharePermission.Draw, "Pidiendo permiso para dibujar…");
        }

        private void OnControlClick()
        {
            if ((_granted & SharePermission.Control) != 0)
            {
                // Soltar el control lo devuelve a quien comparte.
                SetMode(ViewerMode.Watch);
                if (_back != null) _back.Send(new[] { (byte)'R', (byte)(SharePermission.Control | SharePermission.Release) });
                return;
            }
            Ask(SharePermission.Control, "Pidiendo el control del equipo…");
        }

        /// <summary>Para pruebas: lo mismo que pulsar Dibujar o Controlar sin permiso.</summary>
        internal void Request(byte kind)
        {
            Ask(kind, "Pidiendo permiso…");
        }

        /// <summary>Para pruebas: lo mismo que pulsar el botón de control (pedirlo o soltarlo).</summary>
        internal void ToggleControl()
        {
            OnControlClick();
        }

        internal byte Granted { get { return _granted; } }
        internal bool CanAsk { get { return _drawButton.Visibility == Visibility.Visible; } }

        private void Ask(byte kind, string text)
        {
            if (_back == null || (_asking & kind) != 0) return;
            _asking |= kind;
            _back.Send(new[] { (byte)'R', kind });
            Hint(text);
            SyncButtons();
        }

        private void SetMode(ViewerMode mode)
        {
            if (_mode == ViewerMode.Control && mode != ViewerMode.Control) { ReleaseKeys(); ReleaseButtons(); }
            if (mode == ViewerMode.Watch) HidePointer();
            bool entering = mode == ViewerMode.Control && _mode != ViewerMode.Control;
            _mode = mode;
            if (entering && ViewerPrefs.AutoFit && !_fit && _back != null && _sharerVersion >= ScreenShareSession.ResolutionVersion && (_granted & SharePermission.Control) != 0)
                _fit = true;
            Layer.Drawing = mode == ViewerMode.Draw;
            _tools.Visibility = mode == ViewerMode.Draw ? Visibility.Visible : Visibility.Collapsed;
            _image.Cursor = mode == ViewerMode.Control ? Cursors.Arrow : null;
            if (mode == ViewerMode.Control) _cursor.Visibility = Visibility.Collapsed;
            if (mode != ViewerMode.Watch) { Fade(_bar, 1); Focus(); }
            SyncButtons();
            if (_fit && mode == ViewerMode.Control) ScheduleFit();
        }

        /// <summary>Pide el ajuste 500 ms después del último cambio; si todavía no llegó ninguna imagen, espera a la primera (más 1,5 s).</summary>
        private void ScheduleFit()
        {
            if (!_gotFrame) { _fitPending = true; return; }
            _fitTimer.Stop();
            _fitTimer.Start();
        }

        private void SyncButtons()
        {
            Brush active = Theme.Color("#55FFFFFF");
            _drawButton.Background = _mode == ViewerMode.Draw ? active : Brushes.Transparent;
            _controlButton.Background = _mode == ViewerMode.Control ? active : Brushes.Transparent;
            _drawButton.Opacity = (_asking & SharePermission.Draw) != 0 ? 0.5 : 1;
            _controlButton.Opacity = (_asking & SharePermission.Control) != 0 ? 0.5 : 1;
            _drawButton.ToolTip = (_granted & SharePermission.Draw) != 0 ? (_mode == ViewerMode.Draw ? "Dejar de dibujar" : "Dibujar") : "Dibujar sobre la pantalla (pide permiso)";
            _controlButton.ToolTip = (_granted & SharePermission.Control) != 0 ? "Soltar el control" : "Controlar el equipo (pide permiso)";
            // «Ajustar resolución»: solo si quien comparte lo entiende (canal v3) y tengo el control.
            bool canFit = _back != null && _sharerVersion >= ScreenShareSession.ResolutionVersion && (_granted & SharePermission.Control) != 0;
            _fitButton.Visibility = canFit ? Visibility.Visible : Visibility.Collapsed;
            _fitButton.Background = _fit ? active : Brushes.Transparent;
            _fitButton.ToolTip = _fit ? "Dejar de ajustar la resolución (el otro equipo vuelve a la suya)" : "Ajustar la resolución del otro equipo al tamaño de esta ventana";
        }

        private void ToggleFit()
        {
            if (_back == null || _sharerVersion < ScreenShareSession.ResolutionVersion || (_granted & SharePermission.Control) == 0) return;
            _fit = !_fit;
            _fitTimer.Stop();
            _firstFitTimer.Stop();
            if (_fit)
            {
                if (_gotFrame) SendFit(); else _fitPending = true;
                Hint("Resolución ajustada a esta ventana mientras controlas.");
            }
            else
            {
                _back.Send(ResolutionRequest.Packet(0, 0));
                Hint("El otro equipo vuelve a su resolución.");
            }
            SyncButtons();
        }

        /// <summary>Pide al otro equipo el tamaño de esta ventana en píxeles reales.</summary>
        private void SendFit()
        {
            if (!_fit || _back == null || _mode != ViewerMode.Control || ActualWidth < 16 || ActualHeight < 16) return;
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            int width = (int)Math.Round(ActualWidth * dpi.DpiScaleX), height = (int)Math.Round(ActualHeight * dpi.DpiScaleY);
            _back.Send(ResolutionRequest.Packet(width, height));
        }



        private void Hint(string text)
        {
            _hint.Text = text;
            Fade(_hintBox, 1);
            _hintTimer.Stop();
            _hintTimer.Start();
        }

        // ── Puntero colaborativo: mientras dibujo o controlo, los demás ven el mío con mi nombre ──

        private bool SharesPointer { get { return _back != null && _bitmap != null && _sharerVersion >= ScreenShareSession.PointerVersion && _mode != ViewerMode.Watch; } }

        private void TrackPointer(MouseEventArgs e)
        {
            if (!SharesPointer) return;
            ushort x, y;
            if (!Position(e, out x, out y)) { HidePointer(); return; }
            _pointerX = x;
            _pointerY = y;
            _pointerPending = true;
            // Como mucho unas 30 posiciones por segundo; la última siempre sale.
            if (!_pointerTimer.IsEnabled) { FlushPointer(); _pointerTimer.Start(); }
        }

        private void FlushPointer()
        {
            if (!_pointerPending) { _pointerTimer.Stop(); return; }
            _pointerPending = false;
            if (!SharesPointer) return;
            _pointerShown = true;
            SendPointer(true, _pointerX, _pointerY);
        }

        private void HidePointer()
        {
            _pointerPending = false;
            _pointerTimer.Stop();
            if (!_pointerShown) return;
            _pointerShown = false;
            if (_back != null) SendPointer(false, 0, 0);
        }

        private void SendPointer(bool visible, ushort x, ushort y)
        {
            _back.Send(new[] { (byte)'M', (byte)(visible ? 1 : 0), (byte)x, (byte)(x >> 8), (byte)y, (byte)(y >> 8) });
        }

        /// <summary>Para pruebas: lo mismo que mover el mouse sobre la imagen en un punto normalizado.</summary>
        internal void MovePointer(double x, double y)
        {
            if (!SharesPointer) return;
            _pointerShown = true;
            SendPointer(true, AnnotationOp.Norm(x), AnnotationOp.Norm(y));
        }

        // ── Control remoto: mouse y teclado van a quien comparte ─────────────

        private bool Controlling { get { return _mode == ViewerMode.Control && _back != null && _bitmap != null; } }

        private bool Position(MouseEventArgs e, out ushort x, out ushort y)
        {
            Point p = e.GetPosition(_surface);
            x = AnnotationOp.Norm(p.X / Math.Max(1, _surface.Width));
            y = AnnotationOp.Norm(p.Y / Math.Max(1, _surface.Height));
            return p.X >= 0 && p.Y >= 0 && p.X <= _surface.Width && p.Y <= _surface.Height;
        }

        private static byte ButtonCode(MouseButton button)
        {
            return button == MouseButton.Left ? (byte)1 : button == MouseButton.Right ? (byte)2 : button == MouseButton.Middle ? (byte)3 : (byte)0;
        }

        private void OnSurfaceDown(object sender, MouseButtonEventArgs e)
        {
            if (!Controlling) return;
            e.Handled = true;
            byte code = ButtonCode(e.ChangedButton);
            ushort x, y;
            Position(e, out x, out y);
            if (code == 0) return;
            _surface.CaptureMouse();
            _buttonsDown.Add(code);
            _back.Send(new RemoteInputEvent { Type = RemoteInputEvent.Down, Button = code, X = x, Y = y }.ToBytes());
        }

        private void OnSurfaceUp(object sender, MouseButtonEventArgs e)
        {
            if (!Controlling) return;
            e.Handled = true;
            byte code = ButtonCode(e.ChangedButton);
            ushort x, y;
            Position(e, out x, out y);
            if (code == 0 || !_buttonsDown.Remove(code)) return;
            _back.Send(new RemoteInputEvent { Type = RemoteInputEvent.Up, Button = code, X = x, Y = y }.ToBytes());
            if (_buttonsDown.Count == 0 && _surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        }

        private void OnSurfaceMove(object sender, MouseEventArgs e)
        {
            if (!Controlling) return;
            ushort x, y;
            if (!Position(e, out x, out y) && _buttonsDown.Count == 0) return;
            if (x == _lastX && y == _lastY) return;
            _lastX = x; _lastY = y;
            _back.Send(new RemoteInputEvent { Type = RemoteInputEvent.Move, X = x, Y = y }.ToBytes());
        }

        private void OnSurfaceWheel(object sender, MouseWheelEventArgs e)
        {
            if (!Controlling) return;
            e.Handled = true;
            ushort x, y;
            Position(e, out x, out y);
            _back.Send(new RemoteInputEvent { Type = RemoteInputEvent.Wheel, Delta = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, e.Delta)), X = x, Y = y }.ToBytes());
        }

        private void OnPreviewKey(object sender, KeyEventArgs e)
        {
            if (!Controlling) return;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.DeadCharProcessed) key = e.DeadCharProcessedKey;
            if (key == Key.ImeProcessed) key = e.ImeProcessedKey;
            // F11 sigue siendo de esta ventana: pantalla completa.
            if (key == Key.F11) return;
            e.Handled = true;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk <= 0 || vk > 0xFE) return;
            bool down = e.IsDown;
            if (down) _keysDown.Add(vk);
            else if (!_keysDown.Remove(vk)) return;
            _back.Send(new RemoteInputEvent { Type = down ? RemoteInputEvent.KeyDown : RemoteInputEvent.KeyUp, Key = (ushort)vk }.ToBytes());
        }

        private void ReleaseKeys()
        {
            if (_back != null)
                foreach (int vk in _keysDown) _back.Send(new RemoteInputEvent { Type = RemoteInputEvent.KeyUp, Key = (ushort)vk }.ToBytes());
            _keysDown.Clear();
        }

        private void ReleaseButtons()
        {
            if (_back != null)
                foreach (byte code in _buttonsDown) _back.Send(new RemoteInputEvent { Type = RemoteInputEvent.Up, Button = code, X = _lastX, Y = _lastY }.ToBytes());
            _buttonsDown.Clear();
            if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        }

        public void Ended(string reason)
        {
            SetMode(ViewerMode.Watch);
            _drawButton.Visibility = Visibility.Collapsed;
            _controlButton.Visibility = Visibility.Collapsed;
            _fitButton.Visibility = Visibility.Collapsed;
            _fit = false;
            _fitPending = false;
            _fitTimer.Stop();
            _firstFitTimer.Stop();
            _back = null;
            _center.Text = reason;
            _center.Visibility = Visibility.Visible;
            _center.Foreground = Brushes.White;
            _cursor.Visibility = Visibility.Collapsed;
            Pointers.HideAll();
            _image.Opacity = 0.35;
            Fade(_bar, 1);
            _idle.Stop();
            HasEnded = true;
            Raise(EndedChanged);
        }
    }

    /// <summary>Pantalla completa de una ventana: sin marco, maximizada y encima, y vuelta atrás.</summary>
    internal sealed class FullScreenToggle
    {
        private readonly Window _window;
        private WindowState _restore;
        public bool IsFull { get; private set; }

        public FullScreenToggle(Window window)
        {
            _window = window;
        }

        public void Toggle()
        {
            if (!IsFull)
            {
                _restore = _window.WindowState;
                _window.WindowStyle = WindowStyle.None;
                _window.WindowState = WindowState.Normal;
                _window.WindowState = WindowState.Maximized;
                _window.Topmost = true;
            }
            else
            {
                _window.Topmost = false;
                _window.WindowStyle = WindowStyle.SingleBorderWindow;
                _window.WindowState = _restore;
            }
            IsFull = !IsFull;
        }
    }

    /// <summary>Una sola pantalla compartida en su propia ventana.</summary>
    internal sealed class ScreenViewerWindow : Window
    {
        private readonly FullScreenToggle _full;
        public ScreenViewerView View { get; private set; }

        public ScreenViewerWindow(string sender, string source)
        {
            Title = "Lazo · pantalla de " + sender;
            Rect area = SystemParameters.WorkArea;
            Width = Math.Max(640, area.Width * 0.72);
            Height = Math.Max(420, area.Height * 0.78);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.Color("#0F0A0B");
            try { Icon = BitmapFrame.Create(new Uri(System.Reflection.Assembly.GetEntryAssembly().Location)); } catch { }
            _full = new FullScreenToggle(this);
            View = new ScreenViewerView(sender, source);
            View.FullRequested += _full.Toggle;
            View.SpotlightRequested += _full.Toggle;
            View.CloseRequested += Close;
            Content = View;
            KeyDown += (s, e) => { if (e.Key == Key.Escape && _full.IsFull) { _full.Toggle(); e.Handled = true; } };
            Deactivated += (s, e) => View.ReleaseInput();
            Closed += (s, e) => View.Close();
        }

        internal AnnotationLayer Layer { get { return View.Layer; } }
        internal PointerLayer Pointers { get { return View.Pointers; } }
        internal byte Granted { get { return View.Granted; } }
        internal bool CanAsk { get { return View.CanAsk; } }
        internal double Fps { get { return View.Fps; } }
        internal BitmapSource Frame { get { return View.Frame; } }
        internal void Request(byte kind) { View.Request(kind); }
        internal void ToggleControl() { View.ToggleControl(); }
        internal void MovePointer(double x, double y) { View.MovePointer(x, y); }
    }

    /// <summary>
    /// Varias pantallas compartidas a la vez en una sola ventana, como en Discord: en mosaico, o una destacada
    /// con las demás en una tira. Doble clic (o un clic en la tira) destaca una; F11 la pone a pantalla completa.
    /// </summary>
    internal sealed class ScreenStageWindow : Window
    {
        private readonly List<ScreenViewerView> _views = new List<ScreenViewerView>();
        private readonly Grid _content;
        private readonly TextBlock _count;
        private readonly Button _mosaicButton;
        private readonly Button _focusButton;
        private readonly FullScreenToggle _full;
        private ScreenViewerView _spotlight;
        private bool _mosaic = true;

        public ScreenStageWindow()
        {
            Title = "Lazo · pantallas compartidas";
            Rect area = SystemParameters.WorkArea;
            Width = Math.Max(720, area.Width * 0.78);
            Height = Math.Max(460, area.Height * 0.82);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.Color("#0B0708");
            FontFamily = Theme.Font;
            try { Icon = BitmapFrame.Create(new Uri(System.Reflection.Assembly.GetEntryAssembly().Location)); } catch { }
            _full = new FullScreenToggle(this);

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            Grid head = new Grid { Margin = new Thickness(14, 8, 10, 6) };
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Theme.Color("#F25C45"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            title.Children.Add(Theme.Text("Pantallas compartidas", 13, Brushes.White, FontWeights.SemiBold));
            _count = Theme.Text("", 12, Theme.Color("#B9A9AA"));
            _count.Margin = new Thickness(8, 0, 0, 0);
            _count.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(_count);
            head.Children.Add(title);
            StackPanel modes = new StackPanel { Orientation = Orientation.Horizontal };
            _mosaicButton = ModeButton("Mosaico", () => SetMosaic(true));
            _focusButton = ModeButton("Destacada", () => SetMosaic(false));
            modes.Children.Add(_mosaicButton);
            modes.Children.Add(_focusButton);
            Grid.SetColumn(modes, 1);
            head.Children.Add(modes);
            root.Children.Add(head);
            _content = new Grid { Margin = new Thickness(8, 0, 8, 8) };
            Grid.SetRow(_content, 1);
            root.Children.Add(_content);
            Content = root;

            KeyDown += (s, e) => { if (e.Key == Key.Escape && _full.IsFull) { _full.Toggle(); e.Handled = true; } };
            Deactivated += (s, e) => { foreach (ScreenViewerView view in _views) view.ReleaseInput(); };
            Closed += (s, e) => { foreach (ScreenViewerView view in _views.ToArray()) view.Close(); _views.Clear(); };
        }

        public int Count { get { return _views.Count; } }
        public bool IsMosaic { get { return _mosaic; } }
        internal IList<ScreenViewerView> Views { get { return _views.AsReadOnly(); } }
        internal ScreenViewerView Spotlight { get { return _spotlight; } }

        /// <summary>Añade una pantalla; si es la segunda o más, se ven en mosaico.</summary>
        public void Add(ScreenViewerView view)
        {
            _views.Add(view);
            view.CloseRequested += () => Remove(view);
            view.SpotlightRequested += () => { _spotlight = view; SetMosaic(false); };
            view.FullRequested += () =>
            {
                _spotlight = view;
                if (_views.Count > 1) _mosaic = false;
                Layout();
                _full.Toggle();
            };
            // Si una transmisión termina y hay otras, se retira sola al poco, como en Discord.
            view.EndedChanged += () =>
            {
                if (_views.Count < 2) return;
                DispatcherTimer later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                later.Tick += (s, e) => { later.Stop(); if (_views.Contains(view) && _views.Count > 1) Remove(view); };
                later.Start();
            };
            if (_spotlight == null) _spotlight = view;
            Layout();
        }

        public void Remove(ScreenViewerView view)
        {
            if (!_views.Remove(view)) return;
            Detach(view);
            view.Close();
            if (_spotlight == view) _spotlight = _views.FirstOrDefault();
            if (_views.Count == 0) { Close(); return; }
            Layout();
        }

        /// <summary>Para pruebas y para el botón: mosaico o una destacada.</summary>
        internal void SetMosaic(bool mosaic)
        {
            _mosaic = mosaic;
            Layout();
        }

        private void Layout()
        {
            foreach (ScreenViewerView view in _views) Detach(view);
            _content.Children.Clear();
            _content.RowDefinitions.Clear();
            int count = _views.Count;
            _count.Text = count == 1 ? "· 1 pantalla" : "· " + count + " pantallas";
            bool mosaic = _mosaic || count < 2;
            _mosaicButton.Visibility = _focusButton.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
            _mosaicButton.Background = _mosaic ? Theme.Color("#40FFFFFF") : Brushes.Transparent;
            _focusButton.Background = !_mosaic ? Theme.Color("#40FFFFFF") : Brushes.Transparent;
            if (mosaic)
            {
                int columns = (int)Math.Ceiling(Math.Sqrt(count));
                int rows = (int)Math.Ceiling(count / (double)columns);
                System.Windows.Controls.Primitives.UniformGrid grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = columns, Rows = rows };
                foreach (ScreenViewerView view in _views) grid.Children.Add(Tile(view, false));
                _content.Children.Add(grid);
                return;
            }
            ScreenViewerView main = _spotlight != null && _views.Contains(_spotlight) ? _spotlight : _views[0];
            _content.RowDefinitions.Add(new RowDefinition());
            _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(130) });
            _content.Children.Add(Tile(main, false));
            StackPanel strip = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (ScreenViewerView view in _views)
                if (view != main) strip.Children.Add(Tile(view, true));
            ScrollViewer scroll = new ScrollViewer
            {
                Content = strip,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 6, 0, 0)
            };
            Grid.SetRow(scroll, 1);
            _content.Children.Add(scroll);
        }

        private Border Tile(ScreenViewerView view, bool small)
        {
            Border tile = new Border
            {
                Margin = new Thickness(4),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Background = Theme.Color("#0F0A0B"),
                ClipToBounds = true
            };
            view.Compact = small;
            if (!small)
            {
                tile.Child = view;
                return tile;
            }
            // Miniatura: la imagen y el nombre de quien comparte, como en Discord.
            Grid face = new Grid();
            face.Children.Add(view);
            TextBlock name = Theme.Text(view.Sender, 11.5, Brushes.White, FontWeights.SemiBold);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextWrapping = TextWrapping.NoWrap;
            face.Children.Add(new Border
            {
                Background = Theme.Color("#B3000000"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 2, 8, 3),
                Margin = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                Child = name
            });
            tile.Child = face;
            {
                tile.Width = 200;
                tile.ToolTip = "Destacar a " + view.Sender;
                tile.Cursor = Cursors.Hand;
                // En la tira, un clic la destaca; no llega a la pantalla pequeña.
                tile.PreviewMouseLeftButtonDown += (s, e) => { e.Handled = true; _spotlight = view; Layout(); };
                tile.MouseEnter += (s, e) => tile.BorderBrush = Theme.Color("#80FFFFFF");
                tile.MouseLeave += (s, e) => tile.BorderBrush = Brushes.Transparent;
            }
            return tile;
        }

        private static void Detach(ScreenViewerView view)
        {
            Border border = view.Parent as Border;
            if (border != null) border.Child = null;
            Panel panel = view.Parent as Panel;
            if (panel != null) panel.Children.Remove(view);
        }

        private static Button ModeButton(string text, Action click)
        {
            Button button = Theme.Button(text, false);
            button.MinHeight = 28;
            button.FontSize = 12;
            button.Foreground = Brushes.White;
            button.BorderThickness = new Thickness(0);
            button.Background = Brushes.Transparent;
            button.Margin = new Thickness(4, 0, 0, 0);
            button.Click += (s, e) => click();
            return button;
        }
    }

    /// <summary>Aviso discreto abajo a la derecha: alguien quiere compartir su pantalla.</summary>
    internal sealed class ScreenInviteWindow : Window
    {
        private readonly Action<bool> _respond;
        private bool _answered;

        public ScreenInviteWindow(ScreenInvite invite, Action<bool> respond)
        {
            _respond = respond;
            Title = "Lazo · pantalla compartida";
            Width = 360;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowActivated = false;
            ShowInTaskbar = false;
            FontFamily = Theme.Font;

            StackPanel body = new StackPanel();
            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            Border icon = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(20),
                Background = Theme.Primary,
                Child = Icons.Make("screen", 20, Theme.PrimaryText, 2)
            };
            head.Children.Add(icon);
            StackPanel words = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock who = Theme.Text(invite.Sender + " quiere compartir su pantalla", 13, Theme.Ink, FontWeights.SemiBold);
            words.Children.Add(who);
            TextBlock what = Theme.Text(string.IsNullOrEmpty(invite.Source) ? invite.Address.ToString() : invite.Source + " · " + invite.Address, 11.5, Theme.Muted);
            what.TextTrimming = TextTrimming.CharacterEllipsis;
            what.TextWrapping = TextWrapping.NoWrap;
            words.Children.Add(what);
            Grid.SetColumn(words, 1);
            head.Children.Add(words);
            body.Children.Add(head);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            Button later = Theme.Button("Ahora no", false);
            later.Click += (s, e) => Answer(false);
            Button watch = Theme.Button("Ver pantalla", true);
            watch.Margin = new Thickness(8, 0, 0, 0);
            watch.Click += (s, e) => Answer(true);
            buttons.Children.Add(later);
            buttons.Children.Add(watch);
            body.Children.Add(buttons);
            Content = new Border
            {
                Margin = new Thickness(10),
                Background = Theme.ShellSurface(),
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(14, 14, 14, 12),
                Child = body
            };
            DispatcherTimer timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            timeout.Tick += (s, e) => { timeout.Stop(); Answer(false); };
            timeout.Start();
            Loaded += (s, e) => WindowPlacement.PlaceBottomRight(this);
            Closed += (s, e) => { timeout.Stop(); Answer(false); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Answer(false); };
        }

        private void Answer(bool accept)
        {
            if (_answered) return;
            _answered = true;
            _respond(accept);
            if (IsVisible) Close();
        }
    }

    /// <summary>Barra flotante mientras se comparte. Se excluye de la captura para no aparecer en la transmisión.</summary>
    internal sealed class ShareBarWindow : Window
    {
        private readonly TextBlock _text;
        private readonly ScreenOverlayWindow _overlay;
        private readonly Button _drawButton;
        private readonly Button _revokeButton;
        private readonly AnnotationToolbar _tools;
        private Button _stopButton;

        public ShareBarWindow(Action stop) : this(stop, null, null)
        {
        }

        /// <summary>Con <paramref name="overlay"/>, la barra permite dibujar; con <paramref name="revoke"/>, quitar permisos.</summary>
        public ShareBarWindow(Action stop, ScreenOverlayWindow overlay, Action revoke)
        {
            _overlay = overlay;
            Title = "Lazo · compartiendo";
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowActivated = false;
            ShowInTaskbar = false;
            FontFamily = Theme.Font;

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
            System.Windows.Shapes.Ellipse dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = Theme.Color("#F25C45"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
            dot.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.9)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            row.Children.Add(dot);
            _text = Theme.Text("Compartiendo…", 12, Brushes.White, FontWeights.SemiBold);
            _text.TextWrapping = TextWrapping.NoWrap;
            _text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(_text);
            if (overlay != null)
            {
                _drawButton = Theme.Button("Dibujar", false);
                _drawButton.MinHeight = 28;
                _drawButton.Margin = new Thickness(14, 0, 0, 0);
                _drawButton.Content = DrawLabel(false);
                _drawButton.ToolTip = "Escribir sobre la pantalla (Esc para terminar)";
                _drawButton.Click += (s, e) => ToggleDrawing();
                row.Children.Add(_drawButton);
                overlay.DrawingEnded += () => SyncDrawing();
            }
            if (revoke != null)
            {
                _revokeButton = Theme.Button("Quitar permisos", false);
                _revokeButton.MinHeight = 28;
                _revokeButton.Margin = new Thickness(8, 0, 0, 0);
                _revokeButton.ToolTip = "Nadie podrá dibujar ni controlar tu equipo hasta que vuelvas a permitirlo (Ctrl+Alt+Q)";
                _revokeButton.Visibility = Visibility.Collapsed;
                _revokeButton.Click += (s, e) => revoke();
                row.Children.Add(_revokeButton);
            }
            if (overlay != null)
            {
                // Quien comparte puede quitar de una vez todos los dibujos sobre su pantalla, de cualquiera.
                Button clear = Theme.Button("", false);
                clear.Content = ClearLabel();
                clear.MinHeight = 28;
                clear.Margin = new Thickness(8, 0, 0, 0);
                clear.ToolTip = "Borrar todos los dibujos de tu pantalla, también los de quienes miran";
                clear.Visibility = Visibility.Collapsed;
                clear.Click += (s, e) => overlay.Layer.ClearAll();
                overlay.Layer.StrokesChanged += () => clear.Visibility = overlay.Layer.HasStrokes ? Visibility.Visible : Visibility.Collapsed;
                row.Children.Add(clear);
            }
            Button stopButton = Theme.Button("Detener", true);
            _stopButton = stopButton;
            stopButton.MinHeight = 28;
            stopButton.Margin = new Thickness(14, 0, 0, 0);
            { stopButton.Background = Theme.Danger; stopButton.BorderBrush = Theme.Danger; stopButton.Foreground = Brushes.White; }
            stopButton.ToolTip = "Dejar de compartir (Ctrl+Alt+X)";
            stopButton.Click += (s, e) => stop();
            row.Children.Add(stopButton);
            StackPanel rows = new StackPanel();
            rows.Children.Add(row);
            if (overlay != null)
            {
                _tools = new AnnotationToolbar(overlay.Layer, "Borrar todos los trazos", () => overlay.Layer.ClearAll())
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 6, 9, 2),
                    Visibility = Visibility.Collapsed,
                    Cursor = Cursors.Arrow
                };
                rows.Children.Add(_tools);
            }
            Border shell = new Border
            {
                Margin = new Thickness(6),
                Background = Theme.Color("#F22A0E12"),
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(14, 5, 5, 5),
                Child = rows,
                Cursor = Cursors.SizeAll
            };
            shell.MouseLeftButtonDown += (s, e) =>
            {
                // Las herramientas reciben el clic; arrastrar solo desde el resto de la barra.
                if (_tools != null && _tools.IsMouseOver) return;
                try { DragMove(); } catch { }
            };
            Content = shell;
            SourceInitialized += (s, e) =>
            {
                try { Native.SetWindowDisplayAffinity(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x11); } catch { }
            };
            Loaded += (s, e) =>
            {
                Rect area = SystemParameters.WorkArea;
                Left = area.Left + (area.Width - ActualWidth) / 2;
                Top = area.Top + 8;
            };
        }

        public void Update(string text)
        {
            _text.Text = text;
        }

        /// <summary>Sesión de control remoto con contraseña: el aviso lo dice y el botón pasa a «Terminar».</summary>
        public void SetRemote()
        {
            Title = "Lazo · control remoto";
            if (_stopButton == null) return;
            _stopButton.Content = "Terminar";
            _stopButton.ToolTip = "Terminar el control remoto (Ctrl+Alt+X)";
        }

        /// <summary>Mientras alguien controla el equipo, quien comparte no puede activar el dibujo.</summary>
        public void SetDrawAllowed(bool allowed)
        {
            if (_drawButton == null) return;
            _drawButton.IsEnabled = allowed;
            _drawButton.ToolTip = allowed ? "Escribir sobre la pantalla (Esc para terminar)" : "Quita el control para poder dibujar";
        }

        /// <summary>Muestra «Quitar permisos» mientras alguien puede dibujar o controlar.</summary>
        public void SetGranted(bool any)
        {
            if (_revokeButton != null) _revokeButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ToggleDrawing()
        {
            if (_overlay == null) return;
            _overlay.SetDrawing(!_overlay.Drawing);
            SyncDrawing();
            // Al activarse, la capa de dibujo sube; la barra debe quedar encima para poder usarla.
            if (_overlay.Drawing) WindowOrder.RaiseTopmost(this);
        }

        private void SyncDrawing()
        {
            if (_drawButton == null) return;
            bool on = _overlay.Drawing;
            _drawButton.Content = DrawLabel(on);
            _drawButton.Background = on ? Theme.Primary : Theme.SoftSurface;
            _drawButton.Foreground = on ? Theme.PrimaryText : Theme.Ink;
            _tools.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        private static UIElement DrawLabel(bool on)
        {
            StackPanel label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(Icons.Make("pen", 14, on ? Theme.PrimaryText : Theme.Ink, 2));
            TextBlock text = new TextBlock { Text = on ? "Terminar" : "Dibujar", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(text);
            return label;
        }

        private static UIElement ClearLabel()
        {
            StackPanel label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(Icons.Make("trash", 14, Theme.Ink, 2));
            label.Children.Add(new TextBlock { Text = "Borrar dibujos", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            return label;
        }
    }
}
