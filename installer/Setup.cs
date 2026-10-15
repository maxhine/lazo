using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("Lazo Installer")]
[assembly: AssemblyCompany("Jhon Andrew")]
[assembly: AssemblyVersion("0.5.2.0")]
[assembly: AssemblyFileVersion("0.5.2.0")]

namespace LazoInstaller
{
    internal static class Program
    {
        public const string Version = "0.5.2";

        [STAThread]
        private static void Main(string[] args)
        {
            bool uninstall = Array.IndexOf(args, "/uninstall") >= 0;
            bool preview = Array.IndexOf(args, "/preview") >= 0;
            bool update = Array.IndexOf(args, "/update") >= 0;
            if (update)
            {
                // Actualización desde Lazo: sin ventana; al terminar, Lazo vuelve a abrirse solo.
                try { InstallWork.Update(); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Lazo"); }
                return;
            }
            new Application { ShutdownMode = ShutdownMode.OnMainWindowClose }.Run(new SetupWindow(uninstall, preview));
        }
    }

    /// <summary>Colores y letra del tema Oscuro de Lazo (el predeterminado), para que el instalador se vea igual.</summary>
    internal static class Look
    {
        public static readonly FontFamily Font = new FontFamily("Bahnschrift, Segoe UI");
        /// <summary>En Oscuro, lo principal va en píldora clara con texto oscuro, como «Enviar archivos».</summary>
        public static readonly Color Accent = Color.FromRgb(0xE6, 0xE6, 0xE6);
        public static readonly Brush OnAccent = Solid("#161616");
        public static readonly Brush Ink = Solid("#E6E6E6");
        public static readonly Brush Muted = Solid("#8E8E8E");
        public static readonly Brush Soft = Solid("#14FFFFFF");
        public static readonly Brush Line = Solid("#3A3A3A");
        public static readonly Brush Danger = Solid("#E5484D");

        public static Brush Solid(string hex)
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        public static TextBlock Text(string text, double size, Brush color, FontWeight weight)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = color, FontWeight = weight, FontFamily = Font, TextWrapping = TextWrapping.Wrap };
        }

