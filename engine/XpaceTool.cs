using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Lazo
{
    /// <summary>
    /// Las utilidades del Lazo clásico para la isla: la misma lógica (XpaceScanner, XpaceActions…, compiladas tal cual
    /// desde ../lazo/src) con órdenes «herramienta.orden» por el mismo canal JSON. Aquí no hay interfaz: la dibuja la isla.
    /// </summary>
    internal sealed partial class EngineHost
    {
        private async Task DispatchTool(Client client, string cmd, Dictionary<string, object> m)
        {
            string tool = cmd.Substring(0, cmd.IndexOf('.'));
            switch (tool)
            {
                case "xpace": await XpaceCommand(client, cmd, m); break;
                case "cloud": await CloudCommand(client, cmd, m); break;
                case "eyecare": await EyeCareCommand(client, cmd, m); break;
                case "tps": await TpsCommand(client, cmd, m); break;
                default:
                    await Send(client, new Dictionary<string, object> { { "type", "error" }, { "message", "Utilidad desconocida: " + tool } });
                    break;
            }
        }

        private static long Number(Dictionary<string, object> m, string key, long fallback)
        {
            object value;
            long parsed;
            return m.TryGetValue(key, out value) && value != null && long.TryParse(value.ToString(), out parsed) ? parsed : fallback;
        }

        private static bool Flag(Dictionary<string, object> m, string key)
        {
            object value;
            return m.TryGetValue(key, out value) && value != null && (value.ToString() == "True" || value.ToString() == "true");
        }

        // ── Xpace: dónde se va el espacio en disco ──────────────────────────────

        private readonly object _xLock = new object();
        private XResult _xResult;
        private CancellationTokenSource _xRun;
        private bool _xRunning;
        private readonly Dictionary<int, XNode> _xById = new Dictionary<int, XNode>();
        private readonly Dictionary<XNode, int> _xIds = new Dictionary<XNode, int>();
        private int _xNext;

        private int XId(XNode node)
        {
            lock (_xLock)
            {
                int id;
                if (_xIds.TryGetValue(node, out id)) return id;
                id = _xNext++;
                _xIds[node] = id;
                _xById[id] = node;
                return id;
            }
        }

        private static string Iso(long fileTime)
        {
            try { return fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime).ToString("yyyy-MM-ddTHH:mm:ssZ") : ""; }
            catch (ArgumentOutOfRangeException) { return ""; }
        }

        private Dictionary<string, object> XNodeJson(XNode n, string rootPath)
        {
            return new Dictionary<string, object>
            {
                { "id", XId(n) }, { "name", n.Name }, { "path", n.FullPath(rootPath) }, { "size", n.Size }, { "files", n.Files },
                { "folders", n.Folders }, { "ownSize", n.OwnSize }, { "ownFiles", n.OwnFiles }, { "denied", n.Denied },
                { "modified", Iso(n.Modified) }
            };
        }

        private Task XpaceError(string message)
        {
            return Broadcast(new Dictionary<string, object> { { "type", "xpace.error" }, { "message", message } });
        }

        private async Task XpaceCommand(Client client, string cmd, Dictionary<string, object> m)
        {
            switch (cmd)
            {
                case "xpace.scan":
                    await XpaceScan(Str(m, "path"));
                    break;
                case "xpace.cancel":
                {
                    CancellationTokenSource run = _xRun;
                    if (run != null && _xRunning) run.Cancel();
                    break;
                }
                case "xpace.children":
                {
                    XResult result = _xResult;
                    XNode node;
                    lock (_xLock) if (result == null || !_xById.TryGetValue((int)Number(m, "id", 0), out node)) node = null;
                    if (node == null) { await XpaceError("Ese análisis ya no está."); break; }
                    List<XNode> ordered = node.Children.OrderByDescending(c => c.Size).ToList();
                    List<XNode> shown = ordered.Take(300).ToList();
                    await Send(client, new Dictionary<string, object>
                    {
                        { "type", "xpace.children" }, { "node", XNodeJson(node, result.Path) },
                        { "children", shown.Select(c => XNodeJson(c, result.Path)).ToList() },
                        { "more", ordered.Count - shown.Count }, { "moreSize", ordered.Skip(shown.Count).Sum(c => c.Size) }
                    });
                    break;
                }
                case "xpace.largest":
                {
                    XResult result = _xResult;
                    if (result == null) { await XpaceError("Aún no hay análisis."); break; }
                    int limit = (int)Math.Max(1, Math.Min(300, Number(m, "limit", 100)));
                    await Send(client, new Dictionary<string, object>
                    {
                        { "type", "xpace.largest" },
                        { "files", result.Largest.Take(limit).Select(f => new Dictionary<string, object>
                            {
                                { "name", f.Name }, { "folder", f.Folder.FullPath(result.Path) }, { "size", f.Size }, { "modified", Iso(f.Modified) },
                                { "path", f.Folder.FullPath(result.Path).TrimEnd('\\') + "\\" + f.Name }
                            }).ToList() }
                    });
                    break;
                }
                case "xpace.types":
                {
                    XResult result = _xResult;
                    if (result == null) { await XpaceError("Aún no hay análisis."); break; }
                    int limit = (int)Math.Max(1, Math.Min(100, Number(m, "limit", 40)));
                    await Send(client, new Dictionary<string, object>
                    {
                        { "type", "xpace.types" },
                        { "types", result.Types.Take(limit).Select(t => new Dictionary<string, object>
                            { { "extension", t.Extension }, { "size", t.Size }, { "count", t.Count } }).ToList() }
                    });
                    break;
                }
                case "xpace.tips":
                {
                    XResult scanned = _xResult;
                    List<XSuggestion> clean = await Task.Run(() =>
                    {
                        List<XSuggestion> all = new List<XSuggestion>();
                        try { all.AddRange(XpaceSuggestions.Known(XpaceEnv.Current(), XpaceSuggestions.Minimum, null, null)); } catch { }
                        try { XSuggestion bin = XpaceSuggestions.RecycleBin(null); if (bin != null) all.Add(bin); } catch { }
                        if (scanned != null)
                            try { all.AddRange(XpaceSuggestions.From(scanned, XpaceSuggestions.Minimum, DateTime.UtcNow)); } catch { }
                        return XpaceSuggestions.Clean(all);
                    });
                    await Send(client, new Dictionary<string, object>
                    {
                        { "type", "xpace.tips" },
                        { "tips", clean.Select(x => new Dictionary<string, object>
                            {
                                { "path", x.Path }, { "title", x.Title }, { "why", x.Why }, { "safe", x.Safe }, { "size", x.Size },
                                { "isFolder", x.IsFolder }, { "infoOnly", x.InfoOnly }, { "contentsOnly", x.ContentsOnly }
                            }).ToList() }
                    });
                    break;
                }
                case "xpace.remove":
                {
                    string path = Str(m, "path");
                    bool isFolder = Flag(m, "isFolder"), permanent = Flag(m, "permanent"), contentsOnly = Flag(m, "contentsOnly");
                    string problem = await Task.Run(() => XpaceActions.Remove(path, isFolder, permanent, contentsOnly));
                    bool gone = problem == null || (contentsOnly && problem.StartsWith("Se quitó lo que se pudo", StringComparison.Ordinal));
                    XResult result = _xResult;
                    if (gone && result != null && !contentsOnly) XpaceActions.Forget(result, path, isFolder);
                    await Broadcast(new Dictionary<string, object>
                    {
                        { "type", "xpace.removed" }, { "path", path }, { "gone", gone }, { "problem", problem ?? "" }
                    });
                    break;
                }
                default:
                    await Send(client, new Dictionary<string, object> { { "type", "error" }, { "message", "Orden desconocida: " + cmd } });
                    break;
            }
        }

        private async Task XpaceScan(string path)
        {
            lock (_xLock)
            {
                if (_xRunning) return;
                _xRunning = true;
            }
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                _xRunning = false;
                await XpaceError("Esa carpeta no existe.");
                return;
            }
            lock (_xLock)
            {
                _xResult = null;
                _xById.Clear();
                _xIds.Clear();
                _xNext = 0;
            }
            XpaceScanner scanner = new XpaceScanner();
            CancellationTokenSource run = new CancellationTokenSource();
            _xRun = run;
            await Broadcast(new Dictionary<string, object> { { "type", "xpace.started" }, { "path", path } });
            Task<XResult> work = Task.Run(() => scanner.Scan(path, run.Token));
            while (!work.IsCompleted)
            {
                await Task.WhenAny(work, Task.Delay(150));
                if (work.IsCompleted) break;
                await Broadcast(new Dictionary<string, object>
                {
                    { "type", "xpace.progress" }, { "files", scanner.Files }, { "bytes", scanner.Bytes },
                    { "folders", scanner.Folders }, { "current", scanner.Current }
                });
            }
            XResult result = null;
            string error = null;
            try { result = work.Result; }
            catch (AggregateException e) { error = e.InnerException != null ? e.InnerException.Message : e.Message; }
            _xRunning = false;
            if (result != null && result.Cancelled) { result = null; error = "Análisis cancelado."; }
            _xResult = result;
            if (result == null)
            {
                await XpaceError(error ?? "No se pudo analizar.");
                return;
            }
            XId(result.Root);
            await Broadcast(new Dictionary<string, object>
            {
                { "type", "xpace.done" }, { "path", result.Path }, { "rootId", XId(result.Root) }, { "files", result.Files },
                { "bytes", result.Bytes }, { "folders", result.Folders }, { "denied", result.Denied },
                { "elapsedMs", (long)result.Elapsed.TotalMilliseconds }
            });
        }
    }
}
