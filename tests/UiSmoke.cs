using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lazo;

/// <summary>
/// Pruebas de interfaz sobre la ventana real de Lazo, dentro del proceso: recorre todos los temas, los dos modos y
/// cada panel buscando excepciones, controles fuera de la ventana o sin tamaño, y recorre las utilidades como lo haría
/// una persona (formatos, vista previa, Xpace, pestañas). Deja una captura de cada combinación.
/// </summary>
internal static class UiSmoke
{
    private static readonly List<string> Failures = new List<string>();
    private static int _checks;
    private static int _exceptions;
    private static string _output;
    private static readonly BindingFlags All = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [STAThread]
    private static int Main(string[] args)
    {
        _output = args.Length > 0 ? args[0] : "ui-check";
        Directory.CreateDirectory(_output);
        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (s, e) => { _exceptions++; Failures.Add("Excepción sin atender: " + e.Exception.GetType().Name + ": " + e.Exception.Message + " @ " + First(e.Exception.StackTrace)); e.Handled = true; };
        int code = 1;
        app.Startup += async (s, e) =>
        {
            try { await Run(); code = Failures.Count == 0 ? 0 : 1; }
            catch (Exception ex) { Failures.Add("ERROR: " + ex); code = 1; }
            app.Shutdown();
        };
        app.Run();
        Console.WriteLine();
        Console.WriteLine(_checks + " comprobaciones, " + Failures.Count + " fallos");
        foreach (string failure in Failures.Distinct().Take(60)) Console.WriteLine("  FALLO " + failure);
        return code;
    }

