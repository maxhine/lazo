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
    /// Transmisión de pantalla hacia uno o varios compañeros.
    /// Protocolo tras aceptar LAZOS: 'S' ancho alto · 'F' cursorX cursorY n [x y w h len jpeg]… · 'K' · 'E'.
    /// El ritmo lo marca el receptor más lento: no hay colas, así que la latencia se mantiene mínima.
    /// </summary>
    internal sealed class ScreenShareSession
    {
        private sealed class Viewer
        {
            public string Name;
            public TcpClient Client;
            public NetworkStream Stream;
        }

        private readonly NetworkEngine _network;
        private readonly object _lock = new object();
        private readonly List<Viewer> _viewers = new List<Viewer>();
        private volatile bool _running = true;
        private int _key = 1;
        private int _pending;
        private Thread _worker;

        public ShareSource Source { get; private set; }
        public double Fps { get; private set; }
        public double Mbps { get; private set; }
        public double WorkMs { get; private set; }
        public double CaptureMs { get; private set; }
        public event Action Changed;
        public event Action<string> Notice;
        public event Action Ended;

        public ScreenShareSession(NetworkEngine network, ShareSource source)
        {
            _network = network;
            Source = source;
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
                    TcpClient client = await _network.OpenScreenAsync(peer, Source.Label).ConfigureAwait(false);
                    if (!_running) { client.Close(); return; }
                    lock (_lock) _viewers.Add(new Viewer { Name = peer.Name, Client = client, Stream = client.GetStream() });
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

        public void Start()
        {
            _worker = new Thread(Loop) { IsBackground = true, Name = "Lazo screen share", Priority = ThreadPriority.AboveNormal };
            _worker.Start();
        }

        public void Stop() { _running = false; }

        private volatile bool _idle;
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
                if (grabber != null) grabber.Join(2000);
                timeEndPeriod(1);
                if (slots != null) foreach (FrameCapturer slot in slots) if (slot != null) slot.Dispose();
                encoder.Dispose();
                Viewer[] rest;
                lock (_lock) { rest = _viewers.ToArray(); _viewers.Clear(); }
                foreach (Viewer viewer in rest)
                {
                    try { viewer.Stream.Write(new[] { (byte)'E' }, 0, 1); } catch { }
                    try { viewer.Client.Close(); } catch { }
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
                catch { ok = false; }
                if (!ok) free[i].Release();
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
            Action<Viewer> write = viewer =>
            {
                try { viewer.Stream.Write(buffer, 0, length); }
                catch
                {
                    lock (_lock) _viewers.Remove(viewer);
                    try { viewer.Client.Close(); } catch { }
                    Say(viewer.Name + " dejó de ver tu pantalla");
                    Raise(Changed);
                }
            };
            if (targets.Length == 1) write(targets[0]);
            else Parallel.ForEach(targets, write);
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
        private readonly ScreenViewerWindow _window;
        private volatile bool _closed;

        public ScreenViewer(ScreenViewerWindow window)
        {
            _window = window;
        }

        private struct Decoded
        {
            public int X, Y, W, H;
            public byte[] Pixels;
        }

        public void Run(Stream network)
        {
            Dispatcher ui = _window.Dispatcher;
            ui.Invoke((Action)(() => _window.Closed += (s, e) =>
            {
                _closed = true;
                try { network.Close(); } catch { }
            }));
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
                    ui.Invoke((Action)(() => _window.Apply(tiles.Select(t => new ScreenViewerWindow.Patch { X = t.X, Y = t.Y, W = t.W, H = t.H, Pixels = t.Pixels }).ToArray(), cx, cy, count > 0)),
                        DispatcherPriority.Render);
                }
            }
            catch (Exception ex)
            {
                if (!_closed) reason = ex is IOException || ex is EndOfStreamException ? "Se perdió la conexión." : "Error: " + ex.Message;
            }
            finally
            {
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

    internal sealed class ScreenViewerWindow : Window
    {
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
        private WriteableBitmap _bitmap;
        private int _frames;
        private DateTime _statStart = DateTime.UtcNow;
        private double _fps;
        private WindowState _restoreState;
        private bool _full;

        public ScreenViewerWindow(string sender, string source)
        {
            _title = sender + (string.IsNullOrEmpty(source) ? "" : " · " + source);
            Title = "Lazo · pantalla de " + sender;
            Rect area = SystemParameters.WorkArea;
            Width = Math.Max(640, area.Width * 0.72);
            Height = Math.Max(420, area.Height * 0.78);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.Color("#0F0A0B");
            FontFamily = Theme.Font;
            try { Icon = BitmapFrame.Create(new Uri(System.Reflection.Assembly.GetEntryAssembly().Location)); } catch { }

            Grid root = new Grid();
            Content = root;
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
            _surface = new Grid();
            _surface.Children.Add(_image);
            _surface.Children.Add(overlay);
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
            pill.Children.Add(BarButton("expand", "Pantalla completa (F11)", ToggleFull));
            pill.Children.Add(BarButton("close", "Cerrar", Close));
            _bar = new Border
            {
                Background = Theme.Color("#CC2A0E12"),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(14, 4, 6, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 12, 0, 0),
                Child = pill
            };
            root.Children.Add(_bar);

            _idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _idle.Tick += (s, e) => { _idle.Stop(); if (_bitmap != null) Fade(_bar, 0); };
            MouseMove += (s, e) => { Fade(_bar, 1); _idle.Stop(); _idle.Start(); };
            MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) ToggleFull(); };
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.F11) { ToggleFull(); e.Handled = true; }
                else if (e.Key == Key.Escape && _full) { ToggleFull(); e.Handled = true; }
            };
            _idle.Start();
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
            frame.SetValue(Border.BackgroundProperty, Brushes.Transparent);
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

        private void ToggleFull()
        {
            if (!_full)
            {
                _restoreState = WindowState;
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
                Topmost = true;
            }
            else
            {
                Topmost = false;
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = _restoreState;
            }
            _full = !_full;
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
            _center.Visibility = Visibility.Collapsed;
        }

        public void Apply(Patch[] patches, int cursorX, int cursorY, bool changed)
        {
            if (_bitmap == null) return;
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
            if (cursorX >= 0 && cursorY >= 0)
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

        public void Ended(string reason)
        {
            _center.Text = reason;
            _center.Visibility = Visibility.Visible;
            _center.Foreground = Brushes.White;
            _cursor.Visibility = Visibility.Collapsed;
            _image.Opacity = 0.35;
            Fade(_bar, 1);
            _idle.Stop();
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

        public ShareBarWindow(Action stop)
        {
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
            Button stopButton = Theme.Button("Detener", true);
            stopButton.MinHeight = 28;
            stopButton.Margin = new Thickness(14, 0, 0, 0);
            { stopButton.Background = Theme.Danger; stopButton.BorderBrush = Theme.Danger; stopButton.Foreground = Brushes.White; }
            stopButton.Click += (s, e) => stop();
            row.Children.Add(stopButton);
            Border shell = new Border
            {
                Margin = new Thickness(6),
                Background = Theme.Color("#F22A0E12"),
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(14, 5, 5, 5),
                Child = row,
                Cursor = Cursors.SizeAll
            };
            shell.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
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
    }
}
