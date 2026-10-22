using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace Lazo
{
    /// <summary>Un punto leído de una nube, ya en coordenadas reales (no enteros escalados).</summary>
    internal struct CloudPoint
    {
        public double X, Y, Z;
        public ushort Intensity;
        public ushort R, G, B;
    }

    /// <summary>
    /// Lectura secuencial de nubes de puntos LAS, LAZ y E57 para la vista previa y para los formatos que escribe
    /// Lazo. ReCap/DeCap sigue siendo quien escribe .rcp y .rcs.
    /// </summary>
    internal abstract class CloudReader : IDisposable
    {
        public long Count;
        public bool HasColor;
        public bool CanSeek;

        /// <summary>Lee hasta <paramref name="max"/> puntos; devuelve cuántos leyó (0 al terminar).</summary>
        public abstract int Read(CloudPoint[] buffer, int max);
        /// <summary>Salta al punto <paramref name="index"/> (solo si <see cref="CanSeek"/>).</summary>
        public abstract void Seek(long index);
        public abstract void Dispose();

        public static CloudReader Open(string path, string recapFolder)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".las") return new LasReader(path);
            if (ext == ".laz") return new LazReader(path, recapFolder);
            if (ext == ".e57") return new E57Reader(path);
            throw new NotSupportedException("Formato no compatible: " + ext);
        }

        public static bool Readable(string path) { return Path.GetExtension(path).ToLowerInvariant() == ".las" || Path.GetExtension(path).ToLowerInvariant() == ".laz" || Path.GetExtension(path).ToLowerInvariant() == ".e57"; }

        // Utilidades comunes a los lectores LAS/LAZ: la cabecera estándar vive en los primeros bytes de los dos.
        protected static int ColorOffset(int format)
        {
            switch (format)
            {
                case 2: return 20;
                case 3: return 28;
                case 5: return 28;
                case 7: return 30;
                case 8: return 30;
                case 10: return 30;
                default: return -1;
            }
        }
    }

    /// <summary>Datos de la cabecera LAS que comparten LAS y LAZ.</summary>
    internal sealed class LasInfo
    {
        public double Sx, Sy, Sz, Ox, Oy, Oz;
        public double MaxX, MinX, MaxY, MinY, MaxZ, MinZ;
        public uint PointOffset;
        public int Format, RecordLength;
        public long Count;

        /// <summary>Cabecera de un LAS/LAZ, o null si el archivo es de otro formato (E57).</summary>
        public static LasInfo Bounds(string path)
        {
            try { return Read(path); } catch (InvalidDataException) { return null; }
        }

        public static LasInfo Read(string path)
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] h = new byte[375];
                int read = 0;
                while (read < h.Length) { int n = file.Read(h, read, h.Length - read); if (n <= 0) break; read += n; }
                if (read < 227 || Encoding.ASCII.GetString(h, 0, 4) != "LASF") throw new InvalidDataException(Path.GetFileName(path) + " no es un LAS/LAZ válido.");
                LasInfo info = new LasInfo
                {
                    PointOffset = BitConverter.ToUInt32(h, 96),
                    Format = h[104] & 0x3F,
                    RecordLength = BitConverter.ToUInt16(h, 105),
                    Sx = BitConverter.ToDouble(h, 131), Sy = BitConverter.ToDouble(h, 139), Sz = BitConverter.ToDouble(h, 147),
                    Ox = BitConverter.ToDouble(h, 155), Oy = BitConverter.ToDouble(h, 163), Oz = BitConverter.ToDouble(h, 171),
                    Count = BitConverter.ToUInt32(h, 107),
                    MaxX = BitConverter.ToDouble(h, 179), MinX = BitConverter.ToDouble(h, 187),
                    MaxY = BitConverter.ToDouble(h, 195), MinY = BitConverter.ToDouble(h, 203),
                    MaxZ = BitConverter.ToDouble(h, 211), MinZ = BitConverter.ToDouble(h, 219)
                };
                if (h[25] >= 4 && BitConverter.ToUInt16(h, 94) >= 255 && read >= 255)
                {
                    long extended = (long)BitConverter.ToUInt64(h, 247);
                    if (extended > 0) info.Count = extended;
                }
                return info;
            }
        }
    }

    internal sealed class LasReader : CloudReader
    {
        private readonly FileStream _file;
        private readonly LasInfo _info;
        private readonly int _color;
        private long _next;
        private readonly byte[] _buffer;

        public LasReader(string path)
        {
            _info = LasInfo.Read(path);
            if (_info.RecordLength < 12) throw new InvalidDataException("Formato de punto no válido.");
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
            Count = _info.Count;
            _color = ColorOffset(_info.Format);
            HasColor = _color >= 0 && _color + 6 <= _info.RecordLength;
            CanSeek = true;
            _file.Position = _info.PointOffset;
            _buffer = new byte[(1 << 20) / _info.RecordLength * _info.RecordLength + _info.RecordLength];
        }

        public override void Seek(long index)
        {
            _next = Math.Max(0, Math.Min(Count, index));
            _file.Position = _info.PointOffset + _next * _info.RecordLength;
        }

        public override int Read(CloudPoint[] buffer, int max)
        {
            int record = _info.RecordLength;
            int want = (int)Math.Min(Math.Min(max, _buffer.Length / record), Count - _next);
            if (want <= 0) return 0;
            int bytes = want * record, got = 0;
            while (got < bytes) { int n = _file.Read(_buffer, got, bytes - got); if (n <= 0) break; got += n; }
            want = got / record;
            for (int i = 0; i < want; i++)
            {
                int p = i * record;
                buffer[i].X = BitConverter.ToInt32(_buffer, p) * _info.Sx + _info.Ox;
                buffer[i].Y = BitConverter.ToInt32(_buffer, p + 4) * _info.Sy + _info.Oy;
                buffer[i].Z = BitConverter.ToInt32(_buffer, p + 8) * _info.Sz + _info.Oz;
                buffer[i].Intensity = BitConverter.ToUInt16(_buffer, p + 12);
                if (HasColor)
                {
                    buffer[i].R = BitConverter.ToUInt16(_buffer, p + _color);
                    buffer[i].G = BitConverter.ToUInt16(_buffer, p + _color + 2);
                    buffer[i].B = BitConverter.ToUInt16(_buffer, p + _color + 4);
                }
            }
            _next += want;
            return want;
        }

        public override void Dispose() { _file.Dispose(); }
    }

    /// <summary>LAZ con la LASzip que instala ReCap (laszip3.dll); permite saltar a un punto (LAZ con trozos).</summary>
    internal sealed class LazReader : CloudReader
    {
        private const string Laszip = "laszip3.dll";
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int length);
        [DllImport(Laszip)] private static extern int laszip_create(out IntPtr pointer);
        [DllImport(Laszip)] private static extern int laszip_destroy(IntPtr pointer);
        [DllImport(Laszip, CharSet = CharSet.Ansi)] private static extern int laszip_open_reader(IntPtr pointer, string file, out int compressed);
        [DllImport(Laszip)] private static extern int laszip_get_point_pointer(IntPtr pointer, out IntPtr point);
        [DllImport(Laszip)] private static extern int laszip_seek_point(IntPtr pointer, long index);
        [DllImport(Laszip)] private static extern int laszip_read_point(IntPtr pointer);
        [DllImport(Laszip)] private static extern int laszip_close_reader(IntPtr pointer);

        private static readonly object DllLock = new object();
        private static string _folder;

        private readonly LasInfo _info;
        private IntPtr _reader, _point;
        private long _next;

        public LazReader(string path, string recapFolder)
        {
            if (recapFolder == null || !File.Exists(Path.Combine(recapFolder, Laszip)))
                throw new InvalidOperationException("Para leer LAZ hace falta la LASzip que instala ReCap.");
            lock (DllLock) { if (_folder != recapFolder) { SetDllDirectory(recapFolder); _folder = recapFolder; } }
            _info = LasInfo.Read(path);
            Count = _info.Count;
            HasColor = ColorOffset(_info.Format) >= 0;
            CanSeek = true;
            string ansi = path;
            if (ansi.Any(c => c > 127))
            {
                StringBuilder shortPath = new StringBuilder(1024);
                if (GetShortPathName(path, shortPath, shortPath.Capacity) > 0) ansi = shortPath.ToString();
            }
            int compressed;
            if (laszip_create(out _reader) != 0) throw new InvalidOperationException("LASzip no pudo crear el lector.");
            if (laszip_open_reader(_reader, ansi, out compressed) != 0 || laszip_get_point_pointer(_reader, out _point) != 0)
            {
                laszip_destroy(_reader);
                _reader = IntPtr.Zero;
                throw new InvalidOperationException("LASzip no pudo abrir " + Path.GetFileName(path) + ".");
            }
        }

        public override void Seek(long index)
        {
            _next = Math.Max(0, Math.Min(Count, index));
            if (_next < Count && laszip_seek_point(_reader, _next) != 0) throw new InvalidOperationException("LASzip no pudo saltar a esa zona.");
        }

        public override int Read(CloudPoint[] buffer, int max)
        {
            int want = (int)Math.Min(max, Count - _next);
            int done = 0;
            for (; done < want; done++)
            {
                if (laszip_read_point(_reader) != 0) break;
                buffer[done].X = Marshal.ReadInt32(_point, 0) * _info.Sx + _info.Ox;
                buffer[done].Y = Marshal.ReadInt32(_point, 4) * _info.Sy + _info.Oy;
                buffer[done].Z = Marshal.ReadInt32(_point, 8) * _info.Sz + _info.Oz;
                buffer[done].Intensity = (ushort)Marshal.ReadInt16(_point, 12);
                if (HasColor)
                {
                    buffer[done].R = (ushort)Marshal.ReadInt16(_point, 40);
                    buffer[done].G = (ushort)Marshal.ReadInt16(_point, 42);
                    buffer[done].B = (ushort)Marshal.ReadInt16(_point, 44);
                }
            }
            _next += done;
            return done;
        }

        public override void Dispose()
        {
            if (_reader != IntPtr.Zero) { laszip_close_reader(_reader); laszip_destroy(_reader); _reader = IntPtr.Zero; }
        }
    }

    /// <summary>
    /// E57 con coordenadas cartesianas (primer escaneo con puntos): páginas de 1024 bytes con CRC, XML con el
    /// prototipo y paquetes con campos empaquetados por bits. Aplica la pose del escaneo si la trae.
    /// </summary>
    internal sealed class E57Reader : CloudReader
    {
        private enum Kind { Float32, Float64, Integer }

        private sealed class Field
        {
            public string Name;
            public Kind Kind;
            public int Bits;
            public long Min;
            public double Scale = 1, Offset;
            public int Stream;
            public byte[] Pending = new byte[0];
            public long BitPos;
            public List<double> Values = new List<double>();
            public int Head;
        }

        private readonly FileStream _file;
        private readonly string _path;
        private readonly List<Field> _fields = new List<Field>();
        private Field _x, _y, _z, _i, _r, _g, _b, _invalid;
        private long _dataStart;
        private long _logicalPosition;
        private long _produced;
        private double _intMin, _intMax = 1, _colMax = 255;
        private readonly double[] _rotation = { 1, 0, 0, 0 };
        private readonly double[] _translation = { 0, 0, 0 };
        private int _streams;
        private long _records;

        public E57Reader(string path)
        {
            _path = path;
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20);
            byte[] head = new byte[48];
            if (_file.Read(head, 0, 48) < 48 || Encoding.ASCII.GetString(head, 0, 8) != "ASTM-E57") throw new InvalidDataException("No es un E57 válido.");
            long xmlOffset = (long)BitConverter.ToUInt64(head, 24), xmlLength = (long)BitConverter.ToUInt64(head, 32);
            byte[] xml = ReadLogical(PhysicalToLogical(xmlOffset), (int)xmlLength);
            ParseXml(xml);
            HasColor = _r != null && _g != null && _b != null;
            Count = _records;
            CanSeek = false;
            Reset();
        }

        private static long PhysicalToLogical(long physical) { return physical / 1024 * 1020 + physical % 1024; }

        private byte[] ReadLogical(long logical, int length)
        {
            byte[] result = new byte[length];
            int done = 0;
            byte[] page = new byte[1024];
            while (done < length)
            {
                long pageIndex = logical / 1020;
                int inside = (int)(logical % 1020);
                _file.Position = pageIndex * 1024;
                int read = 0;
                while (read < 1024) { int n = _file.Read(page, read, 1024 - read); if (n <= 0) break; read += n; }
                if (read < inside + 1) throw new EndOfStreamException("El E57 está incompleto.");
                int take = Math.Min(length - done, Math.Min(1020, read) - inside);
                if (take <= 0) throw new EndOfStreamException("El E57 está incompleto.");
                Buffer.BlockCopy(page, inside, result, done, take);
                done += take;
                logical += take;
            }
            return result;
        }

        private static XmlElement Child(XmlElement parent, string name)
        {
            foreach (XmlNode node in parent.ChildNodes)
            {
                XmlElement e = node as XmlElement;
                if (e != null && e.LocalName == name) return e;
            }
            return null;
        }

        private static double Num(XmlElement e, double fallback)
        {
            double value;
            return e != null && double.TryParse(e.InnerText.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        private void ParseXml(byte[] xml)
        {
            XmlDocument doc = new XmlDocument();
            using (MemoryStream stream = new MemoryStream(xml))
                doc.Load(stream);
            XmlElement root = doc.DocumentElement;
            XmlElement data3D = Child(root, "data3D");
            if (data3D == null) throw new InvalidDataException("El E57 no trae nubes de puntos.");
            XmlElement scan = null, points = null;
            foreach (XmlNode node in data3D.ChildNodes)
            {
                XmlElement candidate = node as XmlElement;
                if (candidate == null) continue;
                XmlElement p = Child(candidate, "points");
                if (p != null) { scan = candidate; points = p; break; }
            }
            if (points == null) throw new InvalidDataException("El E57 no trae nubes de puntos.");
            _dataStart = long.Parse(points.GetAttribute("fileOffset"), System.Globalization.CultureInfo.InvariantCulture);
            _records = long.Parse(points.GetAttribute("recordCount"), System.Globalization.CultureInfo.InvariantCulture);
            XmlElement proto = Child(points, "prototype");
            if (proto == null) throw new InvalidDataException("E57 sin prototipo de puntos.");
            int streamIndex = 0;
            foreach (XmlNode node in proto.ChildNodes)
            {
                XmlElement e = node as XmlElement;
                if (e == null) continue;
                Field field = new Field { Name = e.LocalName, Stream = streamIndex++ };
                string type = e.GetAttribute("type");
                if (type == "Float")
                {
                    field.Kind = e.GetAttribute("precision") == "single" ? Kind.Float32 : Kind.Float64;
                }
                else if (type == "Integer" || type == "ScaledInteger")
                {
                    long min = e.HasAttribute("minimum") ? long.Parse(e.GetAttribute("minimum"), System.Globalization.CultureInfo.InvariantCulture) : long.MinValue / 2;
                    long max = e.HasAttribute("maximum") ? long.Parse(e.GetAttribute("maximum"), System.Globalization.CultureInfo.InvariantCulture) : long.MaxValue / 2;
                    field.Kind = Kind.Integer;
                    field.Min = min;
                    field.Bits = BitsFor(max - min);
                    if (type == "ScaledInteger")
                    {
                        field.Scale = e.HasAttribute("scale") ? double.Parse(e.GetAttribute("scale"), System.Globalization.CultureInfo.InvariantCulture) : 1;
                        field.Offset = e.HasAttribute("offset") ? double.Parse(e.GetAttribute("offset"), System.Globalization.CultureInfo.InvariantCulture) : 0;
                    }
                }
                else
                {
                    // Tipos que no usamos (cadenas, etc.) no se esperan en un prototipo de puntos.
                    field.Kind = Kind.Float64;
                    field.Name = "?" + field.Name;
                }
                _fields.Add(field);
            }
            _streams = streamIndex;
            _x = _fields.FirstOrDefault(f => f.Name == "cartesianX");
            _y = _fields.FirstOrDefault(f => f.Name == "cartesianY");
            _z = _fields.FirstOrDefault(f => f.Name == "cartesianZ");
            if (_x == null || _y == null || _z == null) throw new NotSupportedException("Este E57 usa coordenadas esféricas; por ahora solo se leen las cartesianas.");
            _i = _fields.FirstOrDefault(f => f.Name == "intensity");
            _r = _fields.FirstOrDefault(f => f.Name == "colorRed");
            _g = _fields.FirstOrDefault(f => f.Name == "colorGreen");
            _b = _fields.FirstOrDefault(f => f.Name == "colorBlue");
            _invalid = _fields.FirstOrDefault(f => f.Name == "cartesianInvalidState");
            XmlElement limits = Child(scan, "intensityLimits");
            if (limits != null) { _intMin = Num(Child(limits, "intensityMinimum"), 0); _intMax = Num(Child(limits, "intensityMaximum"), 1); }
            else if (_i != null && _i.Kind == Kind.Integer) { _intMin = _i.Min; _intMax = _i.Min + (Math.Pow(2, _i.Bits) - 1); }
            XmlElement colors = Child(scan, "colorLimits");
            if (colors != null) _colMax = Math.Max(1, Num(Child(colors, "colorRedMaximum"), 255));
            XmlElement pose = Child(scan, "pose");
            if (pose != null)
            {
                XmlElement rot = Child(pose, "rotation"), tr = Child(pose, "translation");
                if (rot != null)
                {
                    _rotation[0] = Num(Child(rot, "w"), 1); _rotation[1] = Num(Child(rot, "x"), 0);
                    _rotation[2] = Num(Child(rot, "y"), 0); _rotation[3] = Num(Child(rot, "z"), 0);
                }
                if (tr != null) { _translation[0] = Num(Child(tr, "x"), 0); _translation[1] = Num(Child(tr, "y"), 0); _translation[2] = Num(Child(tr, "z"), 0); }
            }
        }

        private static int BitsFor(long range)
        {
            if (range <= 0) return 0;
            int bits = 0;
            ulong v = (ulong)range;
            while (v > 0) { bits++; v >>= 1; }
            return bits;
        }

        private void Reset()
        {
            foreach (Field f in _fields) { f.Pending = new byte[0]; f.BitPos = 0; f.Values.Clear(); f.Head = 0; }
            byte[] section = ReadLogical(PhysicalToLogical(_dataStart), 32);
            long dataPhysical = (long)BitConverter.ToUInt64(section, 16);
            _logicalPosition = PhysicalToLogical(dataPhysical);
            _produced = 0;
        }

        public override void Seek(long index)
        {
            if (index <= 0) { Reset(); return; }
            throw new NotSupportedException("El E57 solo se recorre desde el principio.");
        }

        /// <summary>Lee un paquete de datos y reparte sus bytes entre los campos; devuelve false si ya no hay más.</summary>
        private bool ReadPacket()
        {
            while (true)
            {
                if (_logicalPosition >= long.MaxValue / 2) return false;
                byte[] head = ReadLogical(_logicalPosition, 4);
                int length = BitConverter.ToUInt16(head, 2) + 1;
                if (head[0] == 1)
                {
                    byte[] packet = ReadLogical(_logicalPosition, length);
                    _logicalPosition += length;
                    int count = BitConverter.ToUInt16(packet, 4);
                    int at = 6 + 2 * count;
                    for (int s = 0; s < count; s++)
                    {
                        int size = BitConverter.ToUInt16(packet, 6 + 2 * s);
                        Field f = _fields.FirstOrDefault(q => q.Stream == s);
                        if (f != null && IsUsed(f)) Append(f, packet, at, size);
                        at += size;
                    }
                    return true;
                }
                _logicalPosition += length;
                if (head[0] != 0 && head[0] != 2) return false;
            }
        }

        private bool IsUsed(Field f) { return f == _x || f == _y || f == _z || f == _i || f == _r || f == _g || f == _b || f == _invalid; }

        private void Append(Field f, byte[] packet, int at, int size)
        {
            // Bytes que sobran del paquete anterior + los nuevos; se decodifican todos los valores completos.
            int bytePos = (int)(f.BitPos >> 3);
            byte[] data = new byte[f.Pending.Length - bytePos + size];
            Buffer.BlockCopy(f.Pending, bytePos, data, 0, f.Pending.Length - bytePos);
            Buffer.BlockCopy(packet, at, data, f.Pending.Length - bytePos, size);
            long bit = f.BitPos & 7;
            int width = f.Kind == Kind.Float32 ? 32 : f.Kind == Kind.Float64 ? 64 : f.Bits;
            long totalBits = (long)data.Length * 8;
            if (f.Head > 0 && f.Head == f.Values.Count) { f.Values.Clear(); f.Head = 0; }
            else if (f.Head > 100000) { f.Values.RemoveRange(0, f.Head); f.Head = 0; }
            if (width == 0)
            {
                // Valor constante: el mínimo; no ocupa bits. Los puntos se generan al pedirlos (ver Read).
                return;
            }
            while (bit + width <= totalBits)
            {
                int byteAt = (int)(bit >> 3);
                int shift = (int)(bit & 7);
                double value;
                if (f.Kind == Kind.Float64) value = BitConverter.ToDouble(data, byteAt);
                else if (f.Kind == Kind.Float32) value = BitConverter.ToSingle(data, byteAt);
                else
                {
                    ulong raw = 0;
                    int need = (shift + width + 7) / 8;
                    if (need <= 8)
                    {
                        for (int k = 0; k < need; k++) raw |= (ulong)data[byteAt + k] << (8 * k);
                        raw >>= shift;
                    }
                    else
                    {
                        for (int k = 0; k < 8; k++) raw |= (ulong)data[byteAt + k] << (8 * k);
                        raw >>= shift;
                        raw |= (ulong)data[byteAt + 8] << (64 - shift);
                    }
                    if (width < 64) raw &= (1UL << width) - 1;
                    value = ((double)(long)raw + f.Min) * f.Scale + f.Offset;
                }
                f.Values.Add(value);
                bit += width;
            }
            f.Pending = data;
            f.BitPos = bit;
        }

        private static int Available(Field f) { return f == null ? int.MaxValue : f.Values.Count - f.Head; }

        public override int Read(CloudPoint[] buffer, int max)
        {
            int written = 0;
            while (written < max && _produced < _records)
            {
                int ready = (int)Math.Min(Math.Min(max - written, _records - _produced),
                    Math.Min(Math.Min(Constant(_x) ? int.MaxValue : Available(_x), Constant(_y) ? int.MaxValue : Available(_y)), Constant(_z) ? int.MaxValue : Available(_z)));
                if (_i != null) ready = Math.Min(ready, Constant(_i) ? ready : Available(_i));
                if (HasColor) ready = Math.Min(ready, Math.Min(Constant(_r) ? ready : Available(_r), Math.Min(Constant(_g) ? ready : Available(_g), Constant(_b) ? ready : Available(_b))));
                if (_invalid != null) ready = Math.Min(ready, Constant(_invalid) ? ready : Available(_invalid));
                if (ready <= 0)
                {
                    if (!ReadPacket()) { _produced = _records; break; }
                    continue;
                }
                for (int k = 0; k < ready; k++)
                {
                    double x = Take(_x), y = Take(_y), z = Take(_z);
                    double inten = _i != null ? Take(_i) : 0;
                    double red = 0, green = 0, blue = 0;
                    if (HasColor) { red = Take(_r); green = Take(_g); blue = Take(_b); }
                    double invalid = _invalid != null ? Take(_invalid) : 0;
                    _produced++;
                    if (invalid != 0 || double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z)) continue;
                    Transform(ref x, ref y, ref z);
                    CloudPoint point = new CloudPoint { X = x, Y = y, Z = z };
                    if (_i != null) point.Intensity = (ushort)Math.Max(0, Math.Min(65535, (inten - _intMin) / Math.Max(1e-12, _intMax - _intMin) * 65535));
                    if (HasColor)
                    {
                        point.R = (ushort)Math.Max(0, Math.Min(65535, red / _colMax * 65535));
                        point.G = (ushort)Math.Max(0, Math.Min(65535, green / _colMax * 65535));
                        point.B = (ushort)Math.Max(0, Math.Min(65535, blue / _colMax * 65535));
                    }
                    buffer[written++] = point;
                }
            }
            return written;
        }

        private static bool Constant(Field f) { return f != null && f.Kind == Kind.Integer && f.Bits == 0; }

        private static double Take(Field f)
        {
            if (Constant(f)) return f.Min * f.Scale + f.Offset;
            return f.Values[f.Head++];
        }

        private void Transform(ref double x, ref double y, ref double z)
        {
            double w = _rotation[0], qx = _rotation[1], qy = _rotation[2], qz = _rotation[3];
            if (w == 1 && qx == 0 && qy == 0 && qz == 0 && _translation[0] == 0 && _translation[1] == 0 && _translation[2] == 0) return;
            double rx = (1 - 2 * (qy * qy + qz * qz)) * x + 2 * (qx * qy - w * qz) * y + 2 * (qx * qz + w * qy) * z;
            double ry = 2 * (qx * qy + w * qz) * x + (1 - 2 * (qx * qx + qz * qz)) * y + 2 * (qy * qz - w * qx) * z;
            double rz = 2 * (qx * qz - w * qy) * x + 2 * (qy * qz + w * qx) * y + (1 - 2 * (qx * qx + qy * qy)) * z;
            x = rx + _translation[0]; y = ry + _translation[1]; z = rz + _translation[2];
        }

        public override void Dispose() { _file.Dispose(); }
    }
}
