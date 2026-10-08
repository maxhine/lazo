using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Lazo
{
    /// <summary>Una pantalla o una ventana que se puede compartir.</summary>
    internal sealed class ShareSource
    {
        public bool IsWindow;
        public IntPtr Handle;
        public string Label;
        public string Detail;
        public int Left, Top, Width, Height;
        public override string ToString() { return Label; }
    }

    internal static class ScreenSources
    {
        /// <summary>Monitores conectados, en píxeles físicos.</summary>
        public static List<ShareSource> Screens()
        {
            List<ShareSource> list = new List<ShareSource>();
            IntPtr previous = Native.EnterPerMonitorDpi();
            try
            {
                Native.MonitorEnumProc callback = (IntPtr monitor, IntPtr hdc, ref Native.RECT area, IntPtr data) =>
                {
                    Native.MONITORINFO info = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
                    if (!Native.GetMonitorInfo(monitor, ref info)) return true;
                    Native.RECT r = info.rcMonitor;
                    bool primary = (info.dwFlags & 1) != 0;
                    list.Add(new ShareSource
                    {
                        Handle = monitor,
                        Left = r.Left, Top = r.Top, Width = r.Right - r.Left, Height = r.Bottom - r.Top,
                        Detail = (r.Right - r.Left) + " × " + (r.Bottom - r.Top) + (primary ? " · principal" : "")
                    });
                    return true;
                };
                Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
                GC.KeepAlive(callback);
            }
            finally { Native.LeaveDpi(previous); }
            list.Sort((a, b) => a.Left != b.Left ? a.Left.CompareTo(b.Left) : a.Top.CompareTo(b.Top));
            for (int i = 0; i < list.Count; i++) list[i].Label = "Pantalla " + (i + 1);
            return list;
        }

        /// <summary>Ventanas de aplicación visibles, sin las de Lazo ni las del escritorio.</summary>
        public static List<ShareSource> Windows()
        {
            List<ShareSource> list = new List<ShareSource>();
            int self = Process.GetCurrentProcess().Id;
            Dictionary<int, string> names = new Dictionary<int, string>();
            Native.EnumWindowsProc callback = (hwnd, data) =>
            {
                if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, 4) != IntPtr.Zero) return true;
                if ((Native.GetWindowLong(hwnd, -20) & 0x80) != 0) return true;
                int cloaked;
                if (Native.DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                int length = Native.GetWindowTextLength(hwnd);
                if (length == 0) return true;
                StringBuilder title = new StringBuilder(length + 1);
                Native.GetWindowText(hwnd, title, title.Capacity);
                StringBuilder cls = new StringBuilder(64);
                Native.GetClassName(hwnd, cls, cls.Capacity);
                string klass = cls.ToString();
                if (klass == "Progman" || klass == "WorkerW" || klass == "Shell_TrayWnd") return true;
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if ((int)pid == self) return true;
                string process;
                if (!names.TryGetValue((int)pid, out process))
                {
                    try { process = Process.GetProcessById((int)pid).ProcessName; }
                    catch { process = ""; }
                    names[(int)pid] = process;
                }
                bool minimized = Native.IsIconic(hwnd);
                list.Add(new ShareSource
                {
                    IsWindow = true,
                    Handle = hwnd,
                    Label = title.ToString(),
                    Detail = (process.Length > 0 ? process : "Ventana") + (minimized ? " · minimizada" : "")
                });
                return true;
            };
            Native.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return list;
        }

        /// <summary>Miniatura rápida de una pantalla para el selector.</summary>
        public static System.Windows.Media.Imaging.BitmapSource Thumbnail(ShareSource screen, int width)
        {
            IntPtr previous = Native.EnterPerMonitorDpi();
            try
            {
                int height = Math.Max(1, screen.Height * width / Math.Max(1, screen.Width));
                IntPtr desktop = Native.GetDC(IntPtr.Zero);
                IntPtr memory = Native.CreateCompatibleDC(desktop);
                IntPtr bitmap = Native.CreateCompatibleBitmap(desktop, width, height);
                IntPtr old = Native.SelectObject(memory, bitmap);
                Native.SetStretchBltMode(memory, 4);
                Native.StretchBlt(memory, 0, 0, width, height, desktop, screen.Left, screen.Top, screen.Width, screen.Height, 0x00CC0020);
                Native.SelectObject(memory, old);
                System.Windows.Media.Imaging.BitmapSource source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                source = new System.Windows.Media.Imaging.FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgr32, null, 0);
                source.Freeze();
                Native.DeleteObject(bitmap);
                Native.DeleteDC(memory);
                Native.ReleaseDC(IntPtr.Zero, desktop);
                return source;
            }
            catch { return null; }
            finally { Native.LeaveDpi(previous); }
        }
    }

    /// <summary>
    /// Captura GDI hacia una sección DIB de 32 bits. Las pantallas se copian con BitBlt;
    /// las ventanas con PrintWindow, que funciona aunque estén tapadas por otras.
    /// Las fuentes muy grandes se reducen para mantener la fluidez.
    /// </summary>
    internal sealed class FrameCapturer : IDisposable
    {
        public const int MaxWidth = 2560;
        public const int MaxHeight = 1600;
        private readonly ShareSource _source;
        private IntPtr _desktop;
        private Dib _raw;
        private Dib _scaled;
        public IntPtr Bits { get; private set; }
        public int Stride { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int CursorX { get; private set; }
        public int CursorY { get; private set; }

        public FrameCapturer(ShareSource source)
        {
            _source = source;
            _desktop = Native.GetDC(IntPtr.Zero);
            if (source.IsWindow && Native.IsIconic(source.Handle)) Native.ShowWindow(source.Handle, 9);
        }

        public bool Alive { get { return !_source.IsWindow || Native.IsWindow(_source.Handle); } }

        /// <summary>Captura un fotograma. Devuelve false si la fuente no está disponible ahora.</summary>
        public bool Capture()
        {
            int ox, oy, cw, ch;
            IntPtr sourceDc;
            int sx, sy;
            if (_source.IsWindow)
            {
                IntPtr hwnd = _source.Handle;
                if (!Native.IsWindow(hwnd) || Native.IsIconic(hwnd)) return false;
                Native.RECT window;
                if (!Native.GetWindowRect(hwnd, out window)) return false;
                int ww = window.Right - window.Left, wh = window.Bottom - window.Top;
                if (ww < 8 || wh < 8) return false;
                Native.RECT frame;
                if (Native.DwmGetWindowAttribute(hwnd, 9, out frame, Marshal.SizeOf(typeof(Native.RECT))) != 0) frame = window;
                ox = Math.Max(0, frame.Left - window.Left);
                oy = Math.Max(0, frame.Top - window.Top);
                cw = Math.Min(ww - ox, frame.Right - frame.Left);
                ch = Math.Min(wh - oy, frame.Bottom - frame.Top);
                if (cw < 8 || ch < 8) return false;
                Ensure(ref _raw, ww, wh);
                if (!Native.PrintWindow(hwnd, _raw.Dc, 2)) return false;
                sourceDc = _raw.Dc;
                sx = ox; sy = oy;
                Cursor(frame.Left, frame.Top, cw, ch);
            }
            else
            {
                cw = _source.Width; ch = _source.Height;
                sourceDc = _desktop;
                sx = _source.Left; sy = _source.Top;
                Cursor(_source.Left, _source.Top, cw, ch);
                ox = oy = 0;
            }

            double scale = Math.Min(1.0, Math.Min((double)MaxWidth / cw, (double)MaxHeight / ch));
            if (scale < 1.0)
            {
                int dw = Math.Max(8, (int)(cw * scale)), dh = Math.Max(8, (int)(ch * scale));
                Ensure(ref _scaled, dw, dh);
                Native.SetStretchBltMode(_scaled.Dc, 4);
                Native.SetBrushOrgEx(_scaled.Dc, 0, 0, IntPtr.Zero);
                Native.StretchBlt(_scaled.Dc, 0, 0, dw, dh, sourceDc, sx, sy, cw, ch, 0x00CC0020);
                Use(_scaled, 0, 0, dw, dh);
                CursorX = CursorX < 0 ? -1 : (int)(CursorX * scale);
                CursorY = CursorY < 0 ? -1 : (int)(CursorY * scale);
                return true;
            }
            if (!_source.IsWindow)
            {
                Ensure(ref _raw, cw, ch);
                if (!Native.BitBlt(_raw.Dc, 0, 0, cw, ch, _desktop, sx, sy, 0x00CC0020)) return false;
            }
            Use(_raw, ox, oy, cw, ch);
            return true;
        }

        private void Cursor(int left, int top, int width, int height)
        {
            Native.CURSORINFO info = new Native.CURSORINFO { cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO)) };
            CursorX = CursorY = -1;
            if (!Native.GetCursorInfo(ref info) || (info.flags & 1) == 0) return;
            int x = info.ptScreenPos.X - left, y = info.ptScreenPos.Y - top;
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            CursorX = x; CursorY = y;
        }

        private void Use(Dib dib, int x, int y, int width, int height)
        {
            Stride = dib.Width * 4;
            Bits = dib.Bits + y * Stride + x * 4;
            Width = width;
            Height = height;
        }

        private void Ensure(ref Dib dib, int width, int height)
        {
            if (dib != null && dib.Width == width && dib.Height == height) return;
            if (dib != null) dib.Dispose();
            dib = new Dib(_desktop, width, height);
        }

        public void Dispose()
        {
            if (_raw != null) _raw.Dispose();
            if (_scaled != null) _scaled.Dispose();
            _raw = _scaled = null;
            if (_desktop != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, _desktop);
            _desktop = IntPtr.Zero;
        }

        private sealed class Dib : IDisposable
        {
            public readonly IntPtr Dc;
            public readonly IntPtr Bits;
            public readonly int Width, Height;
            private readonly IntPtr _bitmap, _old;

            public Dib(IntPtr reference, int width, int height)
            {
                Width = width; Height = height;
                Native.BITMAPINFO info = new Native.BITMAPINFO();
                info.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFO));
                info.biWidth = width;
                info.biHeight = -height;
                info.biPlanes = 1;
                info.biBitCount = 32;
                Dc = Native.CreateCompatibleDC(reference);
                IntPtr bits;
                _bitmap = Native.CreateDIBSection(reference, ref info, 0, out bits, IntPtr.Zero, 0);
                if (_bitmap == IntPtr.Zero) { Native.DeleteDC(Dc); throw new OutOfMemoryException("No se pudo reservar el búfer de captura."); }
                Bits = bits;
                _old = Native.SelectObject(Dc, _bitmap);
            }

            public void Dispose()
            {
                Native.SelectObject(Dc, _old);
                Native.DeleteObject(_bitmap);
                Native.DeleteDC(Dc);
            }
        }
    }

    /// <summary>Rectángulo codificado como JPEG.</summary>
    internal struct EncodedTile
    {
        public int X, Y, W, H;
        public byte[] Jpeg;
    }

    /// <summary>
    /// Compara cada fotograma con el último enviado en bloques de 128 × 64 y codifica solo lo que cambió.
    /// Los bloques enviados con calidad reducida durante el movimiento se reenvían nítidos cuando la zona se queda quieta.
    /// </summary>
    internal sealed class TileEncoder : IDisposable
    {
        private const int TileW = 128, TileH = 64;
        private const int MotionQuality = 58, NormalQuality = 74, SharpQuality = 90;
        private const int RefineAfterFrames = 8;
        private IntPtr _previous = IntPtr.Zero;
        private int _width, _height, _cols, _rows;
        private int[] _refine;
        private static readonly System.Drawing.Imaging.ImageCodecInfo Jpeg = FindJpeg();

        public int Width { get { return _width; } }
        public int Height { get { return _height; } }

        /// <summary>Codifica los cambios. Con <paramref name="key"/> envía el fotograma completo.</summary>
        public List<EncodedTile> Encode(FrameCapturer frame, bool key)
        {
            if (frame.Width != _width || frame.Height != _height || _previous == IntPtr.Zero)
            {
                Reset(frame.Width, frame.Height);
                key = true;
            }
            int total = _cols * _rows;
            byte[] quality = new byte[total];
            int dirty = 0;
            for (int ty = 0; ty < _rows; ty++)
            {
                int y0 = ty * TileH, h = Math.Min(TileH, _height - y0);
                for (int tx = 0; tx < _cols; tx++)
                {
                    int x0 = tx * TileW, w = Math.Min(TileW, _width - x0);
                    int index = ty * _cols + tx;
                    bool changed = key || Differs(frame, x0, y0, w, h);
                    if (changed)
                    {
                        Copy(frame, x0, y0, w, h);
                        quality[index] = 1;
                        dirty++;
                    }
                    else if (_refine[index] > 0 && --_refine[index] == 0)
                    {
                        quality[index] = 2;
                    }
                }
            }
            int motionQuality = key ? NormalQuality : dirty * 3 > total ? MotionQuality : NormalQuality;
            for (int i = 0; i < total; i++)
            {
                if (quality[i] == 1) _refine[i] = motionQuality < SharpQuality ? RefineAfterFrames : 0;
                else if (quality[i] == 2) _refine[i] = 0;
            }

            // Une bloques contiguos de la misma fila y calidad en franjas, para reducir cabeceras y costuras.
            List<int[]> runs = new List<int[]>();
            for (int ty = 0; ty < _rows; ty++)
            {
                int tx = 0;
                while (tx < _cols)
                {
                    byte q = quality[ty * _cols + tx];
                    if (q == 0) { tx++; continue; }
                    int start = tx;
                    while (tx < _cols && quality[ty * _cols + tx] == q) tx++;
                    int x0 = start * TileW, y0 = ty * TileH;
                    int x1 = Math.Min(_width, tx * TileW), y1 = Math.Min(_height, y0 + TileH);
                    runs.Add(new[] { x0, y0, x1 - x0, y1 - y0, q == 2 ? SharpQuality : motionQuality });
                }
            }
            EncodedTile[] output = new EncodedTile[runs.Count];
            IntPtr bits = frame.Bits;
            int stride = frame.Stride;
            Parallel.For(0, runs.Count, i =>
            {
                int[] r = runs[i];
                output[i] = new EncodedTile { X = r[0], Y = r[1], W = r[2], H = r[3], Jpeg = Compress(bits, stride, r[0], r[1], r[2], r[3], r[4]) };
            });
            return new List<EncodedTile>(output);
        }

        private bool Differs(FrameCapturer frame, int x, int y, int w, int h)
        {
            int bytes = w * 4;
            int prevStride = _width * 4;
            for (int row = 0; row < h; row++)
            {
                IntPtr a = frame.Bits + (y + row) * frame.Stride + x * 4;
                IntPtr b = _previous + (y + row) * prevStride + x * 4;
                if (Native.memcmp(a, b, (UIntPtr)bytes) != 0) return true;
            }
            return false;
        }

        private void Copy(FrameCapturer frame, int x, int y, int w, int h)
        {
            int bytes = w * 4;
            int prevStride = _width * 4;
            for (int row = 0; row < h; row++)
                Native.CopyMemory(_previous + (y + row) * prevStride + x * 4, frame.Bits + (y + row) * frame.Stride + x * 4, (UIntPtr)bytes);
        }

        private static byte[] Compress(IntPtr bits, int stride, int x, int y, int w, int h, int quality)
        {
            using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(w, h, stride, System.Drawing.Imaging.PixelFormat.Format32bppRgb, bits + y * stride + x * 4))
            using (MemoryStream output = new MemoryStream(w * h / 4))
            using (System.Drawing.Imaging.EncoderParameters parameters = new System.Drawing.Imaging.EncoderParameters(1))
            {
                parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                bitmap.Save(output, Jpeg, parameters);
                return output.ToArray();
            }
        }

        private static System.Drawing.Imaging.ImageCodecInfo FindJpeg()
        {
            foreach (System.Drawing.Imaging.ImageCodecInfo codec in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders())
                if (codec.MimeType == "image/jpeg") return codec;
            throw new InvalidOperationException("No hay codificador JPEG disponible.");
        }

        private void Reset(int width, int height)
        {
            if (_previous != IntPtr.Zero) Marshal.FreeHGlobal(_previous);
            _width = width; _height = height;
            _previous = Marshal.AllocHGlobal(width * height * 4);
            _cols = (width + TileW - 1) / TileW;
            _rows = (height + TileH - 1) / TileH;
            _refine = new int[_cols * _rows];
        }

        public void Dispose()
        {
            if (_previous != IntPtr.Zero) Marshal.FreeHGlobal(_previous);
            _previous = IntPtr.Zero;
        }
    }

    internal static class Native
    {
        public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT area, IntPtr data);
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }
        [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
            public int bmiColors;
        }

        /// <summary>Hace que el hilo actual trabaje en píxeles físicos, aunque Lazo no declare DPI por monitor.</summary>
        public static IntPtr EnterPerMonitorDpi()
        {
            try { return SetThreadDpiAwarenessContext(new IntPtr(-4)); }
            catch { return IntPtr.Zero; }
        }

        public static void LeaveDpi(IntPtr previous)
        {
            if (previous == IntPtr.Zero) return;
            try { SetThreadDpiAwarenessContext(previous); }
            catch { }
        }

        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO info);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr previous);
        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)] public static extern int memcmp(IntPtr a, IntPtr b, UIntPtr count);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] public static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);
    }
}
