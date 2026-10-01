using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lazo
{
    internal enum ThemeKind { Raycast, Glass, Dark, Warm }
    internal enum InterfaceKind { Minimal, Standard }

    internal static class Theme
    {
        public static ThemeKind Mode { get; private set; }
        public static InterfaceKind Interface { get; private set; }
        public static bool IsGlass { get { return Mode == ThemeKind.Glass; } }
        public static bool IsDark { get { return Mode == ThemeKind.Dark; } }
        public static bool IsWarm { get { return Mode == ThemeKind.Warm; } }
        public static bool IsMinimal { get { return Interface == InterfaceKind.Minimal; } }
        public static readonly FontFamily Mono = new FontFamily("Cascadia Code, Consolas");
        private static readonly FontFamily Sans = new FontFamily("Bahnschrift, Segoe UI");
        private static readonly FontFamily Serif = new FontFamily("Georgia, Segoe UI");

        public static void Load()
        {
            try { Mode = ParseTheme(File.ReadAllText(SettingsPath())); }
            catch { Mode = ThemeKind.Raycast; }
            try { Interface = File.ReadAllText(InterfacePath()).Trim() == "standard" ? InterfaceKind.Standard : InterfaceKind.Minimal; }
            catch { Interface = InterfaceKind.Minimal; }
        }

        public static void SetForPreview(ThemeKind mode) { Mode = mode; }

        public static void SetInterface(InterfaceKind kind, bool persist)
        {
            Interface = kind;
            if (!persist) return;
            try
            {
                string path = InterfacePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, kind == InterfaceKind.Standard ? "standard" : "minimal");
            }
            catch { }
        }

        public static void SetAppearance(ThemeKind kind, bool persist)
        {
            Mode = kind;
            if (!persist) return;
            try
            {
                string path = SettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, SerializeTheme(kind));
            }
            catch { }
        }

        public static void SetGlass(bool glass, bool persist)
        {
            SetAppearance(glass ? ThemeKind.Glass : ThemeKind.Raycast, persist);
        }

        public static void Toggle(bool persist)
        {
            SetGlass(!IsGlass, persist);
        }

        internal static ThemeKind ParseTheme(string value)
        {
            value = value.Trim();
            if (value == "glass") return ThemeKind.Glass;
            if (value == "dark") return ThemeKind.Dark;
            if (value == "warm") return ThemeKind.Warm;
            return ThemeKind.Raycast;
        }

        internal static string SerializeTheme(ThemeKind kind)
        {
            if (kind == ThemeKind.Glass) return "glass";
            if (kind == ThemeKind.Dark) return "dark";
            if (kind == ThemeKind.Warm) return "warm";
            return "raycast";
        }

        private static string SettingsPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "theme.txt");
        }

        private static string InterfacePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", "interface.txt");
        }

        public static Brush Color(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        public static Brush Ink { get { return Color(IsDark ? "#E6E6E6" : IsWarm ? "#211915" : "#242424"); } }
        public static Brush Muted { get { return Color(IsDark ? "#8E8E8E" : IsWarm ? "#6B6763" : IsGlass ? "#646464" : "#777777"); } }
        public static Brush Line { get { return Color(IsDark ? "#3A3A3A" : IsWarm ? "#A7A29D" : IsGlass ? "#BFFFFFFF" : "#D3D3D3"); } }
        public static Brush CardSurface { get { return Color(IsDark ? "#242424" : IsWarm ? "#F5EFE6" : IsGlass ? "#B8FFFFFF" : "#F4F4F4"); } }
        public static Brush SoftSurface { get { return Color(IsDark ? "#2E2E2E" : IsWarm ? "#8A8F7A" : IsGlass ? "#80FFFFFF" : "#DEDEDE"); } }
        public static Brush AvatarSurface { get { return Color(IsDark ? "#2A2A2A" : IsWarm ? "#8A8F7A" : IsGlass ? "#B0FFFFFF" : "#ECECEC"); } }
        public static Brush AvatarHover { get { return Color(IsDark ? "#3C3C3C" : IsWarm ? "#A7A29D" : IsGlass ? "#E5FFFFFF" : "#CECECE"); } }
        public static Brush Primary { get { return Color(IsDark ? "#E6E6E6" : IsWarm ? "#C97F63" : IsGlass ? "#252525" : "#F2F2F2"); } }
        public static Brush PrimaryText { get { return Color(IsDark ? "#161616" : IsWarm ? "#211915" : IsGlass ? "#FFFFFF" : "#19191B"); } }
        public static Brush SelectionFill { get { return Color(IsDark ? "#E6E6E6" : IsWarm ? "#C97F63" : "#242424"); } }
        public static Brush SelectionText { get { return IsDark || IsWarm ? Color(IsDark ? "#161616" : "#211915") : Brushes.White; } }
        public static Brush SegmentTrack { get { return Color(IsDark ? "#292929" : IsWarm ? "#F5EFE6" : IsGlass ? "#B8FFFFFF" : "#EAEAEA"); } }
        public static Brush SegmentActive { get { return Color(IsDark ? "#F0F0F0" : IsWarm ? "#C97F63" : "#FFFFFF"); } }
        public static Brush SegmentActiveText { get { return Color(IsWarm ? "#211915" : "#1B1B1B"); } }
        public static Brush SegmentMutedText { get { return Color(IsDark ? "#AAAAAA" : IsWarm ? "#6B6763" : "#959595"); } }
        public static FontFamily Font { get { return Sans; } }
        public static CornerRadius Radius { get { return new CornerRadius(IsGlass ? 20 : 10); } }

        public static Brush ShellSurface()
        {
            if (IsDark) return Color("#171717");
            if (IsWarm) return Color("#F5EFE6");
            if (!IsGlass) return Color("#F1F1F1");
            LinearGradientBrush gradient = new LinearGradientBrush();
            gradient.StartPoint = new Point(0, 0);
            gradient.EndPoint = new Point(1, 1);
            gradient.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(239, 252, 252, 252), 0));
            gradient.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(222, 231, 231, 231), 0.52));
            gradient.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(231, 249, 249, 249), 1));
            return gradient;
        }

        public static TextBlock Text(string value, double size, Brush color, FontWeight weight)
        {
            return new TextBlock { Text = value, FontFamily = IsWarm && size >= 18 ? Serif : Font, FontSize = size,
                FontWeight = weight, Foreground = color, TextWrapping = TextWrapping.Wrap };
        }

        public static TextBlock Text(string value, double size, Brush color)
        {
            return Text(value, size, color, FontWeights.Normal);
        }

        public static Button Button(string label, bool primary)
        {
            Button button = new Button { Content = label, FontFamily = Font, FontSize = 12,
                FontWeight = FontWeights.SemiBold, Cursor = System.Windows.Input.Cursors.Hand,
                MinHeight = 36, Padding = new Thickness(16, 0, 16, 0),
                Background = primary ? Primary : SoftSurface,
                Foreground = primary ? PrimaryText : Ink,
                BorderBrush = primary ? Primary : Line, BorderThickness = new Thickness(1) };
            ControlTemplate template = new ControlTemplate(typeof(System.Windows.Controls.Button));
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(Border));
            frame.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            frame.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            frame.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(IsGlass ? 17 : 8));
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            content.SetValue(ContentPresenter.MarginProperty, new Thickness(8, 0, 8, 0));
            frame.AppendChild(content);
            template.VisualTree = frame;
            Trigger hover = new Trigger { Property = System.Windows.Controls.Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(System.Windows.Controls.Button.OpacityProperty, 0.78));
            template.Triggers.Add(hover);
            Trigger disabled = new Trigger { Property = System.Windows.Controls.Button.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(System.Windows.Controls.Button.OpacityProperty, 0.4));
            template.Triggers.Add(disabled);
            button.Template = template;
            return button;
        }

        public static Border Card(UIElement child, Thickness padding)
        {
            return new Border { Background = CardSurface, BorderBrush = Line,
                BorderThickness = new Thickness(1), CornerRadius = Radius, Padding = padding, Child = child };
        }

        public static DoubleAnimation Animation(double from, double to, int milliseconds)
        {
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd };
        }

    }
}
