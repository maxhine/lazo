using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Lazo;

/// <summary>Xpace: el análisis suma lo mismo que el sistema de archivos y es rápido. Con una ruta de argumento también mide esa carpeta.</summary>
internal static class XpaceSmoke
{
    private static bool Check(bool ok, string what) { Console.WriteLine((ok ? "  ok  " : "  FALLA ") + what); return ok; }

    private static int Main(string[] args)
    {
        bool ok = true;
        string root = Path.Combine(Path.GetTempPath(), "lazo-xpace-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        Directory.CreateDirectory(Path.Combine(root, "vacía"));
        File.WriteAllBytes(Path.Combine(root, "uno.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(root, "a", "dos.txt"), new byte[5000]);
        File.WriteAllBytes(Path.Combine(root, "a", "b", "tres.TXT"), new byte[250]);
        File.WriteAllBytes(Path.Combine(root, "a", "b", "sin"), new byte[7]);
        try
        {
            XResult r = new XpaceScanner().Scan(root, CancellationToken.None);
            ok &= Check(r.Bytes == 6257 && r.Files == 4 && r.Folders == 3, "totales: " + r.Bytes + " bytes, " + r.Files + " archivos, " + r.Folders + " carpetas");
            XNode a = r.Root.Children.First(c => c.Name == "a");
            ok &= Check(a.Size == 5257 && r.Root.Children[0] == a, "la carpeta más grande va primero y suma sus subcarpetas");
            ok &= Check(r.Types.First(t => t.Extension == ".txt").Count == 2, "tipos sin distinguir mayúsculas");
            ok &= Check(r.Largest[0].Name == "dos.txt" && a.FullPath(r.Path) == Path.Combine(root, "a"), "archivos grandes y ruta de la carpeta");
        }
        finally { Directory.Delete(root, true); }

        if (args.Length > 0)
        {
            Stopwatch clock = Stopwatch.StartNew();
            XResult big = new XpaceScanner().Scan(args[0], CancellationToken.None);
            Console.WriteLine("  " + args[0] + ": " + big.Files.ToString("N0") + " archivos, " + big.Folders.ToString("N0") + " carpetas, " + (big.Bytes / 1073741824.0).ToString("0.0") + " GB, sin acceso: " + big.Denied + " en " + clock.Elapsed.TotalSeconds.ToString("0.0") + " s");
            foreach (XNode n in big.Root.Children.Take(5)) Console.WriteLine("    " + n.Name + "  " + (n.Size / 1073741824.0).ToString("0.00") + " GB");
        }
        Console.WriteLine(ok ? "OK" : "FALLÓ");
        return ok ? 0 : 1;
    }
}
