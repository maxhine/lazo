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
    /// <summary>Qué convertir y cómo. Las unidades siguen a DeCap: 0 mm, 1 cm, 2 m, 3 in, 4 ft, 5 ft topográficos de EE. UU.</summary>
    internal sealed class PointCloudJob
    {
        public List<string> Inputs = new List<string>();
        public string OutputFolder;
        public string Name;
        /// <summary>true: una sola nube .rcs; false: proyecto .rcp con un .rcs por escaneo.</summary>
        public bool Unify;
        public int Unit = 2;
        /// <summary>Distancia mínima entre puntos en milímetros; 0 o menos para no reducir.</summary>
        public double DecimationMm;
        /// <summary>Sistema de coordenadas de la nube (clave de Autodesk), o null si no se indica.</summary>
        public string SourceCs;
        /// <summary>Sistema al que se reproyecta (clave de Autodesk), o null para dejarla en el de origen.</summary>
        public string TargetCs;

        /// <summary>
        /// Reproyección que hace Lazo (para lo que ReCap no trae, como Origen Nacional): de qué sistema a cuál.
        /// Solo LAS y LAZ. Con esto, <see cref="SourceCs"/> y <see cref="TargetCs"/> no se usan.
        /// </summary>
        public LazoProjection LazoFrom, LazoTo;
        /// <summary>Las alturas son elipsoidales y se pasan a EGM2008 (si el destino es «+ EGM2008»).</summary>
        public bool Ellipsoidal;
        public string GeoidPath;

        public bool Reprojects
        {
            get { return !string.IsNullOrEmpty(SourceCs) && !string.IsNullOrEmpty(TargetCs) && !string.Equals(SourceCs, TargetCs, StringComparison.OrdinalIgnoreCase); }
        }

        internal PointCloudJob Copy()
        {
            return new PointCloudJob
            {
                Inputs = Inputs.ToList(), OutputFolder = OutputFolder, Name = Name, Unify = Unify, Unit = Unit,
                DecimationMm = DecimationMm, SourceCs = SourceCs, TargetCs = TargetCs,
                LazoFrom = LazoFrom, LazoTo = LazoTo, Ellipsoidal = Ellipsoidal, GeoidPath = GeoidPath
            };
        }
    }

    internal sealed class PointCloudResult
    {
        public bool Ok;
        public string Output;
        public string Message;
    }

    /// <summary>
    /// Convierte nubes de puntos .las, .laz y .e57 a ReCap (.rcp o .rcs) con DeCap.exe, la herramienta de línea de
    /// comandos que instala Autodesk ReCap, usando la licencia de ReCap del equipo. RCS/RCP es un formato cerrado:
    /// solo las herramientas de Autodesk lo escriben, así que sin ReCap no hay conversión.
    /// </summary>
    internal static class PointCloudConverter
    {
        public static readonly string[] Extensions = { ".las", ".laz", ".e57" };
        public static readonly string[] UnitNames = { "mm", "cm", "m", "in", "ft", "ft EE. UU." };

        public static bool IsSupported(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return Extensions.Contains(ext) && File.Exists(path);
        }

        /// <summary>Ruta de DeCap.exe, o null si ReCap no está instalado.</summary>
        public static string FindDeCap()
        {
            List<string> roots = new List<string>();
            foreach (string variable in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
            {
                string root = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrEmpty(root) && !roots.Contains(root, StringComparer.OrdinalIgnoreCase)) roots.Add(root);
            }
            foreach (string root in roots)
            {
                string standard = Path.Combine(root, "Autodesk", "Autodesk ReCap", "decap.exe");
                if (File.Exists(standard)) return standard;
                try
                {
                    string autodesk = Path.Combine(root, "Autodesk");
                    if (!Directory.Exists(autodesk)) continue;
                    foreach (string folder in Directory.GetDirectories(autodesk, "*ReCap*"))
                    {
                        string candidate = Path.Combine(folder, "decap.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>Comprueba la licencia de ReCap. Devuelve null si está bien, o un mensaje para la persona.</summary>
        public static async Task<string> CheckLicenseAsync(string decap)
        {
            StringBuilder output = new StringBuilder();
            int code = await RunAsync(decap, "--checkLicense", line => output.AppendLine(line), CancellationToken.None).ConfigureAwait(false);
            string text = output.ToString();
            if (text.Contains("[2049]") || text.IndexOf("License Check OK", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            Match error = Regex.Match(text, @"\[[EM]\]\[\d+\]\[([^\]]+)\]");
            return "ReCap no tiene una licencia válida en este equipo" + (error.Success ? ": " + error.Groups[1].Value : ".") + (code != 0 ? " (código " + code + ")" : "");
        }

        /// <summary>Revisa el trabajo antes de empezar. Devuelve null si está listo, o qué falta.</summary>
        public static string Validate(PointCloudJob job)
        {
            if (job.Inputs.Count == 0) return "Elige al menos un archivo .las, .laz o .e57.";
            foreach (string input in job.Inputs)
                if (!IsSupported(input)) return "No se puede convertir " + Path.GetFileName(input) + ".";
            // DeCap nombra cada escaneo por su archivo: dos con el mismo nombre se pisarían.
            string twin = job.Inputs.GroupBy(i => Path.GetFileNameWithoutExtension(i), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1).Select(g => g.Key).FirstOrDefault();
            if (twin != null) return "Hay dos archivos llamados «" + twin + "». Cambia el nombre de uno para convertirlos juntos.";
            if (string.IsNullOrWhiteSpace(job.OutputFolder)) return "Elige dónde guardar el resultado.";
            if (string.IsNullOrWhiteSpace(CleanName(job.Name))) return "Escribe un nombre para el resultado.";
            if (job.Unit < 0 || job.Unit >= UnitNames.Length) return "Unidades no válidas.";
            if (job.LazoTo != null)
            {
                if (job.LazoFrom == null) return "Para reproyectar hay que indicar en qué sistema está la nube.";
                string other = job.Inputs.FirstOrDefault(i => !string.Equals(Path.GetExtension(i), ".las", StringComparison.OrdinalIgnoreCase) &&
                                                              !string.Equals(Path.GetExtension(i), ".laz", StringComparison.OrdinalIgnoreCase));
                if (other != null) return "Lazo solo reproyecta LAS y LAZ a " + job.LazoTo.Name + "; " + Path.GetFileName(other) + " no se puede.";
                if (job.LazoTo.Egm2008 && job.Ellipsoidal && (job.GeoidPath == null || !File.Exists(job.GeoidPath)))
                    return "Para pasar las alturas a EGM2008 hace falta la malla egm08_25.gtx.";
            }
            if (!string.IsNullOrEmpty(job.TargetCs) && string.IsNullOrEmpty(job.SourceCs))
                return "Para reproyectar hay que indicar en qué sistema está la nube.";
            // ReCap ignora en silencio un código que no conoce: solo se aceptan los de su biblioteca.
            if (CoordinateLibrary.Available)
                foreach (string key in new[] { job.SourceCs, job.TargetCs })
                    if (!string.IsNullOrEmpty(key) && CoordinateLibrary.Find(key) == null) return "ReCap no conoce el sistema «" + key + "».";
            return null;
        }

        public static string CleanName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            return new string((name ?? "").Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        }

        /// <summary>Nombre que no pisa un resultado anterior en la carpeta: «Nube», «Nube (2)», …</summary>
        public static string FreeName(string folder, string name)
        {
            string candidate = name;
            for (int i = 2; Taken(folder, candidate); i++) candidate = name + " (" + i + ")";
            return candidate;
        }

        private static bool Taken(string folder, string name)
        {
            return File.Exists(Path.Combine(folder, name + ".rcp")) || File.Exists(Path.Combine(folder, name + ".rcs")) ||
                   Directory.Exists(Path.Combine(folder, name + " Support"));
        }

        public static string Arguments(PointCloudJob job, string name)
        {
            List<string> args = new List<string> { "--importWithLicense", Quote(job.OutputFolder), Quote(name) };
            if (job.Unify) args.Add("--unify");
            if (job.Unit != 2) { args.Add("--inputUnitType"); args.Add(job.Unit.ToString(CultureInfo.InvariantCulture)); }
            if (job.DecimationMm > 0) { args.Add("--decimation"); args.Add(job.DecimationMm.ToString("0.###", CultureInfo.InvariantCulture)); }
            if (!string.IsNullOrEmpty(job.SourceCs)) { args.Add("--currentCoordinateSystem"); args.Add(Quote(job.SourceCs)); }
            if (job.Reprojects) { args.Add("--targetCoordinateSystem"); args.Add(Quote(job.TargetCs)); }
            foreach (string input in job.Inputs) args.Add(Quote(input));
            return string.Join(" ", args);
        }

        /// <summary>
        /// Convierte. <paramref name="progress"/> recibe la fracción hecha (0–1) y una línea de estado.
        /// DeCap termina con código 0 aunque falle, así que el resultado se decide por sus mensajes y por los archivos.
        /// </summary>
        /// <summary>
        /// Procesos de ReCap a la vez. Medido en un i9-14900K: con 8, un LAS de 2,8 GB pasa de 164 s a 63 s; con 16
        /// no mejora (manda el disco). ReCap lee cada archivo con un solo núcleo, así que varios procesos sí rinden.
        /// </summary>
        public static int Lanes { get { return Math.Max(1, Math.Min(8, Environment.ProcessorCount / 4)); } }

        /// <summary>Desde este tamaño (o con varios archivos) vale la pena partir y convertir en paralelo.</summary>
        public static long ParallelThreshold = 768L << 20;

        public static async Task<PointCloudResult> ConvertAsync(string decap, PointCloudJob job, Action<double, string> progress, CancellationToken cancel)
        {
            string problem = Validate(job);
            if (problem != null) return new PointCloudResult { Message = problem };
            Directory.CreateDirectory(job.OutputFolder);
            string name = FreeName(job.OutputFolder, CleanName(job.Name));
            bool splittable = job.Inputs.All(i => IsLas(i) || IsLaz(i));
            long size = job.Inputs.Sum(i => new FileInfo(i).Length);
            bool large = size >= ParallelThreshold;
            // Lo pequeño y lo que no se puede repartir va directo, como siempre.
            if (job.LazoTo == null && !job.Reprojects && (job.Unify || (!large && job.Inputs.Count == 1)))
                return await RunAsync(decap, job, name, progress, 0, 1, cancel).ConfigureAwait(false);
            if (job.LazoTo == null && !large && job.Inputs.Count == 1 && !job.Unify)
                return await RunAsync(decap, job, name, progress, 0, 1, cancel).ConfigureAwait(false);

            string temporary = TemporaryFolder(job.OutputFolder);
            GeoidGrid geoid = null;
            try
            {
                Directory.CreateDirectory(temporary);
                if (job.LazoTo != null && job.LazoTo.Egm2008 && job.Ellipsoidal) geoid = new GeoidGrid(job.GeoidPath);

                // 1. Unidades de trabajo: tramos de LAS/LAZ (reproyectados por Lazo si hace falta) o archivos enteros.
                List<CloudTile> units;
                bool built = job.LazoTo != null || (large && splittable);
                if (built)
                {
                    units = CloudReprojector.Plan(job.Inputs, job.LazoTo != null || large ? Lanes : 1, Path.Combine(temporary, "tramos"));
                    await BuildTilesAsync(units, job, geoid, Path.GetDirectoryName(decap), (value) =>
                    {
                        if (progress != null) progress(0.3 * value, job.LazoTo != null ? "Reproyectando a " + job.LazoTo.Name + "…" : "Preparando tramos en paralelo…");
                    }, cancel).ConfigureAwait(false);
                }
                else units = job.Inputs.Select(i => new CloudTile { Input = i, Output = i, ScanName = Path.GetFileNameWithoutExtension(i) }).ToList();
                double start = built ? 0.3 : 0;

                // Sistema con el que ReCap debe etiquetar (y, en su caso, reproyectar) lo que recibe.
                string current = job.LazoTo != null ? (job.LazoTo.RecapKey != null && CoordinateLibrary.Find(job.LazoTo.RecapKey) != null ? job.LazoTo.RecapKey : null) : job.SourceCs;
                string target = job.LazoTo != null ? null : (job.Reprojects ? job.TargetCs : null);

                // 2a. Nube única: ReCap la arma en un solo proceso (con varios no es más rápido; medido).
                if (job.Unify && target == null)
                {
                    PointCloudJob unify = new PointCloudJob
                    {
                        Inputs = units.Select(u => u.Output).ToList(), OutputFolder = job.OutputFolder, Name = name, Unify = true,
                        Unit = job.LazoTo != null ? 2 : job.Unit, DecimationMm = job.DecimationMm, SourceCs = current
                    };
                    return await RunAsync(decap, unify, name, progress, start, 1, cancel).ConfigureAwait(false);
                }

                // 2b. Proyecto (o paso previo de una nube única reproyectada por ReCap): un ReCap por unidad, varios a la vez.
                string[] scans = await ConvertUnitsAsync(decap, units, job, current, target, temporary, (value, text) =>
                {
                    if (progress != null) progress(start + (job.Unify ? 0.6 : 0.65) * (1 - start) * value, text);
                }, cancel).ConfigureAwait(false);

                if (job.Unify)
                {
                    PointCloudJob unify = new PointCloudJob { Inputs = scans.ToList(), OutputFolder = job.OutputFolder, Name = name, Unify = true };
                    return await RunAsync(decap, unify, name, (value, text) =>
                        { if (progress != null) progress(value, "Uniendo en un solo .rcs…"); }, start + 0.6 * (1 - start), 1, cancel).ConfigureAwait(false);
                }

                // 3. Montaje: los .rcs van a la carpeta «Support» del proyecto y ReCap arma el .rcp en segundos.
                string support = Path.Combine(job.OutputFolder, name + " Support");
                Directory.CreateDirectory(support);
                List<string> placed = new List<string>();
                foreach (string scan in scans)
                {
                    string destination = Path.Combine(support, Path.GetFileName(scan));
                    File.Move(scan, destination);
                    placed.Add(destination);
                }
                PointCloudJob assemble = new PointCloudJob { Inputs = placed, OutputFolder = job.OutputFolder, Name = name };
                PointCloudResult result = await RunAsync(decap, assemble, name, (value, text) =>
                    { if (progress != null) progress(value, "Armando el proyecto…"); }, start + 0.65 * (1 - start), 1, cancel).ConfigureAwait(false);
                if (result.Ok) LabelProject(result.Output, target ?? current);
                return result;
            }
            catch (OperationCanceledException) { return new PointCloudResult { Message = "Conversión cancelada." }; }
            catch (Exception ex) { return new PointCloudResult { Message = ex.Message }; }
            finally
            {
                if (geoid != null) geoid.Dispose();
                try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
            }
        }

        private static bool IsLas(string path) { return string.Equals(Path.GetExtension(path), ".las", StringComparison.OrdinalIgnoreCase); }
        private static bool IsLaz(string path) { return string.Equals(Path.GetExtension(path), ".laz", StringComparison.OrdinalIgnoreCase); }

        /// <summary>
        /// Carpeta de trabajo: junto al resultado (mismo disco, sin copias entre discos), salvo que el resultado esté en
        /// OneDrive, para no sincronizar decenas de GB de temporales.
        /// </summary>
        internal static string TemporaryFolder(string output)
        {
            string unique = Guid.NewGuid().ToString("N").Substring(0, 8);
            foreach (string variable in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
            {
                string cloud = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrEmpty(cloud) && Path.GetFullPath(output).StartsWith(Path.GetFullPath(cloud), StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(Path.GetTempPath(), "Lazo nubes", unique);
            }
            return Path.Combine(output, ".lazo-" + unique);
        }

        /// <summary>Escribe (y reproyecta) los tramos, varios a la vez; cada uno además reparte sus puntos entre los núcleos.</summary>
        private static async Task BuildTilesAsync(List<CloudTile> tiles, PointCloudJob job, GeoidGrid geoid, string recapFolder, Action<double> progress, CancellationToken cancel)
        {
            double[] parts = new double[tiles.Count];
            long total = Math.Max(1, tiles.Sum(t => t.Count));
            using (SemaphoreSlim lanes = new SemaphoreSlim(Lanes))
            {
                await Task.WhenAll(tiles.Select((tile, index) => Task.Run(async () =>
                {
                    await lanes.WaitAsync(cancel).ConfigureAwait(false);
                    try
                    {
                        CloudReprojector.Build(tile, job.LazoFrom, job.LazoTo, geoid, recapFolder, value =>
                        {
                            lock (parts)
                            {
                                parts[index] = value * tile.Count;
                                if (progress != null) progress(parts.Sum() / total);
                            }
                        }, cancel);
                    }
                    finally { lanes.Release(); }
                }, cancel))).ConfigureAwait(false);
            }
        }

        /// <summary>Un ReCap por unidad, hasta <see cref="Lanes"/> a la vez. Devuelve los .rcs que generaron.</summary>
        private static async Task<string[]> ConvertUnitsAsync(string decap, List<CloudTile> units, PointCloudJob job, string current, string target,
            string temporary, Action<double, string> progress, CancellationToken cancel)
        {
            string[] scans = new string[units.Count];
            double[] parts = new double[units.Count];
            int finished = 0;
            using (SemaphoreSlim lanes = new SemaphoreSlim(Lanes))
            {
                await Task.WhenAll(units.Select((unit, index) => Task.Run(async () =>
                {
                    await lanes.WaitAsync(cancel).ConfigureAwait(false);
                    try
                    {
                        string folder = Path.Combine(temporary, "u" + index);
                        PointCloudJob one = new PointCloudJob
                        {
                            Inputs = { unit.Output }, OutputFolder = folder, Name = "u" + index,
                            Unit = job.LazoTo != null ? 2 : job.Unit, DecimationMm = job.DecimationMm, SourceCs = current, TargetCs = target
                        };
                        PointCloudResult result = await RunAsync(decap, one, "u" + index, (value, text) =>
                        {
                            lock (parts)
                            {
                                parts[index] = value;
                                if (progress != null) progress(parts.Average(), "Convirtiendo " + units.Count + " partes a la vez (" + finished + " listas)…");
                            }
                        }, 0, 1, cancel).ConfigureAwait(false);
                        if (!result.Ok) throw new InvalidOperationException(unit.ScanName + ": " + result.Message);
                        string produced = Directory.GetFiles(Path.Combine(folder, "u" + index + " Support"), "*.rcs").FirstOrDefault();
                        if (produced == null) throw new InvalidOperationException("ReCap no generó " + unit.ScanName + ".");
                        string renamed = Path.Combine(Path.GetDirectoryName(produced), CleanName(unit.ScanName) + ".rcs");
                        if (!string.Equals(produced, renamed, StringComparison.OrdinalIgnoreCase)) File.Move(produced, renamed);
                        scans[index] = renamed;
                        Interlocked.Increment(ref finished);
                    }
                    finally { lanes.Release(); }
                }, cancel))).ConfigureAwait(false);
            }
            return scans;
        }

        /// <summary>
        /// Un proyecto armado a partir de .rcs queda sin etiqueta de sistema; se le pone la que corresponde
        /// (el .rcp es un ZIP con un XML). Si algo no cuadra, se deja como está.
        /// </summary>
        private static void LabelProject(string rcp, string key)
        {
            if (string.IsNullOrEmpty(key) || rcp == null || !File.Exists(rcp)) return;
            try
            {
                using (System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.Open(rcp, System.IO.Compression.ZipArchiveMode.Update))
                {
                    System.IO.Compression.ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                    if (entry == null) return;
                    string xml;
                    using (StreamReader reader = new StreamReader(entry.Open())) xml = reader.ReadToEnd();
                    string labeled = xml.Replace("<CoordinateSystem Value=\"\" />", "<CoordinateSystem Value=\"" + System.Security.SecurityElement.Escape(key) + "\" />");
                    if (labeled == xml) return;
                    string entryName = entry.FullName;
                    entry.Delete();
                    System.IO.Compression.ZipArchiveEntry replaced = zip.CreateEntry(entryName);
                    using (StreamWriter writer = new StreamWriter(replaced.Open(), new UTF8Encoding(false))) writer.Write(labeled);
                }
            }
            catch { }
        }

        /// <summary>Una ejecución de DeCap; su progreso se lleva al tramo [<paramref name="from"/>, <paramref name="to"/>].</summary>
        private static async Task<PointCloudResult> RunAsync(string decap, PointCloudJob job, string name, Action<double, string> progress,
            double from, double to, CancellationToken cancel)
        {
            Directory.CreateDirectory(job.OutputFolder);
            int scans = job.Inputs.Count;
            List<string> errors = new List<string>();
            string reported = null;
            Action<double, string> report = (value, text) => { if (progress != null) progress(from + (to - from) * Math.Max(0, Math.Min(1, value)), text); };
            report(0, "Preparando…");
            int code = await RunAsync(decap, Arguments(job, name), line =>
            {
                Match step = Regex.Match(line, @"^\[P\]\[(\d+)\s+(\d+)\]");
                if (step.Success)
                {
                    int scan = int.Parse(step.Groups[1].Value, CultureInfo.InvariantCulture);
                    int percent = int.Parse(step.Groups[2].Value, CultureInfo.InvariantCulture);
                    report(((scan - 1) + percent / 100.0) / Math.Max(1, scans), scans > 1 ? "Escaneo " + scan + " de " + scans + "…" : "Convirtiendo…");
                    return;
                }
                Match unified = Regex.Match(line, @"^\((\d+)%\)\s*(.*)$");
                if (unified.Success)
                {
                    string stage = unified.Groups[2].Value.Trim();
                    report(int.Parse(unified.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0,
                        stage.StartsWith("Indexing", StringComparison.OrdinalIgnoreCase) ? "Indexando…" : "Convirtiendo…");
                    return;
                }
                Match result = Regex.Match(line, @"^\[R\]\[(.+)\]\s*$");
                if (result.Success) { reported = result.Groups[1].Value; return; }
                Match error = Regex.Match(line, @"^\[E\]\[\d+\]\[(.+)\]\s*$");
                if (error.Success) errors.Add(error.Groups[1].Value);
            }, cancel).ConfigureAwait(false);

            if (cancel.IsCancellationRequested) return new PointCloudResult { Message = "Conversión cancelada." };
            string expected = Path.Combine(job.OutputFolder, name + (job.Unify ? ".rcs" : ".rcp"));
            string output = File.Exists(expected) ? expected : (reported != null && File.Exists(reported) ? reported : null);
            if (output == null || errors.Count > 0)
                return new PointCloudResult
                {
                    Output = output,
                    Message = errors.Count > 0 ? string.Join(" · ", errors.Distinct()) :
                        "ReCap no generó el resultado" + (code != 0 ? " (código " + code + ")" : "") + "."
                };
            report(1, "Listo");
            return new PointCloudResult { Ok = true, Output = output, Message = "Listo: " + Path.GetFileName(output) };
        }

        private static string Quote(string value)
        {
            // Una barra final antes de la comilla la escaparía; las rutas de carpeta se pasan sin ella.
            return "\"" + (value ?? "").TrimEnd('\\') + "\"";
        }

        private static Task<int> RunAsync(string exe, string arguments, Action<string> line, CancellationToken cancel)
        {
            return Task.Run(() =>
            {
                ProcessStartInfo start = new ProcessStartInfo(exe, arguments)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)
                };
                using (Process process = new Process { StartInfo = start })
                {
                    DataReceivedEventHandler handler = (s, e) => { if (e.Data != null) line(e.Data.Trim()); };
                    process.OutputDataReceived += handler;
                    process.ErrorDataReceived += handler;
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    using (cancel.Register(() => { try { if (!process.HasExited) process.Kill(); } catch { } }))
                        process.WaitForExit();
                    return process.ExitCode;
                }
            });
        }
    }
}
