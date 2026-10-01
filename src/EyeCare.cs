using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Lazo
{
    internal enum EyeCareClockMode { Digital, Analog }
    internal enum EyeCareBreakType { MicroBreak, ActivePause }
    internal enum EyeCareRestStyle { Notice, Eyes }

    internal sealed class EyeCareService
    {
        public static readonly EyeCareService Instance = new EyeCareService();

        public const int MicroBreakInterval = 20 * 60; // 20 minutos (1200 seg)
        public const int MicroBreakDuration = 20;      // 20 segundos
        public const int ActivePauseInterval = 55 * 60; // 55 minutos (3300 seg)
        public const int ActivePauseDuration = 5 * 60;  // 5 minutos (300 seg)

        private readonly DispatcherTimer _timer;
        private Window _currentAlert;
        private DateTime _microDueUtc;
        private DateTime _activeDueUtc;
        private string _statsDate;

        public int SecondsToMicroBreak { get; private set; }
        public int SecondsToActivePause { get; private set; }
        public int TotalWorkSecondsToday { get; private set; }
        public int MicroBreaksCompleted { get; private set; }
        public int ActivePausesCompleted { get; private set; }
        public bool IsPaused { get; private set; }
        public bool SoundEnabled { get; private set; }
        public EyeCareClockMode ClockMode { get; private set; }
        public EyeCareRestStyle RestStyle { get; private set; }

        public event Action Ticked;
        public event Action StateChanged;

        private EyeCareService()
        {
            SoundEnabled = true;
            ClockMode = EyeCareClockMode.Digital;
            RestStyle = EyeCareRestStyle.Notice;
            _statsDate = DateTime.Now.ToString("yyyy-MM-dd");
            Arm(EyeCareBreakType.MicroBreak, MicroBreakInterval);
            Arm(EyeCareBreakType.ActivePause, ActivePauseInterval);
            LoadSettings();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) => OnTick();
        }

        public void Start()
        {
            if (!_timer.IsEnabled) _timer.Start();
        }

        public void Stop()
        {
            if (_timer.IsEnabled) _timer.Stop();
        }

        public void SetClockMode(EyeCareClockMode mode)
        {
            if (ClockMode == mode) return;
            ClockMode = mode;
            SaveSettings();
            if (StateChanged != null) StateChanged();
        }

        public void SetPaused(bool paused)
        {
            if (IsPaused == paused) return;
            IsPaused = paused;
            if (!paused) TryShowDue();
            SaveSettings();
            if (StateChanged != null) StateChanged();
        }

        public void SetSoundEnabled(bool enabled)
        {
            if (SoundEnabled == enabled) return;
            SoundEnabled = enabled;
            SaveSettings();
            if (StateChanged != null) StateChanged();
        }

        public void SetRestStyle(EyeCareRestStyle style)
        {
            if (RestStyle == style) return;
            RestStyle = style;
            SaveSettings();
            if (StateChanged != null) StateChanged();
        }

        public void TriggerMicroBreak()
        {
            ShowAlert(EyeCareBreakType.MicroBreak, MicroBreakDuration);
            Arm(EyeCareBreakType.MicroBreak, MicroBreakInterval);
            SaveSettings();
            if (StateChanged != null) StateChanged();
        }

        public void TriggerActivePause()
        {
            ShowAlert(EyeCareBreakType.ActivePause, ActivePauseDuration);
            Arm(EyeCareBreakType.ActivePause, ActivePauseInterval);
            SaveSettings();
            if (StateChanged != null) StateChanged();
        }

        public void Snooze(EyeCareBreakType type, int seconds = 300)
        {
            Arm(type, seconds);
            CloseCurrentAlert();
            SaveSettings();
            if (StateChanged != null) StateChanged();
        }

        public void CompleteBreak(EyeCareBreakType type)
        {
            if (type == EyeCareBreakType.MicroBreak) MicroBreaksCompleted++;
            else ActivePausesCompleted++;
            Arm(type, type == EyeCareBreakType.MicroBreak ? MicroBreakInterval : ActivePauseInterval);
            SaveSettings();
            CloseCurrentAlert();
            if (StateChanged != null) StateChanged();
        }

        public void CloseCurrentAlert()
        {
            if (_currentAlert != null)
            {
                Window alert = _currentAlert;
                _currentAlert = null;
                try
                {
                    EyeCareAlertWindow toast = alert as EyeCareAlertWindow;
                    if (toast != null) toast.CloseAnimated();
                    else
                    {
                        EyeCareEyesWindow eyes = alert as EyeCareEyesWindow;
                        if (eyes != null) eyes.CloseAnimated();
                        else alert.Close();
                    }
                }
                catch { }
            }
        }

        private void OnTick()
        {
            RollDay();
            TotalWorkSecondsToday++;

            if (IsPaused)
            {
                if (SecondsToMicroBreak > 0) _microDueUtc = _microDueUtc.AddSeconds(1);
                if (SecondsToActivePause > 0) _activeDueUtc = _activeDueUtc.AddSeconds(1);
            }
            else
            {
                RefreshRemaining();
                TryShowDue();
            }

            if (TotalWorkSecondsToday % 60 == 0) SaveSettings();
            if (Ticked != null) Ticked();
        }

        private void RollDay()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (_statsDate == today) return;
            _statsDate = today;
            MicroBreaksCompleted = 0;
            ActivePausesCompleted = 0;
            TotalWorkSecondsToday = 0;
            SaveSettings();
        }

        private void RefreshRemaining()
        {
            SecondsToMicroBreak = Remaining(_microDueUtc);
            SecondsToActivePause = Remaining(_activeDueUtc);
        }

        private static int Remaining(DateTime dueUtc)
        {
            double seconds = (dueUtc - DateTime.UtcNow).TotalSeconds;
            if (seconds <= 0) return 0;
            return (int)Math.Ceiling(seconds);
        }

        private void Arm(EyeCareBreakType type, int seconds)
        {
            DateTime due = DateTime.UtcNow.AddSeconds(Math.Max(0, seconds));
            if (type == EyeCareBreakType.MicroBreak)
            {
                _microDueUtc = due;
                SecondsToMicroBreak = Math.Max(0, seconds);
            }
            else
            {
                _activeDueUtc = due;
                SecondsToActivePause = Math.Max(0, seconds);
            }
        }

        private void TryShowDue()
        {
            if (IsPaused || _currentAlert != null) return;
            RefreshRemaining();
            if (SecondsToMicroBreak <= 0)
            {
                ShowAlert(EyeCareBreakType.MicroBreak, MicroBreakDuration);
                Arm(EyeCareBreakType.MicroBreak, MicroBreakInterval);
                SaveSettings();
                if (StateChanged != null) StateChanged();
                return;
            }
            if (SecondsToActivePause <= 0)
            {
                ShowAlert(EyeCareBreakType.ActivePause, ActivePauseDuration);
                Arm(EyeCareBreakType.ActivePause, ActivePauseInterval);
                SaveSettings();
                if (StateChanged != null) StateChanged();
            }
        }

        private void ShowAlert(EyeCareBreakType type, int durationSeconds)
        {
            CloseCurrentAlert();

            if (SoundEnabled)
            {
                try { System.Media.SystemSounds.Asterisk.Play(); }
                catch { }
            }

            Window alert = type == EyeCareBreakType.MicroBreak && RestStyle == EyeCareRestStyle.Eyes
                ? (Window)new EyeCareEyesWindow(durationSeconds)
                : new EyeCareAlertWindow(type, durationSeconds);
            _currentAlert = alert;
            alert.Closed += (s, e) =>
            {
                if (_currentAlert == alert) _currentAlert = null;
                if (!IsPaused) TryShowDue();
            };
            alert.Show();
            EyeCareAlertWindow toast = alert as EyeCareAlertWindow;
            if (toast != null) WindowPlacement.PlaceBottomRight(toast, 0);
        }

        private static string SettingsPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "eyecare.txt");
        }

        private void LoadSettings()
        {
            try
            {
                string path = SettingsPath();
                if (!File.Exists(path)) return;

                string[] lines = File.ReadAllLines(path);
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                string savedDate = "";
                int microLeft = MicroBreakInterval;
                int activeLeft = ActivePauseInterval;
                bool hasMicroLeft = false;
                bool hasActiveLeft = false;
                DateTime savedUtc = DateTime.UtcNow;
                bool hasSavedUtc = false;

                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || !line.Contains("=")) continue;
                    int idx = line.IndexOf('=');
                    string key = line.Substring(0, idx).Trim().ToLowerInvariant();
                    string val = line.Substring(idx + 1).Trim();

                    switch (key)
                    {
                        case "clock":
                            ClockMode = val.ToLowerInvariant() == "analog" ? EyeCareClockMode.Analog : EyeCareClockMode.Digital;
                            break;
                        case "sound":
                            bool sound;
                            if (bool.TryParse(val, out sound)) SoundEnabled = sound;
                            break;
                        case "paused":
                            bool paused;
                            if (bool.TryParse(val, out paused)) IsPaused = paused;
                            break;
                        case "date":
                            savedDate = val;
                            break;
                        case "micro":
                            int micro;
                            if (int.TryParse(val, out micro)) MicroBreaksCompleted = micro;
                            break;
                        case "active":
                            int active;
                            if (int.TryParse(val, out active)) ActivePausesCompleted = active;
                            break;
                        case "work":
                            int work;
                            if (int.TryParse(val, out work)) TotalWorkSecondsToday = work;
                            break;
                        case "microleft":
                            int microRemaining;
                            if (int.TryParse(val, out microRemaining))
                            {
                                microLeft = microRemaining;
                                hasMicroLeft = true;
                            }
                            break;
                        case "activeleft":
                            int activeRemaining;
                            if (int.TryParse(val, out activeRemaining))
                            {
                                activeLeft = activeRemaining;
                                hasActiveLeft = true;
                            }
                            break;
                        case "rest":
                            RestStyle = val == "eyes" ? EyeCareRestStyle.Eyes : EyeCareRestStyle.Notice;
                            break;
                        case "savedutc":
                            DateTime parsed;
                            if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
                            {
                                savedUtc = parsed.ToUniversalTime();
                                hasSavedUtc = true;
                            }
                            break;
                    }
                }

                _statsDate = string.IsNullOrEmpty(savedDate) ? today : savedDate;
                if (savedDate != today)
                {
                    MicroBreaksCompleted = 0;
                    ActivePausesCompleted = 0;
                    TotalWorkSecondsToday = 0;
                    _statsDate = today;
                }

                int elapsed = 0;
                if (!IsPaused && hasSavedUtc)
                    elapsed = (int)Math.Max(0, (DateTime.UtcNow - savedUtc).TotalSeconds);
                if (hasMicroLeft) SecondsToMicroBreak = Math.Max(0, microLeft - elapsed);
                if (hasActiveLeft) SecondsToActivePause = Math.Max(0, activeLeft - elapsed);
                _microDueUtc = DateTime.UtcNow.AddSeconds(SecondsToMicroBreak);
                _activeDueUtc = DateTime.UtcNow.AddSeconds(SecondsToActivePause);
            }
            catch { }
        }

        public void SaveSettings()
        {
            try
            {
                string path = SettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string content = string.Format(
                    CultureInfo.InvariantCulture,
                    "clock={0}\nsound={1}\npaused={2}\ndate={3}\nmicro={4}\nactive={5}\nwork={6}\nmicroleft={7}\nactiveleft={8}\nsavedutc={9}\nrest={10}\n",
                    ClockMode == EyeCareClockMode.Analog ? "analog" : "digital",
                    SoundEnabled,
                    IsPaused,
                    _statsDate,
                    MicroBreaksCompleted,
                    ActivePausesCompleted,
                    TotalWorkSecondsToday,
                    SecondsToMicroBreak,
                    SecondsToActivePause,
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    RestStyle == EyeCareRestStyle.Eyes ? "eyes" : "notice");
                File.WriteAllText(path, content);
            }
            catch { }
        }
    }

    /// <summary>
    /// Reloj análogo minimalista con dibujo vectorial en WPF.
    /// </summary>
    internal sealed class EyeCareAnalogClock : FrameworkElement
    {
        private double _progressPercent;

        public double ProgressPercent
        {
            get { return _progressPercent; }
            set
            {
                _progressPercent = Math.Max(0.0, Math.Min(1.0, value));
                InvalidateVisual();
            }
        }

        public EyeCareAnalogClock()
        {
            Width = 100;
            Height = 100;
            SnapsToDevicePixels = true;
        }

        public void Refresh()
        {
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double w = ActualWidth;
            double h = ActualHeight;
            if (w < 10 || h < 10) return;

            Point center = new Point(w / 2.0, h / 2.0);
            double radius = Math.Min(w, h) / 2.0 - 4;

            // Fondo de la esfera
            dc.DrawEllipse(Theme.SoftSurface, new Pen(Theme.Line, 1.2), center, radius, radius);

            // Arco de progreso si está activo
            if (_progressPercent > 0.001)
            {
                DrawArc(dc, center, radius - 2, _progressPercent);
            }

            // Marcas horarias
            for (int i = 0; i < 12; i++)
            {
                double angleDeg = i * 30.0;
                double rad = (angleDeg - 90.0) * Math.PI / 180.0;
                bool isMajor = (i % 3 == 0);
                double outerR = radius - 4;
                double innerR = isMajor ? radius - 9 : radius - 7;

                Point p1 = new Point(center.X + Math.Cos(rad) * innerR, center.Y + Math.Sin(rad) * innerR);
                Point p2 = new Point(center.X + Math.Cos(rad) * outerR, center.Y + Math.Sin(rad) * outerR);
                dc.DrawLine(new Pen(isMajor ? Theme.Ink : Theme.Muted, isMajor ? 1.6 : 0.9), p1, p2);
            }

            // Manecillas de hora actual
            DateTime now = DateTime.Now;
            double hourAngle = ((now.Hour % 12) + now.Minute / 60.0 + now.Second / 3600.0) * 30.0;
            double minAngle = (now.Minute + now.Second / 60.0) * 6.0;
            double secAngle = now.Second * 6.0;

            DrawHand(dc, center, hourAngle, radius * 0.48, 2.4, Theme.Ink);
            DrawHand(dc, center, minAngle, radius * 0.70, 1.6, Theme.Ink);

            Brush secBrush = Theme.IsDark ? Theme.Color("#D0D0D0") : Theme.Color("#444444");
            DrawHand(dc, center, secAngle, radius * 0.82, 1.0, secBrush);

            dc.DrawEllipse(Theme.Ink, null, center, 3.0, 3.0);
            dc.DrawEllipse(Theme.CardSurface, null, center, 1.2, 1.2);
        }

        private void DrawHand(DrawingContext dc, Point center, double angleDeg, double length, double thickness, Brush brush)
        {
            double rad = (angleDeg - 90.0) * Math.PI / 180.0;
            Point target = new Point(center.X + Math.Cos(rad) * length, center.Y + Math.Sin(rad) * length);
            Point back = new Point(center.X - Math.Cos(rad) * (length * 0.15), center.Y - Math.Sin(rad) * (length * 0.15));

            Pen pen = new Pen(brush, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawLine(pen, back, target);
        }

        private void DrawArc(DrawingContext dc, Point center, double radius, double percent)
        {
            double angleDeg = percent * 360.0;
            if (angleDeg >= 360.0) angleDeg = 359.99;

            double startAngle = -90.0;
            double endAngle = startAngle + angleDeg;

            double startRad = startAngle * Math.PI / 180.0;
            double endRad = endAngle * Math.PI / 180.0;

            Point startPoint = new Point(center.X + Math.Cos(startRad) * radius, center.Y + Math.Sin(startRad) * radius);
            Point endPoint = new Point(center.X + Math.Cos(endRad) * radius, center.Y + Math.Sin(endRad) * radius);

            StreamGeometry geom = new StreamGeometry();
            using (StreamGeometryContext ctx = geom.Open())
            {
                ctx.BeginFigure(startPoint, false, false);
                ctx.ArcTo(endPoint, new Size(radius, radius), 0, angleDeg > 180.0, SweepDirection.Clockwise, true, false);
            }
            geom.Freeze();

            Pen pen = new Pen(Theme.Ink, 2.2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawGeometry(null, pen, geom);
        }
    }

    /// <summary>
    /// Ventana de alerta flotante no intrusiva para Descanso Visual (estilo Lazo ReceiveWindow).
    /// </summary>
    internal sealed class EyeCareAlertWindow : Window
    {
        private readonly EyeCareBreakType _type;
        private int _remainingSeconds;
        private readonly int _totalSeconds;
        private readonly DispatcherTimer _timer;
        private readonly Border _shell;
        private readonly TextBlock _countdownText;
        private readonly ScaleTransform _progressScale = new ScaleTransform(1, 1);
        private readonly EyeCareAnalogClock _analogClock;
        private bool _closing;

        public EyeCareAlertWindow(EyeCareBreakType type, int durationSeconds)
        {
            _type = type;
            _totalSeconds = durationSeconds;
            _remainingSeconds = durationSeconds;

            Title = type == EyeCareBreakType.MicroBreak ? "Lazo · Regla 20-20-20" : "Lazo · Pausa Activa";
            Width = 360;
            Height = 176;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Topmost = true;
            ShowActivated = false;
            ShowInTaskbar = false;
            FontFamily = Theme.Font;

            Grid outer = new Grid { Margin = new Thickness(10) };
            Content = outer;

            _shell = new Border
            {
                Background = Theme.ShellSurface(),
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                ClipToBounds = true,
                Opacity = 0
            };
            outer.Children.Add(_shell);

            Grid main = new Grid { Margin = new Thickness(14, 12, 14, 12) };
            main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

            // Fila Superior: Icono + Título + Botón Cerrar
            Grid topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

            TextBlock icon = Theme.Text(type == EyeCareBreakType.MicroBreak ? "\uE7B3" : "\uE7BE", 16, Theme.Ink, FontWeights.SemiBold);
            icon.FontFamily = new FontFamily("Segoe MDL2 Assets");
            icon.VerticalAlignment = VerticalAlignment.Center;
            topRow.Children.Add(icon);

            StackPanel titleBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock title = Theme.Text(type == EyeCareBreakType.MicroBreak ? "Descanso Visual · 20-20-20" : "Pausa Activa · Estiramiento", 12.5, Theme.Ink, FontWeights.SemiBold);
            TextBlock subtitle = Theme.Text(type == EyeCareBreakType.MicroBreak ? "Relaja tu vista mirando a 6 metros" : "Despeja tu cuerpo y mente", 11, Theme.Muted);
            titleBox.Children.Add(title);
            titleBox.Children.Add(subtitle);
            Grid.SetColumn(titleBox, 1);
            topRow.Children.Add(titleBox);

            Button close = new Button
            {
                Content = "×",
                FontFamily = Theme.Font,
                FontSize = 16,
                Foreground = Theme.Muted,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Width = 24,
                Height = 24,
                Padding = new Thickness(0, -2, 0, 0)
            };
            close.Click += (s, e) => { EyeCareService.Instance.Snooze(_type, 300); CloseAnimated(); };
            Grid.SetColumn(close, 2);
            topRow.Children.Add(close);
            main.Children.Add(topRow);

            // Fila Central: Reloj + Instrucción
            Grid centerGrid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            centerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            centerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (EyeCareService.Instance.ClockMode == EyeCareClockMode.Analog)
            {
                _analogClock = new EyeCareAnalogClock
                {
                    Width = 56,
                    Height = 56,
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                centerGrid.Children.Add(_analogClock);
                _countdownText = Theme.Text(FormatRemaining(_remainingSeconds), 18, Theme.Ink, FontWeights.SemiBold);
            }
            else
            {
                Border digitPill = new Border
                {
                    Background = Theme.SoftSurface,
                    BorderBrush = Theme.Line,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                _countdownText = Theme.Text(FormatRemaining(_remainingSeconds), 20, Theme.Ink, FontWeights.SemiBold);
                _countdownText.FontFamily = Theme.Mono;
                digitPill.Child = _countdownText;
                centerGrid.Children.Add(digitPill);
            }

            TextBlock desc = Theme.Text(
                type == EyeCareBreakType.MicroBreak
                    ? "Enfoca un objeto a unos 6 metros (20 pies) de distancia durante 20 segundos para relajar los ojos."
                    : "Ponte de pie, estira cuello, hombros y muñecas, respira hondo y desvía la mirada de la pantalla.",
                11,
                Theme.Muted);
            desc.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(desc, 1);
            centerGrid.Children.Add(desc);

            Grid.SetRow(centerGrid, 1);
            main.Children.Add(centerGrid);

            // Fila Inferior: Botones
            Grid bottomRow = new Grid();
            bottomRow.ColumnDefinitions.Add(new ColumnDefinition());
            bottomRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            bottomRow.ColumnDefinitions.Add(new ColumnDefinition());

            Button snooze = Theme.Button("Posponer 5 min", false);
            snooze.MinHeight = 28;
            snooze.Height = 28;
            snooze.FontSize = 11;
            snooze.Click += (s, e) => { EyeCareService.Instance.Snooze(_type, 300); CloseAnimated(); };
            bottomRow.Children.Add(snooze);

            Button done = Theme.Button("¡Listo!", true);
            done.MinHeight = 28;
            done.Height = 28;
            done.FontSize = 11;
            done.Click += (s, e) => { EyeCareService.Instance.CompleteBreak(_type); CloseAnimated(); };
            Grid.SetColumn(done, 2);
            bottomRow.Children.Add(done);

            Grid.SetRow(bottomRow, 2);
            main.Children.Add(bottomRow);

            Border progressBar = new Border
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
            _shell.Child = new Grid { Children = { main, progressBar } };

            Loaded += (s, e) =>
            {
                _shell.BeginAnimation(UIElement.OpacityProperty, Theme.Animation(0, 1, 160));
                UpdateCountdownUi();
            };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) =>
            {
                if (_remainingSeconds > 0)
                {
                    _remainingSeconds--;
                    UpdateCountdownUi();
                    if (_remainingSeconds <= 0)
                    {
                        _timer.Stop();
                        EyeCareService.Instance.CompleteBreak(_type);
                        CloseAnimated();
                    }
                }
            };
            _timer.Start();
        }

        private void UpdateCountdownUi()
        {
            _countdownText.Text = FormatRemaining(_remainingSeconds);
            double progress = (double)(_totalSeconds - _remainingSeconds) / Math.Max(1, _totalSeconds);
            _progressScale.ScaleX = Math.Min(1.0, Math.Max(0.0, 1.0 - progress));

            if (_analogClock != null)
            {
                _analogClock.ProgressPercent = progress;
                _analogClock.Refresh();
            }
        }

        private static string FormatRemaining(int seconds)
        {
            int m = seconds / 60;
            int s = seconds % 60;
            return string.Format("{0:00}:{1:00}", m, s);
        }

        public void CloseAnimated()
        {
            if (_closing) return;
            _closing = true;
            _timer.Stop();
            DoubleAnimation fade = Theme.Animation(1, 0, 140);
            fade.Completed += (s, e) => Close();
            _shell.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    /// <summary>
    /// Tarjeta de control de Descanso Visual integrada en la barra superior de Lazo.
    /// </summary>
    internal sealed class EyeCareCardView : StackPanel
    {
        private readonly TextBlock _statusBadgeText;
        private readonly Border _statusBadge;
        private readonly Border _clockHost;
        private readonly TextBlock _digitalClockText;
        private readonly EyeCareAnalogClock _analogClock;
        private readonly TextBlock _microTimeText;
        private readonly ScaleTransform _microProgressScale = new ScaleTransform(0, 1);
        private readonly TextBlock _activeTimeText;
        private readonly ScaleTransform _activeProgressScale = new ScaleTransform(0, 1);
        private readonly TextBlock _statsText;
        private readonly CheckBox _soundCheck;
        private readonly CheckBox _pauseCheck;
        private readonly Border _segmentContainer;
        private readonly Border _restStyleHost;

        public event Action ContentChanged;

        public EyeCareCardView()
        {
            Orientation = Orientation.Vertical;

            // 1. Cabecera: Título + Badge de estado
            Grid header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            TextBlock eyeIcon = Theme.Text("\uE7B3", 14, Theme.Ink, FontWeights.SemiBold);
            eyeIcon.FontFamily = new FontFamily("Segoe MDL2 Assets");
            eyeIcon.Margin = new Thickness(0, 0, 6, 0);
            eyeIcon.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(eyeIcon);

            TextBlock title = Theme.Text("Descanso Visual", 12.5, Theme.Ink, FontWeights.SemiBold);
            title.VerticalAlignment = VerticalAlignment.Center;
            titleRow.Children.Add(title);
            header.Children.Add(titleRow);

            _statusBadgeText = Theme.Text("Activo", 10, Theme.Ink, FontWeights.SemiBold);
            _statusBadge = new Border
            {
                Background = Theme.SoftSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = _statusBadgeText
            };
            Grid.SetColumn(_statusBadge, 1);
            header.Children.Add(_statusBadge);
            Children.Add(header);

            // 2. Selector Digital / Análogo
            _segmentContainer = new Border { Margin = new Thickness(0, 2, 0, 8) };
            RebuildSegment();
            Children.Add(_segmentContainer);

            // 3. Área del Reloj (Digital o Análogo)
            _clockHost = new Border
            {
                Background = Theme.SoftSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8)
            };

            // Controles de reloj
            _digitalClockText = Theme.Text(DateTime.Now.ToString("HH:mm:ss"), 24, Theme.Ink, FontWeights.SemiBold);
            _digitalClockText.FontFamily = Theme.Mono;
            _digitalClockText.HorizontalAlignment = HorizontalAlignment.Center;

            _analogClock = new EyeCareAnalogClock
            {
                Width = 90,
                Height = 90,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            RebuildClockHost();
            Children.Add(_clockHost);

            // 4. Tarjeta Regla 20-20-20
            Children.Add(BuildBreakSection(
                "Micro-descanso 20-20-20",
                "Cada 20 min mira a 6m durante 20s",
                out _microTimeText,
                _microProgressScale,
                "Descansar ahora (20s)",
                () => EyeCareService.Instance.TriggerMicroBreak()));

            // 5. Tarjeta Pausa Activa (5 min)
            Children.Add(BuildBreakSection(
                "Pausa Activa (5 min)",
                "Cada 55 min estira cuerpo y relaja mente",
                out _activeTimeText,
                _activeProgressScale,
                "Pausa ahora (5m)",
                () => EyeCareService.Instance.TriggerActivePause()));

            // 6. Resumen de Jornada de Trabajo
            Border workCard = new Border
            {
                Background = Theme.SoftSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };
            StackPanel workPanel = new StackPanel();
            TextBlock workHeader = Theme.Text("Hoy", 11, Theme.Ink, FontWeights.SemiBold);
            _statsText = Theme.Text("Pantalla: 0h 0m · 20-20-20: 0 · Pausas: 0", 10.5, Theme.Muted);
            _statsText.Margin = new Thickness(0, 3, 0, 0);
            workPanel.Children.Add(workHeader);
            workPanel.Children.Add(_statsText);
            workCard.Child = workPanel;
            Children.Add(workCard);

            TextBlock restLabel = Theme.Text("Al cumplirse los 20 min", 11, Theme.Ink, FontWeights.SemiBold);
            restLabel.Margin = new Thickness(0, 2, 0, 4);
            Children.Add(restLabel);
            _restStyleHost = new Border { Margin = new Thickness(0, 0, 0, 8) };
            RebuildRestStyle();
            Children.Add(_restStyleHost);

            // 7. Checkboxes de Preferencias
            _soundCheck = new CheckBox
            {
                Content = "Alertas sonoras",
                IsChecked = EyeCareService.Instance.SoundEnabled,
                Foreground = Theme.Ink,
                FontFamily = Theme.Font,
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 4)
            };
            _soundCheck.Checked += (s, e) => EyeCareService.Instance.SetSoundEnabled(true);
            _soundCheck.Unchecked += (s, e) => EyeCareService.Instance.SetSoundEnabled(false);
            Children.Add(_soundCheck);

            _pauseCheck = new CheckBox
            {
                Content = "Pausar temporizadores de descanso",
                IsChecked = EyeCareService.Instance.IsPaused,
                Foreground = Theme.Ink,
                FontFamily = Theme.Font,
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 4)
            };
            _pauseCheck.Checked += (s, e) => EyeCareService.Instance.SetPaused(true);
            _pauseCheck.Unchecked += (s, e) => EyeCareService.Instance.SetPaused(false);
            Children.Add(_pauseCheck);

            UpdateUi();
        }

        private Border BuildBreakSection(string title, string hint, out TextBlock timeLabel, ScaleTransform scale, string buttonLabel, Action action)
        {
            Border card = new Border
            {
                Background = Theme.SoftSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };

            StackPanel sp = new StackPanel();

            Grid top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock titleBlock = Theme.Text(title, 11.5, Theme.Ink, FontWeights.SemiBold);
            timeLabel = Theme.Text("00:00", 11.5, Theme.Ink, FontWeights.SemiBold);
            timeLabel.FontFamily = Theme.Mono;
            Grid.SetColumn(timeLabel, 1);
            top.Children.Add(titleBlock);
            top.Children.Add(timeLabel);
            sp.Children.Add(top);

            TextBlock hintBlock = Theme.Text(hint, 10, Theme.Muted);
            hintBlock.Margin = new Thickness(0, 2, 0, 6);
            sp.Children.Add(hintBlock);

            // Barra de progreso
            Border track = new Border
            {
                Height = 3,
                Background = Theme.Line,
                CornerRadius = new CornerRadius(1.5),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 0, 6),
                Child = new Border
                {
                    Background = Theme.Ink,
                    RenderTransform = scale,
                    RenderTransformOrigin = new Point(0, 0.5)
                }
            };
            sp.Children.Add(track);

            Button btn = Theme.Button(buttonLabel, false);
            btn.MinHeight = 26;
            btn.Height = 26;
            btn.FontSize = 10.5;
            btn.Click += (s, e) => action();
            sp.Children.Add(btn);

            card.Child = sp;
            return card;
        }

        public void RebuildRestStyle()
        {
            bool eyes = EyeCareService.Instance.RestStyle == EyeCareRestStyle.Eyes;
            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            Button notice = CreateSegmentBtn("Notificación", !eyes, () =>
            {
                EyeCareService.Instance.SetRestStyle(EyeCareRestStyle.Notice);
                RebuildRestStyle();
            });
            Button gaze = CreateSegmentBtn("Ojos", eyes, () =>
            {
                EyeCareService.Instance.SetRestStyle(EyeCareRestStyle.Eyes);
                RebuildRestStyle();
            });
            Grid.SetColumn(notice, 0);
            Grid.SetColumn(gaze, 1);
            grid.Children.Add(notice);
            grid.Children.Add(gaze);
            _restStyleHost.Background = Theme.SegmentTrack;
            _restStyleHost.CornerRadius = new CornerRadius(16);
            _restStyleHost.Padding = new Thickness(3);
            _restStyleHost.Child = grid;
            if (ContentChanged != null) ContentChanged();
        }

        public void RebuildSegment()
        {
            bool isDigital = EyeCareService.Instance.ClockMode == EyeCareClockMode.Digital;
            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            Button btnDigital = CreateSegmentBtn("Digital", isDigital, () =>
            {
                EyeCareService.Instance.SetClockMode(EyeCareClockMode.Digital);
                RebuildSegment();
                RebuildClockHost();
            });
            Button btnAnalog = CreateSegmentBtn("Análogo", !isDigital, () =>
            {
                EyeCareService.Instance.SetClockMode(EyeCareClockMode.Analog);
                RebuildSegment();
                RebuildClockHost();
            });

            Grid.SetColumn(btnDigital, 0);
            Grid.SetColumn(btnAnalog, 1);
            grid.Children.Add(btnDigital);
            grid.Children.Add(btnAnalog);

            _segmentContainer.Background = Theme.SegmentTrack;
            _segmentContainer.CornerRadius = new CornerRadius(16);
            _segmentContainer.Padding = new Thickness(3);
            _segmentContainer.Child = grid;
        }

        private static Button CreateSegmentBtn(string label, bool selected, Action onClick)
        {
            Button btn = new Button
            {
                Content = label,
                Height = 26,
                FontFamily = Theme.Font,
                FontSize = 11,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = selected ? Theme.SegmentActiveText : Theme.SegmentMutedText,
                Background = selected ? Theme.SegmentActive : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(Border));
            frame.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            frame.AppendChild(content);
            template.VisualTree = frame;
            btn.Template = template;
            btn.Click += (s, e) => { e.Handled = true; onClick(); };
            return btn;
        }

        private void RebuildClockHost()
        {
            if (EyeCareService.Instance.ClockMode == EyeCareClockMode.Analog)
            {
                StackPanel p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                DetachClockElement(_analogClock);
                p.Children.Add(_analogClock);
                TextBlock sub = Theme.Text(DateTime.Now.ToString("HH:mm:ss"), 11.5, Theme.Muted, FontWeights.SemiBold);
                sub.FontFamily = Theme.Mono;
                sub.HorizontalAlignment = HorizontalAlignment.Center;
                sub.Margin = new Thickness(0, 4, 0, 0);
                p.Children.Add(sub);
                _clockHost.Child = p;
            }
            else
            {
                StackPanel p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                DetachClockElement(_digitalClockText);
                p.Children.Add(_digitalClockText);
                TextBlock sub = Theme.Text("Reloj en tiempo real", 10.5, Theme.Muted);
                sub.HorizontalAlignment = HorizontalAlignment.Center;
                p.Children.Add(sub);
                _clockHost.Child = p;
            }
            if (ContentChanged != null) ContentChanged();
        }

        private static void DetachClockElement(UIElement element)
        {
            Panel previous = LogicalTreeHelper.GetParent(element) as Panel;
            if (previous != null) previous.Children.Remove(element);
        }

        public void UpdateUi()
        {
            // Estado y badge
            bool paused = EyeCareService.Instance.IsPaused;
            _statusBadgeText.Text = paused ? "Pausado" : "Activo";
            _statusBadgeText.Foreground = paused ? Theme.Muted : Theme.Ink;
            _pauseCheck.IsChecked = paused;
            _soundCheck.IsChecked = EyeCareService.Instance.SoundEnabled;

            // Hora actual
            string nowText = DateTime.Now.ToString("HH:mm:ss");
            _digitalClockText.Text = nowText;
            if (EyeCareService.Instance.ClockMode == EyeCareClockMode.Analog)
            {
                _analogClock.Refresh();
                StackPanel p = _clockHost.Child as StackPanel;
                if (p != null && p.Children.Count > 1)
                {
                    TextBlock sub = p.Children[1] as TextBlock;
                    if (sub != null) sub.Text = nowText;
                }
            }

            // 20-20-20
            int secMicro = EyeCareService.Instance.SecondsToMicroBreak;
            _microTimeText.Text = FormatTime(secMicro);
            double microProg = 1.0 - ((double)secMicro / EyeCareService.MicroBreakInterval);
            _microProgressScale.ScaleX = Math.Max(0.0, Math.Min(1.0, microProg));

            // Pausa Activa
            int secActive = EyeCareService.Instance.SecondsToActivePause;
            _activeTimeText.Text = FormatTime(secActive);
            double activeProg = 1.0 - ((double)secActive / EyeCareService.ActivePauseInterval);
            _activeProgressScale.ScaleX = Math.Max(0.0, Math.Min(1.0, activeProg));

            // Estadísticas
            int workSec = EyeCareService.Instance.TotalWorkSecondsToday;
            int hours = workSec / 3600;
            int mins = (workSec % 3600) / 60;
            _statsText.Text = string.Format("Pantalla: {0}h {1:00}m · 20-20-20: {2} · Pausas: {3}",
                hours,
                mins,
                EyeCareService.Instance.MicroBreaksCompleted,
                EyeCareService.Instance.ActivePausesCompleted);
        }

        private static string FormatTime(int seconds)
        {
            int m = seconds / 60;
            int s = seconds % 60;
            return string.Format("{0:00}:{1:00}", m, s);
        }
    }

    internal sealed class EyeGaze : FrameworkElement
    {
        private double _lid;
        private double _look;

        public double Lid
        {
            get { return _lid; }
            set { _lid = value; InvalidateVisual(); }
        }

        public double Look
        {
            get { return _look; }
            set { _look = value; InvalidateVisual(); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w < 20 || h < 20) return;
            dc.DrawRectangle(Theme.Color("#101010"), null, new Rect(0, 0, w, h));
            double eye = Math.Min(w * 0.28, h * 0.34);
            double gap = eye * 0.55;
            double cy = h * 0.42;
            DrawEye(dc, new Point(w / 2 - gap, cy), eye, -1);
            DrawEye(dc, new Point(w / 2 + gap, cy), eye, 1);
        }

        private void DrawEye(DrawingContext dc, Point center, double size, int side)
        {
            double open = Math.Max(0.045, 1 - _lid);
            double rx = size;
            double ry = size * 0.72 * open;
            dc.DrawEllipse(Theme.Color("#F2F2F2"), new Pen(Theme.Color("#D0D0D0"), 2), center, rx, ry);
            double look = _look * size * 0.08 + side * size * 0.04;
            Point iris = new Point(center.X + look, center.Y);
            double ir = size * 0.46;
            dc.DrawEllipse(Theme.Color("#3A3A3A"), null, iris, ir, ir * open);
            dc.DrawEllipse(Theme.Color("#111111"), null, iris, ir * 0.42, ir * 0.42 * open);
            if (open > 0.35)
            {
                dc.DrawEllipse(Theme.Color("#F7F7F7"), null,
                    new Point(iris.X - ir * 0.28, iris.Y - ir * 0.32 * open), ir * 0.14, ir * 0.14 * open);
            }
        }
    }

    internal sealed class EyeCareEyesWindow : Window
    {
        private readonly bool _host;
        private readonly List<EyeCareEyesWindow> _covers = new List<EyeCareEyesWindow>();
        private readonly EyeGaze _gaze = new EyeGaze();
        private readonly TextBlock _count;
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _motion;
        private int _remaining;
        private bool _closing;
        private bool _coversOpened;
        private double _lid;
        private double _lidTarget;
        private int _blinkIn;
        private DateTime _started;

        public EyeCareEyesWindow(int seconds)
            : this(seconds, true)
        {
        }

        private EyeCareEyesWindow(int seconds, bool host)
        {
            _host = host;
            _remaining = seconds;
            _started = DateTime.UtcNow;
            Title = "Lazo · Descanso Visual";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = Theme.Color("#101010");
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = host;
            Width = 800;
            Height = 600;
            FontFamily = Theme.Font;
            Cursor = Cursors.Arrow;

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _gaze.IsHitTestVisible = false;
            root.Children.Add(_gaze);
            StackPanel foot = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 36),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            if (host)
            {
                foot.Children.Add(Theme.Text("Mira lejos de la pantalla", 18, Theme.Color("#D8D8D8"), FontWeights.SemiBold));
                _count = Theme.Text(Format(seconds), 28, Theme.Color("#F2F2F2"), FontWeights.SemiBold);
                _count.FontFamily = Theme.Mono;
                _count.HorizontalAlignment = HorizontalAlignment.Center;
                _count.Margin = new Thickness(0, 8, 0, 0);
                foot.Children.Add(_count);
                Button snooze = new Button
                {
                    Content = "Posponer",
                    FontFamily = Theme.Font,
                    FontSize = 12,
                    Foreground = Theme.Color("#8A8A8A"),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Margin = new Thickness(0, 14, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                snooze.Click += (s, e) => EyeCareService.Instance.Snooze(EyeCareBreakType.MicroBreak, 300);
                foot.Children.Add(snooze);
            }
            else _count = null;
            Grid.SetRow(foot, 1);
            root.Children.Add(foot);
            Content = root;

            SourceInitialized += (s, e) => Place();
            Loaded += (s, e) =>
            {
                if (_host && !_coversOpened)
                {
                    _coversOpened = true;
                    OpenCovers();
                }
                Place();
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                Dispatcher.BeginInvoke((Action)Place, DispatcherPriority.Loaded);
            };
            if (!host) return;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    EyeCareService.Instance.Snooze(EyeCareBreakType.MicroBreak, 300);
                    e.Handled = true;
                }
            };
            Deactivated += (s, e) => { if (!_closing) Activate(); };
            _blinkIn = 90;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (s, e) =>
            {
                if (_remaining > 0) _remaining--;
                if (_count != null) _count.Text = Format(_remaining);
                if (_remaining <= 0)
                {
                    _timer.Stop();
                    EyeCareService.Instance.CompleteBreak(EyeCareBreakType.MicroBreak);
                }
            };
            _motion = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _motion.Tick += (s, e) => StepMotion();
            _timer.Start();
            _motion.Start();
        }

        private void OpenCovers()
        {
            System.Windows.Forms.Screen here = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
            {
                if (screen.DeviceName == here.DeviceName) continue;
                EyeCareEyesWindow cover = new EyeCareEyesWindow(_remaining, false);
                cover.Tag = screen;
                _covers.Add(cover);
                cover.Show();
            }
            Tag = here;
        }

        private void Place()
        {
            System.Windows.Forms.Screen screen = Tag as System.Windows.Forms.Screen;
            if (screen == null) screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            Tag = screen;
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
            {
                Rect px = new Rect(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height);
                Rect dip = new MatrixTransform(source.CompositionTarget.TransformFromDevice).TransformBounds(px);
                Left = dip.X;
                Top = dip.Y;
                Width = Math.Max(1, dip.Width);
                Height = Math.Max(1, dip.Height);
            }
            WindowPlacement.Cover(this, screen, _host);
        }

        private void StepMotion()
        {
            _blinkIn--;
            if (_blinkIn <= 0 && _lidTarget < 0.5)
            {
                _lidTarget = 1;
                _blinkIn = 8;
            }
            else if (_lidTarget > 0.5 && _blinkIn <= 0)
            {
                _lidTarget = 0;
                _blinkIn = 110;
            }
            _lid += (_lidTarget - _lid) * 0.45;
            double look = Math.Sin((DateTime.UtcNow - _started).TotalSeconds * 0.7) * 0.35;
            ApplyPhase(_lid, look);
            foreach (EyeCareEyesWindow cover in _covers) cover.ApplyPhase(_lid, look);
        }

        private void ApplyPhase(double lid, double look)
        {
            _gaze.Lid = lid;
            _gaze.Look = look;
        }

        public void CloseAnimated()
        {
            if (_closing) return;
            _closing = true;
            if (_timer != null) _timer.Stop();
            if (_motion != null) _motion.Stop();
            foreach (EyeCareEyesWindow cover in _covers)
            {
                try { cover.Close(); }
                catch { }
            }
            DoubleAnimation fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (s, e) => { try { Close(); } catch { } };
            BeginAnimation(OpacityProperty, fade);
        }

        private static string Format(int seconds)
        {
            return string.Format("{0:00}:{1:00}", seconds / 60, seconds % 60);
        }
    }
}
