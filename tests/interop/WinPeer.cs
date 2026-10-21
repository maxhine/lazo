using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Lazo;

internal static class WinPeer
{
    private static int Main(string[] a)
    {
        Console.Out.Flush();
        if (a[0] == "vectors") return Vectors(a);
        if (a[0] == "remote") return RemoteViewer(a);
        if (a[0] == "serve")
        {
            NetworkEngine engine = new NetworkEngine(a[2], int.Parse(a[3]), int.Parse(a[4]), a[1]);
            engine.PairingPrompt += r => { Console.WriteLine("CODIGO " + r.Code); return Task.FromResult(true); };
            engine.OfferReceived += o => { Console.WriteLine("OFERTA " + o.FileName + " " + o.Size + " de " + o.PeerId); return Task.FromResult(true); };
            engine.ReceiveFinished += (id, m, p) => Console.WriteLine("RECIBIDO " + m + " " + p);
            engine.ChatReceived += (id, n, t) => Console.WriteLine("CHAT " + id + " " + n + ": " + t);
            string secret = Environment.GetEnvironmentVariable("LAZO_REMOTE_PASSWORD");
            if (!string.IsNullOrEmpty(secret)) engine.Remote.SetPassword(secret, true);
            engine.RemoteAccessAccepted += (id, n, connection) =>
            {
                Console.WriteLine("REMOTO " + n);
                try { connection.Stream.WriteByte((byte)'E'); connection.Stream.Flush(); } catch { }
                connection.Dispose();
                return Task.FromResult(0);
            };
            engine.JoinRequested += (id, n) => { Console.WriteLine("UNIRSE " + n); return true; };
            engine.ShareRequested += (id, n) => { Console.WriteLine("COMPARTIR " + n); return true; };
            engine.ChatSignal += (id, n, k, b) => Console.WriteLine("SENAL " + k + " " + b.Replace("\n", "/"));
            engine.Start();
            Console.WriteLine("LISTO " + engine.SelfId.ToString("D") + " " + engine.SelfFingerprint);
            System.Threading.Thread.Sleep(int.Parse(a[5]) * 1000);
            engine.Dispose();
            return 0;
        }
        using (NetworkEngine engine = new NetworkEngine(Path.GetTempPath(), 0, int.Parse(a[3]), a[1]))
        {
            engine.PairingPrompt += r => { Console.WriteLine("CODIGO " + r.Code); return Task.FromResult(true); };
            Peer peer = new Peer { Id = Guid.Parse(a[4]), Name = "Mac", Address = IPAddress.Parse(a[2]), Port = int.Parse(a[3]), Fingerprint = a[5] };
            try
            {
                engine.SendAsync(peer, a[6], null).GetAwaiter().GetResult();
                Console.WriteLine("OK    archivo enviado al Mac");
                engine.SendChatAsync(peer, a[7]).GetAwaiter().GetResult();
                Console.WriteLine("OK    chat enviado al Mac");
                Console.WriteLine(engine.RequestJoinAsync(peer).GetAwaiter().GetResult() ? "OK    Windows → Mac: petición de unirse aceptada" : "FALLO petición de unirse");
                Console.WriteLine(engine.RequestShareAsync(peer).GetAwaiter().GetResult() ? "OK    Windows → Mac: petición de compartir aceptada" : "FALLO petición de compartir");
                engine.SendReliableSignalAsync(peer, NetworkEngine.ChatKindDelete, "a1b2c3d4").GetAwaiter().GetResult();
                Console.WriteLine("OK    Windows → Mac: borrado entregado");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("FALLO " + ex.Message); return 1; }
        }
    }

    /// Visor remoto con contraseña contra el Mac: remote <perfil> <ip> <tcp> <id> <huella> <contraseña> [falla]
    private static int RemoteViewer(string[] a)
    {
        using (NetworkEngine engine = new NetworkEngine(Path.GetTempPath(), 0, int.Parse(a[3]), a[1]))
        {
            Peer peer = new Peer { Id = Guid.Parse(a[4]), Name = "Mac", Address = IPAddress.Parse(a[2]), Port = int.Parse(a[3]), Fingerprint = a[5] };
            bool expectFailure = a.Length > 7;
            try
            {
                using (SecureConnection connection = engine.ConnectRemoteAsync(peer, a[6]).GetAwaiter().GetResult())
                {
                    int first = connection.Stream.ReadByte();
                    if (expectFailure) { Console.WriteLine("FALLO entró con una contraseña que debía fallar"); return 1; }
                    Console.WriteLine("OK    Windows → Mac: control remoto aceptado (primer paquete " + (char)first + ")");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                if (expectFailure) { Console.WriteLine("OK    Windows → Mac: contraseña incorrecta rechazada (" + ex.Message + ")"); return 0; }
                Console.WriteLine("FALLO control remoto: " + ex.Message);
                return 1;
            }
        }
    }

    private static string B64(string text) { return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)); }
    private static string Text(string b64) { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }

    private static ChatGroup SampleGroup()
    {
        ChatGroup group = new ChatGroup { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "Equipo ñ", Owner = Guid.Parse("22222222-2222-2222-2222-222222222222") };
        group.Members.Add(new GroupMember { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Name = "Ana" });
        group.Members.Add(new GroupMember { Id = Guid.Parse("44444444-4444-4444-4444-444444444444"), Name = "Luis" });
        return group;
    }

    /// Formatos del chat de 0.5.3 (identificador escondido, lista de grupo): sin argumentos los imprime; con los del Mac los comprueba.
    private static int Vectors(string[] a)
    {
        if (a.Length == 1)
        {
            Console.WriteLine("WIRE " + B64(WireId.Wrap("a1b2c3d4", "hola ñ ✓")));
            Console.WriteLine("GROUP " + B64(ChatGroups.Pack(SampleGroup(), Guid.Parse("55555555-5555-5555-5555-555555555555"), "Jhon")));
            return 0;
        }
        string wire = Text(a[1]);
        string id = WireId.Unwrap(ref wire);
        bool wireOk = id == "a1b2c3d4" && wire == "hola ñ ✓";
        ChatGroup group = ChatGroups.Unpack(Text(a[2]), Guid.Parse("33333333-3333-3333-3333-333333333333"));
        bool groupOk = group != null && group.Name == "Equipo ñ" && group.Owner == Guid.Parse("22222222-2222-2222-2222-222222222222") &&
            group.Members.Count == 2 && group.Members[0].Id == Guid.Parse("55555555-5555-5555-5555-555555555555") && group.Members[0].Name == "Jhon" &&
            group.Members[1].Name == "Luis";
        Console.WriteLine((wireOk ? "OK    " : "FALLO ") + "Mac → Windows: identificador de mensaje");
        Console.WriteLine((groupOk ? "OK    " : "FALLO ") + "Mac → Windows: lista de grupo");
        return wireOk && groupOk ? 0 : 1;
    }
}
