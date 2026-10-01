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
            MaxWidth = 900;
            MinHeight = 80;
            MaxHeight = 700;
            SizeChanged += (s, e) => WindowPlacement.PlaceBottomCenter(this, _launcherScreen);
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
            Loaded += (s, e) =>
            {
                _motion.Enter(_shell, _liquid);
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

        private void BuildUi()
        {
            _motion.Cancel();
            _ownAvatar = null;
            Grid outer = new Grid { Margin = new Thickness(9) };
            Content = outer;
            _shell = new Border
            {
                Background = Theme.ShellSurface(),
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Theme.IsGlass ? 22 : (Theme.IsMinimal ? 16 : 13)),
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
                e.Effects = HasFiles(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
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
                double radius = Theme.IsGlass ? 20 : Theme.IsMinimal ? 14 : 11;
                root.Clip = new RectangleGeometry(new Rect(1, 1,
                    Math.Max(0, root.ActualWidth - 2), Math.Max(0, root.ActualHeight - 2)), radius, radius);
            };
            Grid layout = new Grid();
            root.Children.Add(layout);
            if (Theme.IsMinimal) BuildMinimal(layout);
            else if (_picked == null) BuildDevices(layout);
            else BuildTargetSearch(layout);

            Grid overlay = new Grid();
            _settingsCard = SettingsCard();
            _settingsCard.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
            _settingsCard.MouseLeftButtonDown += (s, e) => e.Handled = true;
            overlay.Children.Add(_settingsCard);
            Button gear = IconButton("\uE713", () =>
            {
                _settingsOpen = !_settingsOpen;
                if (_settingsOpen) { _historyOpen = false; _eyeCareOpen = false; _chatOpen = false; }
                SyncChrome();
            }, true);
            gear.HorizontalAlignment = HorizontalAlignment.Right;
            gear.VerticalAlignment = VerticalAlignment.Top;
            gear.Margin = new Thickness(0, 6, 8, 0);
            overlay.Children.Add(gear);
            Button close = IconButton("×", HideAnimated, false);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.VerticalAlignment = VerticalAlignment.Top;
            close.Margin = new Thickness(0, 6, 40, 0);
            overlay.Children.Add(close);
            Button history = IconButton("\uE81C", () =>
            {
                _historyOpen = !_historyOpen;
                if (_historyOpen) { _settingsOpen = false; _eyeCareOpen = false; _chatOpen = false; }
                SyncChrome();
            }, true);
            history.HorizontalAlignment = HorizontalAlignment.Right;
            history.VerticalAlignment = VerticalAlignment.Top;
            history.Margin = new Thickness(0, 6, 72, 0);
            history.ToolTip = "Historial";
            Button chat = IconButton("\uE8F2", () =>
            {
                _chatOpen = !_chatOpen;
                if (_chatOpen) { _settingsOpen = false; _historyOpen = false; _eyeCareOpen = false; }
                SyncChrome();
            }, true);
            chat.HorizontalAlignment = HorizontalAlignment.Right;
            chat.VerticalAlignment = VerticalAlignment.Top;
            chat.Margin = new Thickness(0, 6, 104, 0);
            chat.ToolTip = "Chat";
            Panel.SetZIndex(chat, 2);
            overlay.Children.Add(chat);
            _chatDot = new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Background = Theme.Ink,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 104, 0),
                IsHitTestVisible = false,
                Visibility = ChatStore.UnreadTotal() > 0 ? Visibility.Visible : Visibility.Collapsed
            };
            Panel.SetZIndex(_chatDot, 3);
            overlay.Children.Add(_chatDot);
            Panel.SetZIndex(gear, 2);
            Panel.SetZIndex(close, 2);
            Panel.SetZIndex(history, 2);
            overlay.Children.Add(history);
            _historyCard = HistoryCard();
            _historyCard.Visibility = _historyOpen ? Visibility.Visible : Visibility.Collapsed;
            _historyCard.MouseLeftButtonDown += (s, e) => e.Handled = true;
            overlay.Children.Add(_historyCard);
            Button eyeCare = IconButton("\uE7B3", () =>
            {
                _eyeCareOpen = !_eyeCareOpen;
                if (_eyeCareOpen) { _settingsOpen = false; _historyOpen = false; _chatOpen = false; }
                SyncChrome();
            }, true);
            eyeCare.HorizontalAlignment = HorizontalAlignment.Right;
            eyeCare.VerticalAlignment = VerticalAlignment.Top;
            eyeCare.Margin = new Thickness(0, 6, 136, 0);
            eyeCare.ToolTip = "Descanso Visual";
            Panel.SetZIndex(eyeCare, 2);
            overlay.Children.Add(eyeCare);
            _eyeCareCard = EyeCareCard();
            _eyeCareCard.Visibility = _eyeCareOpen ? Visibility.Visible : Visibility.Collapsed;
            _eyeCareCard.MouseLeftButtonDown += (s, e) => e.Handled = true;
            overlay.Children.Add(_eyeCareCard);
            _chatCard = ChatCard();
            _chatCard.Visibility = _chatOpen ? Visibility.Visible : Visibility.Collapsed;
            _chatCard.MouseLeftButtonDown += (s, e) => e.Handled = true;
            overlay.Children.Add(_chatCard);
            _progressScale = new ScaleTransform(0, 1);
            Border progress = new Border
            {
                Height = 2,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
                Child = new Border
                {
                    Background = Theme.Ink,
                    RenderTransform = _progressScale,
                    RenderTransformOrigin = new Point(0, 0.5)
                }
            };
            overlay.Children.Add(progress);
            root.Children.Add(overlay);
            RenderResults();
            RefreshDots();
            RefreshDevices();
        }

        private void BuildMinimal(Grid layout)
        {
            bool showResults = HasResults();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            _resultsRow = new RowDefinition { Height = showResults ? new GridLength(1, GridUnitType.Star) : new GridLength(0) };
            layout.RowDefinitions.Add(_resultsRow);

            Grid heading = new Grid { Margin = new Thickness(16, 7, 172, 0) };
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            TextBlock wordmark = Theme.Text("L A Z O", 10, Theme.Muted, FontWeights.SemiBold);
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

        private void BuildDevices(Grid layout)
        {
            Button self = OwnAvatar();
            self.Margin = new Thickness(14, 5, 0, 0);
            self.HorizontalAlignment = HorizontalAlignment.Left;
            self.VerticalAlignment = VerticalAlignment.Top;
            layout.Children.Add(self);
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _devices = new WrapPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 0, 8, 4)
            };
            Grid.SetRow(_devices, 1);
            layout.Children.Add(_devices);
            _deviceEmpty = Theme.Text("", 13, Theme.Muted);
            _deviceEmpty.HorizontalAlignment = HorizontalAlignment.Center;
            _deviceEmpty.VerticalAlignment = VerticalAlignment.Center;
            _deviceEmpty.TextAlignment = TextAlignment.Center;
            _deviceEmpty.Margin = new Thickness(32, 0, 32, 0);
            Grid.SetRow(_deviceEmpty, 1);
            layout.Children.Add(_deviceEmpty);
        }

        private void BuildTargetSearch(Grid layout)
        {
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });

            Grid header = new Grid { Margin = new Thickness(10, 0, 172, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            Button back = IconButton("\uE72B", BackToDevices, true);
            back.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(back);
            StackPanel target = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 10, 0)
            };
            target.Children.Add(Avatar(_picked, 28, null));
            TextBlock targetName = Theme.Text(_picked.Name, 12, Theme.Ink, FontWeights.SemiBold);
            targetName.VerticalAlignment = VerticalAlignment.Center;
            targetName.Margin = new Thickness(8, 0, 0, 0);
            targetName.MaxWidth = 120;
            targetName.TextTrimming = TextTrimming.CharacterEllipsis;
            target.Children.Add(targetName);
            Grid.SetColumn(target, 1);
            header.Children.Add(target);
            FrameworkElement search = SearchHost(true);
            search.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(search, 2);
            header.Children.Add(search);
            layout.Children.Add(header);

            Grid resultsArea = ResultsArea(true);
            Grid.SetRow(resultsArea, 1);
            layout.Children.Add(resultsArea);

            Grid footer = new Grid();
            Border footerRule = new Border { Height = 1, Background = Theme.Line, VerticalAlignment = VerticalAlignment.Top };
            footer.Children.Add(footerRule);
            _status = Theme.Text("Elige un archivo para " + _picked.Name, 11, Theme.Muted);
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(16, 0, 8, 0);
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            footer.Children.Add(_status);
            Grid.SetRow(footer, 2);
            layout.Children.Add(footer);
        }

        private FrameworkElement SearchHost(bool placeholder)
        {
            Grid host = new Grid();
            TextBox box = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Theme.Ink,
                FontFamily = Theme.Font,
                FontSize = 18,
                FontWeight = FontWeights.Normal,
                VerticalContentAlignment = VerticalAlignment.Center,
                CaretBrush = Theme.Ink
            };
            if (placeholder)
            {
                _placeholder = Theme.Text("Busca un archivo para enviarlo…", 18, Theme.Muted);
                _placeholder.FontWeight = FontWeights.Normal;
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
            panel.Children.Add(Theme.Text("Modo", 11, Theme.Muted));
            panel.Children.Add(Segment("Minimal", "Standard", Theme.IsMinimal,
                () => ChooseMode(InterfaceKind.Minimal), () => ChooseMode(InterfaceKind.Standard)));
            TextBlock look = Theme.Text("Apariencia", 11, Theme.Muted);
            look.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(look);
            panel.Children.Add(Choices(new[] { "Plano", "Vidrio", "Oscuro", "Cálido" },
                Theme.IsWarm ? 3 : Theme.IsDark ? 2 : Theme.IsGlass ? 1 : 0,
                index => ChooseAppearance(index == 3 ? ThemeKind.Warm : index == 2 ? ThemeKind.Dark : index == 1 ? ThemeKind.Glass : ThemeKind.Raycast)));
            TextBlock nameLabel = Theme.Text("Nombre visible", 11, Theme.Muted);
            nameLabel.Margin = new Thickness(0, 10, 0, 4);
            panel.Children.Add(nameLabel);
            TextBox nameBox = new TextBox
            {
                Text = Identity.Current,
                FontFamily = Theme.Font,
                FontSize = 13,
                Foreground = Theme.Ink,
                Background = Theme.SoftSurface,
                BorderBrush = Theme.Line,
                Padding = new Thickness(8, 4, 8, 4),
                CaretBrush = Theme.Ink
            };
            nameBox.LostFocus += (s, e) => { Identity.Set(nameBox.Text, !_preview); RefreshOwnAvatar(); };
            nameBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Identity.Set(nameBox.Text, !_preview); e.Handled = true; } };
            panel.Children.Add(nameBox);
            StackPanel photoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            ContentControl portrait = new ContentControl { Content = PhotoContent(ProfilePhoto.Current, Identity.Current, 52), Width = 52, Height = 52, ToolTip = "Tu foto de perfil" };
            photoRow.Children.Add(new Border { Width = 54, Height = 54, CornerRadius = new CornerRadius(27), Background = Theme.AvatarSurface, BorderBrush = Theme.Line, BorderThickness = new Thickness(1), Child = portrait });
            Button changePhoto = SegmentButton("Cambiar foto", true, null);
            changePhoto.Width = 100; changePhoto.Margin = new Thickness(10, 0, 6, 0); changePhoto.VerticalAlignment = VerticalAlignment.Center;
            Button clearPhoto = SegmentButton("Quitar", false, null);
            clearPhoto.Width = 58; clearPhoto.VerticalAlignment = VerticalAlignment.Center;
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
            photoRow.Children.Add(changePhoto);
            photoRow.Children.Add(clearPhoto);
            panel.Children.Add(photoRow);
            CheckBox startup = new CheckBox
            {
                Content = "Abrir al iniciar Windows",
                IsChecked = Startup.IsEnabled(),
                Foreground = Theme.Ink,
                FontFamily = Theme.Font,
                FontSize = 12,
                Margin = new Thickness(0, 12, 0, 2)
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
                Margin = new Thickness(0, 8, 0, 2)
            };
            updates.Checked += (s, e) => { Updater.Set(true, !_preview); if (!_preview) Updater.CheckInBackground(text => Dispatcher.BeginInvoke((Action)(() => SetStatus(text))), () => Dispatcher.BeginInvoke((Action)(() => { _exiting = true; Application.Current.Shutdown(); }))); };
            updates.Unchecked += (s, e) => Updater.Set(false, !_preview);
            panel.Children.Add(updates);
            TextBlock chatAlerts = Theme.Text("Avisos de chat", 11, Theme.Muted);
            chatAlerts.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(chatAlerts);
            panel.Children.Add(Segment("Bandeja", "Notificación", !ChatPrefs.UseBalloon,
                () => ChatPrefs.Set(false), () => ChatPrefs.Set(true)));
            Button checkUpdates = Theme.Button("Verificar actualizaciones", false);
            checkUpdates.HorizontalAlignment = HorizontalAlignment.Left;
            checkUpdates.Margin = new Thickness(0, 8, 0, 0);
            checkUpdates.MinHeight = 30;
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
            ScrollViewer scroll = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 470,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            return new Border
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
            };
        }

        private UIElement Segment(string left, string right, bool leftSelected, Action selectLeft, Action selectRight)
        {
            return Choices(new[] { left, right }, leftSelected ? 0 : 1, index => { if (index == 0) selectLeft(); else selectRight(); });
        }

        private UIElement Choices(string[] labels, int selected, Action<int> pick)
        {
            Grid grid = new Grid();
            for (int i = 0; i < labels.Length; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                int index = i;
                Button button = SegmentButton(labels[i], i == selected, () => pick(index));
                Grid.SetColumn(button, i);
                grid.Children.Add(button);
            }
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

        private Button IconButton(string glyph, Action click, bool symbol)
        {
            TextBlock icon = new TextBlock
            {
                Text = glyph,
                FontFamily = symbol ? new FontFamily("Segoe MDL2 Assets") : Theme.Font,
                FontSize = symbol ? 14 : 18,
                Foreground = Theme.Ink,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Button button = new Button
            {
                Content = icon,
                Width = 28,
                Height = 28,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(Border));
            frame.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            frame.Name = "face";
            frame.AppendChild(ButtonPresenter());
            template.VisualTree = frame;
            Trigger hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Theme.SoftSurface, "face"));
            template.Triggers.Add(hover);
            button.Template = template;
            if (click != null) button.Click += (s, e) => { e.Handled = true; click(); };
            return button;
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
                _settingsCard.Visibility = _settingsOpen ? Visibility.Visible : Visibility.Collapsed;
            if (_historyCard != null)
            {
                if (_historyOpen) _historyCard.Child = HistoryList();
                _historyCard.Visibility = _historyOpen ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_eyeCareCard != null)
            {
                if (_eyeCareOpen && _eyeCareView != null) _eyeCareView.UpdateUi();
                _eyeCareCard.Visibility = _eyeCareOpen ? Visibility.Visible : Visibility.Collapsed;
            }
            if (_chatCard != null)
            {
                _chatCard.Visibility = _chatOpen ? Visibility.Visible : Visibility.Collapsed;
                if (_chatPanel != null) _chatPanel.SetActive(_chatOpen);
            }
            if (_resultsRow != null && Theme.IsMinimal)
                _resultsRow.Height = HasResults() ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            ApplySize(false);
        }

        private void ApplySize(bool center)
        {
            double width = 560;
            double height = 104;
            if (!Theme.IsMinimal)
            {
                if (_picked == null)
                {
                    int count = _peers.Count;
                    int columns;
                    int rows;
                    DeviceGrid(count, out columns, out rows);
                    MeasureDevices();
                    width = Math.Max(300, 48 + columns * (_tileWidth + 16));
                    height = count == 0 ? 148 : 52 + rows * (116 + _nameLines * 18);
                }
                else
                {
                    width = 700;
                    height = 480;
                }
if (_settingsOpen)
                {
                    width = Math.Max(width, 360);
                    height = Math.Max(height, 560);
                }
                else if (_historyOpen)
                {
                    width = Math.Max(width, 320);
                    height = Math.Max(height, 280);
                }
            }
            else if (Theme.IsMinimal)
            {
                width = 560;
                bool results = HasResults();
                if (results) height = 440;
                else if (_settingsOpen) height = 420;
                else height = 104;
                if (_settingsOpen || _historyOpen) width = 360;
                if (_historyOpen && !results && !_settingsOpen) height = 280;
            }
            else
            {
                width = 700;
                height = 480;
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
            if (!IsVisible)
            {
                BeginAnimation(WidthProperty, null);
                BeginAnimation(HeightProperty, null);
                Width = width;
                Height = height;
                return;
            }
            if (Math.Abs(Width - width) > 1)
                BeginAnimation(WidthProperty, Theme.Animation(Width, width, 180));
            if (Math.Abs(Height - height) > 1)
                BeginAnimation(HeightProperty, Theme.Animation(Height, height, 180));
        }

        private void FitChat(ref double width, ref double height)
        {
            double scale = 1;
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
                scale = source.CompositionTarget.TransformToDevice.M22;
            if (scale < 0.5) scale = 1;
            double maxHeight = Math.Min(MaxHeight, (_launcherScreen.WorkingArea.Height / scale) - 28);
            double maxWidth = Math.Min(MaxWidth, (_launcherScreen.WorkingArea.Width / scale) - 28);
            width = Math.Min(maxWidth, 640);
            height = Math.Min(maxHeight, 520);
        }

        private void FitEyeCare(ref double width, ref double height)
        {
            const double cardWidth = 320;
            double content = 480;
            if (_eyeCareView != null)
            {
                _eyeCareView.Measure(new Size(cardWidth - 24, double.PositiveInfinity));
                if (_eyeCareView.DesiredSize.Height > 1) content = _eyeCareView.DesiredSize.Height;
            }
            double scale = 1;
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
                scale = source.CompositionTarget.TransformToDevice.M22;
            if (scale < 0.5) scale = 1;
            double maxHeight = Math.Min(MaxHeight, (_launcherScreen.WorkingArea.Height / scale) - 28);
            double chrome = 78;
            height = Math.Min(maxHeight, Math.Max(280, chrome + content));
            width = 380;
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
                Background = Theme.SoftSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(4, 2, 4, 4),
                Cursor = Cursors.Hand
            };
            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            TextBlock icon = Theme.Text("\uE7B3", 14, Theme.Ink, FontWeights.SemiBold);
            icon.FontFamily = new FontFamily("Segoe MDL2 Assets");
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

        private void MeasureDevices()
        {
            _tileWidth = 112;
            _nameLines = 1;
            foreach (Peer peer in _peers)
            {
                double width = NameWidth(peer.Name);
                if (width + 20 > _tileWidth) _tileWidth = Math.Min(240, width + 20);
                if (width > 210) _nameLines = 2;
            }
        }

        private double NameWidth(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            double dpi = 1;
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
                dpi = source.CompositionTarget.TransformToDevice.M11;
            FormattedText text = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(Theme.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                13, Brushes.Black, dpi);
            return text.Width;
        }

        private Button DeviceTile(Peer peer)
        {
            MeasureDevices();
            StackPanel stack = new StackPanel { Width = _tileWidth };
            Border circle = new Border
            {
                Width = 84,
                Height = 84,
                CornerRadius = new CornerRadius(42),
                Background = Theme.AvatarSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = PhotoContent(peer.Photo, peer.Name, 82)
            };
            TextBlock name = Theme.Text(peer.Name, 13, Theme.Ink);
            name.FontWeight = FontWeights.Normal;
            name.TextAlignment = TextAlignment.Center;
            name.TextWrapping = TextWrapping.Wrap;
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Width = _tileWidth;
            name.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(circle);
            stack.Children.Add(name);
            Button button = new Button
            {
                Content = stack,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Margin = new Thickness(8),
                ToolTip = peer.Name + (peer.Address == null ? "" : " · " + peer.Address)
            };
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = ButtonPresenter() };
            button.AllowDrop = true;
            button.MouseEnter += (s, e) => circle.Background = Theme.AvatarHover;
            button.MouseLeave += (s, e) => { if (!button.IsMouseOver) circle.Background = Theme.AvatarSurface; };
            DragEventHandler drag = (s, e) =>
            {
                bool file = HasFiles(e.Data);
                e.Effects = file ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
                circle.Background = file ? Theme.AvatarHover : Theme.AvatarSurface;
            };
            button.PreviewDragOver += drag;
            button.DragOver += drag;
            button.DragLeave += (s, e) => circle.Background = Theme.AvatarSurface;
            button.Drop += (s, e) =>
            {
                circle.Background = Theme.AvatarSurface;
                e.Handled = true;
                SendFiles(ExistingFiles(e.Data.GetData(DataFormats.FileDrop) as string[]), peer);
            };
            button.Click += (s, e) => PickAndSend(peer);
            return button;
        }

        private static void DeviceGrid(int count, out int columns, out int rows)
        {
            if (count <= 1) { columns = 1; rows = 1; return; }
            if (count <= 3) { columns = count; rows = 1; return; }
            if (count == 4) { columns = 2; rows = 2; return; }
            columns = count <= 6 ? 3 : 4;
            rows = (count + columns - 1) / columns;
        }

        private void PickAndSend(Peer peer)
        {
            if (_preview || peer == null) return;
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
            _ownAvatar = Avatar(new Peer { Name = Identity.Current, Photo = ProfilePhoto.Current }, 24,
                () => { _settingsOpen = true; SyncChrome(); });
            _ownAvatar.ToolTip = "Tu perfil · " + Identity.Current;
            System.Windows.Automation.AutomationProperties.SetName(_ownAvatar, "Tu perfil");
            return _ownAvatar;
        }

        private void RefreshOwnAvatar()
        {
            if (_ownAvatar == null) return;
            _ownAvatar.Content = PhotoContent(ProfilePhoto.Current, Identity.Current, 22);
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
            return new Border
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
            };
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
                MaxHeight = 490,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            return new Border
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
            };
        }

        private UIElement HistoryList()
        {
            StackPanel panel = new StackPanel();
            TextBlock title = Theme.Text("Historial", 11, Theme.Muted);
            panel.Children.Add(title);
            string[] rows = TransferLog.Recent();
            if (rows.Length == 0)
            {
                TextBlock empty = Theme.Text("Sin transferencias", 12, Theme.Muted);
                empty.Margin = new Thickness(0, 10, 0, 4);
                panel.Children.Add(empty);
                return panel;
            }
            foreach (string row in rows)
            {
                string[] parts = row.Split('|');
                if (parts.Length < 4) continue;
                string mark = parts[1] == "in" ? "↓" : "↑";
                TextBlock line = Theme.Text(mark + "  " + parts[3], 12, Theme.Ink);
                line.TextTrimming = TextTrimming.CharacterEllipsis;
                line.Margin = new Thickness(0, 8, 0, 0);
                TextBlock meta = Theme.Text(parts[2] + "   " + parts[0].Substring(Math.Max(0, parts[0].Length - 5)), 10, Theme.Muted);
                meta.Margin = new Thickness(16, 0, 0, 0);
                panel.Children.Add(line);
                panel.Children.Add(meta);
            }
            ScrollViewer scroll = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 190,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            return scroll;
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
            foreach (Peer peer in _peers) _devices.Children.Add(DeviceTile(peer));
            if (_deviceEmpty == null) return;
            bool empty = _peers.Count == 0;
            _deviceEmpty.Text = _networkError != null ? "Red no disponible" : "Sin equipos";
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
            if (!Theme.IsMinimal) return;
            _manualFiles.Clear();
            _manualFiles.AddRange(paths);
            if (_search != null) _search.Text = "";
            _selectedRow = 0;
            if (_empty != null) _empty.Text = "";
            SetStatus(paths.Length == 1 ? "Selecciona un destinatario" : paths.Length + " archivos · elige destinatario");
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
            return new Border
            {
                Background = Theme.CardSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(8, 40, 8, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Child = _chatPanel
            };
        }

        private void SendFilesFromChat(Peer peer, string[] paths)
        {
            if (peer == null || paths == null) return;
            SendFiles(paths, peer);
        }

        private void Shake()
        {
            if (!IsVisible) ShowAnimated();
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
                TransferLog.Add("out", peer.Name, Path.GetFileName(path));
                ChatStore.AppendFile(peer.Id, peer.Name, true, path);
                if (_chatPanel != null) _chatPanel.RefreshOpen();
            }
            catch (Exception ex) { SetStatus(ex.Message); }
            finally
            {
                lock (_sendProgress) _sendProgress.Remove(key);
                Interlocked.Decrement(ref _activeSends);
                if (_activeSends == 0) SetStatus("Entregado");
            }
        }

        private void SetStatus(string text)
        {
            if (_status != null) _status.Text = text;
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
                    TransferLog.Add("in", window.PeerName, Path.GetFileName(path));
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
                HideAnimated();
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (Theme.IsMinimal || _picked != null) ChooseFile();
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
            _motion.Enter(_shell, _liquid);
            FocusSearch();
            Dispatcher.BeginInvoke((Action)FocusSearch, DispatcherPriority.Input);
        }

        private void FocusSearch()
        {
            if (_search == null || !IsVisible) return;
            if (!Theme.IsMinimal && _picked == null) return;
            WindowPlacement.TakeForeground(this);
            Activate();
            _search.Focus();
            Keyboard.Focus(_search);
        }

        private void ResetHome()
        {
            _settingsOpen = false;
            _historyOpen = false;
            _eyeCareOpen = false;
            _chatOpen = false;
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

        private void Cleanup()
        {
            _motion.Cancel();
            _searchDelay.Stop();
            if (_everything != null) _everything.Dispose();
            if (_hotkeys != null) _hotkeys.Dispose();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _network.Dispose();
        }
    }
}
