using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Lazo
{
    internal enum AnnotationTool : byte
    {
        Pen = 1,
        Marker = 2,
        Arrow = 3,
        Box = 4,
        /// <summary>Puntero láser: brilla y desaparece solo poco después de levantar el mouse.</summary>
        Laser = 5,
        Eraser = 100
    }

    /// <summary>
    /// Cambio sobre los trazos de una pantalla compartida. Las coordenadas van normalizadas a 0–65535
    /// sobre la fuente compartida, así que no dependen de la resolución ni de la escala de cada equipo.
    /// Formato: tipo · id · [Begin: herramienta color n puntos] · [Points: n puntos].
    /// </summary>
    internal sealed class AnnotationOp
    {
        public const byte Begin = 1, Points = 2, Erase = 3, Clear = 4;
        public const int MaxPoints = 4096;
        public byte Kind;
        public int Id;
        public AnnotationTool Tool;
        public uint Color;
        public ushort[] Xs = new ushort[0];
        public ushort[] Ys = new ushort[0];

        public byte[] ToBytes()
        {
            using (MemoryStream buffer = new MemoryStream(16 + Xs.Length * 4))
            using (BinaryWriter writer = new BinaryWriter(buffer))
            {
                writer.Write(Kind);
                writer.Write(Id);
                if (Kind == Begin)
                {
                    writer.Write((byte)Tool);
                    writer.Write(Color);
                }
                if (Kind == Begin || Kind == Points)
                {
                    writer.Write((ushort)Xs.Length);
                    for (int i = 0; i < Xs.Length; i++) { writer.Write(Xs[i]); writer.Write(Ys[i]); }
                }
                writer.Flush();
                return buffer.ToArray();
            }
        }

        /// <summary>Lee un cambio; devuelve null si no es válido.</summary>
        public static AnnotationOp Parse(byte[] data)
        {
            try
            {
                using (BinaryReader reader = new BinaryReader(new MemoryStream(data)))
                {
                    AnnotationOp op = new AnnotationOp { Kind = reader.ReadByte(), Id = reader.ReadInt32() };
                    if (op.Kind < Begin || op.Kind > Clear) return null;
                    if (op.Kind == Begin)
                    {
                        op.Tool = (AnnotationTool)reader.ReadByte();
                        // Las versiones sin láser descartan esa herramienta: el trazo simplemente no se les ve.
                        if (op.Tool < AnnotationTool.Pen || op.Tool > AnnotationTool.Laser) return null;
                        op.Color = reader.ReadUInt32();
                    }
                    if (op.Kind == Begin || op.Kind == Points)
                    {
                        int count = reader.ReadUInt16();
                        if (count > MaxPoints || (op.Kind == Begin && count == 0)) return null;
                        op.Xs = new ushort[count];
                        op.Ys = new ushort[count];
                        for (int i = 0; i < count; i++) { op.Xs[i] = reader.ReadUInt16(); op.Ys[i] = reader.ReadUInt16(); }
                    }
                    return op;
                }
            }
            catch (EndOfStreamException) { return null; }
        }

        public static ushort Norm(double value)
        {
            return (ushort)Math.Round(Math.Max(0, Math.Min(1, value)) * 65535);
        }
    }

    /// <summary>Estado de los trazos; quien comparte lo conserva para enviarlo a quien se une tarde.</summary>
    internal sealed class AnnotationModel
    {
        internal sealed class Stroke
        {
            public int Id;
            public AnnotationTool Tool;
            public uint Color;
            public readonly List<ushort> Xs = new List<ushort>();
            public readonly List<ushort> Ys = new List<ushort>();
            public bool IsShape { get { return Tool == AnnotationTool.Arrow || Tool == AnnotationTool.Box; } }
        }

        private const int MaxStrokes = 2000;
        private const int MaxStrokePoints = 20000;
        private readonly List<Stroke> _order = new List<Stroke>();
        private readonly Dictionary<int, Stroke> _byId = new Dictionary<int, Stroke>();

        public IEnumerable<Stroke> Strokes { get { return _order; } }

        public Stroke Find(int id)
        {
            Stroke stroke;
            return _byId.TryGetValue(id, out stroke) ? stroke : null;
        }

        /// <summary>Aplica un cambio. Devuelve false si no tiene efecto (id repetido, trazo inexistente, límites).</summary>
        public bool Apply(AnnotationOp op)
        {
            switch (op.Kind)
            {
                case AnnotationOp.Begin:
                    if (_byId.ContainsKey(op.Id) || _order.Count >= MaxStrokes) return false;
                    Stroke stroke = new Stroke { Id = op.Id, Tool = op.Tool, Color = op.Color };
                    _order.Add(stroke);
                    _byId[op.Id] = stroke;
                    Append(stroke, op);
                    return true;
                case AnnotationOp.Points:
                    Stroke target = Find(op.Id);
                    if (target == null || op.Xs.Length == 0) return false;
                    return Append(target, op);
                case AnnotationOp.Erase:
                    Stroke gone = Find(op.Id);
                    if (gone == null) return false;
                    _order.Remove(gone);
                    _byId.Remove(op.Id);
                    return true;
                case AnnotationOp.Clear:
                    if (_order.Count == 0) return false;
                    _order.Clear();
                    _byId.Clear();
                    return true;
            }
            return false;
        }

        private static bool Append(Stroke stroke, AnnotationOp op)
        {
            for (int i = 0; i < op.Xs.Length; i++)
            {
                // Flecha y recuadro solo necesitan el punto inicial y el último.
                if (stroke.IsShape && stroke.Xs.Count >= 2)
                {
                    stroke.Xs[1] = op.Xs[i];
                    stroke.Ys[1] = op.Ys[i];
                    continue;
                }
                if (stroke.Xs.Count >= MaxStrokePoints) return false;
                stroke.Xs.Add(op.Xs[i]);
                stroke.Ys.Add(op.Ys[i]);
            }
            return true;
        }

        /// <summary>Los cambios que reconstruyen el estado actual desde cero.</summary>
        public List<AnnotationOp> Snapshot()
        {
            List<AnnotationOp> ops = new List<AnnotationOp>();
            foreach (Stroke stroke in _order)
            {
                // El láser es efímero: quien se une tarde no lo recibe.
                if (stroke.Tool == AnnotationTool.Laser) continue;
                for (int start = 0; start < stroke.Xs.Count; start += AnnotationOp.MaxPoints)
                {
                    int count = Math.Min(AnnotationOp.MaxPoints, stroke.Xs.Count - start);
                    ops.Add(new AnnotationOp
                    {
                        Kind = start == 0 ? AnnotationOp.Begin : AnnotationOp.Points,
                        Id = stroke.Id,
                        Tool = stroke.Tool,
                        Color = stroke.Color,
                        Xs = stroke.Xs.GetRange(start, count).ToArray(),
                        Ys = stroke.Ys.GetRange(start, count).ToArray()
                    });
                }
            }
            return ops;
        }
    }

    /// <summary>
    /// Capa transparente que pinta los trazos y, con <see cref="Drawing"/> activo, los dibuja con el mouse.
    /// La usan la superposición de quien comparte y la ventana de quien mira.
    /// </summary>
    internal sealed class AnnotationLayer : Canvas
    {
        public static readonly uint[] Colors = { 0xFFF25C45, 0xFFF2C94C, 0xFF27AE60, 0xFF3B82F6, 0xFFFFFFFF, 0xFF111111 };
        private static readonly Random Ids = new Random();
        private readonly AnnotationModel _model = new AnnotationModel();
        private readonly Dictionary<int, Shape> _shapes = new Dictionary<int, Shape>();
        private readonly HashSet<int> _mine = new HashSet<int>();
        private int _current;
        private bool _drawing;
        // Los puntos del trazo en curso se agrupan y salen como mucho cada 16 ms: un solo paquete
        // y un solo redibujado por fotograma, en lugar de uno por cada movimiento del mouse.
        private readonly List<ushort> _batchX = new List<ushort>(), _batchY = new List<ushort>();
        private readonly System.Windows.Threading.DispatcherTimer _batchTimer;

        public AnnotationTool Tool = AnnotationTool.Pen;
        public uint Color = Colors[0];
        /// <summary>Visor: el borrador solo quita los trazos hechos en este equipo.</summary>
        public bool OnlyMine;

        /// <summary>Cambio hecho aquí con el mouse; ya está aplicado en la capa.</summary>
        public event Action<AnnotationOp> Local;

        public AnnotationLayer()
        {
            Background = null;
            ClipToBounds = true;
            IsHitTestVisible = false;
            SizeChanged += (s, e) => Redraw();
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            LostMouseCapture += (s, e) => { FlushBatch(); FinishStroke(_current); _current = 0; };
            _batchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _batchTimer.Tick += (s, e) => FlushBatch();
            _laserTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _laserTimer.Tick += (s, e) => SweepLasers();
        }

        private void FlushBatch()
        {
            _batchTimer.Stop();
            if (_batchX.Count == 0 || _current <= 0) { _batchX.Clear(); _batchY.Clear(); return; }
            AnnotationOp op = new AnnotationOp { Kind = AnnotationOp.Points, Id = _current, Xs = _batchX.ToArray(), Ys = _batchY.ToArray() };
            _batchX.Clear();
            _batchY.Clear();
            Emit(op);
        }

        public bool Drawing
        {
            get { return _drawing; }
            set
            {
                _drawing = value;
                // Un fondo casi transparente hace que la capa reciba el mouse en toda su superficie.
                Background = value ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0)) : null;
                IsHitTestVisible = value;
                Cursor = value ? (Tool == AnnotationTool.Eraser ? Cursors.Hand : Cursors.Pen) : null;
                if (!value && IsMouseCaptured) ReleaseMouseCapture();
            }
        }

        public void SetTool(AnnotationTool tool)
        {
            Tool = tool;
            if (_drawing) Cursor = tool == AnnotationTool.Eraser ? Cursors.Hand : Cursors.Pen;
        }

        /// <summary>Aplica un cambio llegado de la red.</summary>
        public void Apply(AnnotationOp op)
        {
            if (!_model.Apply(op)) return;
            Render(op);
            RaiseStrokes();
        }

        /// <summary>Cambió el número de trazos (propios o llegados de la red).</summary>
        public event Action StrokesChanged;

        public bool HasStrokes { get { return _shapes.Count > 0; } }

        private void RaiseStrokes()
        {
            Action handler = StrokesChanged;
            if (handler != null) handler();
        }

        /// <summary>Borra los trazos hechos en este equipo y avisa de cada borrado.</summary>
        public void EraseMine()
        {
            foreach (int id in _mine.ToArray())
                if (!Emit(new AnnotationOp { Kind = AnnotationOp.Erase, Id = id })) _mine.Remove(id);
        }

        public void ClearAll()
        {
            Emit(new AnnotationOp { Kind = AnnotationOp.Clear });
        }

        /// <summary>Aplica un cambio propio y lo anuncia, como si se hubiera hecho con el mouse.</summary>
        internal void Commit(AnnotationOp op)
        {
            if (Emit(op) && op.Kind == AnnotationOp.Begin) _mine.Add(op.Id);
        }

        private bool Emit(AnnotationOp op)
        {
            if (!_model.Apply(op)) return false;
            Render(op);
            RaiseStrokes();
            Action<AnnotationOp> handler = Local;
            if (handler != null) handler(op);
            return true;
        }

        private void Render(AnnotationOp op)
        {
            if (op.Kind == AnnotationOp.Clear)
            {
                Children.Clear();
                _shapes.Clear();
                _mine.Clear();
                _lasers.Clear();
                return;
            }
            if (op.Kind == AnnotationOp.Erase)
            {
                Shape shape;
                if (_shapes.TryGetValue(op.Id, out shape))
                {
                    if (_lasers.Remove(op.Id)) FadeOut(shape);
                    else Children.Remove(shape);
                }
                _shapes.Remove(op.Id);
                _mine.Remove(op.Id);
                return;
            }
            AnnotationModel.Stroke stroke = _model.Find(op.Id);
            if (stroke == null) return;
            if (stroke.Tool == AnnotationTool.Laser)
            {
                _lasers[op.Id] = DateTime.UtcNow;
                if (!_laserTimer.IsEnabled) _laserTimer.Start();
            }
            Shape existing;
            if (_shapes.TryGetValue(op.Id, out existing)) Children.Remove(existing);
            Shape made = Build(stroke);
            _shapes[op.Id] = made;
            Children.Add(made);
        }

        // ── Láser: quien lo traza lo borra para todos un momento después de levantar el mouse ──

        /// <summary>Tiempo que el láser sigue visible tras levantar el mouse.</summary>
        public static readonly TimeSpan LaserLinger = TimeSpan.FromSeconds(1.2);
        /// <summary>Si quien lo trazó desaparece sin borrarlo, cada equipo lo quita por su cuenta.</summary>
        private static readonly TimeSpan LaserOrphan = TimeSpan.FromSeconds(6);
        private readonly Dictionary<int, DateTime> _lasers = new Dictionary<int, DateTime>();
        private readonly System.Windows.Threading.DispatcherTimer _laserTimer;

        private void SweepLasers()
        {
            if (_lasers.Count == 0) { _laserTimer.Stop(); return; }
            foreach (KeyValuePair<int, DateTime> laser in _lasers.ToArray())
            {
                if (_current == laser.Key || DateTime.UtcNow - laser.Value < LaserOrphan) continue;
                // Solo en este equipo: no se anuncia, porque quien lo trazó ya no está para borrarlo.
                AnnotationOp gone = new AnnotationOp { Kind = AnnotationOp.Erase, Id = laser.Key };
                if (_model.Apply(gone)) { Render(gone); RaiseStrokes(); }
                else _lasers.Remove(laser.Key);
            }
        }

        /// <summary>Terminó un trazo propio (se levantó el mouse). Si es láser, se borra para todos al poco.</summary>
        internal void FinishStroke(int id)
        {
            AnnotationModel.Stroke stroke = id > 0 ? _model.Find(id) : null;
            if (stroke == null || stroke.Tool != AnnotationTool.Laser) return;
            System.Windows.Threading.DispatcherTimer later = new System.Windows.Threading.DispatcherTimer { Interval = LaserLinger };
            later.Tick += (s, e) =>
            {
                later.Stop();
                if (_model.Find(id) != null) Emit(new AnnotationOp { Kind = AnnotationOp.Erase, Id = id });
            };
            later.Start();
        }

        private void FadeOut(Shape shape)
        {
            System.Windows.Media.Animation.DoubleAnimation fade = new System.Windows.Media.Animation.DoubleAnimation(shape.Opacity, 0, TimeSpan.FromMilliseconds(450));
            fade.Completed += (s, e) => Children.Remove(shape);
            shape.BeginAnimation(OpacityProperty, fade);
        }

        private void Redraw()
        {
            Children.Clear();
            _shapes.Clear();
            foreach (AnnotationModel.Stroke stroke in _model.Strokes)
            {
                Shape shape = Build(stroke);
                _shapes[stroke.Id] = shape;
                Children.Add(shape);
            }
        }

        private double Scale { get { return Math.Max(0.6, ActualWidth / 1280.0); } }

        private double StrokeWidth(AnnotationTool tool)
        {
            double base_ = tool == AnnotationTool.Marker ? 16 : tool == AnnotationTool.Pen ? 3.5 : tool == AnnotationTool.Laser ? 4.5 : 4;
            return base_ * Scale;
        }

        private Point At(ushort x, ushort y)
        {
            return new Point(x / 65535.0 * ActualWidth, y / 65535.0 * ActualHeight);
        }

        private Shape Build(AnnotationModel.Stroke stroke)
        {
            uint c = stroke.Color;
            System.Windows.Media.Color color = System.Windows.Media.Color.FromArgb(stroke.Tool == AnnotationTool.Marker ? (byte)0x70 : (byte)(c >> 24),
                (byte)(c >> 16), (byte)(c >> 8), (byte)c);
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            double thickness = StrokeWidth(stroke.Tool);
            Geometry geometry;
            Point first = At(stroke.Xs[0], stroke.Ys[0]);
            Point last = At(stroke.Xs[stroke.Xs.Count - 1], stroke.Ys[stroke.Ys.Count - 1]);
            if (stroke.Tool == AnnotationTool.Box)
            {
                geometry = new RectangleGeometry(new Rect(first, last));
            }
            else if (stroke.Tool == AnnotationTool.Arrow)
            {
                PathGeometry arrow = new PathGeometry();
                arrow.Figures.Add(new PathFigure(first, new[] { new LineSegment(last, true) }, false));
                Vector direction = last - first;
                if (direction.Length > 0.5)
                {
                    direction.Normalize();
                    double head = Math.Max(12 * Scale, thickness * 4);
                    Vector side = new Vector(-direction.Y, direction.X);
                    Point left = last - direction * head + side * head * 0.55;
                    Point right = last - direction * head - side * head * 0.55;
                    arrow.Figures.Add(new PathFigure(left, new PathSegment[] { new LineSegment(last, true), new LineSegment(right, true) }, false));
                }
                geometry = arrow;
            }
            else
            {
                StreamGeometry line = new StreamGeometry();
                using (StreamGeometryContext context = line.Open())
                {
                    context.BeginFigure(first, false, false);
                    List<Point> points = new List<Point>(stroke.Xs.Count);
                    for (int i = 1; i < stroke.Xs.Count; i++) points.Add(At(stroke.Xs[i], stroke.Ys[i]));
                    if (points.Count == 0) points.Add(new Point(first.X + 0.01, first.Y));
                    context.PolyLineTo(points, true, true);
                }
                geometry = line;
            }
            geometry.Freeze();
            System.Windows.Shapes.Path path = new System.Windows.Shapes.Path
            {
                Data = geometry,
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
                Tag = stroke.Id
            };
            // Láser: trazo intenso del color elegido con un halo de luz, visible en fondos claros y oscuros.
            if (stroke.Tool == AnnotationTool.Laser)
                path.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 16 * Scale, Opacity = 0.95 };
            return path;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (!_drawing || ActualWidth < 1 || ActualHeight < 1) return;
            e.Handled = true;
            CaptureMouse();
            Point p = e.GetPosition(this);
            if (Tool == AnnotationTool.Eraser) { _current = -1; EraseAt(p); return; }
            int id;
            lock (Ids) { do id = Ids.Next(1, int.MaxValue); while (_model.Find(id) != null); }
            _current = id;
            _mine.Add(id);
            Emit(new AnnotationOp
            {
                Kind = AnnotationOp.Begin, Id = id, Tool = Tool, Color = Color,
                Xs = new[] { AnnotationOp.Norm(p.X / ActualWidth) }, Ys = new[] { AnnotationOp.Norm(p.Y / ActualHeight) }
            });
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_drawing || _current == 0 || e.LeftButton != MouseButtonState.Pressed) return;
            e.Handled = true;
            Point p = e.GetPosition(this);
            if (_current == -1) { EraseAt(p); return; }
            _batchX.Add(AnnotationOp.Norm(p.X / ActualWidth));
            _batchY.Add(AnnotationOp.Norm(p.Y / ActualHeight));
            if (_batchX.Count >= AnnotationOp.MaxPoints) FlushBatch();
            else if (!_batchTimer.IsEnabled) _batchTimer.Start();
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_drawing) return;
            e.Handled = true;
            FlushBatch();
            FinishStroke(_current);
            _current = 0;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        private void EraseAt(Point p)
        {
            double reach = 8 * Scale;
            foreach (AnnotationModel.Stroke stroke in _model.Strokes.Reverse().ToArray())
            {
                if (OnlyMine && !_mine.Contains(stroke.Id)) continue;
                if (!Hits(stroke, p, reach + StrokeWidth(stroke.Tool) / 2)) continue;
                Emit(new AnnotationOp { Kind = AnnotationOp.Erase, Id = stroke.Id });
                return;
            }
        }

        private bool Hits(AnnotationModel.Stroke stroke, Point p, double reach)
        {
            Point first = At(stroke.Xs[0], stroke.Ys[0]);
            Point last = At(stroke.Xs[stroke.Xs.Count - 1], stroke.Ys[stroke.Ys.Count - 1]);
            if (stroke.Tool == AnnotationTool.Box)
            {
                Point a = new Point(first.X, last.Y), b = new Point(last.X, first.Y);
                return Near(p, first, a) <= reach || Near(p, a, last) <= reach || Near(p, last, b) <= reach || Near(p, b, first) <= reach;
            }
            if (stroke.Tool == AnnotationTool.Arrow) return Near(p, first, last) <= reach;
            Point previous = first;
            if (stroke.Xs.Count == 1) return (p - first).Length <= reach;
            for (int i = 1; i < stroke.Xs.Count; i++)
            {
                Point next = At(stroke.Xs[i], stroke.Ys[i]);
                if (Near(p, previous, next) <= reach) return true;
                previous = next;
            }
            return false;
        }

        private static double Near(Point p, Point a, Point b)
        {
            Vector ab = b - a;
            double length = ab.LengthSquared;
            if (length < 0.0001) return (p - a).Length;
            double t = Math.Max(0, Math.Min(1, Vector.Multiply(p - a, ab) / length));
            return (p - (a + ab * t)).Length;
        }
    }

    /// <summary>Herramientas y colores en una fila, para las barras oscuras de quien comparte y de quien mira.</summary>
    internal sealed class AnnotationToolbar : StackPanel
    {
        private readonly AnnotationLayer _layer;
        private readonly List<KeyValuePair<AnnotationTool, Border>> _tools = new List<KeyValuePair<AnnotationTool, Border>>();
        private readonly List<KeyValuePair<uint, Border>> _swatches = new List<KeyValuePair<uint, Border>>();

        public AnnotationToolbar(AnnotationLayer layer, string clearTip, Action clear)
        {
            _layer = layer;
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            AddTool(AnnotationTool.Pen, "pen", "Lápiz");
            AddTool(AnnotationTool.Marker, "marker", "Marcador");
            AddTool(AnnotationTool.Arrow, "arrow", "Flecha");
            AddTool(AnnotationTool.Box, "box", "Recuadro");
            AddTool(AnnotationTool.Laser, "laser", "Láser: el trazo se desvanece solo");
            AddTool(AnnotationTool.Eraser, "eraser", "Borrador: pulsa un trazo para quitarlo");
            Children.Add(new Border { Width = 1, Height = 18, Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x40, 255, 255, 255)), Margin = new Thickness(8, 0, 6, 0) });
            foreach (uint color in AnnotationLayer.Colors) AddSwatch(color);
            if (clear != null)
            {
                Border trash = Chip("trash", clearTip);
                trash.MouseLeftButtonUp += (s, e) => { clear(); e.Handled = true; };
                trash.Margin = new Thickness(8, 0, 0, 0);
                Children.Add(trash);
            }
            Sync();
        }

        private void AddTool(AnnotationTool tool, string glyph, string tip)
        {
            Border chip = Chip(glyph, tip);
            chip.MouseLeftButtonUp += (s, e) => { _layer.SetTool(tool); Sync(); e.Handled = true; };
            _tools.Add(new KeyValuePair<AnnotationTool, Border>(tool, chip));
            Children.Add(chip);
        }

        private void AddSwatch(uint color)
        {
            Border ring = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(2, 0, 2, 0),
                BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Child = new Ellipse
                {
                    Width = 14,
                    Height = 14,
                    Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color)),
                    Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x50, 255, 255, 255)),
                    StrokeThickness = 1
                }
            };
            ring.MouseLeftButtonUp += (s, e) =>
            {
                _layer.Color = color;
                if (_layer.Tool == AnnotationTool.Eraser) _layer.SetTool(AnnotationTool.Pen);
                Sync();
                e.Handled = true;
            };
            _swatches.Add(new KeyValuePair<uint, Border>(color, ring));
            Children.Add(ring);
        }

        private static Border Chip(string glyph, string tip)
        {
            Border chip = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Margin = new Thickness(1, 0, 1, 0),
                Cursor = Cursors.Hand,
                ToolTip = tip,
                Background = Brushes.Transparent,
                Child = Icons.Make(glyph, 16, Brushes.White, 2)
            };
            chip.MouseEnter += (s, e) => { if (chip.Tag == null) chip.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x26, 255, 255, 255)); };
            chip.MouseLeave += (s, e) => { if (chip.Tag == null) chip.Background = Brushes.Transparent; };
            return chip;
        }

        private void Sync()
        {
            foreach (KeyValuePair<AnnotationTool, Border> pair in _tools)
            {
                bool on = pair.Key == _layer.Tool;
                pair.Value.Tag = on ? (object)true : null;
                pair.Value.Background = on ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 255, 255, 255)) : Brushes.Transparent;
            }
            foreach (KeyValuePair<uint, Border> pair in _swatches)
                pair.Value.BorderBrush = pair.Key == _layer.Color && _layer.Tool != AnnotationTool.Eraser ? Brushes.White : Brushes.Transparent;
        }
    }
}
