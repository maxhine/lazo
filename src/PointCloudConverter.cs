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
            foreach (string input in job.Inputs) args.Add(Quote(input));
            return string.Join(" ", args);
        }

        /// <summary>
        /// Convierte. <paramref name="progress"/> recibe la fracción hecha (0–1) y una línea de estado.
        /// DeCap termina con código 0 aunque falle, así que el resultado se decide por sus mensajes y por los archivos.
        /// </summary>
        public static async Task<PointCloudResult> ConvertAsync(string decap, PointCloudJob job, Action<double, string> progress, CancellationToken cancel)
        {
            string problem = Validate(job);
            if (problem != null) return new PointCloudResult { Message = problem };
            Directory.CreateDirectory(job.OutputFolder);
            string name = FreeName(job.OutputFolder, CleanName(job.Name));
            int scans = job.Inputs.Count;
            List<string> errors = new List<string>();
            string reported = null;
            Action<double, string> report = (value, text) => { if (progress != null) progress(Math.Max(0, Math.Min(1, value)), text); };
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
