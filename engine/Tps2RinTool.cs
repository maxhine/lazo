using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Lazo
{
    /// <summary>Lo que dice un RINEX de observación: cuándo se observó, cada cuánto y con qué.</summary>
    internal sealed class RinexSummary
    {
        public DateTime First, Last;
        public bool HasTimes;
        public double Interval;
        public int Epochs;
        /// <summary>Épocas que habría con el intervalo y sin huecos.</summary>
        public int Expected;
        public int Gaps;
        public double MissingSeconds;
        public int LeapSeconds;
        public string Marker = "", Receiver = "", Antenna = "", Program = "";
        public int Satellites;
        public string Systems = "";
    }

    /// <summary>Lee la cabecera y las épocas de un RINEX 2 de observación (lo que escribe TPS2RIN).</summary>
    internal static class RinexReader
    {
        private static readonly Regex Epoch = new Regex(@"^\s(\d\d)\s+(\d{1,2})\s+(\d{1,2})\s+(\d{1,2})\s+(\d{1,2})\s+(\d{1,2}\.\d+)\s+([0-6])\s*(\d+)", RegexOptions.Compiled);

        private static string Label(string line)
        {
            return line.Length > 60 ? line.Substring(60).Trim() : "";
        }

        private static string Field(string line, int start, int length)
        {
            if (line.Length <= start) return "";
            return line.Substring(start, Math.Min(length, line.Length - start)).Trim();
        }

        public static RinexSummary Read(string path)
        {
            RinexSummary s = new RinexSummary();
            List<DateTime> epochs = new List<DateTime>();
            bool header = true;
            HashSet<string> systems = new HashSet<string>();
            using (StreamReader reader = new StreamReader(path, Encoding.ASCII))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (header)
                    {
                        switch (Label(line))
                        {
                            case "MARKER NAME": s.Marker = Field(line, 0, 60); break;
                            case "REC # / TYPE / VERS": s.Receiver = Field(line, 20, 20); break;
                            case "ANT # / TYPE": s.Antenna = Field(line, 20, 20); break;
                            case "PGM / RUN BY / DATE": s.Program = Field(line, 0, 20); break;
                            case "INTERVAL":
                                double interval;
                                if (double.TryParse(Field(line, 0, 10), NumberStyles.Float, CultureInfo.InvariantCulture, out interval)) s.Interval = interval;
                                break;
                            case "LEAP SECONDS":
                                int leap;
                                if (int.TryParse(Field(line, 0, 6), out leap)) s.LeapSeconds = leap;
                                break;
                            case "# OF SATELLITES":
                                int count;
                                if (int.TryParse(Field(line, 0, 6), out count)) s.Satellites = count;
                                break;
                            case "PRN / # OF OBS":
                                string prn = Field(line, 3, 3);
                                if (prn.Length > 0 && char.IsLetter(line[3])) systems.Add(line.Substring(3, 1));
                                break;
                            case "END OF HEADER": header = false; break;
                        }
                        continue;
                    }
                    Match m = Epoch.Match(line);
                    if (!m.Success) continue;
                    int year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    year += year >= 80 ? 1900 : 2000;
                    double seconds = double.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture);
                    try
                    {
                        epochs.Add(new DateTime(year, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value),
                            int.Parse(m.Groups[5].Value), 0).AddSeconds(seconds));
                    }
                    catch (ArgumentOutOfRangeException) { }
                }
            }
            s.Epochs = epochs.Count;
            s.Systems = string.Concat(systems.OrderBy(x => x));
            if (epochs.Count == 0) return s;
            s.HasTimes = true;
            s.First = epochs[0];
            s.Last = epochs[epochs.Count - 1];
            if (s.Interval <= 0 && epochs.Count > 1)
            {
                // Sin INTERVAL en la cabecera: el paso más frecuente entre épocas.
                s.Interval = epochs.Zip(epochs.Skip(1), (a, b) => Math.Round((b - a).TotalSeconds, 3)).GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
            }
            if (s.Interval > 0)
            {
                s.Expected = (int)Math.Round((s.Last - s.First).TotalSeconds / s.Interval) + 1;
                for (int i = 1; i < epochs.Count; i++)
                {
                    double step = (epochs[i] - epochs[i - 1]).TotalSeconds;
                    if (step > s.Interval * 1.5)
                    {
                        s.Gaps++;
                        s.MissingSeconds += step - s.Interval;
                    }
                }
            }
            return s;
        }
    }

    /// <summary>
    /// TPS2RIN de Topcon (archivos .tps de observación a RINEX) para la isla: el programa es el de siempre, sin tocarlo.
    /// Se ejecuta con cada .tps en una carpeta temporal, se lee el RINEX que deja (tiempos observados, intervalo,
    /// épocas, huecos) y al convertir se copian sus archivos a la carpeta elegida con el nombre que se quiera.
    /// </summary>
    internal sealed partial class EngineHost
    {
        private sealed class TpsRun
        {
            public string Dir;
            public DateTime Modified;
            public long Length;
            public string Message = "";
            public RinexSummary Summary;
            public List<string> Outputs = new List<string>();
        }

        private readonly Dictionary<string, TpsRun> _tps = new Dictionary<string, TpsRun>(StringComparer.OrdinalIgnoreCase);
        private readonly object _tpsLock = new object();
        private string _tpsRoot;

        private static string TpsExeFile()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "tps2rin.txt");
        }

        /// <summary>El TPS2RIN que se usa: el que se eligió, o uno junto al programa o en %APPDATA%\Lazo\tools.</summary>
        private static string TpsExe()
        {
            try
            {
                string file = TpsExeFile();
                if (File.Exists(file))
                {
                    string saved = File.ReadAllText(file).Trim();
                    if (saved.Length > 0 && File.Exists(saved)) return saved;
                }
            }
            catch { }
            string[] near =
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tps2rin.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "tools", "tps2rin.exe")
            };
            return near.FirstOrDefault(File.Exists);
        }

        private string TpsTemp()
        {
            if (_tpsRoot == null)
            {
                _tpsRoot = Path.Combine(Path.GetTempPath(), "lazo-tps-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(_tpsRoot);
                AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { Directory.Delete(_tpsRoot, true); } catch { } };
            }
            return _tpsRoot;
        }

        private static string Iso(DateTime value) { return value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture); }

        private static Dictionary<string, object> TpsJson(string path, TpsRun run)
        {
            Dictionary<string, object> json = new Dictionary<string, object>
            {
                { "type", "tps.file" }, { "path", path }, { "ok", run != null && run.Summary != null && run.Message.Length == 0 },
                { "message", run == null ? "" : run.Message },
                { "baseName", Path.GetFileNameWithoutExtension(path) }
            };
            if (run != null)
            {
                json["outputs"] = run.Outputs.Select(o => new Dictionary<string, object>
                    { { "name", Path.GetFileName(o) }, { "ext", Path.GetExtension(o) }, { "size", new FileInfo(o).Length } }).ToList();
                RinexSummary s = run.Summary;
                if (s != null)
                {
                    json["obs"] = new Dictionary<string, object>
                    {
                        { "hasTimes", s.HasTimes }, { "first", s.HasTimes ? Iso(s.First) : "" }, { "last", s.HasTimes ? Iso(s.Last) : "" },
                        { "leap", s.LeapSeconds }, { "interval", s.Interval }, { "epochs", s.Epochs }, { "expected", s.Expected },
                        { "gaps", s.Gaps }, { "missing", s.MissingSeconds }, { "marker", s.Marker }, { "receiver", s.Receiver },
                        { "antenna", s.Antenna }, { "satellites", s.Satellites }, { "systems", s.Systems },
                        { "producedBy", s.Program }
                    };
                }
            }
            return json;
        }

        /// <summary>Corre TPS2RIN sobre un archivo; el resultado queda en una carpeta temporal.</summary>
        private async Task<TpsRun> TpsConvert(string exe, string file)
        {
            FileInfo info = new FileInfo(file);
            lock (_tpsLock)
            {
                TpsRun cached;
                if (_tps.TryGetValue(file, out cached) && cached.Modified == info.LastWriteTimeUtc && cached.Length == info.Length && cached.Outputs.Count > 0) return cached;
            }
            TpsRun run = new TpsRun { Modified = info.LastWriteTimeUtc, Length = info.Length };
            run.Dir = Path.Combine(TpsTemp(), Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(run.Dir);
            try
            {
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = exe, Arguments = "-q -o \"" + run.Dir + "\" -i \"" + file + "\"", WorkingDirectory = run.Dir,
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                string output;
                int exit;
                using (Process process = Process.Start(start))
                {
                    Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
                    bool done = await Task.Run(() => process.WaitForExit(180000));
                    if (!done)
                    {
                        try { process.Kill(); } catch { }
                        run.Message = "TPS2RIN tardó demasiado y se detuvo.";
                        return run;
                    }
                    exit = process.ExitCode;
                    output = (await stdout + " " + await stderr).Trim();
                }
                run.Outputs = Directory.GetFiles(run.Dir).Where(f => !string.Equals(Path.GetExtension(f), ".log", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f).ToList();
                if (run.Outputs.Count == 0)
                {
                    run.Message = output.Length > 0 ? output : "TPS2RIN no generó archivos (código " + exit + ").";
                    return run;
                }
                string obs = run.Outputs.FirstOrDefault(f => Path.GetExtension(f).Length == 4 && char.ToUpperInvariant(Path.GetExtension(f)[3]) == 'O');
                if (obs != null) run.Summary = await Task.Run(() => RinexReader.Read(obs));
                else run.Message = "TPS2RIN no generó el archivo de observación (.yyO).";
            }
            catch (Exception ex) { run.Message = ex.Message; }
            lock (_tpsLock) _tps[file] = run;
            return run;
        }

        private async Task TpsCommand(Client client, string cmd, Dictionary<string, object> m)
        {
            switch (cmd)
            {
                case "tps.status":
                    await Send(client, new Dictionary<string, object> { { "type", "tps.status" }, { "exe", TpsExe() } });
                    break;
                case "tps.setExe":
                {
                    string path = Str(m, "path");
                    string problem = "";
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) problem = "No existe " + path;
                    else
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(TpsExeFile()));
                            File.WriteAllText(TpsExeFile(), path);
                        }
                        catch (Exception ex) { problem = ex.Message; }
                    }
                    await Send(client, new Dictionary<string, object> { { "type", "tps.status" }, { "exe", TpsExe() }, { "problem", problem } });
                    break;
                }
                case "tps.pick":
                {
                    string kind = Str(m, "kind");
                    string[] chosen = await PickDialog(kind, Str(m, "start"));
                    await Send(client, new Dictionary<string, object> { { "type", "tps.picked" }, { "kind", kind }, { "paths", chosen } });
                    break;
                }
                case "tps.probe":
                {
                    string exe = TpsExe();
                    List<string> files = StringList(m, "files").Where(f => string.Equals(Path.GetExtension(f), ".tps", StringComparison.OrdinalIgnoreCase) && File.Exists(f)).ToList();
                    if (exe == null)
                    {
                        await Send(client, new Dictionary<string, object> { { "type", "tps.status" }, { "exe", null }, { "problem", "" } });
                        break;
                    }
                    foreach (string file in files)
                    {
                        await Send(client, new Dictionary<string, object> { { "type", "tps.working" }, { "path", file } });
                        TpsRun run = await TpsConvert(exe, file);
                        await Send(client, TpsJson(file, run));
                    }
                    await Send(client, new Dictionary<string, object> { { "type", "tps.probed" } });
                    break;
                }
                case "tps.convert":
                {
                    string folder = Str(m, "folder");
                    object items;
                    List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
                    if (m.TryGetValue("items", out items) && items is System.Collections.IEnumerable)
                        foreach (object item in (System.Collections.IEnumerable)items)
                        {
                            Dictionary<string, object> entry = item as Dictionary<string, object>;
                            if (entry != null) list.Add(entry);
                        }
                    List<string> written = new List<string>();
                    string error = "";
                    string exe = TpsExe();
                    foreach (Dictionary<string, object> entry in list)
                    {
                        string file = entry.ContainsKey("file") ? Convert.ToString(entry["file"]) : "";
                        string name = entry.ContainsKey("name") ? Convert.ToString(entry["name"]).Trim() : "";
                        if (exe == null || !File.Exists(file)) { error = "No se encontró " + file; break; }
                        TpsRun run = await TpsConvert(exe, file);
                        if (run.Outputs.Count == 0) { error = run.Message; break; }
                        string target = string.IsNullOrEmpty(folder) ? Path.GetDirectoryName(file) : folder;
                        try
                        {
                            Directory.CreateDirectory(target);
                            written.AddRange(TpsPlace(run, Path.GetFileNameWithoutExtension(file), name, target));
                        }
                        catch (Exception ex) { error = ex.Message; break; }
                    }
                    await Send(client, new Dictionary<string, object> { { "type", "tps.done" }, { "ok", error.Length == 0 }, { "message", error }, { "written", written } });
                    break;
                }
                default:
                    await Send(client, new Dictionary<string, object> { { "type", "error" }, { "message", "Orden desconocida: " + cmd } });
                    break;
            }
        }

        /// <summary>Los diálogos de Windows (archivos .tps, TPS2RIN.exe o una carpeta) en su propio hilo STA.</summary>
        private static Task<string[]> PickDialog(string kind, string start)
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
                        if (kind == "folder")
                        {
                            using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog
                            { Description = "Elige dónde guardar los RINEX", SelectedPath = start ?? "", ShowNewFolderButton = true })
                                done.SetResult(dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK ? new[] { dialog.SelectedPath } : new string[0]);
                        }
                        else
                        {
                            bool exe = kind == "exe";
                            using (System.Windows.Forms.OpenFileDialog dialog = new System.Windows.Forms.OpenFileDialog
                            {
                                Title = exe ? "Elige tps2rin.exe" : "Elige archivos .tps",
                                Filter = exe ? "TPS2RIN (*.exe)|*.exe" : "Observaciones de Topcon (*.tps)|*.tps|Todos los archivos (*.*)|*.*",
                                Multiselect = !exe
                            })
                                done.SetResult(dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK ? dialog.FileNames : new string[0]);
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

        /// <summary>Un nombre sin caracteres que Windows no admite en un archivo.</summary>
        internal static string TpsSafeName(string name, string fallback)
        {
            string clean = new string((name ?? "").Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0).ToArray()).Trim().TrimEnd('.');
            return clean.Length == 0 ? fallback : clean;
        }

        /// <summary>Copia los archivos de una conversión a la carpeta con el nombre pedido, sin pisar lo que ya haya.</summary>
        private static List<string> TpsPlace(TpsRun run, string inputBase, string wanted, string target)
        {
            string baseName = TpsSafeName(wanted, inputBase);
            // Conserva la extensión de cada archivo (.26O, .26N…); si TPS2RIN le puso otro nombre al archivo, el nuevo lo reemplaza.
            string bump = "";
            for (int n = 2; run.Outputs.Any(o => File.Exists(Path.Combine(target, baseName + bump + Path.GetExtension(o)))); n++) bump = " (" + n + ")";
            List<string> written = new List<string>();
            foreach (string output in run.Outputs)
            {
                string destination = Path.Combine(target, baseName + bump + Path.GetExtension(output));
                File.Copy(output, destination, false);
                written.Add(destination);
            }
            return written;
        }
    }
}
