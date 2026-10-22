using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Lazo
{
    internal sealed class ChatLine
    {
        public DateTime When;
        public bool Mine;
        public string Text;
        public string FilePath;
        public bool Nudge;
        /// <summary>Identificador del mensaje (vacío en los antiguos): permite borrarlo en los dos lados.</summary>
        public string Id = "";
        /// <summary>Quién lo escribió, en los grupos.</summary>
        public string Sender = "";
        /// <summary>Se eliminó para todos: queda el aviso, sin el contenido.</summary>
        public bool Deleted;
        /// <summary>Línea del archivo de la conversación (para borrar los mensajes antiguos sin identificador).</summary>
        public int Row;
    }

    internal sealed class ChatThread
    {
        public Guid Id;
        public string Name;
        public string Preview;
        public int Unread;
        public DateTime When;
    }

    internal sealed class GroupMember
    {
        public Guid Id;
        public string Name;
    }

    /// <summary>Un grupo de chat: sin servidor, cada integrante guarda la lista y los mensajes se envían a cada uno.</summary>
    internal sealed class ChatGroup
    {
        public Guid Id;
        public string Name;
        public Guid Owner;
        /// <summary>Los demás integrantes (sin contarte a ti).</summary>
        public List<GroupMember> Members = new List<GroupMember>();
    }

    internal static class ChatGroups
    {
        public const int MaxMembers = 30;

        public static List<ChatGroup> All()
        {
            List<ChatGroup> groups = new List<ChatGroup>();
            try
            {
                string path = FilePath();
                if (!File.Exists(path)) return groups;
                foreach (string row in File.ReadAllLines(path))
                {
                    string[] parts = row.Split('|');
                    Guid id, owner;
                    if (parts.Length < 4 || !Guid.TryParse(parts[0], out id) || !Guid.TryParse(parts[2], out owner)) continue;
                    ChatGroup group = new ChatGroup { Id = id, Name = parts[1], Owner = owner };
                    foreach (string member in parts[3].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] pair = member.Split(new[] { ':' }, 2);
                        Guid memberId;
                        if (pair.Length == 2 && Guid.TryParse(pair[0], out memberId)) group.Members.Add(new GroupMember { Id = memberId, Name = pair[1] });
                    }
                    groups.Add(group);
                }
            }
            catch { }
            return groups;
        }

        public static ChatGroup Get(Guid id)
        {
            return All().FirstOrDefault(g => g.Id == id);
        }

        public static void Save(ChatGroup group)
        {
            try
            {
                List<ChatGroup> groups = All();
                groups.RemoveAll(g => g.Id == group.Id);
                groups.Add(group);
                string path = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, groups.Select(g => g.Id.ToString("N") + "|" + Clean(g.Name) + "|" + g.Owner.ToString("N") + "|" +
                    string.Join(",", g.Members.Select(m => m.Id.ToString("N") + ":" + Clean(m.Name)))));
            }
            catch { }
        }

        public static void Remove(Guid id)
        {
            try
            {
                List<ChatGroup> groups = All();
                groups.RemoveAll(g => g.Id == id);
                string path = FilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, groups.Select(g => g.Id.ToString("N") + "|" + Clean(g.Name) + "|" + g.Owner.ToString("N") + "|" +
                    string.Join(",", g.Members.Select(m => m.Id.ToString("N") + ":" + Clean(m.Name)))));
            }
            catch { }
        }

        /// <summary>Texto de la lista de integrantes para enviarla por red.</summary>
        public static string Pack(ChatGroup group, Guid self, string selfName)
        {
            StringBuilder text = new StringBuilder();
            text.Append(group.Id.ToString("N")).Append('\n').Append(Clean(group.Name)).Append('\n').Append(group.Owner.ToString("N"));
            text.Append('\n').Append(self.ToString("N")).Append(':').Append(Clean(selfName));
            foreach (GroupMember m in group.Members.Where(x => x.Id != self)) text.Append('\n').Append(m.Id.ToString("N")).Append(':').Append(Clean(m.Name));
            return text.ToString();
        }

        /// <summary>Lo contrario de <see cref="Pack"/>, desde el punto de vista de quien recibe: los demás, sin él.</summary>
        public static ChatGroup Unpack(string body, Guid self)
        {
            string[] lines = (body ?? "").Split('\n');
            Guid id, owner;
            if (lines.Length < 4 || !Guid.TryParse(lines[0], out id) || !Guid.TryParse(lines[2], out owner)) return null;
            ChatGroup group = new ChatGroup { Id = id, Name = Clean(lines[1]), Owner = owner };
            if (group.Name.Length == 0) return null;
            for (int i = 3; i < lines.Length && group.Members.Count < MaxMembers; i++)
            {
                string[] pair = lines[i].Split(new[] { ':' }, 2);
                Guid memberId;
                if (pair.Length == 2 && Guid.TryParse(pair[0], out memberId) && memberId != self && pair[1].Trim().Length > 0)
                    group.Members.Add(new GroupMember { Id = memberId, Name = Clean(pair[1]) });
            }
            return group;
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string cleaned = value.Replace("|", " ").Replace(":", " ").Replace(",", " ").Replace("\r", " ").Replace("\n", " ").Trim();
            return cleaned.Length > 40 ? cleaned.Substring(0, 40) : cleaned;
        }

        private static string FilePath()
        {
            return Path.Combine(ChatStore.DataFolder(), "groups.txt");
        }
    }

    /// <summary>
    /// Identificador de un mensaje escondido al principio del texto con caracteres invisibles: así viaja por el
    /// mismo camino de siempre y los equipos que aún no se actualizan lo siguen mostrando sin cambios.
    /// </summary>
    internal static class WireId
    {
        private const char Start = '⁣', End = '⁤';
        private static readonly char[] Alphabet = { '​', '‌', '‍', '⁠' };

        public static string Wrap(string id, string text)
        {
            if (string.IsNullOrEmpty(id) || id.Length != 8) return text;
            StringBuilder code = new StringBuilder();
            code.Append(Start);
            foreach (char c in id)
            {
                int value = Convert.ToInt32(c.ToString(), 16);
                code.Append(Alphabet[(value >> 2) & 3]).Append(Alphabet[value & 3]);
            }
            code.Append(End);
            return code + text;
        }

        /// <summary>Separa el identificador (o "" si no lo trae) y deja el texto limpio.</summary>
        public static string Unwrap(ref string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != Start || text.Length < 18 || text[17] != End) return "";
            StringBuilder id = new StringBuilder();
            for (int i = 0; i < 8; i++)
            {
                int high = Array.IndexOf(Alphabet, text[1 + 2 * i]), low = Array.IndexOf(Alphabet, text[2 + 2 * i]);
                if (high < 0 || low < 0) return "";
                id.Append(((high << 2) | low).ToString("x"));
            }
            text = text.Substring(18);
            return id.ToString();
        }
    }

    /// <summary>Envíos que no pudieron entregarse porque la persona estaba desconectada: salen cuando vuelve.</summary>
    internal static class ChatOutbox
    {
        private static readonly object Gate = new object();
        private static readonly TimeSpan Life = TimeSpan.FromDays(14);

        public static void Add(Guid peer, byte kind, string body)
        {
            lock (Gate)
            {
                try
                {
                    string path = FilePath();
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.AppendAllText(path, peer.ToString("N") + "|" + kind + "|" + DateTime.UtcNow.Ticks + "|" +
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(body ?? "")) + Environment.NewLine);
                }
                catch { }
            }
        }

        public sealed class Item
        {
            public byte Kind;
            public string Body;
            public string Row;
        }

        public static List<Item> For(Guid peer)
        {
            List<Item> items = new List<Item>();
            lock (Gate)
            {
                try
                {
                    string path = FilePath();
                    if (!File.Exists(path)) return items;
                    foreach (string row in File.ReadAllLines(path))
                    {
                        string[] parts = row.Split('|');
                        Guid id;
                        byte kind;
                        long ticks;
                        if (parts.Length < 4 || !Guid.TryParse(parts[0], out id) || id != peer || !byte.TryParse(parts[1], out kind) || !long.TryParse(parts[2], out ticks)) continue;
                        if (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > Life) continue;
                        items.Add(new Item { Kind = kind, Body = Encoding.UTF8.GetString(Convert.FromBase64String(parts[3])), Row = row });
                    }
                }
                catch { }
            }
            return items;
        }

        public static void Done(Item item)
        {
            lock (Gate)
            {
                try
                {
                    string path = FilePath();
                    if (!File.Exists(path)) return;
                    List<string> rows = File.ReadAllLines(path).ToList();
                    int at = rows.IndexOf(item.Row);
                    if (at >= 0) rows.RemoveAt(at);
                    File.WriteAllLines(path, rows);
                }
                catch { }
            }
        }

        public static int Pending(Guid peer) { return For(peer).Count; }

        private static string FilePath()
        {
            return Path.Combine(ChatStore.DataFolder(), "outbox.txt");
        }
    }

    internal static class ChatStore
    {
        public static string NewId() { return Guid.NewGuid().ToString("N").Substring(0, 8); }

        /// <summary>Carpeta de los datos del chat (las pruebas la cambian para no tocar la real).</summary>
        public static string Root;

        public static string DataFolder()
        {
            return Root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "chat");
        }

        public static string Append(Guid id, string name, bool mine, string text, string msgId = null, string sender = null)
        {
            text = Clean(text);
            if (id == Guid.Empty || text.Length == 0) return "";
            if (string.IsNullOrEmpty(msgId)) msgId = NewId();
            try
            {
                string path = ThreadPath(id);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "|" + (mine ? "out" : "in") + "|" + text + "|" + Clean(msgId) + "|" + Clean(sender) + Environment.NewLine);
                List<ChatThread> threads = LoadIndex();
                ChatThread thread = threads.FirstOrDefault(item => item.Id == id);
                if (thread == null)
                {
                    thread = new ChatThread { Id = id };
                    threads.Add(thread);
                }
                thread.Name = string.IsNullOrWhiteSpace(name) ? thread.Name : Clean(name);
                if (string.IsNullOrWhiteSpace(thread.Name)) thread.Name = "Compañero";
                thread.Preview = PreviewOf(text);
                thread.When = DateTime.Now;
                if (!mine) thread.Unread++;
                SaveIndex(threads);
            }
            catch { }
            return msgId;
        }

        /// <summary>
        /// Mensaje de otra persona. Si imita las marcas internas («FILE», «NUDGE»), se neutraliza: si no,
        /// alguien podría hacer pasar un texto por una tarjeta de archivo que apunta a cualquier ruta del equipo.
        /// </summary>
        public static void AppendIncoming(Guid id, string name, string text, string msgId = null, string sender = null)
        {
            text = Clean(text);
            if (text.StartsWith("FILE") || text == "NUDGE" || text == "DELETED") text = "⁣" + text;
            Append(id, name, false, text, msgId, sender);
        }

        /// <summary>Crea la conversación de un grupo (vacía) para que aparezca en la lista.</summary>
        public static void EnsureThread(Guid id, string name)
        {
            try
            {
                List<ChatThread> threads = LoadIndex();
                ChatThread thread = threads.FirstOrDefault(item => item.Id == id);
                if (thread == null) { thread = new ChatThread { Id = id, Preview = "", When = DateTime.Now }; threads.Add(thread); }
                thread.Name = Clean(name);
                SaveIndex(threads);
            }
            catch { }
        }

        /// <summary>Quita un mensaje solo de este equipo.</summary>
        public static void DeleteLocal(Guid thread, ChatLine line)
        {
            try
            {
                string path = ThreadPath(thread);
                if (!File.Exists(path)) return;
                List<string> rows = File.ReadAllLines(path).ToList();
                int at = Locate(rows, line);
                if (at < 0) return;
                rows.RemoveAt(at);
                File.WriteAllLines(path, rows);
                RefreshPreview(thread, rows);
            }
            catch { }
        }

        /// <summary>El mensaje se eliminó para todos: en su lugar queda un aviso.</summary>
        public static bool MarkDeleted(Guid thread, string msgId, string sender = null, bool fromOther = true)
        {
            if (string.IsNullOrEmpty(msgId)) return false;
            try
            {
                string path = ThreadPath(thread);
                if (!File.Exists(path)) return false;
                List<string> rows = File.ReadAllLines(path).ToList();
                bool changed = false;
                for (int i = 0; i < rows.Count; i++)
                {
                    string[] parts = rows[i].Split(new[] { '|' }, 5);
                    if (parts.Length < 4 || parts[3] != msgId) continue;
                    // Quien pide borrar para todos solo puede borrar lo suyo: lo recibido de él, no lo que escribiste tú.
                    if (fromOther && parts[1] != "in") continue;
                    if (sender != null && (parts.Length < 5 || parts[4] != sender)) continue;
                    parts[2] = "DELETED";
                    rows[i] = string.Join("|", parts);
                    changed = true;
                }
                if (changed) { File.WriteAllLines(path, rows); RefreshPreview(thread, rows); }
                return changed;
            }
            catch { return false; }
        }

        /// <summary>Vacía una conversación. Las de personas desaparecen de la lista; los grupos se quedan.</summary>
        public static void Clear(Guid thread)
        {
            try
            {
                string path = ThreadPath(thread);
                if (File.Exists(path)) File.Delete(path);
                List<ChatThread> threads = LoadIndex();
                bool group = ChatGroups.Get(thread) != null;
                ChatThread entry = threads.FirstOrDefault(item => item.Id == thread);
                if (entry != null)
                {
                    if (group) { entry.Preview = ""; entry.Unread = 0; }
                    else threads.Remove(entry);
                    SaveIndex(threads);
                }
            }
            catch { }
        }

        /// <summary>Quita una conversación y su historial (por ejemplo, al salir de un grupo).</summary>
        public static void Remove(Guid thread)
        {
            try
            {
                string path = ThreadPath(thread);
                if (File.Exists(path)) File.Delete(path);
                List<ChatThread> threads = LoadIndex();
                threads.RemoveAll(item => item.Id == thread);
                SaveIndex(threads);
            }
            catch { }
        }

        private static int Locate(List<string> rows, ChatLine line)
        {
            if (string.IsNullOrEmpty(line.Id))
                return line.Row >= 0 && line.Row < rows.Count ? line.Row : -1;
            for (int i = 0; i < rows.Count; i++)
            {
                string[] parts = rows[i].Split(new[] { '|' }, 5);
                if (parts.Length >= 4 && parts[3] == line.Id) return i;
            }
            return -1;
        }

        private static void RefreshPreview(Guid thread, List<string> rows)
        {
            List<ChatThread> threads = LoadIndex();
            ChatThread entry = threads.FirstOrDefault(item => item.Id == thread);
            if (entry == null) return;
            string last = rows.Count == 0 ? null : rows[rows.Count - 1];
            string[] parts = last == null ? null : last.Split(new[] { '|' }, 5);
            entry.Preview = parts == null || parts.Length < 3 ? "" : PreviewOf(parts[2]);
            SaveIndex(threads);
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
                string[] all = File.ReadAllLines(path);
                for (int index = Math.Max(0, all.Length - 200); index < all.Length; index++)
                {
                    string[] parts = all[index].Split(new[] { '|' }, 5);
                    DateTime when;
                    if (parts.Length < 3 || !DateTime.TryParse(parts[0], out when)) continue;
                    ChatLine line = ParseLine(when, parts[1] == "out", parts[2]);
                    line.Row = index;
                    if (parts.Length > 3) line.Id = parts[3];
                    if (parts.Length > 4) line.Sender = parts[4];
                    lines.Add(line);
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

        public static void AppendFile(Guid id, string name, bool mine, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Append(id, name, mine, "FILE\t" + path.Replace("\t", " "));
        }

        public static void AppendNudge(Guid id, string name, bool mine)
        {
            Append(id, name, mine, "NUDGE");
        }

        private static string PreviewOf(string text)
        {
            if (text.StartsWith("FILE\t")) return "Archivo · " + Path.GetFileName(text.Substring(5));
            if (text == "NUDGE") return "Zumbido";
            if (text == "DELETED") return "Mensaje eliminado";
            return text.Length > 42 ? text.Substring(0, 42) + "…" : text;
        }

        private static ChatLine ParseLine(DateTime when, bool mine, string text)
        {
            ChatLine line = new ChatLine { When = when, Mine = mine, Text = text };
            if (text == "DELETED") { line.Deleted = true; line.Text = mine ? "Eliminaste este mensaje" : "Se eliminó este mensaje"; return line; }
            if (text == "NUDGE") { line.Nudge = true; line.Text = "Zumbido"; return line; }
            if (text.StartsWith("FILE\t"))
            {
                line.FilePath = text.Substring(5);
                line.Text = Path.GetFileName(line.FilePath);
            }
            return line;
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
            return DataFolder();
        }
    }

    internal static class ChatPrefs
    {
        public static bool UseBalloon { get; private set; }

        public static void Load()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "chat-notify.txt");
                UseBalloon = File.Exists(path) && File.ReadAllText(path).Trim() == "balloon";
            }
            catch { UseBalloon = false; }
        }

        public static void Set(bool balloon)
        {
            UseBalloon = balloon;
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "chat-notify.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, balloon ? "balloon" : "tray");
            }
            catch { }
        }
    }
}
