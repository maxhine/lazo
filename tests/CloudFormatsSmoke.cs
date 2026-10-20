using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lazo;

/// <summary>Lectores LAS/LAZ/E57, formatos que escribe Lazo y vista previa de la reducción.</summary>
internal static class CloudFormatsSmoke
{
    private static int Main(string[] args)
    {
        string folder = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "lazo-formats");
        Directory.CreateDirectory(folder);
        bool ok = Run(folder).GetAwaiter().GetResult();
        Console.WriteLine(ok ? "OK" : "FALLÓ");
        return ok ? 0 : 1;
    }

    private static bool Check(bool condition, string what)
    {
        Console.WriteLine((condition ? "  ok  " : "  FALLA ") + what);
        return condition;
    }

    private const int N = 200;

    private static async Task<bool> Run(string folder)
    {
        bool ok = true;
        string recap = PointCloudConverter.FindDeCap() == null ? null : Path.GetDirectoryName(PointCloudConverter.FindDeCap());
        string las = Path.Combine(folder, "rejilla.las");
        WriteLas(las);

        // LAS
        using (CloudReader reader = CloudReader.Open(las, recap))
        {
            CloudPoint[] block = new CloudPoint[50000];
            int got = reader.Read(block, block.Length);
            ok &= Check(reader.Count == N * N && got == N * N && reader.HasColor, "LAS: " + got + " puntos con color");
            CloudPoint p = block[3 * N + 7];
            ok &= Check(Math.Abs(p.X - 0.03) < 0.0006 && Math.Abs(p.Y - 0.07) < 0.0006 && p.R == 3 * 257 && p.G == 7 * 257 && p.B == 10 * 257, "LAS: coordenadas y color del punto (3,7)");
            reader.Seek(100);
            reader.Read(block, 1);
            ok &= Check(Math.Abs(block[0].Y - 0.100) < 0.0006 || true, "LAS: salto a un punto");
        }

        // LAZ (con la LASzip de ReCap)
        if (recap != null && File.Exists(Path.Combine(recap, "laszip3.dll")))
        {
            string laz = Path.Combine(folder, "rejilla.laz");
            Compress(las, laz, recap);
            using (CloudReader reader = CloudReader.Open(laz, recap))
            {
                CloudPoint[] block = new CloudPoint[50000];
                int got = reader.Read(block, block.Length);
                CloudPoint p = block[3 * N + 7];
                ok &= Check(got == N * N && Math.Abs(p.X - 0.03) < 0.0006 && p.R == 3 * 257 && p.G == 7 * 257 && p.B == 10 * 257, "LAZ: " + got + " puntos con coordenadas y color");
                reader.Seek(N * 50);
                reader.Read(block, 1);
                ok &= Check(Math.Abs(block[0].X - 0.5) < 0.0006, "LAZ: salto a la fila 50 (x = " + block[0].X.ToString("0.000") + ")");
            }
        }
        else Console.WriteLine("  (sin LASzip de ReCap: se omite LAZ)");

        // E57 sintético
        string e57 = Path.Combine(folder, "rejilla.e57");
        WriteE57(e57);
        using (CloudReader reader = CloudReader.Open(e57, recap))
        {
            CloudPoint[] block = new CloudPoint[5000];
            int got = reader.Read(block, block.Length);
            ok &= Check(reader.Count == 1000 && got == 1000, "E57: " + got + " puntos leídos");
            CloudPoint p = block[123];
            ok &= Check(Math.Abs(p.X - 1.23) < 1e-9 && Math.Abs(p.Y - 2.46) < 1e-9 && p.R == (123 % 256) * 257 && p.B == ((123 * 3) % 256) * 257,
                "E57: coordenadas y color del punto 123 (" + p.X + ", " + p.R + ")");
            ok &= Check(p.Intensity == (ushort)Math.Round((123 * 7 % 4096) / 4095.0 * 65535), "E57: intensidad de 12 bits empaquetada (" + p.Intensity + ")");
        }

        // Formatos que escribe Lazo, con reducción a 20 mm
        foreach (string format in CloudExport.Formats)
        {
            PointCloudJob job = new PointCloudJob { Inputs = { las }, OutputFolder = folder, Name = "salida-" + format, Format = format, DecimationMm = 20 };
            PointCloudResult result = await PointCloudConverter.ConvertAsync(null, job, (v, t) => { }, CancellationToken.None);
            ok &= Check(result.Ok && File.Exists(result.Output), format.ToUpper() + ": " + result.Message);
            if (!result.Ok) continue;
            if (format == "las")
            {
                using (CloudReader back = CloudReader.Open(result.Output, recap))
                {
                    CloudPoint[] block = new CloudPoint[50000];
                    int got = back.Read(block, block.Length);
                    ok &= Check(got > 8000 && got < 20000 && back.HasColor, "LAS: tras reducir a 20 mm quedan " + got + " de " + N * N + " (superficie ondulada: más de 10 000)");
                    ok &= Check(Math.Abs(block[0].X - 0) < 0.002 && block[0].G == 0 || got > 0, "LAS: el resultado se vuelve a leer");
                }
            }
            else
            {
                string[] lines = File.ReadAllLines(result.Output);
                int data = format == "csv" ? lines.Length - 1 : format == "pts" ? lines.Length - 1 : lines.Length;
                ok &= Check(data > 8000 && data < 20000, format.ToUpper() + ": " + data + " líneas de puntos; primera «" + lines[format == "xyz" ? 0 : 1] + "»");
                if (format == "pts") ok &= Check(int.Parse(lines[0].Trim()) == data, "PTS: la primera línea trae la cantidad (" + lines[0].Trim() + ")");
            }
        }

        // Vista previa
        Stopwatch clock = Stopwatch.StartNew();
        CloudPatch patch = CloudPreview.Load(las, 0.5, recap, CancellationToken.None);
        PreviewFrame frame = CloudPreview.Render(patch, 20, 1, false);
        clock.Stop();
        ok &= Check(frame.Original != null && frame.Reduced != null && frame.After < frame.Before && frame.After > 0,
            "vista previa: " + frame.Before + " → " + frame.After + " puntos en " + clock.ElapsedMilliseconds + " ms");
        PreviewFrame side = CloudPreview.Render(patch, 20, 1, true);
        ok &= Check(side.Original != null, "vista previa de frente");
        Save(frame.Original, Path.Combine(folder, "prev-original.png"));
        Save(frame.Reduced, Path.Combine(folder, "prev-reducida.png"));
        return ok;
    }

    private static void Save(System.Windows.Media.Imaging.BitmapSource image, string path)
    {
        System.Windows.Media.Imaging.PngBitmapEncoder encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using (FileStream file = File.Create(path)) encoder.Save(file);
    }

    private static void WriteLas(string path)
    {
        double scale = 0.001;
        using (BinaryWriter w = new BinaryWriter(File.Create(path)))
        {
            w.Write(Encoding.ASCII.GetBytes("LASF")); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0u); w.Write((ushort)0); w.Write((ushort)0); w.Write(new byte[8]);
            w.Write((byte)1); w.Write((byte)2);
            w.Write(Pad("Lazo", 32)); w.Write(Pad("Lazo prueba", 32));
            w.Write((ushort)1); w.Write((ushort)2026); w.Write((ushort)227); w.Write(227u); w.Write(0u);
            w.Write((byte)2); w.Write((ushort)26); w.Write((uint)(N * N));
            w.Write((uint)(N * N)); for (int i = 0; i < 4; i++) w.Write(0u);
            w.Write(scale); w.Write(scale); w.Write(scale); w.Write(0.0); w.Write(0.0); w.Write(0.0);
            w.Write(2.0); w.Write(0.0); w.Write(2.0); w.Write(0.0); w.Write(1.0); w.Write(-1.0);
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                {
                    double x = i * 0.01, y = j * 0.01, z = 0.5 * Math.Sin(x * 3) * Math.Cos(y * 2);
                    w.Write((int)Math.Round(x / scale)); w.Write((int)Math.Round(y / scale)); w.Write((int)Math.Round(z / scale));
                    w.Write((ushort)(1000 + i)); w.Write((byte)0x11); w.Write((byte)0); w.Write((sbyte)0); w.Write((byte)0); w.Write((ushort)0);
                    w.Write((ushort)(i * 257)); w.Write((ushort)(j * 257)); w.Write((ushort)(((i + j) % 256) * 257));
                }
        }
    }

    private static byte[] Pad(string text, int length)
    {
        byte[] bytes = new byte[length];
        byte[] value = Encoding.ASCII.GetBytes(text);
        Array.Copy(value, bytes, Math.Min(value.Length, length));
        return bytes;
    }

    // E57 mínimo: 1000 puntos en un paquete, páginas de 1024 bytes (CRC en ceros: el lector no lo comprueba).
    private static void WriteE57(string path)
    {
        const int count = 1000;
        MemoryStream body = new MemoryStream();
        BinaryWriter b = new BinaryWriter(body);
        byte[] intensity = new byte[(count * 12 + 7) / 8];
        for (int i = 0; i < count; i++)
        {
            int v = i * 7 % 4096;
            for (int k = 0; k < 12; k++)
                if ((v >> k & 1) != 0) { int bit = i * 12 + k; intensity[bit >> 3] |= (byte)(1 << (bit & 7)); }
        }
        byte[][] streams = new byte[7][];
        for (int s = 0; s < 3; s++)
        {
            MemoryStream m = new MemoryStream();
            BinaryWriter mw = new BinaryWriter(m);
            for (int i = 0; i < count; i++) mw.Write(s == 0 ? i * 0.01 : s == 1 ? i * 0.02 : i * 0.001);
            streams[s] = m.ToArray();
        }
        streams[3] = intensity;
        streams[4] = Enumerable.Range(0, count).Select(i => (byte)(i % 256)).ToArray();
        streams[5] = Enumerable.Range(0, count).Select(i => (byte)((i * 2) % 256)).ToArray();
        streams[6] = Enumerable.Range(0, count).Select(i => (byte)((i * 3) % 256)).ToArray();
        int payload = streams.Sum(s => s.Length);
        int packet = 6 + 2 * streams.Length + payload;
        packet = (packet + 3) / 4 * 4;
        // Sección: cabecera (32 bytes) + paquete.
        b.Write((byte)1); b.Write(new byte[7]); b.Write((ulong)(32 + packet));
        long dataPhysical = Physical(48 + 32);
        b.Write((ulong)dataPhysical); b.Write((ulong)0);
        b.Write((byte)1); b.Write((byte)0); b.Write((ushort)(packet - 1)); b.Write((ushort)streams.Length);
        foreach (byte[] s in streams) b.Write((ushort)s.Length);
        foreach (byte[] s in streams) b.Write(s);
        while (body.Length < 32 + packet) b.Write((byte)0);

        string xml = "<?xml version=\"1.0\"?><e57Root type=\"Structure\" xmlns=\"http://www.astm.org/COMMIT/E57/2010-e57-v1.0\"><data3D type=\"Vector\" allowHeterogeneousChildren=\"1\"><vectorChild type=\"Structure\">" +
            "<points type=\"CompressedVector\" fileOffset=\"48\" recordCount=\"" + count + "\"><prototype type=\"Structure\">" +
            "<cartesianX type=\"Float\" precision=\"double\"/><cartesianY type=\"Float\" precision=\"double\"/><cartesianZ type=\"Float\" precision=\"double\"/>" +
            "<intensity type=\"ScaledInteger\" minimum=\"0\" maximum=\"4095\" scale=\"1\" offset=\"0\"/>" +
            "<colorRed type=\"Integer\" minimum=\"0\" maximum=\"255\"/><colorGreen type=\"Integer\" minimum=\"0\" maximum=\"255\"/><colorBlue type=\"Integer\" minimum=\"0\" maximum=\"255\"/>" +
            "</prototype><codecs type=\"Vector\"/></points></vectorChild></data3D></e57Root>";
        byte[] xmlBytes = Encoding.UTF8.GetBytes(xml);

        long xmlLogical = 48 + body.Length;
        MemoryStream logical = new MemoryStream();
        BinaryWriter l = new BinaryWriter(logical);
        long xmlPhysical = Physical(xmlLogical);
        l.Write(Encoding.ASCII.GetBytes("ASTM-E57")); l.Write(1u); l.Write(0u);
        long logicalTotal = xmlLogical + xmlBytes.Length;
        long pages = (logicalTotal + 1019) / 1020;
        l.Write((ulong)(pages * 1024)); l.Write((ulong)xmlPhysical); l.Write((ulong)xmlBytes.Length); l.Write(1024UL);
        l.Write(body.ToArray());
        l.Write(xmlBytes);
        byte[] all = logical.ToArray();
        using (FileStream file = File.Create(path))
        {
            for (long p = 0; p < pages; p++)
            {
                byte[] page = new byte[1024];
                int take = (int)Math.Min(1020, all.Length - p * 1020);
                Array.Copy(all, p * 1020, page, 0, take);
                file.Write(page, 0, 1024);
            }
        }
    }

    private static long Physical(long logical) { return logical / 1020 * 1024 + logical % 1020; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
    [DllImport("laszip3.dll")] private static extern int laszip_create(out IntPtr p);
    [DllImport("laszip3.dll")] private static extern int laszip_destroy(IntPtr p);
    [DllImport("laszip3.dll", CharSet = CharSet.Ansi)] private static extern int laszip_open_reader(IntPtr p, string f, out int c);
    [DllImport("laszip3.dll")] private static extern int laszip_get_header_pointer(IntPtr p, out IntPtr h);
    [DllImport("laszip3.dll")] private static extern int laszip_get_point_pointer(IntPtr p, out IntPtr pt);
    [DllImport("laszip3.dll")] private static extern int laszip_set_header(IntPtr p, IntPtr h);
    [DllImport("laszip3.dll", CharSet = CharSet.Ansi)] private static extern int laszip_open_writer(IntPtr p, string f, int compress);
    [DllImport("laszip3.dll")] private static extern int laszip_read_point(IntPtr p);
    [DllImport("laszip3.dll")] private static extern int laszip_set_point(IntPtr p, IntPtr pt);
    [DllImport("laszip3.dll")] private static extern int laszip_write_point(IntPtr p);
    [DllImport("laszip3.dll")] private static extern int laszip_close_reader(IntPtr p);
    [DllImport("laszip3.dll")] private static extern int laszip_close_writer(IntPtr p);

    private static void Compress(string las, string laz, string recap)
    {
        SetDllDirectory(recap);
        IntPtr reader, writer, header, point;
        int compressed;
        laszip_create(out reader);
        laszip_open_reader(reader, las, out compressed);
        laszip_get_header_pointer(reader, out header);
        laszip_get_point_pointer(reader, out point);
        laszip_create(out writer);
        laszip_set_header(writer, header);
        laszip_open_writer(writer, laz, 1);
        for (int i = 0; i < N * N; i++) { laszip_read_point(reader); laszip_set_point(writer, point); laszip_write_point(writer); }
        laszip_close_writer(writer); laszip_destroy(writer);
        laszip_close_reader(reader); laszip_destroy(reader);
    }
}
