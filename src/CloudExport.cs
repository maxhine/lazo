using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lazo
{
    /// <summary>Conjunto de celdas ocupadas (una por voxel) para reducir puntos sin ordenar la nube.</summary>
    internal sealed class VoxelSet
    {
        private ulong[] _keys = new ulong[1 << 16];
        private int _count;

        public int Count { get { return _count; } }

        /// <summary>true si la celda no estaba ocupada (el punto se queda).</summary>
        public bool Add(double x, double y, double z, double cell)
        {
            ulong ix = (ulong)((long)Math.Floor(x / cell)) & 0x1FFFFF;
            ulong iy = (ulong)((long)Math.Floor(y / cell)) & 0x1FFFFF;
            ulong iz = (ulong)((long)Math.Floor(z / cell)) & 0x1FFFFF;
            ulong key = ((ix << 42) | (iy << 21) | iz) + 1;
            if ((_count + 1) * 2 > _keys.Length) Grow();
            int mask = _keys.Length - 1;
            int slot = (int)((key * 0x9E3779B97F4A7C15UL) >> 40) & mask;
            while (true)
            {
                ulong at = _keys[slot];
                if (at == 0) { _keys[slot] = key; _count++; return true; }
                if (at == key) return false;
                slot = (slot + 1) & mask;
            }
        }

        private void Grow()
        {
            ulong[] old = _keys;
            _keys = new ulong[old.Length * 2];
            _count = 0;
            int mask = _keys.Length - 1;
            foreach (ulong key in old)
            {
                if (key == 0) continue;
                int slot = (int)((key * 0x9E3779B97F4A7C15UL) >> 40) & mask;
                while (_keys[slot] != 0) slot = (slot + 1) & mask;
                _keys[slot] = key;
                _count++;
            }
        }
    }

    /// <summary>
    /// Formatos que escribe Lazo mismo (DeCap solo sabe .rcp y .rcs): LAS, XYZ, PTS y CSV, desde LAS, LAZ o E57, con
    /// reducción de puntos por celdas y, para LAS/LAZ, la reproyección propia de Lazo.
    /// </summary>
    internal static class CloudExport
    {
        public static readonly string[] Formats = { "las", "xyz", "pts", "csv" };

        public static bool IsLazoFormat(string format) { return Formats.Contains(format); }

        public static string Describe(string format)
        {
            switch (format)
            {
                case "las": return "LAS 1.2 con intensidad y color si los hay. Lo abren casi todos los programas.";
                case "xyz": return "Texto «x y z» (y color 0–255 si lo hay).";
                case "pts": return "Texto estilo PTS: «x y z intensidad r g b», con la cantidad de puntos en la primera línea.";
                case "csv": return "Texto separado por comas con encabezado: X, Y, Z, intensidad y color.";
                default: return "";
            }
        }

        public static string Extension(string format) { return "." + format; }

        public static Task<PointCloudResult> RunAsync(string recapFolder, PointCloudJob job, string name, Action<double, string> progress, CancellationToken cancel)
        {
            return Task.Run(() => Run(recapFolder, job, name, progress, cancel));
        }

        private static PointCloudResult Run(string recapFolder, PointCloudJob job, string name, Action<double, string> progress, CancellationToken cancel)
        {
            string output = Path.Combine(job.OutputFolder, name + Extension(job.Format));
            string partial = output + ".lazo-parcial";
            try
            {
                GeoidGrid geoid = null;
                bool reproject = job.LazoFrom != null && job.LazoTo != null;
                if (reproject && job.LazoTo.Egm2008 && job.Ellipsoidal) geoid = new GeoidGrid(job.GeoidPath);
                long total = 0;
                foreach (string input in job.Inputs)
                    using (CloudReader reader = CloudReader.Open(input, recapFolder)) total += reader.Count;
                double cell = job.DecimationMm > 0 ? job.DecimationMm / 1000.0 * UnitToMeters(job) : 0;
                VoxelSet cells = cell > 0 ? new VoxelSet() : null;
                long read = 0, kept = 0;
                CloudPoint[] block = new CloudPoint[1 << 20];
                using (PointSink sink = PointSink.Create(job.Format, partial, job.LazoTo != null && job.LazoTo.Geographic))
                {
                    foreach (string input in job.Inputs)
                    {
                        using (CloudReader reader = CloudReader.Open(input, recapFolder))
                        {
                            if (reproject && geoid != null) PrepareGeoid(geoid, input, job.LazoFrom);
                            sink.Color |= reader.HasColor;
                            int got;
                            while ((got = reader.Read(block, block.Length)) > 0)
                            {
                                cancel.ThrowIfCancellationRequested();
                                int n = got;
                                if (reproject) ReprojectBlock(block, n, job.LazoFrom, job.LazoTo, job.Ellipsoidal ? geoid : null, cancel);
                                read += n;
                                if (cells != null)
                                {
                                    int w = 0;
                                    for (int i = 0; i < n; i++)
                                        if (cells.Add(block[i].X, block[i].Y, block[i].Z, cell)) block[w++] = block[i];
                                    n = w;
                                }
                                sink.Write(block, n);
                                kept += n;
                                if (progress != null) progress(total == 0 ? 1 : Math.Min(0.99, (double)read / total),
                                    "Escribiendo " + Path.GetExtension(output).ToUpperInvariant() + "…");
                            }
                        }
                    }
                    sink.Finish();
                }
                if (File.Exists(output)) File.Delete(output);
                File.Move(partial, output);
                return new PointCloudResult { Ok = true, Output = output, Message = "Listo: " + Path.GetFileName(output) + " (" + kept.ToString("N0", CultureInfo.CurrentCulture) + " de " + read.ToString("N0", CultureInfo.CurrentCulture) + " puntos)" };
            }
            catch (OperationCanceledException)
            {
                TryDelete(partial);
                return new PointCloudResult { Message = "Conversión cancelada." };
            }
            catch (Exception ex)
            {
                TryDelete(partial);
                return new PointCloudResult { Message = ex.Message };
            }
        }

        /// <summary>La reducción va en milímetros de la unidad real: si el archivo está en pies o pulgadas, se pasa a esa unidad.</summary>
        private static double UnitToMeters(PointCloudJob job)
        {
            // La reducción se pide en mm reales; las coordenadas están en la unidad del archivo (solo LAS/LAZ).
            bool lasOrLaz = job.Inputs.All(i => !i.EndsWith(".e57", StringComparison.OrdinalIgnoreCase));
            if (!lasOrLaz) return 1;
            switch (job.Unit)
            {
                case 0: return 1000;
                case 1: return 100;
                case 3: return 1 / 0.0254;
                case 4: return 1 / 0.3048;
                case 5: return 1 / 0.3048006096;
                default: return 1;
            }
        }

        private static void PrepareGeoid(GeoidGrid geoid, string input, LazoProjection from)
        {
            LasInfo info = LasInfo.Bounds(input);
            if (info == null) return;
            double latA, lonA, latB, lonB;
            from.Inverse(info.MaxX, info.MaxY, out latA, out lonA);
            from.Inverse(info.MinX, info.MinY, out latB, out lonB);
            geoid.Prepare(Math.Min(latA, latB) - 0.1, Math.Max(latA, latB) + 0.1);
        }

        private static void ReprojectBlock(CloudPoint[] block, int count, LazoProjection from, LazoProjection to, GeoidGrid geoid, CancellationToken cancel)
        {
            int workers = Math.Max(1, Environment.ProcessorCount);
            int slice = Math.Max(4096, (count + workers - 1) / workers);
            Parallel.For(0, (count + slice - 1) / slice, new ParallelOptions { CancellationToken = cancel }, part =>
            {
                int end = Math.Min(count, (part + 1) * slice);
                for (int i = part * slice; i < end; i++)
                {
                    double lat, lon, x, y;
                    from.Inverse(block[i].X, block[i].Y, out lat, out lon);
                    to.Forward(lat, lon, out x, out y);
                    if (geoid != null) block[i].Z -= geoid.Undulation(lat, lon);
                    block[i].X = x;
                    block[i].Y = y;
                }
            });
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>Destino de los puntos: escribe por bloques y cierra el archivo con su cabecera final.</summary>
    internal abstract class PointSink : IDisposable
    {
        public bool Color;
        public abstract void Write(CloudPoint[] points, int count);
        public abstract void Finish();
        public abstract void Dispose();

        public static PointSink Create(string format, string path, bool geographic)
        {
            if (format == "las") return new LasSink(path, geographic);
            return new TextSink(path, format);
        }
    }

    internal sealed class LasSink : PointSink
    {
        private readonly FileStream _file;
        private readonly bool _geographic;
        private long _count;
        private double _ox, _oy, _oz, _scale;
        private bool _started, _color;
        private double _minX = double.MaxValue, _minY = double.MaxValue, _minZ = double.MaxValue, _maxX = double.MinValue, _maxY = double.MinValue, _maxZ = double.MinValue;
        private byte[] _bytes = new byte[0];

        public LasSink(string path, bool geographic)
        {
            _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
            _geographic = geographic;
            _file.Write(new byte[227], 0, 227);
        }

        public override void Write(CloudPoint[] points, int count)
        {
            if (count == 0) return;
            if (!_started)
            {
                _started = true;
                _color = Color;
                _scale = _geographic ? 1e-7 : 0.001;
                _ox = _geographic ? Math.Round(points[0].X) : Math.Round(points[0].X / 1000) * 1000;
                _oy = _geographic ? Math.Round(points[0].Y) : Math.Round(points[0].Y / 1000) * 1000;
                _oz = Math.Round(points[0].Z / 100) * 100;
            }
            int record = _color ? 26 : 20;
            if (_bytes.Length < count * record) _bytes = new byte[count * record];
            for (int i = 0; i < count; i++)
            {
                CloudPoint p = points[i];
                int p0 = i * record;
                Put(_bytes, p0, Int(p.X, _ox));
                Put(_bytes, p0 + 4, Int(p.Y, _oy));
                Put(_bytes, p0 + 8, Int(p.Z, _oz));
                _bytes[p0 + 12] = (byte)p.Intensity;
                _bytes[p0 + 13] = (byte)(p.Intensity >> 8);
                _bytes[p0 + 14] = 0x11;
                _bytes[p0 + 15] = 0;
                _bytes[p0 + 16] = 0;
                _bytes[p0 + 17] = 0;
                _bytes[p0 + 18] = 0;
                _bytes[p0 + 19] = 0;
                if (_color)
                {
                    _bytes[p0 + 20] = (byte)p.R; _bytes[p0 + 21] = (byte)(p.R >> 8);
                    _bytes[p0 + 22] = (byte)p.G; _bytes[p0 + 23] = (byte)(p.G >> 8);
                    _bytes[p0 + 24] = (byte)p.B; _bytes[p0 + 25] = (byte)(p.B >> 8);
                }
                if (p.X < _minX) _minX = p.X; if (p.X > _maxX) _maxX = p.X;
                if (p.Y < _minY) _minY = p.Y; if (p.Y > _maxY) _maxY = p.Y;
                if (p.Z < _minZ) _minZ = p.Z; if (p.Z > _maxZ) _maxZ = p.Z;
            }
            _file.Write(_bytes, 0, count * record);
            _count += count;
        }

        private int Int(double value, double offset)
        {
            double n = Math.Round((value - offset) / _scale);
            if (n > int.MaxValue || n < int.MinValue) throw new InvalidOperationException("La nube queda fuera del rango que admite el LAS.");
            return (int)n;
        }

        private static void Put(byte[] target, int at, int value)
        {
            target[at] = (byte)value; target[at + 1] = (byte)(value >> 8); target[at + 2] = (byte)(value >> 16); target[at + 3] = (byte)(value >> 24);
        }

        public override void Finish()
        {
            byte[] h = new byte[227];
            Encoding.ASCII.GetBytes("LASF").CopyTo(h, 0);
            h[24] = 1; h[25] = 2;
            Encoding.ASCII.GetBytes("Lazo").CopyTo(h, 26);
            Encoding.ASCII.GetBytes("Lazo").CopyTo(h, 58);
            DateTime now = DateTime.Now;
            BitConverter.GetBytes((ushort)now.DayOfYear).CopyTo(h, 90);
            BitConverter.GetBytes((ushort)now.Year).CopyTo(h, 92);
            BitConverter.GetBytes((ushort)227).CopyTo(h, 94);
            BitConverter.GetBytes((uint)227).CopyTo(h, 96);
            h[104] = (byte)(_color ? 2 : 0);
            BitConverter.GetBytes((ushort)(_color ? 26 : 20)).CopyTo(h, 105);
            BitConverter.GetBytes((uint)Math.Min(_count, uint.MaxValue)).CopyTo(h, 107);
            BitConverter.GetBytes((uint)Math.Min(_count, uint.MaxValue)).CopyTo(h, 111);
            BitConverter.GetBytes(_scale).CopyTo(h, 131); BitConverter.GetBytes(_scale).CopyTo(h, 139);
            BitConverter.GetBytes(_geographic ? _scale : 0.001).CopyTo(h, 147);
            BitConverter.GetBytes(_ox).CopyTo(h, 155); BitConverter.GetBytes(_oy).CopyTo(h, 163); BitConverter.GetBytes(_oz).CopyTo(h, 171);
            if (_count > 0)
            {
                double[] limits = { _maxX, _minX, _maxY, _minY, _maxZ, _minZ };
                for (int i = 0; i < 6; i++) BitConverter.GetBytes(limits[i]).CopyTo(h, 179 + i * 8);
            }
            _file.Position = 0;
            _file.Write(h, 0, h.Length);
        }

        public override void Dispose() { _file.Dispose(); }
    }

    internal sealed class TextSink : PointSink
    {
        private readonly FileStream _file;
        private readonly StreamWriter _writer;
        private readonly string _format;
        private long _count;
        private bool _header;

        public TextSink(string path, string format)
        {
            _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
            _writer = new StreamWriter(_file, new UTF8Encoding(false), 1 << 20) { NewLine = "\n" };
            _format = format;
            if (format == "pts") _writer.WriteLine(new string(' ', 12));
        }

        public override void Write(CloudPoint[] points, int count)
        {
            if (!_header && _format == "csv")
            {
                _writer.WriteLine(Color ? "X,Y,Z,Intensidad,R,G,B" : "X,Y,Z,Intensidad");
                _header = true;
            }
            CultureInfo c = CultureInfo.InvariantCulture;
            StringBuilder line = new StringBuilder(96);
            for (int i = 0; i < count; i++)
            {
                CloudPoint p = points[i];
                line.Length = 0;
                string sep = _format == "csv" ? "," : " ";
                line.Append(p.X.ToString("0.0000", c)).Append(sep).Append(p.Y.ToString("0.0000", c)).Append(sep).Append(p.Z.ToString("0.0000", c));
                if (_format == "pts" || _format == "csv") line.Append(sep).Append(p.Intensity >> 8);
                if (Color) line.Append(sep).Append(p.R >> 8).Append(sep).Append(p.G >> 8).Append(sep).Append(p.B >> 8);
                else if (_format == "pts") line.Append(sep).Append("255").Append(sep).Append("255").Append(sep).Append("255");
                _writer.WriteLine(line.ToString());
            }
            _count += count;
        }

        public override void Finish()
        {
            _writer.Flush();
            if (_format == "pts")
            {
                _file.Position = 0;
                byte[] first = Encoding.ASCII.GetBytes(_count.ToString(CultureInfo.InvariantCulture).PadRight(12));
                _file.Write(first, 0, Math.Min(11, first.Length));
            }
        }

        public override void Dispose() { _writer.Dispose(); }
    }
}
