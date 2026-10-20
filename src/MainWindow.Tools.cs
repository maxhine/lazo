using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lazo
{
    /// <summary>Sección «Utilidades»: herramientas de trabajo que no son de transferencia. Por ahora, nubes de puntos a ReCap.</summary>
    internal sealed partial class MainWindow
    {
        private bool _toolsOpen;
        private Border _toolsCard;
        /// <summary>Utilidad abierta: «cloud» (nubes de puntos) o «xpace» (espacio en disco).</summary>
        private string _toolId = "cloud";
        private XpaceSession _xpace;

        // Estado del conversor: sobrevive a que se cierre y se vuelva a abrir el panel.
        private readonly List<string> _cloudFiles = new List<string>();
        private string _cloudFolder;
        private string _cloudName = "";
        private bool _cloudUnify;
        private string _cloudFormat = "rcp";
        /// <summary>Proyecto .rcp: unir las partes en un solo escaneo al final.</summary>
        private bool _cloudMerge;
        private string _cloudReport;
        private TrayProgressRing _trayRing;
        private int _cloudUnit = 2;
        private string _cloudDecimation = "";
        private CancellationTokenSource _cloudRun;
        private double _cloudProgress;
        private string _cloudStatus;
        private PointCloudResult _cloudResult;
        private string _deCap;
        private bool _deCapLooked;

        /// <summary>Un sistema elegible: uno que calcula Lazo, uno de la biblioteca de ReCap, o ambos a la vez.</summary>
        private sealed class CsChoice
        {
            public LazoProjection Lazo;
            public CoordSystem Recap;
            public string Label { get { return Lazo != null ? Lazo.Name : Recap.Label; } }
            public string RecapKey
            {
                get
                {
                    string key = Recap != null ? Recap.Key : Lazo != null ? Lazo.RecapKey : null;
                    return key != null && (!CoordinateLibrary.Available || CoordinateLibrary.Find(key) != null) ? key : null;
                }
            }
            public int Epsg { get { return Lazo != null ? Lazo.Epsg : Recap.Epsg; } }
            public bool Same(CsChoice other)
            {
                if (other == null) return false;
                if (Lazo != null && other.Lazo != null) return Lazo.Id == other.Lazo.Id;
                return RecapKey != null && RecapKey == other.RecapKey && (Lazo == null || !Lazo.Egm2008) && (other.Lazo == null || !other.Lazo.Egm2008);
            }
        }

        // Sistema de coordenadas: el que trae la nube (o el que se elija) y el de salida (null: el mismo).
        private CloudCrs _cloudDetected;
        private string _cloudDetectNote;
        private CsChoice _cloudSource;
        private CsChoice _cloudTarget;
        private bool _cloudEllipsoidal;
        private string _cloudPicker;
        private readonly string _geoid = GeoidGrid.Find();

        /// <summary>Utilidades ocupa el área principal, como el chat: el inicio (equipos) se oculta mientras está abierta.</summary>
        private Border ToolsCard()
        {
            return Theme.Glass(new Border
            {
                Background = Theme.OverlaySurface,
                CornerRadius = new CornerRadius(24),
                Padding = new Thickness(20, 16, 8, 12),
                Margin = Theme.IsMinimal ? new Thickness(8, 40, 8, 8) : new Thickness(SidebarWidth + 10, HeaderHeight - 6, 12, 12),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Child = ToolsContent()
            });
        }

        /// <summary>Tamaño de la ventana con Utilidades abierta: amplia, para el asistente a dos columnas.</summary>
        private void FitTools(ref double width, ref double height)
        {
            double maxWidth, maxHeight;
            ScreenLimits(out maxWidth, out maxHeight);
            width = Math.Min(maxWidth, Theme.IsMinimal ? 700 : 940);
            height = Math.Min(maxHeight, Theme.IsMinimal ? 640 : 660);
        }

        /// <summary>Con espacio, el asistente va a dos columnas; en Minimal, a una.</summary>
        private bool ToolsWide { get { return !Theme.IsMinimal; } }

        private void RefreshToolsCard()
        {
            if (_toolsCard == null) return;
            ScrollViewer before = _toolsCard.Child as ScrollViewer;
            double offset = before == null ? 0 : before.VerticalOffset;
            _toolsCard.Child = ToolsContent();
            ScrollViewer after = _toolsCard.Child as ScrollViewer;
            if (after != null && offset > 0) after.ScrollToVerticalOffset(offset);
        }

        private UIElement ToolsContent()
        {
            StackPanel panel = new StackPanel();
            Grid head = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Theme.Text("Utilidades", 21, Theme.Ink, FontWeights.SemiBold));
            words.Children.Add(Theme.Text("Herramientas de trabajo para el equipo", 12, Theme.Muted));
            head.Children.Add(words);
            Button close = IconButton("close", () => TogglePanel("tools"));
            close.ToolTip = "Volver al inicio";
            close.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(close, 1);
            head.Children.Add(close);
            panel.Children.Add(head);

            // Galería: cada utilidad nueva se suma aquí como una tarjeta más.
            WrapPanel gallery = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            gallery.Children.Add(ToolTile(CloudGlyph(), "Nube de puntos", "LAS, LAZ o E57 a ReCap, LAS, XYZ, PTS o CSV, con vista previa de la reducción", _toolId == "cloud", () => SelectTool("cloud")));
            gallery.Children.Add(ToolTile(XpaceGlyph(), "Xpace", "Dónde se va el espacio en disco: carpetas, archivos grandes y tipos", _toolId == "xpace", () => SelectTool("xpace")));
            panel.Children.Add(gallery);
            panel.Children.Add(new Border { Height = 1, Background = Theme.Line, Margin = new Thickness(0, 0, 12, 12) });
            panel.Children.Add(_toolId == "xpace" ? XpaceTool() : PointCloudTool());
            return Scroll(panel);
        }

        // ── Nubes de puntos a ReCap ───────────────────────────────────────

        private void SelectTool(string id)
        {
            if (_toolId == id) return;
            _toolId = id;
            RefreshToolsCard();
        }

        private UIElement XpaceTool()
        {
            if (_xpace == null)
            {
                _xpace = new XpaceSession(Dispatcher);
                _xpace.Changed += () => { if (_toolsOpen && _toolId == "xpace") RefreshToolsCard(); };
            }
            return new XpaceView(_xpace, text => ShowToast(text));
        }

        private UIElement XpaceGlyph()
        {
            Canvas map = new Canvas { Width = 20, Height = 20 };
            double[][] boxes = { new[] { 2.0, 2, 9, 16 }, new[] { 12.0, 2, 6, 7 }, new[] { 12.0, 11, 6, 7 } };
            foreach (double[] b in boxes)
            {
                System.Windows.Shapes.Rectangle box = new System.Windows.Shapes.Rectangle { Width = b[2], Height = b[3], Stroke = Theme.Ink, StrokeThickness = 1.6, RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(box, b[0]);
                Canvas.SetTop(box, b[1]);
                map.Children.Add(box);
            }
            return map;
        }

        private UIElement PointCloudTool()
        {
            StackPanel tool = new StackPanel();
            StackPanel left = new StackPanel();
            StackPanel right = new StackPanel();
            if (!_deCapLooked && !_preview) { _deCap = PointCloudConverter.FindDeCap(); _deCapLooked = true; }
            if (_deCap == null && !_preview)
            {
                TextBlock missing = Theme.Text("Necesita Autodesk ReCap instalado en este equipo: el formato de ReCap solo lo escriben las herramientas de Autodesk, y Lazo usa su conversor con tu licencia.", 11.5, Theme.Muted);
                missing.TextWrapping = TextWrapping.Wrap;
                missing.Margin = new Thickness(0, 12, 0, 0);
                tool.Children.Add(missing);
                return tool;
            }

            bool running = _cloudRun != null;
            left.Children.Add(CloudDrop(running));
            if (_cloudFiles.Count > 0)
            {
                StackPanel list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
                foreach (string file in _cloudFiles) list.Children.Add(CloudFileRow(file, running));
                left.Children.Add(list);
            }

            TextBlock outputLabel = Theme.Label("Resultado");
            outputLabel.Margin = new Thickness(0, 12, 0, 6);
            left.Children.Add(outputLabel);
            WrapPanel formats = new WrapPanel();
            string[][] choices = { new[] { "rcp", "Proyecto .rcp" }, new[] { "rcs", "Nube única .rcs" }, new[] { "las", "LAS" }, new[] { "xyz", "XYZ" }, new[] { "pts", "PTS" }, new[] { "csv", "CSV" } };
            foreach (string[] choice in choices)
            {
                string format = choice[0];
                formats.Children.Add(CloudChip(choice[1], _cloudFormat == format, !running, () => { _cloudFormat = format; _cloudUnify = format == "rcs"; RefreshToolsCard(); }));
            }
            left.Children.Add(formats);
            TextBlock modeHelp = Theme.Text(CloudExport.IsLazoFormat(_cloudFormat)
                ? CloudExport.Describe(_cloudFormat) + " Lo escribe Lazo (ReCap solo genera .rcp y .rcs); sirve para LAS, LAZ y E57."
                : _cloudUnify
                ? "Todo en un solo .rcs, para Revit, AutoCAD o Navisworks. Con reducción de puntos se convierte en partes a la vez y se une al final."
                : "Un proyecto .rcp para abrir y organizar en ReCap. Las nubes grandes se convierten en partes a la vez (un escaneo por parte).", 10.5, Theme.Muted);
            modeHelp.TextWrapping = TextWrapping.Wrap;
            modeHelp.Margin = new Thickness(0, 4, 0, 0);
            left.Children.Add(modeHelp);
            if (_cloudFormat == "rcp")
            {
                CheckBox merge = new CheckBox
                {
                    Content = "Unir las partes al final en un solo escaneo",
                    IsChecked = _cloudMerge,
                    IsEnabled = !running,
                    Foreground = Theme.Ink, FontFamily = Theme.Font, FontSize = 11.5,
                    Margin = new Thickness(0, 8, 0, 0),
                    ToolTip = "Más rápido cuando reduces puntos: las partes ya llegan livianas y unirlas toma poco."
                };
                merge.Checked += (s, e) => _cloudMerge = true;
                merge.Unchecked += (s, e) => _cloudMerge = false;
                left.Children.Add(merge);
            }

            TextBlock nameLabel = Theme.Label("Nombre");
            nameLabel.Margin = new Thickness(0, 12, 0, 4);
            left.Children.Add(nameLabel);
            TextBox name = CloudBox(_cloudName, running);
            name.TextChanged += (s, e) => _cloudName = name.Text;
            left.Children.Add(name);

            TextBlock folderLabel = Theme.Label("Guardar en");
            folderLabel.Margin = new Thickness(0, 12, 0, 4);
            left.Children.Add(folderLabel);
            Grid folderRow = new Grid();
            folderRow.ColumnDefinitions.Add(new ColumnDefinition());
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock folder = Theme.Text(string.IsNullOrEmpty(_cloudFolder) ? "La carpeta del primer archivo" : _cloudFolder, 11.5,
                string.IsNullOrEmpty(_cloudFolder) ? Theme.Muted : Theme.Ink);
            folder.TextTrimming = TextTrimming.CharacterEllipsis;
            folder.TextWrapping = TextWrapping.NoWrap;
            folder.VerticalAlignment = VerticalAlignment.Center;
            folder.ToolTip = _cloudFolder;
            folderRow.Children.Add(folder);
            Button pick = Theme.Button("Cambiar…", false);
            pick.MinHeight = 30;
            pick.Margin = new Thickness(8, 0, 0, 0);
            pick.IsEnabled = !running;
            pick.Click += (s, e) => PickCloudFolder();
            Grid.SetColumn(pick, 1);
            folderRow.Children.Add(pick);
            left.Children.Add(folderRow);

            TextBlock unitsLabel = Theme.Label("Unidades del archivo");
            unitsLabel.Margin = new Thickness(0, 12, 0, 4);
            right.Children.Add(unitsLabel);
            WrapPanel units = new WrapPanel();
            for (int i = 0; i < PointCloudConverter.UnitNames.Length; i++)
            {
                int unit = i;
                units.Children.Add(CloudChip(PointCloudConverter.UnitNames[i], _cloudUnit == i, !running, () => { _cloudUnit = unit; RefreshToolsCard(); SchedulePreview(false); }));
            }
            right.Children.Add(units);
            TextBlock unitsHelp = Theme.Text("Solo cuenta para LAS y LAZ; E57 ya guarda sus unidades.", 10.5, Theme.Muted);
            unitsHelp.Margin = new Thickness(0, 2, 0, 0);
            right.Children.Add(unitsHelp);

            TextBlock decimationLabel = Theme.Label("Reducir puntos (opcional)");
            decimationLabel.Margin = new Thickness(0, 12, 0, 4);
            right.Children.Add(decimationLabel);
            Grid decimationRow = new Grid();
            decimationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            decimationRow.ColumnDefinitions.Add(new ColumnDefinition());
            TextBox decimation = CloudBox(_cloudDecimation, running);
            decimation.TextChanged += (s, e) => { _cloudDecimation = decimation.Text; SchedulePreview(false); };
            decimationRow.Children.Add(decimation);
            TextBlock decimationHelp = Theme.Text("mm entre puntos. Vacío: todos los puntos.", 10.5, Theme.Muted);
            decimationHelp.VerticalAlignment = VerticalAlignment.Center;
            decimationHelp.Margin = new Thickness(8, 0, 0, 0);
            decimationHelp.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(decimationHelp, 1);
            decimationRow.Children.Add(decimationHelp);
            right.Children.Add(decimationRow);
            right.Children.Add(CloudPreviewPanel());

            right.Children.Add(CloudProjection(running));

            // Dos columnas con espacio (archivos y salida | unidades y coordenadas); una sola en Minimal.
            Grid columns = new Grid();
            if (ToolsWide)
            {
                columns.ColumnDefinitions.Add(new ColumnDefinition());
                columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                columns.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(right, 2);
            }
            else
            {
                columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(right, 1);
            }
            columns.Children.Add(left);
            columns.Children.Add(right);
            tool.Children.Add(columns);
            tool.Children.Add(CloudFooter(running));
            return tool;
        }

        /// <summary>Una utilidad en la galería: ícono, nombre y para qué sirve.</summary>
        private Button ToolTile(UIElement glyph, string title, string detail, bool selected, Action click)
        {
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Background = Theme.SoftSurface, Child = glyph });
            StackPanel words = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Theme.Text(title, 13.5, Theme.Ink, FontWeights.SemiBold));
            TextBlock small = Theme.Text(detail, 11, Theme.Muted);
            small.TextWrapping = TextWrapping.Wrap;
            words.Children.Add(small);
            Grid.SetColumn(words, 1);
            row.Children.Add(words);
            Border face = new Border
            {
                Width = 300,
                Padding = new Thickness(12, 10, 14, 10),
                CornerRadius = new CornerRadius(16),
                BorderThickness = new Thickness(1.5),
                BorderBrush = selected ? Theme.SelectionFill : Theme.Line,
                Background = selected ? Theme.SoftSurface : Brushes.Transparent,
                Child = row
            };
            Button button = Plain(face, click);
            button.Margin = new Thickness(0, 0, 10, 10);
            return button;
        }

        // ── Sistema de coordenadas ─────────────────────────────────────────

        private UIElement CloudProjection(bool running)
        {
            StackPanel section = new StackPanel();
            TextBlock title = Theme.Label("Sistema de coordenadas");
            title.Margin = new Thickness(0, 14, 0, 4);
            section.Children.Add(title);

            // Origen: lo que declara el archivo, o lo que se elija.
            Grid source = new Grid();
            source.ColumnDefinitions.Add(new ColumnDefinition());
            source.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Theme.Text("Origen", 10.5, Theme.Muted));
            TextBlock name = Theme.Text(SourceText(), 12, _cloudSource == null ? Theme.Muted : Theme.Ink, FontWeights.SemiBold);
            name.TextWrapping = TextWrapping.Wrap;
            words.Children.Add(name);
            if (_cloudDetectNote != null)
            {
                TextBlock note = Theme.Text(_cloudDetectNote, 10.5, Theme.Muted);
                note.TextWrapping = TextWrapping.Wrap;
                words.Children.Add(note);
            }
            source.Children.Add(words);
            Button change = Theme.Button(_cloudPicker == "source" ? "Cerrar" : "Cambiar", false);
            change.MinHeight = 30;
            change.Margin = new Thickness(8, 0, 0, 0);
            change.VerticalAlignment = VerticalAlignment.Top;
            change.IsEnabled = !running;
            change.Click += (s, e) => { _cloudPicker = _cloudPicker == "source" ? null : "source"; RefreshToolsCard(); };
            Grid.SetColumn(change, 1);
            source.Children.Add(change);
            section.Children.Add(source);
            if (_cloudPicker == "source") section.Children.Add(CsPicker(true, choice => { _cloudSource = choice; _cloudPicker = null; RefreshToolsCard(); }));

            // Destino: el mismo, los sistemas del equipo (los .prj de iTwin Capture) u otro.
            TextBlock target = Theme.Text("Salida", 10.5, Theme.Muted);
            target.Margin = new Thickness(0, 10, 0, 4);
            section.Children.Add(target);
            StackPanel options = new StackPanel();
            options.Children.Add(CsOption("Igual que el origen", "No se reproyecta", _cloudTarget == null, !running, () => { _cloudTarget = null; RefreshToolsCard(); }));
            foreach (LazoProjection featured in LazoProjection.All.Where(p => p.Featured))
            {
                LazoProjection value = featured;
                CsChoice choice = new CsChoice { Lazo = value };
                string how = value.RecapKey != null ? "EPSG " + value.Epsg : "Lo calcula Lazo (ReCap no lo trae) · solo LAS/LAZ";
                options.Children.Add(CsOption(value.Name, how, _cloudTarget != null && _cloudTarget.Same(choice), !running,
                    () => { _cloudTarget = choice; RefreshToolsCard(); }));
            }
            bool other = _cloudTarget != null && !(_cloudTarget.Lazo != null && _cloudTarget.Lazo.Featured);
            options.Children.Add(CsOption(other ? _cloudTarget.Label : "Otro sistema…", other ? "Elegido de la lista" : "Buscar por nombre o EPSG",
                other, !running, () => { _cloudPicker = _cloudPicker == "target" ? null : "target"; RefreshToolsCard(); }));
            section.Children.Add(options);
            if (_cloudPicker == "target") section.Children.Add(CsPicker(false, choice => { _cloudTarget = choice; _cloudPicker = null; RefreshToolsCard(); }));

            if (_cloudTarget != null && _cloudTarget.Lazo != null && _cloudTarget.Lazo.Egm2008)
            {
                CheckBox ellipsoidal = new CheckBox
                {
                    Content = "Las alturas de la nube son elipsoidales: pasarlas a EGM2008",
                    IsChecked = _cloudEllipsoidal && _geoid != null,
                    IsEnabled = !running && _geoid != null,
                    Foreground = Theme.Ink, FontFamily = Theme.Font, FontSize = 11.5,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                ellipsoidal.Checked += (s, e) => _cloudEllipsoidal = true;
                ellipsoidal.Unchecked += (s, e) => _cloudEllipsoidal = false;
                section.Children.Add(ellipsoidal);
                TextBlock heights = Theme.Text(_geoid != null
                    ? "Si ya son ortométricas (EGM2008), déjalo sin marcar: las alturas no cambian."
                    : "Para pasar alturas elipsoidales a EGM2008 hace falta egm08_25.gtx (por ejemplo en Documentos\\Bentley\\iTwin Capture Modeler).", 10.5, Theme.Muted);
                heights.TextWrapping = TextWrapping.Wrap;
                heights.Margin = new Thickness(20, 0, 0, 0);
                section.Children.Add(heights);
            }
            string problem = ProjectionProblem();
            if (problem != null)
            {
                TextBlock warning = Theme.Text(problem, 11, Theme.Danger);
                warning.TextWrapping = TextWrapping.Wrap;
                warning.Margin = new Thickness(0, 8, 0, 0);
                section.Children.Add(warning);
            }
            return section;
        }

        private string SourceText()
        {
            if (_cloudFiles.Count == 0) return "Se lee de los archivos al agregarlos";
            if (_cloudSource == null)
                return _cloudDetected != null && _cloudDetected.Epsg > 0
                    ? "EPSG " + _cloudDetected.Epsg + (_cloudDetected.Name.Length > 0 ? " · " + _cloudDetected.Name : "") + " (ReCap no lo conoce)"
                    : "El archivo no indica su sistema";
            bool detected = _cloudDetected != null && (_cloudDetected.Epsg == _cloudSource.Epsg ||
                (_cloudSource.Lazo != null && LazoProjection.Detect(_cloudDetected.Epsg, _cloudDetected.Name) == _cloudSource.Lazo));
            return _cloudSource.Label + (_cloudSource.Epsg > 0 ? " · EPSG " + _cloudSource.Epsg : "") + (detected ? " (del archivo)" : " (elegido)");
        }

        private Button CsOption(string title, string detail, bool selected, bool enabled, Action click)
        {
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            Border dot = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 2, 10, 0),
                VerticalAlignment = VerticalAlignment.Top,
                BorderThickness = new Thickness(selected ? 5 : 1.5),
                BorderBrush = selected ? Theme.SelectionFill : Theme.Muted
            };
            row.Children.Add(dot);
            StackPanel words = new StackPanel();
            TextBlock name = Theme.Text(title, 12, Theme.Ink, selected ? FontWeights.SemiBold : FontWeights.Normal);
            name.TextWrapping = TextWrapping.Wrap;
            words.Children.Add(name);
            words.Children.Add(Theme.Text(detail, 10.5, Theme.Muted));
            Grid.SetColumn(words, 1);
            row.Children.Add(words);
            Border face = new Border { Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Child = row };
            Button button = Plain(face, click);
            button.IsEnabled = enabled;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.MouseEnter += (s, e) => face.Background = Theme.SoftSurface;
            button.MouseLeave += (s, e) => face.Background = Brushes.Transparent;
            return button;
        }

        /// <summary>Buscador de sistemas: los que calcula Lazo y los de la biblioteca de ReCap, por nombre o EPSG.</summary>
        private UIElement CsPicker(bool forSource, Action<CsChoice> pick)
        {
            StackPanel picker = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
            TextBox query = CloudBox("", false);
            query.ToolTip = "Nombre o EPSG, por ejemplo «Bogota», «UTM 18N» o 3116";
            picker.Children.Add(query);
            StackPanel results = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            picker.Children.Add(results);
            Action fill = () =>
            {
                results.Children.Clear();
                string text = query.Text.Trim();
                List<CsChoice> found = new List<CsChoice>();
                foreach (LazoProjection p in LazoProjection.All)
                {
                    if (!forSource && p.Geographic) continue;
                    if (text.Length == 0 || p.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || p.Epsg.ToString() == text)
                        found.Add(new CsChoice { Lazo = p });
                }
                if (text.Length > 0 && CoordinateLibrary.Available)
                    foreach (CoordSystem cs in CoordinateLibrary.Search(text, 12))
                        if (!found.Any(f => f.Lazo != null && f.Lazo.RecapKey == cs.Key)) found.Add(new CsChoice { Recap = cs });
                if (found.Count == 0)
                {
                    results.Children.Add(Theme.Text(CoordinateLibrary.Available ? "Sin resultados." : "No se encontró la biblioteca de sistemas de Autodesk.", 11, Theme.Muted));
                    return;
                }
                foreach (CsChoice choice in found.Take(14))
                {
                    CsChoice value = choice;
                    string detail = (value.Epsg > 0 ? "EPSG " + value.Epsg + " · " : "") + (value.Lazo != null
                        ? (value.Lazo.RecapKey != null ? "Colombia" : "Lo calcula Lazo")
                        : value.Recap.Key);
                    results.Children.Add(CsOption(value.Label, detail, false, true, () => pick(value)));
                }
            };
            query.TextChanged += (s, e) => fill();
            fill();
            query.Loaded += (s, e) => query.Focus();
            return picker;
        }

        /// <summary>Lo que impide la combinación elegida, o null si se puede.</summary>
        private string ProjectionProblem()
        {
            if (_cloudTarget == null) return null;
            if (_cloudSource == null) return "Para reproyectar, indica en qué sistema está la nube (Cambiar).";
            if (_cloudSource.Same(_cloudTarget)) return null;
            bool lazo = NeedsLazo();
            if (lazo && _cloudSource.Lazo == null)
                return "Lazo no sabe calcular desde " + _cloudSource.Label + ". Elige como salida un sistema de ReCap (Otro sistema…).";
            if (!lazo && (_cloudSource.RecapKey == null || _cloudTarget.RecapKey == null))
                return "ReCap no conoce " + (_cloudSource.RecapKey == null ? _cloudSource.Label : _cloudTarget.Label) + " para esta conversión.";
            if (lazo && _cloudFiles.Any(f => string.Equals(Path.GetExtension(f), ".e57", StringComparison.OrdinalIgnoreCase)))
                return "Lazo reproyecta LAS y LAZ; los E57 solo se pueden llevar a sistemas que ReCap conoce.";
            return null;
        }

        /// <summary>La reproyección la hace Lazo (no ReCap): destinos que ReCap no trae, alturas a EGM2008, u origen que ReCap no conoce.</summary>
        private bool NeedsLazo()
        {
            if (_cloudTarget == null || _cloudTarget.Lazo == null) return false;
            return _cloudTarget.Lazo.RecapKey == null || (_cloudTarget.Lazo.Egm2008 && _cloudEllipsoidal) || _cloudSource == null || _cloudSource.RecapKey == null;
        }

        /// <summary>Lee el sistema que declaran los archivos y lo propone como origen.</summary>
        private void DetectCloudCrs()
        {
            List<string> files = _cloudFiles.ToList();
            // Se lee fuera de la interfaz y el resultado vuelve siempre a su hilo.
            System.Threading.Tasks.Task.Run(() => files.Select(CloudCrsReader.Read).ToList())
                .ContinueWith(t => Dispatcher.BeginInvoke((Action)(() => ApplyDetectedCrs(files, t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : new List<CloudCrs>()))));
        }

        private void ApplyDetectedCrs(List<string> files, List<CloudCrs> found)
        {
            if (!files.SequenceEqual(_cloudFiles)) return;
            CloudCrs first = found.FirstOrDefault(c => c != null);
            _cloudDetected = first;
            int kinds = found.Where(c => c != null).Select(c => c.Epsg + "|" + c.Name).Distinct().Count();
            _cloudDetectNote = kinds > 1 ? "Los archivos declaran sistemas distintos; se usa el del primero." :
                found.Any(c => c == null) && first != null ? "Algunos archivos no indican sistema; se asume el mismo." : null;
            LazoProjection lazo = first == null ? null : LazoProjection.Detect(first.Epsg, first.Name);
            _cloudSource = first == null || (lazo == null && first.Match == null) ? null : new CsChoice { Lazo = lazo, Recap = lazo == null ? first.Match : null };
            RefreshToolsCard();
        }

        private UIElement CloudGlyph()
        {
            Canvas dots = new Canvas { Width = 20, Height = 20 };
            double[][] points = { new[] { 3.0, 13 }, new[] { 6.0, 8 }, new[] { 9.0, 12 }, new[] { 11.0, 5 }, new[] { 13.5, 10 }, new[] { 16.0, 14 }, new[] { 7.0, 16 }, new[] { 15.0, 6 } };
            foreach (double[] p in points)
            {
                System.Windows.Shapes.Ellipse dot = new System.Windows.Shapes.Ellipse { Width = 3, Height = 3, Fill = Theme.Ink };
                Canvas.SetLeft(dot, p[0]);
                Canvas.SetTop(dot, p[1]);
                dots.Children.Add(dot);
            }
            return dots;
        }

        private UIElement CloudDrop(bool running)
        {
            Border drop = new Border
            {
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(12, 14, 12, 14),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1.5),
                BorderBrush = Theme.Line,
                Background = Theme.SoftSurface,
                AllowDrop = !running,
                Cursor = running ? null : Cursors.Hand
            };
            StackPanel inside = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            TextBlock big = Theme.Text("Arrastra aquí archivos .las, .laz o .e57", 12.5, Theme.Ink, FontWeights.SemiBold);
            big.HorizontalAlignment = HorizontalAlignment.Center;
            inside.Children.Add(big);
            TextBlock small = Theme.Text("o haz clic para elegirlos", 11, Theme.Muted);
            small.HorizontalAlignment = HorizontalAlignment.Center;
            inside.Children.Add(small);
            drop.Child = inside;
            if (running) { drop.Opacity = 0.5; return drop; }
            drop.MouseLeftButtonUp += (s, e) => PickCloudFiles();
            drop.DragOver += (s, e) =>
            {
                bool any = e.Data.GetDataPresent(DataFormats.FileDrop) &&
                           ((string[])e.Data.GetData(DataFormats.FileDrop)).Any(PointCloudConverter.IsSupported);
                e.Effects = any ? DragDropEffects.Copy : DragDropEffects.None;
                drop.BorderBrush = any ? Theme.SelectionFill : Theme.Line;
                e.Handled = true;
            };
            drop.DragLeave += (s, e) => drop.BorderBrush = Theme.Line;
            drop.Drop += (s, e) =>
            {
                e.Handled = true;
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                AddCloudFiles((string[])e.Data.GetData(DataFormats.FileDrop));
            };
            return drop;
        }

        private UIElement CloudFileRow(string file, bool running)
        {
            Grid row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock name = Theme.Text(Path.GetFileName(file), 12, Theme.Ink, FontWeights.Medium);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextWrapping = TextWrapping.NoWrap;
            name.ToolTip = file;
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            long size = 0;
            try { size = new FileInfo(file).Length; } catch { }
            TextBlock weight = Theme.Text(SizeText(size), 11, Theme.Muted);
            weight.VerticalAlignment = VerticalAlignment.Center;
            weight.Margin = new Thickness(8, 0, 4, 0);
            Grid.SetColumn(weight, 1);
            row.Children.Add(weight);
            Button remove = IconButton("close", () =>
            {
                _cloudFiles.Remove(file);
                _cloudResult = null;
                if (_cloudFiles.Count == 0) { _cloudDetected = null; _cloudDetectNote = null; _cloudSource = null; }
                RefreshToolsCard();
                if (_cloudFiles.Count > 0) DetectCloudCrs();
                SchedulePreview(true);
            });
            remove.ToolTip = "Quitar";
            remove.IsEnabled = !running;
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            return row;
        }

        private static string SizeText(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.CurrentCulture) + " MB";
            return Math.Max(1, bytes / 1024) + " KB";
        }

        private TextBox CloudBox(string text, bool running)
        {
            return new TextBox
            {
                Text = text,
                IsEnabled = !running,
                FontFamily = Theme.Font,
                FontSize = 12.5,
                Foreground = Theme.Ink,
                CaretBrush = Theme.Ink,
                Background = Theme.SoftSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6)
            };
        }

        private Button CloudChip(string text, bool selected, bool enabled, Action click)
        {
            Border pill = new Border
            {
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(11, 5, 11, 6),
                Background = selected ? Theme.SegmentActive : Theme.SoftSurface,
                Child = Theme.Text(text, 11.5, selected ? Theme.SegmentActiveText : Theme.Ink, FontWeights.SemiBold)
            };
            Button button = Plain(pill, click);
            button.Margin = new Thickness(0, 0, 6, 6);
            button.IsEnabled = enabled;
            return button;
        }

        private UIElement CloudFooter(bool running)
        {
            StackPanel footer = new StackPanel { Margin = new Thickness(0, 14, 0, 2) };
            if (running || _cloudStatus != null)
            {
                Border track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.SoftSurface, ClipToBounds = true };
                Border fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.SelectionFill, HorizontalAlignment = HorizontalAlignment.Left };
                track.Child = fill;
                track.SizeChanged += (s, e) => fill.Width = Math.Max(0, track.ActualWidth * _cloudProgress);
                if (running || (_cloudResult != null && _cloudResult.Ok)) footer.Children.Add(track);
                TextBlock status = Theme.Text(_cloudStatus ?? "", 11.5,
                    _cloudResult != null && !_cloudResult.Ok ? Theme.Danger : Theme.Muted);
                status.TextWrapping = TextWrapping.Wrap;
                status.Margin = new Thickness(0, 6, 0, 0);
                footer.Children.Add(status);
            }
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            if (running)
            {
                Button cancel = Theme.Button("Cancelar", false);
                cancel.Click += (s, e) => { if (_cloudRun != null) _cloudRun.Cancel(); };
                buttons.Children.Add(cancel);
            }
            else
            {
                if (_cloudResult != null && _cloudResult.Ok && File.Exists(_cloudResult.Output))
                {
                    string output = _cloudResult.Output;
                    Button show = Theme.Button("Mostrar en carpeta", false);
                    show.Click += (s, e) => RevealCloud(output);
                    buttons.Children.Add(show);
                    if (!CloudExport.IsLazoFormat(_cloudFormat))
                    {
                        Button open = Theme.Button("Abrir en ReCap", false);
                        open.Margin = new Thickness(8, 0, 0, 0);
                        open.Click += (s, e) => OpenCloud(output);
                        buttons.Children.Add(open);
                    }
                }
                if (_cloudReport != null && File.Exists(_cloudReport))
                {
                    string report = _cloudReport;
                    Button view = Theme.Button("Ver reporte", false);
                    view.Margin = new Thickness(8, 0, 0, 0);
                    view.ToolTip = "Cuánto tardó cada paso y el total";
                    view.Click += (s, e) => OpenCloud(report);
                    buttons.Children.Add(view);
                }
                Button convert = Theme.Button("Convertir", true);
                convert.Margin = new Thickness(8, 0, 0, 0);
                convert.IsEnabled = _cloudFiles.Count > 0 && !_preview;
                convert.Click += (s, e) => StartCloudConversion();
                buttons.Children.Add(convert);
            }
            footer.Children.Add(buttons);
            return footer;
        }

        private void PickCloudFiles()
        {
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Elige nubes de puntos",
                Filter = "Nubes de puntos (*.las;*.laz;*.e57)|*.las;*.laz;*.e57|Todos los archivos (*.*)|*.*",
                Multiselect = true
            };
            if (dialog.ShowDialog(this) == true) AddCloudFiles(dialog.FileNames);
        }

        private void AddCloudFiles(IEnumerable<string> files)
        {
            int before = _cloudFiles.Count;
            foreach (string file in files)
                if (PointCloudConverter.IsSupported(file) && !_cloudFiles.Contains(file, StringComparer.OrdinalIgnoreCase)) _cloudFiles.Add(file);
            if (_cloudFiles.Count == before) { ShowToast("Solo se pueden convertir archivos .las, .laz o .e57"); return; }
            if (string.IsNullOrWhiteSpace(_cloudName)) _cloudName = Path.GetFileNameWithoutExtension(_cloudFiles[0]);
            _cloudResult = null;
            _cloudStatus = null;
            RefreshToolsCard();
            DetectCloudCrs();
            SchedulePreview(true);
        }

        private void PickCloudFolder()
        {
            using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Elige dónde guardar el proyecto de ReCap",
                SelectedPath = _cloudFolder ?? (_cloudFiles.Count > 0 ? Path.GetDirectoryName(_cloudFiles[0]) : ""),
                ShowNewFolderButton = true
            })
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                _cloudFolder = dialog.SelectedPath;
                RefreshToolsCard();
            }
        }

        private async void StartCloudConversion()
        {
            if (_cloudRun != null || _deCap == null) return;
            double decimation = 0;
            string decimationText = (_cloudDecimation ?? "").Trim().Replace(',', '.');
            if (decimationText.Length > 0 && (!double.TryParse(decimationText, NumberStyles.Float, CultureInfo.InvariantCulture, out decimation) || decimation <= 0))
            {
                ShowToast("La reducción de puntos va en milímetros, por ejemplo 5");
                return;
            }
            PointCloudJob job = new PointCloudJob
            {
                Inputs = _cloudFiles.ToList(),
                OutputFolder = string.IsNullOrEmpty(_cloudFolder) ? Path.GetDirectoryName(_cloudFiles[0]) : _cloudFolder,
                Name = _cloudName,
                Unify = _cloudFormat == "rcs",
                Format = _cloudFormat,
                Unit = _cloudUnit,
                DecimationMm = decimation
            };
            string projection = ProjectionProblem();
            if (projection != null) { _cloudResult = new PointCloudResult { Message = projection }; _cloudStatus = projection; RefreshToolsCard(); return; }
            if (_cloudTarget != null && !_cloudTarget.Same(_cloudSource) && NeedsLazo())
            {
                job.LazoFrom = _cloudSource.Lazo;
                job.LazoTo = _cloudTarget.Lazo;
                job.Ellipsoidal = _cloudEllipsoidal && _cloudTarget.Lazo.Egm2008;
                job.GeoidPath = _geoid;
            }
            else
            {
                // ReCap: el sistema de la nube queda registrado y, si se eligió otro, ReCap reproyecta.
                job.SourceCs = _cloudSource != null ? _cloudSource.RecapKey : null;
                job.TargetCs = _cloudTarget != null && !_cloudTarget.Same(_cloudSource) ? _cloudTarget.RecapKey : null;
            }
            string problem = PointCloudConverter.Validate(job);
            if (problem != null) { _cloudResult = new PointCloudResult { Message = problem }; _cloudStatus = problem; RefreshToolsCard(); return; }

            job.MergeParts = _cloudFormat == "rcp" && _cloudMerge;
            CancellationTokenSource run = new CancellationTokenSource();
            _cloudRun = run;
            _cloudResult = null;
            _cloudReport = null;
            _cloudProgress = 0;
            _cloudStatus = "Comprobando la licencia de ReCap…";
            RefreshToolsCard();
            TrayProgress(0, "Lazo · convirtiendo nube");
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            PointCloudResult result;
            try
            {
                string license = await PointCloudConverter.CheckLicenseAsync(_deCap);
                result = license != null ? new PointCloudResult { Message = license } :
                    await PointCloudConverter.ConvertAsync(_deCap, job, (value, text) => Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (_cloudRun != run) return;
                        _cloudProgress = value;
                        _cloudStatus = text + "  " + Math.Round(value * 100) + " %  ·  " + ConversionReport.Duration(clock.Elapsed);
                        RefreshToolsCard();
                        TrayProgress(value, "Lazo · nube " + Math.Round(value * 100) + " %");
                    })), run.Token);
            }
            catch (Exception ex) { result = new PointCloudResult { Message = ex.Message }; }
            TrayProgress(-1, null);
            _cloudRun = null;
            _cloudResult = result;
            _cloudReport = result.Report;
            _cloudProgress = result.Ok ? 1 : 0;
            string took = ConversionReport.Duration(clock.Elapsed);
            _cloudStatus = result.Ok ? "Listo en " + took + ": " + result.Output : result.Message;
            RefreshToolsCard();
            ShowToast(result.Ok ? "Nube convertida en " + took + ": " + Path.GetFileName(result.Output) : "No se pudo convertir: " + result.Message);
        }

        /// <summary>Progreso de la conversión como anillo alrededor del ícono de la bandeja (negativo: quitarlo).</summary>
        private void TrayProgress(double value, string text)
        {
            if (_tray == null) return;
            if (_trayRing == null) _trayRing = new TrayProgressRing(_tray);
            if (value < 0) _trayRing.Clear();
            else _trayRing.Show(value, text);
        }

        private void RevealCloud(string path)
        {
            try
            {
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                Process.Start(explorer, "/select,\"" + path + "\"");
            }
            catch (Exception ex) { ShowToast(ex.Message); }
        }

        private void OpenCloud(string path)
        {
            try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch (Exception ex) { ShowToast("No se pudo abrir con ReCap: " + ex.Message); }
        }
    }
}
