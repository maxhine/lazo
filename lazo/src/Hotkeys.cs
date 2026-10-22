using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Lazo
{
    internal sealed class Hotkeys : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private const int WmHotkey = 0x0312;
        private const int HotkeyId = 6149;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModNoRepeat = 0x4000;
        private readonly Window _window;
        private readonly Action _activate;
        private readonly HookProc _hookProc;
        private HwndSource _source;
        private IntPtr _hook;
        private bool _registered;
        private bool _altDown;
        private bool _otherKey;
        private long _downTick;
        private long _lastTapTick;

        // Estado de Ctrl y Alt izquierdo según el teclado físico (la entrada inyectada no cuenta).
        private bool _ctrlDown;
        private bool _leftAltDown;

        /// <summary>
        /// Atajos de emergencia: Ctrl + Alt izquierdo + letra, solo desde el teclado físico. Quien controla a distancia
        /// no puede usarlos, y AltGr (Alt derecho, con el que se escribe @ en teclados latinoamericanos) no los activa.
        /// Devuelve true si el atajo se usó; entonces la tecla no llega a la aplicación.
        /// </summary>
        public Func<char, bool> Chord;

        public bool AlternativeAvailable { get { return _registered; } }
        public bool DoubleAltAvailable { get { return _hook != IntPtr.Zero; } }

        public Hotkeys(Window window, Action activate)
        {
            _window = window;
            _activate = activate;
            _hookProc = HookCallback;
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            _source = HwndSource.FromHwnd(hwnd);
            _source.AddHook(WndProc);
            _registered = RegisterHotKey(hwnd, HotkeyId, ModControl | ModAlt | ModNoRepeat, 0x4C);
            using (Process process = Process.GetCurrentProcess())
            using (ProcessModule module = process.MainModule)
                _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(module.ModuleName), 0);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
            {
                _activate();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int msg = wParam.ToInt32();
                bool down = msg == WmKeyDown || msg == WmSysKeyDown;
                bool up = msg == WmKeyUp || msg == WmSysKeyUp;
                if (down || up)
                {
                    KbdLlHookStruct data = (KbdLlHookStruct)Marshal.PtrToStructure(lParam, typeof(KbdLlHookStruct));
                    // La entrada inyectada (control remoto, macros) no abre Lazo: solo el teclado real.
                    if ((data.Flags & 0x10) != 0) return CallNextHookEx(_hook, code, wParam, lParam);
                    bool leftAlt = data.VkCode == 0xA4 || (data.VkCode == 0x12 && (data.Flags & 0x01) == 0);
                    char letter = Feed(data.VkCode, data.ScanCode, data.Flags, down);
                    if (letter != '\0')
                    {
                        Func<char, bool> chord = Chord;
                        _otherKey = true;
                        _lastTapTick = 0;
                        if (chord != null && chord(letter)) return new IntPtr(1);
                    }
                    long now = Environment.TickCount & int.MaxValue;
                    if (leftAlt)
                    {
                        if (down && !_altDown)
                        {
                            _altDown = true;
                            _otherKey = false;
                            _downTick = now;
                        }
                        else if (up && _altDown)
                        {
                            _altDown = false;
                            if (!_otherKey && now - _downTick < 260)
                            {
                                if (_lastTapTick > 0 && now - _lastTapTick < 430)
                                {
                                    _lastTapTick = 0;
                                    _window.Dispatcher.BeginInvoke(_activate);
                                }
                                else _lastTapTick = now;
                            }
                            else _lastTapTick = 0;
                        }
                    }
                    else if (down)
                    {
                        _otherKey = true;
                        _lastTapTick = 0;
                    }
                }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        /// <summary>
        /// Lleva la cuenta de Ctrl y Alt izquierdo físicos y devuelve la letra si esta pulsación completa
        /// Ctrl + Alt izquierdo + letra; si no, '\0'. La entrada inyectada nunca cuenta.
        /// </summary>
        internal char Feed(int vk, int scan, int flags, bool down)
        {
            if ((flags & 0x10) != 0) return '\0';
            bool leftAlt = vk == 0xA4 || (vk == 0x12 && (flags & 0x01) == 0);
            // AltGr envía un Ctrl izquierdo falso (código de exploración 0x21D); no cuenta como Ctrl.
            bool ctrl = (vk == 0x11 || vk == 0xA2 || vk == 0xA3) && scan != 0x21D;
            if (ctrl) _ctrlDown = down;
            if (leftAlt) _leftAltDown = down;
            if (down && _ctrlDown && _leftAltDown && vk >= 0x41 && vk <= 0x5A) return (char)vk;
            return '\0';
        }

        /// <summary>Para pruebas: solo la lógica de los atajos, sin gancho de teclado.</summary>
        internal Hotkeys()
        {
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            if (_source != null) { _source.RemoveHook(WndProc); _source = null; }
            if (_registered)
            {
                UnregisterHotKey(new WindowInteropHelper(_window).Handle, HotkeyId);
                _registered = false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public int VkCode;
            public int ScanCode;
            public int Flags;
            public int Time;
            public IntPtr ExtraInfo;
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string moduleName);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }
}
