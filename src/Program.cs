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
            if (Array.IndexOf(args, "--preview-eyecare-alert") >= 0)
            {
                Theme.Load();
                Application alertApp = new Application();
                alertApp.Run(new EyeCareAlertWindow(EyeCareBreakType.MicroBreak, 20));
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
