using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lazo;

// Prueba de extremo a extremo del canal de retorno de la pantalla compartida, sin mover el mouse real:
// saludo, permiso para dibujar y para controlar, trazos en ambos sentidos, quitar permisos,
// y que un visor de una versión anterior (que no saluda) solo reciba los paquetes de siempre.
internal static class ControlSmoke
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "control-smoke";
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

    private static async Task<bool> Run(string output)
    {
        Theme.Load();
        IPAddress local = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address)
            .First(a => { byte[] b = a.GetAddressBytes(); return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31; });

        Window shared = new Window { Title = "Lazo prueba control", Width = 520, Height = 360, Content = new Grid { Background = Brushes.Beige } };
        shared.Show();
        await Task.Delay(300);
        ShareSource source = new ShareSource { IsWindow = true, Handle = new System.Windows.Interop.WindowInteropHelper(shared).Handle, Label = "Lazo prueba control" };

        bool ok = true;

        // Atajos de emergencia: solo Ctrl + Alt izquierdo físicos; ni la entrada inyectada ni AltGr (@) los activan.
        Hotkeys physical = new Hotkeys();
        physical.Feed(0xA2, 0x1D, 0, true);
        physical.Feed(0xA4, 0x38, 0x20, true);
        ok &= Check(physical.Feed(0x51, 0x10, 0x20, true) == 'Q', "Ctrl+Alt+Q desde el teclado físico activa el atajo");
        physical.Feed(0x51, 0x10, 0x80, false);
        physical.Feed(0xA4, 0x38, 0x80, false);
        ok &= Check(physical.Feed(0x51, 0x10, 0, true) == '\0', "al soltar Alt ya no hay atajo");
        Hotkeys injected = new Hotkeys();
        injected.Feed(0xA2, 0x1D, 0x10, true);
        injected.Feed(0xA4, 0x38, 0x30, true);
        ok &= Check(injected.Feed(0x51, 0x10, 0x30, true) == '\0', "quien controla a distancia no puede usar los atajos");
        Hotkeys mixed = new Hotkeys();
        mixed.Feed(0xA2, 0x1D, 0x10, true);
        mixed.Feed(0xA4, 0x38, 0x20, true);
        ok &= Check(mixed.Feed(0x51, 0x10, 0x20, true) == '\0', "un Ctrl inyectado no completa el atajo");
        Hotkeys altGr = new Hotkeys();
        altGr.Feed(0xA2, 0x21D, 0x20, true);
        altGr.Feed(0xA5, 0x38, 0x21, true);
        ok &= Check(altGr.Feed(0x51, 0x10, 0x20, true) == '\0', "AltGr+Q (@) no activa el atajo");

        int port = FreePort();
        using (NetworkEngine viewerSide = new NetworkEngine(Path.Combine(output, "inbox"), FreeUdp(), port, Path.Combine(output, "perfil-visor")))
        using (NetworkEngine sharer = new NetworkEngine(Path.Combine(output, "inbox2"), FreeUdp(), FreePort(), Path.Combine(output, "perfil-emisor")))
        {
            // Los dos confirman el código de emparejamiento automáticamente.
            viewerSide.PairingPrompt += request => Task.FromResult(true);
            sharer.PairingPrompt += request => Task.FromResult(true);
            ScreenViewerWindow window = null;
            viewerSide.ScreenOffered += invite =>
            {
                TaskCompletionSource<IScreenSink> result = new TaskCompletionSource<IScreenSink>();
                Application.Current.Dispatcher.BeginInvoke((Action)(() =>
                {
                    window = new ScreenViewerWindow(invite.Sender, invite.Source) { Width = 640, Height = 420, Left = 40, Top = 40, WindowStartupLocation = WindowStartupLocation.Manual };
                    window.Show();
                    result.TrySetResult(new ScreenViewer(window));
                }));
                return result.Task;
            };
            viewerSide.Start();

            ScreenShareSession session = new ScreenShareSession(sharer, source);
            AnnotationLayer hostLayer = new AnnotationLayer { Width = 1280, Height = 720 };
            hostLayer.Measure(new Size(1280, 720));
            hostLayer.Arrange(new Rect(0, 0, 1280, 720));
            hostLayer.Local += op => session.Annotate(op);
            int fromViewer = 0;
            session.Annotated += op => Application.Current.Dispatcher.BeginInvoke((Action)(() => { fromViewer++; hostLayer.Apply(op); }));
            byte asked = 0;
            bool fullNext = false;
            session.PermissionRequested += (id, name, kind) => { asked |= kind; session.Answer(id, kind, true, kind == SharePermission.Control && fullNext); };
            bool ended = false;
            session.Ended += () => ended = true;
            PointerUpdate pointer = null;
            session.PointerMoved += update => pointer = update;
            session.Invite(new Peer { Id = viewerSide.SelfId, Name = "Visor", Address = local, Port = port });
            session.Start();

            ok &= Check(await Until(() => window != null && window.CanAsk, 5000), "el visor nuevo y quien comparte se saludan");

            // Un trazo de quien comparte antes de dar permisos llega al visor.
            hostLayer.Commit(Stroke(11, AnnotationTool.Arrow, 0.1, 0.1, 0.5, 0.5));
            ok &= Check(await Until(() => window.Layer.Children.Count == 1, 3000), "el trazo de quien comparte aparece en el visor");

            window.Request(SharePermission.Draw);
            ok &= Check(await Until(() => (window.Granted & SharePermission.Draw) != 0, 3000) && (asked & SharePermission.Draw) != 0,
                "pedir dibujar llega como petición y el permiso vuelve al visor");

            window.Layer.Commit(Stroke(22, AnnotationTool.Pen, 0.2, 0.2, 0.3, 0.4));
            ok &= Check(await Until(() => fromViewer == 1 && hostLayer.Children.Count == 2, 3000), "el trazo del visor llega a quien comparte");

            // El visor no puede borrar ni alargar un trazo de quien comparte (id 11).
            window.Layer.Commit(new AnnotationOp { Kind = AnnotationOp.Erase, Id = 11 });
            await Task.Delay(500);
            ok &= Check(hostLayer.Children.Count == 2 && fromViewer == 1, "el visor no puede borrar trazos ajenos");

            window.Layer.EraseMine();
            ok &= Check(await Until(() => hostLayer.Children.Count == 1, 3000), "borrar los trazos propios se refleja en quien comparte");

            // Láser: llega a quien comparte y, al levantar el mouse, se desvanece solo en los dos lados.
            int viewerBefore = window.Layer.Children.Count;
            window.Layer.Commit(Stroke(44, AnnotationTool.Laser, 0.3, 0.3, 0.6, 0.4));
            ok &= Check(await Until(() => hostLayer.Children.Count == 2, 3000), "el láser llega a quien comparte");
            window.Layer.FinishStroke(44);
            bool faded = await Until(() => hostLayer.Children.Count == 1 && window.Layer.Children.Count == viewerBefore, 5000);
            if (!faded) Console.WriteLine("  quien comparte: " + hostLayer.Children.Count + " · visor: " + window.Layer.Children.Count);
            ok &= Check(faded, "el láser se desvanece solo en los dos lados");

            // Mientras dibuja, su puntero llega a quien comparte con su nombre.
            window.MovePointer(0.25, 0.5);
            ok &= Check(await Until(() => pointer != null && pointer.Visible && pointer.Name == "Visor" && Math.Abs(pointer.X / 65535.0 - 0.25) < 0.01, 3000),
                "el puntero de quien dibuja llega a quien comparte con su nombre");

            window.Request(SharePermission.Control);
            ok &= Check(await Until(() => (window.Granted & SharePermission.Control) != 0 && session.Controller == "Visor (ventana activa)", 3000)
                && (window.Granted & SharePermission.FullControl) == 0,
                "pedir control concede «Ventana activa» y quien comparte sabe quién controla");

            // Soltar el control y pedirlo de nuevo, ahora con control total.
            window.ToggleControl();
            ok &= Check(await Until(() => (window.Granted & SharePermission.Control) == 0 && session.Controller == null, 3000), "soltar el control lo devuelve");
            fullNext = true;
            window.Request(SharePermission.Control);
            ok &= Check(await Until(() => (window.Granted & SharePermission.FullControl) != 0 && session.Controller == "Visor (control total)", 3000),
                "el control total del equipo llega como permiso propio");

            session.RevokeAll();
            ok &= Check(await Until(() => window.Granted == 0 && session.Controller == null && !session.AnyGranted, 3000), "quitar permisos llega al visor");
            ok &= Check(await Until(() => pointer != null && !pointer.Visible, 3000), "sin permisos, su puntero desaparece");
            window.MovePointer(0.6, 0.6);
            await Task.Delay(500);
            ok &= Check(!pointer.Visible, "sin permisos no se envía el puntero");

            // Sin permiso, un trazo del visor se ignora. (El borrado anterior ya contó como operación del visor.)
            int acceptedBefore = fromViewer;
            window.Layer.Commit(Stroke(33, AnnotationTool.Box, 0.6, 0.6, 0.8, 0.8));
            await Task.Delay(600);
            ok &= Check(hostLayer.Children.Count == 1 && fromViewer == acceptedBefore, "sin permiso, los trazos del visor no se aceptan");

            hostLayer.Commit(new AnnotationOp { Kind = AnnotationOp.Clear });
            ok &= Check(await Until(() => window.Layer.Children.Count == 0, 3000), "borrar todo llega al visor");


            session.Stop();
            ok &= Check(await Until(() => ended, 4000), "la transmisión termina");
            if (window != null) window.Close();
        }
        shared.Close();
        Console.WriteLine(ok ? "OK: canal de retorno de la pantalla compartida" : "FALLO en el canal de retorno");
        return ok;
    }

    private static AnnotationOp Stroke(int id, AnnotationTool tool, double x0, double y0, double x1, double y1)
    {
        return new AnnotationOp
        {
            Kind = AnnotationOp.Begin,
            Id = id,
            Tool = tool,
            Color = 0xFFF25C45,
            Xs = new[] { AnnotationOp.Norm(x0), AnnotationOp.Norm(x1) },
            Ys = new[] { AnnotationOp.Norm(y0), AnnotationOp.Norm(y1) }
        };
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
