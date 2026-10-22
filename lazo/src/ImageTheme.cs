using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lazo
{
    /// <summary>
    /// Tema «Imagen»: toma la foto de fondo de inicio, la usa desenfocada como fondo de toda la ventana
    /// y deriva de ella la paleta (clara u oscura según su brillo) y el acento (su color más vivo).
    /// </summary>
    internal static class ImageTheme
    {
        private static BitmapSource _source;
        private static Palette _palette;
        private static string[] _suggestions = new string[0];
        private static BitmapSource _blur;

        private static readonly Palette Fallback = new Palette
        {
            Dark = true, Shell0 = "#2A2A2E", Shell1 = "#202024", Shell2 = "#18181B",
            Surface = "#26262A", Soft = "#323236", Track = "#1A1A1D", Ink = "#F2F2F2", Muted = "#9A9AA0",
            Line = "#26FFFFFF", Chip = "#F2F2F2", ChipText = "#18181B", Avatar = "#323236", Online = "#4CD964", Accent = "#EE5A45"
        };

        public static Palette Palette { get { Refresh(); return _palette; } }

        /// <summary>Colores de acento propuestos a partir de la foto de inicio (el primero es el principal).</summary>
        public static string[] Suggestions { get { Refresh(); return _suggestions; } }

        /// <summary>Versión diminuta de la imagen; al ampliarla con interpolación queda como un desenfoque fuerte.</summary>
        public static BitmapSource Blurred { get { Refresh(); return _blur; } }

        private static void Refresh()
        {
            BitmapSource image = HeroBackground.Load();
            if (_palette != null && ReferenceEquals(image, _source)) return;
            _source = image;
            if (image == null) { _palette = Fallback; _blur = null; _suggestions = new string[0]; return; }
            try
            {
                _blur = Shrink(image, 18);
                Analyze(Shrink(image, 48));
            }
            catch { _palette = Fallback; _blur = null; _suggestions = new string[0]; }
        }

        private static BitmapSource Shrink(BitmapSource image, int width)
        {
            int height = Math.Max(1, (int)Math.Round((double)image.PixelHeight * width / Math.Max(1, image.PixelWidth)));
            DrawingVisual visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (DrawingContext context = visual.RenderOpen())
                context.DrawImage(image, new Rect(0, 0, width, height));
            RenderTargetBitmap target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            target.Freeze();
            return target;
        }

        /// <summary>Promedio para el fondo y, por tono, el grupo de píxeles más saturado para el acento.</summary>
        private static void Analyze(BitmapSource small)
        {
            int w = small.PixelWidth, h = small.PixelHeight;
            byte[] pixels = new byte[w * h * 4];
            small.CopyPixels(pixels, w * 4, 0);
            double r = 0, g = 0, b = 0;
            double[] weight = new double[12], hr = new double[12], hg = new double[12], hb = new double[12];
            int count = w * h;
            for (int i = 0; i < count; i++)
            {
                double pb = pixels[i * 4], pg = pixels[i * 4 + 1], pr = pixels[i * 4 + 2];
                r += pr; g += pg; b += pb;
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                if (max < 30) continue;
                double saturation = max == 0 ? 0 : (max - min) / max, value = max / 255.0;
                double hue = Hue(pr, pg, pb, max, min);
                int bucket = (int)(hue / 30) % 12;
                double vivid = saturation * saturation * (0.35 + value);
                weight[bucket] += vivid; hr[bucket] += pr * vivid; hg[bucket] += pg * vivid; hb[bucket] += pb * vivid;
            }
            Color average = Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
            int best = 0;
            for (int i = 1; i < 12; i++) if (weight[i] > weight[best]) best = i;
            Color accent = weight[best] > 0.5
                ? Color.FromRgb((byte)(hr[best] / weight[best]), (byte)(hg[best] / weight[best]), (byte)(hb[best] / weight[best]))
                : Theme.Parse("#EE5A45");
            accent = Vivid(accent);
            // Sugerencias: los grupos de tono con más color vivo, de mayor a menor, sin repetir tonos vecinos.
            System.Collections.Generic.List<string> picks = new System.Collections.Generic.List<string>();
            int[] order = new int[12];
            for (int i = 0; i < 12; i++) order[i] = i;
            Array.Sort(order, (x, y) => weight[y].CompareTo(weight[x]));
            System.Collections.Generic.List<int> used = new System.Collections.Generic.List<int>();
            foreach (int bucket in order)
            {
                if (weight[bucket] <= 0.5 || picks.Count >= 5) break;
                bool near = false;
                foreach (int other in used) if (Math.Min(Math.Abs(bucket - other), 12 - Math.Abs(bucket - other)) < 1) near = true;
                if (near) continue;
                used.Add(bucket);
                picks.Add(Hex(Vivid(Color.FromRgb((byte)(hr[bucket] / weight[bucket]), (byte)(hg[bucket] / weight[bucket]), (byte)(hb[bucket] / weight[bucket])))));
            }
            if (picks.Count == 0) picks.Add(Hex(accent));
            _suggestions = picks.ToArray();
            bool dark = Theme.Luminance(average) < 0.42;
            Func<double, string> toBlack = t => Hex(Theme.Mix(average, Colors.Black, t));
            Func<double, string> toWhite = t => Hex(Theme.Mix(average, Colors.White, t));
            _palette = dark
                ? new Palette
                {
                    Dark = true, Shell0 = toBlack(0.42), Shell1 = toBlack(0.56), Shell2 = toBlack(0.68),
                    Surface = toBlack(0.5), Soft = toBlack(0.36), Track = toBlack(0.66), Ink = "#F5F5F5",
                    Muted = Hex(Theme.Mix(Theme.Mix(average, Colors.White, 0.6), Colors.Gray, 0.25)),
                    Line = "#26FFFFFF", Chip = "#F5F5F5", ChipText = toBlack(0.75), Avatar = toBlack(0.3),
                    Online = "#4CD964", Accent = Hex(accent)
                }
                : new Palette
                {
                    Dark = false, Shell0 = toWhite(0.84), Shell1 = toWhite(0.78), Shell2 = toWhite(0.7),
                    Surface = toWhite(0.92), Soft = toWhite(0.72), Track = toWhite(0.66), Ink = "#161616",
                    Muted = Hex(Theme.Mix(Theme.Mix(average, Colors.Black, 0.62), Colors.Gray, 0.2)),
                    Line = "#1A000000", Chip = "#161616", ChipText = toWhite(0.9), Avatar = toWhite(0.6),
                    Online = "#2FB45A", Accent = Hex(accent)
                };
        }

        private static double Hue(double r, double g, double b, double max, double min)
        {
            if (max == min) return 0;
            double d = max - min, hue;
            if (max == r) hue = (g - b) / d % 6;
            else if (max == g) hue = (b - r) / d + 2;
            else hue = (r - g) / d + 4;
            hue *= 60;
            return hue < 0 ? hue + 360 : hue;
        }

        /// <summary>Lleva el acento a un brillo útil: ni casi negro ni casi blanco.</summary>
        private static Color Vivid(Color c)
        {
            double lum = Theme.Luminance(c);
            for (int i = 0; i < 6 && lum < 0.12; i++) { c = Theme.Mix(c, Colors.White, 0.18); lum = Theme.Luminance(c); }
            for (int i = 0; i < 6 && lum > 0.55; i++) { c = Theme.Mix(c, Colors.Black, 0.15); lum = Theme.Luminance(c); }
            return c;
        }

        private static string Hex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        /// <summary>Fondo de la ventana: la imagen desenfocada con un velo del color del tema.</summary>
        public static Brush Shell(byte alpha)
        {
            BitmapSource blur = Blurred;
            Palette p = Palette;
            if (blur == null) return null;
            Rect area = new Rect(0, 0, blur.PixelWidth, blur.PixelHeight);
            DrawingGroup group = new DrawingGroup();
            RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.Linear);
            group.Children.Add(new ImageDrawing(blur, area));
            Color veil = Theme.Parse(p.Shell1);
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb((byte)(p.Dark ? 0x8C : 0x99), veil.R, veil.G, veil.B)), null, new RectangleGeometry(area)));
            DrawingBrush brush = new DrawingBrush(group) { Stretch = Stretch.UniformToFill, Opacity = alpha / 255.0 };
            return brush;
        }
    }
}
