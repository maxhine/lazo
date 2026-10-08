using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lazo;

// Prueba de extremo a extremo: captura real, envío por TCP a una IP privada propia, decodificación y pintado.
internal static class ScreenSmoke
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "screen-smoke";
        string mode = args.Length > 1 ? args[1] : "screen";
        Directory.CreateDirectory(output);
        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int code = 1;
        app.Startup += async (s, e) =>
        {
            try { code = await Run(output, mode) ? 0 : 1; }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex); code = 1; }
            app.Shutdown();
        };
        app.Run();
        return code;
    }

    private static async Task<bool> Run(string output, string mode)
    {
        Theme.Load();
        IPAddress local = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address)
            .First(a => { byte[] b = a.GetAddressBytes(); return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31; });
        int port = FreePort();
        Window animated = null;
        ShareSource source;
        if (mode == "window")
        {
            // Ventana de prueba con una animación continua, compartida aunque quede detrás de otras.
            Border spinner = new Border { Width = 120, Height = 120, Background = Brushes.OrangeRed, RenderTransform = new RotateTransform(), RenderTransformOrigin = new Point(.5, .5) };
            animated = new Window { Title = "Lazo prueba ventana", Width = 640, Height = 420, Content = new Grid { Background = Brushes.Beige, Children = { spinner, new TextBlock { Text = "Ventana compartida", FontSize = 28, Margin = new Thickness(20) } } } };
            animated.Show();
            ((RotateTransform)spinner.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(2)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            await Task.Delay(400);
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(animated).Handle;
            source = new ShareSource { IsWindow = true, Handle = hwnd, Label = "Lazo prueba ventana" };
        }
        else source = ScreenSources.Screens()[0];
        Console.WriteLine("Fuente: " + source.Label + " " + source.Detail);

        using (NetworkEngine viewerSide = new NetworkEngine(Path.Combine(output, "inbox"), FreeUdp(), port))
        using (NetworkEngine sharer = new NetworkEngine(Path.Combine(output, "inbox2"), FreeUdp(), FreePort()))
        {
            ScreenViewerWindow window = null;
            viewerSide.ScreenOffered += invite =>
            {
                TaskCompletionSource<IScreenSink> result = new TaskCompletionSource<IScreenSink>();
                Application.Current.Dispatcher.BeginInvoke((Action)(() =>
                {
                    window = new ScreenViewerWindow(invite.Sender, invite.Source) { Width = 800, Height = 500, Left = 40, Top = 40, WindowStartupLocation = WindowStartupLocation.Manual };
                    window.Show();
                    result.TrySetResult(new ScreenViewer(window));
                }));
                return result.Task;
            };
            viewerSide.Start();
            ScreenShareSession session = new ScreenShareSession(sharer, source);
            session.Notice += text => Console.WriteLine("Aviso: " + text);
            bool ended = false;
            session.Ended += () => ended = true;
            session.Invite(new Peer { Id = Guid.NewGuid(), Name = "Visor", Address = local, Port = port });
            session.Start();
            double best = 0;
            for (int second = 1; second <= 6; second++)
            {
                await Task.Delay(1000);
                Console.WriteLine(string.Format("t={0}s  emisor {1:0} fps · {2:0.0} Mb/s · cod {3:0.0} ms · cap {5:0.0} ms  |  visor {4:0} fps",
                    second, session.Fps, session.Mbps, session.WorkMs, window == null ? 0 : window.Fps, session.CaptureMs));
                if (window != null) best = Math.Max(best, window.Fps);
            }
            bool painted = window != null && window.Frame != null;
            if (painted && mode == "window")
            {
                PngBitmapEncoder png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(window.Frame));
                using (FileStream file = File.Create(Path.Combine(output, "visor-" + mode + ".png"))) png.Save(file);
                Console.WriteLine("Fotograma recibido: " + window.Frame.PixelWidth + " × " + window.Frame.PixelHeight);
            }
            session.Stop();
            for (int i = 0; i < 40 && !ended; i++) await Task.Delay(50);
            if (window != null) window.Close();
            if (animated != null) animated.Close();
            bool ok = painted && best >= 10 && ended;
            Console.WriteLine(ok ? "OK: pantalla compartida de extremo a extremo" : "FALLO: painted=" + painted + " fps=" + best + " ended=" + ended);
            return ok;
        }
    }

    private static int FreePort()
    {
        TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static int FreeUdp()
    {
        using (UdpClient probe = new UdpClient(0)) return ((IPEndPoint)probe.Client.LocalEndPoint).Port;
    }
}
