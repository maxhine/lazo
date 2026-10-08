using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Lazo
{
    internal sealed class MainWindow : Window
    {
        private readonly NetworkEngine _network = new NetworkEngine();
        private readonly bool _preview;
        private readonly DispatcherTimer _searchDelay;
        private readonly List<string> _results = new List<string>();
        private List<Peer> _peers = new List<Peer>();
        private Border _shell;
        private System.Windows.Shapes.Path _liquid;
        private readonly LiquidMotion _motion = new LiquidMotion();
        private Button _ownAvatar;
        private Border _settingsCard;
        private StackPanel _resultList;
        private StackPanel _dots;
        private WrapPanel _devices;
        private TextBlock _deviceEmpty;
        private TextBox _search;
        private TextBlock _placeholder;
        private TextBlock _empty;
        private TextBlock _status;
        private RowDefinition _resultsRow;
        private ScaleTransform _progressScale;
        private EverythingSearch _everything;
        private Hotkeys _hotkeys;
        private System.Windows.Forms.NotifyIcon _tray;
        private readonly System.Collections.Generic.List<ReceiveWindow> _inbox = new System.Collections.Generic.List<ReceiveWindow>();
        private readonly System.Collections.Generic.Dictionary<string, double> _sendProgress = new System.Collections.Generic.Dictionary<string, double>();
        private int _sendSerial;
        private Peer _picked;
        private readonly System.Collections.Generic.List<string> _manualFiles = new System.Collections.Generic.List<string>();
        private string _activeQuery;
        private string _networkError;
        private int _selectedRow;
        private bool _exiting;
        private bool _animating;
        private int _activeSends;
        private bool _settingsOpen;
        private bool _historyOpen;
        private Border _historyCard;
        private bool _eyeCareOpen;
        private bool _eyeCareAction;
        private int _eyeCareFitPass;
        private Border _eyeCareCard;
        private EyeCareCardView _eyeCareView;
        private bool _chatOpen;
        private Border _chatCard;
        private ChatPanel _chatPanel;
        private Border _chatDot;
        private bool _holding;
        private bool _shaking;
        private Grid _bodyHost;
        private TextBlock _tagline;
        private TextBox _peerFilterBox;
        private string _peerFilter = "";
        private double _ownAvatarSize = 24;
        private Button _navHome, _navChat, _navHistory, _navSettings, _navEye, _navShare;
        internal static string PreviewPanel;
        private System.Windows.Shapes.Path _navShareIcon;
        private TextBlock _sectionCount;
        private TextBlock _heroChip;
        private Border _shareDot;
        private bool _shareOpen;
        private Border _shareCard;
        private ScreenShareSession _share;
        private System.Windows.Shapes.Path _navHomeIcon, _navChatIcon, _navHistoryIcon, _navSettingsIcon, _navEyeIcon;
        private string _sendFailure;
        private Border _toast;
        private TextBlock _toastText;
        private DispatcherTimer _toastTimer;
        private DateTime _shownUtc;
        private System.Windows.Forms.Screen _launcherScreen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        private double _tileWidth = 128;
        private int _nameLines = 1;

        public MainWindow(bool preview = false, bool previewGlass = false, bool previewLiveSearch = false, bool previewStandard = false, bool previewDark = false, bool previewSettings = false, bool previewEmpty = false)
        {
            _preview = preview;
            Theme.Load();
            Identity.Load();
            ChatPrefs.Load();
            ProfilePhoto.Load();
            Updater.Load();
            if (previewGlass) Theme.SetForPreview(ThemeKind.Glass);
            if (previewDark) Theme.SetForPreview(ThemeKind.Dark);
            if (previewStandard) Theme.SetInterface(InterfaceKind.Standard, false);
            else if (preview) Theme.SetInterface(InterfaceKind.Minimal, false);
            Title = "Lazo";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            MinWidth = 200;
            MaxWidth = 1280;
            MinHeight = 80;
            MaxHeight = 860;
            SizeChanged += (s, e) =>
            {
                WindowPlacement.PlaceBottomCenter(this, _launcherScreen);
                if (_glassOn) Backdrop.Heal(this, 9, ShellRadius);
            };
            _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            _searchDelay.Tick += (s, e) => { _searchDelay.Stop(); RunSearch(); };
            if (_preview && !previewLiveSearch)
            {
                _peers = new List<Peer> {
                    new Peer { Id = Guid.NewGuid(), Name = "JQUIN" },
                    new Peer { Id = Guid.NewGuid(), Name = "EQUIPO-OFICINA" },
                    new Peer { Id = Guid.NewGuid(), Name = "ATLAS" }
                };
                _results.AddRange(new[] {
                    @"C:\Users\JQUIN\Documents\proyecto-neural-21.pdf",
                    @"C:\Users\JQUIN\Downloads\planos-finales.zip",
                    @"C:\Users\JQUIN\Desktop\presupuesto.xlsx"
                });
            }
            BuildUi();
            if (_preview && !previewStandard && !previewSettings && !previewEmpty && _search != null)
                _search.Text = previewLiveSearch ? "Lazo" : "proyecto";
            ApplySize(false);
            if (previewSettings) { _settingsOpen = true; SyncChrome(); }
            if (PreviewPanel == "share") { _shareOpen = true; SyncChrome(); }
            if (PreviewPanel == "chat") { _chatOpen = true; SyncChrome(); ChatThread first = ChatStore.Threads().OrderByDescending(t => t.When).FirstOrDefault(); if (first != null && _chatPanel != null) _chatPanel.Open(first.Id, first.Name); }
            if (PreviewPanel == "settings") { _settingsOpen = true; SyncChrome(); }
            Loaded += (s, e) =>
            {
                _motion.Enter(_shell, _liquid, EnableGlass);
                FocusSearch();
            };
            SourceInitialized += (s, e) =>
            {
                WindowPlacement.PlaceBottomCenter(this, _launcherScreen);
                if (!_preview || previewLiveSearch)
                {
                    _everything = new EverythingSearch(new System.Windows.Interop.WindowInteropHelper(this).Handle,
                        paths =>
                        {
                            if (_search == null || _search.Text.Trim() != _activeQuery) return;
                            _results.Clear();
                            _results.AddRange(paths);
                            if (_empty != null) _empty.Text = paths.Count == 0 ? "No se encontraron archivos." : "";
                            RenderResults();
                        },
                        message =>
                        {
                            if (_search == null || _search.Text.Trim() != _activeQuery) return;
                            _results.Clear();
                            if (_empty != null) _empty.Text = message;
                            RenderResults();
                        });
                    if (!_preview) SetupHotkeys();
                }
            };
            Closing += (s, e) => { if (!_exiting && !_preview) { e.Cancel = true; HideAnimated(); } };
            
            Closed += (s, e) => Cleanup();
            KeyDown += OnWindowKeyDown;
            if (!_preview)
            {
                SetupTray();
                _network.PeersChanged += OnPeersChanged;
                _network.OfferReceived += OnOfferReceived;
                _network.ReceiveProgress += OnReceiveProgress;
                _network.ReceiveFinished += OnReceiveFinished;
                _network.ChatReceived += OnChatReceived;
                _network.ChatSignal += OnChatSignal;
                _network.ScreenOffered += OnScreenOffered;
                Updater.CheckInBackground(text => Dispatcher.BeginInvoke((Action)(() => SetStatus(text))),
                    () => Dispatcher.BeginInvoke((Action)(() => { _exiting = true; Application.Current.Shutdown(); })));
                try { _network.Start(); }
                catch (Exception ex)
                {
                    _networkError = ex.Message;
                    SetStatus("Red no disponible: " + ex.Message);
                    RefreshDevices();
                }
                EyeCareService.Instance.Ticked += () => Dispatcher.BeginInvoke((Action)RefreshEyeCareCard);
                EyeCareService.Instance.StateChanged += () => Dispatcher.BeginInvoke((Action)RefreshEyeCareCard);
                EyeCareService.Instance.Start();
            }
        }

        private bool _glassOn;
        private static double ShellRadius { get { return Theme.IsMinimal ? 22 : 24; } }

        /// <summary>Activa el desenfoque del escritorio detrás de la ventana cuando termina de aparecer.</summary>
        private void EnableGlass()
        {
            // El acrílico de Windows se pinta sobre el rectángulo completo de una ventana transparente e
            // ignora la región redondeada, así que dejaba una caja cuadrada visible alrededor de Lazo.
            // El efecto de vidrio se hace dentro de la ventana (superficies translúcidas sobre el fondo
            // del tema) y aquí solo se garantiza que no quede ningún acrílico ni región de antes.
            _regionCheck.Stop();
            _glassOn = false;
            Backdrop.Disable(this);
        }

        private void DisableGlass()
        {
            _regionCheck.Stop();
            if (!_glassOn) return;
            _glassOn = false;
            Backdrop.Disable(this);
        }

        // La región redondeada recorta el desenfoque; si Windows o un cambio de tamaño la desajustan,
        // se corrige al instante (mensaje de posición) y como respaldo cada poco tiempo.
        private readonly DispatcherTimer _regionCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        private bool _regionHooked;

        private void HookRegion()
        {
            if (_regionHooked) return;
            System.Windows.Interop.HwndSource source = PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource;
            if (source == null) return;
            _regionHooked = true;
            _regionCheck.Tick += (s, e) => { if (_glassOn && IsVisible) Backdrop.Heal(this, 9, ShellRadius); };
            source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message == 0x0047 && _glassOn) Backdrop.Heal(this, 9, ShellRadius);
                return IntPtr.Zero;
            });
        }

        private void BuildUi()
        {
            bool animating = _motion.Active;
            _motion.Cancel();
            if (animating && _shell != null) _shell.Opacity = 1;
            _ownAvatar = null;
            _status = null;
            _search = null;
            _placeholder = null;
            _resultList = null;
            _resultsRow = null;
            _empty = null;
            _dots = null;
            _devices = null;
            _deviceEmpty = null;
            _peerFilterBox = null;
            _bodyHost = null;
            _tagline = null;
            _navHome = _navChat = _navHistory = _navSettings = _navEye = _navShare = null;
            _shareDot = null;
            _sectionCount = null;
            _heroChip = null;
            Resources.Remove(typeof(System.Windows.Controls.Primitives.ScrollBar));
            Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), Theme.ScrollBarStyle());
            Resources.Remove(typeof(CheckBox));
            Resources.Add(typeof(CheckBox), Theme.CheckBoxStyle());
            Resources.Remove(typeof(TextBox));
            Resources.Add(typeof(TextBox), Theme.TextBoxStyle());
            Grid outer = new Grid { Margin = new Thickness(9) };
            Content = outer;
            _shell = new Border
            {
                Background = Theme.ShellSurface(),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(ShellRadius),
                ClipToBounds = true,
                AllowDrop = true
            };
            outer.Children.Add(_shell);
            _liquid = new System.Windows.Shapes.Path
            {
                Fill = Theme.ShellSurface(),
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            outer.Children.Add(_liquid);
            _shell.DragEnter += (s, e) =>
            {
                e.Effects = HasFiles(e.Data) && (Theme.IsMinimal || _picked != null) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            _shell.Drop += (s, e) =>
            {
                string[] paths = ExistingFiles(e.Data.GetData(DataFormats.FileDrop) as string[]);
                if (paths.Length > 0) SendFiles(paths, _picked);
            };
            _shell.MouseLeftButtonDown += OnShellMouseDown;

            Grid root = new Grid();
            _shell.Child = root;
            root.SizeChanged += (s, e) =>
            {
                double radius = ShellRadius;
                root.Clip = new RectangleGeometry(new Rect(0, 0,
                    Math.Max(0, root.ActualWidth), Math.Max(0, root.ActualHeight)), radius, radius);
            };
            Grid layout = new Grid();
            root.Children.Add(layout);
            if (Theme.IsMinimal) BuildMinimal(layout);
            else BuildStandard(layout);

            Grid overlay = new Grid();
            _settingsCard = SettingsCard();
            _settingsCard.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
            _settingsCard.MouseLeftButtonDown += (s, e) => e.Handled = true;
            overlay.Children.Add(_settingsCard);
            if (Theme.IsMinimal) AddMinimalChrome(overlay);
            _historyCard = HistoryCard();
            _historyCard.Visibility = _historyOpen ? Visibility.Visible : Visibility.Collapsed;
            _historyCard.MouseLeftButtonDown += (s, e) => e.Handled = true;
            overlay.Children.Add(_historyCard);
            _eyeCareCard = EyeCareCard();
            _eyeCareCard.Visibility = _eyeCareOpen ? Visibility.Visible : Visibility.Collapsed;
            _eyeCareCard.MouseLeftButtonDown += (s, e) => e.Handled = true;
            overlay.Children.Add(_eyeCareCard);
            _chatCard = ChatCard();
            _chatCard.Visibility = _chatOpen ? Visibility.Visible : Visibility.Collapsed;
            _chatCard.MouseLeftButtonDown += (s, e) => e.Handled = true;
            overlay.Children.Add(_chatCard);
            _shareCard = ShareCard();
            _shareCard.Visibility = _shareOpen ? Visibility.Visible : Visibility.Collapsed;
            _shareCard.MouseLeftButtonDown += (s, e) => e.Handled = true;
            overlay.Children.Add(_shareCard);
            _progressScale = new ScaleTransform(0, 1);
            Border progress = new Border
            {
                Height = 2,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
                Child = new Border
                {
                    Background = Theme.SelectionFill,
                    RenderTransform = _progressScale,
                    RenderTransformOrigin = new Point(0, 0.5)
                }
            };
            overlay.Children.Add(progress);
            _toastText = Theme.Text("", 11, Theme.Ink);
            _toastText.TextTrimming = TextTrimming.CharacterEllipsis;
            _toastText.TextWrapping = TextWrapping.NoWrap;
            _toast = new Border
            {
                Background = Theme.Color(Theme.P.Dark ? "#E6000000" : "#F2FFFFFF"),
                BorderBrush = Theme.GlassRim(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14, 6, 14, 7),
                MaxWidth = 420,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(Theme.IsMinimal ? 12 : SidebarWidth + 12, 0, 12, 8),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Child = _toastText
            };
            Panel.SetZIndex(_toast, 5);
            overlay.Children.Add(_toast);
            root.Children.Add(overlay);
            RenderResults();
            RefreshDots();
            ApplyBodyMargin();
            RefreshDevices();
            if (_bodyHost != null && _chatOpen) _bodyHost.Visibility = Visibility.Collapsed;
            if (IsVisible) EnableGlass();
        }

        private void TogglePanel(string panel)
        {
            bool open = panel == "settings" ? !_settingsOpen : panel == "history" ? !_historyOpen :
                        panel == "chat" ? !_chatOpen : panel == "share" ? !_shareOpen : !_eyeCareOpen;
            _settingsOpen = _historyOpen = _eyeCareOpen = _chatOpen = _shareOpen = false;
            if (open)
            {
                if (panel == "settings") _settingsOpen = true;
                else if (panel == "history") _historyOpen = true;
                else if (panel == "chat") _chatOpen = true;
                else if (panel == "share") { _shareOpen = true; RefreshShareCard(); }
                else _eyeCareOpen = true;
            }
            SyncChrome();
        }

        private Border ChatDot(Thickness margin, HorizontalAlignment horizontal)
        {
            _chatDot = new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Background = Theme.SelectionFill,
                HorizontalAlignment = horizontal,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = margin,
                IsHitTestVisible = false,
                Visibility = ChatStore.UnreadTotal() > 0 ? Visibility.Visible : Visibility.Collapsed
            };
            Panel.SetZIndex(_chatDot, 3);
            return _chatDot;
        }

        private void AddMinimalChrome(Grid overlay)
        {
            Action<Button, double, string> place = (button, right, tip) =>
            {
                button.HorizontalAlignment = HorizontalAlignment.Right;
                button.VerticalAlignment = VerticalAlignment.Top;
                button.Margin = new Thickness(0, 6, right, 0);
                if (tip != null) button.ToolTip = tip;
                Panel.SetZIndex(button, 2);
                overlay.Children.Add(button);
            };
            place(IconButton("settings", () => TogglePanel("settings")), 8, "Ajustes");
            place(IconButton("close", HideAnimated), 40, "Cerrar");
            place(IconButton("history", () => TogglePanel("history")), 72, "Historial");
            place(IconButton("chat", () => TogglePanel("chat")), 104, "Chat");
            overlay.Children.Add(ChatDot(new Thickness(0, 8, 104, 0), HorizontalAlignment.Right));
            place(IconButton("eye", () => TogglePanel("eyecare")), 136, "Descanso Visual");
            place(IconButton("screen", () => TogglePanel("share")), 168, "Compartir pantalla");
        }

        private void BuildMinimal(Grid layout)
        {
            bool showResults = HasResults();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            _resultsRow = new RowDefinition { Height = showResults ? new GridLength(1, GridUnitType.Star) : new GridLength(0) };
            layout.RowDefinitions.Add(_resultsRow);

            Grid heading = new Grid { Margin = new Thickness(16, 7, 204, 0) };
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            TextBlock wordmark = Theme.Text("", 10, Theme.Muted, FontWeights.SemiBold);
            wordmark.VerticalAlignment = VerticalAlignment.Center;
            StackPanel identity = new StackPanel { Orientation = Orientation.Horizontal };
            identity.Children.Add(OwnAvatar());
            wordmark.Margin = new Thickness(6, 0, 0, 0);
            identity.Children.Add(wordmark);
            heading.Children.Add(identity);
            ScrollViewer dots = new ScrollViewer
            {
                Height = 20,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            _dots = new StackPanel { Orientation = Orientation.Horizontal };
            dots.Content = _dots;
            Grid.SetColumn(dots, 1);
            heading.Children.Add(dots);
            layout.Children.Add(heading);

            FrameworkElement search = SearchHost(true);
            search.Margin = new Thickness(14, 0, 14, 4);
            Grid.SetRow(search, 1);
            layout.Children.Add(search);

            Grid resultsArea = ResultsArea(false);
            Grid.SetRow(resultsArea, 2);
            layout.Children.Add(resultsArea);
        }

        private static double SidebarWidth { get { return 64; } }
        private const double HeaderHeight = 58;

        /// <summary>Botón circular de navegación con ícono vectorial; el estado activo aparece con un fundido.</summary>
        private Button NavButton(string iconName, string tip, Action click, out System.Windows.Shapes.Path icon)
        {
            icon = Icons.Make(iconName, 19, Theme.Ink);
            Border highlight = new Border { CornerRadius = new CornerRadius(19), Background = Theme.NavActive, Opacity = 0, IsHitTestVisible = false };
            Border hover = new Border { CornerRadius = new CornerRadius(19), Background = Theme.Color(Theme.P.Dark ? "#14FFFFFF" : "#0F000000"), Opacity = 0, IsHitTestVisible = false };
            Grid face = new Grid { Background = Brushes.Transparent, Width = 38, Height = 38 };
            face.Children.Add(hover);
            face.Children.Add(highlight);
            face.Children.Add(icon);
            Button button = new Button
            {
                Content = face,
                Width = 38,
                Height = 38,
                Margin = new Thickness(0, 3, 0, 3),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = tip,
                Tag = highlight
            };
            System.Windows.Automation.AutomationProperties.SetName(button, tip);
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = ButtonPresenter() };
            button.MouseEnter += (s, e) => hover.BeginAnimation(OpacityProperty, Theme.Animation(hover.Opacity, 1, 160));
            button.MouseLeave += (s, e) => hover.BeginAnimation(OpacityProperty, Theme.Animation(hover.Opacity, 0, 260));
            button.Click += (s, e) => { e.Handled = true; click(); };
            return button;
        }

        private void MarkNav(Button button, System.Windows.Shapes.Path icon, bool active)
        {
            if (button == null) return;
            Border highlight = button.Tag as Border;
            if (highlight != null) highlight.BeginAnimation(OpacityProperty, Theme.Animation(highlight.Opacity, active ? 1 : 0, 240));
            icon.Stroke = active ? Theme.NavActiveText : Theme.Ink;
        }

        private void UpdateNav()
        {
            if (_navHome == null) return;
            bool panel = _settingsOpen || _historyOpen || _chatOpen || _eyeCareOpen || _shareOpen;
            MarkNav(_navHome, _navHomeIcon, !panel);
            MarkNav(_navChat, _navChatIcon, _chatOpen);
            MarkNav(_navHistory, _navHistoryIcon, _historyOpen);
            MarkNav(_navSettings, _navSettingsIcon, _settingsOpen);
            MarkNav(_navEye, _navEyeIcon, _eyeCareOpen);
            MarkNav(_navShare, _navShareIcon, _shareOpen);
        }

        private void GoHome()
        {
            _settingsOpen = _historyOpen = _eyeCareOpen = _chatOpen = _shareOpen = false;
            if (_picked != null) { BackToDevices(); return; }
            SyncChrome();
        }

        private void BuildStandard(Grid layout)
        {
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SidebarWidth) });
            layout.ColumnDefinitions.Add(new ColumnDefinition());

            Border sidebar = Theme.Glass(new Border
            {
                Background = Theme.SidebarSurface,
                Margin = new Thickness(8, 8, 0, 8),
                CornerRadius = new CornerRadius(20)
            });
            Grid rail = new Grid { Margin = new Thickness(0, 12, 0, 10) };
            rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rail.RowDefinitions.Add(new RowDefinition());
            rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            System.Windows.Shapes.Path logo = Icons.Make("logo", 22, Theme.Ink, 2);
            logo.Margin = new Thickness(0, 2, 0, 6);
            logo.VerticalAlignment = VerticalAlignment.Top;
            rail.Children.Add(logo);
            StackPanel nav = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetRow(nav, 1);
            nav.Margin = new Thickness(0, 6, 0, 0);
            _navHome = NavButton("home", "Inicio", GoHome, out _navHomeIcon);
            nav.Children.Add(_navHome);
            _navChat = NavButton("chat", "Chat", () => TogglePanel("chat"), out _navChatIcon);
            Grid chatHost = new Grid();
            chatHost.Children.Add(_navChat);
            chatHost.Children.Add(ChatDot(new Thickness(0, 8, 8, 0), HorizontalAlignment.Right));
            nav.Children.Add(chatHost);
            _navShare = NavButton("screen", "Compartir pantalla", () => TogglePanel("share"), out _navShareIcon);
            Grid shareHost = new Grid();
            shareHost.Children.Add(_navShare);
            _shareDot = new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Theme.Danger,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 8, 0), IsHitTestVisible = false,
                Visibility = _share != null ? Visibility.Visible : Visibility.Collapsed
            };
            shareHost.Children.Add(_shareDot);
            nav.Children.Add(shareHost);
            _navHistory = NavButton("history", "Historial", () => TogglePanel("history"), out _navHistoryIcon);
            nav.Children.Add(_navHistory);
            _navSettings = NavButton("settings", "Ajustes", () => TogglePanel("settings"), out _navSettingsIcon);
            nav.Children.Add(_navSettings);
            rail.Children.Add(nav);

            // Botón de añadir: círculo con el acento y un anillo punteado alrededor.
            System.Windows.Shapes.Path addIcon = Icons.Make("plus", 19, Theme.PrimaryText, 2.2);
            Button add = new Button
            {
                Content = addIcon,
                Width = 36,
                Height = 36,
                Background = Theme.Primary,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Elegir archivos para enviar",
                Template = Theme.PillTemplate(18),
                Padding = new Thickness(0)
            };
            add.Click += (s, e) => { e.Handled = true; AddFiles(); };
            Grid addHost = new Grid { Width = 46, Height = 46, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
            addHost.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Stroke = new SolidColorBrush(Theme.Mix(Theme.AccentColor, Colors.Transparent, 0.45)),
                StrokeThickness = 1.2,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                IsHitTestVisible = false
            });
            addHost.Children.Add(add);
            Grid.SetRow(addHost, 2);
            rail.Children.Add(addHost);
            sidebar.Child = rail;
            layout.Children.Add(sidebar);

            Grid content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
            content.RowDefinitions.Add(new RowDefinition());
            Grid.SetColumn(content, 1);
            layout.Children.Add(content);

            Grid header = new Grid { Margin = new Thickness(16, 0, 12, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock greet = Theme.Text("", 16, Theme.Muted);
            greet.TextWrapping = TextWrapping.NoWrap;
            greet.Inlines.Add(new System.Windows.Documents.Run(Greeting() + ", "));
            greet.Inlines.Add(new System.Windows.Documents.Run(Identity.Current) { Foreground = Theme.Ink, FontWeight = FontWeights.Bold });
            titles.Children.Add(greet);
            _tagline = Theme.Text(TaglineText(), 12, Theme.Muted);
            _tagline.TextWrapping = TextWrapping.NoWrap;
            _tagline.Margin = new Thickness(0, -2, 0, 0);
            _tagline.Visibility = _tagline.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            titles.Children.Add(_tagline);
            header.Children.Add(titles);

            Border pill = Theme.Glass(new Border
            {
                Background = Theme.SegmentTrack,
                CornerRadius = new CornerRadius(17),
                Height = 34,
                MaxWidth = 300,
                MinWidth = 140,
                Margin = new Thickness(16, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid pillGrid = new Grid();
            pillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            pillGrid.ColumnDefinitions.Add(new ColumnDefinition());
            pillGrid.Children.Add(Icons.Make("search", 15, Theme.Muted));
            FrameworkElement field = _picked == null
                ? PeerFilterHost()
                : SearchHost(true, "Busca un archivo…", 14);
            Grid.SetColumn(field, 1);
            field.Margin = new Thickness(0, 0, 12, 0);
            pillGrid.Children.Add(field);
            pill.Child = pillGrid;
            Grid.SetColumn(pill, 1);
            header.Children.Add(pill);

            StackPanel tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _navEye = RoundTool("eye", "Descanso Visual", () => TogglePanel("eyecare"), out _navEyeIcon);
            _navEye.Margin = new Thickness(0, 0, 6, 0);
            tools.Children.Add(_navEye);
            System.Windows.Shapes.Path closeIcon;
            Button close = RoundTool("close", "Cerrar", HideAnimated, out closeIcon);
            if (Theme.IsMeet)
            {
                // Como «colgar» en Meet: píldora roja.
                ((Border)((Grid)close.Content).Children[0]).Background = Theme.Color("#DC362E");
                close.Width = 46;
                ((Grid)close.Content).Width = 46;
                closeIcon.Stroke = Brushes.White;
            }
            tools.Children.Add(close);
            Button self = OwnAvatar(34);
            self.Margin = new Thickness(8, 0, 0, 0);
            self.BorderBrush = Theme.SelectionFill;
            self.BorderThickness = new Thickness(2);
            tools.Children.Add(self);
            Grid.SetColumn(tools, 2);
            header.Children.Add(tools);
            content.Children.Add(header);

            Grid body = new Grid();
            Grid.SetRow(body, 1);
            content.Children.Add(body);
            _bodyHost = body;
            if (_picked == null) BuildDevices(body);
            else BuildTargetSearch(body);
            UpdateNav();
        }

        /// <summary>Botón redondo de vidrio para la cabecera.</summary>
        private Button RoundTool(string iconName, string tip, Action click, out System.Windows.Shapes.Path icon)
        {
            Button button = NavButton(iconName, tip, click, out icon);
            button.Width = 34;
            button.Height = 34;
            button.Margin = new Thickness(0);
            Grid face = (Grid)button.Content;
            face.Width = 34;
            face.Height = 34;
            face.Children.Insert(0, Theme.Glass(new Border { CornerRadius = new CornerRadius(17), Background = Theme.SegmentTrack, IsHitTestVisible = false }));
            icon.LayoutTransform = new ScaleTransform(16 / 24.0, 16 / 24.0);
            return button;
        }

        private string TaglineText()
        {
            if (_manualFiles.Count > 0)
                return _manualFiles.Count == 1 ? "1 archivo · elige un equipo" : _manualFiles.Count + " archivos · elige un equipo";
            return "";
        }

        private void UpdateTagline()
        {
            if (_tagline == null) return;
            _tagline.Text = TaglineText();
            _tagline.Visibility = _tagline.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddFiles()
        {
            if (_preview) return;
            ChooseFile();
        }

        private FrameworkElement PeerFilterHost()
        {
            Grid host = new Grid();
            TextBlock hint = Theme.Text("Buscar personas…", 14, Theme.Muted);
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.IsHitTestVisible = false;
            hint.TextWrapping = TextWrapping.NoWrap;
            host.Children.Add(hint);
            TextBox box = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Theme.Ink,
                FontFamily = Theme.Font,
                FontSize = 14,
                VerticalContentAlignment = VerticalAlignment.Center,
                CaretBrush = Theme.Ink,
                Text = _peerFilter ?? ""
            };
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            box.TextChanged += (s, e) =>
            {
                _peerFilter = box.Text.Trim();
                hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                RefreshDevices();
                ApplySize(false);
            };
            box.PreviewMouseDown += (s, e) => { if (_settingsOpen) CloseSettings(); };
            host.Children.Add(box);
            _peerFilterBox = box;
            return host;
        }

        private List<Peer> VisiblePeers()
        {
            if (string.IsNullOrEmpty(_peerFilter)) return _peers.ToList();
            return _peers.Where(p => p.Name != null &&
                p.Name.IndexOf(_peerFilter, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        }

        private const double HeroHeight = 124;
        private const double SectionHeight = 32;

        private void BuildDevices(Grid body)
        {
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeroHeight) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(SectionHeight) });
            body.RowDefinitions.Add(new RowDefinition());
            body.Children.Add(HeroCard());

            Grid section = new Grid { Margin = new Thickness(16, 0, 16, 0) };
            TextBlock title = Theme.Text("Equipos", 14, Theme.Ink, FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            section.Children.Add(title);
            _sectionCount = Theme.Text("", 12, Theme.Muted);
            _sectionCount.HorizontalAlignment = HorizontalAlignment.Right;
            _sectionCount.VerticalAlignment = VerticalAlignment.Center;
            section.Children.Add(_sectionCount);
            Grid.SetRow(section, 1);
            body.Children.Add(section);

            _devices = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(12, 0, 12, 12)
            };
            ScrollViewer scroll = new ScrollViewer
            {
                Content = _devices,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 2);
            body.Children.Add(scroll);
            _deviceEmpty = Theme.Text("", 13, Theme.Muted);
            _deviceEmpty.HorizontalAlignment = HorizontalAlignment.Center;
            _deviceEmpty.VerticalAlignment = VerticalAlignment.Center;
            _deviceEmpty.TextAlignment = TextAlignment.Center;
            _deviceEmpty.Margin = new Thickness(32, 0, 32, 0);
            _deviceEmpty.IsHitTestVisible = false;
            Grid.SetRow(_deviceEmpty, 2);
            body.Children.Add(_deviceEmpty);
        }

        private static string Greeting()
        {
            int hour = DateTime.Now.Hour;
            return hour < 12 ? "Buenos días" : hour < 19 ? "Buenas tardes" : "Buenas noches";
        }

        /// <summary>Tarjeta destacada de inicio. El fondo es un degradado del acento o una imagen elegida por la persona.</summary>
        private FrameworkElement HeroCard()
        {
            CornerRadius radius = new CornerRadius(20);
            System.Windows.Media.Imaging.BitmapSource image = HeroBackground.Load();
            Grid content = new Grid();
            if (image == null)
            {
                // Motivo de anillos entrelazados, en tonos claros sobre el acento.
                Grid art = new Grid { Width = 160, HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false, ClipToBounds = true };
                string[] rings = Theme.HeroIsLight ? new[] { "#40000000", "#26000000", "#14000000" } : new[] { "#F2FFFFFF", "#99FFFFFF", "#59FFFFFF" };
                double[,] spots = { { 14, -4 }, { 58, 20 }, { 26, 50 } };
                for (int i = 0; i < 3; i++)
                {
                    art.Children.Add(new System.Windows.Shapes.Ellipse
                    {
                        Width = 78,
                        Height = 78,
                        Stroke = Theme.Color(rings[i]),
                        StrokeThickness = 9,
                        Opacity = 0.7,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(spots[i, 0], spots[i, 1], 0, 0)
                    });
                }
                content.Children.Add(Theme.IsMeet ? CallFaces() : (UIElement)art);
            }
            else
            {
                // Velo oscuro a la izquierda para que el texto se lea sobre cualquier foto.
                LinearGradientBrush veil = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                veil.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0xB0, 0, 0, 0), 0));
                veil.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0x10, 0, 0, 0), 0.75));
                content.Children.Add(new Border { Background = veil, CornerRadius = radius, IsHitTestVisible = false });
            }
            content.Children.Add(Theme.Sheen(radius));

            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 140, 0) };
            _heroChip = Theme.Text(OnlineText(_peers.Count), 21, HeroInk(image != null), FontWeights.SemiBold);
            _heroChip.TextWrapping = TextWrapping.NoWrap;
            text.Children.Add(_heroChip);
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            actions.Children.Add(HeroButton("upload", "Enviar archivos", true, AddFiles, image != null));
            Button share = HeroButton("screen", "Compartir pantalla", false, () => TogglePanel("share"), image != null);
            share.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(share);
            text.Children.Add(actions);
            content.Children.Add(text);

            // Cambiar o restablecer el fondo: aparecen al pasar el cursor por la tarjeta.
            StackPanel edit = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 12, 0), Opacity = 0 };
            edit.Children.Add(HeroTool("image", "Cambiar fondo", ChooseHeroImage));
            if (image != null)
            {
                Button reset = HeroTool("reset", "Restablecer fondo", () => { HeroBackground.Clear(); RebuildHome(); });
                reset.Margin = new Thickness(6, 0, 0, 0);
                edit.Children.Add(reset);
            }
            content.Children.Add(edit);

            Border card = new Border
            {
                Background = image == null ? Theme.HeroSurface() : (Brush)new ImageBrush(image) { Stretch = Stretch.UniformToFill },
                CornerRadius = radius,
                BorderBrush = Theme.GlassRim(),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(16, 2, 16, 6),
                Child = content
            };
            content.Clip = null;
            card.SizeChanged += (s, e) => content.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, content.ActualWidth), Math.Max(0, content.ActualHeight)), 19, 19);
            card.MouseEnter += (s, e) => edit.BeginAnimation(OpacityProperty, Theme.Animation(edit.Opacity, 1, 200));
            card.MouseLeave += (s, e) => edit.BeginAnimation(OpacityProperty, Theme.Animation(edit.Opacity, 0, 300));
            card.ContextMenu = HeroMenu(image != null);
            return card;
        }

        /// <summary>Tema Meet: las personas en línea como en un recuadro de llamada, con anillo y halo.</summary>
        private UIElement CallFaces()
        {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 26, 0), IsHitTestVisible = false };
            Color accent = Theme.AccentColor;
            foreach (Peer peer in _peers.Take(3))
            {
                Grid face = new Grid { Width = 74, Height = 74, Margin = new Thickness(-8, 0, 0, 0) };
                face.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new SolidColorBrush(Color.FromArgb(0x3A, accent.R, accent.G, accent.B)) });
                face.Children.Add(new Border
                {
                    Width = 54, Height = 54, CornerRadius = new CornerRadius(27),
                    Background = Theme.AvatarSurface, BorderBrush = new SolidColorBrush(accent), BorderThickness = new Thickness(2.5),
                    Child = PhotoContent(peer.Photo, peer.Name, 49)
                });
                row.Children.Add(face);
            }
            if (_peers.Count > 3)
            {
                Border more = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = Theme.SoftSurface, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                more.Child = Theme.Text("+" + (_peers.Count - 3), 12, Theme.Ink, FontWeights.SemiBold);
                ((TextBlock)more.Child).HorizontalAlignment = HorizontalAlignment.Center;
                ((TextBlock)more.Child).VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(more);
            }
            return row;
        }

        private ContextMenu HeroMenu(bool custom)
        {
            ContextMenu menu = new ContextMenu();
            MenuItem change = new MenuItem { Header = "Cambiar fondo…" };
            change.Click += (s, e) => ChooseHeroImage();
            menu.Items.Add(change);
            if (custom)
            {
                MenuItem reset = new MenuItem { Header = "Restablecer fondo" };
                reset.Click += (s, e) => { HeroBackground.Clear(); RebuildHome(); };
                menu.Items.Add(reset);
            }
            return menu;
        }

        private void ChooseHeroImage()
        {
            if (_preview) return;
            if (PickHeroFile()) RebuildHome();
        }

        /// <summary>Pide una imagen para el fondo de inicio y la guarda; devuelve si se eligió una válida.</summary>
        private bool PickHeroFile()
        {
            OpenFileDialog dialog = new OpenFileDialog { Title = "Fondo de inicio", Filter = "Imágenes|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp", Multiselect = false };
            bool topmost = Topmost;
            _holding = true;
            Topmost = false;
            bool? chosen = dialog.ShowDialog(this);
            Topmost = topmost;
            _holding = false;
            if (chosen != true) return false;
            try { HeroBackground.Set(dialog.FileName); return true; }
            catch (Exception ex) { ShowToast("No se pudo usar esa imagen: " + ex.Message); return false; }
        }

        private void RebuildHome()
        {
            BuildUi();
            ApplySize(false);
        }

        private Button HeroTool(string icon, string tip, Action click)
        {
            Button button = new Button
            {
                Content = Icons.Make(icon, 16, Brushes.White),
                Width = 32,
                Height = 32,
                ToolTip = tip,
                Background = Theme.Color("#40000000"),
                BorderBrush = Theme.Color("#59FFFFFF"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Padding = new Thickness(0),
                Template = Theme.PillTemplate(16)
            };
            button.Click += (s, e) => { e.Handled = true; click(); };
            return button;
        }

        private static string OnlineText(int count)
        {
            return count == 0 ? "Nadie en línea" : count == 1 ? "1 persona en línea" : count + " personas en línea";
        }

        /// <summary>Texto sobre la tarjeta destacada: blanco sobre fotos y acentos oscuros, tinta sobre acentos claros.</summary>
        private static Brush HeroInk(bool photo)
        {
            return photo || !Theme.HeroIsLight ? Brushes.White : Theme.PrimaryText;
        }

        private static bool HeroIsLight(bool photo)
        {
            return !photo && Theme.HeroIsLight;
        }

        private Button HeroButton(string icon, string label, bool solid, Action click, bool photo)
        {
            bool light = HeroIsLight(photo);
            Brush ink = solid ? (light ? Brushes.White : Theme.Color("#1A1A1F")) : HeroInk(photo);
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
            System.Windows.Shapes.Path glyph = Icons.Make(icon, 14, ink, 2);
            glyph.Margin = new Thickness(0, 0, 7, 0);
            row.Children.Add(glyph);
            row.Children.Add(new TextBlock { Text = label, FontFamily = Theme.Font, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            Button button = new Button
            {
                Content = row,
                Height = 30,
                Padding = new Thickness(6, 0, 8, 0),
                Foreground = ink,
                Background = solid ? Theme.Color(light ? "#E61A1A1F" : "#F2FFFFFF") : Theme.Color(light ? "#1F000000" : "#33FFFFFF"),
                BorderBrush = Theme.Color(light ? "#33000000" : "#66FFFFFF"),
                BorderThickness = new Thickness(solid ? 0 : 1),
                Cursor = Cursors.Hand,
                Template = Theme.PillTemplate(15)
            };
            button.Click += (s, e) => { e.Handled = true; click(); };
            return button;
        }

        private void BuildTargetSearch(Grid body)
        {
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });

            StackPanel target = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 16, 0)
            };
            Button back = IconButton("back", BackToDevices);
            back.ToolTip = "Volver";
            back.VerticalAlignment = VerticalAlignment.Center;
            target.Children.Add(back);
            Button face = Avatar(_picked, 28, null);
            face.Margin = new Thickness(8, 0, 0, 0);
            target.Children.Add(face);
            TextBlock targetName = Theme.Text(_picked.Name, 13, Theme.Ink, FontWeights.SemiBold);
            targetName.VerticalAlignment = VerticalAlignment.Center;
            targetName.Margin = new Thickness(8, 0, 0, 0);
            targetName.MaxWidth = 320;
            targetName.TextTrimming = TextTrimming.CharacterEllipsis;
            targetName.TextWrapping = TextWrapping.NoWrap;
            target.Children.Add(targetName);
            body.Children.Add(target);

            Grid resultsArea = ResultsArea(true);
            Grid.SetRow(resultsArea, 1);
            body.Children.Add(resultsArea);

            Grid footer = new Grid();
            Border footerRule = new Border { Height = 1, Background = Theme.TileLine, VerticalAlignment = VerticalAlignment.Top };
            footer.Children.Add(footerRule);
            _status = Theme.Text("Elige un archivo para " + _picked.Name, 11, Theme.Muted);
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(16, 0, 8, 0);
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            footer.Children.Add(_status);
            Grid.SetRow(footer, 2);
            body.Children.Add(footer);
        }

        private FrameworkElement SearchHost(bool placeholder)
        {
            return SearchHost(placeholder, "Busca un archivo para enviarlo…", 18);
        }

        private FrameworkElement SearchHost(bool placeholder, string hint, double size)
        {
            Grid host = new Grid();
            TextBox box = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Theme.Ink,
                FontFamily = Theme.Font,
                FontSize = size,
                FontWeight = FontWeights.Normal,
                VerticalContentAlignment = VerticalAlignment.Center,
                CaretBrush = Theme.Ink
            };
            if (placeholder)
            {
                _placeholder = Theme.Text(hint, size, Theme.Muted);
                _placeholder.FontWeight = FontWeights.Normal;
                _placeholder.TextWrapping = TextWrapping.NoWrap;
                _placeholder.TextTrimming = TextTrimming.CharacterEllipsis;
                _placeholder.VerticalAlignment = VerticalAlignment.Center;
                _placeholder.IsHitTestVisible = false;
                host.Children.Add(_placeholder);
            }
            else _placeholder = null;
            host.Children.Add(box);
            box.TextChanged += (s, e) =>
            {
                if (_placeholder != null)
                    _placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                SyncChrome();
                if (_preview && _everything == null)
                {
                    RenderResults();
                    return;
                }
                _searchDelay.Stop();
                if (box.Text.Trim().Length == 0)
                {
                    _results.Clear();
                    if (_empty != null) _empty.Text = _manualFiles.Count == 0 ? "Escribe un nombre para buscar archivos." : "";
                    RenderResults();
                }
                else
                {
                    _results.Clear();
                    if (_empty != null) _empty.Text = "Buscando…";
                    RenderResults();
                    _searchDelay.Start();
                }
            };
            box.PreviewMouseDown += (s, e) => { if (_settingsOpen) CloseSettings(); };
            _search = box;
            return host;
        }

        private Grid ResultsArea(bool showEmpty)
        {
            Grid resultsArea = new Grid { Margin = new Thickness(8, 0, 8, 4) };
            ScrollViewer scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            _resultList = new StackPanel();
            scroll.Content = _resultList;
            resultsArea.Children.Add(scroll);
            if (showEmpty)
            {
                _empty = Theme.Text("Escribe un nombre para buscar archivos.", 12, Theme.Muted);
                _empty.HorizontalAlignment = HorizontalAlignment.Center;
                _empty.VerticalAlignment = VerticalAlignment.Center;
                _empty.IsHitTestVisible = false;
                resultsArea.Children.Add(_empty);
            }
            else _empty = null;
            return resultsArea;
        }

        private Border SettingsCard()
        {
            StackPanel panel = new StackPanel();
            if (!Theme.IsMinimal)
            {
                Grid head = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.ColumnDefinitions.Add(new ColumnDefinition());
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Border face = new Border
                {
                    Width = 40,
                    Height = 40,
                    CornerRadius = new CornerRadius(20),
                    Background = Theme.AvatarSurface,
                    BorderBrush = Theme.Line,
                    BorderThickness = new Thickness(1),
                    Child = PhotoContent(ProfilePhoto.Current, Identity.Current, 38)
                };
                head.Children.Add(face);
                TextBlock who = Theme.Text(Identity.Current, 16, Theme.Ink, FontWeights.SemiBold);
                who.TextWrapping = TextWrapping.NoWrap;
                who.TextTrimming = TextTrimming.CharacterEllipsis;
                who.VerticalAlignment = VerticalAlignment.Center;
                who.Margin = new Thickness(10, 0, 8, 0);
                Grid.SetColumn(who, 1);
                head.Children.Add(who);
                Button closePanel = IconButton("close", () => TogglePanel("settings"));
                closePanel.ToolTip = "Cerrar";
                Grid.SetColumn(closePanel, 2);
                head.Children.Add(closePanel);
                panel.Children.Add(head);
            }
            panel.Children.Add(Theme.Label("Modo"));
            panel.Children.Add(Segment("Minimal", "Standard", Theme.IsMinimal,
                () => ChooseMode(InterfaceKind.Minimal), () => ChooseMode(InterfaceKind.Standard)));
            TextBlock look = Theme.Label("Apariencia");
            look.Margin = new Thickness(0, 14, 0, 0);
            panel.Children.Add(look);
            panel.Children.Add(ThemeChips());
            TextBlock accentLabel = Theme.Label("Acento");
            accentLabel.Margin = new Thickness(0, 14, 0, 6);
            panel.Children.Add(accentLabel);
            panel.Children.Add(AccentPicker());
            CheckBox glass = new CheckBox
            {
                Content = "Liquid Glass",
                IsChecked = Theme.LiquidGlass,
                Foreground = Theme.Ink,
                FontFamily = Theme.Font,
                FontSize = 12.5,
                Margin = new Thickness(0, 14, 0, 2)
            };
            glass.Checked += (s, e) => ChooseGlass(true);
            glass.Unchecked += (s, e) => ChooseGlass(false);
            panel.Children.Add(glass);
            if (!Theme.IsMinimal)
            {
                TextBlock heroLabel = Theme.Label("Fondo de inicio");
                heroLabel.Margin = new Thickness(0, 14, 0, 6);
                panel.Children.Add(heroLabel);
                Grid heroRow = new Grid();
                heroRow.ColumnDefinitions.Add(new ColumnDefinition());
                heroRow.ColumnDefinitions.Add(new ColumnDefinition());
                Button heroChange = Theme.Button("Cambiar imagen", false);
                heroChange.MinHeight = 34;
                heroChange.Click += (s, e) => { ChooseHeroImage(); _settingsOpen = true; SyncChrome(); };
                heroRow.Children.Add(heroChange);
                Button heroReset = Theme.Button("Restablecer", false);
                heroReset.MinHeight = 34;
                heroReset.Margin = new Thickness(8, 0, 0, 0);
                heroReset.IsEnabled = HeroBackground.IsCustom;
                heroReset.Click += (s, e) => { HeroBackground.Clear(); _settingsOpen = true; RebuildHome(); };
                Grid.SetColumn(heroReset, 1);
                heroRow.Children.Add(heroReset);
                panel.Children.Add(heroRow);
            }
            TextBlock nameLabel = Theme.Label("Nombre visible");
            nameLabel.Margin = new Thickness(0, 14, 0, 6);
            panel.Children.Add(nameLabel);
            TextBox nameBox = new TextBox
            {
                Text = Identity.Current,
                FontFamily = Theme.Font,
                FontSize = 13,
                Foreground = Theme.Ink,
                Background = Theme.TileSurface,
                BorderBrush = Theme.TileLine,
                Padding = new Thickness(10, 7, 10, 7),
                CaretBrush = Theme.Ink
            };
            nameBox.LostFocus += (s, e) => { Identity.Set(nameBox.Text, !_preview); RefreshOwnAvatar(); };
            nameBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Identity.Set(nameBox.Text, !_preview); RefreshOwnAvatar(); e.Handled = true; } };
            panel.Children.Add(nameBox);
            Grid photoRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            photoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            photoRow.ColumnDefinitions.Add(new ColumnDefinition());
            photoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ContentControl portrait = new ContentControl { Content = PhotoContent(ProfilePhoto.Current, Identity.Current, 52), Width = 52, Height = 52, ToolTip = "Tu foto de perfil" };
            photoRow.Children.Add(new Border { Width = 54, Height = 54, CornerRadius = new CornerRadius(27), Background = Theme.AvatarSurface, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Child = portrait });
            Button changePhoto = Theme.Button("Cambiar foto", false);
            changePhoto.MinHeight = 34; changePhoto.Margin = new Thickness(12, 0, 8, 0); changePhoto.VerticalAlignment = VerticalAlignment.Center;
            Button clearPhoto = Theme.Button("Quitar", false);
            clearPhoto.MinHeight = 34; clearPhoto.VerticalAlignment = VerticalAlignment.Center;
            Action<string> updatePhoto = path =>
            {
                try { ProfilePhoto.Set(path, !_preview); portrait.Content = PhotoContent(ProfilePhoto.Current, Identity.Current, 52); RefreshOwnAvatar(); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Foto de perfil", MessageBoxButton.OK, MessageBoxImage.Information); }
            };
            changePhoto.Click += (s, e) =>
            {
                _holding = true;
                bool topmost = Topmost;
                try
                {
                    Topmost = false;
                    OpenFileDialog dialog = new OpenFileDialog { Title = "Foto de perfil", Filter = "Imágenes|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.ico", Multiselect = false };
                    if (dialog.ShowDialog(this) == true) updatePhoto(dialog.FileName);
                }
                finally { Topmost = topmost; _holding = false; }
            };
            clearPhoto.Click += (s, e) => updatePhoto(null);
            Grid.SetColumn(changePhoto, 1);
            photoRow.Children.Add(changePhoto);
            Grid.SetColumn(clearPhoto, 2);
            photoRow.Children.Add(clearPhoto);
            panel.Children.Add(photoRow);
            CheckBox startup = new CheckBox
            {
                Content = "Abrir al iniciar Windows",
                IsChecked = Startup.IsEnabled(),
                Foreground = Theme.Ink,
                FontFamily = Theme.Font,
                FontSize = 12,
                Margin = new Thickness(0, 16, 0, 2)
            };
            startup.Checked += (s, e) => Startup.Set(true);
            startup.Unchecked += (s, e) => { Startup.Set(false); startup.IsChecked = Startup.IsEnabled(); };
            panel.Children.Add(startup);
            CheckBox updates = new CheckBox
            {
                Content = "Actualizar automáticamente",
                IsChecked = Updater.Enabled,
                Foreground = Theme.Ink,
                FontFamily = Theme.Font,
                FontSize = 12,
                Margin = new Thickness(0, 10, 0, 2)
            };
            updates.Checked += (s, e) => { Updater.Set(true, !_preview); if (!_preview) Updater.CheckInBackground(text => Dispatcher.BeginInvoke((Action)(() => SetStatus(text))), () => Dispatcher.BeginInvoke((Action)(() => { _exiting = true; Application.Current.Shutdown(); }))); };
            updates.Unchecked += (s, e) => Updater.Set(false, !_preview);
            panel.Children.Add(updates);
            TextBlock chatAlerts = Theme.Label("Avisos de chat");
            chatAlerts.Margin = new Thickness(0, 14, 0, 0);
            panel.Children.Add(chatAlerts);
            panel.Children.Add(Segment("Bandeja", "Notificación", !ChatPrefs.UseBalloon,
                () => ChatPrefs.Set(false), () => ChatPrefs.Set(true)));
            Button checkUpdates = Theme.Button("Verificar actualizaciones", false);
            checkUpdates.HorizontalAlignment = HorizontalAlignment.Left;
            checkUpdates.Margin = new Thickness(0, 8, 0, 0);
            checkUpdates.MinHeight = 34;
            TextBlock updateState = Theme.Text("", 11, Theme.Muted);
            updateState.Margin = new Thickness(0, 4, 0, 0);
            checkUpdates.Click += (s, e) =>
            {
                if (_preview) return;
                checkUpdates.IsEnabled = false;
                Updater.Check(text => Dispatcher.BeginInvoke((Action)(() =>
                {
                    updateState.Text = text;
                    SetStatus(text);
                    if (text != "Buscando actualizaciones…" && text != "Instalando actualización…") checkUpdates.IsEnabled = true;
                })), () => Dispatcher.BeginInvoke((Action)(() => { _exiting = true; Application.Current.Shutdown(); })));
            };
            panel.Children.Add(checkUpdates);
            panel.Children.Add(updateState);
            // Espacio a la derecha para que la barra de desplazamiento no tape los controles.
            panel.Margin = new Thickness(0, 0, 12, 0);
            ScrollViewer scroll = new ScrollViewer
            {
                Content = panel,
                MaxHeight = Theme.IsMinimal ? 470 : double.PositiveInfinity,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            return StylePanel(new Border
            {
                Background = Theme.CardSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 8),
                Width = 300,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 40, 8, 0),
                Child = scroll
            }, 290);
        }

        private Border StylePanel(Border card, double width)
        {
            Theme.Glass(card);
            card.CornerRadius = new CornerRadius(Theme.IsMinimal ? 18 : 24);
            card.Background = Theme.CardSurface;
            if (Theme.IsMinimal) return card;
            card.Width = width;
            card.MaxHeight = double.PositiveInfinity;
            card.Padding = new Thickness(16, 14, 16, 12);
            card.HorizontalAlignment = HorizontalAlignment.Right;
            card.VerticalAlignment = VerticalAlignment.Stretch;
            card.Margin = new Thickness(0, HeaderHeight, 12, 12);
            return card;
        }

        private double PanelWidth()
        {
            if (Theme.IsMinimal) return 0;
            if (_settingsOpen) return 290;
            if (_historyOpen) return 270;
            if (_eyeCareOpen) return 300;
            if (_shareOpen) return 300;
            return 0;
        }

        private UIElement Segment(string left, string right, bool leftSelected, Action selectLeft, Action selectRight)
        {
            return Choices(new[] { left, right }, leftSelected ? 0 : 1, index => { if (index == 0) selectLeft(); else selectRight(); });
        }

        /// <summary>Selector de apariencia: una miniatura por tema con su fondo, una superficie y su acento.</summary>
        private UIElement ThemeChips()
        {
            WrapPanel grid = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            ThemeKind[] kinds = { ThemeKind.Wine, ThemeKind.Raycast, ThemeKind.Glass, ThemeKind.Dark, ThemeKind.Warm, ThemeKind.Meet, ThemeKind.Image };
            string[] names = { "Vino", "Plano", "Vidrio", "Oscuro", "Cálido", "Meet", "Imagen" };
            for (int i = 0; i < kinds.Length; i++)
            {
                ThemeKind kind = kinds[i];
                Palette p = Theme.PaletteFor(kind);
                Grid preview = new Grid { Width = 58, Height = 38 };
                LinearGradientBrush shell = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                shell.GradientStops.Add(new GradientStop(Theme.Parse(p.Shell0), 0));
                shell.GradientStops.Add(new GradientStop(Theme.Parse(p.Shell2), 1));
                Brush back = shell;
                if (kind == ThemeKind.Image)
                {
                    System.Windows.Media.Imaging.BitmapSource photo = HeroBackground.Load();
                    if (photo != null) back = new ImageBrush(photo) { Stretch = Stretch.UniformToFill };
                }
                preview.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = back });
                preview.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = Theme.Color(p.Surface), Width = 26, Height = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(7, 0, 0, 7) });
                preview.Children.Add(new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = Theme.Color(p.Accent), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 7, 7) });
                bool selected = Theme.Mode == kind;
                Border ring = new Border { CornerRadius = new CornerRadius(12), BorderBrush = selected ? Theme.Ink : Theme.Line, BorderThickness = new Thickness(selected ? 2 : 1), Padding = new Thickness(2), Child = preview };
                StackPanel stack = new StackPanel();
                stack.Children.Add(ring);
                TextBlock label = Theme.Text(names[i], 11, selected ? Theme.Ink : Theme.Muted, selected ? FontWeights.SemiBold : FontWeights.Normal);
                label.HorizontalAlignment = HorizontalAlignment.Center;
                label.Margin = new Thickness(0, 3, 0, 0);
                stack.Children.Add(label);
                Button button = Plain(stack, () => ChooseAppearance(kind));
                button.Margin = new Thickness(0, 0, 6, 6);
                button.ToolTip = names[i];
                grid.Children.Add(button);
            }
            return grid;
        }

        /// <summary>Selector segmentado: la píldora activa se desliza hasta la opción elegida y luego se aplica.</summary>
        private UIElement Choices(string[] labels, int selected, Action<int> pick)
        {
            Grid grid = new Grid();
            TranslateTransform slide = new TranslateTransform();
            Border indicator = new Border
            {
                Background = Theme.SegmentActive,
                CornerRadius = new CornerRadius(15),
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = slide,
                IsHitTestVisible = false,
                Visibility = selected >= 0 ? Visibility.Visible : Visibility.Hidden
            };
            Grid.SetColumnSpan(indicator, Math.Max(1, labels.Length));
            grid.Children.Add(indicator);
            Button[] buttons = new Button[labels.Length];
            int current = selected;
            Action<bool> place = animate =>
            {
                if (grid.ActualWidth <= 0 || labels.Length == 0) return;
                double width = grid.ActualWidth / labels.Length;
                indicator.Width = width;
                double x = Math.Max(0, current) * width;
                if (animate) slide.BeginAnimation(TranslateTransform.XProperty, Theme.Animation(slide.X, x, 260));
                else { slide.BeginAnimation(TranslateTransform.XProperty, null); slide.X = x; }
                for (int i = 0; i < buttons.Length; i++)
                {
                    buttons[i].Foreground = i == current ? Theme.SegmentActiveText : Theme.SegmentMutedText;
                    buttons[i].FontWeight = i == current ? FontWeights.SemiBold : FontWeights.Normal;
                }
            };
            for (int i = 0; i < labels.Length; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                int index = i;
                Button button = SegmentButton(labels[i], i == selected, () =>
                {
                    if (index == current) return;
                    current = index;
                    indicator.Visibility = Visibility.Visible;
                    place(true);
                    // Se aplica cuando la píldora termina de moverse, para que el cambio se vea fluido.
                    DispatcherTimer apply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                    apply.Tick += (s, e) => { apply.Stop(); pick(index); };
                    apply.Start();
                });
                button.Background = Brushes.Transparent;
                buttons[i] = button;
                Grid.SetColumn(button, i);
                grid.Children.Add(button);
            }
            grid.SizeChanged += (s, e) => place(false);
            return new Border
            {
                Background = Theme.SegmentTrack,
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(3),
                Margin = new Thickness(0, 6, 0, 2),
                Child = grid
            };
        }

        private Button SegmentButton(string label, bool selected, Action click)
        {
            Button button = new Button
            {
                Content = label,
                Height = 30,
                FontFamily = Theme.Font,
                FontSize = 11.5,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = selected ? Theme.SegmentActiveText : Theme.SegmentMutedText,
                Background = selected ? Theme.SegmentActive : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(Border));
            frame.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
            frame.AppendChild(ButtonPresenter());
            template.VisualTree = frame;
            if (!selected)
            {
                Trigger hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
                hover.Setters.Add(new Setter(Button.ForegroundProperty, Theme.Ink));
                template.Triggers.Add(hover);
            }
            button.Template = template;
            if (click != null) button.Click += (s, e) => { e.Handled = true; click(); };
            return button;
        }

        private Button IconButton(string iconName, Action click)
        {
            System.Windows.Shapes.Path icon = Icons.Make(iconName, 17, Theme.Ink);
            Border hover = new Border { CornerRadius = new CornerRadius(15), Background = Theme.Color(Theme.P.Dark ? "#1AFFFFFF" : "#12000000"), Opacity = 0, IsHitTestVisible = false };
            Grid face = new Grid { Background = Brushes.Transparent };
            face.Children.Add(hover);
            face.Children.Add(icon);
            Button button = new Button
            {
                Content = face,
                Width = 30,
                Height = 30,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = ButtonPresenter() };
            button.MouseEnter += (s, e) => hover.BeginAnimation(OpacityProperty, Theme.Animation(hover.Opacity, 1, 150));
            button.MouseLeave += (s, e) => hover.BeginAnimation(OpacityProperty, Theme.Animation(hover.Opacity, 0, 250));
            if (click != null) button.Click += (s, e) => { e.Handled = true; click(); };
            return button;
        }

        /// <summary>Muestras de acento: la del tema, ocho colores y uno personalizado.</summary>
        /// <summary>Muestra «Automático»: la foto de inicio en miniatura, con el color que se toma de ella.</summary>
        private Button AutoSwatch()
        {
            Grid face = new Grid { Width = 28, Height = 28 };
            System.Windows.Media.Imaging.BitmapSource photo = HeroBackground.Load();
            face.Children.Add(new System.Windows.Shapes.Ellipse { Margin = new Thickness(3), Fill = photo == null ? (Brush)Theme.SelectionFill : new ImageBrush(photo) { Stretch = Stretch.UniformToFill } });
            string[] picks = ImageTheme.Suggestions;
            if (picks.Length > 0)
                face.Children.Add(new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = Theme.Color(picks[0]), Stroke = Brushes.White, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
            if (Theme.AccentFromImage)
                face.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = Theme.Ink, StrokeThickness = 2 });
            Button button = new Button { Content = face, ToolTip = "Automático: el color principal de tu imagen", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 4, 4), Template = Theme.PillTemplate(14), Padding = new Thickness(0) };
            button.Click += (s, e) => { e.Handled = true; ChooseAccent(Theme.ImageAccent); };
            return button;
        }

        private UIElement AccentPicker()
        {
            StackPanel host = new StackPanel();
            string[] fromImage = HeroBackground.IsCustom ? ImageTheme.Suggestions : new string[0];
            if (fromImage.Length > 0)
            {
                // Propuestas a partir de la foto de inicio: «Automático» sigue a la foto aunque cambie.
                TextBlock hint = Theme.Text("De tu imagen", 11, Theme.Muted);
                hint.Margin = new Thickness(0, 0, 0, 4);
                host.Children.Add(hint);
                WrapPanel suggestions = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                suggestions.Children.Add(AutoSwatch());
                foreach (string hex in fromImage) suggestions.Children.Add(Swatch(hex, Theme.CustomAccent == hex, "Color de tu imagen", hex));
                host.Children.Add(suggestions);
                TextBlock more2 = Theme.Text("Colores", 11, Theme.Muted);
                more2.Margin = new Thickness(0, 0, 0, 4);
                host.Children.Add(more2);
            }
            WrapPanel row = new WrapPanel();
            host.Children.Add(row);
            row.Children.Add(Swatch(Theme.P.Accent, Theme.CustomAccent.Length == 0, "Del tema", ""));
            foreach (string hex in Theme.AccentChoices)
                if (hex != Theme.P.Accent) row.Children.Add(Swatch(hex, Theme.CustomAccent == hex, hex, hex));
            string[] suggested = HeroBackground.IsCustom ? ImageTheme.Suggestions : new string[0];
            bool custom = Theme.CustomAccent.Length > 0 && !Theme.AccentFromImage && Array.IndexOf(Theme.AccentChoices, Theme.CustomAccent) < 0 && Array.IndexOf(suggested, Theme.CustomAccent) < 0;
            Button more = Swatch(custom ? Theme.CustomAccent : null, custom, "Otro color…", null);
            more.Click += (s, e) =>
            {
                e.Handled = true;
                using (System.Windows.Forms.ColorDialog dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true })
                {
                    Color current = Theme.AccentColor;
                    dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
                    bool topmost = Topmost;
                    Topmost = false;
                    _holding = true;
                    System.Windows.Forms.DialogResult result = dialog.ShowDialog();
                    Topmost = topmost;
                    _holding = false;
                    if (result == System.Windows.Forms.DialogResult.OK)
                        ChooseAccent("#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2"));
                }
            };
            row.Children.Add(more);
            return host;
        }

        private Button Swatch(string hex, bool selected, string tip, string value)
        {
            Grid face = new Grid { Width = 28, Height = 28 };
            if (hex != null)
                face.Children.Add(new System.Windows.Shapes.Ellipse { Fill = Theme.Color(hex), Margin = new Thickness(3) });
            else
            {
                LinearGradientBrush rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                rainbow.GradientStops.Add(new GradientStop(Theme.Parse("#F2547D"), 0));
                rainbow.GradientStops.Add(new GradientStop(Theme.Parse("#F2A33A"), 0.35));
                rainbow.GradientStops.Add(new GradientStop(Theme.Parse("#22A55B"), 0.65));
                rainbow.GradientStops.Add(new GradientStop(Theme.Parse("#3B82F6"), 1));
                face.Children.Add(new System.Windows.Shapes.Ellipse { Fill = rainbow, Margin = new Thickness(3) });
                face.Children.Add(Icons.Make("plus", 14, Brushes.White, 2.4));
            }
            if (selected)
                face.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = Theme.Ink, StrokeThickness = 2 });
            Button button = new Button { Content = face, ToolTip = tip, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 4, 4), Template = Theme.PillTemplate(14), Padding = new Thickness(0) };
            if (value != null) button.Click += (s, e) => { e.Handled = true; ChooseAccent(value); };
            return button;
        }

        private void ChooseAccent(string hex)
        {
            Theme.SetAccent(hex, !_preview);
            _settingsOpen = true;
            BuildUi();
            ApplySize(false);
        }

        private void ChooseGlass(bool on)
        {
            if (Theme.LiquidGlass == on) return;
            Theme.SetLiquidGlass(on, !_preview);
            _settingsOpen = true;
            if (!on) DisableGlass();
            BuildUi();
            ApplySize(false);
        }

        private void ChooseMode(InterfaceKind kind)
        {
            _settingsOpen = true;
            if (Theme.Interface == kind)
            {
                SyncChrome();
                return;
            }
            Theme.SetInterface(kind, !_preview);
            _picked = null;
            _manualFiles.Clear();
            _results.Clear();
            _selectedRow = 0;
            BuildUi();
            ApplySize(true);
        }

        private void ChooseAppearance(ThemeKind kind)
        {
            string query = _search != null ? _search.Text : "";
            _settingsOpen = true;
            if (Theme.Mode == kind)
            {
                SyncChrome();
                return;
            }
            // El tema Imagen necesita una foto: si aún no hay, se pide antes de aplicarlo.
            if (kind == ThemeKind.Image && !HeroBackground.IsCustom && !_preview)
            {
                if (!PickHeroFile()) { BuildUi(); ApplySize(false); return; }
            }
            Theme.SetAppearance(kind, !_preview);
            BuildUi();
            if (_search != null && query.Length > 0) _search.Text = query;
            RenderResults();
            ApplySize(false);
        }

        private void CloseSettings()
        {
            if (!_settingsOpen) return;
            _settingsOpen = false;
            SyncChrome();
        }

        private void OpenEyeCare()
        {
            _eyeCareOpen = true;
            _settingsOpen = false;
            _historyOpen = false;
            _chatOpen = false;
            _shareOpen = false;
            SyncChrome();
        }

        private void CloseEyeCare()
        {
            if (!_eyeCareOpen) return;
            _eyeCareOpen = false;
            SyncChrome();
        }

        private void OpenSearch(Peer peer)
        {
            _picked = peer;
            _settingsOpen = false;
            _historyOpen = false;
            _eyeCareOpen = false;
            _chatOpen = false;
            _shareOpen = false;
            _manualFiles.Clear();
            _results.Clear();
            _selectedRow = 0;
            BuildUi();
            ApplySize(true);
            if (_search != null) _search.Focus();
        }

        private void BackToDevices()
        {
            _picked = null;
            _manualFiles.Clear();
            _results.Clear();
            _selectedRow = 0;
            _settingsOpen = false;
            BuildUi();
            ApplySize(true);
        }

        private bool HasResults()
        {
            if (_manualFiles.Count > 0) return true;
            return _search != null && _search.Text.Trim().Length > 0;
        }

        private void SyncChrome()
        {
            if (_settingsCard != null)
                Theme.Reveal(_settingsCard, _settingsOpen, 28);
            if (_historyCard != null)
            {
                if (_historyOpen) _historyCard.Child = HistoryList();
                Theme.Reveal(_historyCard, _historyOpen, 28);
            }
            if (_eyeCareCard != null)
            {
                if (_eyeCareOpen && _eyeCareView != null) _eyeCareView.UpdateUi();
                Theme.Reveal(_eyeCareCard, _eyeCareOpen, 28);
            }
            if (_chatCard != null)
            {
                Theme.Reveal(_chatCard, _chatOpen, 28);
                if (_chatPanel != null) _chatPanel.SetActive(_chatOpen);
            }
            if (_shareCard != null) Theme.Reveal(_shareCard, _shareOpen, 28);
            if (_bodyHost != null) Theme.Reveal(_bodyHost, !_chatOpen, 0);
            if (_resultsRow != null && Theme.IsMinimal)
                _resultsRow.Height = HasResults() ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            ApplyBodyMargin();
            UpdateNav();
            ApplySize(false);
        }

        private void ApplyBodyMargin()
        {
            if (_bodyHost == null) return;
            double panel = PanelWidth();
            Thickness target = new Thickness(0, 0, panel > 0 ? panel + 12 : 0, 0);
            if (!IsVisible || !SystemParameters.ClientAreaAnimation) { _bodyHost.BeginAnimation(MarginProperty, null); _bodyHost.Margin = target; return; }
            _bodyHost.BeginAnimation(MarginProperty, new ThicknessAnimation(_bodyHost.Margin, target, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        }

        private void ScreenLimits(out double maxWidth, out double maxHeight)
        {
            double scale = 1;
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
                scale = source.CompositionTarget.TransformToDevice.M22;
            if (scale < 0.5) scale = 1;
            maxHeight = Math.Min(MaxHeight, (_launcherScreen.WorkingArea.Height / scale) - 28);
            maxWidth = Math.Min(MaxWidth, (_launcherScreen.WorkingArea.Width / scale) - 28);
        }

        private void ApplySize(bool center)
        {
            double width = 560;
            double height = 104;
            if (!Theme.IsMinimal)
            {
                double bodyWidth;
                double bodyHeight;
                if (_picked == null)
                {
                    int count = VisiblePeers().Count;
                    int columns;
                    int rows;
                    DeviceGrid(count, out columns, out rows);
                    MeasureDevices();
                    bodyWidth = Math.Max(540, 32 + columns * (_tileWidth + 8));
                    bodyHeight = HeroHeight + SectionHeight + (count == 0 ? 90 : 4 + rows * (TileHeight() + 8));
                }
                else
                {
                    bodyWidth = 520;
                    bodyHeight = 340;
                }
                width = SidebarWidth + bodyWidth + 14;
                height = Math.Max(390, HeaderHeight + bodyHeight + 14);
                double panel = PanelWidth();
                if (panel > 0) width += panel + 12;
                if (_settingsOpen) height = Math.Max(height, 560);
                else if (_historyOpen) height = Math.Max(height, 380);
                else if (_shareOpen) height = Math.Max(height, 500);
            }
            else
            {
                width = 560;
                bool results = HasResults();
                if (results) height = 440;
                else if (_settingsOpen) height = 420;
                else height = 104;
                if (_settingsOpen || _historyOpen) width = 360;
                if (_shareOpen) { width = 380; height = 540; }
                if (_historyOpen && !results && !_settingsOpen) height = 280;
            }
            if (_chatOpen)
                FitChat(ref width, ref height);
            else if (_eyeCareOpen)
            {
                FitEyeCare(ref width, ref height);
                if (_eyeCareFitPass < 1)
                {
                    _eyeCareFitPass++;
                    Dispatcher.BeginInvoke((Action)(() => { if (_eyeCareOpen) ApplySize(false); }), DispatcherPriority.Loaded);
                }
            }
            else _eyeCareFitPass = 0;
            if (!Theme.IsMinimal)
            {
                double maxWidth;
                double maxHeight;
                ScreenLimits(out maxWidth, out maxHeight);
                width = Math.Min(width, maxWidth);
                height = Math.Min(height, maxHeight);
            }
            if (!IsVisible)
            {
                BeginAnimation(WidthProperty, null);
                BeginAnimation(HeightProperty, null);
                Width = width;
                Height = height;
                return;
            }
            if (Math.Abs(Width - width) > 1)
                BeginAnimation(WidthProperty, Theme.Animation(Width, width, 260));
            if (Math.Abs(Height - height) > 1)
                BeginAnimation(HeightProperty, Theme.Animation(Height, height, 260));
        }

        private void FitChat(ref double width, ref double height)
        {
            double maxWidth;
            double maxHeight;
            ScreenLimits(out maxWidth, out maxHeight);
            width = Math.Min(maxWidth, Theme.IsMinimal ? 640 : 720);
            height = Math.Min(maxHeight, Theme.IsMinimal ? 520 : 470);
        }

        private void FitEyeCare(ref double width, ref double height)
        {
            double cardWidth = Theme.IsMinimal ? 320 : 340;
            double content = 480;
            if (_eyeCareView != null)
            {
                _eyeCareView.Measure(new Size(cardWidth - 24, double.PositiveInfinity));
                if (_eyeCareView.DesiredSize.Height > 1) content = _eyeCareView.DesiredSize.Height;
            }
            double maxWidth;
            double maxHeight;
            ScreenLimits(out maxWidth, out maxHeight);
            double chrome = Theme.IsMinimal ? 78 : HeaderHeight + 12 + 28 + 18;
            double needed = Math.Min(maxHeight, Math.Max(280, chrome + content));
            height = Theme.IsMinimal ? needed : Math.Max(height, needed);
            if (Theme.IsMinimal) width = 380;
            ScrollViewer scroll = _eyeCareCard == null ? null : _eyeCareCard.Child as ScrollViewer;
            if (scroll != null) scroll.MaxHeight = Math.Max(160, height - chrome);
        }

        private void RunSearch()
        {
            if (_everything == null || _search == null) return;
            string query = _search.Text.Trim();
            if (query.Length > 0) { _activeQuery = query; _everything.Search(query); }
        }

        private void RenderResults()
        {
            if (_resultList == null) return;
            _resultList.Children.Clear();
            _eyeCareAction = _search != null && IsEyeCareQuery(_search.Text);
            if (_eyeCareAction && IsExactEyeCareQuery(_search.Text)) _selectedRow = 0;
            if (_eyeCareAction) _resultList.Children.Add(EyeCareActionRow());
            List<string> paths = _search == null || _search.Text.Trim().Length == 0
                ? _manualFiles.ToList()
                : _results.ToList();
            int offset = _eyeCareAction ? 1 : 0;
            for (int i = 0; i < paths.Count; i++) _resultList.Children.Add(FileRow(paths[i], i + offset));
            if (_empty != null) _empty.Visibility = (paths.Count == 0 && !_eyeCareAction) ? Visibility.Visible : Visibility.Collapsed;
            HighlightRows();
        }

        private static bool IsEyeCareQuery(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] tokens = text.Trim().ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (token == "descanso" || token == "ojo" || token == "ojos" || token == "pausa" || token == "reloj")
                    return true;
            }
            return false;
        }

        private static bool IsExactEyeCareQuery(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string token = text.Trim().ToLowerInvariant();
            return token == "descanso" || token == "ojo" || token == "ojos" || token == "pausa" || token == "reloj";
        }

        private Border EyeCareActionRow()
        {
            Border row = new Border
            {
                Background = Theme.TileSurface,
                BorderBrush = Theme.TileLine,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(4, 2, 4, 4),
                Cursor = Cursors.Hand
            };
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            System.Windows.Shapes.Path icon = Icons.Make("eye", 18, Theme.Ink);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(icon);
            StackPanel sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(Theme.Text("Abrir Descanso Visual", 12, Theme.Ink, FontWeights.SemiBold));
            sp.Children.Add(Theme.Text("Regla 20-20-20, Pausas activas y relojes", 10.5, Theme.Muted));
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            row.Child = g;
            row.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                if (_search != null) _search.Text = "";
                OpenEyeCare();
            };
            return row;
        }

        private Border FileRow(string path, int index)
        {
            Grid grid = new Grid { Margin = new Thickness(12, 0, 8, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            StackPanel labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock name = Theme.Text(Path.GetFileName(path), 12, Theme.Ink, FontWeights.Medium);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            labels.Children.Add(name);
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                TextBlock location = Theme.Text(folder, 11, Theme.Muted);
                location.TextTrimming = TextTrimming.CharacterEllipsis;
                location.Margin = new Thickness(0, 1, 0, 0);
                labels.Children.Add(location);
            }
            grid.Children.Add(labels);
            if (_picked == null)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                StackPanel people = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                foreach (Peer peer in _peers)
                {
                    Peer target = peer;
                    Button person = Avatar(target, 28, () => BeginSend(path, target));
                    person.ToolTip = "Enviar a " + target.Name + (target.Address == null ? "" : " · " + target.Address);
                    people.Children.Add(person);
                }
                ScrollViewer peopleScroll = new ScrollViewer
                {
                    Content = people,
                    MaxWidth = 180,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
                };
                Grid.SetColumn(peopleScroll, 1);
                grid.Children.Add(peopleScroll);
            }
            Border row = new Border
            {
                Child = grid,
                Height = _picked == null ? 44 : 48,
                Background = index == _selectedRow ? Theme.SoftSurface : Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = _picked == null ? Cursors.Arrow : Cursors.Hand,
                ToolTip = _picked == null ? null : "Enviar a " + _picked.Name
            };
            row.MouseLeftButtonDown += (s, e) => { _selectedRow = index; HighlightRows(); };
            if (_picked != null)
                row.MouseLeftButtonUp += (s, e) => BeginSend(path, _picked);
            row.MouseEnter += (s, e) => { if (index != _selectedRow) row.Background = Theme.SoftSurface; };
            row.MouseLeave += (s, e) => { if (index != _selectedRow) row.Background = Brushes.Transparent; };
            return row;
        }

        private static FrameworkElement PhotoContent(string photo, string name, double size)
        {
            if (!string.IsNullOrEmpty(photo))
            {
                try
                {
                    byte[] bytes = Convert.FromBase64String(photo);
                    if (bytes.Length > 12000) throw new InvalidDataException();
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    using (MemoryStream stream = new MemoryStream(bytes))
                    {
                        bitmap.BeginInit();
                        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = 96;
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                        bitmap.Freeze();
                    }
                    return new System.Windows.Shapes.Ellipse { Width = size, Height = size, Fill = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill } };
                }
                catch { /* An invalid peer photo falls back to initials. */ }
            }
            return new TextBlock { Text = Initials(name), FontFamily = Theme.Font, FontSize = Math.Max(7, size * .28),
                FontWeight = FontWeights.SemiBold, Foreground = Theme.Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        private Button Avatar(Peer peer, double size, Action click)
        {
            Button button = new Button
            {
                Content = PhotoContent(peer.Photo, peer.Name, size - 2),
                Width = size,
                Height = size,
                Margin = new Thickness(3, 0, 0, 0),
                FontFamily = Theme.Font,
                FontSize = Math.Max(8, size * 0.34),
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Ink,
                Background = Theme.AvatarSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                Cursor = click == null ? Cursors.Arrow : Cursors.Hand
            };
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory ellipse = new FrameworkElementFactory(typeof(Border));
            ellipse.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2));
            ellipse.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            ellipse.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            ellipse.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            ellipse.AppendChild(ButtonPresenter());
            template.VisualTree = ellipse;
            if (click != null)
            {
                Trigger hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
                hover.Setters.Add(new Setter(Button.BackgroundProperty, Theme.AvatarHover));
                template.Triggers.Add(hover);
                if (click != null) button.Click += (s, e) => { e.Handled = true; click(); };
            }
            button.Template = template;
            return button;
        }

        private const double TileInset = 0;
        private const double AvatarSize = 64;
        private const double HaloSize = 82;

        private void MeasureDevices()
        {
            _tileWidth = 96;
            _nameLines = 1;
            List<Peer> visible = VisiblePeers();
            foreach (Peer peer in visible)
            {
                double width = NameWidth(peer.Name);
                if (width + 8 > _tileWidth) _tileWidth = Math.Min(132, width + 8);
            }
            double lineWidth = _tileWidth - 4;
            foreach (Peer peer in visible)
            {
                int lines = (int)Math.Ceiling(NameWidth(peer.Name) / lineWidth);
                if (lines > _nameLines) _nameLines = Math.Min(2, lines);
            }
        }

        private double TileHeight()
        {
            return HaloSize + 4 + _nameLines * 16 + 6;
        }

        private double NameWidth(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            double dpi = 1;
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
                dpi = source.CompositionTarget.TransformToDevice.M11;
            FormattedText text = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(Theme.Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                12.5, Brushes.Black, dpi);
            return text.Width;
        }

        /// <summary>
        /// Persona o equipo: solo el círculo con su foto y su nombre, sin recuadro.
        /// Al pasar el cursor o arrastrar un archivo aparece un halo, como el indicador de voz de una videollamada.
        /// </summary>
        private Button DeviceTile(Peer peer)
        {
            Grid avatar = new Grid { Width = HaloSize, Height = HaloSize, HorizontalAlignment = HorizontalAlignment.Center };
            System.Windows.Shapes.Ellipse halo = new System.Windows.Shapes.Ellipse
            {
                Fill = Theme.NavActive,
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.8, 0.8),
                IsHitTestVisible = false
            };
            avatar.Children.Add(halo);
            Border circle = new Border
            {
                Width = AvatarSize,
                Height = AvatarSize,
                CornerRadius = new CornerRadius(AvatarSize / 2),
                Background = Theme.AvatarSurface,
                BorderBrush = Theme.Primary,
                BorderThickness = new Thickness(2),
                Child = PhotoContent(peer.Photo, peer.Name, AvatarSize - 4)
            };
            avatar.Children.Add(circle);
            Border online = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = Theme.Online,
                BorderBrush = Theme.Color(Theme.P.Shell1),
                BorderThickness = new Thickness(2.5),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 10, 10),
                ToolTip = "Disponible",
                IsHitTestVisible = false
            };
            avatar.Children.Add(online);
            TextBlock name = Theme.Text(peer.Name, 12.5, Theme.Ink, FontWeights.SemiBold);
            name.TextAlignment = TextAlignment.Center;
            name.TextWrapping = TextWrapping.Wrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxHeight = 34;
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Width = _tileWidth;
            name.Margin = new Thickness(0, 2, 0, 0);
            StackPanel stack = new StackPanel { Width = _tileWidth, Height = TileHeight(), Background = Brushes.Transparent };
            stack.Children.Add(avatar);
            stack.Children.Add(name);
            ScaleTransform haloScale = (ScaleTransform)halo.RenderTransform;
            Action<bool> glow = on =>
            {
                halo.BeginAnimation(OpacityProperty, Theme.Animation(halo.Opacity, on ? 1 : 0, on ? 200 : 320));
                haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, Theme.Animation(haloScale.ScaleX, on ? 1 : 0.8, 320));
                haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, Theme.Animation(haloScale.ScaleY, on ? 1 : 0.8, 320));
            };
            Button button = new Button
            {
                Content = stack,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Margin = new Thickness(4),
                ToolTip = peer.Name + (peer.Address == null ? "" : " · " + peer.Address)
            };
            System.Windows.Automation.AutomationProperties.SetName(button, peer.Name);
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = ButtonPresenter() };
            button.AllowDrop = true;
            button.MouseEnter += (s, e) => glow(true);
            button.MouseLeave += (s, e) => { if (!button.IsMouseOver) glow(false); };
            DragEventHandler drag = (s, e) =>
            {
                bool file = HasFiles(e.Data);
                e.Effects = file ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
                glow(file);
            };
            button.PreviewDragOver += drag;
            button.DragOver += drag;
            button.DragLeave += (s, e) => glow(false);
            button.Drop += (s, e) =>
            {
                glow(false);
                e.Handled = true;
                SendFiles(ExistingFiles(e.Data.GetData(DataFormats.FileDrop) as string[]), peer);
            };
            button.Click += (s, e) => PickAndSend(peer);
            return button;
        }

        private static void DeviceGrid(int count, out int columns, out int rows)
        {
            columns = Math.Max(1, Math.Min(count, 5));
            rows = Math.Max(1, (count + columns - 1) / columns);
        }

        private void PickAndSend(Peer peer)
        {
            if (_preview || peer == null) return;
            if (_manualFiles.Count > 0)
            {
                string[] pending = _manualFiles.ToArray();
                _manualFiles.Clear();
                UpdateTagline();
                SendFiles(pending, peer);
                return;
            }
            OpenFileDialog dialog = new OpenFileDialog { Title = "Archivos para " + peer.Name, Multiselect = true };
            bool topmost = Topmost;
            _holding = true;
            Topmost = false;
            bool? chosen = dialog.ShowDialog(this);
            Topmost = topmost;
            _holding = false;
            if (chosen == true) SendFiles(dialog.FileNames, peer);
        }

        private Button OwnAvatar()
        {
            return OwnAvatar(24);
        }

        private Button OwnAvatar(double size)
        {
            _ownAvatarSize = size;
            _ownAvatar = Avatar(new Peer { Name = Identity.Current, Photo = ProfilePhoto.Current }, size,
                () => { _settingsOpen = true; SyncChrome(); });
            _ownAvatar.ToolTip = "Tu perfil · " + Identity.Current;
            System.Windows.Automation.AutomationProperties.SetName(_ownAvatar, "Tu perfil");
            return _ownAvatar;
        }

        private void RefreshOwnAvatar()
        {
            if (_ownAvatar == null) return;
            _ownAvatar.Content = PhotoContent(ProfilePhoto.Current, Identity.Current, _ownAvatarSize - 2);
            _ownAvatar.ToolTip = "Tu perfil · " + Identity.Current;
        }

        private Border Presence(Peer peer)
        {
            Border dot = new Border
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 4, 0),
                CornerRadius = new CornerRadius(11),
                Background = Theme.AvatarSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                ToolTip = "Soltar en " + peer.Name,
                AllowDrop = true,
                Child = PhotoContent(peer.Photo, peer.Name, 20)
            };
            dot.DragOver += (s, e) =>
            {
                e.Effects = HasFiles(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
                dot.Background = HasFiles(e.Data) ? Theme.AvatarHover : Theme.AvatarSurface;
            };
            dot.DragLeave += (s, e) => dot.Background = Theme.AvatarSurface;
            dot.Drop += (s, e) =>
            {
                dot.Background = Theme.AvatarSurface;
                e.Handled = true;
                SendFiles(ExistingFiles(e.Data.GetData(DataFormats.FileDrop) as string[]), peer);
            };
            return dot;
        }

        private Border HistoryCard()
        {
            return StylePanel(new Border
            {
                Background = Theme.CardSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 8),
                Width = 280,
                MaxHeight = 220,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 40, 8, 0),
                Child = HistoryList()
            }, 300);
        }

        private void RefreshEyeCareCard()
        {
            if (_eyeCareOpen && _eyeCareView != null)
            {
                _eyeCareView.UpdateUi();
            }
        }

        private Border EyeCareCard()
        {
            _eyeCareView = new EyeCareCardView();
            _eyeCareView.ContentChanged += () => { if (_eyeCareOpen) ApplySize(false); };
            ScrollViewer scroll = new ScrollViewer
            {
                Content = _eyeCareView,
                MaxHeight = Theme.IsMinimal ? 490 : double.PositiveInfinity,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            return StylePanel(new Border
            {
                Background = Theme.CardSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Width = 320,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 40, 8, 0),
                Child = scroll
            }, 300);
        }

        private UIElement HistoryList()
        {
            StackPanel items = new StackPanel();
            string[] rows = TransferLog.Recent();
            int shown = 0;
            foreach (string row in rows)
            {
                string[] parts = row.Split('|');
                if (parts.Length < 4) continue;
                items.Children.Add(HistoryRow(parts));
                shown++;
            }
            if (shown == 0)
            {
                TextBlock empty = Theme.Text("Sin transferencias", 13, Theme.Muted);
                empty.Margin = new Thickness(2, 12, 0, 4);
                items.Children.Add(empty);
            }
            ScrollViewer scroll = new ScrollViewer
            {
                Content = items,
                MaxHeight = Theme.IsMinimal ? 190 : double.PositiveInfinity,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid frame = new Grid();
            frame.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            frame.RowDefinitions.Add(new RowDefinition());
            if (Theme.IsMinimal)
            {
                TextBlock title = Theme.Label("Historial");
                title.Margin = new Thickness(0, 0, 0, 6);
                frame.Children.Add(title);
            }
            else
            {
                Grid head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                head.ColumnDefinitions.Add(new ColumnDefinition());
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                TextBlock title = Theme.Text("Historial", 16, Theme.Ink, FontWeights.SemiBold);
                title.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(title);
                Button closePanel = IconButton("close", () => TogglePanel("history"));
                closePanel.ToolTip = "Cerrar";
                Grid.SetColumn(closePanel, 1);
                head.Children.Add(closePanel);
                frame.Children.Add(head);
            }
            Grid.SetRow(scroll, 1);
            frame.Children.Add(scroll);
            return frame;
        }

        private Border HistoryRow(string[] parts)
        {
            bool incoming = parts[1] == "in";
            string name = parts[3];
            string location = parts.Length > 4 ? parts[4] : "";
            DateTime when;
            string time = parts[0];
            if (DateTime.TryParse(parts[0], out when))
                time = when.Date == DateTime.Today ? when.ToString("HH:mm") : when.ToString("dd MMM · HH:mm", CultureInfo.CurrentUICulture);
            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            Border badge = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = Theme.AvatarSurface,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = Icons.Make(incoming ? "folder" : "upload", 14, Theme.Ink, 2)
            };
            grid.Children.Add(badge);
            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock title = Theme.Text(name, 13, Theme.Ink, FontWeights.SemiBold);
            title.TextWrapping = TextWrapping.NoWrap;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            TextBlock meta = Theme.Text((incoming ? "De " : "Para ") + parts[2] + " · " + time, 11, Theme.Muted);
            meta.TextWrapping = TextWrapping.NoWrap;
            meta.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(title);
            text.Children.Add(meta);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Border row = new Border
            {
                Child = grid,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                ToolTip = "Mostrar en carpeta"
            };
            row.MouseEnter += (s, e) => row.Background = Theme.TileHover;
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (s, e) => { e.Handled = true; RevealTransfer(location, name, incoming); };
            return row;
        }

        private void RevealTransfer(string location, string name, bool incoming)
        {
            string path = location;
            if (string.IsNullOrEmpty(path) && incoming)
                path = Path.Combine(NetworkEngine.DefaultReceiveDirectory(), name);
            try
            {
                string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                string arguments = null;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) arguments = "/select,\"" + path + "\"";
                else if (!string.IsNullOrEmpty(path) && Directory.Exists(Path.GetDirectoryName(path)))
                    arguments = "\"" + Path.GetDirectoryName(path) + "\"";
                if (arguments == null)
                {
                    ShowToast("El archivo ya no está en su ubicación.");
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = explorer,
                    Arguments = arguments,
                    UseShellExecute = true
                });
                if (!_preview) HideAnimated();
            }
            catch (Exception ex) { ShowToast(ex.Message); }
        }

        private void RefreshDots()
        {
            if (_dots == null) return;
            _dots.Children.Clear();
            foreach (Peer peer in _peers) _dots.Children.Add(Presence(peer));
        }

        private void RefreshDevices()
        {
            if (_devices == null) return;
            _devices.Children.Clear();
            MeasureDevices();
            List<Peer> visible = VisiblePeers();
            foreach (Peer peer in visible) _devices.Children.Add(DeviceTile(peer));
            if (_sectionCount != null) _sectionCount.Text = visible.Count == _peers.Count ? "" : visible.Count + " de " + _peers.Count;
            if (_heroChip != null) _heroChip.Text = OnlineText(_peers.Count);
            if (_deviceEmpty == null) return;
            bool empty = visible.Count == 0;
            _deviceEmpty.Text = _networkError != null ? "Red no disponible" : _peers.Count > 0 ? "Sin coincidencias" : "Sin equipos";
            _deviceEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            _devices.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        internal static string Initials(string name)
        {
            string[] words = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2) return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant();
            return name.Length >= 2 ? name.Substring(0, 2).ToUpperInvariant() : name.ToUpperInvariant();
        }

        private void HighlightRows()
        {
            if (_resultList == null) return;
            for (int i = 0; i < _resultList.Children.Count; i++)
                ((Border)_resultList.Children[i]).Background = i == _selectedRow ? Theme.SoftSurface : Brushes.Transparent;
        }

        private static FrameworkElementFactory ButtonPresenter()
        {
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            return content;
        }

        private static bool HasFiles(System.Windows.IDataObject data)
        {
            return ExistingFiles(data.GetData(DataFormats.FileDrop) as string[]).Length > 0;
        }

        private static string[] ExistingFiles(string[] paths)
        {
            if (paths == null) return new string[0];
            return paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private void ChooseFile()
        {
            OpenFileDialog dialog = new OpenFileDialog { Title = "Seleccionar archivos", Multiselect = true };
            _holding = true;
            bool? chosen = dialog.ShowDialog(this);
            _holding = false;
            if (chosen == true) SendFiles(dialog.FileNames, _picked);
        }

        private void SendFiles(string[] paths, Peer peer)
        {
            paths = ExistingFiles(paths);
            if (paths.Length == 0) { SetStatus("Selecciona archivos válidos."); return; }
            if (peer != null)
            {
                foreach (string path in paths) BeginSend(path, peer);
                return;
            }
            _manualFiles.Clear();
            _manualFiles.AddRange(paths);
            if (_search != null) _search.Text = "";
            _selectedRow = 0;
            if (_empty != null) _empty.Text = "";
            SetStatus(paths.Length == 1 ? "Selecciona un destinatario" : paths.Length + " archivos · elige destinatario");
            UpdateTagline();
            RenderResults();
            SyncChrome();
        }

        private string SelectedPath()
        {
            List<string> paths = _search == null || _search.Text.Trim().Length == 0
                ? _manualFiles.ToList()
                : _results.ToList();
            if (paths.Count == 0) return null;
            int index = _selectedRow - (_eyeCareAction ? 1 : 0);
            if (index < 0 || index >= paths.Count) return null;
            return paths[index];
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double number = bytes;
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;
            while (number >= 1024 && unit < units.Length - 1) { number /= 1024; unit++; }
            return number.ToString(number < 10 ? "0.0" : "0") + " " + units[unit];
        }

        private void OpenChat()
        {
            if (_preview) return;
            if (!IsVisible) ShowAnimated();
            _chatOpen = true;
            _settingsOpen = false;
            _historyOpen = false;
            _eyeCareOpen = false;
            _shareOpen = false;
            SyncChrome();
        }

        private void CloseChat()
        {
            if (!_chatOpen) return;
            _chatOpen = false;
            SyncChrome();
        }

        private Border ChatCard()
        {
            _chatPanel = new ChatPanel(_network, RefreshChatDot, CloseChat, Shake);
            _chatPanel.SendFiles += SendFilesFromChat;
            _chatPanel.ShareScreen += ShareWith;
            _chatPanel.SharingWith = id => _share != null && _shareInvited.Contains(id);
            return Theme.Glass(new Border
            {
                Background = Theme.OverlaySurface,
                CornerRadius = new CornerRadius(24),
                Margin = Theme.IsMinimal ? new Thickness(8, 40, 8, 8) : new Thickness(SidebarWidth + 10, HeaderHeight - 6, 12, 12),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Child = _chatPanel
            });
        }

        private void SendFilesFromChat(Peer peer, string[] paths)
        {
            if (peer == null || paths == null) return;
            SendFiles(paths, peer);
        }

        private void Shake()
        {
            if (!IsVisible) ShowAnimated();
            if (_shaking) return;
            _shaking = true;
            double origin = Left;
            int step = 0;
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(28) };
            timer.Tick += (s, e) =>
            {
                step++;
                Left = origin + (step % 2 == 0 ? -10 : 10);
                if (step >= 8)
                {
                    timer.Stop();
                    Left = origin;
                    _shaking = false;
                }
            };
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            timer.Start();
        }

        private void RefreshChatDot()
        {
            int unread = ChatStore.UnreadTotal();
            if (_chatDot != null) _chatDot.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_tray != null)
            {
                string label = unread > 0 ? "Lazo · " + unread + (unread == 1 ? " mensaje" : " mensajes") : "Lazo · transferencia local";
                if (label.Length > 63) label = label.Substring(0, 63);
                try { _tray.Text = label; } catch { }
            }
        }

        private void OnChatReceived(Guid id, string name, string text)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                ChatStore.Append(id, name, false, text);
                if (_chatPanel != null) _chatPanel.Incoming(id, name, text);
                if (_chatPanel == null || !_chatPanel.Viewing(id)) NotifyChat(name, text);
                RefreshChatDot();
            }));
        }

        private void OnChatSignal(Guid id, string name, byte kind, string body)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                if (kind == NetworkEngine.ChatKindNudge)
                {
                    ChatStore.AppendNudge(id, name, false);
                    if (_chatPanel != null) _chatPanel.Incoming(id, name, "Zumbido");
                    Shake();
                    if (_chatPanel == null || !_chatPanel.Viewing(id)) NotifyChat(name, "Zumbido");
                    RefreshChatDot();
                    return;
                }
                if (_chatPanel != null) _chatPanel.NoteSignal(id, name, kind, body);
            }));
        }

        private void NotifyChat(string name, string text)
        {
            RefreshChatDot();
            if (!ChatPrefs.UseBalloon || _tray == null) return;
            try { _tray.ShowBalloonTip(4000, name, text, System.Windows.Forms.ToolTipIcon.Info); }
            catch { }
        }

        private void OnPeersChanged(List<Peer> peers)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                _peers = peers;
                RefreshDots();
                RefreshDevices();
                RenderResults();
                if (!Theme.IsMinimal && _picked == null) ApplySize(false);
            }));
        }

        private async void BeginSend(string path, Peer peer)
        {
            await SendToAsync(path, peer);
        }

        private async Task SendToAsync(string path, Peer peer)
        {
            if (_preview || peer == null) return;
            if (!File.Exists(path)) { SetStatus("El archivo ya no existe."); return; }
            if (_activeSends == 0) _sendFailure = null;
            string key = Interlocked.Increment(ref _sendSerial) + "|" + path;
            lock (_sendProgress) _sendProgress[key] = 0;
            Interlocked.Increment(ref _activeSends);
            UpdateSendStatus(Path.GetFileName(path), 0);
            try
            {
                await Task.Run(() => _network.SendAsync(peer, path, value => Dispatcher.BeginInvoke((Action)(() =>
                {
                    lock (_sendProgress) _sendProgress[key] = value;
                    UpdateSendStatus(Path.GetFileName(path), value);
                }))));
                UpdateSendStatus(Path.GetFileName(path), 1);
                TransferLog.Add("out", peer.Name, Path.GetFileName(path), path);
                ChatStore.AppendFile(peer.Id, peer.Name, true, path);
                if (_chatPanel != null) _chatPanel.RefreshOpen();
            }
            catch (Exception ex) { _sendFailure = ex.Message; SetStatus(ex.Message); }
            finally
            {
                lock (_sendProgress) _sendProgress.Remove(key);
                Interlocked.Decrement(ref _activeSends);
                if (_activeSends == 0) SetStatus(_sendFailure ?? "Entregado");
            }
        }

        private void SetStatus(string text)
        {
            if (_status != null) _status.Text = text;
            else ShowToast(text);
        }

        private void ShowToast(string text)
        {
            if (_toast == null || _toastText == null || string.IsNullOrEmpty(text)) return;
            _toastText.Text = text;
            _toast.Visibility = Visibility.Visible;
            if (_toastTimer == null)
            {
                _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); if (_toast != null) _toast.Visibility = Visibility.Collapsed; };
            }
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void UpdateSendStatus(string name, double value)
        {
            int count = _activeSends;
            if (count <= 1) SetStatus("Enviando " + (value * 100).ToString("0") + "% · " + name);
            else SetStatus("Enviando " + count + " archivos · " + name);
            if (_progressScale == null) return;
            double average = value;
            lock (_sendProgress) if (_sendProgress.Count > 0) average = _sendProgress.Values.Average();
            _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty, Theme.Animation(_progressScale.ScaleX, average, 90));
        }

        private Task<bool> OnOfferReceived(Offer offer)
        {
            TaskCompletionSource<bool> response = new TaskCompletionSource<bool>();
            Dispatcher.BeginInvoke((Action)(() =>
            {
                ReceiveWindow window = null;
                for (int i = 0; i < _inbox.Count; i++) if (_inbox[i].CanAbsorb(offer)) window = _inbox[i];
                if (window != null)
                {
                    window.Add(offer, accept => response.TrySetResult(accept));
                    StackInbox();
                    return;
                }
                window = new ReceiveWindow(offer, accept => response.TrySetResult(accept));
                window.Closed += (s, e) => { _inbox.Remove(window); StackInbox(); };
                _inbox.Add(window);
                window.Show();
                StackInbox();
            }));
            return response.Task;
        }

        private void StackInbox()
        {
            for (int i = 0; i < _inbox.Count; i++) WindowPlacement.PlaceBottomRight(_inbox[i], i);
        }

        private ReceiveWindow Inbox(Guid id)
        {
            for (int i = 0; i < _inbox.Count; i++) if (_inbox[i].Contains(id)) return _inbox[i];
            return null;
        }

        private void OnReceiveProgress(Guid id, double value)
        {
            Dispatcher.BeginInvoke((Action)(() => { ReceiveWindow window = Inbox(id); if (window != null) window.SetFileProgress(id, value); }));
        }

        private void OnReceiveFinished(Guid id, string message, string path)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                ReceiveWindow window = Inbox(id);
                if (window != null) window.FinishFile(id, message, path);
                if (!string.IsNullOrEmpty(path) && window != null)
                {
                    TransferLog.Add("in", window.PeerName, Path.GetFileName(path), path);
                    Peer peer = _peers.FirstOrDefault(item => item.Address != null && window.PeerAddress != null && item.Address.Equals(window.PeerAddress));
                    if (peer == null) peer = _peers.FirstOrDefault(item => string.Equals(item.Name, window.PeerName, StringComparison.OrdinalIgnoreCase));
                    if (peer != null)
                    {
                        ChatStore.AppendFile(peer.Id, peer.Name, false, path);
                        if (_chatPanel != null) _chatPanel.RefreshOpen();
                        if (_chatPanel == null || !_chatPanel.Viewing(peer.Id)) NotifyChat(peer.Name, "Archivo · " + Path.GetFileName(path));
                    }
                }
            }));
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_shareOpen)
                {
                    _shareOpen = false;
                    SyncChrome();
                    e.Handled = true;
                    return;
                }
                if (_chatOpen)
                {
                    _chatOpen = false;
                    SyncChrome();
                    e.Handled = true;
                    return;
                }
                if (_eyeCareOpen)
                {
            _eyeCareOpen = false;
            _chatOpen = false;
                    SyncChrome();
                    e.Handled = true;
                    return;
                }
                if (_historyOpen)
                {
                    _historyOpen = false;
                    SyncChrome();
                    e.Handled = true;
                    return;
                }
                if (_settingsOpen)
                {
                    _settingsOpen = false;
                    SyncChrome();
                    e.Handled = true;
                    return;
                }
                if (_peerFilterBox != null && _peerFilterBox.Text.Length > 0)
                {
                    _peerFilterBox.Text = "";
                    e.Handled = true;
                    return;
                }
                HideAnimated();
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                ChooseFile();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (_eyeCareAction && _selectedRow == 0)
                {
                    if (_search != null) _search.Text = "";
                    OpenEyeCare();
                    e.Handled = true;
                    return;
                }
                if (_picked != null)
                {
                    string path = SelectedPath();
                    if (path != null) BeginSend(path, _picked);
                    e.Handled = true;
                }
                else if (_peerFilterBox != null && _peerFilterBox.IsKeyboardFocused)
                {
                    List<Peer> match = VisiblePeers();
                    if (match.Count == 1) PickAndSend(match[0]);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Down && _resultList != null && _resultList.Children.Count > 0)
            {
                _selectedRow = Math.Min(_resultList.Children.Count - 1, _selectedRow + 1);
                HighlightRows();
                e.Handled = true;
            }
            else if (e.Key == Key.Up && _resultList != null && _resultList.Children.Count > 0)
            {
                _selectedRow = Math.Max(0, _selectedRow - 1);
                HighlightRows();
                e.Handled = true;
            }
        }

        private void SetupHotkeys()
        {
            _hotkeys = new Hotkeys(this, Toggle);
            if (!_hotkeys.DoubleAltAvailable && !_hotkeys.AlternativeAvailable) SetStatus("Abre Lazo desde la bandeja.");
            else if (!_hotkeys.DoubleAltAvailable) SetStatus("Ctrl+Alt+L para abrir Lazo.");
            else if (!_hotkeys.AlternativeAvailable) SetStatus("Doble Alt para abrir Lazo.");
        }

        private void Toggle() { if (IsVisible) HideAnimated(); else ShowAnimated(); }
        public void ActivateFromElsewhere()
        {
            if (IsVisible)
            {
                Activate();
                if (_search != null) _search.Focus();
                return;
            }
            ShowAnimated();
        }

        private void ShowAnimated()
        {
            if (_animating) return;
            _launcherScreen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            Topmost = true;
            ResetHome();
            _shownUtc = DateTime.UtcNow;
            Show();
            WindowPlacement.PlaceBottomCenter(this, _launcherScreen);
            WindowPlacement.TakeForeground(this);
            Activate();
            _motion.Enter(_shell, _liquid, EnableGlass);
            FocusSearch();
            Dispatcher.BeginInvoke((Action)FocusSearch, DispatcherPriority.Input);
        }

        private void FocusSearch()
        {
            TextBox target = _search ?? _peerFilterBox;
            if (target == null || !IsVisible) return;
            WindowPlacement.TakeForeground(this);
            Activate();
            target.Focus();
            Keyboard.Focus(target);
        }

        private void ResetHome()
        {
            _settingsOpen = false;
            _historyOpen = false;
            _eyeCareOpen = false;
            _chatOpen = false;
            _shareOpen = false;
            _picked = null;
            _manualFiles.Clear();
            _results.Clear();
            _selectedRow = 0;
            BuildUi();
            ApplySize(false);
        }

        private void HideAnimated()
        {
            if (_animating || !IsVisible) return;
            _animating = true;
            DisableGlass();
            _motion.Leave(_shell, _liquid, () =>
            {
                Hide();
                _animating = false;
                ResetHome();
            });
        }

        private void SetupTray()
        {
            _tray = new System.Windows.Forms.NotifyIcon();
            _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location);
            _tray.Text = "Lazo · transferencia local";
            _tray.Visible = true;
            _tray.DoubleClick += (s, e) => Dispatcher.BeginInvoke((Action)ShowAnimated);
            System.Windows.Forms.ContextMenuStrip menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Abrir Lazo", null, (s, e) => Dispatcher.BeginInvoke((Action)ShowAnimated));
            menu.Items.Add("Chat", null, (s, e) => Dispatcher.BeginInvoke((Action)OpenChat));
            menu.Items.Add("Descanso Visual", null, (s, e) => Dispatcher.BeginInvoke((Action)(() => { ShowAnimated(); OpenEyeCare(); })));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Micro-descanso ahora (20s)", null, (s, e) => Dispatcher.BeginInvoke((Action)(() => EyeCareService.Instance.TriggerMicroBreak())));
            menu.Items.Add("Pausa activa ahora (5m)", null, (s, e) => Dispatcher.BeginInvoke((Action)(() => EyeCareService.Instance.TriggerActivePause())));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Salir", null, (s, e) => Dispatcher.BeginInvoke((Action)(() => { _exiting = true; Close(); Application.Current.Shutdown(); })));
            _tray.ContextMenuStrip = menu;
        }

        private void OnShellMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_settingsOpen)
            {
                CloseSettings();
                return;
            }
            if (_historyOpen)
            {
                _historyOpen = false;
                SyncChrome();
                return;
            }
            if (_eyeCareOpen)
            {
                _eyeCareOpen = false;
                SyncChrome();
                return;
            }
            if (_chatOpen)
            {
                _chatOpen = false;
                SyncChrome();
                return;
            }
            if (_shareOpen)
            {
                _shareOpen = false;
                SyncChrome();
                return;
            }
            if (IsInteractive(e.OriginalSource as DependencyObject)) return;
            try { DragMove(); }
            catch { }
        }

        private static bool IsInteractive(DependencyObject source)
        {
            while (source != null)
            {
                if (source is Button || source is TextBox || source is System.Windows.Controls.Primitives.ScrollBar) return true;
                source = VisualTreeHelper.GetParent(source);
            }
            return false;
        }

        // ── Compartir pantalla ─────────────────────────────────────────────

        private ShareSource _shareSource;
        private readonly HashSet<Guid> _shareTargets = new HashSet<Guid>();
        private ShareBarWindow _shareBar;

        private Border ShareCard()
        {
            return StylePanel(new Border
            {
                Background = Theme.CardSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 8),
                Width = 340,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = Theme.IsMinimal ? VerticalAlignment.Stretch : VerticalAlignment.Top,
                Margin = Theme.IsMinimal ? new Thickness(8, 40, 8, 8) : new Thickness(0, 40, 8, 0),
                Child = ShareContent()
            }, 300);
        }

        private void RefreshShareCard()
        {
            if (_shareCard != null) _shareCard.Child = ShareContent();
            if (_picker != null && _picker.Children.Count > 0) ((Border)_picker.Children[0]).Child = PickerContent();
            if (_shareDot != null) _shareDot.Visibility = _share != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private UIElement ShareContent()
        {
            StackPanel panel = new StackPanel();
            Grid head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock title = Theme.Text("Compartir pantalla", 16, Theme.Ink, FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(title);
            Button close = IconButton("close", () => TogglePanel("share"));
            close.ToolTip = "Cerrar";
            Grid.SetColumn(close, 1);
            head.Children.Add(close);
            panel.Children.Add(head);

            if (_share != null)
            {
                BuildActiveShare(panel);
                return Scroll(panel);
            }

            panel.Children.Add(Theme.Label("Qué compartir"));
            List<ShareSource> screens = ScreenSources.Screens();
            if (_shareSource == null && screens.Count > 0) _shareSource = screens[0];
            WrapPanel screenRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (ShareSource screen in screens) screenRow.Children.Add(ScreenChoice(screen));
            panel.Children.Add(screenRow);

            TextBlock windowsLabel = Theme.Label("Ventanas");
            windowsLabel.Margin = new Thickness(0, 10, 0, 4);
            panel.Children.Add(windowsLabel);
            StackPanel windows = new StackPanel();
            List<ShareSource> list = ScreenSources.Windows();
            foreach (ShareSource window in list) windows.Children.Add(WindowChoice(window));
            if (list.Count == 0) windows.Children.Add(Theme.Text("No hay otras ventanas abiertas.", 11.5, Theme.Muted));
            panel.Children.Add(new ScrollViewer
            {
                Content = windows,
                MaxHeight = 124,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });

            TextBlock peopleLabel = Theme.Label("Con quién");
            peopleLabel.Margin = new Thickness(0, 12, 0, 6);
            panel.Children.Add(peopleLabel);
            _shareTargets.RemoveWhere(id => !_peers.Any(p => p.Id == id));
            if (_peers.Count == 0)
            {
                TextBlock none = Theme.Text(_preview ? "Los equipos en línea aparecen aquí." : "No hay equipos en línea.", 11.5, Theme.Muted);
                panel.Children.Add(none);
            }
            WrapPanel people = new WrapPanel();
            foreach (Peer peer in _peers) people.Children.Add(PeerChoice(peer));
            panel.Children.Add(people);

            Button start = Theme.Button("Compartir", true);
            start.Margin = new Thickness(0, 14, 0, 0);
            start.HorizontalAlignment = HorizontalAlignment.Stretch;
            start.IsEnabled = _shareSource != null && _shareTargets.Count > 0 && !_preview;
            start.Click += (s, e) => StartShare();
            panel.Children.Add(start);
            TextBlock note = Theme.Text("Solo se ve lo que elijas. Cada persona acepta la invitación antes de ver nada.", 11, Theme.Muted);
            note.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(note);
            return Scroll(panel);
        }

        private static ScrollViewer Scroll(UIElement content)
        {
            FrameworkElement element = content as FrameworkElement;
            if (element != null) element.Margin = new Thickness(0, 0, 12, 0);
            return new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
        }

        private void BuildActiveShare(StackPanel panel)
        {
            Border live = new Border
            {
                Background = Theme.HeroSurface(),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14, 12, 14, 12)
            };
            StackPanel words = new StackPanel();
            StackPanel line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0) });
            line.Children.Add(Theme.Text("En vivo", 11.5, Brushes.White, FontWeights.SemiBold));
            words.Children.Add(line);
            TextBlock what = Theme.Text(_share.Source.Label, 17, Brushes.White, FontWeights.SemiBold);
            what.TextTrimming = TextTrimming.CharacterEllipsis;
            what.TextWrapping = TextWrapping.NoWrap;
            what.Margin = new Thickness(0, 4, 0, 0);
            words.Children.Add(what);
            words.Children.Add(Theme.Text(ShareSummary(), 11.5, Theme.Color("#E6FFFFFF")));
            live.Child = words;
            panel.Children.Add(live);

            TextBlock invite = Theme.Label("Invitar a más personas");
            invite.Margin = new Thickness(0, 14, 0, 6);
            panel.Children.Add(invite);
            string[] watching = _share.ViewerNames();
            WrapPanel people = new WrapPanel();
            foreach (Peer peer in _peers)
            {
                Peer target = peer;
                bool already = watching.Contains(peer.Name);
                Button chip = Chip(peer, already, () => { if (_share != null) { _share.Invite(target); ShowToast("Invitación enviada a " + target.Name); } });
                chip.IsEnabled = !already;
                people.Children.Add(chip);
            }
            panel.Children.Add(people);
            Button stop = Theme.Button("Dejar de compartir", true);
            stop.Background = Theme.Danger;
            stop.BorderBrush = Theme.Danger;
            stop.Foreground = Brushes.White;
            stop.Margin = new Thickness(0, 14, 0, 0);
            stop.Click += (s, e) => StopShare();
            panel.Children.Add(stop);
        }

        private string ShareSummary()
        {
            if (_share == null) return "";
            string[] names = _share.ViewerNames();
            string who = names.Length == 0 ? (_share.Pending > 0 ? "Esperando respuesta…" : "Nadie está viendo")
                       : names.Length == 1 ? "Lo ve " + names[0] : "Lo ven " + names.Length + " personas";
            if (names.Length == 0) return who;
            return who + " · " + Math.Round(_share.Fps) + " fps · " + _share.Mbps.ToString("0.0") + " Mb/s";
        }

        private Button ScreenChoice(ShareSource screen)
        {
            bool selected = _shareSource != null && !_shareSource.IsWindow && _shareSource.Handle == screen.Handle;
            StackPanel stack = new StackPanel();
            Border frame = new Border
            {
                Width = 132,
                Height = 74,
                CornerRadius = new CornerRadius(10),
                Background = Theme.SoftSurface,
                BorderBrush = selected ? Theme.SelectionFill : Brushes.Transparent,
                BorderThickness = new Thickness(2),
                ClipToBounds = true
            };
            System.Windows.Media.Imaging.BitmapSource thumb = ScreenSources.Thumbnail(screen, 240);
            if (thumb != null) frame.Child = new Image { Source = thumb, Stretch = Stretch.UniformToFill };
            stack.Children.Add(frame);
            TextBlock label = Theme.Text(screen.Label, 12, Theme.Ink, FontWeights.SemiBold);
            label.Margin = new Thickness(2, 5, 0, 0);
            stack.Children.Add(label);
            TextBlock detail = Theme.Text(screen.Detail, 10.5, Theme.Muted);
            detail.Margin = new Thickness(2, 0, 0, 0);
            stack.Children.Add(detail);
            Button button = Plain(stack, () => { _shareSource = screen; RefreshShareCard(); });
            button.Margin = new Thickness(0, 0, 10, 6);
            return button;
        }

        private Button WindowChoice(ShareSource window)
        {
            bool selected = _shareSource != null && _shareSource.IsWindow && _shareSource.Handle == window.Handle;
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            { System.Windows.Shapes.Path windowIcon = Icons.Make("window", 16, selected ? Theme.SelectionText : Theme.Muted); windowIcon.Margin = new Thickness(10, 0, 10, 0); row.Children.Add(windowIcon); }
            StackPanel words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock name = Theme.Text(window.Label, 12, selected ? Theme.SelectionText : Theme.Ink, FontWeights.Medium);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextWrapping = TextWrapping.NoWrap;
            words.Children.Add(name);
            TextBlock detail = Theme.Text(window.Detail, 10.5, selected ? Theme.SelectionText : Theme.Muted);
            detail.TextWrapping = TextWrapping.NoWrap;
            words.Children.Add(detail);
            Grid.SetColumn(words, 1);
            row.Children.Add(words);
            Border face = new Border
            {
                Height = 40,
                CornerRadius = new CornerRadius(10),
                Background = selected ? Theme.SelectionFill : Brushes.Transparent,
                Child = row,
                Margin = new Thickness(0, 1, 0, 1)
            };
            Button button = Plain(face, () => { _shareSource = window; RefreshShareCard(); });
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            if (!selected)
            {
                button.MouseEnter += (s, e) => face.Background = Theme.SoftSurface;
                button.MouseLeave += (s, e) => face.Background = Brushes.Transparent;
            }
            return button;
        }

        private Button PeerChoice(Peer peer)
        {
            Peer target = peer;
            return Chip(peer, _shareTargets.Contains(peer.Id), () =>
            {
                if (!_shareTargets.Remove(target.Id)) _shareTargets.Add(target.Id);
                RefreshShareCard();
            });
        }

        private Button Chip(Peer peer, bool selected, Action click)
        {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
            Border face = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = Theme.AvatarSurface,
                Child = PhotoContent(peer.Photo, peer.Name, 24)
            };
            row.Children.Add(face);
            TextBlock name = Theme.Text(peer.Name, 12, selected ? Theme.SegmentActiveText : Theme.Ink, FontWeights.SemiBold);
            name.TextWrapping = TextWrapping.NoWrap;
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(7, 0, 4, 0);
            name.MaxWidth = 150;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(name);
            if (selected)
                { System.Windows.Shapes.Path tick = Icons.Make("check", 13, Theme.SegmentActiveText, 2.4); tick.Margin = new Thickness(2, 0, 2, 0); row.Children.Add(tick); }
            Border pill = new Border
            {
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(4, 4, 10, 4),
                Background = selected ? Theme.SegmentActive : Theme.SoftSurface,
                Child = row
            };
            Button button = Plain(pill, click);
            button.Margin = new Thickness(0, 0, 6, 6);
            button.ToolTip = peer.Name + (peer.Address == null ? "" : " · " + peer.Address);
            return button;
        }

        private Button Plain(UIElement content, Action click)
        {
            Button button = new Button { Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Padding = new Thickness(0) };
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            ControlTemplate template = new ControlTemplate(typeof(Button)) { VisualTree = presenter };
            Trigger disabled = new Trigger { Property = Button.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Button.OpacityProperty, 0.5));
            template.Triggers.Add(disabled);
            button.Template = template;
            button.Click += (s, e) => { e.Handled = true; click(); };
            return button;
        }

        private void StartShare()
        {
            StartShare(_peers.Where(p => _shareTargets.Contains(p.Id)).ToList());
        }

        private readonly HashSet<Guid> _shareInvited = new HashSet<Guid>();

        private void StartShare(List<Peer> targets)
        {
            if (_share != null || _shareSource == null || _preview || targets.Count == 0) return;
            ScreenShareSession session = new ScreenShareSession(_network, _shareSource);
            session.Notice += text => Dispatcher.BeginInvoke((Action)(() => ShowToast(text)));
            session.Changed += () => Dispatcher.BeginInvoke((Action)(() => { if (_share == session) UpdateShareStatus(); }));
            session.Ended += () => Dispatcher.BeginInvoke((Action)(() => OnShareEnded(session)));
            _share = session;
            _shareInvited.Clear();
            foreach (Peer peer in targets) { session.Invite(peer); _shareInvited.Add(peer.Id); }
            session.Start();
            _shareBar = new ShareBarWindow(StopShare);
            _shareBar.Show();
            UpdateShareStatus();
            RefreshShareCard();
            if (_chatPanel != null) _chatPanel.RefreshOpen();
            ShowToast(targets.Count == 1 ? "Invitación enviada a " + targets[0].Name : "Invitaciones enviadas");
        }

        /// <summary>Desde el chat: invita a esa persona a la transmisión en curso, la detiene o elige qué compartir.</summary>
        private void ShareWith(Peer peer)
        {
            if (_share != null)
            {
                if (_shareInvited.Contains(peer.Id)) { StopShare(); return; }
                _share.Invite(peer);
                _shareInvited.Add(peer.Id);
                ShowToast("Invitación enviada a " + peer.Name);
                if (_chatPanel != null) _chatPanel.RefreshOpen();
                return;
            }
            OpenPicker(peer);
        }

        private Grid _picker;
        private Peer _pickerPeer;

        /// <summary>Selector de pantalla o ventana sobre el chat, como al «transmitir» en Discord.</summary>
        private void OpenPicker(Peer peer)
        {
            _pickerPeer = peer;
            ClosePicker();
            Grid overlay = new Grid { Background = Theme.Color("#66000000") };
            overlay.MouseLeftButtonDown += (s, e) => { if (e.OriginalSource == overlay) ClosePicker(); e.Handled = true; };
            Border card = Theme.Glass(new Border
            {
                Background = Theme.Color(Theme.P.Dark ? "#F2" + Theme.P.Shell1.Substring(1) : "#F7" + Theme.P.Shell0.Substring(1)),
                CornerRadius = new CornerRadius(24),
                Padding = new Thickness(18, 16, 18, 16),
                Width = 470,
                MaxHeight = 520,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
            card.Child = PickerContent();
            overlay.Children.Add(card);
            Panel.SetZIndex(overlay, 20);
            _picker = overlay;
            Grid root = (Grid)_shell.Child;
            root.Children.Add(overlay);
            overlay.Opacity = 0;
            overlay.BeginAnimation(OpacityProperty, Theme.Animation(0, 1, 200));
            ScaleTransform pop = new ScaleTransform(0.96, 0.96);
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            card.RenderTransform = pop;
            pop.BeginAnimation(ScaleTransform.ScaleXProperty, Theme.Animation(0.96, 1, 280));
            pop.BeginAnimation(ScaleTransform.ScaleYProperty, Theme.Animation(0.96, 1, 280));
        }

        private UIElement PickerContent()
        {
            StackPanel panel = new StackPanel();
            Grid head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock title = Theme.Text("Compartir pantalla con " + (_pickerPeer == null ? "" : _pickerPeer.Name), 16, Theme.Ink, FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.TextWrapping = TextWrapping.NoWrap;
            title.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(title);
            Button close = IconButton("close", ClosePicker);
            Grid.SetColumn(close, 1);
            head.Children.Add(close);
            panel.Children.Add(head);
            panel.Children.Add(Theme.Label("Pantallas"));
            List<ShareSource> screens = ScreenSources.Screens();
            if (_shareSource == null && screens.Count > 0) _shareSource = screens[0];
            WrapPanel screenRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (ShareSource screen in screens) screenRow.Children.Add(ScreenChoice(screen));
            panel.Children.Add(screenRow);
            TextBlock windowsLabel = Theme.Label("Ventanas");
            windowsLabel.Margin = new Thickness(0, 8, 0, 4);
            panel.Children.Add(windowsLabel);
            StackPanel windows = new StackPanel();
            foreach (ShareSource window in ScreenSources.Windows()) windows.Children.Add(WindowChoice(window));
            panel.Children.Add(new ScrollViewer { Content = windows, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            Button cancel = Theme.Button("Cancelar", false);
            cancel.Click += (s, e) => ClosePicker();
            buttons.Children.Add(cancel);
            Button go = Theme.Button("Transmitir", true);
            go.Margin = new Thickness(8, 0, 0, 0);
            go.IsEnabled = _shareSource != null && _pickerPeer != null && !_preview;
            go.Click += (s, e) =>
            {
                Peer target = _pickerPeer;
                ClosePicker();
                if (target != null) StartShare(new List<Peer> { target });
            };
            buttons.Children.Add(go);
            panel.Children.Add(buttons);
            return panel;
        }

        private void ClosePicker()
        {
            if (_picker == null) return;
            Grid closing = _picker;
            _picker = null;
            DoubleAnimation fade = new DoubleAnimation(closing.Opacity, 0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (s, e) => { Panel parent = closing.Parent as Panel; if (parent != null) parent.Children.Remove(closing); };
            closing.BeginAnimation(OpacityProperty, fade);
        }

        private void UpdateShareStatus()
        {
            if (_share == null) return;
            string[] names = _share.ViewerNames();
            string text = "Compartiendo " + _share.Source.Label;
            if (text.Length > 46) text = text.Substring(0, 45) + "…";
            text += names.Length == 0 ? " · esperando…" : " · " + (names.Length == 1 ? names[0] : names.Length + " personas") + " · " + Math.Round(_share.Fps) + " fps";
            if (_shareBar != null) _shareBar.Update(text);
            if (_shareOpen) RefreshShareCard();
        }

        private void StopShare()
        {
            if (_share != null) _share.Stop();
        }

        private void OnShareEnded(ScreenShareSession session)
        {
            if (_share != session) return;
            _share = null;
            if (_shareBar != null) { _shareBar.Close(); _shareBar = null; }
            _shareInvited.Clear();
            RefreshShareCard();
            if (_chatPanel != null) _chatPanel.RefreshOpen();
            ShowToast("Dejaste de compartir la pantalla");
        }

        private Task<IScreenSink> OnScreenOffered(ScreenInvite invite)
        {
            TaskCompletionSource<IScreenSink> response = new TaskCompletionSource<IScreenSink>();
            Dispatcher.BeginInvoke((Action)(() =>
            {
                ScreenInviteWindow prompt = new ScreenInviteWindow(invite, accept =>
                {
                    if (!accept) { response.TrySetResult(null); return; }
                    ScreenViewerWindow window = new ScreenViewerWindow(invite.Sender, invite.Source);
                    window.Show();
                    window.Activate();
                    response.TrySetResult(new ScreenViewer(window));
                });
                prompt.Show();
            }));
            return response.Task;
        }

        private void Cleanup()
        {
            _motion.Cancel();
            _searchDelay.Stop();
            if (_everything != null) _everything.Dispose();
            if (_hotkeys != null) _hotkeys.Dispose();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            if (_share != null) _share.Stop();
            _network.Dispose();
        }
    }
}
