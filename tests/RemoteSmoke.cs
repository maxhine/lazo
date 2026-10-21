using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Lazo;

/// <summary>
/// Control remoto con contraseña y ajuste de resolución (docs/control-remoto.md), sin interfaz: derivación y prueba de la
/// contraseña, almacén y bloqueo, elección del modo de pantalla y el paquete 'Z', y el reto completo entre dos motores de red
/// en el mismo equipo (contraseña correcta, errónea, desactivado, ocupado y bloqueo tras cinco fallos).
/// </summary>
internal static class RemoteSmoke
{
    private static int _failed;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "OK    " : "FALLO ") + what);
        if (!ok) _failed++;
    }

    private static string Hex(byte[] bytes) { return string.Concat(bytes.Select(b => b.ToString("x2"))); }

    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "lazo-remote-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(root);
        try
        {
            Derivation();
            Store(root);
            Lockout(root);
            Picker();
            Packet();
            if (args.Length > 0 && args[0] == "--sin-red") Console.WriteLine("(se omite la prueba de red)");
            else Network(root).GetAwaiter().GetResult();
        }
        catch (Exception ex) { Console.WriteLine("FALLO excepción: " + ex); _failed++; }
        finally { try { Directory.Delete(root, true); } catch { } }
        Console.WriteLine(_failed == 0 ? "Todo correcto." : _failed + " comprobaciones fallaron.");
        return _failed == 0 ? 0 : 1;
    }

    // ── PBKDF2 y prueba ─────────────────────────────────────────────────

    private static void Derivation()
    {
        byte[] salt = Encoding.ASCII.GetBytes("salt");
        Check(Hex(RemoteAccess.DeriveKey("password", salt, 1)) == "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b", "PBKDF2-HMAC-SHA256: vector con 1 iteración");
        Check(Hex(RemoteAccess.DeriveKey("password", salt, 2)) == "ae4d0c95af6b46d32d0adff928f06dd02a303f8ef3c251dfd6e2d85a95474c43", "PBKDF2-HMAC-SHA256: vector con 2 iteraciones");
        Check(Hex(RemoteAccess.DeriveKey("password", salt, 4096)) == "c5e478d59288c841aa530db6845c4c8d962893a001ce4e11a4963873aa98134a", "PBKDF2-HMAC-SHA256: vector con 4096 iteraciones");

        byte[] nonce = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        string viewer = "11111111-2222-3333-4444-555555555555", host = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        byte[] proof = RemoteAccess.ComputeProof("contraseña ñ ✓", salt, 1000, nonce, viewer, host);
        Check(proof.Length == 32, "la prueba mide 32 bytes");
        Check(Hex(proof) == Hex(RemoteAccess.ComputeProof("contraseña ñ ✓", salt, 1000, nonce, viewer.ToUpperInvariant(), host.ToUpperInvariant())), "los identificadores se normalizan a minúscula");
        Check(Hex(proof) != Hex(RemoteAccess.ComputeProof("contraseña ñ ✓", salt, 1000, nonce, host, viewer)), "la prueba está atada al orden visor|anfitrión");
        Check(Hex(proof) != Hex(RemoteAccess.ComputeProof("otra contraseña", salt, 1000, nonce, viewer, host)), "otra contraseña da otra prueba");
        byte[] nonce2 = (byte[])nonce.Clone();
        nonce2[0] ^= 1;
        Check(Hex(proof) != Hex(RemoteAccess.ComputeProof("contraseña ñ ✓", salt, 1000, nonce2, viewer, host)), "otro nonce da otra prueba");
        // Contra el HMAC que formaría cualquiera siguiendo la especificación.
        byte[] key = RemoteAccess.DeriveKey("contraseña ñ ✓", salt, 1000);
        byte[] ids = Encoding.UTF8.GetBytes(viewer + "|" + host);
        byte[] message = nonce.Concat(ids).ToArray();
        using (System.Security.Cryptography.HMACSHA256 hmac = new System.Security.Cryptography.HMACSHA256(key))
            Check(Hex(hmac.ComputeHash(message)) == Hex(proof), "prueba = HMAC-SHA256(clave, nonce ‖ visor|anfitrión)");
    }

    // ── Almacén ─────────────────────────────────────────────────────────

    private static void Store(string root)
    {
        string folder = Path.Combine(root, "almacen");
        RemoteAccess remote = new RemoteAccess(folder);
        Check(!remote.HasPassword && !remote.Enabled, "sin contraseña, el control remoto está apagado");
        bool rejected = false;
        try { remote.SetPassword("corta", true); } catch (ArgumentException) { rejected = true; }
        Check(rejected && !remote.HasPassword, "una contraseña de menos de 8 caracteres se rechaza");
        bool noPassword = false;
        try { remote.SetEnabled(true); } catch (InvalidOperationException) { noPassword = true; }
        Check(noPassword, "no se puede activar sin contraseña");

        remote.SetPassword("12345678", true);
        Check(remote.HasPassword && remote.Enabled, "contraseña de 8 caracteres: activado");
        string line = File.ReadAllText(Path.Combine(folder, "remote.txt")).Trim();
        string[] parts = line.Split('|');
        Check(parts.Length == 5 && parts[0] == "v1" && parts[1] == "150000" && parts[4] == "1" &&
            Convert.FromBase64String(parts[2]).Length == 16 && Convert.FromBase64String(parts[3]).Length == 32, "remote.txt: v1|150000|sal|hash|1");
        Check(!line.Contains("12345678"), "la contraseña no queda en el archivo");

        RemoteAccess again = new RemoteAccess(folder);
        Check(again.HasPassword && again.Enabled, "se vuelve a leer del archivo");
        RemoteChallenge challenge = again.NewChallenge();
        string viewer = Guid.NewGuid().ToString("D"), host = Guid.NewGuid().ToString("D");
        byte[] good = RemoteAccess.ComputeProof("12345678", challenge.Salt, challenge.Iterations, challenge.Nonce, viewer, host);
        Check(challenge.Salt.Length == 16 && challenge.Nonce.Length == 16 && challenge.Iterations == 150000, "el reto trae sal, iteraciones y nonce");
        Check(again.Verify(challenge, good, viewer, host), "la prueba correcta se acepta");
        byte[] bad = RemoteAccess.ComputeProof("87654321", challenge.Salt, challenge.Iterations, challenge.Nonce, viewer, host);
        Check(!again.Verify(challenge, bad, viewer, host), "la prueba con otra contraseña se rechaza");
        Check(!again.Verify(challenge, good, Guid.NewGuid().ToString("D"), host), "la prueba de otro visor no sirve");
        Check(!again.Verify(challenge, new byte[31], viewer, host), "una prueba de tamaño incorrecto se rechaza");
        Check(again.NewChallenge().Nonce.SequenceEqual(challenge.Nonce) == false, "cada reto lleva un nonce nuevo");

        again.SetEnabled(false);
        Check(!again.Enabled && again.HasPassword && !new RemoteAccess(folder).Enabled, "desactivar conserva la contraseña y se recuerda");
        again.SetPassword("otra-contraseña", false);
        Check(!again.Enabled && again.HasPassword, "cambiar la contraseña puede dejarlo apagado");
        again.Clear();
        Check(!again.HasPassword && !File.Exists(Path.Combine(folder, "remote.txt")), "quitar la contraseña borra el archivo");
    }

    // ── Bloqueo ─────────────────────────────────────────────────────────

    private static void Lockout(string root)
    {
        RemoteAccess remote = new RemoteAccess(Path.Combine(root, "bloqueo"));
        DateTime now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        remote.Clock = delegate { return now; };
        string peer = Guid.NewGuid().ToString("D"), other = Guid.NewGuid().ToString("D");
        for (int i = 0; i < 4; i++) { remote.RegisterFailure(peer); now = now.AddSeconds(30); }
        Check(!remote.IsLocked(peer), "cuatro fallos no bloquean");
        remote.RegisterFailure(peer);
        Check(remote.IsLocked(peer), "el quinto fallo en diez minutos bloquea");
        Check(!remote.IsLocked(other), "el bloqueo es solo de ese equipo");
        now = now.AddMinutes(9);
        Check(remote.IsLocked(peer), "sigue bloqueado a los 9 minutos");
        now = now.AddMinutes(2);
        Check(!remote.IsLocked(peer), "se desbloquea a los 10 minutos");
        // Fallos viejos no cuentan.
        for (int i = 0; i < 4; i++) remote.RegisterFailure(peer);
        now = now.AddMinutes(11);
        remote.RegisterFailure(peer);
        Check(!remote.IsLocked(peer), "fallos de hace más de diez minutos no suman");
        // Un acierto reinicia la cuenta.
        for (int i = 0; i < 4; i++) remote.RegisterFailure(peer);
        remote.RegisterSuccess(peer);
        remote.RegisterFailure(peer);
        Check(!remote.IsLocked(peer), "un acierto reinicia la cuenta de fallos");
    }

    // ── Modo de pantalla ────────────────────────────────────────────────

    private static List<DisplayMode> Modes()
    {
        int[][] sizes =
        {
            new[] { 800, 600 }, new[] { 1024, 768 }, new[] { 1280, 720 }, new[] { 1280, 1024 }, new[] { 1366, 768 },
            new[] { 1600, 900 }, new[] { 1680, 1050 }, new[] { 1920, 1080 }, new[] { 2560, 1440 }, new[] { 3840, 2160 }
        };
        List<DisplayMode> modes = new List<DisplayMode>();
        foreach (int[] size in sizes)
        {
            modes.Add(new DisplayMode(size[0], size[1], 60));
            modes.Add(new DisplayMode(size[0], size[1], 144));
        }
        modes.Add(new DisplayMode(640, 480, 60));
        return modes;
    }

    private static string Pick(IList<DisplayMode> modes, int w, int h, int frequency)
    {
        DisplayMode chosen;
        return DisplayModePicker.Choose(modes, w, h, frequency, out chosen) ? chosen.ToString() : "ninguno";
    }

    private static void Picker()
    {
        List<DisplayMode> modes = Modes();
        Check(Pick(modes, 1700, 950, 60) == "1600×900@60", "ventana 1700×950: el mayor modo 16:9 que cabe es 1600×900");
        Check(Pick(modes, 1920, 1080, 60) == "1920×1080@60", "ventana 1920×1080: justo ese modo");
        Check(Pick(modes, 3840, 2160, 60) == "3840×2160@60", "ventana 3840×2160: el modo nativo");
        Check(Pick(modes, 5000, 2800, 60) == "3840×2160@60", "ventana más grande que todo: el mayor modo de su proporción");
        Check(Pick(modes, 1280, 800, 60) == "1680×1050@60" || Pick(modes, 1280, 800, 60) == "1280×720@60", "ventana 16:10: respeta la proporción o la más cercana");
        Check(Pick(modes, 1700, 1050, 60) == "1680×1050@60", "ventana 1700×1050 (16:10): 1680×1050");
        Check(Pick(modes, 700, 400, 60) == "1280×720@60", "ventana más pequeña que todo: el más pequeño de su proporción, sin modos diminutos");
        Check(Pick(modes, 1000, 750, 60) == "1024×768@60" || Pick(modes, 1000, 750, 60) == "800×600@60", "ventana 4:3 pequeña: un modo 4:3");
        Check(Pick(modes, 2000, 1000, 60) == "1600×900@60", "ventana 2:1: ninguna proporción parecida, la más cercana que quepa");
        Check(Pick(modes, 1920, 1080, 144) == "1920×1080@144", "conserva el refresco actual si ese tamaño lo ofrece");
        List<DisplayMode> only = new List<DisplayMode> { new DisplayMode(1920, 1080, 75), new DisplayMode(1920, 1080, 60) };
        Check(Pick(only, 1920, 1080, 144) == "1920×1080@75", "si no ofrece el refresco actual, el más alto");
        Check(Pick(new List<DisplayMode>(), 1920, 1080, 60) == "ninguno", "sin modos no hay elección");
        Check(Pick(modes, 0, 0, 60) == "ninguno", "un tamaño pedido de cero no elige nada");
    }

    private static void Packet()
    {
        byte[] p = ResolutionRequest.Packet(1920, 1080);
        Check(p.Length == 5 && p[0] == (byte)'Z' && p[1] == 0x80 && p[2] == 0x07 && p[3] == 0x38 && p[4] == 0x04, "'Z' 1920×1080 = 5A 80 07 38 04");
        byte[] zero = ResolutionRequest.Packet(0, 0);
        Check(zero.SequenceEqual(new byte[] { (byte)'Z', 0, 0, 0, 0 }), "'Z' 0 0 restaura");
        byte[] clamp = ResolutionRequest.Packet(70000, -5);
        Check(clamp.SequenceEqual(new byte[] { (byte)'Z', 0xFF, 0xFF, 0, 0 }), "los valores fuera de rango se recortan");
    }

    // ── Red: reto completo entre dos motores ─────────────────────────────

    private static async Task Network(string root)
    {
        IPAddress local = LocalAddress();
        using (NetworkEngine host = new NetworkEngine(Path.Combine(root, "host-in"), FreeUdp(), FreePort(), Path.Combine(root, "host")))
        using (NetworkEngine viewer = new NetworkEngine(Path.Combine(root, "viewer-in"), FreeUdp(), FreePort(), Path.Combine(root, "viewer")))
        {
            string acceptedName = null;
            Guid acceptedId = Guid.Empty;
            TaskCompletionSource<bool> done = new TaskCompletionSource<bool>();
            TaskCompletionSource<bool> arrived = new TaskCompletionSource<bool>();
            host.RemoteAccessAccepted += (id, name, connection) =>
            {
                acceptedId = id;
                acceptedName = name;
                // A partir de aquí la conexión es la transmisión: el anfitrión escribe y el visor lee.
                connection.Stream.WriteByte((byte)'K');
                connection.Stream.Flush();
                arrived.TrySetResult(true);
                return done.Task;
            };
            host.Start();
            Peer peer = new Peer { Id = host.SelfId, Name = "Anfitrión", Address = local, Port = PortOf(host), Fingerprint = host.SelfFingerprint };

            // Desactivado.
            string message = await Fails(viewer, peer, "12345678");
            Check(message != null && message.Contains("desactivado"), "con el control remoto desactivado se rechaza: " + message);

            host.Remote.SetPassword("la-contraseña-buena", true);
            // Contraseña errónea.
            message = await Fails(viewer, peer, "la-contraseña-mala");
            Check(message != null && message.Contains("incorrecta"), "contraseña errónea: " + message);
            Check(acceptedName == null, "una contraseña errónea no arranca ninguna sesión");

            // Ocupado.
            host.RemoteBusy = () => true;
            message = await Fails(viewer, peer, "la-contraseña-buena");
            Check(message != null && message.Contains("ocupado"), "con otra sesión en curso se rechaza: " + message);
            host.RemoteBusy = null;

            // Correcta.
            SecureConnection connection2 = await viewer.ConnectRemoteAsync(peer, "la-contraseña-buena");
            using (connection2)
            {
                Task finished = await Task.WhenAny(arrived.Task, Task.Delay(5000));
                Check(finished == arrived.Task && acceptedId == viewer.SelfId, "la contraseña correcta entrega la conexión al anfitrión con el id del visor");
                Check(acceptedName != null && acceptedName.Length > 0, "el anfitrión recibe el nombre del visor");
                connection2.Client.ReceiveTimeout = 5000;
                Check(connection2.Stream.ReadByte() == 'K', "la conexión pasa a ser la transmisión (el visor lee lo que escribe el anfitrión)");
                done.TrySetResult(true);
            }

            // Bloqueo tras cinco fallos seguidos (cada uno tarda un segundo).
            for (int i = 0; i < 5; i++) await Fails(viewer, peer, "mala-" + i + "-contraseña");
            Check(host.Remote.IsLocked(viewer.SelfId.ToString("D")), "el anfitrión anotó el bloqueo de ese equipo");
            message = await Fails(viewer, peer, "la-contraseña-buena");
            Check(message != null, "tras cinco fallos el equipo queda bloqueado, aunque la contraseña ya sea la buena: " + message);
        }
    }

    private static int PortOf(NetworkEngine engine)
    {
        System.Reflection.FieldInfo field = typeof(NetworkEngine).GetField("_transferPort", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (int)field.GetValue(engine);
    }

    /// <summary>Intenta entrar; devuelve el mensaje del error (o null si entró, en cuyo caso se cierra).</summary>
    private static async Task<string> Fails(NetworkEngine viewer, Peer peer, string password)
    {
        try
        {
            SecureConnection connection = await viewer.ConnectRemoteAsync(peer, password);
            connection.Dispose();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static IPAddress LocalAddress()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address)
            .First(a => { byte[] b = a.GetAddressBytes(); return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31; });
    }

    private static int FreePort()
    {
        TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static int FreeUdp()
    {
        using (UdpClient probe = new UdpClient(0)) return ((IPEndPoint)probe.Client.LocalEndPoint).Port;
    }
}
