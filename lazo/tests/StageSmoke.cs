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
using Lazo;

// Dos personas comparten a la vez con el mismo visor: las dos pantallas llegan a una sola ventana en mosaico,
// se puede destacar una, y al cerrar una la otra sigue.
internal static class StageSmoke
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "stage-smoke";
        Directory.CreateDirectory(output);
        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int code = 1;
        app.Startup += async (s, e) =>
        {
            try { code = await Run(output) ? 0 : 1; }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex); code = 1; }
            app.Shutdown();
        };
        app.Run();
        return code;
    }

    private static bool Check(bool condition, string what)
    {
        Console.WriteLine((condition ? "OK    " : "FALLO ") + what);
        return condition;
    }

    private static async Task<bool> Until(Func<bool> condition, int milliseconds)
    {
        for (int waited = 0; waited < milliseconds; waited += 50)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private static Window Source(string title, Brush color, int left)
    {
        Window window = new Window
        {
            Title = title, Width = 420, Height = 280, Left = left, Top = 60, WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new Grid { Background = color, Children = { new TextBlock { Text = title, FontSize = 36, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } } }
        };
        window.Show();
        return window;
    }

    private static async Task<bool> Run(string output)
    {
        Theme.Load();
        IPAddress local = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address)
            .First(a => { byte[] b = a.GetAddressBytes(); return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31; });

        Window first = Source("Lazo prueba Ana", new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xB5)), 60);
        Window second = Source("Lazo prueba Beto", new SolidColorBrush(Color.FromRgb(0x2F, 0x9B, 0x5E)), 520);
        await Task.Delay(400);
        bool ok = true;
        int port = FreePort();
        using (NetworkEngine viewerSide = new NetworkEngine(Path.Combine(output, "inbox"), FreeUdp(), port, Path.Combine(output, "perfil-visor")))
        using (NetworkEngine ana = new NetworkEngine(Path.Combine(output, "inbox-a"), FreeUdp(), FreePort(), Path.Combine(output, "perfil-ana")))
        using (NetworkEngine beto = new NetworkEngine(Path.Combine(output, "inbox-b"), FreeUdp(), FreePort(), Path.Combine(output, "perfil-beto")))
        {
            ScreenStageWindow stage = new ScreenStageWindow { Width = 960, Height = 600, Left = 40, Top = 380, WindowStartupLocation = WindowStartupLocation.Manual };
            viewerSide.ScreenOffered += invite =>
            {
                TaskCompletionSource<IScreenSink> result = new TaskCompletionSource<IScreenSink>();
                Application.Current.Dispatcher.BeginInvoke((Action)(() =>
                {
                    ScreenViewerView view = new ScreenViewerView(invite.Sender, invite.Source);
                    stage.Add(view);
                    if (!stage.IsVisible) stage.Show();
                    result.TrySetResult(new ScreenViewer(view));
                }));
                return result.Task;
            };
            viewerSide.Start();
            Peer viewer = new Peer { Id = viewerSide.SelfId, Name = "Visor", Address = local, Port = port };
            ShareSource sourceA = new ShareSource { IsWindow = true, Handle = new System.Windows.Interop.WindowInteropHelper(first).Handle, Label = "Ana" };
            ShareSource sourceB = new ShareSource { IsWindow = true, Handle = new System.Windows.Interop.WindowInteropHelper(second).Handle, Label = "Beto" };
            ScreenShareSession shareA = new ScreenShareSession(ana, sourceA);
            ScreenShareSession shareB = new ScreenShareSession(beto, sourceB);
            shareA.Invite(viewer);
            shareA.Start();
            shareB.Invite(viewer);
            shareB.Start();

            ok &= Check(await Until(() => stage.Count == 2, 8000), "las dos pantallas llegan a la misma ventana");
            ok &= Check(await Until(() => stage.Views.All(v => v.Frame != null), 8000), "las dos reciben imagen a la vez");
            ok &= Check(stage.IsMosaic && stage.Views.All(v => v.Parent != null), "se ven en mosaico");
            await Task.Delay(1200);
            Save(stage, Path.Combine(output, "mosaico.png"));

            stage.SetMosaic(false);
            ok &= Check(!stage.IsMosaic && stage.Spotlight != null && stage.Views.All(v => v.Parent != null), "una destacada y la otra en la tira");
            await Task.Delay(600);
            Save(stage, Path.Combine(output, "destacada.png"));

            ScreenViewerView remaining = stage.Views[1];
            stage.Remove(stage.Views[0]);
            ok &= Check(stage.Count == 1 && stage.Views[0] == remaining && remaining.Parent != null && stage.IsVisible, "cerrar una deja la otra a la vista");

            shareA.Stop();
            shareB.Stop();
            await Task.Delay(600);
            stage.Close();
        }
        first.Close();
        second.Close();
        Console.WriteLine(ok ? "OK: varias pantallas en mosaico" : "FALLO en el mosaico");
        return ok;
    }

    private static void Save(Window window, string path)
    {
        FrameworkElement content = (FrameworkElement)window.Content;
        RenderTargetBitmap bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        PngBitmapEncoder png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream file = File.Create(path)) png.Save(file);
        Console.WriteLine("Captura: " + path);
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