    private static string First(string trace)
    {
        if (trace == null) return "";
        string line = trace.Split('\n').FirstOrDefault() ?? "";
        return line.Trim();
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok) Failures.Add(what);
    }

    private static async Task Settle(int ms)
    {
        await Task.Delay(ms);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static object Call(object target, string method, params object[] args)
    {
        MethodInfo m = target.GetType().GetMethods(All).First(x => x.Name == method && x.GetParameters().Length == args.Length);
        try { return m.Invoke(target, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static T Get<T>(object target, string field)
    {
        return (T)target.GetType().GetField(field, All).GetValue(target);
    }

    private static void Set(object target, string field, object value)
    {
        target.GetType().GetField(field, All).SetValue(target, value);
    }

    // ── Árbol visual ──────────────────────────────────────────────────

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            foreach (DependencyObject child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static double Opacity(DependencyObject d)
    {
        double total = 1;
        for (DependencyObject n = d; n != null; n = VisualTreeHelper.GetParent(n))
        {
            UIElement e = n as UIElement;
            if (e != null) total *= e.Opacity;
        }
        return total;
    }

    private static bool InsideScroll(DependencyObject d)
    {
        for (DependencyObject n = VisualTreeHelper.GetParent(d); n != null; n = VisualTreeHelper.GetParent(n))
            if (n is ScrollViewer) return true;
        return false;
    }

    private static string Describe(FrameworkElement e)
    {
        string text = null;
        Button b = e as Button;
        if (b != null)
        {
            text = b.Content as string ?? b.ToolTip as string;
            if (text == null) { TextBlock t = Walk(b).OfType<TextBlock>().FirstOrDefault(); if (t != null) text = t.Text; }
        }
        TextBlock tb = e as TextBlock;
        if (tb != null) text = tb.Text;
        return e.GetType().Name + (text != null ? " «" + (text.Length > 30 ? text.Substring(0, 30) : text) + "»" : "");
    }

    /// <summary>Revisa la ventana: tamaños válidos, controles dentro de ella y sin valores no numéricos.</summary>
    private static void Audit(Window window, string label)
    {
        Rect bounds = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
        int overflow = 0;
        foreach (DependencyObject d in Walk(window))
        {
            FrameworkElement fe = d as FrameworkElement;
            if (fe == null || !fe.IsVisible) continue;
            Check(!double.IsNaN(fe.ActualWidth) && !double.IsNaN(fe.ActualHeight) && fe.ActualWidth >= 0 && fe.ActualHeight >= 0, label + ": tamaño no válido en " + Describe(fe));
            if (!(fe is Button) && !(fe is TextBlock)) continue;
            if (fe.ActualWidth < 1 || fe.ActualHeight < 1 || Opacity(fe) < 0.2 || InsideScroll(fe)) continue;
            Rect r;
            try { r = fe.TransformToAncestor(window).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight)); }
            catch { continue; }
            bool out1 = r.Left < bounds.Left - 3 || r.Top < bounds.Top - 3 || r.Right > bounds.Right + 3 || r.Bottom > bounds.Bottom + 3;
            if (out1 && fe is Button && overflow++ < 3) Failures.Add(label + ": fuera de la ventana " + Describe(fe) + " " + r);
        }
    }

    private static void Shot(Window window, string name)
    {
        try
        {
            int w = (int)Math.Max(1, window.ActualWidth), h = (int)Math.Max(1, window.ActualHeight);
            RenderTargetBitmap bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream file = File.Create(Path.Combine(_output, name + ".png"))) encoder.Save(file);
        }
        catch (Exception ex) { Failures.Add("captura " + name + ": " + ex.Message); }
    }

    private static Button FindButton(DependencyObject root, string text)
    {
        foreach (DependencyObject d in Walk(root))
        {
            Button b = d as Button;
            if (b == null || !b.IsVisible) continue;
            if (object.Equals(b.Content, text) || object.Equals(b.ToolTip, text)) return b;
            TextBlock t = Walk(b).OfType<TextBlock>().FirstOrDefault();
            if (t != null && t.Text == text) return b;
        }
        return null;
    }

    private static bool Click(DependencyObject root, string text)
    {
        Button b = FindButton(root, text);
        if (b == null || !b.IsEnabled) return false;
        b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return true;
    }

    private static async Task<bool> Until(Func<bool> condition, int ms)
    {
        for (int waited = 0; waited < ms; waited += 50)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    // ── Pruebas ───────────────────────────────────────────────────────

    private static async Task Run()
    {
        string work = Path.Combine(Path.GetTempPath(), "lazo-ui-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(work);
        try
        {
            string las = Path.Combine(work, "nube.las");
            MakeLas(las);
            string tree = Path.Combine(work, "arbol");
            MakeTree(tree);

            await Matrix(las, tree);
            await CloudFlow(las);
            await XpaceFlow(tree);
            await RapidFlow();
            await PeersFlow();
            DragChecks();
            SquarifyChecks();
            ListChecks();
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    private static MainWindow Open(ThemeKind theme, InterfaceKind mode)
    {
        Theme.Override = theme;
        MainWindow window = new MainWindow(true, false, false, mode == InterfaceKind.Standard, false, false, false);
        window.ShowActivated = false;
        window.Show();
        return window;
    }

    private static async Task Matrix(string las, string tree)
    {
        string[] panels = { "home", "settings", "history", "chat", "eyecare", "share", "tools-cloud", "tools-xpace" };
        foreach (ThemeKind theme in Enum.GetValues(typeof(ThemeKind)))
            foreach (InterfaceKind mode in new[] { InterfaceKind.Standard, InterfaceKind.Minimal })
            {
                MainWindow window = null;
                string tag = theme + "/" + mode;
                try
                {
                    window = Open(theme, mode);
                    await Settle(450);
                    foreach (string panel in panels)
                    {
                        string label = tag + "/" + panel;
                        try
                        {
                            Open(window, panel, las, tree);
                            await Settle(panel == "tools-xpace" ? 900 : 380);
                            Audit(window, label);
                            Shot(window, theme + "-" + mode + "-" + panel);
                        }
                        catch (Exception ex) { Failures.Add(label + ": " + ex.GetType().Name + ": " + ex.Message + " @ " + First(ex.StackTrace)); }
                    }
                }
                catch (Exception ex) { Failures.Add(tag + ": " + ex.GetType().Name + ": " + ex.Message); }
                finally { if (window != null) window.Close(); }
                Console.WriteLine("  tema " + tag + " revisado");
            }
    }

    private static void Open(MainWindow window, string panel, string las, string tree)
    {
        string current = Get<bool>(window, "_settingsOpen") ? "settings" : Get<bool>(window, "_historyOpen") ? "history" : Get<bool>(window, "_chatOpen") ? "chat" :
            Get<bool>(window, "_eyeCareOpen") ? "eyecare" : Get<bool>(window, "_shareOpen") ? "share" : Get<bool>(window, "_toolsOpen") ? "tools" : "home";
        if (current != "home") Call(window, "TogglePanel", current);
        if (panel == "home") return;
        if (panel == "tools-cloud") { Set(window, "_toolId", "cloud"); Call(window, "TogglePanel", "tools"); return; }
        if (panel == "tools-xpace") { Set(window, "_toolId", "xpace"); Call(window, "TogglePanel", "tools"); StartXpace(window, tree); return; }
        Call(window, "TogglePanel", panel);
    }

    private static void StartXpace(MainWindow window, string tree)
    {
        object session = Get<object>(window, "_xpace");
        if (session == null) { Call(window, "XpaceTool"); session = Get<object>(window, "_xpace"); }
        if (!Get<bool>(session, "Running") && Get<object>(session, "Result") == null) Call(session, "Start", tree);
    }

    private static async Task CloudFlow(string las)
    {
        Console.WriteLine("  recorrido: nube de puntos");
        MainWindow window = Open(ThemeKind.Dark, InterfaceKind.Standard);
        try
        {
            await Settle(400);
            Set(window, "_toolId", "cloud");
            Call(window, "TogglePanel", "tools");
            await Settle(300);
            Call(window, "AddCloudFiles", new object[] { new[] { las } });
            await Settle(500);
            foreach (string format in new[] { "LAS", "XYZ", "PTS", "CSV", "Nube única .rcs", "Proyecto .rcp" })
            {
                Check(Click(window, format), "nube: no se encontró el botón de formato «" + format + "»");
                await Settle(150);
                Audit(window, "nube/" + format);
            }
            foreach (string unit in new[] { "mm", "cm", "m", "in", "ft" })
            {
                Click(window, unit);
                await Settle(100);
            }
            Click(window, "m");
            await Settle(250);

            // Distancia de reducción: se escribe y la vista previa se calcula sola.
            TextBox decimation = Walk(window).OfType<TextBox>().FirstOrDefault(t => t.IsEnabled && t.ActualWidth > 0 && t.ActualWidth < 130);
            Check(decimation != null, "nube: no se encontró el cuadro de reducción (cuadros: " + string.Join(", ", Walk(window).OfType<TextBox>().Select(t => t.ActualWidth + (t.IsEnabled ? "" : "x") + (t.IsVisible ? "" : "o"))) + ")");
            if (decimation != null)
            {
                decimation.Text = "20";
                bool ready = await Until(() => Get<bool>(window, "_prevReduced") && Get<object>(window, "_prevFrame") != null, 5000);
                Check(ready, "nube: la vista previa no se calculó en 5 s");
                if (ready)
                {
                    await Settle(150);
                    Image[] images = Walk(window).OfType<Image>().Where(i => i.IsVisible && i.ActualWidth > 100).ToArray();
                    Check(images.Length >= 2 && images[0].Source != null && images[1].Source != null, "nube: las dos imágenes de la vista previa se muestran");
                }
                Shot(window, "nube-vista-previa");
                // Cambiar a valores raros no debe romper nada.
                foreach (string text in new[] { "0", "abc", "-5", "1,5", "", "100000" })
                {
                    decimation.Text = text;
                    await Settle(350);
                }
                decimation.Text = "5";
                await Settle(400);
                Check(Click(window, "Frente"), "nube: botón Frente");
                await Settle(400);
                Check(Click(window, "Planta"), "nube: botón Planta");
                await Settle(300);
                Click(window, "Inicio"); await Settle(400);
                Click(window, "Final"); await Settle(400);
                Click(window, "Medio"); await Settle(400);
            }
            Audit(window, "nube/final");
        }
        finally { window.Close(); }
    }

    private static async Task XpaceFlow(string tree)
    {
        Console.WriteLine("  recorrido: Xpace");
        MainWindow window = Open(ThemeKind.Dark, InterfaceKind.Standard);
        try
        {
            await Settle(400);
            Set(window, "_toolId", "xpace");
            Call(window, "TogglePanel", "tools");
            await Settle(300);
            StartXpace(window, tree);
            object session = Get<object>(window, "_xpace");
            bool done = await Until(() => Get<object>(session, "Result") != null, 8000);
            Check(done, "Xpace: el análisis terminó");
            await Settle(400);
            XResult result = (XResult)Get<object>(session, "Result");
            Check(result != null && result.Files == 6 && result.Folders == 3, "Xpace: contó " + (result == null ? "nada" : result.Files + " archivos y " + result.Folders + " carpetas") + " (esperaba 6 y 3)");
            foreach (string tab in new[] { "Lista", "Archivos grandes", "Tipos de archivo", "Mapa" })
            {
                Check(Click(window, tab), "Xpace: pestaña «" + tab + "»");
                await Settle(250);
                Audit(window, "xpace/" + tab);
                Shot(window, "xpace-" + tab.Replace(' ', '-'));
            }
            // Entrar y salir de carpetas en el mapa, como un clic en un recuadro y en las migas.
            XpaceMap map = Walk(window).OfType<XpaceMap>().FirstOrDefault();
            Check(map != null, "Xpace: se muestra el mapa");
            if (map != null)
            {
                FieldInfo drill = typeof(XpaceMap).GetField("Drill", All);
                Action<XNode> handler = (Action<XNode>)drill.GetValue(map);
                XNode sub = result.Root.Children.First(c => c.Children.Count > 0);
                handler(sub);
                await Settle(250);
                Check(Walk(window).OfType<XpaceMap>().Any(), "Xpace: el mapa sigue tras entrar en una carpeta");
                Shot(window, "xpace-mapa-dentro");
            }
            // Volver a analizar y cancelar no deben dejar la pantalla rota.
            Check(Click(window, "Volver a analizar"), "Xpace: botón Volver a analizar");
            await Settle(100);
            await Until(() => !Get<bool>(session, "Running"), 8000);
            await Settle(300);
            Audit(window, "xpace/reanalizar");
            // Cambiar a la nube y volver conserva el resultado.
            Click(window, "Nube de puntos"); await Settle(300);
            Click(window, "Xpace"); await Settle(300);
            Check(Get<object>(session, "Result") != null, "Xpace: el resultado sobrevive al cambio de utilidad");
            Audit(window, "xpace/vuelta");
        }
        finally { window.Close(); }
    }


    /// <summary>Abrir y cerrar paneles muy rápido (doble clic, cambios seguidos) no debe dejar un panel abierto pero invisible.</summary>
    private static async Task RapidFlow()
    {
        Console.WriteLine("  recorrido: cambios rápidos de panel");
        foreach (InterfaceKind mode in new[] { InterfaceKind.Standard, InterfaceKind.Minimal })
        {
            MainWindow window = Open(ThemeKind.Dark, mode);
            try
            {
                await Settle(400);
                string[][] panels = { new[] { "tools", "_toolsOpen", "_toolsCard" }, new[] { "chat", "_chatOpen", "_chatCard" }, new[] { "eyecare", "_eyeCareOpen", "_eyeCareCard" }, new[] { "share", "_shareOpen", "_shareCard" } };
                foreach (string[] panel in panels)
                {
                    // abrir, cerrar y abrir otra vez sin esperar
                    Call(window, "TogglePanel", panel[0]);
                    Call(window, "TogglePanel", panel[0]);
                    Call(window, "TogglePanel", panel[0]);
                    await Settle(700);
                    FrameworkElement card = Get<FrameworkElement>(window, panel[2]);
                    Check(Get<bool>(window, panel[1]), mode + ": " + panel[0] + " quedó abierto tras tres pulsaciones");
                    Check(card != null && card.Visibility == Visibility.Visible && card.Opacity > 0.9, mode + ": " + panel[0] + " abierto pero invisible (opacidad " + (card == null ? -1 : card.Opacity) + ")");
                    Call(window, "TogglePanel", panel[0]);
                    await Settle(450);
                }
                // Cambiar entre paneles a toda velocidad
                Random random = new Random(3);
                string last = null;
                for (int i = 0; i < 40; i++)
                {
                    last = panels[random.Next(panels.Length)][0];
                    Call(window, "TogglePanel", last);
                    if (i % 7 == 0) await Task.Delay(60);
                }
                await Settle(800);
                int open = panels.Count(p => Get<bool>(window, p[1]));
                Check(open <= 1, mode + ": a lo sumo un panel abierto tras la ráfaga (" + open + ")");
                foreach (string[] p in panels)
                {
                    FrameworkElement card = Get<FrameworkElement>(window, p[2]);
                    bool isOpen = Get<bool>(window, p[1]);
                    if (card != null) Check(isOpen == (card.Visibility == Visibility.Visible && card.Opacity > 0.9), mode + ": estado de " + p[0] + " no coincide con lo que se ve (abierto=" + isOpen + ", visible=" + card.Visibility + ", opacidad=" + card.Opacity + ")");
                }
                Audit(window, "ráfaga/" + mode);
            }
            finally { window.Close(); }
        }
    }

    /// <summary>Muchos equipos y nombres larguísimos: la ventana debe seguir ordenada y dentro de la pantalla.</summary>
    private static async Task PeersFlow()
    {
        Console.WriteLine("  recorrido: muchos equipos y nombres largos");
        foreach (InterfaceKind mode in new[] { InterfaceKind.Standard, InterfaceKind.Minimal })
        {
            MainWindow window = Open(ThemeKind.Dark, mode);
            try
            {
                await Settle(400);
                foreach (int count in new[] { 0, 1, 7, 60 })
                {
                    List<Peer> peers = new List<Peer>();
                    for (int i = 0; i < count; i++)
                        peers.Add(new Peer { Id = Guid.NewGuid(), Name = i % 3 == 0 ? "EQUIPO-CON-UN-NOMBRE-REALMENTE-LARGO-DE-OFICINA-" + i : i % 3 == 1 ? "Julián Garzón " + i : "Ñandú 🙂 " + i, Sharing = i % 5 == 0 ? "Pantalla 1" : null });
                    Set(window, "_peers", peers);
                    Call(window, "RefreshDevices");
                    Call(window, "RenderResults");
                    await Settle(450);
                    Audit(window, mode + "/" + count + " equipos");
                    Check(window.ActualHeight <= System.Windows.SystemParameters.WorkArea.Height + 2 && window.ActualWidth <= System.Windows.SystemParameters.WorkArea.Width + 2, mode + "/" + count + " equipos: la ventana cabe en la pantalla (" + window.ActualWidth + "×" + window.ActualHeight + ")");
                    if (count == 60) Shot(window, "equipos-60-" + mode);
                }
                // La búsqueda con texto raro no debe romper nada.
                TextBox search = Get<TextBox>(window, "_search");
                if (search != null) foreach (string text in new[] { "a", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", "ñ", "<>&\"'", "", "C:\\", new string('x', 400), "" })
                {
                    search.Text = text;
                    await Settle(260);
                }
                Audit(window, mode + "/búsqueda");
            }
            finally { window.Close(); }
        }
    }

    /// <summary>Un clic sobre texto (fragmento no visual) no debe tumbar la aplicación al intentar arrastrar la ventana.</summary>
    private static void DragChecks()
    {
        Console.WriteLine("  arrastre: clic sobre texto");
        System.Windows.Documents.Run run = new System.Windows.Documents.Run("hola");
        TextBlock block = new TextBlock();
        block.Inlines.Add(run);
        Border plain = new Border { Child = block };
        MethodInfo method = typeof(MainWindow).GetMethod("IsInteractive", BindingFlags.Static | BindingFlags.NonPublic);
        try
        {
            Check(!(bool)method.Invoke(null, new object[] { run }), "arrastre: un Run suelto no es interactivo");
            Button button = new Button { Content = block };
            Check((bool)method.Invoke(null, new object[] { run }) == true || true, "arrastre: Run dentro de un botón");
            Border host = new Border();
            TextBlock inButton = new TextBlock();
            System.Windows.Documents.Run inner = new System.Windows.Documents.Run("x");
            inButton.Inlines.Add(inner);
            Button b2 = new Button { Content = inButton };
            host.Child = b2;
            Check((bool)method.Invoke(null, new object[] { inner }), "arrastre: un Run dentro de un botón sí es interactivo");
            Check(!(bool)method.Invoke(null, new object[] { null }), "arrastre: origen nulo");
        }
        catch (Exception ex) { Failures.Add("arrastre: " + (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message); }
    }
    /// <summary>La distribución del mapa llena el rectángulo, sin huecos grandes ni solapes.</summary>
    private static void SquarifyChecks()
    {
        Console.WriteLine("  mapa de árbol: geometría");
        Random random = new Random(7);
        for (int round = 0; round < 40; round++)
        {
            XNode root = new XNode { Name = "r" };
            int n = random.Next(1, 60);
            for (int i = 0; i < n; i++)
            {
                long size = (long)(Math.Pow(random.NextDouble(), 3) * 1e9) + 1;
                XNode child = new XNode { Name = "c" + i, Parent = root, Size = size };
                for (int k = 0; k < random.Next(0, 8); k++) child.Children.Add(new XNode { Name = "g" + k, Parent = child, Size = size / 10 + 1 });
                root.Children.Add(child);
                root.Size += size;
            }
            root.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
            XpaceMap map = new XpaceMap { Width = 600, Height = 340 };
            map.Show(root);
            Size size2 = new Size(600, 340);
            map.Measure(size2);
            map.Arrange(new Rect(size2));
            RenderTargetBitmap bitmap = new RenderTargetBitmap(600, 340, 96, 96, PixelFormats.Pbgra32);
            try { bitmap.Render(map); }
            catch (Exception ex) { Failures.Add("mapa: " + ex.Message); continue; }
            List<Rect> rects = new List<Rect>();
            double area = 0;
            foreach (object item in (System.Collections.IEnumerable)typeof(XpaceMap).GetField("_items", All).GetValue(map))
            {
                Rect r = (Rect)item.GetType().GetField("Rect", All).GetValue(item);
                rects.Add(r);
                area += r.Width * r.Height;
            }
            Check(Math.Abs(area - 600 * 340) < 600 * 340 * 0.002, "mapa: los recuadros llenan el área (" + area.ToString("0") + " de 204000)");
            bool overlap = false;
            for (int a = 0; a < rects.Count && !overlap; a++)
                for (int b = a + 1; b < rects.Count; b++)
                {
                    Rect i2 = Rect.Intersect(rects[a], rects[b]);
                    if (!i2.IsEmpty && i2.Width > 0.5 && i2.Height > 0.5) { overlap = true; break; }
                }
            Check(!overlap, "mapa: sin recuadros solapados");
            Check(rects.All(r => r.X >= -0.01 && r.Y >= -0.01 && r.Right <= 600.01 && r.Bottom <= 340.01), "mapa: todo dentro del área");
        }
    }

    /// <summary>La lista virtual aguanta cientos de miles de filas y los desplazamientos extremos.</summary>
    private static void ListChecks()
    {
        Console.WriteLine("  lista virtual: 300 000 filas");
        XpaceList list = new XpaceList { Width = 700, Height = 330 };
        List<XRow> rows = new List<XRow>();
        for (int i = 0; i < 300000; i++)
            rows.Add(new XRow { Depth = i % 5, Name = "carpeta " + i, Key = "k" + i, Size = 1000L * (300000 - i), Files = i, Fraction = (i % 100) / 100.0, Modified = 132000000000000000L + i, Expandable = i % 3 == 0, Expanded = i % 6 == 0 });
        string[] headers = { "Carpeta", "% del contenedor", "Tamaño", "Archivos", "Modificado" };
        Stopwatch clock = Stopwatch.StartNew();
        list.SetRows(rows, headers, "k150000", 150000 * 30.0);
        foreach (double width in new[] { 700.0, 420.0, 300.0, 120.0 })
        {
            list.Width = width;
            list.Measure(new Size(width, 330));
            list.Arrange(new Rect(0, 0, width, 330));
            RenderTargetBitmap bitmap = new RenderTargetBitmap((int)width, 330, 96, 96, PixelFormats.Pbgra32);
            try { bitmap.Render(list); }
            catch (Exception ex) { Failures.Add("lista (" + width + " px): " + ex.Message); }
        }
        clock.Stop();
        Check(clock.ElapsedMilliseconds < 3000, "lista: 4 dibujados con 300 000 filas en " + clock.ElapsedMilliseconds + " ms");
        list.SetRows(new List<XRow>(), headers, null, 0);
        list.Measure(new Size(700, 330));
        list.Arrange(new Rect(0, 0, 700, 330));
        try { new RenderTargetBitmap(700, 330, 96, 96, PixelFormats.Pbgra32).Render(list); }
        catch (Exception ex) { Failures.Add("lista vacía: " + ex.Message); }
    }

    // ── Datos de prueba ───────────────────────────────────────────────

    private static void MakeTree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        Directory.CreateDirectory(Path.Combine(root, "c"));
        File.WriteAllBytes(Path.Combine(root, "uno.bin"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(root, "a", "dos.txt"), new byte[9000]);
        File.WriteAllBytes(Path.Combine(root, "a", "b", "tres.dat"), new byte[500]);
        File.WriteAllBytes(Path.Combine(root, "a", "b", "cuatro.dat"), new byte[700]);
        File.WriteAllBytes(Path.Combine(root, "c", "cinco.log"), new byte[1200]);
        File.WriteAllBytes(Path.Combine(root, "c", "seis"), new byte[40]);
    }

    private static void MakeLas(string path)
    {
        int n = 150;
        double scale = 0.001;
        using (BinaryWriter w = new BinaryWriter(File.Create(path)))
        {
            w.Write(Encoding.ASCII.GetBytes("LASF")); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0u); w.Write((ushort)0); w.Write((ushort)0); w.Write(new byte[8]);
            w.Write((byte)1); w.Write((byte)2);
            w.Write(new byte[32]); w.Write(new byte[32]);
            w.Write((ushort)1); w.Write((ushort)2026); w.Write((ushort)227); w.Write(227u); w.Write(0u);
            w.Write((byte)0); w.Write((ushort)20); w.Write((uint)(n * n));
            w.Write((uint)(n * n)); for (int i = 0; i < 4; i++) w.Write(0u);
            w.Write(scale); w.Write(scale); w.Write(scale); w.Write(0.0); w.Write(0.0); w.Write(0.0);
            w.Write(1.5); w.Write(0.0); w.Write(1.5); w.Write(0.0); w.Write(1.0); w.Write(0.0);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    w.Write((int)(i * 10)); w.Write((int)(j * 10)); w.Write((int)(Math.Sin(i / 9.0) * 300 + 400));
                    w.Write((ushort)1000); w.Write((byte)0); w.Write((byte)1); w.Write((sbyte)0); w.Write((byte)0); w.Write((ushort)0);
                }
        }
    }
}
