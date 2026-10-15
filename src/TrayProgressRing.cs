using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Lazo
{
    /// <summary>
    /// Progreso de una tarea larga como anillo alrededor del ícono de Lazo en la bandeja: un aro tenue de fondo y el
    /// avance en coral, empezando arriba y en el sentido del reloj. Se redibuja solo cuando cambia el porcentaje.
    /// </summary>
    internal sealed class TrayProgressRing
    {
        private static readonly Color Track = Color.FromArgb(110, 140, 140, 140);
        private static readonly Color Fill = Color.FromArgb(255, 0xF2, 0x5C, 0x45);
        private readonly NotifyIcon _tray;
        private readonly Icon _original;
        private readonly string _originalText;
        private IntPtr _handle = IntPtr.Zero;
        private int _shown = -1;

        public TrayProgressRing(NotifyIcon tray)
        {
            _tray = tray;
            _original = tray.Icon;
            _originalText = tray.Text;
        }

        public void Show(double value, string text)
        {
            int percent = (int)Math.Round(Math.Max(0, Math.Min(1, value)) * 100);
            if (!string.IsNullOrEmpty(text)) try { _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text; } catch { }
            if (percent == _shown) return;
            _shown = percent;
            using (Bitmap bitmap = Render(_original, percent / 100.0, 32))
            {
                IntPtr handle = bitmap.GetHicon();
                _tray.Icon = Icon.FromHandle(handle);
                Release();
                _handle = handle;
            }
        }

        public void Clear()
        {
            _shown = -1;
            try { _tray.Icon = _original; _tray.Text = _originalText; } catch { }
            Release();
        }

        private void Release()
        {
            if (_handle != IntPtr.Zero) { DestroyIcon(_handle); _handle = IntPtr.Zero; }
        }

        /// <summary>El ícono un poco reducido en el centro y el anillo alrededor.</summary>
        internal static Bitmap Render(Icon icon, double value, int size)
        {
            Bitmap bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);
                float stroke = size * 0.13f;
                float inset = stroke * 1.35f;
                using (Icon sized = new Icon(icon, size, size))
                    g.DrawIcon(sized, new Rectangle((int)inset, (int)inset, (int)(size - inset * 2), (int)(size - inset * 2)));
                RectangleF ring = new RectangleF(stroke / 2, stroke / 2, size - stroke, size - stroke);
                using (Pen track = new Pen(Track, stroke))
                    g.DrawEllipse(track, ring);
                if (value > 0)
                    using (Pen fill = new Pen(Fill, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(fill, ring, -90, (float)(360 * Math.Max(0.02, value)));
            }
            return bitmap;
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}
