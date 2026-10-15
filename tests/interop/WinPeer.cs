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
        if (a[0] == "serve")
        {
            NetworkEngine engine = new NetworkEngine(a[2], int.Parse(a[3]), int.Parse(a[4]), a[1]);
            engine.PairingPrompt += r => { Console.WriteLine("CODIGO " + r.Code); return Task.FromResult(true); };
            engine.OfferReceived += o => { Console.WriteLine("OFERTA " + o.FileName + " " + o.Size + " de " + o.PeerId); return Task.FromResult(true); };
            engine.ReceiveFinished += (id, m, p) => Console.WriteLine("RECIBIDO " + m + " " + p);
            engine.ChatReceived += (id, n, t) => Console.WriteLine("CHAT " + id + " " + n + ": " + t);
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
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("FALLO " + ex.Message); return 1; }
        }
    }
}
