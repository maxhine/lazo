using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Lazo
{
    /// <summary>
    /// Motor de Lazo sin ventanas: la misma red, cifrado y transferencia del Lazo clásico (los
    /// archivos de ../lazo/src se compilan tal cual), expuestos a la isla por un WebSocket local
    /// con mensajes JSON (docs/engine-protocol.md). Solo escucha en 127.0.0.1 y exige un token.
    /// </summary>
    internal sealed partial class EngineHost : IDisposable
    {
        public const int ProtocolVersion = 1;
        private static readonly string[] AllowedOrigins =
        {
            "tauri://localhost", "http://tauri.localhost", "https://tauri.localhost",
            "http://127.0.0.1:1420", "http://localhost:1420"
        };
        private const int OfferTimeoutSeconds = 60;

        private sealed class Client
        {
            public WebSocket Socket;
            public readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);
        }

        private readonly NetworkEngine _net;
        private readonly string _token;
        private readonly HttpListener _http;
        private readonly int _port;
        private readonly object _lock = new object();
        private readonly List<Client> _clients = new List<Client>();
        private readonly Dictionary<Guid, TaskCompletionSource<bool>> _offers = new Dictionary<Guid, TaskCompletionSource<bool>>();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Dictionary<string, DateTime> _progressSent = new Dictionary<string, DateTime>();

        /// <summary>Solo con --allow-parallel: habilita la orden addPeer, para probar dos motores en un mismo equipo.</summary>
        public bool TestMode;
        private readonly Dictionary<Guid, Peer> _testPeers = new Dictionary<Guid, Peer>();

        public int Port { get { return _port; } }
        public string Token { get { return _token; } }

        public EngineHost(NetworkEngine net, int port, string token)
        {
            _net = net;
            _port = port;
            _token = token;
            _http = new HttpListener();
            _http.Prefixes.Add("http://127.0.0.1:" + port + "/");
        }

        public void Start()
        {
            if (_net != null)
            {
                _net.PeersChanged += OnPeersChanged;
                _net.OfferReceived += OnOfferReceived;
                _net.ReceiveProgress += OnReceiveProgress;
                _net.ReceiveFinished += OnReceiveFinished;
                _net.ChatReceived += OnChatReceived;
            }
            _http.Start();
            Task.Run((Func<Task>)AcceptLoop);
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _http.Close(); } catch { }
            lock (_lock)
            {
                foreach (TaskCompletionSource<bool> offer in _offers.Values) offer.TrySetResult(false);
                _offers.Clear();
            }
        }

        // ── Conexiones ──────────────────────────────────────────────────────────

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _http.GetContextAsync(); }
                catch { return; }
                Task.Run(() => Handle(context));
            }
        }

        private async Task Handle(HttpListenerContext context)
        {
            try
            {
                string origin = context.Request.Headers["Origin"];
                bool originOk = string.IsNullOrEmpty(origin) || AllowedOrigins.Contains(origin);
                bool tokenOk = TokenMatches(context.Request.QueryString["t"]);
                if (context.Request.Url.AbsolutePath != "/ws" || !context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }
                if (!originOk || !tokenOk)
                {
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }
                HttpListenerWebSocketContext accepted = await context.AcceptWebSocketAsync(null);
                Client client = new Client { Socket = accepted.WebSocket };
                lock (_lock) _clients.Add(client);
                try
                {
                    await Send(client, Hello());
                    if (_net != null) await Send(client, PeersMessage(_net.Snapshot()));
                    await ReadLoop(client);
                }
                finally
                {
                    lock (_lock) _clients.Remove(client);
                    try { client.Socket.Dispose(); } catch { }
                }
            }
            catch { }
        }

        private bool TokenMatches(string given)
        {
            if (string.IsNullOrEmpty(given) || given.Length != _token.Length) return false;
            int diff = 0;
            for (int i = 0; i < given.Length; i++) diff |= given[i] ^ _token[i];
            return diff == 0;
        }

        private async Task ReadLoop(Client client)
        {
            byte[] buffer = new byte[16 * 1024];
            while (client.Socket.State == WebSocketState.Open && !_stop.IsCancellationRequested)
            {
                StringBuilder text = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await client.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), _stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (text.Length > 256 * 1024) return;
                } while (!result.EndOfMessage);
                Dictionary<string, object> command;
                try { command = _json.Deserialize<Dictionary<string, object>>(text.ToString()); }
                catch { continue; }
                if (command == null) continue;
                Task.Run(() => Dispatch(client, command));
            }
        }

        // ── Órdenes de la isla ──────────────────────────────────────────────────

        private static string Str(Dictionary<string, object> m, string key)
        {
            object value;
            return m.TryGetValue(key, out value) && value != null ? value.ToString() : "";
        }

        private Peer FindPeer(string id)
        {
            Guid guid;
            if (!Guid.TryParse(id, out guid)) return null;
            Peer test;
            lock (_lock) if (_testPeers.TryGetValue(guid, out test)) return test;
            return _net.Snapshot().FirstOrDefault(p => p.Id == guid);
        }

        private async Task Dispatch(Client client, Dictionary<string, object> m)
        {
            string cmd = Str(m, "cmd");
            try
            {
                // «herramienta.orden»: las utilidades del Lazo clásico (Xpace, nubes de puntos…), sin red.
                if (cmd.IndexOf('.') > 0)
                {
                    await DispatchTool(client, cmd, m);
                    return;
                }
                if (_net == null && cmd != "ping" && cmd != "state")
                {
                    await Send(client, new Dictionary<string, object> { { "type", "error" }, { "message", "El motor de red no está en este modo." } });
                    return;
                }
                switch (cmd)
                {
                    case "ping":
                        await Send(client, new Dictionary<string, object> { { "type", "pong" } });
                        break;
                    case "state":
                        await Send(client, Hello());
                        if (_net != null) await Send(client, PeersMessage(_net.Snapshot()));
                        break;
                    case "addPeer":
                    {
                        // La difusión por UDP no distingue dos copias en un mismo equipo (comparten el puerto de
                        // transferencia que el anuncio exige), así que las pruebas registran al otro motor a mano.
                        Guid id;
                        int port;
                        IPAddress address;
                        if (!TestMode || !Guid.TryParse(Str(m, "id"), out id) || !int.TryParse(Str(m, "port"), out port) ||
                            !IPAddress.TryParse(Str(m, "address"), out address))
                        {
                            await Send(client, new Dictionary<string, object> { { "type", "error" }, { "message", "addPeer no disponible." } });
                            break;
                        }
                        lock (_lock)
                            _testPeers[id] = new Peer { Id = id, Name = Str(m, "name"), Address = address, Port = port, Fingerprint = Str(m, "fingerprint") };
                        await Send(client, new Dictionary<string, object> { { "type", "peerAdded" }, { "id", id.ToString("D") } });
                        break;
                    }
                    case "photo":
                    {
                        Peer peer = FindPeer(Str(m, "peerId"));
                        await Send(client, new Dictionary<string, object>
                        {
                            { "type", "photo" }, { "peerId", Str(m, "peerId") }, { "data", peer == null ? "" : peer.Photo }
                        });
                        break;
                    }
                    case "offerDecision":
                    {
                        Guid id;
                        TaskCompletionSource<bool> offer;
                        bool accept = Str(m, "accept") == "True";
                        lock (_lock)
                        {
                            if (!Guid.TryParse(Str(m, "id"), out id) || !_offers.TryGetValue(id, out offer)) return;
                            _offers.Remove(id);
                        }
                        offer.TrySetResult(accept);
                        break;
                    }
                    case "chat":
                    {
                        Peer peer = FindPeer(Str(m, "peerId"));
                        bool ok = false;
                        string message = "";
                        if (peer == null) message = "Esa persona ya no está disponible.";
                        else
                        {
                            try { await _net.SendChatAsync(peer, Str(m, "text")); ok = true; }
                            catch (Exception e) { message = e.Message; }
                        }
                        await Broadcast(new Dictionary<string, object>
                        {
                            { "type", "chatSent" }, { "peerId", Str(m, "peerId") }, { "ok", ok }, { "message", message }
                        });
                        break;
                    }
                    case "send":
                        await SendFile(Str(m, "token"), Str(m, "peerId"), Str(m, "path"));
                        break;
                    default:
                        await Send(client, new Dictionary<string, object>
                        {
                            { "type", "error" }, { "message", "Orden desconocida: " + cmd }
                        });
                        break;
                }
            }
            catch (Exception e)
            {
                DiagLog.Write("engine: " + e);
            }
        }

        private async Task SendFile(string token, string peerId, string path)
        {
            Peer peer = FindPeer(peerId);
            bool ok = false;
            string message = "";
            if (peer == null) message = "Esa persona ya no está disponible.";
            else if (string.IsNullOrEmpty(path) || !File.Exists(path)) message = "No se encontró el archivo.";
            else
            {
                try
                {
                    await _net.SendAsync(peer, path, value => OnSendProgress(token, value));
                    ok = true;
                }
                catch (Exception e) { message = e.Message; }
            }
            lock (_lock) _progressSent.Remove(token);
            await Broadcast(new Dictionary<string, object>
            {
                { "type", "sendDone" }, { "token", token }, { "peerId", peerId }, { "ok", ok }, { "message", message }
            });
        }

        private void OnSendProgress(string token, double value)
        {
            if (!ShouldReport(token, value)) return;
            Broadcast(new Dictionary<string, object> { { "type", "sendProgress" }, { "token", token }, { "value", value } });
        }

        /// <summary>Como máximo unos 10 avisos por segundo y transferencia; el 100 % siempre pasa.</summary>
        private bool ShouldReport(string key, double value)
        {
            lock (_lock)
            {
                DateTime last;
                DateTime now = DateTime.UtcNow;
                if (value < 1 && _progressSent.TryGetValue(key, out last) && (now - last).TotalMilliseconds < 100) return false;
                _progressSent[key] = now;
                return true;
            }
        }

        // ── Eventos del motor ───────────────────────────────────────────────────

        private void OnPeersChanged(List<Peer> peers)
        {
            Broadcast(PeersMessage(peers));
        }

        private Task<bool> OnOfferReceived(Offer offer)
        {
            TaskCompletionSource<bool> response = new TaskCompletionSource<bool>();
            bool anyone;
            lock (_lock)
            {
                anyone = _clients.Count > 0;
                if (anyone) _offers[offer.Id] = response;
            }
            // Sin isla conectada nadie puede decidir: se rechaza, igual que si se pulsara «Rechazar».
            if (!anyone) return Task.FromResult(false);
            Broadcast(new Dictionary<string, object>
            {
                { "type", "offer" }, { "id", offer.Id.ToString("D") }, { "peerId", offer.PeerId.ToString("D") },
                { "sender", offer.Sender }, { "fileName", offer.FileName }, { "size", offer.Size },
                { "preview", offer.Preview == null ? "" : Convert.ToBase64String(offer.Preview) }
            });
            Task.Delay(TimeSpan.FromSeconds(OfferTimeoutSeconds)).ContinueWith(t =>
            {
                lock (_lock) _offers.Remove(offer.Id);
                response.TrySetResult(false);
            });
            return response.Task;
        }

        private void OnReceiveProgress(Guid id, double value)
        {
            string key = "r:" + id.ToString("N");
            if (!ShouldReport(key, value)) return;
            Broadcast(new Dictionary<string, object> { { "type", "receiveProgress" }, { "id", id.ToString("D") }, { "value", value } });
        }

        private void OnReceiveFinished(Guid id, string message, string path)
        {
            lock (_lock) _progressSent.Remove("r:" + id.ToString("N"));
            Broadcast(new Dictionary<string, object>
            {
                { "type", "receiveFinished" }, { "id", id.ToString("D") }, { "message", message ?? "" }, { "path", path ?? "" }
            });
        }

        private void OnChatReceived(Guid id, string name, string text)
        {
            Broadcast(new Dictionary<string, object>
            {
                { "type", "chat" }, { "peerId", id.ToString("D") }, { "name", name }, { "text", text }
            });
        }

        // ── Mensajes ────────────────────────────────────────────────────────────

        private Dictionary<string, object> Hello()
        {
            if (_net == null) return new Dictionary<string, object> { { "type", "hello" }, { "version", ProtocolVersion }, { "tools", true } };
            return new Dictionary<string, object>
            {
                { "type", "hello" }, { "version", ProtocolVersion },
                { "selfId", _net.SelfId.ToString("D") }, { "name", Identity.Current ?? "" },
                { "fingerprint", _net.SelfFingerprint }, { "receiveDir", _net.ReceiveDirectory }
            };
        }

        private static Dictionary<string, object> PeersMessage(List<Peer> peers)
        {
            return new Dictionary<string, object>
            {
                { "type", "peers" },
                { "peers", peers.Select(p => new Dictionary<string, object>
                    {
                        { "id", p.Id.ToString("D") }, { "name", p.Name }, { "address", p.Address == null ? "" : p.Address.ToString() },
                        { "paired", p.Paired }, { "verified", p.Verified }, { "identityChanged", p.IdentityChanged },
                        { "sharing", p.Sharing ?? "" }, { "hasPhoto", !string.IsNullOrEmpty(p.Photo) }
                    }).ToList() }
            };
        }

        private async Task Send(Client client, object message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(_json.Serialize(message));
            await client.SendLock.WaitAsync();
            try
            {
                if (client.Socket.State == WebSocketState.Open)
                    await client.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { }
            finally { client.SendLock.Release(); }
        }

        private Task Broadcast(object message)
        {
            List<Client> snapshot;
            lock (_lock) snapshot = _clients.ToList();
            return Task.WhenAll(snapshot.Select(c => Send(c, message)));
        }
    }
}
