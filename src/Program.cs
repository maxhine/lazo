using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Net;

namespace Lazo
{
    internal static class Program
    {
        private static byte[] PreviewImage()
        {
            using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(64, 48))
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
            using (MemoryStream output = new MemoryStream())
            {
                graphics.Clear(System.Drawing.Color.FromArgb(90, 90, 90));
                graphics.FillRectangle(System.Drawing.Brushes.White, 18, 10, 28, 22);
                bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Jpeg);
                return output.ToArray();
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            if (Array.IndexOf(args, "--preview-wine") >= 0) Theme.Override = ThemeKind.Wine;
            if (Array.IndexOf(args, "--preview-noglass") >= 0) Theme.NoGlassOverride = true;
            foreach (string arg in args)
                if (arg.StartsWith("--preview-theme=")) Theme.Override = Theme.ParseTheme(arg.Substring(16));
            foreach (string arg in args)
                if (arg.StartsWith("--preview-panel=")) MainWindow.PreviewPanel = arg.Substring(16);
            if (Array.IndexOf(args, "--preview-share") >= 0) MainWindow.PreviewPanel = "share";
            if (Array.IndexOf(args, "--preview-eyecare-alert") >= 0)
            {
                Theme.Load();
                Application alertApp = new Application();
                alertApp.Run(new EyeCareAlertWindow(EyeCareBreakType.MicroBreak, 20));
                return;
            }
            if (Array.IndexOf(args, "--preview-pointers") >= 0)
            {
                // Punteros colaborativos sobre un fondo, para revisar su aspecto.
                Theme.Load();
                PointerLayer pointers = new PointerLayer();
                AnnotationLayer strokes = new AnnotationLayer();
                System.Windows.Controls.Grid surface = new System.Windows.Controls.Grid { Background = Theme.Color("#EDEDED") };
                surface.Children.Add(strokes);
                surface.Children.Add(pointers);
                System.Windows.Window window = new System.Windows.Window { Title = "Lazo punteros", Width = 640, Height = 400, Content = surface };
                window.Loaded += (s, e) =>
                {
                    pointers.Move(1, "Bea", 0.55, 0.25, 0);
                    pointers.Move(2, "Pedro", 0.22, 0.62, 1);
                    pointers.Move(3, "Julián Garzón", 0.7, 0.6, 2);
                    // Un trazo láser rojo en curva, como si Bea lo estuviera trazando.
                    int n = 40;
                    ushort[] xs = new ushort[n], ys = new ushort[n];
                    for (int i = 0; i < n; i++)
                    {
                        double t = i / (double)(n - 1);
                        xs[i] = AnnotationOp.Norm(0.12 + 0.42 * t);
                        ys[i] = AnnotationOp.Norm(0.35 + 0.18 * Math.Sin(t * Math.PI * 1.6));
                    }
                    strokes.Commit(new AnnotationOp { Kind = AnnotationOp.Begin, Id = 7, Tool = AnnotationTool.Laser, Color = AnnotationLayer.Colors[0], Xs = xs, Ys = ys });
                };
                new Application().Run(window);
                return;
            }
            bool previewReceive = Array.IndexOf(args, "--preview-receive") >= 0 ||
                                  Array.IndexOf(args, "--preview-receive-glass") >= 0;
            if (previewReceive)
            {
                Theme.Load();
                if (Array.IndexOf(args, "--preview-receive-glass") >= 0) Theme.SetForPreview(ThemeKind.Glass);
                Offer offer = new Offer { Id = Guid.NewGuid(), Sender = "EQUIPO-OFICINA",
                    Address = IPAddress.Parse("192.168.1.12"), FileName = "foto.jpg", Size = 3429018,
                    Preview = PreviewImage() };
                Application receiveApp = new Application();
                receiveApp.Run(new ReceiveWindow(offer, accepted => { }));
                return;
            }
            bool previewStandard = Array.IndexOf(args, "--preview-standard") >= 0;
            bool previewDark = Array.IndexOf(args, "--preview-dark") >= 0;
            bool preview = previewStandard || previewDark || Array.IndexOf(args, "--preview-empty") >= 0 ||
                           Array.IndexOf(args, "--preview-settings") >= 0 ||
                           Array.IndexOf(args, "--preview") >= 0 ||
                           Array.IndexOf(args, "--preview-glass") >= 0 ||
                           Array.IndexOf(args, "--preview-live-search") >= 0;
            if (preview)
            {
                Application previewApp = new Application();
                previewApp.Run(new MainWindow(true, Array.IndexOf(args, "--preview-glass") >= 0,
                    Array.IndexOf(args, "--preview-live-search") >= 0, previewStandard, previewDark,
                    Array.IndexOf(args, "--preview-settings") >= 0,
                    Array.IndexOf(args, "--preview-empty") >= 0));
                return;
            }
            if (Startup.ShouldExitAtLogon(args)) return;
            bool first;
            using (Mutex instance = new Mutex(true, "Local\\Lazo.Transfer.App", out first))
            {
                if (!first)
                {
                    try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting("Local\\Lazo.Transfer.Show")) signal.Set(); }
                    catch { }
                    return;
                }
                using (EventWaitHandle signal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Lazo.Transfer.Show"))
                {
                    Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    MainWindow window = new MainWindow();
                    Thread listener = new Thread(() =>
                    {
                        while (true)
                        {
                            signal.WaitOne();
                            try { window.Dispatcher.BeginInvoke((Action)window.ActivateFromElsewhere); }
                            catch { return; }
                        }
                    });
                    listener.IsBackground = true;
                    listener.Start();
                    window.Show();
                    app.Run();
                }
            }
        }
    }
}
