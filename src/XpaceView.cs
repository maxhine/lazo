using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lazo
{
    /// <summary>Estado de Xpace: sobrevive a que se cierre y se vuelva a abrir el panel, y a reconstruir la interfaz.</summary>
    internal sealed class XpaceSession
    {
        public string Path;
        public bool Running;
        public XResult Result;
        public string Error;
        public string Tab = "map";
        public XNode MapNode;
        public readonly HashSet<XNode> Expanded = new HashSet<XNode>();
        public double Offset;
        public string SelectedKey;
        public XpaceScanner Scanner;

        /// <summary>Empezó o terminó un análisis: hay que reconstruir la pantalla.</summary>
        public event Action Changed;
        /// <summary>Avance durante el análisis (cada ~150 ms).</summary>
        public event Action Progress;

        private CancellationTokenSource _run;
        private readonly DispatcherTimer _timer;
        private readonly Dispatcher _dispatcher;

        public XpaceSession(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
            _timer.Tick += (s, e) => { if (Progress != null) Progress(); };
        }

        public void Start(string path)
        {
            if (Running) return;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path)) { Error = "Esa carpeta no existe."; Raise(); return; }
            Path = path;
            Error = null;
            Result = null;
            Expanded.Clear();
            MapNode = null;
            Offset = 0;
            SelectedKey = null;
            Running = true;
            Scanner = new XpaceScanner();
            CancellationTokenSource run = new CancellationTokenSource();
            _run = run;
            XpaceScanner scanner = Scanner;
            _timer.Start();
            Raise();
            Task.Run(() =>
            {
                XResult result = null;
                string error = null;
                try { result = scanner.Scan(path, run.Token); }
                catch (Exception ex) { error = ex.Message; }
                _dispatcher.BeginInvoke((Action)(() =>
                {
                    if (_run != run) return;
                    _timer.Stop();
                    Running = false;
                    Result = result;
                    Error = error;
                    if (result != null && !result.Cancelled) { Expanded.Add(result.Root); MapNode = result.Root; }
                    if (result != null && result.Cancelled) { Result = null; Error = "Análisis cancelado."; }
                    Raise();
                }));
            });
        }

        public void Cancel() { if (_run != null && Running) _run.Cancel(); }

        private void Raise() { if (Changed != null) Changed(); }
    }

    /// <summary>Una fila de la lista de Xpace.</summary>
    internal sealed class XRow
    {
        public int Depth;
        public string Name, Sub, Path, Key;
        public long Size, Files = -1, Modified;
        public double Fraction = -1;
        public XNode Node;
        public bool IsFile, Expandable, Expanded;
    }

    /// <summary>
    /// Lista virtual dibujada a mano: con cientos de miles de filas solo se dibujan las que se ven, así que
    /// desplazarse y expandir carpetas es inmediato.
    /// </summary>
    internal sealed class XpaceList : FrameworkElement
    {
        private const double RowHeight = 30, HeaderHeight = 28;
        private List<XRow> _rows = new List<XRow>();
        private string[] _headers = new string[5];
        private int _hover = -1;
        private int _selected = -1;
        private bool _dragging;
        public double Offset;

        public event Action<XRow> Toggle;
        public event Action<XRow> Activate;
        public event Action<XRow> Selected;
        public event Action<XRow> ContextRequested;

        public XpaceList()
        {
            Focusable = true;
            ClipToBounds = true;
            Cursor = Cursors.Arrow;
            ContextMenuOpening += (s, e) =>
            {
                XRow row = Current;
                if (row == null || ContextRequested == null) { e.Handled = true; return; }
                ContextRequested(row);
            };
        }

        public XRow Current { get { return _selected >= 0 && _selected < _rows.Count ? _rows[_selected] : null; } }

        /// <summary>Cabeceras: nombre, barra, tamaño, archivos, fecha (null: la columna no se muestra).</summary>
        public void SetRows(List<XRow> rows, string[] headers, string selectedKey, double offset)
        {
            _rows = rows;
            _headers = headers;
            _selected = selectedKey == null ? -1 : rows.FindIndex(r => r.Key == selectedKey);
            Offset = offset;
            Clamp();
            InvalidateVisual();
        }

        private double Body { get { return Math.Max(0, ActualHeight - HeaderHeight); } }
        private double Total { get { return _rows.Count * RowHeight; } }
        private void Clamp() { Offset = Math.Max(0, Math.Min(Offset, Math.Max(0, Total - Body))); }

        protected override Size MeasureOverride(Size available)
        {
            return new Size(double.IsInfinity(available.Width) ? 400 : available.Width, double.IsInfinity(available.Height) ? 320 : available.Height);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            Offset -= e.Delta / 120.0 * RowHeight * 2;
            Clamp();
            InvalidateVisual();
            e.Handled = true;
        }

        private int RowAt(double y)
        {
            if (y < HeaderHeight) return -1;
            int index = (int)((y - HeaderHeight + Offset) / RowHeight);
            return index >= 0 && index < _rows.Count ? index : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Point p = e.GetPosition(this);
            if (_dragging)
            {
                ScrollToThumb(p.Y);
                return;
            }
            int hover = RowAt(p.Y);
            if (hover != _hover) { _hover = hover; InvalidateVisual(); }
        }

        protected override void OnMouseLeave(MouseEventArgs e) { _hover = -1; InvalidateVisual(); }

        private void ScrollToThumb(double y)
        {
            double range = Math.Max(1, Body);
            Offset = (y - HeaderHeight) / range * Total - Body / 2;
            Clamp();
            InvalidateVisual();
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            if (_dragging) { _dragging = false; ReleaseMouseCapture(); }
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            Focus();
            Point p = e.GetPosition(this);
            if (e.ChangedButton == MouseButton.Left && p.X > ActualWidth - 14 && Total > Body)
            {
                _dragging = true;
                CaptureMouse();
                ScrollToThumb(p.Y);
                return;
            }
            int index = RowAt(p.Y);
            if (index < 0) return;
            _selected = index;
            XRow row = _rows[index];
            InvalidateVisual();
            if (Selected != null) Selected(row);
            if (e.ChangedButton != MouseButton.Left) return;
            double arrow = 8 + row.Depth * 16;
            if (row.Expandable && p.X >= arrow - 2 && p.X <= arrow + 18 && Toggle != null) Toggle(row);
            else if (e.ClickCount == 2)
            {
                if (row.Expandable && Toggle != null) Toggle(row);
                else if (Activate != null) Activate(row);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_rows.Count == 0) return;
            int index = _selected < 0 ? 0 : _selected;
            int page = (int)(Body / RowHeight) - 1;
            switch (e.Key)
            {
                case Key.Down: index++; break;
                case Key.Up: index--; break;
                case Key.PageDown: index += page; break;
                case Key.PageUp: index -= page; break;
                case Key.Home: index = 0; break;
                case Key.End: index = _rows.Count - 1; break;
                case Key.Right:
                    if (Current != null && Current.Expandable && !Current.Expanded && Toggle != null) Toggle(Current);
                    e.Handled = true;
                    return;
                case Key.Left:
                    if (Current != null && Current.Expandable && Current.Expanded && Toggle != null) Toggle(Current);
                    e.Handled = true;
                    return;
                case Key.Enter:
                    if (Current != null) { if (Current.Expandable && Toggle != null) Toggle(Current); else if (Activate != null) Activate(Current); }
                    e.Handled = true;
                    return;
                default: return;
            }
            index = Math.Max(0, Math.Min(_rows.Count - 1, index));
            _selected = index;
            double top = index * RowHeight, bottom = top + RowHeight;
            if (top < Offset) Offset = top; else if (bottom > Offset + Body) Offset = bottom - Body;
            Clamp();
            InvalidateVisual();
            if (Selected != null) Selected(_rows[index]);
            e.Handled = true;
        }

        public static string SizeText(long bytes)
        {
            CultureInfo c = CultureInfo.CurrentCulture;
            if (bytes >= 1L << 40) return (bytes / (double)(1L << 40)).ToString("0.00", c) + " TB";
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", c) + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0", c) + " MB";
            if (bytes >= 1L << 10) return (bytes / 1024.0).ToString("0", c) + " KB";
            return bytes + " B";
        }

        private static string DateText(long fileTime)
        {
            if (fileTime <= 0) return "";
            try { return DateTime.FromFileTimeUtc(fileTime).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); } catch { return ""; }
        }

        private FormattedText Format(string text, double size, Brush brush, double maxWidth, FontWeight weight, TextAlignment align = TextAlignment.Left)
        {
            FormattedText formatted = new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(Theme.Font, FontStyles.Normal, weight, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = align
            };
            formatted.MaxTextWidth = Math.Max(1, maxWidth);
            return formatted;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth, height = ActualHeight;
            if (width < 20 || height < 20) return;
            Brush ink = Theme.Ink, muted = Theme.Muted, line = Theme.Line;
            Brush hover = Theme.Color(Theme.P.Dark ? "#12FFFFFF" : "#0C000000");
            Brush selected = Theme.NavActive;
            Color accent = Theme.AccentColor;
            Brush fillBrush = new SolidColorBrush(Color.FromArgb(0xC0, accent.R, accent.G, accent.B));
            Brush track = Theme.Color(Theme.P.Dark ? "#18FFFFFF" : "#12000000");

            bool wide = width >= 600;
            double dateW = _headers[4] != null && wide ? 86 : 0, filesW = _headers[3] != null && wide ? 84 : 0, sizeW = _headers[2] != null ? 82 : 0;
            double barW = _headers[1] != null ? (width >= 460 ? 112 : 70) : 0, percentW = _headers[1] != null ? 44 : 0;
            double right = width - 16;
            double dateX = right - dateW, filesX = dateX - filesW, sizeX = filesX - sizeW, percentX = sizeX - percentW, barX = percentX - barW;
            double nameEnd = Math.Min(barX, sizeX) - 10;
            if (_headers[1] == null) nameEnd = sizeX - 10;

            // Cabecera
            dc.DrawText(Format(_headers[0] ?? "", 10.5, muted, nameEnd - 8, FontWeights.SemiBold), new Point(10, 7));
            if (_headers[1] != null) dc.DrawText(Format(_headers[1], 10.5, muted, barW + percentW, FontWeights.SemiBold), new Point(barX, 7));
            if (_headers[2] != null) dc.DrawText(Format(_headers[2], 10.5, muted, sizeW, FontWeights.SemiBold, TextAlignment.Right), new Point(sizeX, 7));
            if (filesW > 0) dc.DrawText(Format(_headers[3], 10.5, muted, filesW, FontWeights.SemiBold, TextAlignment.Right), new Point(filesX, 7));
            if (dateW > 0) dc.DrawText(Format(_headers[4], 10.5, muted, dateW, FontWeights.SemiBold, TextAlignment.Right), new Point(dateX, 7));
            dc.DrawRectangle(line, null, new Rect(0, HeaderHeight - 1, width, 1));

            dc.PushClip(new RectangleGeometry(new Rect(0, HeaderHeight, width, Math.Max(0, height - HeaderHeight))));
            int first = (int)(Offset / RowHeight);
            int last = Math.Min(_rows.Count - 1, (int)((Offset + Body) / RowHeight) + 1);
            for (int i = Math.Max(0, first); i <= last; i++)
            {
                XRow row = _rows[i];
                double y = HeaderHeight + i * RowHeight - Offset;
                if (i == _selected) dc.DrawRoundedRectangle(selected, null, new Rect(2, y + 1, width - 12, RowHeight - 2), 8, 8);
                else if (i == _hover) dc.DrawRoundedRectangle(hover, null, new Rect(2, y + 1, width - 12, RowHeight - 2), 8, 8);

                double x = 8 + row.Depth * 16;
                if (row.Expandable)
                {
                    StreamGeometry chevron = new StreamGeometry();
                    using (StreamGeometryContext g = chevron.Open())
                    {
                        double cy = y + RowHeight / 2;
                        if (row.Expanded) { g.BeginFigure(new Point(x + 3, cy - 2), false, false); g.LineTo(new Point(x + 7, cy + 2), true, false); g.LineTo(new Point(x + 11, cy - 2), true, false); }
                        else { g.BeginFigure(new Point(x + 5, cy - 4), false, false); g.LineTo(new Point(x + 9, cy), true, false); g.LineTo(new Point(x + 5, cy + 4), true, false); }
                    }
                    chevron.Freeze();
                    dc.DrawGeometry(null, new Pen(muted, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, chevron);
                }
                double nameX = x + 18;
                string name = row.Name;
                Brush nameBrush = row.IsFile && row.Node == null && row.Sub == null && row.Files < 0 ? ink : ink;
                FormattedText nameText = Format(name, 12.5, nameBrush, Math.Max(20, nameEnd - nameX), row.Expandable || row.Depth == 0 ? FontWeights.SemiBold : FontWeights.Normal);
                dc.DrawText(nameText, new Point(nameX, y + (RowHeight - nameText.Height) / 2));
                if (row.Sub != null)
                {
                    double subX = nameX + Math.Min(nameText.WidthIncludingTrailingWhitespace, nameEnd - nameX) + 10;
                    if (subX < nameEnd - 30)
                    {
                        FormattedText sub = Format(row.Sub, 11, muted, nameEnd - subX, FontWeights.Normal);
                        dc.DrawText(sub, new Point(subX, y + (RowHeight - sub.Height) / 2));
                    }
                }
                if (barW > 0 && row.Fraction >= 0)
                {
                    double by = y + RowHeight / 2 - 4;
                    dc.DrawRoundedRectangle(track, null, new Rect(barX, by, barW, 8), 4, 4);
                    double fill = Math.Max(row.Fraction > 0 ? 3 : 0, barW * Math.Min(1, row.Fraction));
                    if (fill > 0) dc.DrawRoundedRectangle(fillBrush, null, new Rect(barX, by, fill, 8), 4, 4);
                    FormattedText percent = Format(row.Fraction >= 0.995 ? "100 %" : (row.Fraction * 100).ToString(row.Fraction < 0.1 ? "0.0" : "0", CultureInfo.CurrentCulture) + " %", 10.5, muted, percentW - 4, FontWeights.Normal, TextAlignment.Right);
                    dc.DrawText(percent, new Point(percentX + 4, y + (RowHeight - percent.Height) / 2));
                }
                if (sizeW > 0)
                {
                    FormattedText size = Format(SizeText(row.Size), 12, ink, sizeW, FontWeights.Medium, TextAlignment.Right);
                    dc.DrawText(size, new Point(sizeX, y + (RowHeight - size.Height) / 2));
                }
                if (filesW > 0 && row.Files >= 0)
                {
                    FormattedText files = Format(row.Files.ToString("N0", CultureInfo.CurrentCulture), 11, muted, filesW, FontWeights.Normal, TextAlignment.Right);
                    dc.DrawText(files, new Point(filesX, y + (RowHeight - files.Height) / 2));
                }
                if (dateW > 0 && row.Modified > 0)
                {
                    FormattedText date = Format(DateText(row.Modified), 11, muted, dateW, FontWeights.Normal, TextAlignment.Right);
                    dc.DrawText(date, new Point(dateX, y + (RowHeight - date.Height) / 2));
                }
            }
            dc.Pop();

            if (Total > Body)
            {
                double trackHeight = Body, thumb = Math.Max(24, trackHeight * Body / Total);
                double top = HeaderHeight + (trackHeight - thumb) * (Offset / Math.Max(1, Total - Body));
                dc.DrawRoundedRectangle(muted, null, new Rect(width - 7, top, 4, thumb), 2, 2);
            }
        }
    }

    /// <summary>Pantalla de Xpace: elegir dónde mirar, analizar y recorrer el espacio por carpetas, archivos grandes y tipos.</summary>
    internal sealed class XpaceView : StackPanel
    {
        private readonly XpaceSession _s;
        private readonly Action<string> _toast;
        private TextBlock _status;
        private XpaceList _list;
        private Action _onProgress;

        public XpaceView(XpaceSession session, Action<string> toast)
        {
            _s = session;
            _toast = toast;
            Build();
            Unloaded += (s, e) => { if (_onProgress != null) _s.Progress -= _onProgress; };
        }

        private Button Pill(string label, bool primary, double height, Action click)
        {
            Button button = Theme.Button(label, primary);
            button.MinHeight = height;
            button.FontSize = 12;
            button.Padding = new Thickness(12, 0, 12, 0);
            button.Click += (s, e) => click();
            return button;
        }

        private void Build()
        {
            // Dónde mirar
            Grid pathRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            pathRow.ColumnDefinitions.Add(new ColumnDefinition());
            pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Border pathBox = new Border
            {
                Background = Theme.SoftSurface, BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 7, 12, 8)
            };
            TextBlock path = Theme.Text(string.IsNullOrEmpty(_s.Path) ? "Elige una unidad o una carpeta para analizar" : _s.Path, 12.5,
                string.IsNullOrEmpty(_s.Path) ? Theme.Muted : Theme.Ink);
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            path.TextWrapping = TextWrapping.NoWrap;
            pathBox.Child = path;
            pathRow.Children.Add(pathBox);
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            Button pick = Pill("Carpeta…", false, 34, PickFolder);
            pick.IsEnabled = !_s.Running;
            actions.Children.Add(pick);
            if (_s.Running)
            {
                Button cancel = Pill("Cancelar", false, 34, () => _s.Cancel());
                cancel.Margin = new Thickness(8, 0, 0, 0);
                actions.Children.Add(cancel);
            }
            else
            {
                Button scan = Pill(_s.Result == null ? "Analizar" : "Volver a analizar", true, 34, () => _s.Start(_s.Path));
                scan.Margin = new Thickness(8, 0, 0, 0);
                scan.IsEnabled = !string.IsNullOrEmpty(_s.Path);
                actions.Children.Add(scan);
            }
            Grid.SetColumn(actions, 1);
            pathRow.Children.Add(actions);
            Children.Add(pathRow);

            WrapPanel drives = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable && drive.DriveType != DriveType.Network)) continue;
                    string root = drive.RootDirectory.FullName;
                    Button button = Pill(root.TrimEnd('\\') + "  ·  " + XpaceList.SizeText(drive.AvailableFreeSpace) + " libres", string.Equals(_s.Path, root, StringComparison.OrdinalIgnoreCase), 30, () => _s.Start(root));
                    button.Margin = new Thickness(0, 0, 6, 6);
                    button.IsEnabled = !_s.Running;
                    button.ToolTip = root + " · " + XpaceList.SizeText(drive.TotalSize) + " en total";
                    drives.Children.Add(button);
                }
                catch { }
            }
            Children.Add(drives);

            if (_s.Running) { BuildRunning(); return; }
            if (_s.Error != null)
            {
                TextBlock error = Theme.Text(_s.Error, 12, Theme.Danger);
                error.Margin = new Thickness(0, 6, 0, 0);
                Children.Add(error);
            }
            if (_s.Result == null)
            {
                if (_s.Error == null)
                {
                    TextBlock hint = Theme.Text("Xpace recorre las carpetas en paralelo y muestra dónde se va el espacio, al estilo de TreeSize. No lee el contenido de los archivos y funciona sin permisos de administrador.", 11.5, Theme.Muted);
                    hint.Margin = new Thickness(0, 10, 0, 0);
                    Children.Add(hint);
                }
                return;
            }
            BuildResult();
        }

        private void BuildRunning()
        {
            Border track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.SoftSurface, ClipToBounds = true, Margin = new Thickness(0, 8, 0, 8) };
            Border bar = new Border { Height = 4, Width = 120, CornerRadius = new CornerRadius(2), Background = Theme.SelectionFill, HorizontalAlignment = HorizontalAlignment.Left };
            TranslateTransform move = new TranslateTransform();
            bar.RenderTransform = move;
            track.Child = bar;
            track.SizeChanged += (s, e) =>
            {
                DoubleAnimation slide = new DoubleAnimation(-120, Math.Max(200, track.ActualWidth), TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever };
                move.BeginAnimation(TranslateTransform.XProperty, slide);
            };
            Children.Add(track);
            _status = Theme.Text("Analizando…", 12, Theme.Ink, FontWeights.Medium);
            Children.Add(_status);
            TextBlock where = Theme.Text("", 10.5, Theme.Muted);
            where.TextTrimming = TextTrimming.CharacterEllipsis;
            where.TextWrapping = TextWrapping.NoWrap;
            where.Margin = new Thickness(0, 2, 0, 0);
            Children.Add(where);
            Stopwatch clock = Stopwatch.StartNew();
            _onProgress = () =>
            {
                XpaceScanner scanner = _s.Scanner;
                if (scanner == null) return;
                double seconds = Math.Max(0.1, clock.Elapsed.TotalSeconds);
                _status.Text = "Analizando…  " + scanner.Files.ToString("N0", CultureInfo.CurrentCulture) + " archivos · " + XpaceList.SizeText(scanner.Bytes) +
                    " · " + scanner.Folders.ToString("N0", CultureInfo.CurrentCulture) + " carpetas · " + ((int)(scanner.Files / seconds)).ToString("N0", CultureInfo.CurrentCulture) + " archivos/s";
                where.Text = scanner.Current;
            };
            _s.Progress += _onProgress;
            _onProgress();
        }

        private void BuildResult()
        {
            XResult r = _s.Result;
            // Resumen
            Border summary = new Border { Background = Theme.SoftSurface, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 2, 0, 10) };
            StackPanel info = new StackPanel();
            Grid facts = new Grid();
            DriveInfo drive = null;
            try { drive = new DriveInfo(Path.GetPathRoot(r.Path)); } catch { }
            bool wholeDrive = drive != null && drive.IsReady && string.Equals(r.Path.TrimEnd('\\') + "\\", drive.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase);
            string[][] cells =
            {
                new[] { XpaceList.SizeText(r.Bytes), "Analizado" },
                new[] { r.Files.ToString("N0", CultureInfo.CurrentCulture), "archivos" },
                new[] { r.Folders.ToString("N0", CultureInfo.CurrentCulture), "carpetas" },
                wholeDrive ? new[] { XpaceList.SizeText(drive.AvailableFreeSpace), "libres en el disco" } : new[] { r.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture) + " s", "en analizar" }
            };
            for (int i = 0; i < cells.Length; i++)
            {
                facts.ColumnDefinitions.Add(new ColumnDefinition());
                StackPanel cell = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 14, 0, 6, 0) };
                cell.Children.Add(Theme.Text(cells[i][1], 10.5, Theme.Muted));
                cell.Children.Add(Theme.Text(cells[i][0], 19, Theme.Ink, FontWeights.SemiBold));
                Grid.SetColumn(cell, i);
                facts.Children.Add(cell);
                if (i > 0)
                {
                    Border divider = new Border { Width = 1, Background = Theme.Line, HorizontalAlignment = HorizontalAlignment.Left };
                    Grid.SetColumn(divider, i);
                    facts.Children.Add(divider);
                }
            }
            info.Children.Add(facts);
            if (wholeDrive)
            {
                long used = drive.TotalSize - drive.AvailableFreeSpace;
                info.Children.Add(Meter(used, drive.TotalSize, r.Bytes));
                TextBlock note = Theme.Text("Disco: " + XpaceList.SizeText(used) + " usados de " + XpaceList.SizeText(drive.TotalSize) +
                    (r.Bytes < used * 0.97 ? " · " + XpaceList.SizeText(used - r.Bytes) + " no se pudieron ver (archivos del sistema, otros usuarios o espacio reservado)." : "."), 10.5, Theme.Muted);
                note.Margin = new Thickness(0, 4, 0, 0);
                info.Children.Add(note);
            }
            if (r.Denied > 0)
            {
                TextBlock denied = Theme.Text(r.Denied.ToString("N0", CultureInfo.CurrentCulture) + " carpetas sin acceso quedaron fuera del total.", 10.5, Theme.Muted);
                denied.Margin = new Thickness(0, 3, 0, 0);
                info.Children.Add(denied);
            }
            summary.Child = info;
            Children.Add(summary);

            // Vistas
            WrapPanel tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            string[][] views = { new[] { "map", "Mapa" }, new[] { "folders", "Lista" }, new[] { "files", "Archivos grandes" }, new[] { "types", "Tipos de archivo" } };
            foreach (string[] view in views)
            {
                string id = view[0];
                Button tab = Pill(view[1], _s.Tab == id, 30, () => { _s.Offset = 0; _s.SelectedKey = null; _s.Tab = id; Rebuild(); });
                tab.Margin = new Thickness(0, 0, 6, 0);
                tabs.Children.Add(tab);
            }
            Children.Add(tabs);

            if (_s.Tab == "map")
            {
                BuildMap();
                return;
            }
            _list = new XpaceList { Height = 330, Offset = _s.Offset };
            _list.Toggle += row =>
            {
                if (row.Node == null) return;
                if (!_s.Expanded.Add(row.Node)) _s.Expanded.Remove(row.Node);
                _s.SelectedKey = row.Key;
                Fill();
            };
            _list.Selected += row => _s.SelectedKey = row.Key;
            _list.Activate += row => Reveal(row);
            _list.ContextRequested += row => _list.ContextMenu = Menu(row);
            Children.Add(_list);
            Fill();

            TextBlock hint = Theme.Text("Doble clic expande o abre en el Explorador · clic derecho para copiar la ruta · tamaño lógico, como lo muestra el Explorador.", 10.5, Theme.Muted);
            hint.Margin = new Thickness(0, 6, 0, 0);
            Children.Add(hint);
        }

        /// <summary>Mapa de árbol con migas de pan: cada recuadro es una carpeta y un clic entra en ella.</summary>
        private void BuildMap()
        {
            XNode node = _s.MapNode ?? _s.Result.Root;
            List<XNode> chain = new List<XNode>();
            for (XNode n = node; n != null; n = n.Parent) chain.Insert(0, n);
            Grid crumbs = new Grid { Margin = new Thickness(2, 0, 2, 6) };
            crumbs.ColumnDefinitions.Add(new ColumnDefinition());
            crumbs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            WrapPanel trail = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < chain.Count; i++)
            {
                XNode target = chain[i];
                bool last = i == chain.Count - 1;
                TextBlock label = Theme.Text(i == 0 ? _s.Result.Path : target.Name, 12, last ? Theme.Ink : Theme.Muted, last ? FontWeights.SemiBold : FontWeights.Normal);
                label.TextWrapping = TextWrapping.NoWrap;
                label.Cursor = last ? Cursors.Arrow : Cursors.Hand;
                if (!last) label.MouseLeftButtonDown += (s, e) => { _s.MapNode = target; Rebuild(); };
                trail.Children.Add(label);
                if (!last) trail.Children.Add(Theme.Text("  ›  ", 12, Theme.Muted));
            }
            crumbs.Children.Add(trail);
            TextBlock total = Theme.Text(XpaceList.SizeText(node.Size), 12, Theme.Muted);
            total.FontFamily = Theme.Mono;
            Grid.SetColumn(total, 1);
            crumbs.Children.Add(total);
            Children.Add(crumbs);

            XpaceMap map = new XpaceMap { Height = 340 };
            map.Drill += child => { _s.MapNode = child; Rebuild(); };
            map.Show(node);
            Border frame = new Border { CornerRadius = new CornerRadius(14), BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Padding = new Thickness(6), Child = map };
            Children.Add(frame);
            TextBlock hint = Theme.Text("Cada recuadro mide lo que ocupa. Clic en una carpeta para entrar · las migas de arriba vuelven atrás · pasa el cursor para ver el detalle.", 10.5, Theme.Muted);
            hint.Margin = new Thickness(0, 6, 0, 0);
            Children.Add(hint);
        }

        private UIElement Meter(long used, long total, long seen)
        {
            Border track = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = Theme.Color(Theme.P.Dark ? "#18FFFFFF" : "#14000000"), Margin = new Thickness(0, 8, 0, 0), ClipToBounds = true };
            Grid fills = new Grid();
            Border usedBar = new Border { CornerRadius = new CornerRadius(4), Background = Theme.Color(Theme.P.Dark ? "#40FFFFFF" : "#30000000"), HorizontalAlignment = HorizontalAlignment.Left };
            Border seenBar = new Border { CornerRadius = new CornerRadius(4), Background = Theme.SelectionFill, HorizontalAlignment = HorizontalAlignment.Left };
            fills.Children.Add(usedBar);
            fills.Children.Add(seenBar);
            track.Child = fills;
            track.SizeChanged += (s, e) =>
            {
                usedBar.Width = track.ActualWidth * Math.Min(1.0, used / (double)Math.Max(1, total));
                seenBar.Width = track.ActualWidth * Math.Min(1.0, seen / (double)Math.Max(1, total));
            };
            return track;
        }

        /// <summary>Cambió la vista (pestañas): se arma de nuevo todo el panel.</summary>
        private void Rebuild()
        {
            Children.Clear();
            Build();
        }

        /// <summary>Arma las filas de la vista elegida y se las pasa a la lista, conservando el desplazamiento.</summary>
        private void Fill()
        {
            if (_list != null) _s.Offset = _list.Offset;
            XResult r = _s.Result;
            List<XRow> rows = new List<XRow>();
            string[] headers;
            if (_s.Tab == "files")
            {
                headers = new[] { "Archivo", null, "Tamaño", null, "Modificado" };
                long top = Math.Max(1, r.Largest.Count > 0 ? r.Largest[0].Size : 1);
                foreach (XFile f in r.Largest)
                {
                    string folder = f.Folder.FullPath(r.Path);
                    rows.Add(new XRow { Name = f.Name, Sub = folder, Path = Path.Combine(folder, f.Name), Key = Path.Combine(folder, f.Name), Size = f.Size, Modified = f.Modified, IsFile = true });
                }
            }
            else if (_s.Tab == "types")
            {
                headers = new[] { "Tipo", "% del total", "Tamaño", "Archivos", null };
                foreach (XType t in r.Types.Take(500))
                    rows.Add(new XRow { Name = t.Extension, Key = "ext:" + t.Extension, Size = t.Size, Files = t.Count, Fraction = r.Bytes == 0 ? 0 : (double)t.Size / r.Bytes, IsFile = true });
            }
            else
            {
                headers = new[] { "Carpeta", "% del contenedor", "Tamaño", "Archivos", "Modificado" };
                AddNode(rows, r.Root, 0, r.Path, 1.0);
            }
            _list.SetRows(rows, headers, _s.SelectedKey, _s.Offset);
        }

        private void AddNode(List<XRow> rows, XNode node, int depth, string path, double fraction)
        {
            bool expandable = node.Children.Count > 0 || node.OwnFiles > 0;
            bool expanded = expandable && _s.Expanded.Contains(node);
            rows.Add(new XRow
            {
                Depth = depth, Name = depth == 0 ? path : node.Name, Path = path, Key = path, Node = node, Size = node.Size, Files = node.Files,
                Modified = node.Modified, Fraction = fraction, Expandable = expandable, Expanded = expanded
            });
            if (!expanded) return;
            foreach (XNode child in node.Children)
                AddNode(rows, child, depth + 1, Path.Combine(path.TrimEnd('\\') + "\\", child.Name), node.Size == 0 ? 0 : (double)child.Size / node.Size);
            if (node.OwnFiles > 0)
                rows.Add(new XRow
                {
                    Depth = depth + 1, Name = "[" + node.OwnFiles.ToString("N0", CultureInfo.CurrentCulture) + (node.OwnFiles == 1 ? " archivo]" : " archivos]"),
                    Path = path, Key = path + "|archivos", Size = node.OwnSize, Files = node.OwnFiles, Fraction = node.Size == 0 ? 0 : (double)node.OwnSize / node.Size, IsFile = true
                });
        }

        private ContextMenu Menu(XRow row)
        {
            ContextMenu menu = new ContextMenu();
            if (row.Path != null)
            {
                MenuItem show = new MenuItem { Header = row.IsFile && row.Node == null && row.Key.IndexOf('|') < 0 ? "Mostrar en el Explorador" : "Abrir en el Explorador" };
                show.Click += (s, e) => Reveal(row);
                menu.Items.Add(show);
                MenuItem copy = new MenuItem { Header = "Copiar ruta" };
                copy.Click += (s, e) => { try { Clipboard.SetText(row.Path); _toast("Ruta copiada"); } catch { } };
                menu.Items.Add(copy);
            }
            if (row.Expandable)
            {
                MenuItem toggle = new MenuItem { Header = row.Expanded ? "Contraer" : "Expandir" };
                toggle.Click += (s, e) => { if (!_s.Expanded.Add(row.Node)) _s.Expanded.Remove(row.Node); Fill(); };
                menu.Items.Add(toggle);
            }
            return menu;
        }

        private void Reveal(XRow row)
        {
            if (row.Path == null) return;
            try
            {
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                bool file = row.IsFile && row.Node == null && row.Key.IndexOf('|') < 0 && File.Exists(row.Path);
                Process.Start(explorer, file ? "/select,\"" + row.Path + "\"" : "\"" + row.Path + "\"");
            }
            catch (Exception ex) { _toast(ex.Message); }
        }

        private void PickFolder()
        {
            using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Elige la carpeta que quieres analizar",
                SelectedPath = _s.Path ?? "",
                ShowNewFolderButton = false
            })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                _s.Start(dialog.SelectedPath);
            }
        }
    }
}
