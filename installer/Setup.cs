using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Lazo Installer")]
[assembly: AssemblyCompany("Jhon Andrew")]
[assembly: AssemblyVersion("0.4.13.0")]
[assembly: AssemblyFileVersion("0.4.13.0")]

namespace LazoInstaller
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool uninstall = Array.IndexOf(args, "/uninstall") >= 0;
            bool preview = Array.IndexOf(args, "/preview") >= 0;
            bool update = Array.IndexOf(args, "/update") >= 0;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (update)
            {
                try { InstallWork.Update(); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Lazo"); }
                return;
            }
            Application.Run(new SetupForm(uninstall, preview));
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly bool _uninstall;
        private readonly bool _preview;
        private readonly CheckBox _startup;
        private readonly CheckBox _desktop;
        private readonly Button _action;
        private readonly Button _cancel;
        private readonly Label _status;
        private readonly ProgressBar _progress;

        public SetupForm(bool uninstall, bool preview)
        {
            _uninstall = uninstall;
            _preview = preview;
            Text = uninstall ? "Desinstalar Lazo" : "Instalar Lazo";
            ClientSize = new Size(580, 340);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(247, 247, 247);
            Font = new Font("Segoe UI", 9F);

            Panel rail = new Panel { Left = 0, Top = 0, Width = 160, Height = 340,
                BackColor = Color.FromArgb(31, 31, 31) };
            Controls.Add(rail);
            rail.Controls.Add(new Label { Left = 24, Top = 34, Width = 120, Height = 35,
                Text = "LAZO", ForeColor = Color.White, Font = new Font("Consolas", 22F, FontStyle.Bold) });
            rail.Controls.Add(new Label { Left = 25, Top = 74, Width = 120, Height = 40,
                Text = "TRANSFERENCIA\nLOCAL", ForeColor = Color.FromArgb(175, 175, 175),
                Font = new Font("Consolas", 8F) });
            rail.Controls.Add(new Label { Left = 25, Top = 300, Width = 120, Height = 20,
                Text = "VERSIÓN 0.4.13", ForeColor = Color.FromArgb(175, 175, 175),
                Font = new Font("Consolas", 8F) });

            Controls.Add(new Label { Left = 193, Top = 31, Width = 355, Height = 35,
                Text = uninstall ? "Desinstalar Lazo" : "Instalar Lazo", ForeColor = Color.FromArgb(31, 31, 31),
                Font = new Font("Segoe UI", 19F, FontStyle.Bold) });
            Controls.Add(new Label { Left = 194, Top = 77, Width = 350, Height = 42,
                Text = uninstall ? "Quita la aplicación, sus accesos directos y las reglas de red." :
                    "Comparte archivos entre equipos Windows de la misma red privada.",
                ForeColor = Color.FromArgb(91, 91, 91) });

            Panel options = new Panel { Left = 194, Top = 130, Width = 350, Height = 98,
                BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(options);
            _startup = new CheckBox { Left = 15, Top = 15, Width = 315, Height = 23,
                Text = "Iniciar Lazo con Windows", Checked = true, Enabled = !uninstall };
            _desktop = new CheckBox { Left = 15, Top = 52, Width = 315, Height = 23,
                Text = "Crear acceso directo en el escritorio", Enabled = !uninstall };
            options.Controls.Add(_startup);
            options.Controls.Add(_desktop);
            if (uninstall) options.Visible = false;

            _status = new Label { Left = 194, Top = uninstall ? 150 : 240, Width = 350, Height = 34,
                Text = uninstall ? "La carpeta Descargas\\Lazo se conservará." :
                    "El firewall se limitará al perfil Privado y a la subred local.",
                ForeColor = Color.FromArgb(101, 101, 101), Font = new Font("Segoe UI", 8.5F) };
            Controls.Add(_status);
            _progress = new ProgressBar { Left = 194, Top = 278, Width = 350, Height = 3,
                Style = ProgressBarStyle.Marquee, Visible = false, MarqueeAnimationSpeed = 18 };
            Controls.Add(_progress);
            _cancel = new Button { Left = 331, Top = 292, Width = 102, Height = 32,
                Text = "Cancelar", FlatStyle = FlatStyle.Flat, BackColor = Color.White };
            _cancel.FlatAppearance.BorderColor = Color.FromArgb(185, 185, 185);
            _cancel.Click += (s, e) => Close();
            Controls.Add(_cancel);
            _action = new Button { Left = 441, Top = 292, Width = 103, Height = 32,
                Text = uninstall ? "Desinstalar" : "Instalar", FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(31, 31, 31), ForeColor = Color.White };
            _action.FlatAppearance.BorderSize = 0;
            _action.Click += OnAction;
            Controls.Add(_action);
            AcceptButton = _action;
        }

        private async void OnAction(object sender, EventArgs e)
        {
            if (_preview)
            {
                MessageBox.Show(this, "Vista previa del instalador. No se modificó este equipo.", "Lazo",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_uninstall && MessageBox.Show(this, "¿Desinstalar Lazo de este equipo?", "Lazo",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _action.Enabled = false;
            _cancel.Enabled = false;
            _progress.Visible = true;
            _status.Text = _uninstall ? "Desinstalando…" : "Instalando…";
            try
            {
                bool startup = _startup.Checked;
                bool desktop = _desktop.Checked;
                string warning = await System.Threading.Tasks.Task.Run(() =>
                    _uninstall ? InstallWork.Uninstall() : InstallWork.Install(startup, desktop));
                _progress.Visible = false;
                _status.Text = _uninstall ? "Lazo se desinstaló." : "Lazo está instalado. Ábrelo desde Inicio.";
                if (warning.Length > 0)
                    MessageBox.Show(this, warning, "Lazo · acción pendiente", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    MessageBox.Show(this, _uninstall ? "Lazo se desinstaló." :
                        "Lazo está instalado. Ábrelo desde el menú Inicio.", "Lazo",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                _progress.Visible = false;
                _status.Text = ex.Message;
                _action.Enabled = true;
                _cancel.Enabled = true;
                MessageBox.Show(this, ex.Message, "Lazo · error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal static class InstallWork
    {
        private const string AppName = "Lazo";
        private const string TcpRule = "Lazo transferencia TCP";
        private const string UdpRule = "Lazo descubrimiento UDP";
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Lazo";
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const int DelayUntilReboot = 0x4;

        private static string InstallDirectory
        {
            get
            {
                string programFiles = Environment.GetEnvironmentVariable("ProgramW6432");
                if (String.IsNullOrEmpty(programFiles)) programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Path.Combine(programFiles, AppName);
            }
        }

        private static RegistryKey Machine
        {
            get
            {
                return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                    Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
            }
        }

        public static void Update()
        {
            string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Lazo.lnk");
            bool startup = false;
            try
            {
                using (RegistryKey run = Machine.OpenSubKey(RunKey))
                    startup = run != null && run.GetValue(AppName) != null;
            }
            catch { }
            Install(startup, File.Exists(desktop), true);
            Process.Start(Path.Combine(InstallDirectory, "Lazo.exe"));
        }

        public static string Install(bool startup, bool desktop)
        {
            return Install(startup, desktop, false);
        }

        public static string Install(bool startup, bool desktop, bool forceClose)
        {
            string folder = InstallDirectory;
            string app = Path.Combine(folder, "Lazo.exe");
            string uninstall = Path.Combine(folder, "Uninstall.exe");
            EnsureClosed(forceClose);
            Directory.CreateDirectory(folder);
            string next = Path.Combine(folder, "Lazo.exe.new");
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Lazo.Payload"))
            {
                if (resource == null) throw new InvalidOperationException("El instalador no contiene Lazo.exe.");
                using (FileStream output = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None)) resource.CopyTo(output);
            }
            try
            {
                if (File.Exists(app)) File.Replace(next, app, null);
                else File.Move(next, app);
            }
            finally { if (File.Exists(next)) File.Delete(next); }
            File.Copy(Assembly.GetExecutingAssembly().Location, uninstall, true);
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Lazo.lnk"), app);
            string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Lazo.lnk");
            if (desktop) CreateShortcut(desktopLink, app);
            else if (File.Exists(desktopLink)) File.Delete(desktopLink);

            using (RegistryKey root = Machine)
            {
                using (RegistryKey entry = root.CreateSubKey(UninstallKey))
                {
                    entry.SetValue("DisplayName", "Lazo");
                    entry.SetValue("Publisher", "Jhon Andrew");
                    entry.SetValue("DisplayVersion", "0.4.13");
                    entry.SetValue("InstallLocation", folder);
                    entry.SetValue("DisplayIcon", app);
                    entry.SetValue("UninstallString", "\"" + uninstall + "\" /uninstall");
                    entry.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    entry.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
                using (RegistryKey run = root.CreateSubKey(RunKey))
                {
                    if (startup) run.SetValue(AppName, "\"" + app + "\"");
                    else run.DeleteValue(AppName, false);
                }
            }

            string warning = "";
            try
            {
                DeleteRule(TcpRule);
                DeleteRule(UdpRule);
                AddRule(TcpRule, app, "TCP", "48352");
                AddRule(UdpRule, app, "UDP", "48351");
            }
            catch (Exception ex) { warning = "La aplicación se instaló, pero el firewall no quedó configurado: " + ex.Message; }
            return warning;
        }

        public static string Uninstall()
        {
            string folder = InstallDirectory;
            string app = Path.Combine(folder, "Lazo.exe");
            string uninstaller = Path.Combine(folder, "Uninstall.exe");
            EnsureClosed(false);
            string warning = "";
            try { DeleteRule(TcpRule); DeleteRule(UdpRule); }
            catch (Exception ex) { warning = "No se pudieron retirar todas las reglas del firewall: " + ex.Message; }
            using (RegistryKey root = Machine)
            {
                root.DeleteSubKey(UninstallKey, false);
                using (RegistryKey run = root.OpenSubKey(RunKey, true))
                    if (run != null) run.DeleteValue(AppName, false);
            }
            string startLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Lazo.lnk");
            string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Lazo.lnk");
            if (File.Exists(startLink)) File.Delete(startLink);
            if (File.Exists(desktopLink)) File.Delete(desktopLink);
            if (File.Exists(app)) File.Delete(app);
            if (File.Exists(uninstaller))
            {
                if (!MoveFileEx(uninstaller, null, DelayUntilReboot))
                    warning += " El desinstalador se eliminará manualmente tras reiniciar.";
                if (!MoveFileEx(folder, null, DelayUntilReboot))
                    warning += " La carpeta de instalación puede permanecer vacía hasta que se elimine manualmente.";
            }
            else if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0) Directory.Delete(folder);
            return warning.Trim();
        }

        private static void EnsureClosed(bool force)
        {
            for (int attempt = 0; attempt < (force ? 24 : 1); attempt++)
            {
                bool busy = false;
                foreach (Process process in Process.GetProcessesByName("Lazo"))
                {
                    try
                    {
                        if (process.Id == Process.GetCurrentProcess().Id) continue;
                        busy = true;
                        if (force && attempt >= 8) process.Kill();
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    finally { process.Dispose(); }
                }
                if (!busy) return;
                if (!force) throw new InvalidOperationException("Cierra todas las ventanas de Lazo desde la bandeja y vuelve a intentarlo.");
                System.Threading.Thread.Sleep(250);
            }
        }

        private static void AddRule(string name, string program, string protocol, string port)
        {
            RunNetsh("advfirewall firewall add rule name=\"" + name + "\" dir=in action=allow " +
                "program=\"" + program + "\" protocol=" + protocol + " localport=" + port +
                " profile=private remoteip=localsubnet");
        }

        private static void DeleteRule(string name)
        {
            RunNetsh("advfirewall firewall delete rule name=\"" + name + "\"", false);
        }

        private static void RunNetsh(string arguments, bool required = true)
        {
            ProcessStartInfo start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (required && process.ExitCode != 0) throw new InvalidOperationException((error + " " + output).Trim());
            }
        }

        private static void CreateShortcut(string path, string target)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("Windows Script Host no está disponible para crear accesos directos.");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = null;
            try
            {
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type type = shortcut.GetType();
                type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(target) });
                type.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Transferencia local de archivos" });
                type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, new object[0]);
            }
            finally
            {
                if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
                Marshal.FinalReleaseComObject(shell);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existing, string replacement, int flags);
    }
}
