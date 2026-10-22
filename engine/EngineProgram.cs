using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Lazo
{
    /// <summary>
    /// Lazo.Engine.exe: el motor de Lazo sin interfaz, para la isla.
    ///   --info &lt;archivo&gt;      dónde escribir {version, port, token, pid} (por defecto %APPDATA%\Lazo\engine.json)
    ///   --port &lt;n&gt;            puerto del WebSocket local (por defecto el primero libre desde 48360)
    ///   --profile &lt;carpeta&gt;   carpeta de identidad y datos (por defecto %APPDATA%\Lazo)
    ///   --receive &lt;carpeta&gt;   carpeta de recepción
    ///   --ports &lt;d&gt;,&lt;t&gt;       puertos de descubrimiento UDP y transferencia TCP (pruebas)
    ///   --allow-parallel       no exige ser la única copia de Lazo (pruebas)
    ///   --exit-on-stdin-close  termina cuando quien lo lanzó cierra su entrada
    ///   --tools-only           solo las utilidades (Xpace, nubes de puntos…), sin abrir la red
    /// </summary>
    internal static class EngineProgram
    {
        private static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static int Main(string[] args)
        {
            bool parallel = Array.IndexOf(args, "--allow-parallel") >= 0 || Array.IndexOf(args, "--tools-only") >= 0;
            bool first;
            // Mismo cerrojo que el Lazo clásico: los dos usan los mismos puertos, solo uno a la vez.
            string name = parallel ? "Local\\Lazo.Engine.Test." + Guid.NewGuid().ToString("N") : "Local\\Lazo.Transfer.App";
            using (Mutex instance = new Mutex(true, name, out first))
            {
                if (!first)
                {
                    Console.Error.WriteLine("Lazo ya está abierto (clásico o isla).");
                    return 2;
                }
                try { return Run(args); }
                catch (Exception e)
                {
                    Console.Error.WriteLine("No se pudo iniciar el motor: " + e.Message);
                    return 1;
                }
            }
        }

        private static int Run(string[] args)
        {
            string profile = Arg(args, "--profile");
            string receive = Arg(args, "--receive");
            string ports = Arg(args, "--ports");
            int discovery = NetworkEngine.DiscoveryPort, transfer = NetworkEngine.TransferPort;
            if (ports != null)
            {
                string[] p = ports.Split(',');
                if (p.Length != 2 || !int.TryParse(p[0], out discovery) || !int.TryParse(p[1], out transfer))
                    throw new ArgumentException("--ports espera «descubrimiento,transferencia».");
            }
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string info = Arg(args, "--info") ?? Path.Combine(profile ?? Path.Combine(appData, "Lazo"), "engine.json");

            Identity.Load();
            ProfilePhoto.Load();
            bool toolsOnly = Array.IndexOf(args, "--tools-only") >= 0;
            NetworkEngine net = toolsOnly ? null : new NetworkEngine(receive, discovery, transfer, profile);
            try
            {
                if (net != null) net.Start();
                string token = NewToken();
                int requested;
                string portText = Arg(args, "--port");
                int start = portText != null && int.TryParse(portText, out requested) ? requested : 48360;
                EngineHost host = null;
                for (int port = start; port < start + 40 && host == null; port++)
                {
                    EngineHost candidate = new EngineHost(net, port, token) { TestMode = Array.IndexOf(args, "--allow-parallel") >= 0 };
                    try { candidate.Start(); host = candidate; }
                    catch (HttpListenerException) { candidate.Dispose(); if (portText != null) throw; }
                }
                if (host == null) throw new InvalidOperationException("No hay un puerto local libre para el motor.");

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(info)));
                File.WriteAllText(info, new JavaScriptSerializer().Serialize(new Dictionary<string, object>
                {
                    { "version", EngineHost.ProtocolVersion }, { "port", host.Port }, { "token", token },
                    { "pid", System.Diagnostics.Process.GetCurrentProcess().Id }
                }));
                Console.WriteLine("READY " + host.Port);

                ManualResetEvent done = new ManualResetEvent(false);
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; done.Set(); };
                if (Array.IndexOf(args, "--exit-on-stdin-close") >= 0)
                {
                    Thread watcher = new Thread(() =>
                    {
                        try { while (Console.In.ReadLine() != null) { } } catch { }
                        done.Set();
                    });
                    watcher.IsBackground = true;
                    watcher.Start();
                }
                done.WaitOne();
                host.Dispose();
                try { File.Delete(info); } catch { }
            }
            finally
            {
                if (net != null) net.Dispose();
            }
            return 0;
        }

        private static string NewToken()
        {
            byte[] bytes = new byte[24];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            StringBuilder hex = new StringBuilder();
            foreach (byte b in bytes) hex.Append(b.ToString("x2"));
            return hex.ToString();
        }
    }
}
