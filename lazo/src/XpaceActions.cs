using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Lazo
{
    /// <summary>Carpetas del sistema y del usuario que Xpace conoce (se pueden sustituir en las pruebas).</summary>
    internal sealed class XpaceEnv
    {
        public string WindowsDir, ProgramFiles, ProgramFilesX86, ProgramData, UsersRoot, Profile;
        public string Desktop, Documents, Downloads, Pictures, Music, Videos;
        public string AppData, Roaming, Local, LocalLow, Temp;

        public static XpaceEnv Current()
        {
            XpaceEnv env = new XpaceEnv();
            env.WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            env.ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            env.ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            env.ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            env.Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            env.UsersRoot = string.IsNullOrEmpty(env.Profile) ? "" : Path.GetDirectoryName(env.Profile.TrimEnd('\\'));
            env.Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            env.Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            env.Downloads = string.IsNullOrEmpty(env.Profile) ? "" : Path.Combine(env.Profile, "Downloads");
            env.Pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            env.Music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            env.Videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            env.Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            env.Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            env.AppData = string.IsNullOrEmpty(env.Roaming) ? "" : Path.GetDirectoryName(env.Roaming.TrimEnd('\\'));
            env.LocalLow = string.IsNullOrEmpty(env.AppData) ? "" : Path.Combine(env.AppData, "LocalLow");
            env.Temp = Path.GetTempPath();
            return env;
        }
    }

    /// <summary>Una sugerencia de qué quitar para liberar espacio.</summary>
    internal sealed class XSuggestion
    {
        public string Path, Title, Why;
        /// <summary>«Seguro» (se vuelve a generar solo) o «Revisar» (mira antes).</summary>
        public bool Safe;
        public long Size;
        public bool IsFolder;
        /// <summary>Solo informa: no se quita desde Xpace (por ejemplo la papelera de reciclaje).</summary>
        public bool InfoOnly;
        /// <summary>Se quita lo que hay dentro y la carpeta se queda (temporales, cachés en uso).</summary>
        public bool ContentsOnly;
    }

    /// <summary>Ubicación conocida que suele ocupar espacio recuperable.</summary>
    internal sealed class XKnownSpot
    {
        public string Path, Title, Why;
        public bool Safe = true;
        public bool ContentsOnly = true;
    }

    /// <summary>
    /// Quitar desde Xpace: reglas de protección, aviso de carpetas sincronizadas, puesta al día del análisis en memoria y
    /// sugerencias. Sin interfaz, para poder probarlo aparte (solo depende del escáner).
    /// </summary>
    internal static class XpaceActions
    {
        private static readonly Regex DriveRoot = new Regex(@"^[A-Za-z]:\\?$", RegexOptions.Compiled);
        private static readonly Regex UncRoot = new Regex(@"^\\\\[^\\]+\\[^\\]+\\?$", RegexOptions.Compiled);

        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string p = path.Replace('/', '\\');
            while (p.Length > 3 && p.EndsWith("\\", StringComparison.Ordinal)) p = p.Substring(0, p.Length - 1);
            return p;
        }

        private static bool Same(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
                string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        private static bool Inside(string path, string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            string f = Normalize(folder);
            return Normalize(path).StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Por qué Xpace no quita esa ruta, o null si se puede quitar.</summary>
        public static string ProtectionReason(string path)
        {
            return ProtectionReason(path, XpaceEnv.Current());
        }

        public static string ProtectionReason(string path, XpaceEnv env)
        {
            string p = Normalize(path);
            if (p.Length == 0) return "No es una ruta válida.";
            if (DriveRoot.IsMatch(p) || UncRoot.IsMatch(p)) return "Es una unidad completa.";
            foreach (string segment in p.Split('\\'))
                if (string.Equals(segment, "System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(segment, "$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
                    return "Es una carpeta del sistema.";
            if (Same(p, env.WindowsDir) || Inside(p, env.WindowsDir)) return "Pertenece al sistema de Windows.";
            if (Same(p, env.ProgramFiles) || Same(p, env.ProgramFilesX86)) return "Es la carpeta de programas instalados.";
            if (Same(p, env.ProgramData)) return "Es la carpeta de datos de los programas.";
            if (Same(p, env.UsersRoot)) return "Es la carpeta de los usuarios de este equipo.";
            if (Same(p, env.Profile)) return "Es tu carpeta de usuario.";
            string[] main = { env.Desktop, env.Documents, env.Downloads, env.Pictures, env.Music, env.Videos, env.AppData, env.Roaming, env.Local, env.LocalLow };
            foreach (string folder in main)
                if (Same(p, folder)) return "Es una carpeta principal de tu usuario; elige algo dentro de ella.";
            return null;
        }

        /// <summary>La ruta está en una carpeta que se sincroniza con la nube (borrarla también la borra allí).</summary>
        public static bool IsSynced(string path)
        {
            foreach (string segment in Normalize(path).Split('\\'))
            {
                if (segment.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)) return true;
                if (segment.StartsWith("Dropbox", StringComparison.OrdinalIgnoreCase)) return true;
                if (segment.StartsWith("Google Drive", StringComparison.OrdinalIgnoreCase) || segment.Equals("GoogleDrive", StringComparison.OrdinalIgnoreCase)) return true;
                if (segment.Equals("iCloudDrive", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ── Quitar de verdad ─────────────────────────────────────────────

        /// <summary>
        /// Quita un archivo o carpeta: a la papelera de reciclaje (recuperable) o para siempre. Devuelve null si salió bien,
        /// o el motivo del fallo. Para <paramref name="contentsOnly"/> quita lo que hay dentro y deja la carpeta.
        /// </summary>
        public static string Remove(string path, bool isFolder, bool permanent, bool contentsOnly)
        {
            try
            {
                if (contentsOnly && Directory.Exists(path))
                {
                    int failed = 0, removed = 0;
                    foreach (string entry in Directory.GetFileSystemEntries(path))
                    {
                        string problem = RemoveOne(entry, Directory.Exists(entry), permanent);
                        if (problem == null) removed++; else failed++;
                    }
                    if (failed > 0 && removed == 0) return "Windows no dejó quitar nada: todo está en uso.";
                    return failed > 0 ? "Se quitó lo que se pudo; " + failed + " elementos estaban en uso y se quedaron." : null;
                }
                return RemoveOne(path, isFolder, permanent);
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static string RemoveOne(string path, bool isFolder, bool permanent)
        {
            try
            {
                if (isFolder)
                {
                    if (permanent) { ClearReadOnly(path); Directory.Delete(path, true); }
                    else Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else
                {
                    if (permanent) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
                    else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static void ClearReadOnly(string folder)
        {
            try
            {
                foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
            catch { }
        }

        // ── Poner al día el análisis sin volver a recorrer el disco ───────

        private static XNode Find(XResult r, string path, out string[] parts)
        {
            parts = null;
            string root = Normalize(r.Path), p = Normalize(path);
            if (root.Length == 0 || !p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            string rest = p.Substring(root.Length).Trim('\\');
            if (rest.Length == 0) return null;
            parts = rest.Split('\\');
            XNode node = r.Root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string name = parts[i];
                XNode next = node.Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                if (next == null) return null;
                node = next;
            }
            return node;
        }

        private static bool IsUnder(XNode node, XNode ancestor)
        {
            for (XNode n = node; n != null; n = n.Parent) if (n == ancestor) return true;
            return false;
        }

        /// <summary>Quita de lo analizado un elemento ya borrado (carpeta o archivo de la lista de grandes).</summary>
        public static void Forget(XResult r, string path, bool isFolder)
        {
            string[] parts;
            XNode parent = Find(r, path, out parts);
            if (parent == null) return;
            string last = parts[parts.Length - 1];
            if (isFolder)
            {
                XNode target = parent.Children.FirstOrDefault(c => string.Equals(c.Name, last, StringComparison.OrdinalIgnoreCase));
                if (target == null) return;
                parent.Children.Remove(target);
                for (XNode n = parent; n != null; n = n.Parent)
                {
                    n.Size -= target.Size;
                    n.Files -= target.Files;
                    n.Folders -= target.Folders + 1;
                }
                r.Largest.RemoveAll(f => IsUnder(f.Folder, target));
            }
            else
            {
                int index = r.Largest.FindIndex(f => f.Folder == parent && string.Equals(f.Name, last, StringComparison.OrdinalIgnoreCase));
                if (index < 0) return;
                XFile file = r.Largest[index];
                r.Largest.RemoveAt(index);
                parent.OwnSize -= file.Size;
                parent.OwnFiles -= 1;
                for (XNode n = parent; n != null; n = n.Parent) { n.Size -= file.Size; n.Files -= 1; }
                string ext = System.IO.Path.GetExtension(file.Name).ToLowerInvariant();
                if (ext.Length == 0) ext = "(sin extensión)";
                XType type = r.Types.FirstOrDefault(t => string.Equals(t.Extension, ext, StringComparison.OrdinalIgnoreCase));
                if (type != null) { type.Size -= file.Size; type.Count -= 1; }
            }
            r.Bytes = r.Root.Size;
            r.Files = r.Root.Files;
            r.Folders = r.Root.Folders;
        }
    }

    /// <summary>Qué conviene quitar: ubicaciones conocidas y lo que sale del análisis.</summary>
    internal static class XpaceSuggestions
    {
        public const long Minimum = 50L << 20;

        private static void Spot(List<XKnownSpot> list, string path, string title, string why, bool safe, bool contentsOnly)
        {
            if (string.IsNullOrEmpty(path)) return;
            list.Add(new XKnownSpot { Path = path, Title = title, Why = why, Safe = safe, ContentsOnly = contentsOnly });
        }

        /// <summary>Ubicaciones conocidas (sin comprobar si existen ni cuánto ocupan).</summary>
        public static List<XKnownSpot> KnownSpots(XpaceEnv env)
        {
            List<XKnownSpot> list = new List<XKnownSpot>();
            string local = env.Local, roaming = env.Roaming, profile = env.Profile;
            Spot(list, env.Temp == null ? null : env.Temp.TrimEnd('\\'), "Archivos temporales", "Programas y Windows los vuelven a crear; lo que esté en uso se queda.", true, true);
            if (!string.IsNullOrEmpty(local)) Spot(list, System.IO.Path.Combine(local, "Temp"), "Archivos temporales del usuario", "Programas y Windows los vuelven a crear; lo que esté en uso se queda.", true, true);
            if (!string.IsNullOrEmpty(local))
            {
                Spot(list, System.IO.Path.Combine(local, @"Microsoft\Windows\INetCache"), "Caché de Internet de Windows", "Se vuelve a llenar sola al navegar.", true, true);
                Spot(list, System.IO.Path.Combine(local, @"CrashDumps"), "Volcados de errores", "Solo sirven para diagnosticar cierres inesperados.", true, true);
                Spot(list, System.IO.Path.Combine(local, @"Microsoft\Windows\WER"), "Informes de errores de Windows", "Solo sirven para diagnosticar fallos.", true, true);
                Spot(list, System.IO.Path.Combine(local, @"npm-cache"), "Caché de npm", "npm la vuelve a llenar cuando instala paquetes.", true, true);
                Spot(list, System.IO.Path.Combine(local, @"pip\Cache"), "Caché de pip", "pip la vuelve a llenar cuando instala paquetes.", true, true);
                Spot(list, System.IO.Path.Combine(local, @"NuGet\v3-cache"), "Caché de NuGet", "Se vuelve a descargar al restaurar paquetes.", true, true);
                string[][] browsers =
                {
                    new[] { "Google Chrome", @"Google\Chrome\User Data\Default" },
                    new[] { "Microsoft Edge", @"Microsoft\Edge\User Data\Default" },
                    new[] { "Brave", @"BraveSoftware\Brave-Browser\User Data\Default" }
                };
                foreach (string[] browser in browsers)
                    foreach (string folder in new[] { "Cache", "Code Cache", "GPUCache" })
                        Spot(list, System.IO.Path.Combine(local, browser[1], folder), "Caché de " + browser[0] + " (" + folder + ")", "El navegador la recrea; no borra contraseñas ni marcadores.", true, true);
                string firefox = System.IO.Path.Combine(local, @"Mozilla\Firefox\Profiles");
                try
                {
                    if (Directory.Exists(firefox))
                        foreach (string profileFolder in Directory.GetDirectories(firefox))
                            Spot(list, System.IO.Path.Combine(profileFolder, "cache2"), "Caché de Firefox", "Firefox la recrea; no borra contraseñas ni marcadores.", true, true);
                }
                catch { }
            }
            if (!string.IsNullOrEmpty(roaming)) Spot(list, System.IO.Path.Combine(roaming, "npm-cache"), "Caché de npm", "npm la vuelve a llenar cuando instala paquetes.", true, true);
            if (!string.IsNullOrEmpty(profile)) Spot(list, System.IO.Path.Combine(profile, @".gradle\caches"), "Caché de Gradle", "Se vuelve a descargar al compilar.", true, true);
            return list;
        }

        /// <summary>Ubicaciones conocidas que existen y ocupan al menos <paramref name="minimum"/> bytes.</summary>
        public static List<XSuggestion> Known(XpaceEnv env, long minimum, Func<string, long> measure, Func<string, bool> exists)
        {
            if (measure == null) measure = path => new XpaceScanner().Scan(path, System.Threading.CancellationToken.None).Bytes;
            if (exists == null) exists = Directory.Exists;
            List<XKnownSpot> spots = KnownSpots(env);
            List<XSuggestion> found = new List<XSuggestion>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<XKnownSpot> unique = new List<XKnownSpot>();
            foreach (XKnownSpot spot in spots)
                if (seen.Add(XpaceActions.Normalize(spot.Path)) && exists(spot.Path)) unique.Add(spot);
            object gate = new object();
            Parallel.ForEach(unique, spot =>
            {
                long size;
                try { size = measure(spot.Path); } catch { return; }
                if (size < minimum) return;
                lock (gate)
                    found.Add(new XSuggestion { Path = spot.Path, Title = spot.Title, Why = spot.Why, Safe = spot.Safe, Size = size, IsFolder = true, ContentsOnly = spot.ContentsOnly });
            });
            return Clean(found);
        }

        /// <summary>Papelera de reciclaje del usuario: solo informativa, se vacía desde el Explorador.</summary>
        public static XSuggestion RecycleBin(Func<string, long> measure)
        {
            try
            {
                string sid = WindowsIdentity.GetCurrent().User.Value;
                string root = Environment.GetEnvironmentVariable("SystemDrive");
                if (string.IsNullOrEmpty(root)) root = "C:";
                string path = root + "\\$Recycle.Bin\\" + sid;
                if (!Directory.Exists(path)) return null;
                if (measure == null) measure = p => new XpaceScanner().Scan(p, System.Threading.CancellationToken.None).Bytes;
                long size = measure(path);
                if (size < (1 << 20)) return null;
                return new XSuggestion { Path = path, Title = "Papelera de reciclaje", Why = "Ya está «borrado» pero sigue ocupando espacio. Vacíala desde el Explorador.", Safe = true, Size = size, IsFolder = true, InfoOnly = true };
            }
            catch { return null; }
        }

        private static readonly string[] Regenerable = { "node_modules", "__pycache__", ".venv", "venv", "obj", "bin" };

        private static bool HasProjectBeside(string folder)
        {
            try
            {
                return Directory.GetFiles(folder, "*.csproj").Length > 0 || Directory.GetFiles(folder, "*.sln").Length > 0 ||
                    Directory.GetFiles(folder, "*.vbproj").Length > 0 || Directory.GetFiles(folder, "*.fsproj").Length > 0;
            }
            catch { return false; }
        }

        /// <summary>Sugerencias que salen de lo analizado: regenerables, instaladores, comprimidos y archivos enormes sin tocar.</summary>
        public static List<XSuggestion> From(XResult r, long minimum, DateTime now)
        {
            List<XSuggestion> found = new List<XSuggestion>();
            Stack<XNode> stack = new Stack<XNode>();
            stack.Push(r.Root);
            while (stack.Count > 0)
            {
                XNode node = stack.Pop();
                foreach (XNode child in node.Children)
                {
                    string rule = Regenerable.FirstOrDefault(n => string.Equals(n, child.Name, StringComparison.OrdinalIgnoreCase));
                    string full = child.FullPath(r.Path);
                    bool match = rule != null && child.Size >= minimum;
                    if (match && (rule == "obj" || rule == "bin") && !HasProjectBeside(node.FullPath(r.Path))) match = false;
                    if (!match) { stack.Push(child); continue; }
                    string title, why;
                    bool safe = true;
                    switch (rule.ToLowerInvariant())
                    {
                        case "node_modules": title = "Dependencias de Node"; why = "npm install las vuelve a crear."; break;
                        case "__pycache__": title = "Caché de Python"; why = "Python la recrea al ejecutar."; break;
                        case "obj": case "bin": title = "Compilación (" + rule + ")"; why = "Se vuelve a generar al compilar el proyecto."; safe = false; break;
                        default: title = "Entorno virtual de Python"; why = "Se puede recrear, pero revisa que tengas el requirements."; safe = false; break;
                    }
                    found.Add(new XSuggestion { Path = full, Title = title + " · " + node.Name, Why = why, Safe = safe, Size = child.Size, IsFolder = true });
                }
            }
            DateTime year = now.AddDays(-365);
            foreach (XFile file in r.Largest)
            {
                string folder = file.Folder.FullPath(r.Path);
                string full = folder.TrimEnd('\\') + "\\" + file.Name;
                string ext = System.IO.Path.GetExtension(file.Name).ToLowerInvariant();
                bool downloads = folder.IndexOf("\\Downloads", StringComparison.OrdinalIgnoreCase) >= 0 || folder.IndexOf("\\Descargas", StringComparison.OrdinalIgnoreCase) >= 0;
                DateTime modified = DateTime.MaxValue;
                try { if (file.Modified > 0) modified = DateTime.FromFileTimeUtc(file.Modified); } catch { }
                if (downloads && file.Size >= minimum && (ext == ".exe" || ext == ".msi" || ext == ".iso" || ext == ".msix" || ext == ".msixbundle"))
                    found.Add(new XSuggestion { Path = full, Title = file.Name, Why = "Instalador o imagen de disco en Descargas: si ya instalaste el programa, no hace falta.", Safe = false, Size = file.Size });
                else if (file.Size >= (1L << 30) && modified < year)
                    found.Add(new XSuggestion { Path = full, Title = file.Name, Why = "Más de 1 GB sin modificar desde " + modified.ToString("yyyy-MM-dd") + ".", Safe = false, Size = file.Size });
                else if (downloads && file.Size >= (200L << 20) && (ext == ".zip" || ext == ".7z" || ext == ".rar"))
                    found.Add(new XSuggestion { Path = full, Title = file.Name, Why = "Comprimido grande en Descargas; si ya lo extrajiste, sobra.", Safe = false, Size = file.Size });
            }
            return Clean(found.Where(s => XpaceActions.ProtectionReason(s.Path) == null).ToList());
        }

        /// <summary>Sin duplicados, sin lo que ya está dentro de otra sugerencia y de mayor a menor tamaño.</summary>
        public static List<XSuggestion> Clean(List<XSuggestion> all)
        {
            List<XSuggestion> unique = new List<XSuggestion>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XSuggestion s in all) if (seen.Add(XpaceActions.Normalize(s.Path))) unique.Add(s);
            List<XSuggestion> result = unique.Where(s => !unique.Any(o => o != s &&
                XpaceActions.Normalize(s.Path).StartsWith(XpaceActions.Normalize(o.Path).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
            result.Sort((a, b) => b.Size.CompareTo(a.Size));
            return result;
        }
    }
}
