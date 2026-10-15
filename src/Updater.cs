using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Lazo
{
    internal static class Updater
    {
        public const string Repo = "maxhine/lazo";
        /// <summary>
        /// Clave pública P-256 (X || Y) que firma las versiones. La privada no está en el repositorio: solo en el
        /// equipo que publica (~/.lazo/release-signing-key.pem). Cada instalador lleva al lado su firma «.sig».
        /// </summary>
        internal const string ReleaseKey = "8a3ce980b63d00c676ebe200b094a90f5c8d78a4614bf7b99fc0560bd7e505325a9f09929f4adaaa7fdcfada9cbfcb929737b3595f69175aed94c0485367df89";
        private static readonly Version Current = Assembly.GetExecutingAssembly().GetName().Version;

        public static bool Enabled { get; private set; }

        public static void Load()
        {
            try { Enabled = File.ReadAllText(Path()).Trim() != "0"; }
            catch { Enabled = true; }
        }

        public static void Set(bool enabled, bool persist)
        {
            Enabled = enabled;
            if (!persist) return;
            try
            {
                string path = Path();
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                File.WriteAllText(path, enabled ? "1" : "0");
            }
            catch { }
        }

        public static void CheckInBackground(Action<string> report, Action shutdown)
        {
            if (!Enabled) return;
            Check(report, shutdown);
        }

        public static void Check(Action<string> report, Action shutdown)
        {
            Task.Run(() =>
            {
                try
                {
                    report("Buscando actualizaciones…");
                    string setup = DownloadNewer();
                    if (setup == null)
                    {
                        report("Lazo está actualizado.");
                        return;
                    }
                    report("Instalando actualización…");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = setup,
                        Arguments = "/update",
                        UseShellExecute = true
                    });
                    shutdown();
                }
                catch (UpdateRejectedException ex)
                {
                    report(ex.Message);
                }
                catch
                {
                    report("No se pudo comprobar.");
                }
            });
        }

        private static string DownloadNewer()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            string json;
            using (WebClient client = Client())
                json = client.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
            Match tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9]+\\.[0-9]+\\.[0-9]+)\"");
            Version remote;
            if (!tag.Success || !Version.TryParse(tag.Groups[1].Value, out remote) || remote <= Current) return null;
            Match asset = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(https:[^\"]*Lazo-Setup-[^\"]+\\.exe)\"");
            if (!asset.Success) return null;
            string url = asset.Groups[1].Value.Replace("\\u0026", "&");
            if (!Allowed(url)) return null;
            Match signature = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(https:[^\"]*Lazo-Setup-[^\"]+\\.exe\\.sig)\"");
            if (!signature.Success) throw new UpdateRejectedException("La actualización no está firmada; no se instaló.");
            string sigUrl = signature.Groups[1].Value.Replace("\\u0026", "&");
            if (!Allowed(sigUrl)) return null;
            // Carpeta propia: otro programa no puede cambiar el instalador entre la comprobación y la ejecución.
            string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Lazo-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string file = System.IO.Path.Combine(folder, "Lazo-Setup-" + remote + ".exe");
            string sig;
            using (WebClient client = Client())
            {
                client.DownloadFile(url, file);
                sig = client.DownloadString(sigUrl);
            }
            if (!Verify(file, sig))
            {
                try { File.Delete(file); } catch { }
                throw new UpdateRejectedException("La firma de la actualización no es válida; no se instaló.");
            }
            return file;
        }

        /// <summary>Firma ECDSA P-256 (DER en base64, como la produce openssl) del SHA-256 del archivo.</summary>
        internal static bool Verify(string file, string signatureBase64)
        {
            try
            {
                byte[] der = Convert.FromBase64String(signatureBase64.Trim());
                byte[] raw = LazoCrypto.DerToP1363(der);
                byte[] hash;
                using (FileStream input = File.OpenRead(file)) hash = System.Security.Cryptography.SHA256.Create().ComputeHash(input);
                return raw != null && LazoCrypto.Verify(LazoCrypto.FromHex(ReleaseKey), hash, raw);
            }
            catch { return false; }
        }

        private sealed class UpdateRejectedException : Exception
        {
            public UpdateRejectedException(string message) : base(message) { }
        }

        private static WebClient Client()
        {
            WebClient client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] = "Lazo";
            client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return client;
        }

        private static bool Allowed(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != "https") return false;
            string host = uri.Host;
            return host == "github.com" || host == "objects.githubusercontent.com" ||
                   host == "release-assets.githubusercontent.com" || host.EndsWith(".githubusercontent.com");
        }

        private static string Path()
        {
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "updates.txt");
        }
    }
}
