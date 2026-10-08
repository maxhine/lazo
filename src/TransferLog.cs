using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lazo
{
    internal static class TransferLog
    {
        public static void Add(string direction, string peer, string file, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(file)) return;
            try
            {
                string path = Path();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                List<string> lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
                lines.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "|" + direction + "|" + Clean(peer) + "|" + Clean(file) + "|" + Clean(fullPath));
                if (lines.Count > 40) lines = lines.Skip(lines.Count - 40).ToList();
                File.WriteAllLines(path, lines);
            }
            catch { }
        }

        public static string[] Recent()
        {
            try
            {
                string path = Path();
                if (!File.Exists(path)) return new string[0];
                return File.ReadAllLines(path).Reverse().Take(30).ToArray();
            }
            catch { return new string[0]; }
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return value.Replace("|", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static string Path()
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "history.txt");
        }
    }
}
