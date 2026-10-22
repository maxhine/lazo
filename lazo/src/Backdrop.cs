using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Lazo
{
    /// <summary>
    /// Desenfoque acrílico del escritorio detrás de una ventana transparente (Liquid Glass).
    /// La región redondeada evita que el desenfoque asome por fuera de las esquinas.
    /// </summary>
    internal static class Backdrop
    {
        private const int AccentDisabled = 0;
        private const int AccentAcrylic = 4;

        public static bool Enable(Window window, uint tint, double margin, double radius)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;
            // Primero la región: así el desenfoque nunca asoma por fuera de las esquinas, aunque Windows
            // informe un fallo al aplicarlo y lo aplique igualmente.
            Shape(window, margin, radius);
            SetAccent(hwnd, AccentAcrylic, tint);
            Shape(window, margin, radius);
            return true;
        }

        public static void Disable(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            SetAccent(hwnd, AccentDisabled, 0);
            SetWindowRgn(hwnd, IntPtr.Zero, true);
        }

        /// <summary>Recorta la ventana a un rectángulo redondeado en píxeles físicos.</summary>
        public static void Shape(Window window, double margin, double radius)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            PresentationSource source = PresentationSource.FromVisual(window);
            if (hwnd == IntPtr.Zero || source == null || source.CompositionTarget == null) return;
            System.Windows.Media.Matrix device = source.CompositionTarget.TransformToDevice;
            // Tamaño real de la ventana en Windows: durante las animaciones puede ir por delante del de WPF.
            RECT bounds;
            if (!GetWindowRect(hwnd, out bounds)) return;
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            int m = (int)Math.Round(margin * device.M11), r = (int)Math.Round(radius * 2 * device.M11);
            if (width <= 2 * m || height <= 2 * m) return;
            IntPtr region = CreateRoundRectRgn(m, m, width - m + 1, height - m + 1, r, r);
            if (SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
        }

        private static bool SetAccent(IntPtr hwnd, int state, uint tint)
        {
            try
            {
                AccentPolicy accent = new AccentPolicy { State = state, Flags = 2, Gradient = tint };
                int size = Marshal.SizeOf(accent);
                IntPtr pointer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(accent, pointer, false);
                    CompositionData data = new CompositionData { Attribute = 19, Data = pointer, Size = size };
                    return SetWindowCompositionAttribute(hwnd, ref data) != 0;
                }
                finally { Marshal.FreeHGlobal(pointer); }
            }
            catch { return false; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] private static extern int GetWindowRgnBox(IntPtr hwnd, out RECT rect);

        /// <summary>Comprueba que la región siga coincidiendo con la ventana; si no, la vuelve a aplicar.</summary>
        public static void Heal(Window window, double margin, double radius)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            RECT bounds, box;
            if (!GetWindowRect(hwnd, out bounds)) return;
            int kind = GetWindowRgnBox(hwnd, out box);
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            if (kind <= 1 || Math.Abs(box.Right - (width - box.Left)) > 2 || Math.Abs(box.Bottom - (height - box.Top)) > 2) Shape(window, margin, radius);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy { public int State; public int Flags; public uint Gradient; public int Animation; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }

        [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
        [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    }
}
