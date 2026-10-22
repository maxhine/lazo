using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace Lazo
{
    /// <summary>
    /// Inicio con Windows por usuario. El instalador puede dejar además una entrada para todo el equipo
    /// (HKLM), que un usuario sin permisos de administrador no puede borrar; por eso el estado real se
    /// guarda también en %AppData%\Lazo\startup.txt y Lazo se cierra solo si arrancó por esa entrada
    /// estando desactivado. Se respeta también lo que la persona desactive desde el Administrador de tareas.
    /// </summary>
    internal static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string Name = "Lazo";
        public const string Argument = "--startup";

        private static string PreferencePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "startup.txt");
        }

        /// <summary>Preferencia explícita de la persona; null si nunca la cambió desde Lazo.</summary>
        private static bool? Preference()
        {
            try
            {
                string value = File.ReadAllText(PreferencePath()).Trim();
                if (value == "on") return true;
                if (value == "off") return false;
            }
            catch { }
            return null;
        }

        public static bool IsEnabled()
        {
            bool? preference = Preference();
            if (preference.HasValue) return preference.Value && UserEntryActive();
            if (UserEntryActive()) return true;
            using (RegistryKey machine = Machine()) return Has(machine) && Approved(Registry.LocalMachine);
        }

        public static void Set(bool enabled)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath()));
                File.WriteAllText(PreferencePath(), enabled ? "on" : "off");
            }
            catch { }
            string command = "\"" + Assembly.GetExecutingAssembly().Location + "\" " + Argument;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled) key.SetValue(Name, command);
                    else key.DeleteValue(Name, false);
                }
                // Si se había desactivado en el Administrador de tareas, se vuelve a aprobar.
                using (RegistryKey approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                    if (approved != null && approved.GetValue(Name) != null) approved.DeleteValue(Name, false);
            }
            catch { }
            if (!enabled)
            {
                // Solo funciona con permisos de administrador; si no, la preferencia anterior lo cubre.
                try
                {
                    using (RegistryKey machine = Machine())
                    using (RegistryKey run = machine == null ? null : machine.OpenSubKey(RunKey, true))
                        if (run != null && run.GetValue(Name) != null) run.DeleteValue(Name, false);
                }
                catch { }
            }
        }

        /// <summary>
        /// Al arrancar: si Lazo se abrió automáticamente con la sesión pero la persona lo desactivó, se cierra.
        /// </summary>
        public static bool ShouldExitAtLogon(string[] args)
        {
            if (Preference() != false) return false;
            if (Array.IndexOf(args, Argument) >= 0) return true;
            // Entrada antigua del instalador (sin argumento): se reconoce por arrancar junto con la sesión.
            try
            {
                DateTime started = Process.GetCurrentProcess().StartTime;
                foreach (Process shell in Process.GetProcessesByName("explorer"))
                {
                    if (shell.SessionId != Process.GetCurrentProcess().SessionId) continue;
                    if ((started - shell.StartTime).TotalSeconds < 120) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool UserEntryActive()
        {
            return Has(Registry.CurrentUser) && Approved(Registry.CurrentUser);
        }

        private static bool Approved(RegistryKey root)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(ApprovedKey))
                {
                    byte[] value = key == null ? null : key.GetValue(Name) as byte[];
                    return value == null || value.Length == 0 || (value[0] & 1) == 0;
                }
            }
            catch { return true; }
        }

        private static bool Has(RegistryKey root)
        {
            if (root == null) return false;
            using (RegistryKey key = root.OpenSubKey(RunKey))
                return key != null && key.GetValue(Name) != null;
        }

        private static RegistryKey Machine()
        {
            try
            {
                return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                    Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
            }
            catch { return null; }
        }
    }
}
