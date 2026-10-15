using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;

namespace Lazo
{
    /// <summary>
    /// Puntero de alguien que mira y dibuja o controla, reenviado por quien comparte a los demás (canal de retorno v2):
    /// 'M' u16 longitud + [i32 id · u8 visible · u16 x · u16 y · u8 color · nombre UTF-8].
    /// </summary>
    internal sealed class PointerUpdate
    {
        public int Id;
        public bool Visible;
        public ushort X, Y;
        public byte Color;
        public string Name = "";

        public byte[] ToBytes()
        {
            using (MemoryStream buffer = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(buffer))
            {
                writer.Write(Id);
                writer.Write((byte)(Visible ? 1 : 0));
                writer.Write(X);
                writer.Write(Y);
                writer.Write(Color);
                byte[] name = Encoding.UTF8.GetBytes(Name ?? "");
                writer.Write(name, 0, Math.Min(name.Length, 180));
                writer.Flush();
                return buffer.ToArray();
            }
        }

        public static PointerUpdate Parse(byte[] data)
        {
            if (data == null || data.Length < 10) return null;
            PointerUpdate update = new PointerUpdate
            {
                Id = BitConverter.ToInt32(data, 0),
                Visible = data[4] != 0,
                X = BitConverter.ToUInt16(data, 5),
                Y = BitConverter.ToUInt16(data, 7),
                Color = data[9]
            };
            try { update.Name = new UTF8Encoding(false, true).GetString(data, 10, Math.Min(data.Length - 10, 180)); }
            catch (ArgumentException) { update.Name = ""; }
            return update;
        }
    }

    /// <summary>Punteros con nombre, como en una pizarra colaborativa. Cada uno se desvanece si deja de moverse.</summary>
    internal sealed class PointerLayer : Canvas
    {
        public static readonly Color[] Palette =
        {
            Color.FromRgb(0x4C, 0x5F, 0xF2), Color.FromRgb(0x2F, 0xB5, 0x6E), Color.FromRgb(0xE0, 0x4E, 0x8A),
            Color.FromRgb(0xF2, 0x8C, 0x28), Color.FromRgb(0x8E, 0x5C, 0xE6), Color.FromRgb(0x17, 0xA8, 0xB8)
        };

        private const double IdleSeconds = 5;

        private sealed class Pointer
        {
            public Canvas View;
            public TextBlock Label;
            public Border Tag;
            public Path Arrow;
            public DateTime Moved;
            public bool Shown;
        }

        private readonly Dictionary<int, Pointer> _pointers = new Dictionary<int, Pointer>();
        private readonly DispatcherTimer _idle;
        private double _scale = 1;

        public PointerLayer()
        {
            IsHitTestVisible = false;
            ClipToBounds = true;
            _idle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _idle.Tick += (s, e) => FadeIdle();
        }

        /// <summary>Tamaño de los punteros respecto a la superficie (el visor escala la imagen).</summary>
        public double PointerScale
        {
            get { return _scale; }
            set
            {
                _scale = value;
                foreach (Pointer pointer in _pointers.Values) pointer.View.LayoutTransform = new ScaleTransform(value, value);
            }
        }

        public int Count { get { int n = 0; foreach (Pointer p in _pointers.Values) if (p.Shown) n++; return n; } }

        public string NameOf(int id)
        {
            Pointer pointer;
            return _pointers.TryGetValue(id, out pointer) && pointer.Shown ? pointer.Label.Text : null;
        }

        public void Apply(PointerUpdate update)
        {
            if (update.Visible) Move(update.Id, update.Name, update.X / 65535.0, update.Y / 65535.0, update.Color);
            else Hide(update.Id);
        }

        public void Move(int id, string name, double x, double y, byte color)
        {
            Pointer pointer;
            if (!_pointers.TryGetValue(id, out pointer))
            {
                pointer = Build(color);
                _pointers[id] = pointer;
                Children.Add(pointer.View);
            }
            pointer.Label.Text = string.IsNullOrWhiteSpace(name) ? "Invitado" : name;
            SolidColorBrush brush = new SolidColorBrush(Palette[color % Palette.Length]);
            pointer.Arrow.Fill = brush;
            pointer.Tag.Background = brush;
            SetLeft(pointer.View, x * ActualWidth);
            SetTop(pointer.View, y * ActualHeight);
            pointer.Moved = DateTime.UtcNow;
            if (!pointer.Shown) { pointer.Shown = true; Fade(pointer.View, 1, 120); }
            if (!_idle.IsEnabled) _idle.Start();
        }

        public void Hide(int id)
        {
            Pointer pointer;
            if (!_pointers.TryGetValue(id, out pointer) || !pointer.Shown) return;
            pointer.Shown = false;
            Fade(pointer.View, 0, 200);
        }

        public void HideAll()
        {
            foreach (int id in new List<int>(_pointers.Keys)) Hide(id);
        }

        private void FadeIdle()
        {
            bool any = false;
            foreach (KeyValuePair<int, Pointer> entry in _pointers)
            {
                if (!entry.Value.Shown) continue;
                any = true;
                if ((DateTime.UtcNow - entry.Value.Moved).TotalSeconds > IdleSeconds) Hide(entry.Key);
            }
            if (!any) _idle.Stop();
        }

        private Pointer Build(byte color)
        {
            Path arrow = new Path
            {
                Data = Geometry.Parse("M0,0 L0,17 L4.5,12.8 L7.6,19.6 L10.3,18.4 L7.3,11.8 L13,11.8 Z"),
                Stroke = Brushes.White,
                StrokeThickness = 1.4,
                StrokeLineJoin = PenLineJoin.Round
            };
            TextBlock label = new TextBlock
            {
                Foreground = Brushes.White,
                FontFamily = Theme.Font,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold
            };
            Border tag = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9, 3, 9, 4),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                Child = label
            };
            SetLeft(tag, 13);
            SetTop(tag, 19);
            Canvas view = new Canvas { Opacity = 0, LayoutTransform = new ScaleTransform(_scale, _scale) };
            arrow.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 5, ShadowDepth = 1, Opacity = 0.35 };
            tag.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.3 };
            view.Children.Add(arrow);
            view.Children.Add(tag);
            return new Pointer { View = view, Label = label, Tag = tag, Arrow = arrow };
        }

        private static void Fade(UIElement element, double to, int ms)
        {
            element.BeginAnimation(OpacityProperty, Theme.Animation(element.Opacity, to, ms));
        }
    }
}
