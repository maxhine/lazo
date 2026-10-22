using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Lazo
{
    // An independent transparent surface lets the liquid rise from the physical
    // screen edge, including the taskbar gap, without resizing the search window.
    internal sealed class LiquidMotion
    {
        private Border _shell;
        private Window _surface;
        private Path _body, _rim, _neck;
        private Stopwatch _clock;
        private EventHandler _render;
        private Action _completed;
        private double _progress, _from, _target, _duration, _width, _height, _floor;
        public void Enter(Border shell, Path shape) { Start(shell, shape, true, null); }
        public void Enter(Border shell, Path shape, Action completed) { Start(shell, shape, true, completed); }
        public void Leave(Border shell, Path shape, Action completed) { Start(shell, shape, false, completed); }

        private void Start(Border shell, Path shape, bool entering, Action completed)
        {
            double from = ReferenceEquals(shell, _shell) ? _progress : (entering ? 0 : 1);
            Cancel();
            _shell = shell;
            _from = from; _target = entering ? 1 : 0; _progress = from;
            _duration = Math.Max(40, (entering ? 720 : 430) * Math.Abs(_target - from));
            _completed = completed;
            shape.Visibility = Visibility.Collapsed;
            shell.UpdateLayout();
            if (!SystemParameters.ClientAreaAnimation)
            {
                _progress = _target; shell.Opacity = entering ? 1 : 0;
                if (completed != null) completed();
                return;
            }
            _width = Math.Max(28, shell.ActualWidth); _height = Math.Max(28, shell.ActualHeight);
            Point origin = shell.PointToScreen(new Point());
            Matrix fromDevice = PresentationSource.FromVisual(shell).CompositionTarget.TransformFromDevice;
            Point position = fromDevice.Transform(origin);
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)origin.X, (int)origin.Y));
            _floor = Math.Max(_height, (screen.Bounds.Bottom - origin.Y) * fromDevice.M22);
            Canvas canvas = new Canvas { IsHitTestVisible = false };
            _surface = new Window { Owner = Window.GetWindow(shell), WindowStyle = WindowStyle.None,
                AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
                ShowActivated = false, Topmost = true, ResizeMode = ResizeMode.NoResize,
                Left = position.X - 12, Top = position.Y - 12, Width = _width + 24, Height = _floor + 12,
                Content = canvas, IsHitTestVisible = false };
            var water = new LinearGradientBrush();
            water.StartPoint = new Point(.15, 0); water.EndPoint = new Point(.8, 1);
            // El agua toma los colores del tema y el acento, con reflejos claros en los bordes.
            Color deep = Theme.Parse(Theme.P.Shell2), mid = Theme.Parse(Theme.P.Shell0), accent = Theme.AccentColor;
            water.GradientStops.Add(new GradientStop(Color.FromArgb(235, 245, 245, 247), 0));
            water.GradientStops.Add(new GradientStop(Color.FromArgb(200, mid.R, mid.G, mid.B), .18));
            water.GradientStops.Add(new GradientStop(Color.FromArgb(215, deep.R, deep.G, deep.B), .48));
            water.GradientStops.Add(new GradientStop(Color.FromArgb(205, accent.R, accent.G, accent.B), .83));
            water.GradientStops.Add(new GradientStop(Color.FromArgb(240, 245, 245, 247), 1));
            water.Freeze();
            _neck = new Path { Fill = water };
            _body = new Path { Fill = water, Stroke = new SolidColorBrush(Color.FromArgb(150, 240, 240, 240)), StrokeThickness = .8 };
            _rim = new Path { Stroke = new LinearGradientBrush(Color.FromArgb(225,255,255,255), Colors.Transparent, 90), StrokeThickness = 1.7 };
            foreach (Path part in new[] { _neck, _body, _rim })
            { Canvas.SetLeft(part, 12); Canvas.SetTop(part, 12); canvas.Children.Add(part); }
            _surface.Show();
            _clock = Stopwatch.StartNew();
            Draw();
            _render = (s, e) => Tick();
            CompositionTarget.Rendering += _render;
        }
        private void Tick()
        {
            double time = Math.Min(1, _clock.Elapsed.TotalMilliseconds / _duration);
            _progress = _from + (_target - _from) * time;
            Draw();
            if (time < 1) return;
            Action done = _completed;
            _shell.Opacity = _target;
            Cancel();
            if (done != null) done();
        }
        private static double Smooth(double v) { v = Math.Max(0, Math.Min(1,v)); return v*v*(3-2*v); }
        private static double Mix(double a, double b, double t) { return a + (b-a)*t; }
        private void Draw()
        {
            double p = _progress;
            double rise = Smooth(p/.48), spread = Smooth((p-.26)/.57);
            double cx = _width/2;
            double cy = Mix(_floor+19, _height/2, rise);
            double rx = Mix(16, _width/2-1, spread);
            double ry = Mix(24, _height/2-1, spread);
            double wobble = Math.Sin((p-.48)*Math.PI*5) * Math.Pow(1-Smooth((p-.48)/.52),2) * Smooth((p-.40)/.12);
            rx += wobble*5; ry -= wobble*3;
            double round = Mix(Math.Min(rx,ry)*.65, _shell.CornerRadius.TopLeft, spread);
            round = Math.Min(round, Math.Min(rx,ry));
            double l=cx-rx, r=cx+rx, t=cy-ry, b=cy+ry;
            double arch = (1-spread)*ry*.2;
            Point[] ends = { new Point(l+round,t), new Point(r-round,t), new Point(r,t+round),
                new Point(r,b-round), new Point(r-round,b), new Point(l+round,b), new Point(l,b-round), new Point(l,t+round) };
            Point[] c1 = { new Point(Mix(l+round,r-round,.33),t), new Point(r-round*.45,t),
                new Point(r+wobble*2,cy), new Point(r,b-round*.45), new Point(cx+rx*.35,b),
                new Point(l+round*.45,b), new Point(l,b-round*1.3), new Point(l,t+round*.45) };
            Point[] c2 = { new Point(Mix(l+round,r-round,.67),t), new Point(r,t+round*.45),
                new Point(r,b-round*1.3), new Point(r-round*.45,b), new Point(cx-rx*.35,b),
                new Point(l,b-round*.45), new Point(l-wobble*2,cy), new Point(l+round*.45,t) };
            StreamGeometry body = new StreamGeometry();
            double step = Math.PI/4, tangent = 4.0/3*Math.Tan(Math.PI/16);
            using (var g=body.Open())
            {
                double angle = -Math.PI*5/8;
                g.BeginFigure(Blend(Drop(cx,cy,rx,ry,angle),ends[0],spread),true,true);
                for(int i=0;i<8;i++)
                {
                    double next=angle+step;
                    Point start=Drop(cx,cy,rx,ry,angle), end=Drop(cx,cy,rx,ry,next);
                    Vector d1=Slope(rx,ry,angle)*tangent, d2=Slope(rx,ry,next)*tangent;
                    g.BezierTo(Blend(start+d1,c1[i],spread),Blend(end-d2,c2[i],spread),
                        Blend(end,ends[(i+1)%8],spread),true,false);
                    angle=next;
                }
            }
            body.Freeze(); _body.Data=body;
            double attach=1-Smooth((p-.22)/.25), stem=11*attach;
            StreamGeometry neck=new StreamGeometry();
            using(var g=neck.Open())
            {
                g.BeginFigure(new Point(cx-32*attach,_floor+2),true,true);
                g.BezierTo(new Point(cx-stem,_floor-8),new Point(cx-stem,cy+ry*.3),new Point(cx-ry*.45,cy+ry*.3),true,false);
                g.LineTo(new Point(cx+ry*.45,cy+ry*.3),true,false);
                g.BezierTo(new Point(cx+stem,cy+ry*.3),new Point(cx+stem,_floor-8),new Point(cx+32*attach,_floor+2),true,false);
            }
            neck.Freeze(); _neck.Data=neck; _neck.Opacity=attach;
            StreamGeometry rim=new StreamGeometry();
            using(var g=rim.Open())
            {
                g.BeginFigure(new Point(l+round*.65,cy),false,false);
                g.BezierTo(new Point(l+round*.35,t+5),new Point(cx-rx*.3,t+3-arch*.65),new Point(cx+rx*.4,t+4),true,false);
            }
            rim.Freeze(); _rim.Data=rim;
            double reveal=Smooth((p-.78)/.22);
            _shell.Opacity=reveal;
            _surface.Opacity=(1-reveal)*Smooth(p/.07);
        }
        private static Point Blend(Point a, Point b, double t) { return new Point(Mix(a.X,b.X,t),Mix(a.Y,b.Y,t)); }
        private static Point Drop(double cx,double cy,double rx,double ry,double a)
        { return new Point(cx+rx*Math.Cos(a)*(.85+.15*Math.Sin(a)),cy+ry*Math.Sin(a)); }
        private static Vector Slope(double rx,double ry,double a)
        { return new Vector(rx*(-.85*Math.Sin(a)+.15*Math.Cos(2*a)),ry*Math.Cos(a)); }
        public bool Active { get { return _render != null; } }

        public void Cancel()
        {
            if (_render != null) CompositionTarget.Rendering -= _render;
            _render=null;
            if (_clock != null) _clock.Stop();
            _clock=null; _completed=null;
            if (_surface != null) { _surface.Close(); _surface=null; }
        }
    }
}
