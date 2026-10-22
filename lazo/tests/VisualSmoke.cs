using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.Serialization;
using PathShape = System.Windows.Shapes.Path;
class VisualSmoke
{
    static Assembly app;
    static BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Field(object o,string name,object v) { o.GetType().GetField(name,flags).SetValue(o,v); }
    static void Save(FrameworkElement element, string name, int w, int h)
    {
        element.Measure(new Size(w,h)); element.Arrange(new Rect(0,0,w,h)); element.UpdateLayout();
        var bitmap=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(name)) encoder.Save(stream);
    }
    static Button FindButton(DependencyObject root, string text)
    {
        var button=root as Button;
        if(button!=null && object.Equals(button.Content,text)) return button;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        { var found=FindButton(VisualTreeHelper.GetChild(root,i),text); if(found!=null)return found; }
        return null;
    }
    static Button FindLogicalButton(DependencyObject root, string text)
    {
        var button=root as Button;
        if(button!=null && (object.Equals(button.Content,text) || object.Equals(button.ToolTip,text))) return button;
        foreach(object child in LogicalTreeHelper.GetChildren(root))
        {
            var dependency=child as DependencyObject;
            if(dependency!=null) { var found=FindLogicalButton(dependency,text); if(found!=null)return found; }
        }
        return null;
    }
    static void VerifyClockRebuilds(string output)
    {
        var clockType=app.GetType("Lazo.EyeCareCardView");
        var clock=FormatterServices.GetUninitializedObject(clockType);
        var host=new Border();
        var digital=new TextBlock { Text="12:34:56", FontSize=24, Foreground=Brushes.White, HorizontalAlignment=HorizontalAlignment.Center };
        var analog=(FrameworkElement)Activator.CreateInstance(app.GetType("Lazo.EyeCareAnalogClock"),true);
        Field(clock,"_clockHost",host);
        Field(clock,"_digitalClockText",digital);
        Field(clock,"_analogClock",analog);
        var serviceType=app.GetType("Lazo.EyeCareService");
        var service=serviceType.GetField("<ClockMode>k__BackingField",BindingFlags.Instance|BindingFlags.NonPublic);
        var serviceInstance=serviceType.GetField("Instance",BindingFlags.Static|BindingFlags.Public).GetValue(null);
        var modeType=app.GetType("Lazo.EyeCareClockMode");
        var rebuild=clockType.GetMethod("RebuildClockHost",flags);
        foreach(string mode in new[]{"Digital","Analog","Digital","Analog","Digital"})
        {
            var oldParent=LogicalTreeHelper.GetParent(digital);
            service.SetValue(serviceInstance,Enum.Parse(modeType,mode));
            try { rebuild.Invoke(clock,null); }
            catch(TargetInvocationException error) { throw new Exception("Clock rebuild failed in "+mode+" mode; parent="+(oldParent==null?"null":oldParent.GetType().FullName),error.InnerException); }
            var panel=host.Child as StackPanel;
            if(panel==null) throw new Exception("Clock host is not a StackPanel after "+mode);
            if(mode=="Digital")
            {
                if(!panel.Children.Contains(digital) || !object.ReferenceEquals(VisualTreeHelper.GetParent(digital),panel))
                    throw new Exception("Digital clock has an invalid WPF parent after rebuild");
                Save(host,System.IO.Path.Combine(output,"clock-digital.png"),240,72);
            }
            else
            {
                if(!panel.Children.Contains(analog) || !object.ReferenceEquals(VisualTreeHelper.GetParent(analog),panel))
                    throw new Exception("Analog clock has an invalid WPF parent after rebuild");
                Save(host,System.IO.Path.Combine(output,"clock-analog.png"),240,120);
            }
        }
        Console.WriteLine("PASS: repeated digital/analog clock rebuilds preserve WPF parents and render both modes.");
    }
    static Delegate CreateChatSender(Type peerType, Func<object,string,Task> send)
    {
        var peer=System.Linq.Expressions.Expression.Parameter(peerType,"peer");
        var text=System.Linq.Expressions.Expression.Parameter(typeof(string),"text");
        var invoke=System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(send),System.Linq.Expressions.Expression.Convert(peer,typeof(object)),text);
        return System.Linq.Expressions.Expression.Lambda(typeof(Func<,,>).MakeGenericType(peerType,typeof(string),typeof(Task)),invoke,peer,text).Compile();
    }
    static void PumpUntil(Func<bool> condition)
    {
        var frame=new DispatcherFrame();
        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(10) };
        bool timedOut=false;
        DateTime deadline=DateTime.UtcNow.AddSeconds(5);
        timer.Tick += (s,e) =>
        {
            if(condition()) frame.Continue=false;
            else if(DateTime.UtcNow>=deadline) { timedOut=true; frame.Continue=false; }
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if(timedOut) throw new TimeoutException("Timed out waiting for the dispatcher condition");
    }
    static void VerifyFailedChatSendKeepsDraft()
    {
        var chatType=app.GetType("Lazo.ChatPanel");
        var engineType=app.GetType("Lazo.NetworkEngine");
        var network=FormatterServices.GetUninitializedObject(engineType);
        var chat=(FrameworkElement)Activator.CreateInstance(chatType,new object[]{network,null,null,null});
        var peerType=app.GetType("Lazo.Peer");
        var peer=FormatterServices.GetUninitializedObject(peerType);
        Guid peerId=Guid.NewGuid();
        peerType.GetField("Id").SetValue(peer,peerId);
        peerType.GetField("Name").SetValue(peer,"Prueba aislada");
        peerType.GetField("Address").SetValue(peer,IPAddress.Parse("203.0.113.7"));
        var peers=(System.Collections.IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(peerType));
        peers.Add(peer);
        Field(chat,"_peers",peers);
        var composer=(TextBox)chatType.GetField("_composer",flags).GetValue(chat);
        string original="  borrador con espacios  ";
        Field(chat,"_open",peerId);
        composer.Text=original;
        string submitted=null;
        var sendCompletion=new TaskCompletionSource<bool>();
        Field(chat,"_sendChatAsync",CreateChatSender(peerType,(target,text) => { submitted=text; return sendCompletion.Task; }));
        var previousContext=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            chatType.GetMethod("SendCurrent",flags).Invoke(chat,null);
            if(composer.Text!=original) throw new Exception("Starting a chat send cleared the in-flight draft");
            if(submitted!=original.Trim()) throw new Exception("Chat send did not preserve its existing trimmed payload");
            if(!(bool)chatType.GetField("_sending",flags).GetValue(chat)) throw new Exception("Chat send did not remain pending");

            string editedOriginal=original+" edición en curso";
            composer.Text=editedOriginal;
            Guid otherPeerId=Guid.NewGuid();
            chatType.GetMethod("SelectDraft",flags).Invoke(chat,new object[]{otherPeerId});
            string otherDraft="borrador de otra conversación";
            composer.Text=otherDraft;

            sendCompletion.SetException(new IOException("Fallo de transporte simulado"));
            PumpUntil(() => !(bool)chatType.GetField("_sending",flags).GetValue(chat));
            if(composer.Text!=otherDraft) throw new Exception("Failed send overwrote the selected conversation draft");

            chatType.GetMethod("SelectDraft",flags).Invoke(chat,new object[]{peerId});
            if(composer.Text!=editedOriginal) throw new Exception("Failed send or conversation switch lost the originating draft or its in-flight edit");
            chatType.GetMethod("SelectDraft",flags).Invoke(chat,new object[]{otherPeerId});
            if(composer.Text!=otherDraft) throw new Exception("Switching back lost the other conversation draft");
            Console.WriteLine("PASS: failed async chat send preserves whitespace, in-flight edits, and per-conversation drafts.");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            foreach(string timerName in new[]{"_presence","_typingStop","_pulse"})
                ((DispatcherTimer)chatType.GetField(timerName,flags).GetValue(chat)).Stop();
        }
    }
    static string ThemeColor(Type themeType, string propertyName)
    {
        var brush=(SolidColorBrush)themeType.GetProperty(propertyName,BindingFlags.Public|BindingFlags.Static).GetValue(null,null);
        return brush.Color.ToString();
    }
    static double Luminance(Color color)
    {
        Func<byte,double> channel=value =>
        {
            double normalized=value/255.0;
            return normalized<=0.04045 ? normalized/12.92 : Math.Pow((normalized+0.055)/1.055,2.4);
        };
        return 0.2126*channel(color.R)+0.7152*channel(color.G)+0.0722*channel(color.B);
    }
    static void VerifyWarmTheme(string output)
    {
        var themeType=app.GetType("Lazo.Theme");
        var themeKind=app.GetType("Lazo.ThemeKind");
        object warm=Enum.Parse(themeKind,"Warm");
        var serialize=themeType.GetMethod("SerializeTheme",BindingFlags.Static|BindingFlags.NonPublic);
        var parse=themeType.GetMethod("ParseTheme",BindingFlags.Static|BindingFlags.NonPublic);
        foreach(string[] item in new[]{new[]{"Raycast","raycast"},new[]{"Glass","glass"},new[]{"Dark","dark"},new[]{"Warm","warm"}})
        {
            object kind=Enum.Parse(themeKind,item[0]);
            string token=(string)serialize.Invoke(null,new[]{kind});
            if(token!=item[1] || !Enum.Equals(parse.Invoke(null,new object[]{token}),kind))
                throw new Exception("Theme token did not round-trip for "+item[0]);
        }
        // Todas las paletas comparten estructura: se comprueba el contraste de texto en cada una.
        var paletteType=app.GetType("Lazo.Palette");
        foreach(string name in new[]{"Wine","Raycast","Glass","Dark","Warm","Meet"})
        {
            themeType.GetMethod("SetForPreview").Invoke(null,new[]{Enum.Parse(themeKind,name)});
            object palette=themeType.GetProperty("P").GetValue(null,null);
            Func<string,Color> role=field=>(Color)ColorConverter.ConvertFromString((string)paletteType.GetField(field).GetValue(palette));
            Func<Color,Color,double> contrast=(a,b)=>(Math.Max(Luminance(a),Luminance(b))+0.05)/(Math.Min(Luminance(a),Luminance(b))+0.05);
            if(contrast(role("Ink"),role("Shell1"))<7) throw new Exception(name+" theme ink contrast is below 7:1");
            if(contrast(role("Muted"),role("Shell1"))<4.5) throw new Exception(name+" theme muted text contrast is below 4.5:1");
            if(contrast(role("ChipText"),role("Chip"))<4.5) throw new Exception(name+" theme selected segment contrast is below 4.5:1");
            Color accent=(Color)themeType.GetProperty("AccentColor").GetValue(null,null);
            Color onAccent=((SolidColorBrush)themeType.GetProperty("PrimaryText").GetValue(null,null)).Color;
            if(contrast(accent,onAccent)<3) throw new Exception(name+" theme text on accent is below 3:1");
            if(!(themeType.GetMethod("ShellSurface").Invoke(null,null) is LinearGradientBrush))
                throw new Exception(name+" theme shell lost its gradient");
        }
        themeType.GetMethod("SetForPreview").Invoke(null,new[]{warm});
        var text=(TextBlock)themeType.GetMethod("Text",new[]{typeof(string),typeof(double),typeof(Brush),typeof(FontWeight)})
            .Invoke(null,new object[]{"Encabezado",20.0,Brushes.Black,FontWeights.SemiBold});
        if(!text.FontFamily.Source.Contains("Segoe UI"))
            throw new Exception("Heading font lacks the Segoe UI fallback");

        var main=app.GetType("Lazo.MainWindow");
        foreach(bool standard in new[]{false,true})
        {
            var window=(Window)Activator.CreateInstance(main,new object[]{true,false,false,standard,false,true,false});
            app.GetType("Lazo.Identity").GetField("<Current>k__BackingField",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,"Tema de prueba");
            app.GetType("Lazo.ProfilePhoto").GetField("Current",BindingFlags.Static|BindingFlags.Public).SetValue(null,"");
            themeType.GetMethod("SetForPreview").Invoke(null,new[]{warm});
            var interfaceType=app.GetType("Lazo.InterfaceKind");
            themeType.GetMethod("SetInterface").Invoke(null,new object[]{Enum.Parse(interfaceType,standard?"Standard":"Minimal"),false});
            main.GetMethod("BuildUi",flags).Invoke(window,null);
            var settings=(DependencyObject)main.GetField("_settingsCard",flags).GetValue(window);
            var warmChoice=FindLogicalButton(settings,"Cálido");
            if(warmChoice==null) throw new Exception("Warm appearance is missing from the settings selector");
            warmChoice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(!Enum.Equals(themeType.GetProperty("Mode").GetValue(null,null),warm))
                throw new Exception("Warm appearance selector did not activate the new theme");
            Save((FrameworkElement)window.Content,System.IO.Path.Combine(output,standard?"theme-warm-standard.png":"theme-warm-minimal.png"),standard?760:342,standard?500:402);
            window.Close();
        }
        Console.WriteLine("PASS: theme tokens, contrast on every palette, settings selector, and Minimal/Standard previews.");
    }
    static void VerifyStandardManyPeers(string output)
    {
        var main=app.GetType("Lazo.MainWindow");
        var themeType=app.GetType("Lazo.Theme");
        var peerType=app.GetType("Lazo.Peer");
        foreach(string kind in new[]{"Dark","Raycast"})
        {
            var window=(Window)Activator.CreateInstance(main,new object[]{true,false,false,true,false,false,false});
            themeType.GetMethod("SetForPreview").Invoke(null,new[]{Enum.Parse(app.GetType("Lazo.ThemeKind"),kind)});
            themeType.GetMethod("SetInterface").Invoke(null,new object[]{Enum.Parse(app.GetType("Lazo.InterfaceKind"),"Standard"),false});
            var peers=(System.Collections.IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(peerType));
            string[] names={"Equipo de dirección general con un nombre extraordinariamente largo número 1","Ana","Oficina-Bogotá","DESARROLLO_PRINCIPAL_ESTACION_DE_TRABAJO_04","Luis","Marta","Sala de juntas","Recepción"};
            for(int i=0;i<18;i++)
            {
                var peer=FormatterServices.GetUninitializedObject(peerType);
                peerType.GetField("Id").SetValue(peer,Guid.NewGuid());
                peerType.GetField("Name").SetValue(peer,names[i%names.Length]+" "+i);
                peerType.GetField("Photo").SetValue(peer,"");
                peers.Add(peer);
            }
            Field(window,"_peers",peers);
            main.GetMethod("BuildUi",flags).Invoke(window,null);
            main.GetMethod("ApplySize",flags).Invoke(window,new object[]{false});
            var screen=(System.Windows.Forms.Screen)main.GetField("_launcherScreen",flags).GetValue(window);
            if(window.Height>screen.WorkingArea.Height) throw new Exception("Standard window taller than the screen with many peers");
            var devices=(WrapPanel)main.GetField("_devices",flags).GetValue(window);
            if(!(LogicalTreeHelper.GetParent(devices) is ScrollViewer)) throw new Exception("Standard device grid is not scrollable");
            if(devices.Children.Count!=18) throw new Exception("Standard device grid lost peers");
            foreach(Button tile in devices.Children)
            {
                var stack=(StackPanel)tile.Content;
                double limit=stack.Height;
                stack.Height=double.NaN;
                stack.Measure(new Size(stack.Width,double.PositiveInfinity));
                if(stack.DesiredSize.Height>limit+0.5) throw new Exception("Long device name overflows its tile");
                stack.Height=limit;
            }
            Save((FrameworkElement)window.Content,System.IO.Path.Combine(output,"standard-many-"+kind+".png"),(int)window.Width,(int)window.Height);
            window.Close();
        }
        Console.WriteLine("PASS: Standard with many peers and long names scrolls, fits tiles, and stays on screen.");
    }
    static void VerifyStatusToast()
    {
        var main=app.GetType("Lazo.MainWindow");
        foreach(bool standard in new[]{false,true})
        {
            var window=(Window)Activator.CreateInstance(main,new object[]{true,false,false,standard,false,false,false});
            app.GetType("Lazo.Theme").GetMethod("SetInterface").Invoke(null,new object[]{Enum.Parse(app.GetType("Lazo.InterfaceKind"),standard?"Standard":"Minimal"),false});
            main.GetMethod("BuildUi",flags).Invoke(window,null);
            main.GetMethod("SetStatus",flags).Invoke(window,new object[]{"El equipo no respondió."});
            var toast=(Border)main.GetField("_toast",flags).GetValue(window);
            var text=(TextBlock)main.GetField("_toastText",flags).GetValue(window);
            if(toast==null || toast.Visibility!=Visibility.Visible || text.Text!="El equipo no respondió.")
                throw new Exception("Status message is invisible outside the target search view ("+(standard?"Standard":"Minimal")+")");
            window.Close();
        }
        Console.WriteLine("PASS: transfer status reaches the user in Minimal and Standard home views.");
    }
    [STAThread] static void Main(string[] args)
    {
        app=Assembly.LoadFrom(args[0]); Directory.CreateDirectory(args[1]);
        VerifyStandardManyPeers(args[1]);
        VerifyStatusToast();
        VerifyClockRebuilds(args[1]);
        VerifyFailedChatSendKeepsDraft();
        VerifyWarmTheme(args[1]);
        var main=app.GetType("Lazo.MainWindow");
        var window=(Window)Activator.CreateInstance(main, new object[]{true,false,false,false,true,true,false});
        Save((FrameworkElement)window.Content, System.IO.Path.Combine(args[1],"settings.png"),342,402);
        var photo=app.GetType("Lazo.ProfilePhoto");
        photo.GetMethod("Set").Invoke(null,new object[]{System.IO.Path.Combine(args[1],"settings.png"),false});
        string value=(string)photo.GetField("Current").GetValue(null);
        if(Convert.FromBase64String(value).Length>12000) throw new Exception("Photo bound");
        var avatar=(FrameworkElement)main.GetMethod("PhotoContent",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{value,"Prueba",84.0});
        Save(avatar,System.IO.Path.Combine(args[1],"avatar.png"),84,84);
        if(!(avatar is System.Windows.Shapes.Ellipse)) throw new Exception("Valid photo fell back to initials");
        main.GetMethod("RefreshOwnAvatar",flags).Invoke(window,null);
        var own=(Button)main.GetField("_ownAvatar",flags).GetValue(window);
        if(own==null || !(own.Content is System.Windows.Shapes.Ellipse)) throw new Exception("Own photo not refreshed");
        own.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(!(bool)main.GetField("_settingsOpen",flags).GetValue(window)) throw new Exception("Own profile click");
        main.GetMethod("BuildUi",flags).Invoke(window,null);
        Save((FrameworkElement)window.Content,System.IO.Path.Combine(args[1],"own-profile.png"),342,402);
        var fallback=(FrameworkElement)main.GetMethod("PhotoContent",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{"invalid","Prueba",84.0});
        if(!(fallback is TextBlock)) throw new Exception("Invalid photo fallback");
        Button clear = FindButton((DependencyObject)window.Content, "Quitar");
        if(clear == null) throw new Exception("Missing remove button");
        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if((string)photo.GetField("Current").GetValue(null)!="") throw new Exception("Remove photo");
        int clicked=0;
        var segment=(Button)main.GetMethod("SegmentButton",flags).Invoke(window,new object[]{"Picker test",true,null});
        segment.Click += (sender,e) => clicked++;
        segment.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if(clicked!=1) throw new Exception("Picker click was consumed");
        var placement=app.GetType("Lazo.WindowPlacement").GetMethod("BottomCenter",BindingFlags.Static|BindingFlags.NonPublic);
        foreach(var area in new[]{new System.Drawing.Rectangle(-1920,0,1920,1040),new System.Drawing.Rectangle(1920,-1080,2560,1400)})
        {
            var point=(System.Drawing.Point)placement.Invoke(null,new object[]{area,560,420});
            if(point.X!=area.Left+(area.Width-560)/2 || point.Y!=area.Bottom-424) throw new Exception("Monitor placement");
        }
        Console.WriteLine("PASS: routed photo clicks and placement on monitors with negative/positive origins.");
        var offerType=app.GetType("Lazo.Offer");
        var offer=Activator.CreateInstance(offerType,true);
        offerType.GetField("FileName").SetValue(offer,"Planos recibidos.pdf");
        offerType.GetField("Sender").SetValue(offer,"Equipo de prueba");
        var receiveType=app.GetType("Lazo.ReceiveWindow");
        var receive=(Window)Activator.CreateInstance(receiveType,new object[]{offer,new Action<bool>(accepted=>{})});
        receiveType.GetMethod("Finish").Invoke(receive,new object[]{"Archivo recibido",System.IO.Path.Combine(args[1],"Planos recibidos.pdf")});
        ((Border)receiveType.GetField("_shell",flags).GetValue(receive)).Opacity=1;
        Save((FrameworkElement)receive.Content,System.IO.Path.Combine(args[1],"received.png"),312,118);
        var open=FindButton((DependencyObject)receive.Content,"Abrir");
        if(open==null || !open.IsEnabled || FindButton((DependencyObject)receive.Content,"Mostrar en carpeta")==null)
            throw new Exception("Missing received actions");
        receiveType.GetMethod("Finish").Invoke(receive,new object[]{"Error: transferencia incompleta",null});
        if(open.IsEnabled) throw new Exception("Open enabled after failed reception");
        receive.Close();
        Console.WriteLine("PASS: received-file actions and disabled Open on errors.");
        var motion=Activator.CreateInstance(app.GetType("Lazo.LiquidMotion"),true);
        var sheet=new StackPanel { Orientation=Orientation.Vertical, Background=new SolidColorBrush(Color.FromRgb(65,65,65)) };
        foreach(double p in new[]{.10,.24,.40,.56,.72,.88,1.0})
        {
            var canvas=new Canvas {Width=566,Height=154};
            var body=new PathShape { Fill=new LinearGradientBrush(Colors.White,Color.FromArgb(200,35,35,35),90),Stroke=Brushes.Silver,StrokeThickness=1 };
            var neck=new PathShape { Fill=Brushes.Gray };
            var rim=new PathShape { Stroke=Brushes.White,StrokeThickness=1.7 };
            canvas.Children.Add(neck); canvas.Children.Add(body); canvas.Children.Add(rim);
            Field(motion,"_shell",new Border {CornerRadius=new CornerRadius(16)});
            Field(motion,"_surface",new Window()); Field(motion,"_body",body); Field(motion,"_neck",neck); Field(motion,"_rim",rim);
            Field(motion,"_width",542.0); Field(motion,"_height",86.0); Field(motion,"_floor",150.0); Field(motion,"_progress",p);
            motion.GetType().GetMethod("Draw",flags).Invoke(motion,null);
            sheet.Children.Add(canvas);
        }
        Save(sheet,System.IO.Path.Combine(args[1],"motion.png"),566,1078);
        Console.WriteLine("PASS: settings render, photo normalization/removal/invalid fallback, seven liquid frames.");
    }
}
