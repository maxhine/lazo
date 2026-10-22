using System;
using System.Collections.Generic;

namespace Lazo
{
    /// <summary>Un modo de pantalla que ofrece el monitor.</summary>
    internal struct DisplayMode
    {
        public int Width, Height, Frequency;
        public DisplayMode(int width, int height, int frequency)
        {
            Width = width;
            Height = height;
            Frequency = frequency;
        }
        public override string ToString() { return Width + "×" + Height + "@" + Frequency; }
    }

    /// <summary>
    /// Elige el modo de pantalla que mejor sirve a una ventana de cierto tamaño (docs/control-remoto.md §2). Función pura:
    /// el de mayor área que no supere lo pedido y cuya proporción se parezca a la pedida (tolerancia 6 %); si no hay ninguno
    /// que quepa, el más cercano por arriba; si ninguno tiene una proporción parecida, el de proporción más cercana.
    /// </summary>
    internal static class DisplayModePicker
    {
        public const double RatioTolerance = 0.06;
        public const int MinWidth = 800, MinHeight = 600;

        /// <summary>Devuelve el modo elegido (con el mejor refresco de ese tamaño) o false si no hay modos.</summary>
        public static bool Choose(IList<DisplayMode> modes, int wantWidth, int wantHeight, int preferredFrequency, out DisplayMode chosen)
        {
            chosen = new DisplayMode();
            if (modes == null || modes.Count == 0 || wantWidth <= 0 || wantHeight <= 0) return false;
            // Un tamaño por modo (el refresco se decide al final) y sin modos diminutos, salvo que no haya otros.
            Dictionary<long, DisplayMode> sizes = new Dictionary<long, DisplayMode>();
            for (int i = 0; i < modes.Count; i++)
            {
                DisplayMode m = modes[i];
                if (m.Width <= 0 || m.Height <= 0) continue;
                sizes[((long)m.Width << 32) | (uint)m.Height] = m;
            }
            List<DisplayMode> pool = new List<DisplayMode>();
            foreach (DisplayMode m in sizes.Values) if (m.Width >= MinWidth && m.Height >= MinHeight) pool.Add(m);
            if (pool.Count == 0) pool.AddRange(sizes.Values);
            if (pool.Count == 0) return false;

            double want = (double)wantWidth / wantHeight;
            List<DisplayMode> similar = new List<DisplayMode>();
            foreach (DisplayMode m in pool) if (Math.Abs(Ratio(m) / want - 1) <= RatioTolerance) similar.Add(m);
            if (similar.Count == 0)
            {
                // Ninguna proporción parecida: las que más se acerquen.
                double best = double.MaxValue;
                foreach (DisplayMode m in pool) best = Math.Min(best, Math.Abs(Ratio(m) / want - 1));
                foreach (DisplayMode m in pool) if (Math.Abs(Ratio(m) / want - 1) <= best + 0.005) similar.Add(m);
            }

            bool found = false;
            DisplayMode pick = new DisplayMode();
            foreach (DisplayMode m in similar)
            {
                if (m.Width > wantWidth || m.Height > wantHeight) continue;
                if (!found || Area(m) > Area(pick) || (Area(m) == Area(pick) && Closer(m, pick, want))) { pick = m; found = true; }
            }
            if (!found)
            {
                // Nada cabe: el más pequeño que se pase lo menos posible.
                foreach (DisplayMode m in similar)
                    if (!found || Area(m) < Area(pick) || (Area(m) == Area(pick) && Closer(m, pick, want))) { pick = m; found = true; }
            }
            // Refresco: el actual si ese tamaño lo ofrece; si no, el más alto.
            int frequency = 0;
            bool preferred = false;
            for (int i = 0; i < modes.Count; i++)
            {
                if (modes[i].Width != pick.Width || modes[i].Height != pick.Height) continue;
                if (modes[i].Frequency == preferredFrequency) preferred = true;
                frequency = Math.Max(frequency, modes[i].Frequency);
            }
            chosen = new DisplayMode(pick.Width, pick.Height, preferred ? preferredFrequency : frequency);
            return true;
        }

        private static double Ratio(DisplayMode m) { return (double)m.Width / m.Height; }
        private static long Area(DisplayMode m) { return (long)m.Width * m.Height; }

        private static bool Closer(DisplayMode a, DisplayMode b, double want)
        {
            return Math.Abs(Ratio(a) - want) < Math.Abs(Ratio(b) - want);
        }
    }

    /// <summary>Paquete «ajustar resolución» del canal de retorno v3: <c>'Z'</c> u16 ancho u16 alto (0, 0 = restaurar).</summary>
    internal static class ResolutionRequest
    {
        public static byte[] Packet(int width, int height)
        {
            ushort w = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, width)), h = (ushort)Math.Max(0, Math.Min(ushort.MaxValue, height));
            return new[] { (byte)'Z', (byte)w, (byte)(w >> 8), (byte)h, (byte)(h >> 8) };
        }
    }
}
