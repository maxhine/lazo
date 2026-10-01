using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Lazo
{
    internal sealed class ReceiveWindow : Window
    {
        private sealed class Item
        {
            public Offer Offer;
            public Action<bool> Respond;
            public bool Decided;
            public bool Done;
            public string Path;
            public TextBlock Label;
        }

        private readonly Border _shell;
        private readonly TextBlock _detail;
        private readonly StackPanel _files;
        private readonly Button _accept;
        private readonly Button _reject;
        private readonly Button _dismiss;
        private readonly ScaleTransform _progress = new ScaleTransform(0, 1);
        private readonly List<Item> _items = new List<Item>();
        private readonly DispatcherTimer _timeout;
        private readonly string _sender;
        private readonly System.Net.IPAddress _address;
        private bool _takeAll;
        private bool _closing;
        public Guid OfferId { get; private set; }
        public string PeerName { get { return _sender; } }
        public System.Net.IPAddress PeerAddress { get { return _address; } }

        public ReceiveWindow(Offer offer, Action<bool> respond)
        {
            OfferId = offer.Id;
            _sender = offer.Sender;
            _address = offer.Address;
            Title = "Lazo · archivo entrante";
            Width = 360;
            Height = 132;
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

            Grid main = new Grid { Margin = new Thickness(12, 10, 12, 12) };
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
            _shell.Child = main;
            main.SizeChanged += (s, e) =>
                main.Clip = new RectangleGeometry(new Rect(1, 1,
                    Math.Max(0, main.ActualWidth - 2), Math.Max(0, main.ActualHeight - 2)), 12, 12);

            Grid body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            _files = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            _detail = Theme.Text("", 11, Theme.Muted);
            _detail.Margin = new Thickness(0, 3, 0, 0);
            _detail.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(_files, 1);
            body.Children.Add(_files);
            main.Children.Add(body);

            Grid buttons = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            _reject = Compact("Rechazar", false);
            _reject.Click += (s, e) =>
            {
                if (_items.Any(item => item.Done && item.Path != null)) { OpenReceived(true); return; }
                RejectPending();
                CloseAnimated();
            };
            buttons.Children.Add(_reject);
            _accept = Compact("Aceptar", true);
            _accept.Click += (s, e) =>
            {
                if (_items.Count == 1 && _items[0].Done && _items[0].Path != null) { OpenReceived(false); return; }
                if (_takeAll) return;
                _takeAll = true;
                AcceptPending();
            };
            Grid.SetColumn(_accept, 2);
            buttons.Children.Add(_accept);
            Grid.SetRow(buttons, 1);
            main.Children.Add(buttons);

            Border track = new Border
            {
                Height = 2,
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsHitTestVisible = false,
                Child = new Border
                {
                    Background = Theme.Ink,
                    RenderTransform = _progress,
                    RenderTransformOrigin = new Point(0, 0.5)
                }
            };
            main.Children.Add(track);

            _dismiss = Compact("×", false);
            _dismiss.Width = 22;
            _dismiss.MinWidth = 0;
            _dismiss.MinHeight = 20;
            _dismiss.Height = 20;
            _dismiss.Padding = new Thickness(0);
            _dismiss.HorizontalAlignment = HorizontalAlignment.Right;
            _dismiss.VerticalAlignment = VerticalAlignment.Top;
            _dismiss.Margin = new Thickness(0, 4, 4, 0);
            _dismiss.ToolTip = "Cerrar";
            _dismiss.Visibility = Visibility.Collapsed;
            _dismiss.Click += (s, e) => CloseAnimated();
            outer.Children.Add(_dismiss);
            KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) CloseAnimated(); };
            Loaded += (s, e) =>
            {
                TranslateTransform rise = new TranslateTransform(0, 18);
                _shell.RenderTransform = rise;
                rise.BeginAnimation(TranslateTransform.YProperty, Theme.Animation(18, 0, 190));
                _shell.BeginAnimation(OpacityProperty, Theme.Animation(0, 1, 150));
            };
            Closing += (s, e) => RejectPending();
            _timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
            _timeout.Tick += (s, e) => { _timeout.Stop(); if (!_takeAll) { RejectPending(); CloseAnimated(); } };
            _timeout.Start();
            Add(offer, respond);
        }

        public bool CanAbsorb(Offer offer)
        {
            if (_closing || offer == null || _address == null || offer.Address == null) return false;
            if (!offer.Address.Equals(_address)) return false;
            if (!_takeAll) return true;
            return _items.Any(item => item.Decided && !item.Done);
        }

        public bool Contains(Guid id)
        {
            return _items.Any(item => item.Offer.Id == id);
        }

        public void Add(Offer offer, Action<bool> respond)
        {
            Item item = new Item
            {
                Offer = offer,
                Respond = respond,
                Label = Theme.Text(offer.FileName, 13, Theme.Ink, FontWeights.SemiBold)
            };
            item.Label.TextTrimming = TextTrimming.CharacterEllipsis;
            _items.Add(item);
            RefreshList();
            Fit();
            if (_takeAll) AcceptPending();
            else { _timeout.Stop(); _timeout.Start(); }
        }

        private void RefreshList()
        {
            _files.Children.Clear();
            ImageSource preview = null;
            foreach (Item item in _items)
            {
                if (preview == null) preview = Decode(item.Offer.Preview);
                _files.Children.Add(item.Label);
            }
            long total = _items.Sum(item => item.Offer.Size);
            string count = _items.Count == 1 ? MainWindow.FormatSize(total) : _items.Count + " archivos · " + MainWindow.FormatSize(total);
            _detail.Text = count + "  ·  " + _sender;
            _files.Children.Add(_detail);
            _accept.Content = _items.Count > 1 ? "Aceptar todos" : "Aceptar";
            Grid body = (Grid)((Grid)_shell.Child).Children[0];
            if (body.Children.Count > 1 && body.Children[0] is Border) body.Children.RemoveAt(0);
            if (preview != null && (body.Children.Count == 0 || !(body.Children[0] is Border)))
            {
                Border frame = new Border
                {
                    Width = 52,
                    Height = 52,
                    CornerRadius = new CornerRadius(6),
                    BorderBrush = Theme.Line,
                    BorderThickness = new Thickness(1),
                    ClipToBounds = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new Image { Source = preview, Stretch = Stretch.UniformToFill }
                };
                body.Children.Insert(0, frame);
            }
        }

        private void Fit()
        {
            Height = Math.Min(280, 108 + _items.Count * 18);
        }

        private void AcceptPending()
        {
            _takeAll = true;
            _timeout.Stop();
            foreach (Item item in _items.Where(entry => !entry.Decided).ToList())
            {
                item.Decided = true;
                item.Respond(true);
            }
            _accept.IsEnabled = false;
            _reject.IsEnabled = false;
            _detail.Text = "Guardando " + _items.Count + "…";
        }

        private void RejectPending()
        {
            foreach (Item item in _items.Where(entry => !entry.Decided).ToList())
            {
                item.Decided = true;
                item.Respond(false);
            }
            _timeout.Stop();
        }

        private static ImageSource Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = new MemoryStream(bytes);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 112;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        }

        private static Button Compact(string label, bool primary)
        {
            Button button = Theme.Button(label, primary);
            button.MinHeight = 28;
            button.FontSize = 11;
            button.Padding = new Thickness(8, 0, 8, 0);
            return button;
        }

        public void SetProgress(double value)
        {
            if (_items.Count == 0) return;
            SetFileProgress(_items[0].Offer.Id, value);
        }

        public void SetFileProgress(Guid id, double value)
        {
            if (value < 0) value = 0;
            if (value > 1) value = 1;
            Item item = _items.FirstOrDefault(entry => entry.Offer.Id == id);
            if (item != null) item.Label.Opacity = 0.55 + value * 0.45;
            double average = _items.Count == 0 ? value : _items.Average(entry => entry.Done ? 1 : entry.Offer.Id == id ? value : 0);
            _progress.BeginAnimation(ScaleTransform.ScaleXProperty, Theme.Animation(_progress.ScaleX, average, 90));
            int done = _items.Count(entry => entry.Done);
            _detail.Text = done + " de " + _items.Count + "  ·  " + (average * 100).ToString("0") + "%";
        }

        public void Finish(string message, string path)
        {
            if (_items.Count == 0) return;
            FinishFile(_items[0].Offer.Id, message, path);
            ShowResult();
        }

        public void FinishFile(Guid id, string message, string path)
        {
            Item item = _items.FirstOrDefault(entry => entry.Offer.Id == id);
            if (item == null) return;
            item.Done = true;
            item.Path = string.IsNullOrEmpty(path) ? null : path;
            item.Label.Opacity = item.Path == null ? 0.4 : 1;
            if (_items.All(entry => !entry.Decided || entry.Done)) ShowResult();
            else _detail.Text = _items.Count(entry => entry.Done) + " de " + _items.Count;
        }

        private void ShowResult()
        {
            int saved = _items.Count(item => item.Path != null);
            bool success = saved > 0;
            _detail.Text = success
                ? (saved == 1 ? "Archivo recibido" : saved + " archivos guardados")
                : "No se pudo guardar";
            _progress.BeginAnimation(ScaleTransform.ScaleXProperty, Theme.Animation(_progress.ScaleX, success ? 1 : 0, 120));
            _reject.Content = success ? "Mostrar en carpeta" : "Cerrar";
            _accept.Content = "Abrir";
            _reject.IsEnabled = true;
            _accept.IsEnabled = saved == 1;
            _dismiss.Visibility = Visibility.Visible;
        }

        private void OpenReceived(bool showInFolder)
        {
            try
            {
                string path = _items.Select(item => item.Path).FirstOrDefault(item => item != null && File.Exists(item));
                if (path == null) throw new FileNotFoundException("El archivo ya no está en la carpeta de recepción.");
                if (showInFolder)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                        Arguments = "/select,\"" + path + "\"",
                        UseShellExecute = true
                    });
                else
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                CloseAnimated();
            }
            catch (Exception ex)
            {
                _detail.Text = "No se pudo abrir el archivo";
                _detail.ToolTip = ex.Message;
            }
        }

        private void CloseAnimated()
        {
            if (_closing) return;
            _closing = true;
            RejectPending();
            DoubleAnimation fade = Theme.Animation(1, 0, 120);
            fade.Completed += (s, e) => Close();
            _shell.BeginAnimation(OpacityProperty, fade);
            TranslateTransform rise = _shell.RenderTransform as TranslateTransform;
            if (rise != null) rise.BeginAnimation(TranslateTransform.YProperty, Theme.Animation(0, 14, 130));
        }
    }
}
