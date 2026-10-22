using System;
using System.IO;
using System.Text;

namespace Lazo
{
    /// <summary>
    /// Registro de diagnóstico en <c>%APPDATA%\Lazo\lazo.log</c> (sin contenido de mensajes ni de archivos). Se recorta a 1 MB.
    /// Sirve para saber por qué falla una conexión o una transmisión cuando no hay forma de verla en pantalla.
    /// </summary>
    internal static class DiagLog
    {
        private static readonly object Gate = new object();

        public static string Path
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "lazo.log"); }
        }

        public static void Write(string text)
        {
            try
            {
                lock (Gate)
                {
                    string path = Path;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    FileInfo info = new FileInfo(path);
                    if (info.Exists && info.Length > (1 << 20)) File.Delete(path);
                    File.AppendAllText(path, DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + " " + text + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
