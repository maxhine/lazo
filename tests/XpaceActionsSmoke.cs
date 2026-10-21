using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lazo;

/// <summary>Xpace: reglas de protección, carpetas sincronizadas, puesta al día del análisis tras quitar y sugerencias. Solo lo puro: no borra nada.</summary>
internal static class XpaceActionsSmoke
{
    private static bool Check(bool ok, string what) { Console.WriteLine((ok ? "  ok  " : "  FALLA ") + what); return ok; }

    private static XpaceEnv Env()
    {
        return new XpaceEnv
        {
            WindowsDir = @"C:\Windows", ProgramFiles = @"C:\Program Files", ProgramFilesX86 = @"C:\Program Files (x86)", ProgramData = @"C:\ProgramData",
            UsersRoot = @"C:\Users", Profile = @"C:\Users\Ana", Desktop = @"C:\Users\Ana\Desktop", Documents = @"C:\Users\Ana\Documents",
            Downloads = @"C:\Users\Ana\Downloads", Pictures = @"C:\Users\Ana\Pictures", Music = @"C:\Users\Ana\Music", Videos = @"C:\Users\Ana\Videos",
            AppData = @"C:\Users\Ana\AppData", Roaming = @"C:\Users\Ana\AppData\Roaming", Local = @"C:\Users\Ana\AppData\Local",
            LocalLow = @"C:\Users\Ana\AppData\LocalLow", Temp = @"C:\Users\Ana\AppData\Local\Temp\"
        };
    }

    private static XNode Node(string name, XNode parent, long ownSize, long ownFiles)
    {
        XNode node = new XNode { Name = name, Parent = parent, OwnSize = ownSize, OwnFiles = ownFiles };
        if (parent != null) parent.Children.Add(node);
        return node;
    }

    private static void Sum(XNode node)
    {
        node.Size = node.OwnSize;
        node.Files = node.OwnFiles;
        node.Folders = 0;
        foreach (XNode child in node.Children)
        {
            Sum(child);
            node.Size += child.Size;
            node.Files += child.Files;
            node.Folders += child.Folders + 1;
        }
    }

