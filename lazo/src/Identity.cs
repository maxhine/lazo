using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Lazo
{
    internal static class Identity
    {
        public static string Current { get; private set; }

        public static void Load()
        {
            string custom = Read();
            Current = string.IsNullOrWhiteSpace(custom) ? WindowsName() : Trim(custom);
        }

        public static void Set(string name, bool persist)
        {
            string windows = WindowsName();
            string next = string.IsNullOrWhiteSpace(name) ? windows : Trim(name);
            if (next.Length == 0) next = windows;
            Current = next;
            if (!persist) return;
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "name.txt");
                if (string.Equals(next, windows, StringComparison.Ordinal))
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, next);
            }
            catch { }
        }

        public static string WindowsName()
        {
            int size = 256;
            StringBuilder buffer = new StringBuilder(size);
            if (GetUserNameEx(3, buffer, ref size) && buffer.Length > 0) return Trim(buffer.ToString());
            string user = Environment.UserName;
            return string.IsNullOrWhiteSpace(user) ? "Lazo" : Trim(user);
        }

        private static string Read()
        {
            try
            {
                return File.ReadAllText(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "name.txt"));
            }
            catch { return null; }
        }

        private static string Trim(string value)
        {
            StringBuilder builder = new StringBuilder();
            foreach (char c in value)
            {
                if (char.IsControl(c) || c == '|') continue;
                builder.Append(c);
                if (builder.Length == 60) break;
            }
            return builder.ToString().Trim();
        }

        [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserNameEx(int format, StringBuilder name, ref int size);
    }
}
