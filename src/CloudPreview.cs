using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lazo
{
    /// <summary>Un bloque de puntos consecutivos de la nube (zona de muestra) para la vista previa de la reducción.</summary>
    internal sealed class CloudPatch
    {
        public float[] X, Y, Z;
        public int Count;
        public long Total;
        public string Path;
        public double Zone;
        public bool CanZone;
        public string Format;
    }

    /// <summary>Las dos imágenes de la vista previa y qué muestran.</summary>
    internal sealed class PreviewFrame
    {
        public BitmapSource Original, Reduced;
        public long Before, After;
        public double WindowMeters;
    }

    /// <summary>
    /// Vista previa de la reducción de puntos: lee un bloque de la nube (un millón y medio de puntos seguidos, en
    /// la zona elegida), recorta una ventana alrededor del centro del bloque y la dibuja dos veces: tal cual y
    /// reducida a la distancia pedida. La ventana mide unas 40 veces esa distancia, así que cada punto reducido se
    /// ve aparte. Todo es en memoria y en un solo hilo de fondo: ni toca la conversión ni traba la interfaz.
    /// </summary>
    internal static class CloudPreview
    {
        public const int Size = 192;
        private const int BlockPoints = 1500000;
        private const int E57BlockPoints = 500000;

        public static CloudPatch Load(string path, double zone, string recapFolder, CancellationToken cancel)
        {
            using (CloudReader reader = CloudReader.Open(path, recapFolder))
            {
                int target = reader.CanSeek ? BlockPoints : E57BlockPoints;
                long first = reader.CanSeek && reader.Count > target ? (long)((reader.Count - target) * Math.Max(0, Math.Min(1, zone))) : 0;
                if (reader.CanSeek && first > 0) reader.Seek(first);
                CloudPoint[] block = new CloudPoint[target];
                int count = 0, got;
                CloudPoint[] chunk = new CloudPoint[1 << 16];
                while (count < target && (got = reader.Read(chunk, Math.Min(chunk.Length, target - count))) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    Array.Copy(chunk, 0, block, count, got);
                    count += got;
                }
                if (count == 0) throw new InvalidDataException("No se pudieron leer puntos de " + System.IO.Path.GetFileName(path) + ".");
                double ox = block[count / 2].X, oy = block[count / 2].Y, oz = block[count / 2].Z;
                CloudPatch patch = new CloudPatch
                {
                    X = new float[count], Y = new float[count], Z = new float[count], Count = count,
                    Total = reader.Count, Path = path, Zone = zone, CanZone = reader.CanSeek,
                    Format = System.IO.Path.GetExtension(path).ToLowerInvariant()
                };
                for (int i = 0; i < count; i++)
                {
                    patch.X[i] = (float)(block[i].X - ox);
                    patch.Y[i] = (float)(block[i].Y - oy);
                    patch.Z[i] = (float)(block[i].Z - oz);
                }
                return patch;
            }
        }

        /// <param name="mm">Distancia entre puntos en milímetros reales.</param>
        /// <param name="unitsPerMeter">Unidades del archivo por metro (1 si está en metros).</param>
        /// <param name="elevation">false: vista de planta (X, Y); true: de frente (X, Z).</param>
        public static PreviewFrame Render(CloudPatch patch, double mm, double unitsPerMeter, bool elevation)
        {
            double d = Math.Max(1e-6, mm / 1000.0 * unitsPerMeter);
            float[] u = patch.X, v = elevation ? patch.Z : patch.Y, depth = elevation ? patch.Y : patch.Z;
            // El centro de la ventana es el centro del bloque (los datos están centrados en el punto del medio).
            double side = 40 * d;
            int[] chosen = null;
            int found = 0;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                double half = side / 2;
                List<int> list = new List<int>(1 << 14);
                for (int i = 0; i < patch.Count; i++)
                    if (Math.Abs(u[i]) <= half && Math.Abs(v[i]) <= half) list.Add(i);
                found = list.Count;
                chosen = list.ToArray();
                if (found >= 600) break;
                side *= 2;
            }
            PreviewFrame frame = new PreviewFrame { Before = found, WindowMeters = side / unitsPerMeter };
            if (found == 0) return frame;

            double dmin = double.MaxValue, dmax = double.MinValue;
            foreach (int i in chosen) { if (depth[i] < dmin) dmin = depth[i]; if (depth[i] > dmax) dmax = depth[i]; }
            double range = Math.Max(1e-9, dmax - dmin);
            frame.Original = Draw(patch, chosen, u, v, depth, side, dmin, range);

            VoxelSet cells = new VoxelSet();
            List<int> kept = new List<int>();
            foreach (int i in chosen)
                if (cells.Add(patch.X[i], patch.Y[i], patch.Z[i], d)) kept.Add(i);
            frame.After = kept.Count;
            frame.Reduced = Draw(patch, kept.ToArray(), u, v, depth, side, dmin, range);
            return frame;
        }

        private static BitmapSource Draw(CloudPatch patch, int[] points, float[] u, float[] v, float[] depth, double side, double dmin, double range)
        {
            byte[] pixels = new byte[Size * Size];
            float[] nearest = new float[Size * Size];
            for (int i = 0; i < pixels.Length; i++) { pixels[i] = 22; nearest[i] = float.MinValue; }
            double scale = Size / side;
            foreach (int i in points)
            {
                int px = (int)((u[i] + side / 2) * scale), py = Size - 1 - (int)((v[i] + side / 2) * scale);
                byte shade = (byte)(70 + 185 * Math.Max(0, Math.Min(1, (depth[i] - dmin) / range)));
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int x = px + dx, y = py - dy;
                        if (x < 0 || y < 0 || x >= Size || y >= Size) continue;
                        int at = y * Size + x;
                        if (depth[i] >= nearest[at]) { nearest[at] = depth[i]; pixels[at] = shade; }
                    }
            }
            BitmapSource image = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Gray8, null, pixels, Size);
            image.Freeze();
            return image;
        }
    }
}
