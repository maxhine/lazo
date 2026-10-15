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
        WriteLas(las, 150, 0, 0, 0);
        bool ok = true;

        // Sistema de coordenadas: lo que declara el archivo y los .prj de iTwin Capture.
        string bogota = Path.Combine(folder, "bogota.las");
        WriteLas(bogota, 60, 1000400, 1001100, 3116);
        CloudCrs found = CloudCrsReader.Read(bogota);
        ok &= Check(found != null && found.Epsg == 3116, "se lee el EPSG que declara el LAS (GeoKeys)");
        ok &= Check(!CoordinateLibrary.Available || (found.Match != null && found.Match.Key == "MAGNA.Columbia-Bogota"),
            "se reconoce en la biblioteca de ReCap (" + (found != null && found.Match != null ? found.Match.Key : "sin biblioteca") + ")");
        CloudCrs compound = CloudCrsReader.FromText("COMPD_CS[\"MAGNA-SIRGAS / Colombia Bogota zone + EGM2008 geoid height\",PROJCS[\"MAGNA-SIRGAS / Colombia Bogota zone\",GEOGCS[\"MAGNA-SIRGAS\",AUTHORITY[\"EPSG\",\"4686\"]],UNIT[\"metre\",1,AUTHORITY[\"EPSG\",\"9001\"]],AUTHORITY[\"EPSG\",\"3116\"]],VERT_CS[\"EGM2008 geoid height\",AUTHORITY[\"EPSG\",\"3855\"]]]");
        ok &= Check(compound != null && compound.Epsg == 3116, "en un WKT compuesto vale el sistema horizontal (3116, no 3855)");
        CloudCrs national = CloudCrsReader.FromText("PROJCS[\"MAGNA-SIRGAS_2018_Origen-Nacional\",GEOGCS[\"MAGNA-SIRGAS_2018\"],PROJECTION[\"Transverse_Mercator\"]]");
        ok &= Check(national != null && LazoProjection.Detect(national.Epsg, national.Name).Id == "on", "Origen Nacional se reconoce por su nombre");

        // Reproyección propia: Bogotá a Origen Nacional, punto por punto, y alturas a EGM2008.
        LazoProjection from = LazoProjection.ById("3116"), to = LazoProjection.ById("on-egm");
        string geoidPath = GeoidGrid.Find();
        string reprojected = CloudReprojector.Reproject(bogota, from, to, null, false, Path.Combine(folder, "reproy"), null, null, CancellationToken.None);
        double[] original = FirstPoint(bogota), moved = FirstPoint(reprojected);
        double lat, lon, ex, ey;
        from.Inverse(original[0], original[1], out lat, out lon);
        to.Forward(lat, lon, out ex, out ey);
        ok &= Check(Math.Abs(moved[0] - ex) < 0.0015 && Math.Abs(moved[1] - ey) < 0.0015 && Math.Abs(moved[2] - original[2]) < 0.0015,
            string.Format("punto reproyectado a Origen Nacional: {0:F3} {1:F3}", moved[0], moved[1]));
        ok &= Check(CloudCrsReader.Read(reprojected) == null, "el LAS reproyectado ya no declara el sistema viejo");
        if (geoidPath != null)
        {
            using (GeoidGrid geoid = new GeoidGrid(geoidPath))
            {
                string ortho = CloudReprojector.Reproject(bogota, from, to, geoid, true, Path.Combine(folder, "ortho"), null, null, CancellationToken.None);
                double expected = original[2] - geoid.Undulation(lat, lon);
                ok &= Check(Math.Abs(FirstPoint(ortho)[2] - expected) < 0.0015,
                    string.Format("altura elipsoidal a EGM2008: {0:F3} → {1:F3} (N = {2:F3})", original[2], FirstPoint(ortho)[2], geoid.Undulation(lat, lon)));
            }
        }
        else Console.WriteLine("OMITIDO: no hay egm08_25.gtx para probar las alturas.");

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

        PointCloudJob national2 = new PointCloudJob { OutputFolder = output, Name = "Origen Nacional", Unify = true, LazoFrom = from, LazoTo = to };
        national2.Inputs.Add(bogota);
        PointCloudResult on = await PointCloudConverter.ConvertAsync(decap, national2, null, CancellationToken.None);
        ok &= Check(on.Ok && on.Output.EndsWith("Origen Nacional.rcs") && !Directory.GetDirectories(output, ".lazo-*").Any(),
            "LAS en Bogotá a .rcs en Origen Nacional, sin dejar temporales (" + on.Message + ")");
        if (CoordinateLibrary.Available)
        {
            PointCloudJob utm = new PointCloudJob { OutputFolder = output, Name = "UTM", Unify = true, SourceCs = "MAGNA.Columbia-Bogota", TargetCs = "UTM84-18N" };
            utm.Inputs.Add(bogota);
            PointCloudResult utmResult = await PointCloudConverter.ConvertAsync(decap, utm, null, CancellationToken.None);
            ok &= Check(utmResult.Ok && utmResult.Output.EndsWith("UTM.rcs"), "reproyección de ReCap a una nube única (dos pasos) (" + utmResult.Message + ")");
            PointCloudJob unknown = new PointCloudJob { OutputFolder = output, Name = "x", SourceCs = "NO.EXISTE", TargetCs = "UTM84-18N" };
            unknown.Inputs.Add(bogota);
            ok &= Check(PointCloudConverter.Validate(unknown) != null, "un sistema que ReCap no conoce se rechaza (ReCap lo ignoraría en silencio)");
        }
        return ok;
    }

    /// <summary>Primer punto de un LAS, en coordenadas reales.</summary>
    private static double[] FirstPoint(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int at = (int)BitConverter.ToUInt32(data, 96);
        return new[]
        {
            BitConverter.ToInt32(data, at) * BitConverter.ToDouble(data, 131) + BitConverter.ToDouble(data, 155),
            BitConverter.ToInt32(data, at + 4) * BitConverter.ToDouble(data, 139) + BitConverter.ToDouble(data, 163),
            BitConverter.ToInt32(data, at + 8) * BitConverter.ToDouble(data, 147) + BitConverter.ToDouble(data, 171)
        };
    }

    /// <summary>
    /// LAS 1.2, formato de punto 0: una superficie ondulada de n × n puntos de 20 m, desde (x0, y0).
    /// Con <paramref name="epsg"/> lleva su sistema en un registro GeoKey.
    /// </summary>
    private static void WriteLas(string path, int n, double x0, double y0, int epsg)
    {
        const double scale = 0.001, size = 20;
        byte[] geokeys = new byte[0];
        if (epsg > 0)
        {
            MemoryStream vlr = new MemoryStream();
            BinaryWriter v = new BinaryWriter(vlr);
            v.Write((ushort)0); v.Write(Pad("LASF_Projection", 16)); v.Write((ushort)34735); v.Write((ushort)16); v.Write(Pad("GeoKeys", 32));
            foreach (ushort value in new ushort[] { 1, 1, 0, 1, 3072, 0, 1, (ushort)epsg }) v.Write(value);
            geokeys = vlr.ToArray();
        }
        using (BinaryWriter w = new BinaryWriter(File.Create(path)))
        {
            w.Write(Encoding.ASCII.GetBytes("LASF")); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0u); w.Write((ushort)0); w.Write((ushort)0); w.Write(new byte[8]);
            w.Write((byte)1); w.Write((byte)2);
            w.Write(Pad("Lazo", 32)); w.Write(Pad("Lazo prueba", 32));
            w.Write((ushort)1); w.Write((ushort)2026); w.Write((ushort)227); w.Write((uint)(227 + geokeys.Length)); w.Write((uint)(epsg > 0 ? 1 : 0));
            w.Write((byte)0); w.Write((ushort)20); w.Write((uint)(n * n));
            w.Write((uint)(n * n)); for (int i = 0; i < 4; i++) w.Write(0u);
            w.Write(scale); w.Write(scale); w.Write(scale); w.Write(x0); w.Write(y0); w.Write(2600.0);
            w.Write(x0 + size); w.Write(x0); w.Write(y0 + size); w.Write(y0); w.Write(2603.0); w.Write(2597.0);
            w.Write(geokeys);
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
