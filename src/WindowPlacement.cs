using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Lazo
{
    internal static class WindowPlacement
    {
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;

        public static void PlaceBottomCenter(Window window, System.Windows.Forms.Screen screen)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            Rect bounds;
            if (!GetWindowRect(hwnd, out bounds)) return;
            Rectangle area = screen.WorkingArea;
            int width = bounds.Right - bounds.Left;
            int height = bounds.Bottom - bounds.Top;
            System.Drawing.Point anchor = BottomCenter(area, width, height);
            int x = anchor.X, y = anchor.Y;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }

        internal static System.Drawing.Point BottomCenter(Rectangle area, int width, int height)
        {
            return new System.Drawing.Point(area.Left + Math.Max(0, (area.Width - width) / 2),
                Math.Max(area.Top, area.Bottom - height - 4));
        }

        public static void PlaceBottomRight(Window window)
        {
            PlaceBottomRight(window, 0);
        }

        public static void Cover(Window window, System.Windows.Forms.Screen screen, bool activate)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || screen == null) return;
            Rectangle bounds = screen.Bounds;
            uint flags = activate ? 0u : SwpNoActivate;
            SetWindowPos(hwnd, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, flags);
        }

        public static void PlaceBottomRight(Window window, int stack)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            Rect bounds;
            if (!GetWindowRect(hwnd, out bounds)) return;
            Rectangle area = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            int width = bounds.Right - bounds.Left;
            int height = bounds.Bottom - bounds.Top;
            int x = area.Right - width - 18;
            int y = area.Bottom - height - 18 - stack * (height + 8);
            if (x < area.Left) x = area.Left;
            if (y < area.Top) y = area.Top;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        public static void TakeForeground(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            IntPtr foreground = GetForegroundWindow();
            uint foreThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
            uint appThread = GetCurrentThreadId();
            bool attached = foreThread != 0 && foreThread != appThread && AttachThreadInput(foreThread, appThread, true);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            if (attached) AttachThreadInput(foreThread, appThread, false);
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out Rect bounds);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y,
            int width, int height, uint flags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attach, uint attachTo, bool enable);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hwnd);
    }
}
