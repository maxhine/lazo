using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lazo
{
    /// <summary>Un tramo de puntos de un LAS/LAZ que se procesa por separado (y en paralelo con los demás).</summary>
    internal sealed class CloudTile
    {
        public string Input;
        public long First, Count;
        public string Output;
        /// <summary>Nombre del escaneo en ReCap: el del archivo, o «archivo parte 3» si se partió.</summary>
        public string ScanName;
    }

    /// <summary>
    /// Prepara nubes LAS/LAZ para ReCap usando toda la máquina: las parte en tramos de puntos consecutivos y cada
    /// tramo se escribe (y, si hace falta, se reproyecta con <see cref="LazoProjection"/>) en paralelo. Los LAZ se
    /// descomprimen por tramos con la LASzip que instala ReCap, varios a la vez. Las coordenadas viven en los
    /// primeros 12 bytes de cada punto en todos los formatos; se recalculan escala, desplazamiento y límites, y los
    /// registros de proyección viejos se renombran para que nadie los lea.
    /// </summary>
    internal static class CloudReprojector
    {
        private const int Chunk = 1 << 20;

        /// <summary>Reproyecta un archivo completo a un LAS en <paramref name="folder"/> (un solo tramo).</summary>
        public static string Reproject(string input, LazoProjection from, LazoProjection to, GeoidGrid geoid, bool ellipsoidal,
            string folder, string recapFolder, Action<double> progress, CancellationToken cancel)
        {
            Directory.CreateDirectory(folder);
            CloudTile tile = Plan(new[] { input }, 1, folder)[0];
            Build(tile, from, to, ellipsoidal ? geoid : null, recapFolder, progress, cancel);
            return tile.Output;
        }

        /// <summary>
        /// Reparte los archivos en unos <paramref name="parts"/> tramos de tamaño parecido: los grandes se parten,
        /// los pequeños van enteros. Cada tramo será un escaneo en ReCap.
        /// </summary>
        public static List<CloudTile> Plan(IList<string> inputs, int parts, string folder)
        {
            List<CloudTile> tiles = new List<CloudTile>();
            long total = inputs.Sum(i => new FileInfo(i).Length);
            foreach (string input in inputs)
            {
                long count = PointCount(input);
                int pieces = Math.Max(1, (int)Math.Round((double)new FileInfo(input).Length / Math.Max(1, total) * parts));
                pieces = (int)Math.Min(pieces, Math.Max(1, count / 1000000));
                string stem = Path.GetFileNameWithoutExtension(input);
                for (int k = 0; k < pieces; k++)
                {
                    long first = count * k / pieces, last = count * (k + 1) / pieces;
                    string scan = pieces == 1 ? stem : stem + " parte " + (k + 1);
                    tiles.Add(new CloudTile
                    {
                        Input = input, First = first, Count = last - first, ScanName = scan,
                        Output = Path.Combine(folder, Guid.NewGuid().ToString("N").Substring(0, 6), scan + ".las")
                    });
                }
            }
            return tiles;
        }

        /// <summary>
        /// Escribe un tramo como LAS independiente y, con <paramref name="to"/>, lo reproyecta. Las alturas pasan a
        /// EGM2008 si se da <paramref name="geoid"/>. Usa todos los núcleos para el cálculo.
        /// </summary>
        public static void Build(CloudTile tile, LazoProjection from, LazoProjection to, GeoidGrid geoid, string recapFolder,
            Action<double> progress, CancellationToken cancel)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tile.Output));
            bool laz = string.Equals(Path.GetExtension(tile.Input), ".laz", StringComparison.OrdinalIgnoreCase);
            if (laz) DecompressRange(tile, recapFolder, cancel);
            else CopyRange(tile, cancel);
            if (progress != null) progress(0.4);
            Finish(tile.Output, to == null ? null : from, to, geoid, tile.Count, value => { if (progress != null) progress(0.4 + 0.6 * value); }, cancel);
        }

        /// <summary>Cabecera, registros y los puntos del tramo, copiados tal cual.</summary>
        private static void CopyRange(CloudTile tile, CancellationToken cancel)
        {
            using (FileStream reader = new FileStream(tile.Input, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (FileStream writer = new FileStream(tile.Output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                byte[] start = new byte[227];
                Fill(reader, start);
                if (Encoding.ASCII.GetString(start, 0, 4) != "LASF") throw new InvalidDataException(Path.GetFileName(tile.Input) + " no es un LAS válido.");
                uint pointOffset = BitConverter.ToUInt32(start, 96);
                int recordLength = BitConverter.ToUInt16(start, 105);
                reader.Position = 0;
                byte[] head = new byte[pointOffset];
                Fill(reader, head);
                writer.Write(head, 0, head.Length);
                reader.Position = pointOffset + tile.First * recordLength;
                long remaining = tile.Count * recordLength;
                byte[] buffer = new byte[Math.Max(recordLength, (Chunk / recordLength) * recordLength)];
                while (remaining > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    int take = (int)Math.Min(buffer.Length, remaining);
                    if (Fill(reader, buffer, take) < take) throw new EndOfStreamException(Path.GetFileName(tile.Input) + " está incompleto.");
                    writer.Write(buffer, 0, take);
                    remaining -= take;
                }
            }
        }

        /// <summary>
        /// Recorre los puntos del LAS ya escrito: reproyecta (si se pide), calcula los límites y deja la cabecera
        /// coherente con el tramo. Trabaja por bloques de un millón de puntos repartidos entre todos los núcleos.
        /// </summary>
        private static void Finish(string path, LazoProjection from, LazoProjection to, GeoidGrid geoid, long count, Action<double> progress, CancellationToken cancel)
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                byte[] start = new byte[227];
                Fill(file, start);
                int headerSize = BitConverter.ToUInt16(start, 94);
                file.Position = 0;
                byte[] header = new byte[headerSize];
                Fill(file, header);
                byte minor = header[25];
                uint pointOffset = BitConverter.ToUInt32(header, 96);
                uint vlrCount = BitConverter.ToUInt32(header, 100);
                int recordLength = BitConverter.ToUInt16(header, 105);
                if (recordLength < 12) throw new InvalidDataException("Formato de punto no válido.");
                double sx = D(header, 131), sy = D(header, 139), sz = D(header, 147);
                double ox = D(header, 155), oy = D(header, 163), oz = D(header, 171);
                double nsx = sx, nsy = sy, nox = ox, noy = oy;
                bool reproject = to != null && from != null;
                if (reproject)
                {
                    nsx = to.Geographic ? 1e-7 : (from.Geographic ? 0.001 : Math.Min(sx, 0.001));
                    nsy = to.Geographic ? 1e-7 : (from.Geographic ? 0.001 : Math.Min(sy, 0.001));
                    double cx = (D(header, 179) + D(header, 187)) / 2, cy = (D(header, 195) + D(header, 203)) / 2, ncx, ncy, lat, lon;
                    from.Inverse(cx, cy, out lat, out lon);
                    to.Forward(lat, lon, out ncx, out ncy);
                    nox = to.Geographic ? Math.Round(ncx) : Math.Round(ncx / 1000) * 1000;
                    noy = to.Geographic ? Math.Round(ncy) : Math.Round(ncy / 1000) * 1000;
                    if (geoid != null)
                    {
                        double latA, lonA, latB, lonB;
                        from.Inverse(D(header, 187), D(header, 203), out latA, out lonA);
                        from.Inverse(D(header, 179), D(header, 195), out latB, out lonB);
                        geoid.Prepare(Math.Min(latA, latB) - 0.1, Math.Max(latA, latB) + 0.1);
                    }
                }

                int perChunk = Math.Max(1, Chunk / recordLength);
                byte[] buffer = new byte[perChunk * recordLength];
                double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                object bounds = new object();
                long done = 0;
                file.Position = pointOffset;
                int workers = Math.Max(1, Environment.ProcessorCount);
                while (done < count)
                {
                    cancel.ThrowIfCancellationRequested();
                    int records = (int)Math.Min(perChunk, count - done);
                    int bytes = records * recordLength;
                    long at = file.Position;
                    if (Fill(file, buffer, bytes) < bytes) throw new EndOfStreamException(Path.GetFileName(path) + " está incompleto.");
                    int slice = Math.Max(4096, (records + workers - 1) / workers);
                    Parallel.For(0, (records + slice - 1) / slice, new ParallelOptions { CancellationToken = cancel }, part =>
                    {
                        double lx = double.MaxValue, ly = double.MaxValue, lz = double.MaxValue;
                        double hx = double.MinValue, hy = double.MinValue, hz = double.MinValue;
                        int end = Math.Min(records, (part + 1) * slice);
                        for (int r = part * slice; r < end; r++)
                        {
                            int p = r * recordLength;
                            double x = BitConverter.ToInt32(buffer, p) * sx + ox;
                            double y = BitConverter.ToInt32(buffer, p + 4) * sy + oy;
                            double z = BitConverter.ToInt32(buffer, p + 8) * sz + oz;
                            if (reproject)
                            {
                                double lat, lon, nx, ny;
                                from.Inverse(x, y, out lat, out lon);
                                to.Forward(lat, lon, out nx, out ny);
                                if (geoid != null) z -= geoid.Undulation(lat, lon);
                                x = nx;
                                y = ny;
                                PutInt(buffer, p, Int(x, nox, nsx));
                                PutInt(buffer, p + 4, Int(y, noy, nsy));
                                PutInt(buffer, p + 8, Int(z, oz, sz));
                            }
                            if (x < lx) lx = x; if (x > hx) hx = x;
                            if (y < ly) ly = y; if (y > hy) hy = y;
                            if (z < lz) lz = z; if (z > hz) hz = z;
                        }
                        lock (bounds)
                        {
                            minX = Math.Min(minX, lx); maxX = Math.Max(maxX, hx);
                            minY = Math.Min(minY, ly); maxY = Math.Max(maxY, hy);
                            minZ = Math.Min(minZ, lz); maxZ = Math.Max(maxZ, hz);
                        }
                    });
                    if (reproject)
                    {
                        file.Position = at;
                        file.Write(buffer, 0, bytes);
                    }
                    done += records;
                    if (progress != null) progress(count == 0 ? 1 : (double)done / count);
                }

                // Cabecera del tramo: cantidad de puntos, límites y, si se reproyectó, escala y desplazamiento nuevos.
                Put(header, 107, BitConverter.GetBytes((uint)Math.Min(count, uint.MaxValue)));
                Put(header, 111, BitConverter.GetBytes((uint)Math.Min(count, uint.MaxValue)));
                for (int i = 1; i < 5; i++) Put(header, 111 + i * 4, new byte[4]);
                if (minor >= 4 && headerSize >= 255) Put(header, 247, BitConverter.GetBytes((ulong)count));
                if (minor >= 4 && headerSize >= 375) { Put(header, 255, BitConverter.GetBytes((ulong)count)); for (int i = 1; i < 15; i++) Put(header, 255 + i * 8, new byte[8]); }
                if (minor >= 4 && headerSize >= 247) { Put(header, 235, new byte[8]); Put(header, 243, new byte[4]); }
                if (count > 0)
                {
                    double[] limits = { maxX, minX, maxY, minY, maxZ, minZ };
                    for (int i = 0; i < 6; i++) Put(header, 179 + i * 8, BitConverter.GetBytes(limits[i]));
                }
                if (reproject)
                {
                    Put(header, 131, BitConverter.GetBytes(nsx));
                    Put(header, 139, BitConverter.GetBytes(nsy));
                    Put(header, 155, BitConverter.GetBytes(nox));
                    Put(header, 163, BitConverter.GetBytes(noy));
                    header[6] = (byte)(header[6] & ~0x10);
                }
                file.Position = 0;
                file.Write(header, 0, header.Length);
                // El sistema viejo ya no vale: sus registros se renombran (mismo tamaño, nada se desplaza).
                if (reproject)
                {
                    long position = headerSize;
                    byte[] vlr = new byte[54];
                    for (uint i = 0; i < vlrCount && position + 54 <= pointOffset; i++)
                    {
                        file.Position = position;
                        Fill(file, vlr);
                        if (Encoding.ASCII.GetString(vlr, 2, 16).TrimEnd('\0') == "LASF_Projection")
                        {
                            file.Position = position + 2;
                            file.Write(Encoding.ASCII.GetBytes("Lazo_Reproyectad"), 0, 16);
                        }
                        position += 54 + BitConverter.ToUInt16(vlr, 20);
                    }
                }
                // Lo que había después de los puntos del archivo original (registros extendidos) no pertenece al tramo.
                file.SetLength(pointOffset + count * recordLength);
            }
        }

        private static int Int(double value, double offset, double scale)
        {
            double n = Math.Round((value - offset) / scale);
            if (n > int.MaxValue || n < int.MinValue)
                throw new InvalidOperationException("La nube queda fuera del rango del sistema de destino; revisa que el sistema de origen sea el correcto.");
            return (int)n;
        }

        private static void PutInt(byte[] target, int at, int value)
        {
            target[at] = (byte)value;
            target[at + 1] = (byte)(value >> 8);
            target[at + 2] = (byte)(value >> 16);
            target[at + 3] = (byte)(value >> 24);
        }

        private static double D(byte[] data, int at) { return BitConverter.ToDouble(data, at); }

        private static void Put(byte[] target, int at, byte[] value) { Buffer.BlockCopy(value, 0, target, at, value.Length); }

        private static int Fill(Stream stream, byte[] buffer, int count = -1)
        {
            if (count < 0) count = buffer.Length;
            int done = 0;
            while (done < count) { int read = stream.Read(buffer, done, count - done); if (read <= 0) break; done += read; }
            return done;
        }

        /// <summary>Puntos que declara la cabecera de un LAS o LAZ (también los de 64 bits de LAS 1.4).</summary>
        public static long PointCount(string path)
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] header = new byte[375];
                int read = Fill(file, header);
                if (read < 227 || Encoding.ASCII.GetString(header, 0, 4) != "LASF") return 0;
                long count = BitConverter.ToUInt32(header, 107);
                if (header[25] >= 4 && BitConverter.ToUInt16(header, 94) >= 255 && read >= 255)
                {
                    long extended = (long)BitConverter.ToUInt64(header, 247);
                    if (extended > 0) count = extended;
                }
                return count;
            }
        }

        // ── LAZ por tramos con la LASzip de ReCap ──────────────────────────

        private const string Laszip = "laszip3.dll";
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
        [DllImport(Laszip)] private static extern int laszip_create(out IntPtr pointer);
        [DllImport(Laszip)] private static extern int laszip_destroy(IntPtr pointer);
        [DllImport(Laszip, CharSet = CharSet.Ansi)] private static extern int laszip_open_reader(IntPtr pointer, string file, out int compressed);
        [DllImport(Laszip)] private static extern int laszip_get_header_pointer(IntPtr pointer, out IntPtr header);
        [DllImport(Laszip)] private static extern int laszip_get_point_pointer(IntPtr pointer, out IntPtr point);
        [DllImport(Laszip)] private static extern int laszip_set_header(IntPtr pointer, IntPtr header);
        [DllImport(Laszip, CharSet = CharSet.Ansi)] private static extern int laszip_open_writer(IntPtr pointer, string file, int compress);
        [DllImport(Laszip)] private static extern int laszip_seek_point(IntPtr pointer, long index);
        [DllImport(Laszip)] private static extern int laszip_read_point(IntPtr pointer);
        [DllImport(Laszip)] private static extern int laszip_set_point(IntPtr pointer, IntPtr point);
        [DllImport(Laszip)] private static extern int laszip_write_point(IntPtr pointer);
        [DllImport(Laszip)] private static extern int laszip_close_reader(IntPtr pointer);
        [DllImport(Laszip)] private static extern int laszip_close_writer(IntPtr pointer);

        private static readonly object DllLock = new object();
        private static string _dllFolder;

        /// <summary>Descomprime el tramo a un LAS. Varios tramos pueden descomprimirse a la vez (un lector por tramo).</summary>
        private static void DecompressRange(CloudTile tile, string recapFolder, CancellationToken cancel)
        {
            if (recapFolder == null || !File.Exists(Path.Combine(recapFolder, Laszip)))
                throw new InvalidOperationException("Para leer LAZ hace falta la LASzip que instala ReCap.");
            lock (DllLock)
            {
                if (_dllFolder != recapFolder) { SetDllDirectory(recapFolder); _dllFolder = recapFolder; }
            }
            // LASzip abre rutas en ANSI: con acentos se le da una ruta corta sin ellos.
            string input = AnsiSafe(tile.Input), output = tile.Output;
            string plainOutput = AnsiSafe(Path.GetDirectoryName(output)) == Path.GetDirectoryName(output) && IsAnsi(Path.GetFileName(output))
                ? output : Path.Combine(Path.GetDirectoryName(output), Guid.NewGuid().ToString("N") + ".las");
            IntPtr reader, writer, header, point;
            int compressed;
            Check(laszip_create(out reader), "crear lector");
            try
            {
                Check(laszip_open_reader(reader, input, out compressed), "abrir el LAZ");
                Check(laszip_get_header_pointer(reader, out header), "leer la cabecera");
                Check(laszip_get_point_pointer(reader, out point), "leer puntos");
                if (tile.First > 0) Check(laszip_seek_point(reader, tile.First), "ir al tramo");
                Check(laszip_create(out writer), "crear escritor");
                try
                {
                    Check(laszip_set_header(writer, header), "copiar la cabecera");
                    Check(laszip_open_writer(writer, plainOutput, 0), "crear el LAS");
                    for (long i = 0; i < tile.Count; i++)
                    {
                        if ((i & 0xFFFF) == 0) cancel.ThrowIfCancellationRequested();
                        Check(laszip_read_point(reader), "leer un punto");
                        Check(laszip_set_point(writer, point), "copiar un punto");
                        Check(laszip_write_point(writer), "escribir un punto");
                    }
                    laszip_close_writer(writer);
                }
                finally { laszip_destroy(writer); }
                laszip_close_reader(reader);
            }
            finally { laszip_destroy(reader); }
            if (plainOutput != output) File.Move(plainOutput, output);
        }

        private static bool IsAnsi(string text)
        {
            return text.All(c => c < 128);
        }

        /// <summary>La ruta corta 8.3 de Windows cuando la normal tiene caracteres que LASzip no abre.</summary>
        private static string AnsiSafe(string path)
        {
            if (IsAnsi(path)) return path;
            StringBuilder shortPath = new StringBuilder(1024);
            return GetShortPathName(path, shortPath, shortPath.Capacity) > 0 && IsAnsi(shortPath.ToString()) ? shortPath.ToString() : path;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int length);

        private static void Check(int result, string what)
        {
            if (result != 0) throw new InvalidOperationException("LASzip no pudo " + what + ".");
        }
    }
}
