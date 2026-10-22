using System;
using System.IO;
using System.Linq;
using Lazo;

/// <summary>Chat sin interfaz: identificadores, borrado local y para todos, grupos, cola de salida.</summary>
internal static class ChatSmoke
{
    private static bool _ok = true;
    private static void Check(bool ok, string what) { Console.WriteLine((ok ? "  ok  " : "  FALLA ") + what); if (!ok) _ok = false; }

    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "lazo-chat-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(root);
        ChatStore.Root = root;
        try
        {
            Guid me = Guid.NewGuid(), ana = Guid.NewGuid(), luis = Guid.NewGuid();

            // Identificador invisible en el texto
            string wire = WireId.Wrap("a1b2c3d4", "hola mundo");
            string text = wire;
            string id = WireId.Unwrap(ref text);
            Check(id == "a1b2c3d4" && text == "hola mundo", "el identificador viaja escondido y se separa del texto");
            string plain = "sin id";
            Check(WireId.Unwrap(ref plain) == "" && plain == "sin id", "un mensaje sin identificador queda igual");
            Check(wire.Replace("​", "").Replace("‌", "").Replace("‍", "").Replace("⁠", "").Replace("⁣", "").Replace("⁤", "") == "hola mundo", "todo lo añadido es invisible");

            // Mensajes con identificador y borrado
            string m1 = ChatStore.Append(ana, "Ana", true, "primero");
            ChatStore.Append(ana, "Ana", false, "segundo", "deadbeef");
            ChatStore.Append(ana, "Ana", false, "tercero", "cafe0001");
            var lines = ChatStore.Messages(ana);
            Check(lines.Count == 3 && lines[0].Id == m1 && lines[1].Id == "deadbeef", "los mensajes guardan su identificador");
            ChatStore.DeleteLocal(ana, lines[0]);
            lines = ChatStore.Messages(ana);
            Check(lines.Count == 2 && lines[0].Text == "segundo", "eliminar para mí quita solo ese mensaje");
            Check(ChatStore.MarkDeleted(ana, "deadbeef"), "eliminar para todos reemplaza el mensaje recibido");
            lines = ChatStore.Messages(ana);
            Check(lines[0].Deleted && lines[0].Text.Contains("eliminó") && lines[1].Text == "tercero", "queda el aviso: se eliminó este mensaje");
            ChatStore.Append(ana, "Ana", true, "mío", "11112222");
            Check(!ChatStore.MarkDeleted(ana, "11112222"), "otra persona no puede borrar un mensaje que escribí yo");
            Check(ChatStore.MarkDeleted(ana, "11112222", null, false), "yo sí puedo borrar el mío para todos");
            Check(ChatStore.Threads().First(t => t.Id == ana).Preview == "Mensaje eliminado", "la vista previa de la lista se actualiza");

            // Mensajes antiguos (sin identificador) se pueden borrar por línea
            File.AppendAllText(Path.Combine(root, ana.ToString("N") + ".txt"), "2025-01-01 10:00|in|antiguo" + Environment.NewLine);
            lines = ChatStore.Messages(ana);
            ChatLine old = lines.Last();
            Check(old.Text == "antiguo" && old.Id == "", "los mensajes antiguos se siguen leyendo");
            ChatStore.DeleteLocal(ana, old);
            Check(!ChatStore.Messages(ana).Any(l => l.Text == "antiguo"), "y se pueden eliminar");

            // Vaciar
            ChatStore.Clear(ana);
            Check(ChatStore.Messages(ana).Count == 0 && !ChatStore.Threads().Any(t => t.Id == ana), "vaciar una conversación directa la quita de la lista");

            // Grupos
            ChatGroup group = new ChatGroup { Id = Guid.NewGuid(), Name = "Obra: torre | A", Owner = me };
            group.Members.Add(new GroupMember { Id = ana, Name = "Ana, la jefa" });
            group.Members.Add(new GroupMember { Id = luis, Name = "Luis" });
            ChatGroups.Save(group);
            ChatGroup read = ChatGroups.Get(group.Id);
            Check(read != null && read.Members.Count == 2 && !read.Name.Contains("|") && read.Members[0].Name == "Ana  la jefa", "el grupo se guarda y limpia los caracteres reservados");
            string packed = ChatGroups.Pack(group, me, "Jhon");
            ChatGroup asLuis = ChatGroups.Unpack(packed, luis);
            Check(asLuis != null && asLuis.Owner == me && asLuis.Members.Count == 2 && asLuis.Members.All(m => m.Id != luis) && asLuis.Members.Any(m => m.Id == me), "quien recibe el grupo ve a los demás (incluido el creador) y no a sí mismo");

            // Eventos de red
            ChatGroups.Remove(group.Id);
            ChatEvents.Result r = ChatEvents.Apply(me, "Jhon", NetworkEngine.ChatKindGroupInfo, packed, luis);
            Check(r.Changed && r.Notify && ChatGroups.Get(group.Id) != null, "recibir la invitación crea el grupo");
            r = ChatEvents.Apply(ana, "Ana", NetworkEngine.ChatKindGroupMessage, group.Id.ToString("N") + "\nabcd1234\nhola grupo\ncon dos líneas", luis);
            var gl = ChatStore.Messages(group.Id);
            Check(r.Changed && r.Notify && gl.Count == 1 && gl[0].Sender == "Ana" && gl[0].Id == "abcd1234" && gl[0].Text.StartsWith("hola grupo"), "un mensaje de grupo llega con su autor");
            r = ChatEvents.Apply(Guid.NewGuid(), "Intruso", NetworkEngine.ChatKindGroupMessage, group.Id.ToString("N") + "\nzzzz0000\nspam", luis);
            Check(!r.Changed && ChatStore.Messages(group.Id).Count == 1, "quien no es del grupo no puede escribir en él");
            r = ChatEvents.Apply(Guid.NewGuid(), "Intruso", NetworkEngine.ChatKindGroupInfo, ChatGroups.Pack(new ChatGroup { Id = group.Id, Name = "Hackeado", Owner = Guid.NewGuid() }, Guid.NewGuid(), "Intruso"), luis);
            Check(!r.Changed && ChatGroups.Get(group.Id).Name != "Hackeado", "solo el creador puede cambiar un grupo");
            r = ChatEvents.Apply(luis, "Luis", NetworkEngine.ChatKindDelete, group.Id.ToString("N") + "\nabcd1234", luis);
            Check(!r.Changed, "un integrante no puede borrar el mensaje de otro");
            r = ChatEvents.Apply(ana, "Ana", NetworkEngine.ChatKindDelete, group.Id.ToString("N") + "\nabcd1234", luis);
            Check(r.Changed && ChatStore.Messages(group.Id)[0].Deleted, "el autor sí borra su mensaje de grupo para todos");
            r = ChatEvents.Apply(ana, "Ana", NetworkEngine.ChatKindGroupLeave, group.Id.ToString("N"), luis);
            Check(r.Changed && !ChatGroups.Get(group.Id).Members.Any(m => m.Id == ana), "cuando alguien sale, deja de ser integrante");

            // Borrado directo
            ChatStore.AppendIncoming(luis, "Luis", "directo", "feedface");
            r = ChatEvents.Apply(luis, "Luis", NetworkEngine.ChatKindDelete, "feedface", me);
            Check(r.Changed && ChatStore.Messages(luis)[0].Deleted, "borrado para todos en una conversación directa");
            ChatStore.AppendIncoming(luis, "Luis", "FILE\tC:\\secreto.txt");
            ChatStore.AppendIncoming(luis, "Luis", "DELETED");
            var neutral = ChatStore.Messages(luis);
            Check(neutral.Skip(1).All(l => l.FilePath == null && !l.Deleted), "un mensaje que imita las marcas internas se neutraliza");

            // Salir del grupo
            ChatStore.Remove(group.Id);
            ChatGroups.Remove(group.Id);
            Check(ChatGroups.Get(group.Id) == null && !ChatStore.Threads().Any(t => t.Id == group.Id), "salir del grupo lo quita");

            // Cola de salida
            ChatOutbox.Add(ana, NetworkEngine.ChatKindGroupMessage, "g\nid\ntexto ñ");
            ChatOutbox.Add(luis, NetworkEngine.ChatKindDelete, "x");
            var queued = ChatOutbox.For(ana);
            Check(queued.Count == 1 && queued[0].Body == "g\nid\ntexto ñ" && ChatOutbox.Pending(luis) == 1, "la cola guarda lo no entregado por persona");
            ChatOutbox.Done(queued[0]);
            Check(ChatOutbox.Pending(ana) == 0 && ChatOutbox.Pending(luis) == 1, "lo entregado sale de la cola");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        Console.WriteLine(_ok ? "OK" : "FALLÓ");
        return _ok ? 0 : 1;
    }
}
