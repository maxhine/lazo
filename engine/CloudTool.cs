using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Lazo
{
    /// <summary>
    /// El conversor de nubes de puntos del Lazo clásico para la isla: la misma lógica (PointCloudConverter, CloudExport,
    /// CloudReprojector, CloudPreview…, compiladas tal cual desde ../lazo/src) con órdenes «cloud.*». Las decisiones
    /// (quién reproyecta, qué combinaciones no se pueden) son las de MainWindow.Tools.cs, repetidas aquí en C#.
    /// </summary>
    internal sealed partial class EngineHost
    {
        /// <summary>Un sistema elegible: uno que calcula Lazo, uno de la biblioteca de ReCap, o ambos a la vez.</summary>
        private sealed class CsChoice
        {
            public LazoProjection Lazo;
            public CoordSystem Recap;
            public string Label { get { return Lazo != null ? Lazo.Name : Recap.Label; } }
            public string RecapKey
            {
                get
                {
                    string key = Recap != null ? Recap.Key : Lazo != null ? Lazo.RecapKey : null;
                    return key != null && (!CoordinateLibrary.Available || CoordinateLibrary.Find(key) != null) ? key : null;
                }
            }
            public int Epsg { get { return Lazo != null ? Lazo.Epsg : Recap.Epsg; } }
            public string Ref { get { return Lazo != null ? "lazo:" + Lazo.Id : "recap:" + Recap.Key; } }
            public bool Same(CsChoice other)
            {
                if (other == null) return false;
                if (Lazo != null && other.Lazo != null) return Lazo.Id == other.Lazo.Id;
                return RecapKey != null && RecapKey == other.RecapKey && (Lazo == null || !Lazo.Egm2008) && (other.Lazo == null || !other.Lazo.Egm2008);
            }

            public Dictionary<string, object> Json()
            {
                return new Dictionary<string, object>
                {
                    { "ref", Ref }, { "label", Label }, { "epsg", Epsg },
                    { "detail", Lazo != null ? (Lazo.RecapKey != null ? "Colombia" : "Lo calcula Lazo") : Recap.Key },
                    { "recapKey", RecapKey }, { "lazo", Lazo != null }, { "egm2008", Lazo != null && Lazo.Egm2008 }
                };
            }
        }

        private static CsChoice ResolveChoice(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return null;
            if (reference.StartsWith("lazo:", StringComparison.Ordinal))
            {
                LazoProjection p = LazoProjection.ById(reference.Substring(5));
                return p == null ? null : new CsChoice { Lazo = p };
            }
            if (reference.StartsWith("recap:", StringComparison.Ordinal))
            {
                CoordSystem cs = CoordinateLibrary.Find(reference.Substring(6));
                return cs == null ? null : new CsChoice { Recap = cs };
            }
            return null;
        }

        private static List<string> StringList(Dictionary<string, object> m, string key)
        {
            object value;
            List<string> list = new List<string>();
            if (m.TryGetValue(key, out value) && value is IEnumerable && !(value is string))
                foreach (object item in (IEnumerable)value) if (item != null) list.Add(item.ToString());
            return list;
        }

        private static string Decimal(double value) { return value.ToString("0.###", CultureInfo.InvariantCulture); }

        // ── La nube en marcha ───────────────────────────────────────────────────

        private CancellationTokenSource _cRun;
        private CancellationTokenSource _cPreviewRun;
        private CloudPatch _cPatch;
        private string _cDeCap;
        private bool _cDeCapLooked;

        private string DeCap()
        {
            if (!_cDeCapLooked) { _cDeCap = PointCloudConverter.FindDeCap(); _cDeCapLooked = true; }
            return _cDeCap;
        }

        private async Task CloudCommand(Client client, string cmd, Dictionary<string, object> m)
        {
            switch (cmd)
            {
                case "cloud.status":
                    await Send(client, new Dictionary<string, object>
                    {
                        { "type", "cloud.status" }, { "deCap", DeCap() }, { "geoid", GeoidGrid.Find() != null }, { "library", CoordinateLibrary.Available },
                        { "running", _cRun != null },
                        { "units", PointCloudConverter.UnitNames }, { "formats", new[] { "rcp", "rcs", "las", "xyz", "pts", "csv" } },
                        { "projections", LazoProjection.All.Select(p => new Dictionary<string, object>
                            { { "ref", "lazo:" + p.Id }, { "label", p.Name }, { "epsg", p.Epsg }, { "geographic", p.Geographic },
                              { "featured", p.Featured }, { "egm2008", p.Egm2008 }, { "recap", p.RecapKey != null } }).ToList() }
                    });
                    break;
                case "cloud.probe":
                {
                    List<string> files = StringList(m, "files").Where(PointCloudConverter.IsSupported).ToList();
                    List<CloudCrs> found = await Task.Run(() => files.Select(f => { try { return CloudCrsReader.Read(f); } catch { return null; } }).ToList());
                    CloudCrs first = found.FirstOrDefault(c => c != null);
                    int kinds = found.Where(c => c != null).Select(c => c.Epsg + "|" + c.Name).Distinct().Count();
                    string note = kinds > 1 ? "Los archivos declaran sistemas distintos; se usa el del primero." :
                        found.Any(c => c == null) && first != null ? "Algunos archivos no indican sistema; se asume el mismo." : "";
                    LazoProjection lazo = first == null ? null : LazoProjection.Detect(first.Epsg, first.Name);
                    CsChoice detected = first == null || (lazo == null && first.Match == null) ? null : new CsChoice { Lazo = lazo, Recap = lazo == null ? first.Match : null };
                    List<Dictionary<string, object>> sizes = files.Select(f =>
                    {
                        long size = 0;
                        try { size = new FileInfo(f).Length; } catch { }
                        return new Dictionary<string, object> { { "path", f }, { "size", size } };
                    }).ToList();
                    await Send(client, new Dictionary<string, object>
                    {
                        { "type", "cloud.probe" }, { "files", sizes },
                        { "detected", detected == null ? null : detected.Json() }, { "declared", first == null ? "" : first.Name },
                        { "note", note }
                    });
                    break;
                }
                case "cloud.search":
                {
                    string text = (Str(m, "query") ?? "").Trim();
                    bool forSource = Flag(m, "source");
                    List<CsChoice> results = new List<CsChoice>();
                    foreach (LazoProjection p in LazoProjection.All)
                    {
                        if (!forSource && p.Geographic) continue;
                        if (text.Length == 0 || p.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || p.Epsg.ToString() == text)
                            results.Add(new CsChoice { Lazo = p });
                    }
                    if (text.Length > 0 && CoordinateLibrary.Available)
                        foreach (CoordSystem cs in CoordinateLibrary.Search(text, 12))
                            if (!results.Any(f => f.Lazo != null && f.Lazo.RecapKey == cs.Key)) results.Add(new CsChoice { Recap = cs });
                    await Send(client, new Dictionary<string, object>
                    {
                        { "type", "cloud.search" }, { "query", text }, { "library", CoordinateLibrary.Available },
                        { "results", results.Take(14).Select(r => r.Json()).ToList() }
                    });
                    break;
                }
                case "cloud.pickFiles":
                case "cloud.pickFolder":
                {
                    string start = Str(m, "start");
                    bool files = cmd == "cloud.pickFiles";
                    string[] chosen = await Dialogs(files, start);
                    await Send(client, new Dictionary<string, object>
                    {
                        { "type", "cloud.picked" }, { "kind", files ? "files" : "folder" }, { "paths", chosen }
                    });
                    break;
                }
                case "cloud.preview":
                    await CloudPreviewCommand(client, m);
                    break;
                case "cloud.convert":
                    await CloudConvert(m);
                    break;
                case "cloud.cancel":
                {
                    CancellationTokenSource run = _cRun;
                    if (run != null) run.Cancel();
                    break;
                }
                case "cloud.open":
                {
                    string path = Str(m, "path");
                    string problem = "";
                    try { if (File.Exists(path)) Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); else problem = "No existe " + path; }
                    catch (Exception ex) { problem = "No se pudo abrir con ReCap: " + ex.Message; }
                    await Send(client, new Dictionary<string, object> { { "type", "cloud.opened" }, { "path", path }, { "problem", problem } });
                    break;
                }
                default:
                    await Send(client, new Dictionary<string, object> { { "type", "error" }, { "message", "Orden desconocida: " + cmd } });
                    break;
            }
        }

        /// <summary>Los diálogos de Windows en su propio hilo (STA), con una ventana invisible arriba de todo como dueña.</summary>
        private static Task<string[]> Dialogs(bool files, string start)
        {
            TaskCompletionSource<string[]> done = new TaskCompletionSource<string[]>();
            Thread thread = new Thread(() =>
            {
                try
                {
                    using (System.Windows.Forms.Form owner = new System.Windows.Forms.Form
                    {
                        TopMost = true, ShowInTaskbar = false, FormBorderStyle = System.Windows.Forms.FormBorderStyle.None,
                        StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen, Opacity = 0, Width = 1, Height = 1
                    })
                    {
                        owner.Show();
                        if (files)
                        {
                            using (System.Windows.Forms.OpenFileDialog dialog = new System.Windows.Forms.OpenFileDialog
                            {
                                Title = "Elige nubes de puntos",
                                Filter = "Nubes de puntos (*.las;*.laz;*.e57)|*.las;*.laz;*.e57|Todos los archivos (*.*)|*.*",
                                Multiselect = true
                            })
                            {
                                done.SetResult(dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK ? dialog.FileNames : new string[0]);
                            }
                        }
                        else
                        {
                            using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog
                            {
                                Description = "Elige dónde guardar el resultado",
                                SelectedPath = string.IsNullOrEmpty(start) ? "" : start,
                                ShowNewFolderButton = true
                            })
                            {
                                done.SetResult(dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK ? new[] { dialog.SelectedPath } : new string[0]);
                            }
                        }
                    }
                }
                catch (Exception ex) { done.TrySetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return done.Task;
        }

        // ── Vista previa de la reducción ────────────────────────────────────────

        private static string Png(BitmapSource image)
        {
            if (image == null) return "";
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (MemoryStream stream = new MemoryStream())
            {
                encoder.Save(stream);
                return Convert.ToBase64String(stream.ToArray());
            }
        }

        private static double UnitsPerMeter(int unit, string file)
        {
            if (file != null && file.EndsWith(".e57", StringComparison.OrdinalIgnoreCase)) return 1;
            switch (unit)
            {
                case 0: return 1000;
                case 1: return 100;
                case 3: return 1 / 0.0254;
                case 4: return 1 / 0.3048;
                case 5: return 1 / 0.3048006096;
                default: return 1;
            }
        }

        private async Task CloudPreviewCommand(Client client, Dictionary<string, object> m)
        {
            string path = Str(m, "file");
            long seq = Number(m, "seq", 0);
            double zone = 0.5, mm = 0;
            double.TryParse(Str(m, "zone") ?? "0.5", NumberStyles.Float, CultureInfo.InvariantCulture, out zone);
            double.TryParse(Str(m, "mm") ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture, out mm);
            bool elevation = Flag(m, "elevation");
            int unit = (int)Number(m, "unit", 2);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                await Send(client, new Dictionary<string, object> { { "type", "cloud.preview" }, { "seq", seq }, { "ok", false }, { "message", "" } });
                return;
            }
            CancellationTokenSource previous = _cPreviewRun;
            if (previous != null) previous.Cancel();
            CancellationTokenSource run = new CancellationTokenSource();
            _cPreviewRun = run;
            string recap = DeCap() == null ? null : Path.GetDirectoryName(DeCap());
            CloudPatch patch = _cPatch;
            Dictionary<string, object> reply;
            try
            {
                reply = await Task.Run(() =>
                {
                    CloudPatch loaded = patch;
                    if (loaded == null || !string.Equals(loaded.Path, path, StringComparison.OrdinalIgnoreCase) || Math.Abs(loaded.Zone - zone) > 0.001)
                        loaded = CloudPreview.Load(path, zone, recap, run.Token);
                    run.Token.ThrowIfCancellationRequested();
                    PreviewFrame frame = CloudPreview.Render(loaded, mm > 0 ? mm : 10, UnitsPerMeter(unit, path), elevation);
                    _cPatch = loaded;
                    return new Dictionary<string, object>
                    {
                        { "type", "cloud.preview" }, { "seq", seq }, { "ok", frame.Original != null }, { "message", "" },
                        { "original", Png(frame.Original) }, { "reduced", mm > 0 ? Png(frame.Reduced) : "" },
                        { "before", frame.Before }, { "after", frame.After }, { "windowMeters", frame.WindowMeters },
                        { "reducedShown", mm > 0 }, { "canZone", loaded.CanZone }, { "e57", loaded.Format == ".e57" }
                    };
                });
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                reply = new Dictionary<string, object> { { "type", "cloud.preview" }, { "seq", seq }, { "ok", false }, { "message", "No se pudo mostrar la vista previa: " + ex.Message } };
            }
            if (_cPreviewRun != run) return;
            await Send(client, reply);
        }

        // ── Conversión ──────────────────────────────────────────────────────────

        private Task CloudDone(bool ok, string output, string message, string report, long elapsedMs)
        {
            return Broadcast(new Dictionary<string, object>
            {
                { "type", "cloud.done" }, { "ok", ok }, { "output", output ?? "" }, { "message", message ?? "" }, { "report", report ?? "" }, { "elapsedMs", elapsedMs }
            });
        }

        private async Task CloudConvert(Dictionary<string, object> m)
        {
            if (_cRun != null) return;
            string decap = DeCap();
            if (decap == null) { await CloudDone(false, null, "Necesita Autodesk ReCap instalado en este equipo.", null, 0); return; }
            List<string> files = StringList(m, "files");
            if (files.Count == 0) { await CloudDone(false, null, "Elige al menos una nube.", null, 0); return; }
            double decimation = 0;
            string decimationText = (Str(m, "decimation") ?? "").Trim().Replace(',', '.');
            if (decimationText.Length > 0 && (!double.TryParse(decimationText, NumberStyles.Float, CultureInfo.InvariantCulture, out decimation) || decimation <= 0))
            {
                await CloudDone(false, null, "La reducción de puntos va en milímetros, por ejemplo 5", null, 0);
                return;
            }
            string format = Str(m, "format") ?? "rcp";
            CsChoice source = ResolveChoice(Str(m, "source")), target = ResolveChoice(Str(m, "target"));
            bool ellipsoidal = Flag(m, "ellipsoidal");
            PointCloudJob job = new PointCloudJob
            {
                Inputs = files,
                OutputFolder = string.IsNullOrEmpty(Str(m, "folder")) ? Path.GetDirectoryName(files[0]) : Str(m, "folder"),
                Name = Str(m, "name"),
                Unify = format == "rcs",
                Format = format,
                Unit = (int)Number(m, "unit", 2),
                DecimationMm = decimation
            };
            string projection = ProjectionProblem(source, target, ellipsoidal, files);
            if (projection != null) { await CloudDone(false, null, projection, null, 0); return; }
            if (target != null && !target.Same(source) && NeedsLazo(source, target, ellipsoidal))
            {
                job.LazoFrom = source.Lazo;
                job.LazoTo = target.Lazo;
                job.Ellipsoidal = ellipsoidal && target.Lazo.Egm2008;
                job.GeoidPath = GeoidGrid.Find();
            }
            else
            {
                job.SourceCs = source != null ? source.RecapKey : null;
                job.TargetCs = target != null && !target.Same(source) ? target.RecapKey : null;
            }
            string problem = PointCloudConverter.Validate(job);
            if (problem != null) { await CloudDone(false, null, problem, null, 0); return; }

            job.MergeParts = format == "rcp" && Flag(m, "merge");
            CancellationTokenSource run = new CancellationTokenSource();
            _cRun = run;
            Stopwatch clock = Stopwatch.StartNew();
            await Broadcast(new Dictionary<string, object> { { "type", "cloud.started" } });
            await Broadcast(new Dictionary<string, object> { { "type", "cloud.progress" }, { "value", 0.0 }, { "text", "Comprobando la licencia de ReCap…" } });
            PointCloudResult result;
            try
            {
                string license = await PointCloudConverter.CheckLicenseAsync(decap);
                result = license != null ? new PointCloudResult { Message = license } :
                    await PointCloudConverter.ConvertAsync(decap, job, (value, text) =>
                    {
                        if (_cRun != run) return;
                        Broadcast(new Dictionary<string, object> { { "type", "cloud.progress" }, { "value", value }, { "text", text ?? "" } });
                    }, run.Token);
            }
            catch (OperationCanceledException) { result = new PointCloudResult { Message = "Conversión cancelada." }; }
            catch (Exception ex) { result = new PointCloudResult { Message = ex.Message }; }
            _cRun = null;
            await CloudDone(result.Ok, result.Output, result.Ok ? "" : result.Message, result.Report, clock.ElapsedMilliseconds);
        }

        /// <summary>Lo que impide la combinación elegida, o null si se puede.</summary>
        private static string ProjectionProblem(CsChoice source, CsChoice target, bool ellipsoidal, List<string> files)
        {
            if (target == null) return null;
            if (source == null) return "Para reproyectar, indica en qué sistema está la nube (Cambiar).";
            if (source.Same(target)) return null;
            bool lazo = NeedsLazo(source, target, ellipsoidal);
            if (lazo && source.Lazo == null)
                return "Lazo no sabe calcular desde " + source.Label + ". Elige como salida un sistema de ReCap (Otro sistema…).";
            if (!lazo && (source.RecapKey == null || target.RecapKey == null))
                return "ReCap no conoce " + (source.RecapKey == null ? source.Label : target.Label) + " para esta conversión.";
            if (lazo && files.Any(f => string.Equals(Path.GetExtension(f), ".e57", StringComparison.OrdinalIgnoreCase)))
                return "Lazo reproyecta LAS y LAZ; los E57 solo se pueden llevar a sistemas que ReCap conoce.";
            return null;
        }

        /// <summary>La reproyección la hace Lazo (no ReCap): destinos que ReCap no trae, alturas a EGM2008, u origen que ReCap no conoce.</summary>
        private static bool NeedsLazo(CsChoice source, CsChoice target, bool ellipsoidal)
        {
            if (target == null || target.Lazo == null) return false;
            return target.Lazo.RecapKey == null || (target.Lazo.Egm2008 && ellipsoidal) || source == null || source.RecapKey == null;
        }
    }
}
