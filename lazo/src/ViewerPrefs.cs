using System;
using System.IO;

namespace Lazo
{
    /// <summary>
    /// Preferencias del visor de pantalla compartida. «Ajustar la resolución del otro equipo al controlar»: al empezar a controlar,
    /// el otro equipo toma el tamaño de esta ventana (por defecto activado). Igual que en el Mac.
    /// </summary>
    internal static class ViewerPrefs
    {
        private static bool? _autoFit;

        private static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "autofit.txt"); }
        }

        public static bool AutoFit
        {
            get
            {
                if (_autoFit == null)
                {
                    try { _autoFit = File.ReadAllText(FilePath).Trim() != "0"; }
                    catch { _autoFit = true; }
                }
                return _autoFit.Value;
            }
        }

        public static void SetAutoFit(bool on, bool persist)
        {
            _autoFit = on;
            if (!persist) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, on ? "1" : "0");
            }
            catch { }
        }
    }
}
