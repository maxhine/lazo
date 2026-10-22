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
        /// <summary>rcp, rcs (los escribe ReCap) o las, xyz, pts, csv (los escribe Lazo).</summary>
        public string Format = "rcp";
        /// <summary>Proyecto .rcp: al final se unen las partes en un solo escaneo.</summary>
        public bool MergeParts;
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
                Inputs = Inputs.ToList(), OutputFolder = OutputFolder, Name = Name, Unify = Unify, Format = Format, Unit = Unit,
                DecimationMm = DecimationMm, SourceCs = SourceCs, TargetCs = TargetCs, MergeParts = MergeParts,
                LazoFrom = LazoFrom, LazoTo = LazoTo, Ellipsoidal = Ellipsoidal, GeoidPath = GeoidPath
            };
        }
    }

    internal sealed class PointCloudResult
    {
        public bool Ok;
        public string Output;
        public string Message;
        /// <summary>Reporte de tiempos guardado junto al resultado («nombre - reporte.txt»), si se pudo escribir.</summary>
        public string Report;
    }

    /// <summary>
    /// Cuánto tardó cada paso de una conversión y en total. Se guarda como texto junto al resultado.
    /// </summary>
    internal sealed class ConversionReport
    {
        private readonly PointCloudJob _job;
        private readonly string _name;
        private readonly DateTime _started = DateTime.Now;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<KeyValuePair<string, TimeSpan>> _steps = new List<KeyValuePair<string, TimeSpan>>();
        private readonly List<string> _parts = new List<string>();
        public string Strategy = "";

        public ConversionReport(PointCloudJob job, string name)
        {
            _job = job;
            _name = name;
        }

        public async Task<T> TimeAsync<T>(string step, Func<Task<T>> work)
        {
            Stopwatch clock = Stopwatch.StartNew();
            try { return await work().ConfigureAwait(false); }
            finally { lock (_steps) _steps.Add(new KeyValuePair<string, TimeSpan>(step, clock.Elapsed)); }
        }

        public void Part(string name, long points, TimeSpan elapsed, int attempts, bool ok)
        {
            string line = string.Format(CultureInfo.InvariantCulture, "  {0,-40} {1,14} {2,10}{3}", Trim(name, 40),
                points > 0 ? points.ToString("N0", CultureInfo.GetCultureInfo("es-CO")) + " pts" : "", Duration(elapsed),
                (attempts > 1 ? "  (" + attempts + " intentos)" : "") + (ok ? "" : "  FALLÓ"));
            lock (_parts) _parts.Add(line);
        }

        /// <summary>Cierra el reporte, lo guarda junto al resultado y devuelve su ruta (o null).</summary>
        public string Finish(PointCloudResult result)
        {
            TimeSpan total = _clock.Elapsed;
            CultureInfo es = CultureInfo.GetCultureInfo("es-CO");
            StringBuilder text = new StringBuilder();
            text.AppendLine("Lazo · reporte de conversión de nube de puntos");
            text.AppendLine(new string('─', 60));
            text.AppendLine("Inicio:       " + _started.ToString("yyyy-MM-dd HH:mm:ss", es));
            text.AppendLine("Fin:          " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", es));
            text.AppendLine("Duración:     " + Duration(total));
            text.AppendLine("Resultado:    " + (result.Ok ? result.Output : "falló · " + result.Message));
            if (result.Ok) text.AppendLine("Tamaño:       " + Size(OutputSize(result.Output)));
            text.AppendLine();
            text.AppendLine("Entradas");
            long inputBytes = 0;
            foreach (string input in _job.Inputs)
            {
                long bytes = 0;
                try { bytes = new FileInfo(input).Length; } catch { }
                inputBytes += bytes;
                text.AppendLine("  " + input + "  (" + Size(bytes) + ")");
            }
            if (result.Ok && total.TotalSeconds > 0)
                text.AppendLine("  Ritmo: " + Size((long)(inputBytes / total.TotalSeconds)) + "/s");
            text.AppendLine();
            text.AppendLine("Opciones");
            text.AppendLine("  Salida:       " + (CloudExport.IsLazoFormat(_job.Format) ? "archivo ." + _job.Format + " (lo escribe Lazo)" : _job.Unify ? "nube única .rcs" : "proyecto .rcp" + (_job.MergeParts ? " con un solo escaneo" : "")));
            text.AppendLine("  Reducción:    " + (_job.DecimationMm > 0 ? _job.DecimationMm.ToString("0.###", es) + " mm entre puntos" : "sin reducir"));
            text.AppendLine("  Unidades:     " + PointCloudConverter.UnitNames[Math.Max(0, Math.Min(PointCloudConverter.UnitNames.Length - 1, _job.Unit))]);
            if (_job.LazoTo != null) text.AppendLine("  Coordenadas:  " + (_job.LazoFrom != null ? _job.LazoFrom.Name : "?") + " → " + _job.LazoTo.Name + " (Lazo)" + (_job.Ellipsoidal ? ", alturas a EGM2008" : ""));
            else if (!string.IsNullOrEmpty(_job.SourceCs)) text.AppendLine("  Coordenadas:  " + _job.SourceCs + (_job.Reprojects ? " → " + _job.TargetCs + " (ReCap)" : ""));
            text.AppendLine("  Equipo:       " + Environment.ProcessorCount + " hilos · hasta " + PointCloudConverter.Lanes + " ReCap a la vez");
            if (Strategy.Length > 0) text.AppendLine("  Cómo:         " + Strategy);
            text.AppendLine();
            text.AppendLine("Pasos");
            lock (_steps)
                foreach (KeyValuePair<string, TimeSpan> step in _steps)
                    text.AppendLine(string.Format("  {0,-62} {1,10}", Trim(step.Key, 62), Duration(step.Value)));
            text.AppendLine(string.Format("  {0,-62} {1,10}", "Total", Duration(total)));
            lock (_parts)
                if (_parts.Count > 0)
                {
                    text.AppendLine();
                    text.AppendLine("Partes (cada una en su propio ReCap)");
                    foreach (string part in _parts) text.AppendLine(part);
                }
            try
            {
                string path = Path.Combine(_job.OutputFolder, _name + " - reporte.txt");
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
                return path;
            }
            catch { return null; }
        }

        private static long OutputSize(string output)
        {
            long bytes = 0;
            try
            {
                if (File.Exists(output)) bytes += new FileInfo(output).Length;
                string support = Path.Combine(Path.GetDirectoryName(output), Path.GetFileNameWithoutExtension(output) + " Support");
                if (output.EndsWith(".rcp", StringComparison.OrdinalIgnoreCase) && Directory.Exists(support))
                    bytes += Directory.GetFiles(support, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            }
            catch { }
            return bytes;
        }

        internal static string Duration(TimeSpan time)
        {
            if (time.TotalHours >= 1) return string.Format("{0} h {1:00} min", (int)time.TotalHours, time.Minutes);
            if (time.TotalMinutes >= 1) return string.Format("{0} min {1:00} s", (int)time.TotalMinutes, time.Seconds);
            return time.TotalSeconds.ToString("0.0", CultureInfo.GetCultureInfo("es-CO")) + " s";
        }

        private static string Size(long bytes)
        {
            CultureInfo es = CultureInfo.GetCultureInfo("es-CO");
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", es) + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0", es) + " MB";
            return Math.Max(0, bytes / 1024) + " KB";
        }

        private static string Trim(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }
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
            if (CloudExport.IsLazoFormat(job.Format) && job.Reprojects)
                return "Los formatos que escribe Lazo (" + job.Format.ToUpperInvariant() + ") solo reproyectan con los sistemas que calcula Lazo; elige otro destino o un resultado de ReCap.";
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
            foreach (string format in CloudExport.Formats)
                if (File.Exists(Path.Combine(folder, name + "." + format))) return true;
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
            ConversionReport report = new ConversionReport(job, name);
            PointCloudResult result;
            if (CloudExport.IsLazoFormat(job.Format))
            {
                string recapFolder = string.IsNullOrEmpty(decap) ? null : Path.GetDirectoryName(decap);
                result = await report.TimeAsync("Escribir " + job.Format.ToUpperInvariant(), () => CloudExport.RunAsync(recapFolder, job, name, progress, cancel)).ConfigureAwait(false);
                result.Report = report.Finish(result);
                return result;
            }
            try { result = await ConvertCoreAsync(decap, job, name, progress, report, cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { result = new PointCloudResult { Message = "Conversión cancelada." }; }
            catch (Exception ex) { result = new PointCloudResult { Message = ex.Message }; }
            result.Report = report.Finish(result);
            return result;
        }

        private static async Task<PointCloudResult> ConvertCoreAsync(string decap, PointCloudJob job, string name, Action<double, string> progress,
            ConversionReport report, CancellationToken cancel)
        {
            bool splittable = job.Inputs.All(i => IsLas(i) || IsLaz(i));
            long size = job.Inputs.Sum(i => new FileInfo(i).Length);
            bool large = size >= ParallelThreshold;
            bool decimated = job.DecimationMm > 0;
            // Nube única sin reproyectar: con reducción de puntos conviene convertir en paralelo y unir al final (las
            // partes ya llegan reducidas y unirlas es rápido); sin reducción, ReCap directo es más rápido (medido).
            bool partsThenUnify = job.Unify && large && splittable && decimated;
            bool direct = job.LazoTo == null && !job.Reprojects &&
                          ((job.Unify && !partsThenUnify) || (!job.Unify && !large && job.Inputs.Count == 1 && !job.MergeParts));
            if (direct)
            {
                report.Strategy = job.Unify ? "ReCap directo en un solo proceso (sin reducción de puntos es lo más rápido)." : "ReCap directo (nube pequeña).";
                return await report.TimeAsync("Conversión con ReCap", () => RunAsync(decap, job, name, progress, 0, 1, cancel)).ConfigureAwait(false);
            }

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
                    await report.TimeAsync(job.LazoTo != null ? "Reproyectar a " + job.LazoTo.Name + " y preparar " + units.Count + " tramos" : "Preparar " + units.Count + " tramos",
                        async () => { await BuildTilesAsync(units, job, geoid, Path.GetDirectoryName(decap), value =>
                        {
                            if (progress != null) progress(0.3 * value, job.LazoTo != null ? "Reproyectando a " + job.LazoTo.Name + "…" : "Preparando tramos en paralelo…");
                        }, cancel).ConfigureAwait(false); return true; }).ConfigureAwait(false);
                }
                else units = job.Inputs.Select(i => new CloudTile { Input = i, Output = i, ScanName = Path.GetFileNameWithoutExtension(i), Count = PointCountOrZero(i) }).ToList();
                double start = built ? 0.3 : 0;

                // Sistema con el que ReCap debe etiquetar (y, en su caso, reproyectar) lo que recibe.
                string current = job.LazoTo != null ? (job.LazoTo.RecapKey != null && CoordinateLibrary.Find(job.LazoTo.RecapKey) != null ? job.LazoTo.RecapKey : null) : job.SourceCs;
                string target = job.LazoTo != null ? null : (job.Reprojects ? job.TargetCs : null);

                // 2a. Nube única sin reducción tras reproyectar con Lazo: ReCap la arma de una vez desde los tramos.
                if (job.Unify && target == null && !decimated)
                {
                    report.Strategy = "Lazo preparó los tramos en paralelo y ReCap armó la nube única en un solo proceso.";
                    PointCloudJob unify = new PointCloudJob
                    {
                        Inputs = units.Select(u => u.Output).ToList(), OutputFolder = job.OutputFolder, Name = name, Unify = true,
                        Unit = job.LazoTo != null ? 2 : job.Unit, SourceCs = current
                    };
                    return await report.TimeAsync("Armar la nube única con ReCap", () => RunAsync(decap, unify, name, progress, start, 1, cancel)).ConfigureAwait(false);
                }

                // 2b. Un ReCap por unidad, varios a la vez (con la reducción de puntos ya aplicada en cada parte).
                bool merge = job.Unify || job.MergeParts;
                double convertEnd = start + (merge ? 0.55 : 0.85) * (1 - start);
                string[] scans = await report.TimeAsync("Convertir " + units.Count + " partes en paralelo (" + Math.Min(Lanes, units.Count) + " ReCap a la vez)",
                    () => ConvertUnitsAsync(decap, units, job, current, target, temporary, report, (value, text) =>
                    {
                        if (progress != null) progress(start + (convertEnd - start) * value, text);
                    }, cancel)).ConfigureAwait(false);

                // 3. Unir las partes en un solo .rcs: es el resultado (nube única) o el único escaneo del proyecto.
                if (merge)
                {
                    string mergedFolder = job.Unify ? job.OutputFolder : Path.Combine(temporary, "unida");
                    string mergedName = job.Unify ? name : "unida";
                    PointCloudJob unify = new PointCloudJob { Inputs = scans.ToList(), OutputFolder = mergedFolder, Name = mergedName, Unify = true };
                    PointCloudResult merged = await report.TimeAsync("Unir las " + scans.Length + " partes en un solo .rcs", () => RunAsync(decap, unify, mergedName, (value, text) =>
                        { if (progress != null) progress(value, "Uniendo las partes en un solo .rcs…"); }, convertEnd, job.Unify ? 1 : 0.95, cancel)).ConfigureAwait(false);
                    if (job.Unify || !merged.Ok)
                    {
                        report.Strategy = "Partes convertidas en paralelo y unidas al final en una sola nube.";
                        return merged;
                    }
                    // El escaneo único se llama como el proyecto.
                    string single = Path.Combine(temporary, "unida", name + " nube.rcs");
                    File.Move(merged.Output, single);
                    scans = new[] { single };
                    report.Strategy = "Partes convertidas en paralelo, unidas en un solo escaneo y armadas en un proyecto.";
                }
                else report.Strategy = "Partes convertidas en paralelo y armadas en un proyecto (un escaneo por parte).";

                // 4. Montaje: los .rcs van a la carpeta «Support» del proyecto y ReCap arma el .rcp en segundos.
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
                PointCloudResult result = await report.TimeAsync("Armar el proyecto .rcp", () => RunAsync(decap, assemble, name, (value, text) =>
                    { if (progress != null) progress(value, "Armando el proyecto…"); }, merge ? 0.95 : convertEnd, 1, cancel)).ConfigureAwait(false);
                if (result.Ok) LabelProject(result.Output, target ?? current);
                return result;
            }
            finally
            {
                if (geoid != null) geoid.Dispose();
                try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); } catch { }
            }
        }

        private static long PointCountOrZero(string path)
        {
            try { return IsLas(path) || IsLaz(path) ? CloudReprojector.PointCount(path) : 0; } catch { return 0; }
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

        private static bool IsLicensing(string message)
        {
            return message != null && message.IndexOf("licens", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Un ReCap por unidad, hasta <see cref="Lanes"/> a la vez. Devuelve los .rcs que generaron.</summary>
        private static async Task<string[]> ConvertUnitsAsync(string decap, List<CloudTile> units, PointCloudJob job, string current, string target,
            string temporary, ConversionReport report, Action<double, string> progress, CancellationToken cancel)
        {
            string[] scans = new string[units.Count];
            double[] parts = new double[units.Count];
            int finished = 0;
            using (SemaphoreSlim lanes = new SemaphoreSlim(Lanes))
            using (SemaphoreSlim licensing = new SemaphoreSlim(1))
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
                        PointCloudResult result = null;
                        Stopwatch clock = Stopwatch.StartNew();
                        int attempts = 0;
                        // La licencia de ReCap no aguanta que varios procesos la pidan a la vez: arrancan de uno en uno
                        // (el siguiente espera a que el anterior ya esté convirtiendo) y, si aun así falla, se reintenta.
                        for (int attempt = 1; attempt <= 3; attempt++)
                        {
                            attempts = attempt;
                            if (Directory.Exists(folder)) try { Directory.Delete(folder, true); } catch { }
                            await licensing.WaitAsync(cancel).ConfigureAwait(false);
                            int released = 0;
                            Action release = () => { if (Interlocked.Exchange(ref released, 1) == 0) licensing.Release(); };
                            try
                            {
                                result = await RunAsync(decap, one, "u" + index, (value, text) =>
                                {
                                    lock (parts)
                                    {
                                        parts[index] = value;
                                        if (progress != null) progress(parts.Average(), "Convirtiendo " + units.Count + " partes a la vez (" + finished + " listas)…");
                                    }
                                }, 0, 1, cancel, release).ConfigureAwait(false);
                            }
                            finally { release(); }
                            if (result.Ok || !IsLicensing(result.Message) || cancel.IsCancellationRequested) break;
                            await Task.Delay(2000 * attempt, cancel).ConfigureAwait(false);
                        }
                        report.Part(unit.ScanName, unit.Count, clock.Elapsed, attempts, result.Ok);
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
            double from, double to, CancellationToken cancel, Action licensed = null)
        {
            // Avisa una sola vez cuando ReCap ya pasó la licencia y empezó a trabajar (o terminó).
            int signaled = 0;
            Action passed = () => { if (licensed != null && Interlocked.Exchange(ref signaled, 1) == 0) licensed(); };
            try
            {
                return await RunOnceAsync(decap, job, name, progress, from, to, cancel, passed).ConfigureAwait(false);
            }
            finally { passed(); }
        }

        private static async Task<PointCloudResult> RunOnceAsync(string decap, PointCloudJob job, string name, Action<double, string> progress,
            double from, double to, CancellationToken cancel, Action passed)
        {
            Directory.CreateDirectory(job.OutputFolder);
            int scans = job.Inputs.Count;
            List<string> errors = new List<string>();
            string reported = null;
            Action<double, string> report = (value, text) => { if (progress != null) progress(from + (to - from) * Math.Max(0, Math.Min(1, value)), text); };
            report(0, "Preparando…");
            int code = await RunAsync(decap, Arguments(job, name), line =>
            {
                if (line.StartsWith("Importing", StringComparison.OrdinalIgnoreCase) || line.StartsWith("[P]") || line.StartsWith("[E]") ||
                    line.StartsWith("Enabling", StringComparison.OrdinalIgnoreCase) || line.StartsWith("(")) passed();
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
