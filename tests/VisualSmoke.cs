using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    [STAThread] static void Main(string[] args)
    {
        app=Assembly.LoadFrom(args[0]); Directory.CreateDirectory(args[1]);
        VerifyClockRebuilds(args[1]);
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
