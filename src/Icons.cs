using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Lazo
{
    /// <summary>
    /// Íconos de trazo en una rejilla de 24 × 24, con extremos y uniones redondeados.
    /// No dependen de las fuentes de símbolos de Windows, así que se ven igual en Windows 10 y 11.
    /// </summary>
    internal static class Icons
    {
        private static readonly Dictionary<string, string> Data = new Dictionary<string, string>
        {
            { "home", "M3.5 10.5 12 3.5l8.5 7 M5.5 9v10a1.5 1.5 0 0 0 1.5 1.5h3v-6h4v6h3a1.5 1.5 0 0 0 1.5-1.5V9" },
            { "chat", "M20.5 11.5a8.5 8.5 0 0 1-12.4 7.6L3.5 20.5l1.4-4.4A8.5 8.5 0 1 1 20.5 11.5z" },
            { "screen", "M4.5 4h15a1.5 1.5 0 0 1 1.5 1.5v9a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 14.5v-9A1.5 1.5 0 0 1 4.5 4z M8.5 20.5h7 M12 16v4.5" },
            { "history", "M3.5 12a8.5 8.5 0 1 0 2.5-6L3.5 8.5 M3.5 3.5v5h5 M12 7.5V12l3 2" },
            { "settings", "M20 7h-8.5 M7.5 7H4 M20 17h-4 M12 17H4 M9.5 4.5a2.5 2.5 0 1 1 0 5a2.5 2.5 0 1 1 0-5z M14 14.5a2.5 2.5 0 1 1 0 5a2.5 2.5 0 1 1 0-5z" },
            { "plus", "M12 5v14 M5 12h14" },
            { "eye", "M2.5 12s3.5-7 9.5-7 9.5 7 9.5 7-3.5 7-9.5 7-9.5-7-9.5-7z M12 9a3 3 0 1 1 0 6a3 3 0 1 1 0-6z" },
            { "close", "M6.5 6.5l11 11 M17.5 6.5l-11 11" },
            { "search", "M11 4a7 7 0 1 1 0 14a7 7 0 1 1 0-14z M20 20l-4-4" },
            { "upload", "M12 15.5V4 M7.5 8.5 12 4l4.5 4.5 M4 15v3.5A2.5 2.5 0 0 0 6.5 21h11a2.5 2.5 0 0 0 2.5-2.5V15" },
            { "logo", "M10 13.5a4 4 0 0 0 5.66.34l2.84-2.84a4 4 0 0 0-5.66-5.66l-1.2 1.2 M14 10.5a4 4 0 0 0-5.66-.34l-2.84 2.84a4 4 0 0 0 5.66 5.66l1.2-1.2" },
            { "attach", "M20.5 11.5l-8.2 8.2a5 5 0 0 1-7.1-7.1l8.6-8.6a3.3 3.3 0 0 1 4.7 4.7l-8.6 8.6a1.7 1.7 0 0 1-2.4-2.4l7.9-7.9" },
            { "send", "M21 3 10.5 13.5 M21 3l-6.5 18-4-7.5-7.5-4z" },
            { "zap", "M13 2.5 4 14h7.5l-1 7.5 9-11.5H12z" },
            { "check", "M5 12.5l4.5 4.5L19 7" },
            { "back", "M14.5 18l-6-6 6-6" },
            { "window", "M4.5 4h15A1.5 1.5 0 0 1 21 5.5v13a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5v-13A1.5 1.5 0 0 1 4.5 4z M3 9h18 M6.5 6.5h.01 M9 6.5h.01" },
            { "expand", "M8.5 3.5h-3a2 2 0 0 0-2 2v3 M20.5 8.5v-3a2 2 0 0 0-2-2h-3 M3.5 15.5v3a2 2 0 0 0 2 2h3 M15.5 20.5h3a2 2 0 0 0 2-2v-3" },
            { "image", "M5.5 3.5h13a2 2 0 0 1 2 2v13a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2v-13a2 2 0 0 1 2-2z M9 7.5a1.5 1.5 0 1 1 0 3a1.5 1.5 0 1 1 0-3z M20.5 15l-5-5-11 10.5" },
            { "reset", "M3.5 12a8.5 8.5 0 1 0 2.5-6L3.5 8.5 M3.5 3.5v5h5" },
            { "file", "M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z M14 3v5h5" },
            { "folder", "M3.5 7.5a2 2 0 0 1 2-2h4l2 2.5h7a2 2 0 0 1 2 2v7.5a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z" },
            { "stop", "M7.5 6h9A1.5 1.5 0 0 1 18 7.5v9a1.5 1.5 0 0 1-1.5 1.5h-9A1.5 1.5 0 0 1 6 16.5v-9A1.5 1.5 0 0 1 7.5 6z" },
            { "user", "M12 4a4 4 0 1 1 0 8a4 4 0 1 1 0-8z M4.5 20.5a7.5 7.5 0 0 1 15 0" },
            { "palette", "M12 3.5a8.5 8.5 0 0 0 0 17c1.2 0 1.8-.9 1.4-2l-.3-.8c-.5-1.2.4-2.2 1.6-2.2h2.1a3.7 3.7 0 0 0 3.7-3.7A8.5 8.5 0 0 0 12 3.5z M7.5 11.5h.01 M10 7.5h.01 M14.5 7.5h.01" },
            { "live", "M8.5 8.5a5 5 0 0 0 0 7 M15.5 8.5a5 5 0 0 1 0 7 M5.6 5.6a9 9 0 0 0 0 12.8 M18.4 5.6a9 9 0 0 1 0 12.8 M12 10.8a1.2 1.2 0 1 1 0 2.4a1.2 1.2 0 1 1 0-2.4z" },
        };

        private static readonly Dictionary<string, Geometry> Cache = new Dictionary<string, Geometry>();

        public static Geometry Geometry(string name)
        {
            Geometry geometry;
            if (Cache.TryGetValue(name, out geometry)) return geometry;
            geometry = System.Windows.Media.Geometry.Parse(Data[name]);
            geometry.Freeze();
            Cache[name] = geometry;
            return geometry;
        }

        /// <summary>Ícono listo para colocar; el color se cambia con <see cref="Shape.Stroke"/>.</summary>
        public static Path Make(string name, double size, Brush brush, double weight = 1.8)
        {
            double scale = size / 24.0;
            return new Path
            {
                Data = Geometry(name),
                Stroke = brush,
                StrokeThickness = weight,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 24,
                Height = 24,
                Stretch = Stretch.None,
                LayoutTransform = new ScaleTransform(scale, scale),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = false,
                IsHitTestVisible = false
            };
        }
    }
}
