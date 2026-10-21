using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lazo
{
    /// <summary>
    /// Mapa de árbol de una carpeta: cada recuadro mide lo que ocupa, con las subcarpetas más grandes dibujadas
    /// dentro. Clic en un recuadro entra en esa carpeta. Distribución «squarified» (recuadros lo más cuadrados posible).
    /// </summary>
    internal sealed class XpaceMap : FrameworkElement
    {
        private sealed class Item
        {
            public XNode Node;
            public string Name;
            public long Size;
            public bool Files, Other;
            public int Hue;
            public Rect Rect;
            public List<Item> Kids = new List<Item>();
        }

        private static readonly string[] Palette = { "#8B6CF6", "#EE5A45", "#14B8A6", "#3B82F6", "#F2A33A", "#F2547D", "#22A55B", "#E879F9" };

        private XNode _node;
        private long _total;
        private List<Item> _items = new List<Item>();
        private Item _hover;
        private Size _laidOut;

        public event Action<XNode> Drill;
        /// <summary>Clic derecho › abrir en el Explorador / quitar una carpeta del mapa.</summary>
        public event Action<string> RevealRequested;
        public event Action<string, long> RemoveRequested;
        /// <summary>Ruta de la carpeta analizada (para construir la de cada recuadro).</summary>
        public string RootPath = "";

        public XpaceMap() { ClipToBounds = true; }

        protected override void OnContextMenuOpening(ContextMenuEventArgs e)
        {
            Item hit = At(Mouse.GetPosition(this));
            if (hit == null || hit.Node == null || hit.Other) { e.Handled = true; return; }
            string path = hit.Node.FullPath(RootPath);
            long size = hit.Size;
            ContextMenu menu = new ContextMenu();
            MenuItem show = new MenuItem { Header = "Abrir en el Explorador" };
            show.Click += (s, a) => { if (RevealRequested != null) RevealRequested(path); };
            menu.Items.Add(show);
            if (!hit.Files && hit.Node.Parent != null)
            {
                MenuItem remove = new MenuItem { Header = "Quitar…" };
                remove.Click += (s, a) => { if (RemoveRequested != null) RemoveRequested(path, size); };
                menu.Items.Add(new Separator());
                menu.Items.Add(remove);
            }
            ContextMenu = menu;
            base.OnContextMenuOpening(e);
        }

        public void Show(XNode node)
        {
            _node = node;
            _hover = null;
            _laidOut = Size.Empty;
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size available)
        {
            return new Size(double.IsInfinity(available.Width) ? 400 : available.Width, double.IsInfinity(available.Height) ? 320 : available.Height);
        }

        private List<Item> Children(XNode node, int hue, bool top)
        {
            List<Item> list = new List<Item>();
            long total = Math.Max(1, node.Size);
            long other = 0;
            int otherCount = 0;
            int index = 0;
            foreach (XNode child in node.Children)
            {
                if (child.Size <= 0) continue;
                if (child.Size < total * 0.004 && list.Count >= 4) { other += child.Size; otherCount++; continue; }
                list.Add(new Item { Node = child, Name = child.Name, Size = child.Size, Hue = top ? index % Palette.Length : hue });
                index++;
            }
            if (node.OwnSize > 0) list.Add(new Item { Node = node, Name = "archivos sueltos", Size = node.OwnSize, Files = true, Hue = -1 });
            if (other > 0) list.Add(new Item { Name = "otros (" + otherCount.ToString("N0", CultureInfo.CurrentCulture) + ")", Size = other, Other = true, Hue = -1 });
            list.Sort((a, b) => b.Size.CompareTo(a.Size));
            return list;
        }

        private void Build(Size size)
        {
            _items = new List<Item>();
            _total = 0;
            if (_node == null || size.Width < 20 || size.Height < 20) return;
            _total = Math.Max(1, _node.Size);
            _items = Children(_node, 0, true);
            Squarify(_items, new Rect(0, 0, size.Width, size.Height));
            foreach (Item item in _items)
            {
                if (item.Node == null || item.Files || item.Other || item.Rect.Width < 120 || item.Rect.Height < 80) continue;
                item.Kids = Children(item.Node, item.Hue, false);
                Rect inner = new Rect(item.Rect.X + 4, item.Rect.Y + 24, item.Rect.Width - 8, item.Rect.Height - 28);
                if (item.Kids.Count > 0) Squarify(item.Kids, inner);
            }
            _laidOut = size;
        }

        /// <summary>Reparte los elementos (ya ordenados de mayor a menor) en el rectángulo.</summary>
        private static void Squarify(List<Item> items, Rect area)
        {
            double total = items.Sum(i => (double)i.Size);
            if (total <= 0 || area.Width <= 0 || area.Height <= 0) return;
            double scale = area.Width * area.Height / total;
            int start = 0;
            Rect free = area;
            while (start < items.Count)
            {
                bool horizontal = free.Width >= free.Height;
                double side = horizontal ? free.Height : free.Width;
                int end = start;
                double sum = 0, best = double.MaxValue;
                while (end < items.Count)
                {
                    double next = sum + items[end].Size * scale;
                    double worst = Worst(items, start, end + 1, next, side, scale);
                    if (worst > best && end > start) break;
                    best = worst;
                    sum = next;
                    end++;
                }
                double thickness = side <= 0 ? 0 : sum / side;
                double offset = 0;
                for (int i = start; i < end; i++)
                {
                    double length = sum <= 0 ? 0 : items[i].Size * scale / sum * side;
                    items[i].Rect = horizontal
                        ? new Rect(free.X, free.Y + offset, thickness, length)
                        : new Rect(free.X + offset, free.Y, length, thickness);
                    offset += length;
                }
                free = horizontal ? new Rect(free.X + thickness, free.Y, Math.Max(0, free.Width - thickness), free.Height)
                                  : new Rect(free.X, free.Y + thickness, free.Width, Math.Max(0, free.Height - thickness));
                start = end;
            }
        }

        private static double Worst(List<Item> items, int start, int end, double sum, double side, double scale)
        {
            double max = 0, min = double.MaxValue;
            for (int i = start; i < end; i++)
            {
                double area = items[i].Size * scale;
                if (area > max) max = area;
                if (area < min) min = area;
            }
            if (min <= 0) return double.MaxValue;
            double s2 = side * side, sum2 = sum * sum;
            return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
        }

        private Item At(Point p)
        {
            foreach (Item item in _items)
            {
                if (!item.Rect.Contains(p)) continue;
                foreach (Item kid in item.Kids) if (kid.Rect.Contains(p)) return kid;
                return item;
            }
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Item hover = At(e.GetPosition(this));
            if (hover == _hover) return;
            _hover = hover;
            ToolTip = hover == null ? null : hover.Name + "\n" + XpaceList.SizeText(hover.Size) + " · " + (hover.Size * 100.0 / _total).ToString("0.#", CultureInfo.CurrentCulture) + " %";
            Cursor = hover != null && hover.Node != null && !hover.Files && hover.Node.Children.Count > 0 ? Cursors.Hand : Cursors.Arrow;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; InvalidateVisual(); }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Item hit = At(e.GetPosition(this));
            if (hit == null || hit.Node == null || hit.Files || hit.Node.Children.Count == 0) return;
            if (Drill != null) Drill(hit.Node);
        }

        private FormattedText Text(string text, double size, Brush brush, double width, FontWeight weight, bool mono = false)
        {
            return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(mono ? Theme.Mono : Theme.Font, FontStyles.Normal, weight, FontStretches.Normal), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { MaxTextWidth = Math.Max(1, width), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        }

        protected override void OnRender(DrawingContext dc)
        {
            Size size = new Size(ActualWidth, ActualHeight);
            if (size != _laidOut) Build(size);
            if (_node == null || _items.Count == 0)
            {
                FormattedText empty = Text("Esta carpeta no tiene contenido para mostrar.", 12, Theme.Muted, size.Width - 24, FontWeights.Normal);
                dc.DrawText(empty, new Point(12, 12));
                return;
            }
            Brush ink = Theme.Ink, muted = Theme.Muted;
            bool dark = Theme.P.Dark;
            foreach (Item item in _items) DrawItem(dc, item, 1, ink, muted, dark);
        }

        private void DrawItem(DrawingContext dc, Item item, int depth, Brush ink, Brush muted, bool dark)
        {
            Rect r = item.Rect;
            if (r.Width < 3 || r.Height < 3) return;
            r = new Rect(r.X + 1.5, r.Y + 1.5, Math.Max(0, r.Width - 3), Math.Max(0, r.Height - 3));
            Color baseColor = item.Hue >= 0 ? Theme.Parse(Palette[item.Hue % Palette.Length]) : (dark ? Color.FromRgb(150, 150, 158) : Color.FromRgb(110, 110, 120));
            byte fillAlpha = (byte)(depth == 1 ? (dark ? 0x30 : 0x2A) : (dark ? 0x40 : 0x38));
            if (item == _hover) fillAlpha = (byte)Math.Min(255, fillAlpha + 0x26);
            Brush fill = new SolidColorBrush(Color.FromArgb(fillAlpha, baseColor.R, baseColor.G, baseColor.B));
            Pen border = new Pen(new SolidColorBrush(Color.FromArgb((byte)(item == _hover ? 0xFF : 0x88), baseColor.R, baseColor.G, baseColor.B)), item == _hover ? 1.6 : 1);
            double radius = depth == 1 ? 8 : 5;
            dc.DrawRoundedRectangle(fill, border, r, radius, radius);

            if (r.Width > 56 && r.Height > 28)
            {
                FormattedText name = Text(item.Name, depth == 1 ? 12 : 11, ink, r.Width - 16, depth == 1 ? FontWeights.SemiBold : FontWeights.Normal);
                dc.DrawText(name, new Point(r.X + 8, r.Y + 6));
                if (depth == 1 && r.Width > 90)
                {
                    FormattedText size = Text(XpaceList.SizeText(item.Size), 10.5, muted, r.Width - 16, FontWeights.Normal, true);
                    // Con hijos dibujados, el tamaño va a la derecha del título; sin ellos, abajo y más grande.
                    if (item.Kids.Count > 0)
                    {
                        if (r.Width > 170 + name.WidthIncludingTrailingWhitespace * 0.0)
                            dc.DrawText(Text(XpaceList.SizeText(item.Size), 10.5, muted, 80, FontWeights.Normal, true), new Point(r.Right - 8 - Math.Min(80, size.WidthIncludingTrailingWhitespace), r.Y + 7));
                    }
                    else if (r.Height > 56)
                    {
                        FormattedText big = Text(XpaceList.SizeText(item.Size), r.Width > 150 && r.Height > 90 ? 15 : 12, ink, r.Width - 16, FontWeights.SemiBold, true);
                        dc.DrawText(big, new Point(r.X + 8, r.Bottom - 8 - big.Height));
                    }
                }
            }
            foreach (Item kid in item.Kids) DrawItem(dc, kid, 2, ink, muted, dark);
        }
    }
}
