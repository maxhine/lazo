using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lazo
{
    /// <summary>Permisos que quien comparte concede a cada persona que mira.</summary>
    internal static class SharePermission
    {
        public const byte Draw = 1;
        public const byte Control = 2;
        /// <summary>Junto con <see cref="Control"/>: control total del equipo. Sin él, solo la ventana activa.</summary>
        public const byte FullControl = 4;
        /// <summary>Solo para el aviso local: alguien que no mira pide unirse. No viaja en el canal de retorno.</summary>
        public const byte Watch = 0x20;
        /// <summary>Solo para el aviso local: alguien pide que compartas tu pantalla.</summary>
        public const byte AskShare = 0x10;
        /// <summary>Se suma al tipo de una petición para renunciar a ese permiso.</summary>
        public const byte Release = 0x80;
    }

    /// <summary>
    /// Envío de quien mira hacia quien comparte (peticiones, trazos, mouse y teclado) en su propio hilo,
    /// para que una red lenta no congele la ventana. Los paquetes salen en el orden en que se piden.
    /// Nunca se descarta nada salvo movimientos del mouse repetidos: si la red va lenta, varios movimientos
    /// seguidos se funden en el último, pero soltar una tecla o un botón siempre llega.
    /// </summary>
    internal sealed class ScreenBackChannel
    {
        private readonly Stream _stream;
        private readonly LinkedList<byte[]> _queue = new LinkedList<byte[]>();
        private bool _closed;

        public ScreenBackChannel(Stream stream)
        {
            _stream = stream;
            new System.Threading.Thread(Pump) { IsBackground = true, Name = "Lazo screen back-channel" }.Start();
        }

        public void Send(byte[] packet)
        {
            lock (_queue)
            {
                if (_closed) return;
                if (IsMove(packet) && _queue.Last != null && IsMove(_queue.Last.Value) && _queue.Last.Value[0] == packet[0]) _queue.Last.Value = packet;
                else _queue.AddLast(packet);
                System.Threading.Monitor.Pulse(_queue);
            }
        }

        /// <summary>Movimientos que se pueden sustituir por el último: del mouse controlado o del puntero colaborativo.</summary>
        private static bool IsMove(byte[] packet)
        {
            return packet.Length >= 2 && ((packet[0] == (byte)'I' && packet[1] == RemoteInputEvent.Move) || packet[0] == (byte)'M');
        }

        public void Close()
        {
            lock (_queue)
            {
                _closed = true;
                System.Threading.Monitor.Pulse(_queue);
            }
        }

        private void Pump()
        {
            try
            {
                while (true)
                {
                    List<byte[]> batch = new List<byte[]>();
                    lock (_queue)
                    {
                        while (_queue.Count == 0 && !_closed) System.Threading.Monitor.Wait(_queue);
                        if (_queue.Count == 0 && _closed) return;
                        batch.AddRange(_queue);
                        _queue.Clear();
                    }
                    foreach (byte[] packet in batch) _stream.Write(packet, 0, packet.Length);
                    _stream.Flush();
                }
            }
            catch { Close(); }
        }
    }

    /// <summary>
    /// Evento de mouse o teclado enviado por quien controla. Las coordenadas van normalizadas (0–65535)
    /// sobre la fuente compartida. Tamaño fijo por tipo: 1 mover · 2/3 botón · 4 rueda · 5/6 tecla.
    /// </summary>
    internal struct RemoteInputEvent
    {
        public const byte Move = 1, Down = 2, Up = 3, Wheel = 4, KeyDown = 5, KeyUp = 6;
        public byte Type;
        public byte Button;
        public ushort X, Y;
        public short Delta;
        public ushort Key;

        public byte[] ToBytes()
        {
            using (MemoryStream buffer = new MemoryStream(8))
            using (BinaryWriter writer = new BinaryWriter(buffer))
            {
                writer.Write((byte)'I');
                writer.Write(Type);
                switch (Type)
                {
                    case Move: writer.Write(X); writer.Write(Y); break;
                    case Down:
                    case Up: writer.Write(Button); writer.Write(X); writer.Write(Y); break;
                    case Wheel: writer.Write(Delta); writer.Write(X); writer.Write(Y); break;
                    default: writer.Write(Key); break;
                }
                writer.Flush();
                return buffer.ToArray();
            }
        }

        /// <summary>Lee el cuerpo tras el tipo 'I'. Devuelve false si el tipo no se conoce.</summary>
        public static bool Read(BinaryReader reader, out RemoteInputEvent input)
        {
            input = new RemoteInputEvent { Type = reader.ReadByte() };
            switch (input.Type)
            {
                case Move: input.X = reader.ReadUInt16(); input.Y = reader.ReadUInt16(); return true;
                case Down:
                case Up: input.Button = reader.ReadByte(); input.X = reader.ReadUInt16(); input.Y = reader.ReadUInt16(); return true;
                case Wheel: input.Delta = reader.ReadInt16(); input.X = reader.ReadUInt16(); input.Y = reader.ReadUInt16(); return true;
                case KeyDown:
                case KeyUp: input.Key = reader.ReadUInt16(); return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Reproduce en este equipo el mouse y el teclado de quien tiene el control, limitado al área compartida.
    /// <para><b>Ventana activa</b>: solo la aplicación compartida (o la que estaba al frente al conceder el control,
    /// si se comparte una pantalla). Los clics que caerían en otra aplicación y las teclas con otra aplicación al
    /// frente se descartan, y se bloquean las teclas Windows, Alt+Tab, Alt+Esc y Ctrl+Esc.</para>
    /// <para><b>Control total del equipo</b>: mouse dentro de lo compartido y teclado sin restricciones.</para>
    /// Recuerda botones y teclas pulsados para soltarlos al quitar el control y no dejar nada atascado.
    /// Debe usarse desde un hilo en píxeles físicos (<see cref="Native.EnterPerMonitorDpi"/>).
    /// </summary>
    internal sealed class RemoteInputInjector
    {
        /// <summary>Marca la entrada inyectada por Lazo; el atajo de teclado de Lazo la ignora.</summary>
        public static readonly IntPtr Marker = new IntPtr(0x4C415A4F);
        private readonly ShareSource _source;
        private readonly object _lock = new object();
        private readonly HashSet<ushort> _keys = new HashSet<ushort>();
        private readonly HashSet<byte> _buttons = new HashSet<byte>();
        private bool _full;
        private IntPtr _target;
        private uint _targetPid;

        public RemoteInputInjector(ShareSource source)
        {
            _source = source;
        }

        /// <summary>Modo del control recién concedido. En «ventana activa» fija la aplicación permitida.</summary>
        public void Configure(bool full)
        {
            lock (_lock)
            {
                _full = full;
                _target = full ? IntPtr.Zero : PickTarget();
                _targetPid = 0;
                if (_target != IntPtr.Zero) Native.GetWindowThreadProcessId(_target, out _targetPid);
            }
        }

        /// <summary>Título de la ventana permitida en «ventana activa», para mostrarlo a quien comparte.</summary>
        public string TargetTitle
        {
            get
            {
                lock (_lock)
                {
                    if (_full || _target == IntPtr.Zero) return null;
                    int length = Native.GetWindowTextLength(_target);
                    System.Text.StringBuilder title = new System.Text.StringBuilder(length + 1);
                    Native.GetWindowText(_target, title, title.Capacity);
                    return title.ToString();
                }
            }
        }

        private IntPtr PickTarget()
        {
            if (_source.IsWindow) return _source.Handle;
            uint self = (uint)Process.GetCurrentProcess().Id;
            IntPtr front = Root(GetForegroundWindow());
            if (Usable(front, self) && Intersects(front)) return front;
            // Lo que está al frente puede ser el propio aviso de Lazo: se toma la primera ventana debajo.
            for (IntPtr hwnd = GetTopWindow(IntPtr.Zero); hwnd != IntPtr.Zero; hwnd = Native.GetWindow(hwnd, 2))
                if (Usable(hwnd, self) && Intersects(hwnd)) return hwnd;
            return IntPtr.Zero;
        }

        private static bool Usable(IntPtr hwnd, uint self)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return false;
            if ((Native.GetWindowLong(hwnd, -20) & 0x80) != 0 || Native.GetWindowTextLength(hwnd) == 0) return false;
            int cloaked;
            if (Native.DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0) return false;
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            return pid != self;
        }

        private bool Intersects(IntPtr hwnd)
        {
            Native.RECT r;
            if (!Native.GetWindowRect(hwnd, out r)) return false;
            return r.Right > _source.Left && r.Left < _source.Left + _source.Width && r.Bottom > _source.Top && r.Top < _source.Top + _source.Height;
        }

        private static IntPtr Root(IntPtr hwnd)
        {
            return hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, 2);
        }

        private static uint ProcessOf(IntPtr hwnd)
        {
            uint pid = 0;
            if (hwnd != IntPtr.Zero) Native.GetWindowThreadProcessId(hwnd, out pid);
            return pid;
        }

        /// <summary>La aplicación permitida está al frente; si no, se intenta traer (la entrada inyectada lo permite).</summary>
        private bool TargetInFront()
        {
            if (_targetPid == 0 || !Native.IsWindow(_target)) return false;
            if (ProcessOf(GetForegroundWindow()) == _targetPid) return true;
            SetForegroundWindow(_target);
            return ProcessOf(GetForegroundWindow()) == _targetPid;
        }

        public void Apply(RemoteInputEvent input)
        {
            lock (_lock)
            {
                switch (input.Type)
                {
                    case RemoteInputEvent.Move:
                        int mx, my;
                        if (Point(input.X, input.Y, out mx, out my)) MoveTo(mx, my, 0, 0);
                        break;
                    case RemoteInputEvent.Down:
                    case RemoteInputEvent.Up:
                        uint flag = ButtonFlag(input.Button, input.Type == RemoteInputEvent.Down);
                        if (flag == 0) return;
                        int bx, by;
                        if (!Point(input.X, input.Y, out bx, out by)) return;
                        if (input.Type == RemoteInputEvent.Down)
                        {
                            if (!_full && (!Allowed(bx, by) || !TargetInFront())) return;
                            _buttons.Add(input.Button);
                        }
                        else if (!_buttons.Remove(input.Button)) return;
                        MoveTo(bx, by, flag, 0);
                        break;
                    case RemoteInputEvent.Wheel:
                        int wx, wy;
                        if (!Point(input.X, input.Y, out wx, out wy)) return;
                        if (!_full && !Allowed(wx, wy)) return;
                        MoveTo(wx, wy, MouseWheel, input.Delta);
                        break;
                    case RemoteInputEvent.KeyDown:
                        if (input.Key == 0 || input.Key > 0xFE) return;
                        if (!_full && (Blocked(input.Key) || !TargetInFront())) return;
                        _keys.Add(input.Key);
                        Key(input.Key, false);
                        break;
                    case RemoteInputEvent.KeyUp:
                        if (!_keys.Remove(input.Key)) return;
                        Key(input.Key, true);
                        break;
                }
            }
        }

        /// <summary>El punto cae en la aplicación permitida (o en sus menús y diálogos).</summary>
        private bool Allowed(int x, int y)
        {
            if (_targetPid == 0) return false;
            return ProcessOf(Root(WindowFromPoint(new POINT { X = x, Y = y }))) == _targetPid;
        }

        /// <summary>Combinaciones que sacarían el control de la ventana activa.</summary>
        private bool Blocked(ushort vk)
        {
            if (vk == 0x5B || vk == 0x5C) return true;
            bool alt = _keys.Contains(0x12) || _keys.Contains(0xA4) || _keys.Contains(0xA5);
            bool ctrl = _keys.Contains(0x11) || _keys.Contains(0xA2) || _keys.Contains(0xA3);
            if (vk == 0x09 && alt) return true;
            if (vk == 0x1B && (alt || ctrl)) return true;
            return false;
        }

        /// <summary>Suelta todo lo que quien controlaba dejó pulsado.</summary>
        public void ReleaseAll()
        {
            lock (_lock)
            {
                foreach (ushort key in _keys) Key(key, true);
                _keys.Clear();
                foreach (byte button in _buttons)
                {
                    uint flag = ButtonFlag(button, false);
                    if (flag != 0) Send(MouseInput(0, 0, flag, 0));
                }
                _buttons.Clear();
            }
        }

        /// <summary>Punto físico dentro de lo compartido.</summary>
        private bool Point(ushort nx, ushort ny, out int x, out int y)
        {
            Native.RECT area;
            x = y = 0;
            if (!Area(out area)) return false;
            int width = Math.Max(1, area.Right - area.Left), height = Math.Max(1, area.Bottom - area.Top);
            x = (int)Math.Round(area.Left + nx / 65535.0 * (width - 1));
            y = (int)Math.Round(area.Top + ny / 65535.0 * (height - 1));
            return true;
        }

        private void MoveTo(int x, int y, uint extra, int data)
        {
            int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);
            int vw = Math.Max(2, GetSystemMetrics(78)), vh = Math.Max(2, GetSystemMetrics(79));
            int ax = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
            int ay = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
            Send(MouseInput(ax, ay, MouseMove | MouseAbsolute | MouseVirtualDesk | extra, data));
        }

        /// <summary>Rectángulo físico de la fuente: el monitor o el marco visible de la ventana.</summary>
        private bool Area(out Native.RECT area)
        {
            if (!_source.IsWindow)
            {
                area = new Native.RECT { Left = _source.Left, Top = _source.Top, Right = _source.Left + _source.Width, Bottom = _source.Top + _source.Height };
                return true;
            }
            area = new Native.RECT();
            if (!Native.IsWindow(_source.Handle) || Native.IsIconic(_source.Handle)) return false;
            if (Native.DwmGetWindowAttribute(_source.Handle, 9, out area, Marshal.SizeOf(typeof(Native.RECT))) == 0) return true;
            return Native.GetWindowRect(_source.Handle, out area);
        }

        private static uint ButtonFlag(byte button, bool down)
        {
            switch (button)
            {
                case 1: return down ? 0x0002u : 0x0004u;
                case 2: return down ? 0x0008u : 0x0010u;
                case 3: return down ? 0x0020u : 0x0040u;
            }
            return 0;
        }

        private static void Key(ushort vk, bool up)
        {
            uint flags = up ? 0x0002u : 0u;
            if (IsExtended(vk)) flags |= 0x0001u;
            INPUT input = new INPUT { type = 1 };
            input.u.ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags, dwExtraInfo = Marker };
            Send(input);
        }

        private static bool IsExtended(ushort vk)
        {
            switch (vk)
            {
                case 0x21: case 0x22: case 0x23: case 0x24: case 0x25: case 0x26: case 0x27: case 0x28:
                case 0x2C: case 0x2D: case 0x2E: case 0x5B: case 0x5C: case 0x5D: case 0x6F: case 0x90:
                case 0xA3: case 0xA5:
                    return true;
            }
            return false;
        }

        private static INPUT MouseInput(int x, int y, uint flags, int data)
        {
            INPUT input = new INPUT { type = 0 };
            input.u.mi = new MOUSEINPUT { dx = x, dy = y, mouseData = data, dwFlags = flags, dwExtraInfo = Marker };
            return input;
        }

        private static void Send(INPUT input)
        {
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        private const uint MouseMove = 0x0001, MouseWheel = 0x0800, MouseVirtualDesk = 0x4000, MouseAbsolute = 0x8000;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx; public int dy; public int mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public int type; public InputUnion u; }

        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    }


    /// <summary>
    /// Capa transparente sobre lo que se comparte, donde quien comparte dibuja y ve lo que dibujan los demás.
    /// No aparece en la transmisión (los trazos viajan aparte, como vectores) y deja pasar el mouse
    /// mientras no se está dibujando.
    /// </summary>
    internal sealed class ScreenOverlayWindow : Window
    {
        private readonly ShareSource _source;
        private readonly DispatcherTimer _follow;
        private IntPtr _hwnd;
        private Native.RECT _placed;
        private bool _placedOnce;

        public AnnotationLayer Layer { get; private set; }
        /// <summary>Punteros con nombre de quienes miran y dibujan o controlan.</summary>
        public PointerLayer Pointers { get; private set; }
        public event Action DrawingEnded;
        public event Action<bool> DrawingChanged;

        public ScreenOverlayWindow(ShareSource source)
        {
            _source = source;
            Title = "Lazo · anotaciones";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Left = -32000;
            Top = -32000;
            Width = 1;
            Height = 1;
            Layer = new AnnotationLayer();
            Pointers = new PointerLayer();
            Grid content = new Grid();
            content.Children.Add(Layer);
            content.Children.Add(Pointers);
            Content = content;
            _follow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(source.IsWindow ? 120 : 1000) };
            _follow.Tick += (s, e) => Follow();
            SourceInitialized += (s, e) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                SetClickThrough(true);
                try { Native.SetWindowDisplayAffinity(_hwnd, 0x11); } catch { }
                Follow();
                _follow.Start();
            };
            KeyDown += (s, e) => { if (e.Key == Key.Escape && Layer.Drawing) { SetDrawing(false); e.Handled = true; } };
            Closed += (s, e) => _follow.Stop();
        }

        public bool Drawing { get { return Layer.Drawing; } }

        public void SetDrawing(bool on)
        {
            if (Layer.Drawing == on) return;
            Layer.Drawing = on;
            SetClickThrough(!on);
            Action<bool> changed = DrawingChanged;
            if (changed != null) changed(on);
            if (on) { Activate(); Focus(); }
            else
            {
                Action handler = DrawingEnded;
                if (handler != null) handler();
            }
        }

        private void SetClickThrough(bool through)
        {
            if (_hwnd == IntPtr.Zero) return;
            int style = Native.GetWindowLong(_hwnd, -20);
            // WS_EX_TOOLWINDOW siempre; WS_EX_TRANSPARENT y WS_EX_NOACTIVATE mientras no se dibuja.
            style |= 0x80;
            if (through) style |= 0x20 | 0x08000000;
            else style &= ~(0x20 | 0x08000000);
            SetWindowLong(_hwnd, -20, style);
        }

        /// <summary>Se coloca exactamente sobre la fuente, en píxeles físicos. Las ventanas se siguen al moverse.</summary>
        private void Follow()
        {
            if (_hwnd == IntPtr.Zero) return;
            IntPtr previous = Native.EnterPerMonitorDpi();
            try
            {
                Native.RECT area = new Native.RECT();
                bool visible;
                if (_source.IsWindow)
                {
                    visible = Native.IsWindow(_source.Handle) && !Native.IsIconic(_source.Handle) &&
                        Native.DwmGetWindowAttribute(_source.Handle, 9, out area, Marshal.SizeOf(typeof(Native.RECT))) == 0;
                }
                else
                {
                    visible = true;
                    area = new Native.RECT { Left = _source.Left, Top = _source.Top, Right = _source.Left + _source.Width, Bottom = _source.Top + _source.Height };
                }
                Opacity = visible ? 1 : 0;
                if (!visible) { if (Layer.Drawing) SetDrawing(false); return; }
                if (area.Left == _placed.Left && area.Top == _placed.Top && area.Right == _placed.Right && area.Bottom == _placed.Bottom) return;
                // Solo la primera vez se coloca en la capa «siempre visible»; después no se reordena,
                // para no tapar la barra ni los avisos que se abran luego.
                uint flags = _placedOnce ? 0x0010u | 0x0004u : 0x0010u;
                _placedOnce = true;
                _placed = area;
                SetWindowPos(_hwnd, new IntPtr(-1), area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top, flags);
            }
            finally { Native.LeaveDpi(previous); }
        }

        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    }

    internal static class WindowOrder
    {
        /// <summary>Pone la ventana encima de las demás «siempre visibles» sin activarla.</summary>
        public static void RaiseTopmost(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    }

    /// <summary>
    /// Aviso abajo a la derecha: alguien que mira pide dibujar o controlar. No aparece en la transmisión.
    /// Para el control se elige el modo: «Ventana activa» o «Control total del equipo».
    /// </summary>
    internal sealed class PermissionPromptWindow : Window
    {
        private readonly Action<bool, bool> _respond;
        private bool _answered;
        public readonly int ViewerId;

        public PermissionPromptWindow(int viewerId, string viewer, byte kind, int stack, Action<bool, bool> respond)
        {
            ViewerId = viewerId;
            _respond = respond;
            bool control = kind == SharePermission.Control;
            bool watch = kind == SharePermission.Watch;
            bool ask = kind == SharePermission.AskShare;
            Title = "Lazo · permiso";
            Width = control ? 430 : 380;
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
            head.Children.Add(new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(20),
                Background = control ? Theme.Danger : Theme.Primary,
                Child = Icons.Make(control ? "pointer" : watch ? "eye" : ask ? "screen" : "pen", 20, control ? Brushes.White : Theme.PrimaryText, 2)
            });
            StackPanel words = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Theme.Text(viewer + (control ? " quiere controlar tu equipo" : watch ? " quiere ver tu pantalla" : ask ? " te pide que compartas tu pantalla" : " quiere dibujar en tu pantalla"), 13, Theme.Ink, FontWeights.SemiBold));
            words.Children.Add(Theme.Text(control
                ? "Ventana activa: solo la ventana que compartes (o la que tienes al frente). Control total: mouse y teclado en todo el equipo, incluidas las teclas de Windows. Quítalo cuando quieras desde la barra superior."
                : watch ? "Verá lo mismo que los demás que miran. Puedes dejar de compartir cuando quieras."
                : ask ? "Elige qué compartir; nada se ve hasta que pulses Compartir."
                : "Sus trazos aparecerán sobre tu pantalla y los verán todos los que miran.", 11.5, Theme.Muted));
            Grid.SetColumn(words, 1);
            head.Children.Add(words);
            body.Children.Add(head);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            Button deny = Theme.Button("Rechazar", false);
            deny.Click += (s, e) => Answer(false, false);
            buttons.Children.Add(deny);
            if (control)
            {
                Button full = Theme.Button("Control total", false);
                full.Margin = new Thickness(8, 0, 0, 0);
                full.ToolTip = "Control total del equipo";
                full.Click += (s, e) => Answer(true, true);
                buttons.Children.Add(full);
                Button active = Theme.Button("Ventana activa", true);
                active.Margin = new Thickness(8, 0, 0, 0);
                active.Click += (s, e) => Answer(true, false);
                buttons.Children.Add(active);
            }
            else
            {
                deny.Content = ask ? "Ahora no" : "Rechazar";
                Button allow = Theme.Button(ask ? "Elegir qué compartir" : "Permitir", true);
                allow.Margin = new Thickness(8, 0, 0, 0);
                allow.Click += (s, e) => Answer(true, false);
                buttons.Children.Add(allow);
            }
            body.Children.Add(buttons);
            TextBlock keys = Theme.Text(control
                ? "Ctrl+Alt+A: ventana activa · Ctrl+Alt+R: rechazar · Ctrl+Alt+Q: quitar el control después"
                : "Ctrl+Alt+A: aceptar · Ctrl+Alt+R: rechazar", 10.5, Theme.Muted);
            keys.TextWrapping = TextWrapping.Wrap;
            keys.HorizontalAlignment = HorizontalAlignment.Right;
            keys.Margin = new Thickness(0, 8, 2, 0);
            body.Children.Add(keys);
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
            timeout.Tick += (s, e) => { timeout.Stop(); Answer(false, false); };
            timeout.Start();
            SourceInitialized += (s, e) =>
            {
                try { Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11); } catch { }
            };
            Loaded += (s, e) => WindowPlacement.PlaceBottomRight(this, stack);
            Closed += (s, e) => { timeout.Stop(); Answer(false, false); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Answer(false, false); };
        }

        /// <summary>Vuelve a colocarse cuando se cierra otro aviso de la pila.</summary>
        public void Restack(int stack)
        {
            if (IsLoaded) WindowPlacement.PlaceBottomRight(this, stack);
        }

        public void Dismiss()
        {
            Answer(false, false);
        }

        /// <summary>Atajo Ctrl+Alt+A: acepta. Para el control, la opción prudente: solo la ventana activa.</summary>
        public void Accept()
        {
            Answer(true, false);
        }

        private void Answer(bool accept, bool full)
        {
            if (_answered) return;
            _answered = true;
            _respond(accept, full);
            if (IsVisible) Close();
        }
    }
}
