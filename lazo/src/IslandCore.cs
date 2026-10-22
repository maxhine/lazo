using System;
using System.Diagnostics;
using System.IO;

namespace Lazo
{
    /// <summary>
    /// El camino de vuelta al núcleo nuevo (la isla): Lazo clásico y la isla no pueden estar abiertos a la vez (usan los mismos
    /// puertos), así que se anota la elección, se cierra el clásico y la isla se abre un instante después.
    /// </summary>
    internal static class IslandCore
    {
        /// <summary>La isla es la que trae el programa de utilidades al lado (engine\Lazo.Engine.exe): así no se confunde con el clásico.</summary>
        private static bool IsIsland(string exe, string self)
        {
            if (!File.Exists(exe)) return false;
            if (!string.IsNullOrEmpty(self) && string.Equals(Path.GetFullPath(exe), Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase)) return false;
            return File.Exists(Path.Combine(Path.GetDirectoryName(exe), "engine", "Lazo.Engine.exe"));
        }

        /// <summary>Dónde está la isla instalada, o null. La que contiene a este Lazo clásico (carpeta classic) va primero.</summary>
        internal static string Find(string self, string localAppData, string programFiles)
        {
            string[] candidates =
            {
                string.IsNullOrEmpty(self) ? null : Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(self) ?? "") ?? "", "lazo.exe"),
                Path.Combine(localAppData, "Lazo", "lazo.exe"),
                Path.Combine(localAppData, "Programs", "Lazo", "lazo.exe"),
                Path.Combine(programFiles, "Lazo", "lazo.exe")
            };
            foreach (string candidate in candidates)
            {
                try { if (candidate != null && IsIsland(candidate, self)) return candidate; }
                catch (ArgumentException) { }
            }
            return null;
        }

        public static string Find()
        {
            return Find(Process.GetCurrentProcess().MainModule.FileName,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        }

        /// <summary>Anota la elección y deja la isla lista para abrirse en cuanto este programa termine. Devuelve el motivo si no se puede.</summary>
        public static string Switch(string island)
        {
            if (island == null || !File.Exists(island)) return "No se encontró la isla instalada.";
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "core.txt"), "island");
                // Espera a que este Lazo suelte los puertos y abre la isla.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c timeout /t 3 /nobreak >nul & start \"\" \"" + island + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
