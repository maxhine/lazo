using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lazo
{
    /// <summary>Vista previa de la reducción de puntos del conversor: la zona tal cual, al lado de cómo quedaría.</summary>
    internal sealed partial class MainWindow
    {
        private CloudPatch _prevPatch;
        private PreviewFrame _prevFrame;
        private bool _prevElevation;
        private double _prevZone = 0.5;
        private bool _prevBusy;
        private string _prevMessage;
        private bool _prevReduced;
        private CancellationTokenSource _prevRun;
        private DispatcherTimer _prevTimer;
        private Image _prevOriginalImage, _prevReducedImage;
        private TextBlock _prevCaption;

        private UIElement CloudPreviewPanel()
        {
            StackPanel section = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock title = Theme.Label("Vista previa de la reducción");
            title.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(title);
            WrapPanel options = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            options.Children.Add(CloudChip("Planta", !_prevElevation, true, () => { _prevElevation = false; RefreshToolsCard(); SchedulePreview(false); }));
            options.Children.Add(CloudChip("Frente", _prevElevation, true, () => { _prevElevation = true; RefreshToolsCard(); SchedulePreview(false); }));
            Grid.SetColumn(options, 1);
            head.Children.Add(options);
            section.Children.Add(head);

            Grid pair = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            pair.ColumnDefinitions.Add(new ColumnDefinition());
            pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            pair.ColumnDefinitions.Add(new ColumnDefinition());
            _prevOriginalImage = PreviewBox(pair, 0, "Original");
            _prevReducedImage = PreviewBox(pair, 2, "Reducida");
            section.Children.Add(pair);

            _prevCaption = Theme.Text("", 10.5, Theme.Muted);
            _prevCaption.TextWrapping = TextWrapping.Wrap;
            _prevCaption.Margin = new Thickness(0, 5, 0, 0);
            section.Children.Add(_prevCaption);

            if (_prevPatch != null && _prevPatch.CanZone)
            {
                WrapPanel zones = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                TextBlock zoneLabel = Theme.Text("Zona del archivo", 10.5, Theme.Muted);
                zoneLabel.VerticalAlignment = VerticalAlignment.Center;
                zoneLabel.Margin = new Thickness(0, 0, 8, 6);
                zones.Children.Add(zoneLabel);
                string[] names = { "Inicio", "Medio", "Final" };
                double[] values = { 0, 0.5, 1 };
                for (int i = 0; i < 3; i++)
                {
                    double zone = values[i];
                    zones.Children.Add(CloudChip(names[i], Math.Abs(_prevZone - zone) < 0.01, true, () => { _prevZone = zone; RefreshToolsCard(); SchedulePreview(true); }));
                }
                section.Children.Add(zones);
            }
            ShowPreview();
            if (_cloudFiles.Count > 0 && _prevFrame == null && !_prevBusy && _prevMessage == null && !_preview) SchedulePreview(false);
            return section;
        }

        private Image PreviewBox(Grid parent, int column, string label)
        {
            Image image = new Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            Border frame = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromRgb(22, 22, 22)),
                ClipToBounds = true,
                MaxHeight = 200,
                Child = image
            };
            Grid cell = new Grid { Margin = new Thickness(0) };
            cell.Children.Add(frame);
            Border tag = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 2, 7, 3),
                Margin = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = Theme.Text(label, 10.5, Brushes.White, FontWeights.SemiBold)
            };
            cell.Children.Add(tag);
            Grid.SetColumn(cell, column);
            parent.Children.Add(cell);
            // Cuadrado: el alto sigue al ancho.
            cell.SizeChanged += (s, e) => { frame.Height = Math.Min(200, e.NewSize.Width); };
            return image;
        }

        /// <summary>Pone en pantalla lo último calculado (o el estado), sin reconstruir el panel.</summary>
        private void ShowPreview()
        {
            if (_prevCaption == null) return;
            if (_prevFrame != null && _prevFrame.Original != null)
            {
                _prevOriginalImage.Source = _prevFrame.Original;
                _prevReducedImage.Source = _prevReduced ? _prevFrame.Reduced : null;
                string window = _prevFrame.WindowMeters >= 1 ? _prevFrame.WindowMeters.ToString("0.0", CultureInfo.CurrentCulture) + " m" : (_prevFrame.WindowMeters * 100).ToString("0", CultureInfo.CurrentCulture) + " cm";
                _prevCaption.Text = _prevReduced
                    ? "Ventana de " + window + ": " + _prevFrame.Before.ToString("N0", CultureInfo.CurrentCulture) + " → " + _prevFrame.After.ToString("N0", CultureInfo.CurrentCulture) +
                      " puntos (queda el " + Math.Round(100.0 * _prevFrame.After / Math.Max(1, _prevFrame.Before)) + " %). Aproximada: ReCap puede elegir otros puntos."
                    : "Ventana de " + window + " con " + _prevFrame.Before.ToString("N0", CultureInfo.CurrentCulture) + " puntos. Escribe una distancia en mm para ver cómo quedaría.";
                if (_prevPatch != null && _prevPatch.Format == ".e57") _prevCaption.Text += " E57: se muestra el inicio del archivo.";
                return;
            }
            _prevOriginalImage.Source = null;
            _prevReducedImage.Source = null;
            _prevCaption.Text = _prevBusy ? "Leyendo una muestra de la nube…" :
                _prevMessage ?? (_cloudFiles.Count == 0 ? "Elige una nube para ver cómo queda la reducción." : "");
        }

        private double PreviewUnitsPerMeter()
        {
            if (_cloudFiles.Count > 0 && _cloudFiles[0].EndsWith(".e57", StringComparison.OrdinalIgnoreCase)) return 1;
            switch (_cloudUnit)
            {
                case 0: return 1000;
                case 1: return 100;
                case 3: return 1 / 0.0254;
                case 4: return 1 / 0.3048;
                case 5: return 1 / 0.3048006096;
                default: return 1;
            }
        }

        /// <summary>Pide recalcular la vista previa; espera un instante por si se sigue escribiendo.</summary>
        private void SchedulePreview(bool reload)
        {
            if (_prevTimer == null)
            {
                _prevTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
                _prevTimer.Tick += (s, e) => { _prevTimer.Stop(); StartPreview(); };
            }
            if (reload) { _prevPatch = null; _prevFrame = null; _prevMessage = null; }
            _prevTimer.Stop();
            _prevTimer.Start();
        }

        private void StartPreview()
        {
            if (_prevRun != null) _prevRun.Cancel();
            if (_cloudFiles.Count == 0)
            {
                _prevPatch = null; _prevFrame = null; _prevMessage = null;
                ShowPreview();
                return;
            }
            double mm = 0;
            string text = (_cloudDecimation ?? "").Trim().Replace(',', '.');
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out mm);
            if (mm < 0) mm = 0;
            string path = _cloudFiles[0];
            string recap = _deCap == null ? null : Path.GetDirectoryName(_deCap);
            double units = PreviewUnitsPerMeter();
            bool elevation = _prevElevation;
            double zone = _prevZone;
            CloudPatch patch = _prevPatch;
            CancellationTokenSource run = new CancellationTokenSource();
            _prevRun = run;
            _prevBusy = patch == null;
            if (patch == null) ShowPreview();
            Task.Run(() =>
            {
                try
                {
                    CloudPatch loaded = patch;
                    if (loaded == null || !string.Equals(loaded.Path, path, StringComparison.OrdinalIgnoreCase) || Math.Abs(loaded.Zone - zone) > 0.001)
                        loaded = CloudPreview.Load(path, zone, recap, run.Token);
                    run.Token.ThrowIfCancellationRequested();
                    PreviewFrame frame = CloudPreview.Render(loaded, mm > 0 ? mm : 10, units, elevation);
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (_prevRun != run) return;
                        _prevPatch = loaded; _prevFrame = frame; _prevReduced = mm > 0; _prevBusy = false; _prevMessage = null;
                        ShowPreview();
                    }));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (_prevRun != run) return;
                        _prevBusy = false; _prevFrame = null;
                        _prevMessage = "No se pudo mostrar la vista previa: " + ex.Message;
                        ShowPreview();
                    }));
                }
            });
        }
    }
}
