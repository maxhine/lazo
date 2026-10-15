using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lazo
{
    /// <summary>
    /// Verificación opcional: las dos personas ven el mismo código de 6 cifras y lo confirman.
    /// Si el código no coincide, alguien se interpuso en la red entre los dos equipos.
    /// </summary>
    internal sealed class PairingWindow : Window
    {
        private readonly TaskCompletionSource<bool> _result = new TaskCompletionSource<bool>();
        private readonly TextBlock _state;
        private readonly Button _accept;

        public Task<bool> Result { get { return _result.Task; } }

        public PairingWindow(PairingRequest request, int stack)
        {
            Title = "Lazo · verificar";
            Width = 380;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowActivated = !request.Incoming;
            ShowInTaskbar = false;
            FontFamily = Theme.Font;

            StackPanel body = new StackPanel();
            body.Children.Add(Theme.Text(request.Incoming
                ? request.PeerName + " quiere verificar la conexión con este equipo"
                : "Verificar a " + request.PeerName, 13, Theme.Ink, FontWeights.SemiBold));
            TextBlock help = Theme.Text("Comprueba con la otra persona que ve exactamente el mismo código. Si no coincide, cancela: alguien se interpuso en la red.", 11.5, Theme.Muted);
            help.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(help);
            TextBlock code = Theme.Text(request.Code.Substring(0, 3) + " " + request.Code.Substring(3), 34, Theme.Ink, FontWeights.SemiBold);
            code.HorizontalAlignment = HorizontalAlignment.Center;
            code.Margin = new Thickness(0, 12, 0, 4);
            body.Children.Add(code);
            TextBlock print = Theme.Text("Huella " + LazoCrypto.ShortFingerprint(request.Fingerprint), 10.5, Theme.Muted);
            print.HorizontalAlignment = HorizontalAlignment.Center;
            body.Children.Add(print);
            _state = Theme.Text("", 11.5, Theme.Muted);
            _state.HorizontalAlignment = HorizontalAlignment.Center;
            _state.Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(_state);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            Button cancel = Theme.Button("Cancelar", false);
            cancel.Click += (s, e) => Finish(false);
            _accept = Theme.Button("Coincide", true);
            _accept.Margin = new Thickness(8, 0, 0, 0);
            _accept.Click += (s, e) => Confirm();
            buttons.Children.Add(cancel);
            buttons.Children.Add(_accept);
            body.Children.Add(buttons);
            Content = new Border
            {
                Margin = new Thickness(10),
                Background = Theme.ShellSurface(),
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(16, 14, 14, 12),
                Child = body
            };
            DispatcherTimer timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(85) };
            timeout.Tick += (s, e) => { timeout.Stop(); Finish(false); };
            timeout.Start();
            // El código no debe verse en una pantalla compartida: solo quien está delante lo compara.
            SourceInitialized += (s, e) =>
            {
                try { Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11); } catch { }
            };
            Loaded += (s, e) => WindowPlacement.PlaceBottomRight(this, stack);
            Closed += (s, e) => { timeout.Stop(); Finish(false); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Finish(false); };
            _peerName = request.PeerName;
            request.Remote.ContinueWith(t =>
            {
                bool yes = t.Status == TaskStatus.RanToCompletion && t.Result;
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    _remote = yes ? 1 : 0;
                    if (!yes)
                    {
                        _state.Text = _result.Task.IsCompleted && _result.Task.Result ? "No se verificó." : _peerName + " canceló.";
                        Finish(false);
                        CloseSoon();
                    }
                    else if (_result.Task.IsCompleted && _result.Task.Result) Paired();
                    else _state.Text = _peerName + " ya confirmó.";
                }));
            });
        }

        private readonly string _peerName;
        private int _remote = -1;

        private void Paired()
        {
            _state.Text = "Equipo verificado.";
            CloseSoon();
        }

        private void Confirm()
        {
            if (_result.Task.IsCompleted) return;
            _result.TrySetResult(true);
            _accept.IsEnabled = false;
            if (_remote == 1) Paired();
            else _state.Text = "Esperando a " + _peerName + "…";
        }

        private void CloseSoon()
        {
            DispatcherTimer later = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            later.Tick += (s, e) => { later.Stop(); if (IsVisible) Close(); };
            later.Start();
        }

        private void Finish(bool ok)
        {
            _result.TrySetResult(ok);
            if (!ok && IsVisible) CloseSoon();
        }
    }
}
