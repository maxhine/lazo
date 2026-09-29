using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lazo
{
    internal sealed class ChatLine
    {
        public DateTime When;
        public bool Mine;
        public string Text;
    }

    internal sealed class ChatThread
    {
        public Guid Id;
        public string Name;
        public string Preview;
        public int Unread;
        public DateTime When;
    }

    internal static class ChatStore
    {
        public static void Append(Guid id, string name, bool mine, string text)
        {
            text = Clean(text);
            if (id == Guid.Empty || text.Length == 0) return;
            try
            {
                string path = ThreadPath(id);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "|" + (mine ? "out" : "in") + "|" + text + Environment.NewLine);
                List<ChatThread> threads = LoadIndex();
                ChatThread thread = threads.FirstOrDefault(item => item.Id == id);
                if (thread == null)
                {
                    thread = new ChatThread { Id = id };
                    threads.Add(thread);
                }
                thread.Name = string.IsNullOrWhiteSpace(name) ? thread.Name : Clean(name);
                if (string.IsNullOrWhiteSpace(thread.Name)) thread.Name = "Compañero";
                thread.Preview = text.Length > 42 ? text.Substring(0, 42) + "…" : text;
                thread.When = DateTime.Now;
                if (!mine) thread.Unread++;
                SaveIndex(threads);
            }
            catch { }
        }

        public static void MarkRead(Guid id)
        {
            try
            {
                List<ChatThread> threads = LoadIndex();
                ChatThread thread = threads.FirstOrDefault(item => item.Id == id);
                if (thread == null || thread.Unread == 0) return;
                thread.Unread = 0;
                SaveIndex(threads);
            }
            catch { }
        }

        public static int UnreadTotal()
        {
            try { return LoadIndex().Sum(item => item.Unread); }
            catch { return 0; }
        }

        public static List<ChatThread> Threads()
        {
            try { return LoadIndex().OrderByDescending(item => item.When).ToList(); }
            catch { return new List<ChatThread>(); }
        }

        public static List<ChatLine> Messages(Guid id)
        {
            List<ChatLine> lines = new List<ChatLine>();
            try
            {
                string path = ThreadPath(id);
                if (!File.Exists(path)) return lines;
                foreach (string row in File.ReadAllLines(path).Reverse().Take(200).Reverse())
                {
                    string[] parts = row.Split(new[] { '|' }, 3);
                    DateTime when;
                    if (parts.Length < 3 || !DateTime.TryParse(parts[0], out when)) continue;
                    lines.Add(new ChatLine { When = when, Mine = parts[1] == "out", Text = parts[2] });
                }
            }
            catch { }
            return lines;
        }

        private static List<ChatThread> LoadIndex()
        {
            List<ChatThread> threads = new List<ChatThread>();
            string path = IndexPath();
            if (!File.Exists(path)) return threads;
            foreach (string row in File.ReadAllLines(path))
            {
                string[] parts = row.Split('|');
                Guid id;
                int unread;
                DateTime when;
                if (parts.Length < 5 || !Guid.TryParse(parts[0], out id) || !int.TryParse(parts[3], out unread) || !DateTime.TryParse(parts[4], out when)) continue;
                threads.Add(new ChatThread { Id = id, Name = parts[1], Preview = parts[2], Unread = unread, When = when });
            }
            return threads;
        }

        private static void SaveIndex(List<ChatThread> threads)
        {
            string path = IndexPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, threads.Select(item =>
                item.Id + "|" + Clean(item.Name) + "|" + Clean(item.Preview) + "|" + item.Unread + "|" + item.When.ToString("yyyy-MM-dd HH:mm")));
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return value.Replace("|", " ").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        private static string ThreadPath(Guid id)
        {
            return Path.Combine(Folder(), id.ToString("N") + ".txt");
        }

        private static string IndexPath()
        {
            return Path.Combine(Folder(), "index.txt");
        }

        private static string Folder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "chat");
        }
    }
}
