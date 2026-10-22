using System;
using System.IO;
using Lazo;

/// <summary>Dónde busca el Lazo clásico a la isla: solo la que trae el programa de utilidades al lado, nunca a sí mismo ni a otro clásico.</summary>
internal static class IslandCoreSmoke
{
    private static int fails;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok  " : "  FALLA ") + what);
        if (!ok) fails++;
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "x");
    }

    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "lazo-islandcore-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        string local = Path.Combine(root, "local"), files = Path.Combine(root, "files");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(files);
        try
        {
            string classicInFiles = Path.Combine(files, "Lazo", "Lazo.exe");
            Touch(classicInFiles);
            Check(IslandCore.Find(classicInFiles, local, files) == null, "solo el clásico instalado: no hay isla");

            string island = Path.Combine(local, "Lazo", "lazo.exe");
            Touch(island);
            Check(IslandCore.Find(classicInFiles, local, files) == null, "un lazo.exe sin el programa de utilidades al lado no es la isla");

            Touch(Path.Combine(local, "Lazo", "engine", "Lazo.Engine.exe"));
            Check(IslandCore.Find(classicInFiles, local, files) == island, "con engine/Lazo.Engine.exe al lado sí es la isla");

            string bundled = Path.Combine(local, "Lazo", "classic", "Lazo.exe");
            Touch(bundled);
            Check(IslandCore.Find(bundled, local, files) == island, "el clásico que viene dentro de la isla encuentra a su isla");
            Check(IslandCore.Find(island, local, files) == null, "la isla no se encuentra a sí misma");

            string problem = IslandCore.Switch(Path.Combine(root, "no-esta.exe"));
            Check(problem != null && problem.Contains("No se encontró"), "sin isla instalada no cambia nada y lo dice");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        Console.WriteLine(fails == 0 ? "OK" : "FALLÓ");
        return fails == 0 ? 0 : 1;
    }
}
