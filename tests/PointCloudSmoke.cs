using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lazo;

// Conversión real de nubes de puntos con DeCap (Autodesk ReCap): LAS a proyecto .rcp y a nube única .rcs.
// Si ReCap no está instalado, la prueba lo dice y termina sin fallar.
internal static class PointCloudSmoke
{
    private static int Main(string[] args)
    {
        try { return Run(args.Length > 0 ? args[0] : "pointcloud-smoke").GetAwaiter().GetResult() ? 0 : 1; }
        catch (Exception ex) { Console.WriteLine("ERROR: " + ex); return 1; }
    }

    private static bool Check(bool condition, string what)
    {
        Console.WriteLine((condition ? "OK    " : "FALLO ") + what);
        return condition;
    }

    private static async Task<bool> Run(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string las = Path.Combine(folder, "terreno.las");
        WriteLas(las, 150);
        bool ok = true;

        PointCloudJob twins = new PointCloudJob { OutputFolder = folder, Name = "x" };
        twins.Inputs.Add(las);
        string copy = Path.Combine(folder, "otra");
        Directory.CreateDirectory(copy);
        File.Copy(las, Path.Combine(copy, "terreno.las"));
        twins.Inputs.Add(Path.Combine(copy, "terreno.las"));
        ok &= Check(PointCloudConverter.Validate(twins) != null, "dos archivos con el mismo nombre se rechazan antes de empezar");
        ok &= Check(PointCloudConverter.CleanName("Nube: piso 2?") == "Nube piso 2", "el nombre del resultado se limpia");
        ok &= Check(PointCloudConverter.Arguments(new PointCloudJob { OutputFolder = @"C:\a\", Name = "n", Unify = true, Unit = 0, DecimationMm = 5, Inputs = { @"C:\x.las" } }, "n")
            == "--importWithLicense \"C:\\a\" \"n\" --unify --inputUnitType 0 --decimation 5 \"C:\\x.las\"", "argumentos para DeCap");

        string decap = PointCloudConverter.FindDeCap();
        if (decap == null)
        {
            Console.WriteLine("OMITIDO: Autodesk ReCap no está instalado; no se prueba la conversión real.");
            return ok;
        }
        Console.WriteLine("DeCap: " + decap);
        string license = await PointCloudConverter.CheckLicenseAsync(decap);
        if (license != null) { Console.WriteLine("OMITIDO: " + license); return ok; }

        string output = Path.Combine(folder, "salida");
        double last = -1;
        int updates = 0;
        PointCloudJob project = new PointCloudJob { OutputFolder = output, Name = "Proyecto prueba" };
        project.Inputs.Add(las);
        PointCloudResult rcp = await PointCloudConverter.ConvertAsync(decap, project, (value, text) => { last = value; updates++; }, CancellationToken.None);
        ok &= Check(rcp.Ok && rcp.Output.EndsWith("Proyecto prueba.rcp") && File.Exists(Path.Combine(output, "Proyecto prueba Support", "terreno.rcs")),
            "LAS a proyecto .rcp con su .rcs (" + rcp.Message + ")");
        ok &= Check(updates > 1 && Math.Abs(last - 1) < 0.001, "el progreso avanza hasta el final");

        PointCloudResult again = await PointCloudConverter.ConvertAsync(decap, project, null, CancellationToken.None);
        ok &= Check(again.Ok && again.Output.EndsWith("Proyecto prueba (2).rcp"), "una segunda conversión no pisa la primera");

        PointCloudJob unified = new PointCloudJob { OutputFolder = output, Name = "Nube única", Unify = true, Unit = 2, DecimationMm = 10 };
        unified.Inputs.Add(las);
        PointCloudResult rcs = await PointCloudConverter.ConvertAsync(decap, unified, null, CancellationToken.None);
        ok &= Check(rcs.Ok && rcs.Output.EndsWith("Nube única.rcs") && new FileInfo(rcs.Output).Length > 1000, "LAS a nube única .rcs (" + rcs.Message + ")");
        return ok;
    }

    /// <summary>LAS 1.2, formato de punto 0: una superficie ondulada de n × n puntos, en metros.</summary>
    private static void WriteLas(string path, int n)
    {
        const double scale = 0.001, size = 20;
        using (BinaryWriter w = new BinaryWriter(File.Create(path)))
        {
            w.Write(Encoding.ASCII.GetBytes("LASF")); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0u); w.Write((ushort)0); w.Write((ushort)0); w.Write(new byte[8]);
            w.Write((byte)1); w.Write((byte)2);
            w.Write(Pad("Lazo", 32)); w.Write(Pad("Lazo prueba", 32));
            w.Write((ushort)1); w.Write((ushort)2026); w.Write((ushort)227); w.Write(227u); w.Write(0u);
            w.Write((byte)0); w.Write((ushort)20); w.Write((uint)(n * n));
            w.Write((uint)(n * n)); for (int i = 0; i < 4; i++) w.Write(0u);
            w.Write(scale); w.Write(scale); w.Write(scale); w.Write(0.0); w.Write(0.0); w.Write(0.0);
            w.Write(size); w.Write(0.0); w.Write(size); w.Write(0.0); w.Write(3.0); w.Write(-3.0);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double x = size * i / (n - 1), y = size * j / (n - 1), z = 2 * Math.Sin(x / 3) * Math.Cos(y / 4);
                    w.Write((int)(x / scale)); w.Write((int)(y / scale)); w.Write((int)(z / scale));
                    w.Write((ushort)(16000 + (int)(z * 5000))); w.Write((byte)0); w.Write((byte)1); w.Write((sbyte)0); w.Write((byte)0); w.Write((ushort)0);
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
}