        /// <summary>Botón en forma de píldora, como los de Lazo.</summary>
        public static Button Pill(string text, Brush background, Brush foreground, Brush border)
        {
            Button button = new Button
            {
                Content = text, FontFamily = Font, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = foreground, Background = background, BorderBrush = border, BorderThickness = new Thickness(border == null ? 0 : 1),
                MinWidth = 110, Height = 38, Padding = new Thickness(18, 0, 18, 0), Cursor = Cursors.Hand
            };
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory face = new FrameworkElementFactory(typeof(Border));
            face.Name = "face";
            face.SetValue(Border.CornerRadiusProperty, new CornerRadius(19));
            face.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            face.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            face.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            face.AppendChild(content);
            template.VisualTree = face;
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.88));
            template.Triggers.Add(hover);
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            template.Triggers.Add(disabled);
            button.Template = template;
            return button;
        }

        /// <summary>Ícono de Lazo: dos eslabones, en trazo redondeado.</summary>
        public static Path Logo(double size, Brush color)
        {
            return new Path
            {
                Data = Geometry.Parse("M10 13.5a4 4 0 0 0 5.66.34l2.84-2.84a4 4 0 0 0-5.66-5.66l-1.2 1.2 M14 10.5a4 4 0 0 0-5.66-.34l-2.84 2.84a4 4 0 0 0 5.66 5.66l1.2-1.2"),
                Stroke = color, StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, Width = size, Height = size, Stretch = Stretch.Uniform
            };
        }
    }

    /// <summary>Interruptor de Lazo: etiqueta, explicación y una pastilla que se desliza.</summary>
    internal sealed class Switch : Border
    {
        private readonly Border _track;
        private readonly Border _knob;
        private bool _on;
        public bool IsOn { get { return _on; } }

        public Switch(string title, string detail, bool on)
        {
            _on = on;
            Cursor = Cursors.Hand;
            Background = Brushes.Transparent;
            Padding = new Thickness(14, 10, 14, 10);
            CornerRadius = new CornerRadius(14);
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel words = new StackPanel();
            words.Children.Add(Look.Text(title, 13.5, Look.Ink, FontWeights.SemiBold));
            TextBlock small = Look.Text(detail, 11.5, Look.Muted, FontWeights.Normal);
            small.Margin = new Thickness(0, 2, 0, 0);
            words.Children.Add(small);
            row.Children.Add(words);
            _knob = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(3, 0, 0, 0) };
            _knob.RenderTransform = new TranslateTransform();
            _track = new Border { Width = 42, Height = 24, CornerRadius = new CornerRadius(12), Child = _knob, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            Grid.SetColumn(_track, 1);
            row.Children.Add(_track);
            Child = row;
            MouseEnter += (s, e) => Background = Look.Soft;
            MouseLeave += (s, e) => Background = Brushes.Transparent;
            MouseLeftButtonUp += (s, e) => { if (IsEnabled) Set(!_on, true); };
            Set(on, false);
        }

        private void Set(bool on, bool animate)
        {
            _on = on;
            _track.Background = on ? new SolidColorBrush(Look.Accent) : Look.Solid("#3DFFFFFF");
            _knob.Background = on ? Look.OnAccent : Look.Solid("#D0D0D0");
            double to = on ? 18 : 0;
            TranslateTransform move = (TranslateTransform)_knob.RenderTransform;
            if (animate) move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() });
            else move.X = to;
        }
    }

    internal sealed class SetupWindow : Window
    {
        private readonly bool _uninstall;
        private readonly bool _preview;
        private readonly Switch _startup;
        private readonly Switch _desktop;
        private readonly StackPanel _options;
        private readonly Button _action;
        private readonly Button _cancel;
        private readonly TextBlock _title;
        private readonly TextBlock _lead;
        private readonly TextBlock _status;
        private readonly Border _progress;
        private readonly Border _badge;

        public SetupWindow(bool uninstall, bool preview)
        {
            _uninstall = uninstall;
            _preview = preview;
            Title = uninstall ? "Desinstalar Lazo" : "Instalar Lazo";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Look.Font;
            try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(Assembly.GetExecutingAssembly().Location)); } catch { }

            StackPanel body = new StackPanel { Margin = new Thickness(30, 26, 30, 26) };

            // Cabecera: ícono, nombre, versión y cerrar.
            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _badge = new Border
            {
                Width = 52, Height = 52, CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(Look.Accent),
                Child = Look.Logo(28, Look.OnAccent)
            };
            head.Children.Add(_badge);
            StackPanel name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            name.Children.Add(Look.Text("Lazo", 26, Look.Ink, FontWeights.SemiBold));
            TextBlock version = Look.Text("Versión " + Program.Version + " · Transferencia local", 12, Look.Muted, FontWeights.Normal);
            name.Children.Add(version);
            Grid.SetColumn(name, 1);
            head.Children.Add(name);
            Button close = Look.Pill("✕", Brushes.Transparent, Look.Muted, null);
            close.MinWidth = 36;
            close.Width = 36;
            close.Height = 36;
            close.Padding = new Thickness(0);
            close.FontSize = 13;
            close.VerticalAlignment = VerticalAlignment.Top;
            close.ToolTip = "Cerrar";
            close.Click += (s, e) => { if (_cancel.IsEnabled) Close(); };
            Grid.SetColumn(close, 2);
            head.Children.Add(close);
            body.Children.Add(head);

            _title = Look.Text(uninstall ? "Desinstalar Lazo" : "Instalar Lazo", 21, Look.Ink, FontWeights.SemiBold);
            _title.Margin = new Thickness(0, 26, 0, 6);
            body.Children.Add(_title);
            _lead = Look.Text(uninstall
                ? "Se quitan la aplicación, sus accesos directos y las reglas de red. Lo que recibiste se conserva."
                : "Comparte archivos, pantalla y chat con tu equipo en la misma red, cifrado de extremo a extremo.",
                13, Look.Muted, FontWeights.Normal);
            body.Children.Add(_lead);

            _options = new StackPanel { Margin = new Thickness(-14, 16, -14, 0) };
            _startup = new Switch("Abrir Lazo al iniciar Windows", "Queda en la bandeja, listo para recibir.", true);
            _desktop = new Switch("Acceso directo en el escritorio", "Además del que se crea en el menú Inicio.", false);
            _options.Children.Add(_startup);
            _options.Children.Add(_desktop);
            if (uninstall) _options.Visibility = Visibility.Collapsed;
            body.Children.Add(_options);

            _status = Look.Text(uninstall ? "La carpeta donde recibes archivos no se borra."
                : "El firewall se abre solo para la red privada y la subred local. Al terminar, Lazo se abre solo.",
                11.5, Look.Muted, FontWeights.Normal);
            _status.Margin = new Thickness(0, 16, 0, 0);
            body.Children.Add(_status);

            // Barra de progreso fina con el acento, que recorre de lado a lado mientras trabaja.
            Border track = new Border { Height = 3, CornerRadius = new CornerRadius(2), Background = Look.Soft, Margin = new Thickness(0, 14, 0, 0), ClipToBounds = true, Visibility = Visibility.Hidden };
            _progress = track;
            Border runner = new Border { Width = 140, Height = 3, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Look.Accent), HorizontalAlignment = HorizontalAlignment.Left };
            runner.RenderTransform = new TranslateTransform();
            track.Child = runner;
            body.Children.Add(track);

            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            _cancel = Look.Pill("Cancelar", Look.Soft, Look.Ink, Look.Line);
            _cancel.Click += (s, e) => Close();
            buttons.Children.Add(_cancel);
            _action = Look.Pill(uninstall ? "Desinstalar" : "Instalar", uninstall ? Look.Danger : new SolidColorBrush(Look.Accent), uninstall ? Brushes.White : Look.OnAccent, null);
            _action.Margin = new Thickness(10, 0, 0, 0);
            _action.IsDefault = true;
            _action.Click += OnAction;
            buttons.Children.Add(_action);
            body.Children.Add(buttons);

            Border shell = new Border
            {
                Margin = new Thickness(14),
                CornerRadius = new CornerRadius(26),
                BorderBrush = Look.Line,
                BorderThickness = new Thickness(1),
                Background = new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0x26, 0x26, 0x26), 0),
                    new GradientStop(Color.FromRgb(0x1C, 0x1C, 0x1C), 0.55),
                    new GradientStop(Color.FromRgb(0x14, 0x14, 0x14), 1)
                }, new Point(0, 0), new Point(1, 1)),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.45 },
                Child = body
            };
            shell.MouseLeftButtonDown += (s, e) => { if (e.OriginalSource is Border || e.OriginalSource is StackPanel || e.OriginalSource is TextBlock) try { DragMove(); } catch { } };
            Content = shell;
            KeyDown += (s, e) => { if (e.Key == Key.Escape && _cancel.IsEnabled) Close(); };

            // Entrada suave, como las ventanas de Lazo.
            Opacity = 0;
            Loaded += (s, e) => BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            DoubleAnimation run = new DoubleAnimation(-140, 520, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            ((TranslateTransform)runner.RenderTransform).BeginAnimation(TranslateTransform.XProperty, run);
        }

        private async void OnAction(object sender, RoutedEventArgs e)
        {
            if (_preview)
            {
                _status.Text = "Vista previa del instalador: no se modificó este equipo.";
                return;
            }
            if (_uninstall && _action.Content as string == "Desinstalar")
            {
                // Confirmación en la misma ventana: el botón cambia y hay que pulsarlo otra vez.
                _action.Content = "Sí, desinstalar";
                _status.Text = "¿Seguro? Pulsa otra vez para desinstalar Lazo de este equipo.";
                _status.Foreground = Look.Ink;
                return;
            }
            _action.IsEnabled = false;
            _cancel.IsEnabled = false;
            _startup.IsEnabled = _desktop.IsEnabled = false;
            _progress.Visibility = Visibility.Visible;
            _status.Foreground = Look.Muted;
            _status.Text = _uninstall ? "Desinstalando…" : "Instalando…";
            try
            {
                bool startup = _startup.IsOn;
                bool desktop = _desktop.IsOn;
                string warning = await Task.Run(() => _uninstall ? InstallWork.Uninstall() : InstallWork.Install(startup, desktop));
                _progress.Visibility = Visibility.Hidden;
                Done(warning);
            }
            catch (Exception ex)
            {
                _progress.Visibility = Visibility.Hidden;
                _status.Text = ex.Message;
                _status.Foreground = Look.Danger;
                _action.IsEnabled = true;
                _cancel.IsEnabled = true;
                _startup.IsEnabled = _desktop.IsEnabled = true;
            }
        }

        /// <summary>Terminado: se dice en la ventana y, al instalar, Lazo se abre solo y el instalador se cierra.</summary>
        private void Done(string warning)
        {
            _badge.Background = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59));
            _badge.Child = new Path
            {
                Data = Geometry.Parse("M5 12.5l4.5 4.5L19 7"), Stroke = Brushes.White, StrokeThickness = 2.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Width = 26, Height = 26, Stretch = Stretch.Uniform
            };
            _options.Visibility = Visibility.Collapsed;
            _cancel.Visibility = Visibility.Collapsed;
            _action.IsEnabled = true;
            _action.Click -= OnAction;
            _action.Click += (s, e) => Close();
            if (_uninstall)
            {
                _title.Text = "Lazo se desinstaló";
                _lead.Text = "Gracias por usarlo. Lo que recibiste sigue en su carpeta.";
                _status.Text = warning;
                _status.Foreground = warning.Length > 0 ? Look.Ink : Look.Muted;
                _action.Content = "Cerrar";
                _action.Background = new SolidColorBrush(Look.Accent);
                _action.Foreground = Look.OnAccent;
                return;
            }
            _title.Text = "Lazo está listo";
            _lead.Text = "Se abre en un momento. Pulsa dos veces Alt para traerlo cuando quieras.";
            _action.Content = "Abrir Lazo";
            if (warning.Length > 0)
            {
                // Hay algo que contar (el firewall): se espera a que la persona lo lea.
                _status.Text = warning;
                _status.Foreground = Look.Ink;
                _action.Click += (s, e) => InstallWork.LaunchAsUser();
                return;
            }
            _status.Text = "Abriendo Lazo…";
            DispatcherTimer later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
            later.Tick += (s, e) =>
            {
                later.Stop();
                InstallWork.LaunchAsUser();
                Close();
            };
            later.Start();
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
                return System.IO.Path.Combine(programFiles, AppName);
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
            string desktop = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Lazo.lnk");
            bool startup = false;
            try
            {
                using (RegistryKey run = Machine.OpenSubKey(RunKey))
                    startup = run != null && run.GetValue(AppName) != null;
            }
            catch { }
            Install(startup, File.Exists(desktop), true);
            LaunchAsUser();
        }

        /// <summary>
        /// Abre Lazo como la persona que usa el equipo, no como administrador: el instalador corre elevado, y una
        /// app elevada no recibe archivos arrastrados desde el Explorador. Se le pide al Explorador que lo abra.
        /// </summary>
        public static void LaunchAsUser()
        {
            string app = System.IO.Path.Combine(InstallDirectory, "Lazo.exe");
            try
            {
                string explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                Process.Start(new ProcessStartInfo(explorer, "\"" + app + "\"") { UseShellExecute = false });
            }
            catch
            {
                try { Process.Start(app); } catch { }
            }
        }

        public static string Install(bool startup, bool desktop)
        {
            return Install(startup, desktop, true);
        }

        public static string Install(bool startup, bool desktop, bool forceClose)
        {
            string folder = InstallDirectory;
            string app = System.IO.Path.Combine(folder, "Lazo.exe");
            string uninstall = System.IO.Path.Combine(folder, "Uninstall.exe");
            EnsureClosed(forceClose);
            Directory.CreateDirectory(folder);
            string next = System.IO.Path.Combine(folder, "Lazo.exe.new");
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
            CreateShortcut(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Lazo.lnk"), app);
            string desktopLink = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Lazo.lnk");
            if (desktop) CreateShortcut(desktopLink, app);
            else if (File.Exists(desktopLink)) File.Delete(desktopLink);

            using (RegistryKey root = Machine)
            {
                using (RegistryKey entry = root.CreateSubKey(UninstallKey))
                {
                    entry.SetValue("DisplayName", "Lazo");
                    entry.SetValue("Publisher", "Jhon Andrew");
                    entry.SetValue("DisplayVersion", Program.Version);
                    entry.SetValue("InstallLocation", folder);
                    entry.SetValue("DisplayIcon", app);
                    entry.SetValue("UninstallString", "\"" + uninstall + "\" /uninstall");
                    entry.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    entry.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
                using (RegistryKey run = root.CreateSubKey(RunKey))
                {
                    if (startup) run.SetValue(AppName, "\"" + app + "\" --startup");
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
            catch (Exception ex) { warning = "Lazo se instaló, pero el firewall no quedó configurado: " + ex.Message; }
            return warning;
        }

        public static string Uninstall()
        {
            string folder = InstallDirectory;
            string app = System.IO.Path.Combine(folder, "Lazo.exe");
            string uninstaller = System.IO.Path.Combine(folder, "Uninstall.exe");
            EnsureClosed(true);
            string warning = "";
            try { DeleteRule(TcpRule); DeleteRule(UdpRule); }
            catch (Exception ex) { warning = "No se pudieron retirar todas las reglas del firewall: " + ex.Message; }
            using (RegistryKey root = Machine)
            {
                root.DeleteSubKey(UninstallKey, false);
                using (RegistryKey run = root.OpenSubKey(RunKey, true))
                    if (run != null) run.DeleteValue(AppName, false);
            }
            string startLink = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Lazo.lnk");
            string desktopLink = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Lazo.lnk");
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
                        if (force && attempt >= 4) { process.Kill(); process.WaitForExit(3000); }
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
            ProcessStartInfo start = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments)
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
                type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { System.IO.Path.GetDirectoryName(target) });
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