    private static int Main()
    {
        bool ok = true;
        XpaceEnv env = Env();

        // Protecciones
        string[] forbidden =
        {
            @"C:\", @"C:", @"D:\", @"C:\Windows", @"C:\Windows\System32", @"c:\windows\fonts", @"C:\Program Files", @"C:\Program Files (x86)",
            @"C:\ProgramData", @"C:\Users", @"C:\Users\Ana", @"C:\Users\Ana\", @"C:\Users\Ana\Documents", @"C:\Users\Ana\Desktop", @"C:\Users\Ana\Downloads",
            @"C:\Users\Ana\Pictures", @"C:\Users\Ana\Music", @"C:\Users\Ana\Videos", @"C:\Users\Ana\AppData", @"C:\Users\Ana\AppData\Roaming",
            @"C:\Users\Ana\AppData\Local", @"C:\Users\Ana\AppData\LocalLow", @"D:\$Recycle.Bin", @"C:\System Volume Information\algo", @"\\servidor\recurso", ""
        };
        string[] allowed =
        {
            @"C:\Users\Ana\Downloads\setup.exe", @"C:\Users\Ana\Documents\viejo", @"C:\Users\Ana\AppData\Local\Temp", @"C:\Users\Ana\AppData\Local\Google\Chrome",
            @"D:\datos\proyecto\node_modules", @"C:\Program Files\MiApp", @"C:\Users\Ana\Proyecto\obj", @"E:\copias\2019"
        };
        ok &= Check(forbidden.All(p => XpaceActions.ProtectionReason(p, env) != null), "protegidas: raíces, Windows, programas, usuario y sus carpetas principales, papelera");
        foreach (string p in forbidden) if (XpaceActions.ProtectionReason(p, env) == null) Console.WriteLine("    se debía proteger: '" + p + "'");
        ok &= Check(allowed.All(p => XpaceActions.ProtectionReason(p, env) == null), "permitidas: lo que hay dentro de las carpetas principales y otros discos");
        foreach (string p in allowed) if (XpaceActions.ProtectionReason(p, env) != null) Console.WriteLine("    se debía permitir: '" + p + "'");
        ok &= Check(XpaceActions.ProtectionReason(@"C:\Windows\System32", env).Contains("Windows"), "el motivo se explica en español");

        // Carpetas sincronizadas
        ok &= Check(XpaceActions.IsSynced(@"C:\Users\Ana\OneDrive\Docs\a.txt") && XpaceActions.IsSynced(@"C:\Users\Ana\OneDrive - Empresa\x") &&
            XpaceActions.IsSynced(@"D:\Dropbox\f") && XpaceActions.IsSynced(@"C:\Users\Ana\Google Drive\f") && XpaceActions.IsSynced(@"C:\Users\Ana\iCloudDrive\f"), "sincronizadas: OneDrive, Dropbox, Google Drive, iCloud");
        ok &= Check(!XpaceActions.IsSynced(@"C:\Users\Ana\Documents\a.txt") && !XpaceActions.IsSynced(@"D:\OtraCosa\x"), "no sincronizadas");

        // Puesta al día del análisis: raíz C:\data con a (a\b), c y una lista de grandes
        XNode root = Node(@"C:\data", null, 10, 1);
        XNode a = Node("a", root, 100, 1);
        XNode b = Node("b", a, 200, 2);
        XNode c = Node("c", root, 50, 1);
        Sum(root);
        List<XFile> largest = new List<XFile>
        {
            new XFile { Name = "x.bin", Folder = a, Size = 100 },
            new XFile { Name = "y1.TXT", Folder = b, Size = 120 },
            new XFile { Name = "y2.txt", Folder = b, Size = 80 },
            new XFile { Name = "z.txt", Folder = c, Size = 50 }
        };
        XResult r = new XResult
        {
            Path = @"C:\data", Root = root, Largest = largest,
            Types = new List<XType> { new XType { Extension = ".txt", Size = 250, Count = 3 }, new XType { Extension = ".bin", Size = 100, Count = 1 } },
            Files = root.Files, Bytes = root.Size, Folders = root.Folders
        };
        ok &= Check(r.Bytes == 360 && r.Files == 5 && r.Folders == 3, "árbol de prueba: 360 bytes, 5 archivos, 3 carpetas");
        XpaceActions.Forget(r, @"C:\data\a\b", true);
        ok &= Check(r.Bytes == 160 && r.Files == 3 && r.Folders == 2 && a.Size == 100 && a.Children.Count == 0 && root.Size == 160, "quitar una carpeta resta tamaño, archivos y carpetas a todos sus ancestros");
        ok &= Check(r.Largest.Count == 2 && r.Largest.All(f => f.Folder != b), "quitar una carpeta saca sus archivos de la lista de grandes");
        XpaceActions.Forget(r, @"c:\DATA\a\x.bin", false);
        ok &= Check(a.OwnSize == 0 && a.OwnFiles == 0 && r.Bytes == 60 && r.Files == 2 && r.Largest.Count == 1, "quitar un archivo ajusta su carpeta y los totales (sin distinguir mayúsculas)");
        XType bin = r.Types.First(t => t.Extension == ".bin");
        ok &= Check(bin.Size == 0 && bin.Count == 0, "quitar un archivo ajusta su tipo");
        XpaceActions.Forget(r, @"C:\data\no\existe", true);
        ok &= Check(r.Bytes == 60, "una ruta que no está en el análisis no cambia nada");

        // Sugerencias del análisis
        XNode top = Node(@"C:\proyecto", null, 0, 0);
        XNode app = Node("app", top, 10, 1);
        XNode modules = Node("node_modules", app, 5000, 50);
        XNode cache = Node("__pycache__", app, 3000, 5);
        XNode venv = Node(".venv", app, 4000, 9);
        XNode objFolder = Node("obj", app, 6000, 3);           // sin proyecto al lado: no se sugiere
        XNode downloads = Node("Downloads", top, 0, 0);
        XNode other = Node("videos", top, 0, 0);
        Sum(top);
        long year = DateTime.UtcNow.AddDays(-800).ToFileTimeUtc();
        XResult an = new XResult
        {
            Path = @"C:\proyecto", Root = top, Types = new List<XType>(), Files = top.Files, Bytes = top.Size, Folders = top.Folders,
            Largest = new List<XFile>
            {
                new XFile { Name = "setup.exe", Folder = downloads, Size = 5000 },
                new XFile { Name = "mini.exe", Folder = downloads, Size = 10 },
                new XFile { Name = "vm.vhdx", Folder = other, Size = 3L << 30, Modified = year },
                new XFile { Name = "reciente.mkv", Folder = other, Size = 3L << 30, Modified = DateTime.UtcNow.ToFileTimeUtc() },
                new XFile { Name = "datos.zip", Folder = downloads, Size = 300L << 20 },
                new XFile { Name = "app.exe", Folder = app, Size = 5000 }
            }
        };
        List<XSuggestion> tips = XpaceSuggestions.From(an, 1000, DateTime.UtcNow);
        Func<string, XSuggestion> pick = name => tips.FirstOrDefault(t => t.Path.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        ok &= Check(pick("node_modules") != null && pick("node_modules").Safe && pick("node_modules").IsFolder && pick("node_modules").Size == 5000, "node_modules: sugerida, «Seguro»");
        ok &= Check(pick("__pycache__") != null && pick("__pycache__").Safe, "__pycache__: sugerida, «Seguro»");
        ok &= Check(pick(".venv") != null && !pick(".venv").Safe, ".venv: sugerida, «Revisar»");
        ok &= Check(pick("obj") == null, "obj sin proyecto al lado: no se sugiere");
        ok &= Check(pick("setup.exe") != null && !pick("setup.exe").Safe, "instalador grande en Descargas: «Revisar»");
        ok &= Check(pick("mini.exe") == null && pick("app.exe") == null, "instaladores pequeños o fuera de Descargas: no se sugieren");
        ok &= Check(pick("vm.vhdx") != null && !pick("vm.vhdx").Safe && pick("reciente.mkv") == null, "más de 1 GB sin tocar en un año: «Revisar»; los recientes, no");
        ok &= Check(pick("datos.zip") != null && !pick("datos.zip").Safe, "comprimido de 200 MB o más en Descargas: «Revisar»");
        ok &= Check(tips.Select(t => t.Size).SequenceEqual(tips.Select(t => t.Size).OrderByDescending(x => x)), "ordenadas de mayor a menor");

        // Sin duplicados ni anidadas
        List<XSuggestion> messy = new List<XSuggestion>
        {
            new XSuggestion { Path = @"C:\data\a", Size = 10 }, new XSuggestion { Path = @"c:\DATA\A\", Size = 10 },
            new XSuggestion { Path = @"C:\data\a\b", Size = 99 }, new XSuggestion { Path = @"C:\data\ab", Size = 5 }
        };
        List<XSuggestion> clean = XpaceSuggestions.Clean(messy);
        ok &= Check(clean.Count == 2 && clean.Any(s => s.Path == @"C:\data\a") && clean.Any(s => s.Path == @"C:\data\ab"), "sin duplicados y sin lo que ya está dentro de otra sugerencia (a\\b dentro de a; ab no)");

        // Ubicaciones conocidas
        List<XKnownSpot> spots = XpaceSuggestions.KnownSpots(env);
        Func<string, bool> has = tail => spots.Any(s => XpaceActions.Normalize(s.Path).EndsWith(tail, StringComparison.OrdinalIgnoreCase));
        ok &= Check(has(@"Local\Temp") && has(@"INetCache") && has(@"CrashDumps") && has(@"Windows\WER") && has(@"npm-cache") && has(@"pip\Cache") && has(@"NuGet\v3-cache") && has(@".gradle\caches"),
            "ubicaciones conocidas: temporales, caché de Internet, volcados, npm, pip, NuGet y Gradle");
        ok &= Check(has(@"Google\Chrome\User Data\Default\Cache") && has(@"Microsoft\Edge\User Data\Default\Code Cache") && has(@"Brave-Browser\User Data\Default\GPUCache"), "cachés de Chrome, Edge y Brave");
        ok &= Check(spots.All(s => XpaceActions.ProtectionReason(s.Path, env) == null), "ninguna ubicación conocida está protegida");
        Func<string, long> measure = path => XpaceActions.Normalize(path).EndsWith(@"Local\Temp", StringComparison.OrdinalIgnoreCase) ? 100L << 20 : 10L << 20;
        List<XSuggestion> known = XpaceSuggestions.Known(env, 50L << 20, measure, path => true);
        ok &= Check(known.Count == 1 && known[0].ContentsOnly && known[0].Safe && known[0].Size == 100L << 20, "solo las de 50 MB o más, y %TEMP% y Local\\Temp cuentan una vez");
        ok &= Check(XpaceSuggestions.Known(env, 50L << 20, measure, path => false).Count == 0, "las que no existen se omiten");

        Console.WriteLine(ok ? "OK" : "FALLÓ");
        return ok ? 0 : 1;
    }
}
