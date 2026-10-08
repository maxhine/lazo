using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Lazo
{
    internal enum ThemeKind { Raycast, Glass, Dark, Warm, Wine, Image, Meet }
    internal enum InterfaceKind { Minimal, Standard }

    /// <summary>Colores de un tema. El acento se elige aparte y se aplica sobre cualquiera de ellos.</summary>
    internal sealed class Palette
    {
        public bool Dark;
        public string Shell0, Shell1, Shell2;
        public string Surface, Soft, Track, Ink, Muted, Line, Chip, ChipText, Avatar, Online, Accent;
    }

    internal static class Theme
    {
        public static ThemeKind Mode { get; private set; }
        public static InterfaceKind Interface { get; private set; }
        public static bool IsGlass { get { return Mode == ThemeKind.Glass; } }
        public static bool IsDark { get { return Mode == ThemeKind.Dark; } }
        public static bool IsWarm { get { return Mode == ThemeKind.Warm; } }
        public static bool IsWine { get { return Mode == ThemeKind.Wine; } }
        public static bool IsImage { get { return Mode == ThemeKind.Image; } }
        public static bool IsMeet { get { return Mode == ThemeKind.Meet; } }
        public static bool IsDarkSurface { get { return P.Dark; } }
        public static bool IsMinimal { get { return Interface == InterfaceKind.Minimal; } }
        public static readonly FontFamily Mono = new FontFamily("Cascadia Code, Consolas");
        private static readonly FontFamily Sans = new FontFamily("Bahnschrift, Segoe UI");
        private static readonly FontFamily Display = new FontFamily("Bahnschrift, Segoe UI");

        /// <summary>Acento personalizado (#RRGGBB) o vacío para usar el del tema.</summary>
        public static string CustomAccent { get; private set; }
        /// <summary>Superficies translúcidas con desenfoque del escritorio (Liquid Glass).</summary>
        public static bool LiquidGlass { get; private set; }

        public static readonly string[] AccentChoices = { "#EE5A45", "#F2547D", "#8B6CF6", "#3B82F6", "#14B8A6", "#22A55B", "#F2A33A", "#2E2F35" };

        public static void Load()
        {
            try { Mode = ParseTheme(File.ReadAllText(SettingsPath("theme.txt"))); }
            catch { Mode = ThemeKind.Wine; }
            if (Override.HasValue) Mode = Override.Value;
            try { Interface = File.ReadAllText(SettingsPath("interface.txt")).Trim() == "standard" ? InterfaceKind.Standard : InterfaceKind.Minimal; }
            catch { Interface = InterfaceKind.Minimal; }
            try { CustomAccent = NormalizeAccent(File.ReadAllText(SettingsPath("accent.txt"))); }
            catch { CustomAccent = ""; }
            try { LiquidGlass = File.ReadAllText(SettingsPath("glass.txt")).Trim() != "off"; }
            catch { LiquidGlass = true; }
            if (NoGlassOverride) LiquidGlass = false;
        }

        public static ThemeKind? Override;
        public static bool NoGlassOverride;
        public static void SetForPreview(ThemeKind mode) { Mode = mode; }

        public static void SetInterface(InterfaceKind kind, bool persist)
        {
            Interface = kind;
            if (persist) Save("interface.txt", kind == InterfaceKind.Standard ? "standard" : "minimal");
        }

        public static void SetAppearance(ThemeKind kind, bool persist)
        {
            Mode = kind;
            if (persist) Save("theme.txt", SerializeTheme(kind));
        }

        public static void SetAccent(string hex, bool persist)
        {
            CustomAccent = NormalizeAccent(hex ?? "");
            if (persist) Save("accent.txt", CustomAccent);
        }

        public static void SetLiquidGlass(bool on, bool persist)
        {
            LiquidGlass = on;
            if (persist) Save("glass.txt", on ? "on" : "off");
        }

        public static void SetGlass(bool glass, bool persist)
        {
            SetAppearance(glass ? ThemeKind.Glass : ThemeKind.Raycast, persist);
        }

        public static void Toggle(bool persist)
        {
            SetGlass(!IsGlass, persist);
        }

        private static void Save(string file, string value)
        {
            try
            {
                string path = SettingsPath(file);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, value);
            }
            catch { }
        }

        private static string NormalizeAccent(string value)
        {
            return (value ?? "").Trim() == ImageAccent ? ImageAccent : NormalizeHex(value);
        }

        internal static string NormalizeHex(string value)
        {
            value = (value ?? "").Trim();
            if (value.Length == 7 && value[0] == '#')
            {
                int parsed;
                if (int.TryParse(value.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out parsed)) return value.ToUpperInvariant();
            }
            return "";
        }

        internal static ThemeKind ParseTheme(string value)
        {
            value = value.Trim();
            if (value == "glass") return ThemeKind.Glass;
            if (value == "dark") return ThemeKind.Dark;
            if (value == "warm") return ThemeKind.Warm;
            if (value == "wine") return ThemeKind.Wine;
            if (value == "image") return ThemeKind.Image;
            if (value == "meet") return ThemeKind.Meet;
            return ThemeKind.Raycast;
        }

        internal static string SerializeTheme(ThemeKind kind)
        {
            if (kind == ThemeKind.Glass) return "glass";
            if (kind == ThemeKind.Dark) return "dark";
            if (kind == ThemeKind.Warm) return "warm";
            if (kind == ThemeKind.Wine) return "wine";
            if (kind == ThemeKind.Image) return "image";
            if (kind == ThemeKind.Meet) return "meet";
            return "raycast";
        }

        private static string SettingsPath(string file)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lazo", file);
        }

        // ── Paletas ─────────────────────────────────────────────────────────
        // Todas comparten la misma estructura (la del tema Vino); cambian solo los colores.

        private static readonly Palette WinePalette = new Palette
        {
            Dark = true, Shell0 = "#632026", Shell1 = "#551A20", Shell2 = "#4A161B",
            Surface = "#47161B", Soft = "#5E2329", Track = "#3B1115", Ink = "#FFF6F1", Muted = "#C99B9B",
            Line = "#24FFFFFF", Chip = "#FCEBD2", ChipText = "#4A1519", Avatar = "#6A272D", Online = "#7FD97A", Accent = "#EE5A45"
        };
        private static readonly Palette DarkPalette = new Palette
        {
            Dark = true, Shell0 = "#262626", Shell1 = "#1C1C1C", Shell2 = "#141414",
            Surface = "#242424", Soft = "#2E2E2E", Track = "#171717", Ink = "#E6E6E6", Muted = "#8E8E8E",
            Line = "#3A3A3A", Chip = "#E6E6E6", ChipText = "#161616", Avatar = "#2A2A2A", Online = "#34C759", Accent = "#E6E6E6"
        };
        private static readonly Palette FlatPalette = new Palette
        {
            Dark = false, Shell0 = "#F8F8FA", Shell1 = "#F0F1F4", Shell2 = "#E8E9EE",
            Surface = "#FFFFFF", Soft = "#ECEDF1", Track = "#E3E4E9", Ink = "#1E1F24", Muted = "#666771",
            Line = "#16000000", Chip = "#1E1F24", ChipText = "#FFFFFF", Avatar = "#E3E4E9", Online = "#2FB45A", Accent = "#2E2F35"
        };
        private static readonly Palette GlassPalette = new Palette
        {
            Dark = false, Shell0 = "#F4F7FC", Shell1 = "#ECF0F7", Shell2 = "#E4E9F2",
            Surface = "#FFFFFF", Soft = "#E8ECF4", Track = "#DDE3EE", Ink = "#1B2130", Muted = "#5F6676",
            Line = "#1A1B2130", Chip = "#1B2130", ChipText = "#FFFFFF", Avatar = "#DDE3EE", Online = "#2FB45A", Accent = "#3B82F6"
        };
        private static readonly Palette WarmPalette = new Palette
        {
            Dark = false, Shell0 = "#F7F1E8", Shell1 = "#F1E8DC", Shell2 = "#EADFD0",
            Surface = "#FCF8F2", Soft = "#EEE4D7", Track = "#E6D9C8", Ink = "#211915", Muted = "#6B6560",
            Line = "#1F3B2A1E", Chip = "#211915", ChipText = "#F7F1E8", Avatar = "#E2D4C1", Online = "#5F9A57", Accent = "#C97F63"
        };

        // Meet: casi negro de Google Meet, controles gris oscuro, selección azul claro y anillo ámbar.
        private static readonly Palette MeetPalette = new Palette
        {
            Dark = true, Shell0 = "#1E1F20", Shell1 = "#171718", Shell2 = "#131314",
            Surface = "#1E1F20", Soft = "#333537", Track = "#1E1F20", Ink = "#E3E3E3", Muted = "#9AA0A6",
            Line = "#3C4043", Chip = "#A8C7FA", ChipText = "#062E6F", Avatar = "#3C4043", Online = "#34A853", Accent = "#FBBC04"
        };

        public static Palette P { get { return PaletteFor(Mode); } }

        public static Palette PaletteFor(ThemeKind kind)
        {
            {
                switch (kind)
                {
                    case ThemeKind.Wine: return WinePalette;
                    case ThemeKind.Dark: return DarkPalette;
                    case ThemeKind.Glass: return GlassPalette;
                    case ThemeKind.Warm: return WarmPalette;
                    case ThemeKind.Image: return ImageTheme.Palette;
                    case ThemeKind.Meet: return MeetPalette;
                    default: return FlatPalette;
                }
            }
        }

        /// <summary>Valor especial de acento: tomarlo siempre del color principal de la foto de inicio.</summary>
        public const string ImageAccent = "image";
        public static bool AccentFromImage { get { return CustomAccent == ImageAccent; } }

        public static string AccentHex
        {
            get
            {
                if (AccentFromImage)
                {
                    string[] picks = ImageTheme.Suggestions;
                    return picks.Length > 0 ? picks[0] : P.Accent;
                }
                return CustomAccent.Length > 0 ? CustomAccent : P.Accent;
            }
        }
        public static Color AccentColor { get { return Parse(AccentHex); } }

        public static Color Parse(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

        public static Brush Color(string hex)
        {
            return new SolidColorBrush(Parse(hex));
        }

        private static Brush Solid(Color color) { return new SolidColorBrush(color); }

        internal static Color Mix(Color a, Color b, double t)
        {
            return System.Windows.Media.Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }

        private static Color WithAlpha(Color c, byte alpha) { return System.Windows.Media.Color.FromArgb(alpha, c.R, c.G, c.B); }

        internal static double Luminance(Color c)
        {
            Func<double, double> channel = v => { v /= 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); };
            return 0.2126 * channel(c.R) + 0.7152 * channel(c.G) + 0.0722 * channel(c.B);
        }

        /// <summary>Superficie con la translucidez de Liquid Glass cuando está activa.</summary>
        private static Brush Glassy(string hex, byte alpha)
        {
            Color c = Parse(hex);
            if (LiquidGlass && c.A == 255) c = WithAlpha(c, alpha);
            return Solid(c);
        }

        public static Brush Ink { get { return Color(P.Ink); } }
        public static Brush Muted { get { return Color(P.Muted); } }
        public static Brush Line { get { return Color(P.Line); } }
        public static Brush CardSurface { get { return Glassy(P.Surface, (byte)(P.Dark ? 0xB8 : 0xC8)); } }
        /// <summary>Vidrio denso para paneles que tapan el contenido (chat, selector).</summary>
        public static Brush OverlaySurface { get { return Glassy(P.Shell1, (byte)0xEE); } }
        public static Brush SoftSurface { get { return Glassy(P.Soft, (byte)(P.Dark ? 0xC0 : 0xD0)); } }
        public static Brush AvatarSurface { get { return Color(P.Avatar); } }
        public static Brush AvatarHover { get { return Solid(Mix(Parse(P.Avatar), Parse(P.Dark ? "#FFFFFF" : "#000000"), 0.08)); } }
        public static Brush Primary { get { return AccentGradient(); } }
        public static Brush PrimaryText { get { return Solid(OnAccent()); } }
        public static Brush SelectionFill { get { return Solid(AccentColor); } }
        public static Brush SelectionText { get { return Solid(OnAccent()); } }
        public static Brush SegmentTrack { get { return Glassy(P.Track, (byte)(P.Dark ? 0xC8 : 0xD8)); } }
        public static Brush SegmentActive { get { return Color(P.Chip); } }
        public static Brush SegmentActiveText { get { return Color(P.ChipText); } }
        public static Brush SegmentMutedText { get { return Color(P.Muted); } }
        public static FontFamily Font { get { return Sans; } }
        public static FontFamily DisplayFont { get { return Display; } }
        public static Brush SidebarSurface { get { return Glassy(P.Track, (byte)(P.Dark ? 0xC0 : 0xD0)); } }
        public static Brush TileSurface { get { return Glassy(P.Surface, (byte)(P.Dark ? 0xB0 : 0xC0)); } }
        public static Brush TileHover { get { return Solid(Mix(Parse(P.Surface), Parse(P.Dark ? "#FFFFFF" : "#000000"), 0.06)); } }
        public static Brush TileLine { get { return Color(P.Line); } }
        public static Brush NavActive { get { return Solid(WithAlpha(AccentReadable(), 0x30)); } }
        public static Brush NavActiveText { get { return Solid(AccentReadable()); } }
        public static Brush Cream { get { return Color(P.Chip); } }
        public static Brush CreamText { get { return Color(P.ChipText); } }
        public static Brush Danger { get { return Color("#E5484D"); } }
        public static bool FloatingRail { get { return true; } }
        public static Brush Online { get { return Color(P.Online); } }
        public static CornerRadius Radius { get { return new CornerRadius(18); } }

        /// <summary>Texto legible sobre el acento: blanco o tinta oscura según su luminancia.</summary>
        private static Color OnAccent()
        {
            // Blanco mientras se lea (3:1, texto seminegrita); si el acento es claro, tinta oscura.
            return 1.05 / (Luminance(AccentColor) + 0.05) >= 3 ? Colors.White : Parse("#1A1A1F");
        }

        /// <summary>El acento ajustado para leerse sobre el fondo del tema (íconos activos, enlaces).</summary>
        public static Color AccentReadable()
        {
            Color accent = AccentColor;
            double lum = Luminance(accent);
            if (P.Dark && lum < 0.18) return Mix(accent, Colors.White, 0.45);
            if (!P.Dark && lum > 0.45) return Mix(accent, Colors.Black, 0.35);
            return accent;
        }

        public static Brush AccentGradient()
        {
            Color accent = AccentColor;
            LinearGradientBrush brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            brush.GradientStops.Add(new GradientStop(Mix(accent, Colors.White, 0.14), 0));
            brush.GradientStops.Add(new GradientStop(Mix(accent, Colors.Black, 0.10), 1));
            brush.Freeze();
            return brush;
        }

        public static Brush Coral() { return AccentGradient(); }

        /// <summary>Degradado de la tarjeta destacada de inicio, derivado del acento.</summary>
        public static Brush HeroSurface()
        {
            Color accent = AccentColor;
            if (IsMeet)
            {
                // Recuadro de participante: tono cálido del acento muy oscurecido, con luz al centro.
                RadialGradientBrush tile = new RadialGradientBrush { RadiusX = 0.75, RadiusY = 0.9 };
                tile.GradientStops.Add(new GradientStop(Mix(accent, Parse("#1E1F20"), 0.78), 0));
                tile.GradientStops.Add(new GradientStop(Mix(accent, Parse("#1E1F20"), 0.86), 1));
                tile.Freeze();
                return tile;
            }
            LinearGradientBrush hero = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0.6) };
            hero.GradientStops.Add(new GradientStop(Mix(accent, Colors.White, 0.16), 0));
            hero.GradientStops.Add(new GradientStop(accent, 0.55));
            hero.GradientStops.Add(new GradientStop(Mix(accent, Colors.Black, 0.38), 1));
            hero.Freeze();
            return hero;
        }

        /// <summary>Si la tarjeta destacada (sin foto) es clara y necesita texto oscuro.</summary>
        public static bool HeroIsLight { get { return !IsMeet && ((SolidColorBrush)PrimaryText).Color != Colors.White; } }

        /// <summary>Borde especular de Liquid Glass: luz arriba a la izquierda que se desvanece y reaparece abajo.</summary>
        public static Brush GlassRim()
        {
            byte a0 = (byte)(P.Dark ? 0x70 : 0xF0), a1 = (byte)(P.Dark ? 0x10 : 0x40), a2 = (byte)(P.Dark ? 0x30 : 0x90);
            LinearGradientBrush rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            rim.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(a0, 255, 255, 255), 0));
            rim.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(a1, 255, 255, 255), 0.45));
            rim.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(a1, 255, 255, 255), 0.7));
            rim.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(a2, 255, 255, 255), 1));
            rim.Freeze();
            return rim;
        }

        /// <summary>Brillo superior interno de una superficie de vidrio.</summary>
        public static Border Sheen(CornerRadius radius)
        {
            LinearGradientBrush sheen = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            sheen.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb((byte)(P.Dark ? 0x1C : 0x66), 255, 255, 255), 0));
            sheen.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, 255, 255, 255), 0.42));
            sheen.Freeze();
            return new Border { CornerRadius = radius, Background = sheen, IsHitTestVisible = false };
        }

        /// <summary>Aplica el material de vidrio a un borde: relleno translúcido y borde especular.</summary>
        public static T Glass<T>(T border) where T : Border
        {
            border.BorderBrush = GlassRim();
            border.BorderThickness = new Thickness(1);
            return border;
        }

        public static TextBlock Label(string value)
        {
            TextBlock label = Text(value, 12, Muted, FontWeights.SemiBold);
            label.TextWrapping = TextWrapping.NoWrap;
            return label;
        }

        /// <summary>Interruptor animado en lugar de la casilla clásica.</summary>
        public static Style CheckBoxStyle()
        {
            Color accent = AccentColor;
            string on = "#" + accent.R.ToString("X2") + accent.G.ToString("X2") + accent.B.ToString("X2");
            string off = P.Dark ? "#40FFFFFF" : "#26000000";
            string xaml =
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='CheckBox'>" +
                "<Setter Property='Cursor' Value='Hand'/>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'>" +
                "<Grid Background='Transparent'><Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>" +
                "<ContentPresenter VerticalAlignment='Center' Margin='0,0,12,0'/>" +
                "<Border Name='track' Grid.Column='1' Width='38' Height='22' CornerRadius='11' Background='" + off + "' VerticalAlignment='Center'>" +
                "<Ellipse Name='knob' Width='16' Height='16' Fill='White' HorizontalAlignment='Left' Margin='3,0,0,0'>" +
                "<Ellipse.RenderTransform><TranslateTransform x:Name='shift' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'/></Ellipse.RenderTransform>" +
                "<Ellipse.Effect><DropShadowEffect BlurRadius='4' ShadowDepth='1' Opacity='0.25'/></Ellipse.Effect></Ellipse>" +
                "</Border></Grid>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsChecked' Value='True'>" +
                "<Setter TargetName='track' Property='Background' Value='" + on + "'/>" +
                "<Trigger.EnterActions><BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='shift' Storyboard.TargetProperty='X' To='16' Duration='0:0:0.22'><DoubleAnimation.EasingFunction><CubicEase EasingMode='EaseOut'/></DoubleAnimation.EasingFunction></DoubleAnimation></Storyboard></BeginStoryboard></Trigger.EnterActions>" +
                "<Trigger.ExitActions><BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='shift' Storyboard.TargetProperty='X' To='0' Duration='0:0:0.22'><DoubleAnimation.EasingFunction><CubicEase EasingMode='EaseOut'/></DoubleAnimation.EasingFunction></DoubleAnimation></Storyboard></BeginStoryboard></Trigger.ExitActions>" +
                "</Trigger>" +
                "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='track' Property='Opacity' Value='0.88'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>";
            return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        public static Style TextBoxStyle()
        {
            Color accent = AccentReadable();
            string focus = "#" + accent.R.ToString("X2") + accent.G.ToString("X2") + accent.B.ToString("X2");
            string xaml =
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TextBox'>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='TextBox'>" +
                "<Border Name='frame' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='12' SnapsToDevicePixels='True'>" +
                "<ScrollViewer Name='PART_ContentHost' Padding='{TemplateBinding Padding}' Focusable='False' VerticalScrollBarVisibility='Hidden' HorizontalScrollBarVisibility='Hidden'/>" +
                "</Border>" +
                "<ControlTemplate.Triggers><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='frame' Property='BorderBrush' Value='" + focus + "'/></Trigger></ControlTemplate.Triggers>" +
                "</ControlTemplate></Setter.Value></Setter></Style>";
            return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        public static Brush ShellSurface()
        {
            Palette p = P;
            byte alpha = 0xFF;
            if (IsImage)
            {
                Brush photo = ImageTheme.Shell(0xFF);
                if (photo != null) return photo;
            }
            LinearGradientBrush shell = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            shell.GradientStops.Add(new GradientStop(WithAlpha(Parse(p.Shell0), alpha), 0));
            shell.GradientStops.Add(new GradientStop(WithAlpha(Parse(p.Shell1), alpha), 0.6));
            shell.GradientStops.Add(new GradientStop(WithAlpha(Parse(p.Shell2), alpha), 1));
            return shell;
        }

        /// <summary>Color base (ABGR) que tiñe el desenfoque del escritorio detrás de la ventana.</summary>
        public static uint BackdropTint()
        {
            Color c = Parse(P.Shell1);
            return ((uint)0x40 << 24) | ((uint)c.B << 16) | ((uint)c.G << 8) | c.R;
        }

        public static Style ScrollBarStyle()
        {
            string thumb = P.Dark ? "#50FFFFFF" : "#40000000";
            string xaml =
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ScrollBar'>" +
                "<Setter Property='Background' Value='Transparent'/><Setter Property='Width' Value='8'/>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>" +
                "<Grid Background='Transparent'><Track Name='PART_Track' IsDirectionReversed='True'>" +
                "<Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False'/></Track.DecreaseRepeatButton>" +
                "<Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False'/></Track.IncreaseRepeatButton>" +
                "<Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>" +
                "<Border Margin='2' CornerRadius='3' MinHeight='24' MinWidth='4' Background='" + thumb + "'/>" +
                "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb></Track></Grid>" +
                "</ControlTemplate></Setter.Value></Setter>" +
                "<Style.Triggers><Trigger Property='Orientation' Value='Horizontal'>" +
                "<Setter Property='Width' Value='Auto'/><Setter Property='Height' Value='8'/>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>" +
                "<Grid Background='Transparent'><Track Name='PART_Track'>" +
                "<Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageLeftCommand' Opacity='0' Focusable='False'/></Track.DecreaseRepeatButton>" +
                "<Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageRightCommand' Opacity='0' Focusable='False'/></Track.IncreaseRepeatButton>" +
                "<Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>" +
                "<Border Margin='2' CornerRadius='3' MinHeight='4' MinWidth='24' Background='" + thumb + "'/>" +
                "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb></Track></Grid>" +
                "</ControlTemplate></Setter.Value></Setter>" +
                "</Trigger></Style.Triggers></Style>";
            return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        public static TextBlock Text(string value, double size, Brush color, FontWeight weight)
        {
            return new TextBlock { Text = value, FontFamily = size >= 18 ? Display : Font, FontSize = size,
                FontWeight = size >= 18 && weight == FontWeights.SemiBold ? FontWeights.Bold : weight,
                Foreground = color, TextWrapping = TextWrapping.Wrap };
        }

        public static TextBlock Text(string value, double size, Brush color)
        {
            return Text(value, size, color, FontWeights.Normal);
        }

        /// <summary>Plantilla de botón en píldora con transición suave al pasar el cursor y al pulsar.</summary>
        public static ControlTemplate PillTemplate(double radius)
        {
            string xaml =
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                "<Border Name='face' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' " +
                "CornerRadius='" + radius.ToString(System.Globalization.CultureInfo.InvariantCulture) + "' Padding='{TemplateBinding Padding}' RenderTransformOrigin='0.5,0.5'>" +
                "<Border.RenderTransform><ScaleTransform x:Name='scale'/></Border.RenderTransform>" +
                "<Grid><Border Name='glow' CornerRadius='" + radius.ToString(System.Globalization.CultureInfo.InvariantCulture) + "' Background='#FFFFFF' Opacity='0' Margin='-1'/>" +
                "<ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center' Margin='8,0,8,0'/></Grid></Border>" +
                "<ControlTemplate.Triggers>" +
                "<Trigger Property='IsMouseOver' Value='True'>" +
                "<Trigger.EnterActions><BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='glow' Storyboard.TargetProperty='Opacity' To='0.12' Duration='0:0:0.18'/></Storyboard></BeginStoryboard></Trigger.EnterActions>" +
                "<Trigger.ExitActions><BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='glow' Storyboard.TargetProperty='Opacity' To='0' Duration='0:0:0.25'/></Storyboard></BeginStoryboard></Trigger.ExitActions>" +
                "</Trigger>" +
                "<Trigger Property='IsPressed' Value='True'>" +
                "<Trigger.EnterActions><BeginStoryboard><Storyboard>" +
                "<DoubleAnimation Storyboard.TargetName='scale' Storyboard.TargetProperty='ScaleX' To='0.96' Duration='0:0:0.09'/>" +
                "<DoubleAnimation Storyboard.TargetName='scale' Storyboard.TargetProperty='ScaleY' To='0.96' Duration='0:0:0.09'/></Storyboard></BeginStoryboard></Trigger.EnterActions>" +
                "<Trigger.ExitActions><BeginStoryboard><Storyboard>" +
                "<DoubleAnimation Storyboard.TargetName='scale' Storyboard.TargetProperty='ScaleX' To='1' Duration='0:0:0.2'><DoubleAnimation.EasingFunction><BackEase EasingMode='EaseOut' Amplitude='0.6'/></DoubleAnimation.EasingFunction></DoubleAnimation>" +
                "<DoubleAnimation Storyboard.TargetName='scale' Storyboard.TargetProperty='ScaleY' To='1' Duration='0:0:0.2'><DoubleAnimation.EasingFunction><BackEase EasingMode='EaseOut' Amplitude='0.6'/></DoubleAnimation.EasingFunction></DoubleAnimation></Storyboard></BeginStoryboard></Trigger.ExitActions>" +
                "</Trigger>" +
                "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>";
            return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
        }

        public static Button Button(string label, bool primary)
        {
            Button button = new Button { Content = label, FontFamily = Font, FontSize = 12.5,
                FontWeight = FontWeights.SemiBold, Cursor = System.Windows.Input.Cursors.Hand,
                MinHeight = 36, Padding = new Thickness(14, 0, 14, 0),
                Background = primary ? Primary : SoftSurface,
                Foreground = primary ? PrimaryText : Ink,
                BorderBrush = primary ? Brushes.Transparent : GlassRim(), BorderThickness = new Thickness(primary ? 0 : 1),
                HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Template = PillTemplate(18);
            return button;
        }

        public static Border Card(UIElement child, Thickness padding)
        {
            return Glass(new Border { Background = CardSurface, CornerRadius = Radius, Padding = padding, Child = child });
        }

        public static DoubleAnimation Animation(double from, double to, int milliseconds)
        {
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd };
        }

        /// <summary>Aparición suave de un panel: fundido y desplazamiento corto.</summary>
        public static void Reveal(FrameworkElement element, bool visible, double offsetX)
        {
            TranslateTransform shift = element.RenderTransform as TranslateTransform;
            if (shift == null) { shift = new TranslateTransform(); element.RenderTransform = shift; }
            if (visible)
            {
                bool wasHidden = element.Visibility != Visibility.Visible;
                element.Visibility = Visibility.Visible;
                if (!wasHidden || !SystemParameters.ClientAreaAnimation) { element.Opacity = 1; shift.X = 0; return; }
                element.BeginAnimation(UIElement.OpacityProperty, Animation(0, 1, 260));
                shift.BeginAnimation(TranslateTransform.XProperty, Animation(offsetX, 0, 320));
            }
            else
            {
                if (element.Visibility != Visibility.Visible) return;
                if (!SystemParameters.ClientAreaAnimation) { element.Visibility = Visibility.Collapsed; return; }
                DoubleAnimation fade = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(140));
                fade.Completed += (s, e) =>
                {
                    if (element.Opacity < 0.05) element.Visibility = Visibility.Collapsed;
                };
                element.BeginAnimation(UIElement.OpacityProperty, fade);
                shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, offsetX * 0.5, TimeSpan.FromMilliseconds(140)));
            }
        }
    }
}
