using System;
using System.Linq;

namespace Lazo
{
    /// <summary>
    /// Lo que pasa cuando llega un borrado o algo de un grupo: se valida quién lo manda y se actualiza el
    /// almacén local. Sin interfaz, para poder probarlo aparte.
    /// </summary>
    internal static class ChatEvents
    {
        public sealed class Result
        {
            public bool Changed;
            public Guid Thread;
            public string Title, Text;
            /// <summary>Avisar a la persona (mensaje nuevo o alta en un grupo).</summary>
            public bool Notify;
        }

        public static Result Apply(Guid from, string name, byte kind, string body, Guid self)
        {
            Result none = new Result();
            body = body ?? "";
            string[] lines = body.Split('\n');
            if (kind == NetworkEngine.ChatKindDelete)
            {
                Guid groupId;
                bool inGroup = lines.Length >= 2 && Guid.TryParse(lines[0], out groupId);
                string id = (inGroup ? lines[1] : lines[0]).Trim();
                Guid thread = from;
                string sender = null;
                if (inGroup)
                {
                    Guid.TryParse(lines[0], out thread);
                    ChatGroup group = ChatGroups.Get(thread);
                    if (group == null || !IsMember(group, from)) return none;
                    sender = name;
                }
                if (id.Length == 0 || !ChatStore.MarkDeleted(thread, id, sender)) return none;
                return new Result { Changed = true, Thread = thread };
            }
            if (kind == NetworkEngine.ChatKindGroupMessage)
            {
                Guid groupId;
                if (lines.Length < 3 || !Guid.TryParse(lines[0], out groupId)) return none;
                ChatGroup group = ChatGroups.Get(groupId);
                if (group == null || !IsMember(group, from)) return none;
                string text = string.Join("\n", lines.Skip(2)).Trim();
                if (text.Length == 0 || text.Length > 2000) return none;
                ChatStore.AppendIncoming(groupId, group.Name, text, lines[1].Trim(), name);
                return new Result { Changed = true, Thread = groupId, Notify = true, Title = group.Name, Text = name + ": " + text };
            }
            if (kind == NetworkEngine.ChatKindGroupInfo)
            {
                ChatGroup incoming = ChatGroups.Unpack(body, self);
                if (incoming == null || incoming.Owner != from) return none;
                ChatGroup existing = ChatGroups.Get(incoming.Id);
                if (existing != null && existing.Owner != from) return none;
                ChatGroups.Save(incoming);
                ChatStore.EnsureThread(incoming.Id, incoming.Name);
                return new Result { Changed = true, Thread = incoming.Id, Notify = existing == null, Title = incoming.Name, Text = name + " te añadió al grupo" };
            }
            if (kind == NetworkEngine.ChatKindGroupLeave)
            {
                Guid groupId;
                if (!Guid.TryParse(lines[0].Trim(), out groupId)) return none;
                ChatGroup group = ChatGroups.Get(groupId);
                if (group == null || !IsMember(group, from)) return none;
                group.Members.RemoveAll(m => m.Id == from);
                ChatGroups.Save(group);
                return new Result { Changed = true, Thread = groupId };
            }
            return none;
        }

        public static bool IsMember(ChatGroup group, Guid id)
        {
            return group.Owner == id || group.Members.Any(m => m.Id == id);
        }
    }
}
