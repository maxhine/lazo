using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Lazo
{
    /// <summary>
    /// Reproyecta una nube LAS o LAZ con <see cref="LazoProjection"/> y deja un LAS nuevo para ReCap. Se reescriben
    /// las coordenadas de cada punto (los primeros 12 bytes de todos los formatos), se recalculan desplazamientos,
    /// escala y límites, y se quitan los registros de proyección viejos. LAZ se descomprime antes con la LASzip que
    /// instala ReCap.
    /// </summary>
    internal static class CloudReprojector
    {
        private const int Chunk = 65536;

        /// <param name="ellipsoidal">Las alturas de la nube son elipsoidales y se pasan a EGM2008 con <paramref name="geoid"/>.</param>
        public static string Reproject(string input, LazoProjection from, LazoProjection to, GeoidGrid geoid, bool ellipsoidal,
            string folder, string recapFolder, Action<double> progress, CancellationToken cancel)
        {
            Directory.CreateDirectory(folder);
            string source = input;
            string unpacked = null;
            try
            {
                if (string.Equals(Path.GetExtension(input), ".laz", StringComparison.OrdinalIgnoreCase))
                {
                    unpacked = Path.Combine(folder, Path.GetFileNameWithoutExtension(input) + ".descomprimido.las");
                    Decompress(input, unpacked, recapFolder, cancel);
                    source = unpacked;
                }
                string output = Path.Combine(folder, Path.GetFileNameWithoutExtension(input) + ".las");
                Rewrite(source, output, from, to, ellipsoidal ? geoid : null, progress, cancel);
                return output;
            }
            finally
            {
                if (unpacked != null) try { File.Delete(unpacked); } catch { }
            }
        }

        private static void Rewrite(string input, string output, LazoProjection from, LazoProjection to, GeoidGrid geoid,
            Action<double> progress, CancellationToken cancel)
        {
            using (FileStream reader = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (FileStream writer = new FileStream(output, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            {
                byte[] start = new byte[227];
                Fill(reader, start);
                if (Encoding.ASCII.GetString(start, 0, 4) != "LASF") throw new InvalidDataException(Path.GetFileName(input) + " no es un LAS válido.");
                int headerSize = BitConverter.ToUInt16(start, 94);
                reader.Position = 0;
                byte[] header = new byte[headerSize];
                Fill(reader, header);
                byte minor = header[25];
                uint pointOffset = BitConverter.ToUInt32(header, 96);
                uint vlrCount = BitConverter.ToUInt32(header, 100);
                int recordLength = BitConverter.ToUInt16(header, 105);
                long count = BitConverter.ToUInt32(header, 107);
                if (minor >= 4 && headerSize >= 255) { long extended = (long)BitConverter.ToUInt64(header, 247); if (extended > 0) count = extended; }
                if (recordLength < 12) throw new InvalidDataException("Formato de punto no válido.");
                double sx = D(header, 131), sy = D(header, 139), sz = D(header, 147);
                double ox = D(header, 155), oy = D(header, 163), oz = D(header, 171);

                // Registros: se conservan todos menos los de proyección (el sistema viejo ya no vale).
                byte[] vlrs = new byte[pointOffset - headerSize];
                Fill(reader, vlrs);
                MemoryStream kept = new MemoryStream();
                int keptCount = 0, at = 0;
                for (uint i = 0; i < vlrCount && at + 54 <= vlrs.Length; i++)
                {
                    string user = Encoding.ASCII.GetString(vlrs, at + 2, 16).TrimEnd('\0');
                    int length = BitConverter.ToUInt16(vlrs, at + 20);
                    int size = Math.Min(54 + length, vlrs.Length - at);
                    if (user != "LASF_Projection") { kept.Write(vlrs, at, size); keptCount++; }
                    at += size;
                }

                // Escala y desplazamiento del destino: milímetros en proyectadas, el centro como origen.
                double nsx = to.Geographic ? 1e-7 : (from.Geographic ? 0.001 : Math.Min(sx, 0.001));
                double nsy = to.Geographic ? 1e-7 : (from.Geographic ? 0.001 : Math.Min(sy, 0.001));
                double cx = (D(header, 179) + D(header, 187)) / 2, cy = (D(header, 195) + D(header, 203)) / 2, ncx, ncy;
                Transform(from, to, cx, cy, out ncx, out ncy);
                double nox = to.Geographic ? Math.Round(ncx) : Math.Round(ncx / 1000) * 1000;
                double noy = to.Geographic ? Math.Round(ncy) : Math.Round(ncy / 1000) * 1000;

                byte[] newHeader = (byte[])header.Clone();
                Put(newHeader, 100, BitConverter.GetBytes((uint)keptCount));
                Put(newHeader, 96, BitConverter.GetBytes((uint)(headerSize + kept.Length)));
                Put(newHeader, 131, BitConverter.GetBytes(nsx));
                Put(newHeader, 139, BitConverter.GetBytes(nsy));
                Put(newHeader, 155, BitConverter.GetBytes(nox));
                Put(newHeader, 163, BitConverter.GetBytes(noy));
                if (minor >= 4 && headerSize >= 247) { Put(newHeader, 235, new byte[8]); Put(newHeader, 243, new byte[4]); }
                newHeader[6] = (byte)(newHeader[6] & ~0x10);
                writer.Write(newHeader, 0, newHeader.Length);
                kept.WriteTo(writer);

                reader.Position = pointOffset;
                byte[] buffer = new byte[Chunk * recordLength];
                double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                long done = 0;
                while (done < count)
                {
                    cancel.ThrowIfCancellationRequested();
                    int records = (int)Math.Min(Chunk, count - done);
                    int bytes = records * recordLength;
                    if (Fill(reader, buffer, bytes) < bytes) throw new EndOfStreamException(Path.GetFileName(input) + " está incompleto.");
                    for (int r = 0; r < records; r++)
                    {
                        int p = r * recordLength;
                        double x = BitConverter.ToInt32(buffer, p) * sx + ox;
                        double y = BitConverter.ToInt32(buffer, p + 4) * sy + oy;
                        double z = BitConverter.ToInt32(buffer, p + 8) * sz + oz;
                        double lat, lon, nx, ny;
                        from.Inverse(x, y, out lat, out lon);
                        to.Forward(lat, lon, out nx, out ny);
                        if (geoid != null) z -= geoid.Undulation(lat, lon);
                        Put(buffer, p, BitConverter.GetBytes(Int(nx, nox, nsx)));
                        Put(buffer, p + 4, BitConverter.GetBytes(Int(ny, noy, nsy)));
                        Put(buffer, p + 8, BitConverter.GetBytes(Int(z, oz, sz)));
                        if (nx < minX) minX = nx; if (nx > maxX) maxX = nx;
                        if (ny < minY) minY = ny; if (ny > maxY) maxY = ny;
                        if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                    }
                    writer.Write(buffer, 0, bytes);
                    done += records;
                    if (progress != null) progress(count == 0 ? 1 : (double)done / count);
                }
                if (count > 0)
                {
                    writer.Position = 179;
                    foreach (double v in new[] { maxX, minX, maxY, minY, maxZ, minZ }) writer.Write(BitConverter.GetBytes(v), 0, 8);
                }
            }
        }

        private static void Transform(LazoProjection from, LazoProjection to, double x, double y, out double nx, out double ny)
        {
            double lat, lon;
            from.Inverse(x, y, out lat, out lon);
            to.Forward(lat, lon, out nx, out ny);
        }

        private static int Int(double value, double offset, double scale)
        {
            double n = Math.Round((value - offset) / scale);
            if (n > int.MaxValue || n < int.MinValue)
                throw new InvalidOperationException("La nube queda fuera del rango del sistema de destino; revisa que el sistema de origen sea el correcto.");
            return (int)n;
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

        // ── LAZ a LAS con la LASzip de ReCap ───────────────────────────────

        private const string Laszip = "laszip3.dll";
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
        [DllImport(Laszip)] private static extern int laszip_create(out IntPtr pointer);
        [DllImport(Laszip)] private static extern int laszip_destroy(IntPtr pointer);
        [DllImport(Laszip, CharSet = CharSet.Ansi)] private static extern int laszip_open_reader(IntPtr pointer, string file, out int compressed);
        [DllImport(Laszip)] private static extern int laszip_get_header_pointer(IntPtr pointer, out IntPtr header);
        [DllImport(Laszip)] private static extern int laszip_get_point_pointer(IntPtr pointer, out IntPtr point);
        [DllImport(Laszip)] private static extern int laszip_set_header(IntPtr pointer, IntPtr header);
        [DllImport(Laszip, CharSet = CharSet.Ansi)] private static extern int laszip_open_writer(IntPtr pointer, string file, int compress);
        [DllImport(Laszip)] private static extern int laszip_read_point(IntPtr pointer);
        [DllImport(Laszip)] private static extern int laszip_set_point(IntPtr pointer, IntPtr point);
        [DllImport(Laszip)] private static extern int laszip_write_point(IntPtr pointer);
        [DllImport(Laszip)] private static extern int laszip_close_reader(IntPtr pointer);
        [DllImport(Laszip)] private static extern int laszip_close_writer(IntPtr pointer);

        private static readonly object LaszipLock = new object();

        private static void Decompress(string input, string output, string recapFolder, CancellationToken cancel)
        {
            if (recapFolder == null || !File.Exists(Path.Combine(recapFolder, Laszip)))
                throw new InvalidOperationException("Para reproyectar LAZ hace falta la LASzip que instala ReCap.");
            long count = PointCount(input);
            // LASzip abre rutas en ANSI: una copia con nombre simple evita problemas con acentos.
            string plain = Path.Combine(Path.GetDirectoryName(output), "entrada-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".laz");
            File.Copy(input, plain);
            try
            {
                lock (LaszipLock)
                {
                    SetDllDirectory(recapFolder);
                    IntPtr reader, writer, header, point;
                    int compressed;
                    Check(laszip_create(out reader), "crear lector");
                    Check(laszip_open_reader(reader, plain, out compressed), "abrir el LAZ");
                    Check(laszip_get_header_pointer(reader, out header), "leer la cabecera");
                    Check(laszip_get_point_pointer(reader, out point), "leer puntos");
                    Check(laszip_create(out writer), "crear escritor");
                    Check(laszip_set_header(writer, header), "copiar la cabecera");
                    Check(laszip_open_writer(writer, output, 0), "crear el LAS");
                    try
                    {
                        for (long i = 0; i < count; i++)
                        {
                            if ((i & 0xFFFF) == 0) cancel.ThrowIfCancellationRequested();
                            Check(laszip_read_point(reader), "leer un punto");
                            Check(laszip_set_point(writer, point), "copiar un punto");
                            Check(laszip_write_point(writer), "escribir un punto");
                        }
                    }
                    finally
                    {
                        laszip_close_writer(writer);
                        laszip_close_reader(reader);
                        laszip_destroy(writer);
                        laszip_destroy(reader);
                    }
                }
            }
            finally { try { File.Delete(plain); } catch { } }
        }

        private static long PointCount(string path)
        {
            using (BinaryReader reader = new BinaryReader(File.OpenRead(path)))
            {
                byte[] header = reader.ReadBytes(375);
                long count = BitConverter.ToUInt32(header, 107);
                if (header[25] >= 4 && BitConverter.ToUInt16(header, 94) >= 255 && header.Length >= 255)
                {
                    long extended = (long)BitConverter.ToUInt64(header, 247);
                    if (extended > 0) count = extended;
                }
                return count;
            }
        }

        private static void Check(int result, string what)
        {
            if (result != 0) throw new InvalidOperationException("LASzip no pudo " + what + ".");
        }
    }
}
