using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Lazo;

/// <summary>
/// Prueba de red de extremo a extremo con el protocolo seguro LAZO3: descubrimiento, rechazo de conexiones
/// sin cifrar, nada se envía sin emparejar, emparejamiento con código, transferencia con SHA-256 y chat.
/// Dos motores en el mismo equipo, cada uno con su propia identidad.
/// </summary>
internal static class NetworkSmoke
{
    private static int Main(string[] args)
    {
        try { Run(args.Length > 0 ? args[0] : "network-smoke").GetAwaiter().GetResult(); return 0; }
        catch (Exception ex) { Console.WriteLine("FALLO: " + ex.Message); return 1; }
    }

    private static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        string input = Path.Combine(directory, "origen.bin");
        byte[] data = new byte[3 * 1024 * 1024 + 57];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(input, data);
        IPAddress local = LocalAddress();
        int receiverPort = FreePort(), receiverUdp = FreeUdp();

        using (NetworkEngine receiver = new NetworkEngine(Path.Combine(directory, "recibidos"), receiverUdp, receiverPort, Path.Combine(directory, "perfil-receptor")))
        using (NetworkEngine sender = new NetworkEngine(Path.Combine(directory, "recibidos-emisor"), FreeUdp(), FreePort(), Path.Combine(directory, "perfil-emisor")))
        {
            Check(receiver.SelfFingerprint != sender.SelfFingerprint && receiver.SelfFingerprint.Length == 64, "cada equipo tiene su propia clave");
            TaskCompletionSource<string> finished = new TaskCompletionSource<string>();
            string receivedPath = null;
            Guid offerPeer = Guid.Empty;
            receiver.OfferReceived += offer => { offerPeer = offer.PeerId; return Task.FromResult(true); };
            receiver.ReceiveFinished += (id, message, path) => { receivedPath = path; finished.TrySetResult(message); };
            string chat = null;
            Guid chatFrom = Guid.Empty;
            receiver.ChatReceived += (id, name, text) => { chatFrom = id; chat = text; };
            receiver.Start();

            // Descubrimiento: anuncio LAZO3 con huella.
            Guid remoteId = Guid.NewGuid();
            TaskCompletionSource<bool> appeared = new TaskCompletionSource<bool>();
            TaskCompletionSource<bool> photo = new TaskCompletionSource<bool>();
            receiver.PeersChanged += peers =>
            {
                Peer peer = peers.FirstOrDefault(item => item.Id == remoteId);
                if (peer == null) return;
                if (peer.Fingerprint == new string('a', 64) && !peer.Paired) appeared.TrySetResult(true);
                if (peer.Photo == "dGVzdA==") photo.TrySetResult(true);
            };
            using (UdpClient announcer = new UdpClient(new IPEndPoint(local, 0)))
            {
                Action<string> announce = message =>
                {
                    byte[] packet = Encoding.UTF8.GetBytes(message);
                    announcer.Send(packet, packet.Length, new IPEndPoint(local, receiverUdp));
                };
                announce("LAZO1|" + Guid.NewGuid() + "|Antiguo|" + receiverPort);
                announce("LAZO3|" + remoteId + "|Foto prueba|" + receiverPort + "|" + new string('a', 64));
                await Expect(appeared.Task, 3000, "descubrimiento LAZO3 con huella");
                announce("LAZO3A|" + remoteId + "|dGVzdA==");
                await Expect(photo.Task, 3000, "foto por UDP");
            }
            Check(!receiver.Snapshot().Any(p => p.Name == "Antiguo"), "los anuncios de 0.4.x se ignoran");

            // Una conexión sin cifrar (protocolo anterior) se cierra sin respuesta.
            using (TcpClient legacy = new TcpClient())
            {
                await legacy.ConnectAsync(local, receiverPort);
                legacy.ReceiveTimeout = 3000;
                NetworkStream stream = legacy.GetStream();
                byte[] hello = Encoding.ASCII.GetBytes("LAZOC");
                stream.Write(hello, 0, hello.Length);
                int answer;
                try { answer = stream.ReadByte(); } catch (IOException) { answer = -1; }
                Check(answer == -1, "una conexión sin cifrar se rechaza");
            }

            Peer target = new Peer { Id = receiver.SelfId, Name = "Receptor", Address = local, Port = receiverPort, Fingerprint = receiver.SelfFingerprint };

            // Alguien se adelanta con el identificador de un compañero que se anuncia con otra clave: se rechaza y no se recuerda.
            string victimProfile = Path.Combine(directory, "perfil-adelantado");
            Directory.CreateDirectory(victimProfile);
            File.WriteAllText(Path.Combine(victimProfile, "device.id"), remoteId.ToString("D"));
            using (NetworkEngine early = new NetworkEngine(Path.Combine(directory, "recibidos-adelantado"), FreeUdp(), FreePort(), victimProfile))
            {
                bool refused = false;
                try { await early.SendChatAsync(target, "me hago pasar por otro"); }
                catch (Exception) { refused = true; }
                Check(refused && chat == null && receiver.Pairings.Get(remoteId.ToString("D")) == null,
                    "quien usa el identificador de un compañero anunciado con otra clave es rechazado");
            }

            // El emisor se anuncia, como hace cada 2,5 s: así el receptor puede confirmar su clave al primer contacto.
            using (UdpClient announcer = new UdpClient(new IPEndPoint(local, 0)))
            {
                TaskCompletionSource<bool> seen = new TaskCompletionSource<bool>();
                receiver.PeersChanged += peers => { if (peers.Any(p => p.Id == sender.SelfId)) seen.TrySetResult(true); };
                byte[] packet = Encoding.UTF8.GetBytes("LAZO3|" + sender.SelfId + "|Emisor|" + receiverPort + "|" + sender.SelfFingerprint);
                announcer.Send(packet, packet.Length, new IPEndPoint(local, receiverUdp));
                await Expect(seen.Task, 3000, "el receptor ve el anuncio del emisor");
            }

            // Primer contacto: se recuerdan las claves sin pedir ningún código.
            int prompts = 0;
            bool acceptCode = false;
            string codeSeenBySender = null, codeSeenByReceiver = null;
            sender.PairingPrompt += request => { prompts++; codeSeenBySender = request.Code; return Task.FromResult(acceptCode); };
            receiver.PairingPrompt += request => { codeSeenByReceiver = request.Code; return Task.FromResult(true); };
            await sender.SendAsync(target, input, null);
            await Expect(finished.Task, 15000, "transferencia al primer contacto");
            if (finished.Task.Result.StartsWith("Error:")) throw new Exception(finished.Task.Result);
            Check(prompts == 0, "no se pide ningún código para enviar");
            Check(receiver.Pairings.Get(sender.SelfId.ToString("D")) != null && sender.Pairings.Get(receiver.SelfId.ToString("D")) != null, "los dos recuerdan la clave del otro");
            Check(offerPeer == sender.SelfId, "la oferta llega con la identidad comprobada de quien envía");
            string received = Path.Combine(directory, "recibidos", "origen.bin");
            Check(receivedPath == received && File.ReadAllBytes(received).SequenceEqual(data), "archivo cifrado recibido íntegro (" + data.Length + " bytes)");

            await sender.SendChatAsync(target, "hola por canal cifrado");
            await Task.Delay(300);
            Check(chat == "hola por canal cifrado" && chatFrom == sender.SelfId, "chat con la identidad real");

            // Verificación opcional: si alguien cancela no queda verificado; si los dos confirman, sí.
            Check(!await sender.VerifyAsync(target) && !sender.Pairings.Get(receiver.SelfId.ToString("D")).Verified, "verificación cancelada no marca nada");
            acceptCode = true;
            Check(await sender.VerifyAsync(target), "verificación confirmada por los dos");
            Check(codeSeenBySender != null && codeSeenBySender == codeSeenByReceiver && codeSeenBySender.Length == 6, "los dos equipos muestran el mismo código (" + codeSeenBySender + ")");
            await Task.Delay(200);
            Check(sender.Pairings.Get(receiver.SelfId.ToString("D")).Verified && receiver.Pairings.Get(sender.SelfId.ToString("D")).Verified, "los dos lo marcan como verificado");

            // Otro equipo que dice ser el emisor (mismo id, otra clave) es rechazado.
            string clone = Path.Combine(directory, "perfil-clon");
            Directory.CreateDirectory(clone);
            File.Copy(Path.Combine(directory, "perfil-emisor", "device.id"), Path.Combine(clone, "device.id"));
            using (NetworkEngine impostor = new NetworkEngine(Path.Combine(directory, "recibidos-clon"), FreeUdp(), FreePort(), clone))
            {
                impostor.PairingPrompt += request => Task.FromResult(true);
                bool blocked = false;
                try { await impostor.SendChatAsync(target, "suplantación"); }
                catch (Exception) { blocked = true; }
                Check(blocked && chat == "hola por canal cifrado", "un equipo que suplanta el identificador es rechazado");
            }
        }
        Console.WriteLine("OK: protocolo seguro de extremo a extremo");
    }

    private static void Check(bool condition, string what)
    {
        Console.WriteLine((condition ? "OK    " : "FALLO ") + what);
        if (!condition) throw new Exception(what);
    }

    private static async Task Expect(Task task, int milliseconds, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(milliseconds)) != task) throw new Exception("Tiempo agotado: " + what);
        Console.WriteLine("OK    " + what);
    }

    internal static IPAddress LocalAddress()
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
