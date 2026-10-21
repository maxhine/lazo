using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lazo
{
    /// <summary>
    /// Control remoto con contraseña (docs/control-remoto.md). Anfitrión: ajustes, aviso permanente y sesión sin preguntar.
    /// Visor: tarjeta «Control remoto» en Utilidades para entrar a otro equipo con su contraseña.
    /// </summary>
    internal sealed partial class MainWindow
    {
        /// <summary>Quién está dentro por control remoto con contraseña, o null.</summary>
        private string _remoteName;

        // Visor: lo que se está escribiendo en la tarjeta de Utilidades.
        private Guid _remoteTarget;
        private string _remotePassword = "";
        private string _remoteStatus = "";
        private bool _remoteConnecting;

        // ── Anfitrión ─────────────────────────────────────────────────────

        /// <summary>
        /// Entró alguien con la contraseña correcta: arranca la transmisión de la pantalla principal con esa persona ya
        /// dentro y con todos los permisos. La tarea termina cuando se va; hasta entonces la conexión sigue abierta.
        /// </summary>
        private Task OnRemoteAccess(Guid viewerId, string name, SecureConnection connection)
        {
            TaskCompletionSource<bool> done = new TaskCompletionSource<bool>();
            Dispatcher.BeginInvoke((Action)(() => StartRemoteSession(name, connection, done)));
            return done.Task;
        }

        private void StartRemoteSession(string name, SecureConnection connection, TaskCompletionSource<bool> done)
        {
            ShareSource source = _preview || _share != null ? null : ScreenSources.Primary();
            if (source == null)
            {
                connection.Dispose();
                done.TrySetResult(false);
                return;
            }
            ScreenShareSession session = new ScreenShareSession(_network, source);
            session.Notice += text => Dispatcher.BeginInvoke((Action)(() => ShowToast(text)));
            session.Changed += () => Dispatcher.BeginInvoke((Action)(() => { if (_share == session) UpdateShareStatus(); }));
            session.Ended += () => Dispatcher.BeginInvoke((Action)(() => OnShareEnded(session)));
            ScreenOverlayWindow overlay = new ScreenOverlayWindow(source);
            overlay.Layer.Local += op => session.Annotate(op);
            session.Annotated += op => Dispatcher.BeginInvoke((Action)(() => { if (_shareOverlay == overlay) overlay.Layer.Apply(op); }));
            session.PointerMoved += pointer => Dispatcher.BeginInvoke((Action)(() => { if (_shareOverlay == overlay) overlay.Pointers.Apply(pointer); }));
            session.PermissionRequested += (viewerId, viewerName, kind) => Dispatcher.BeginInvoke((Action)(() => AskSharePermission(session, viewerId, viewerName, kind)));
            session.ViewerLeft += viewerId => Dispatcher.BeginInvoke((Action)(() => DismissPrompts(viewerId)));
            overlay.DrawingChanged += drawing => session.InputPaused = drawing;
            _shareOverlay = overlay;
            _share = session;
            _shareInvited.Clear();
            _remoteName = name;
            if (!session.AttachRemote(connection, name, () => done.TrySetResult(true)))
            {
                _share = null;
                _shareOverlay = null;
                _remoteName = null;
                overlay.Close();
                session.Stop();
                connection.Dispose();
                done.TrySetResult(false);
                return;
            }
            session.Start();
            // No se anuncia a la red como «compartiendo»: nadie debe poder pedir unirse a una sesión privada.
            overlay.Show();
            _shareBar = new ShareBarWindow(StopShare, overlay, session.RevokeAll);
            _shareBar.SetRemote();
            _shareBar.Show();
            UpdateShareStatus();
            RefreshShareCard();
            string message = name + " entró a este equipo por control remoto";
            ShowToast(message);
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            if (_tray != null)
            {
                try { _tray.ShowBalloonTip(6000, "Control remoto", message + ". Ctrl+Alt+X lo termina.", System.Windows.Forms.ToolTipIcon.Warning); }
                catch { }
            }
        }

        /// <summary>Sección «Control remoto» de Ajustes: permitir, contraseña y estado de la sesión.</summary>
        private void AddRemoteAccess(StackPanel panel)
        {
            RemoteAccess remote = _network.Remote;
            TextBlock label = Theme.Label("Control remoto");
            label.Margin = new Thickness(0, 14, 0, 0);
            panel.Children.Add(label);
            TextBlock help = Theme.Text("Entrar a este equipo desde otro de la red con una contraseña, sin que nadie acepte. Mientras alguien esté dentro verás un aviso permanente con «Terminar».", 11, Theme.Muted);
            help.TextWrapping = TextWrapping.Wrap;
            help.Margin = new Thickness(0, 0, 0, 4);
            panel.Children.Add(help);

            CheckBox allow = new CheckBox
            {
                Content = "Permitir control remoto con contraseña",
                IsChecked = remote.Enabled,
                Foreground = Theme.Ink,
                FontFamily = Theme.Font,
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 4)
            };
            TextBlock state = Theme.Text("", 11, Theme.Muted);
            state.TextWrapping = TextWrapping.Wrap;
            PasswordBox box = new PasswordBox
            {
                FontFamily = Theme.Font,
                FontSize = 12.5,
                Foreground = Theme.Ink,
                CaretBrush = Theme.Ink,
                Background = Theme.SoftSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                MaxLength = 64,
                ToolTip = "Mínimo 8 caracteres. Solo se guarda un hash; no se puede leer después."
            };
            Button save = Theme.Button(remote.HasPassword ? "Cambiar contraseña" : "Elegir contraseña", false);
            save.MinHeight = 30;
            save.HorizontalAlignment = HorizontalAlignment.Left;
            save.Margin = new Thickness(0, 6, 0, 0);
            Action refresh = () =>
            {
                allow.IsChecked = remote.Enabled;
                save.Content = remote.HasPassword ? "Cambiar contraseña" : "Elegir contraseña";
                state.Text = !remote.HasPassword ? "Sin contraseña: nadie puede entrar."
                    : remote.Enabled ? "Activado. Quien tenga la contraseña puede entrar y controlar este equipo."
                    : "Desactivado. La contraseña sigue guardada.";
            };
            allow.Checked += (s, e) =>
            {
                if (_preview) return;
                if (!remote.HasPassword) { allow.IsChecked = false; ShowToast("Primero elige una contraseña."); box.Focus(); return; }
                remote.SetEnabled(true);
                refresh();
                ShowToast("Control remoto activado.");
            };
            allow.Unchecked += (s, e) =>
            {
                if (_preview) return;
                remote.SetEnabled(false);
                refresh();
                ShowToast("Control remoto desactivado.");
            };
            save.Click += (s, e) =>
            {
                if (_preview) return;
                try
                {
                    remote.SetPassword(box.Password, true);
                    box.Password = "";
                    refresh();
                    ShowToast("Contraseña guardada y control remoto activado.");
                }
                catch (ArgumentException ex) { ShowToast(ex.Message); }
            };
            panel.Children.Add(allow);
            panel.Children.Add(box);
            panel.Children.Add(save);
            state.Margin = new Thickness(0, 6, 0, 0);
            panel.Children.Add(state);
            refresh();

            if (_remoteName != null)
            {
                Grid live = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                live.ColumnDefinitions.Add(new ColumnDefinition());
                live.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                TextBlock who = Theme.Text("Ahora mismo: " + _remoteName + " está dentro", 12, Theme.Ink, FontWeights.SemiBold);
                who.VerticalAlignment = VerticalAlignment.Center;
                who.TextWrapping = TextWrapping.Wrap;
                live.Children.Add(who);
                Button end = Theme.Button("Terminar", false);
                end.MinHeight = 28;
                end.Click += (s, e) => StopShare();
                Grid.SetColumn(end, 1);
                live.Children.Add(end);
                panel.Children.Add(live);
            }
        }

        // ── Visor ─────────────────────────────────────────────────────────

        private UIElement RemoteTool()
        {
            StackPanel tool = new StackPanel();
            TextBlock intro = Theme.Text("Entra a otro equipo de la red con la contraseña de control remoto que definió su dueño, sin que nadie acepte. Verás su pantalla y tendrás el control; «Ajustar resolución» adapta el otro equipo al tamaño de esta ventana.", 11.5, Theme.Muted);
            intro.TextWrapping = TextWrapping.Wrap;
            intro.Margin = new Thickness(0, 0, 0, 10);
            tool.Children.Add(intro);

            TextBlock who = Theme.Label("Equipo");
            who.Margin = new Thickness(0, 0, 0, 6);
            tool.Children.Add(who);
            List<Peer> peers = _peers.Where(p => p.Address != null).ToList();
            if (peers.Count == 0)
            {
                TextBlock none = Theme.Text("No hay otros equipos con Lazo en la red ahora mismo.", 12, Theme.Muted);
                none.Margin = new Thickness(0, 0, 0, 8);
                tool.Children.Add(none);
            }
            else
            {
                if (!peers.Any(p => p.Id == _remoteTarget)) _remoteTarget = peers[0].Id;
                WrapPanel row = new WrapPanel();
                foreach (Peer peer in peers)
                {
                    Peer target = peer;
                    row.Children.Add(Chip(peer, peer.Id == _remoteTarget, () => { if (_remoteConnecting) return; _remoteTarget = target.Id; RefreshToolsCard(); }));
                }
                tool.Children.Add(row);
            }

            TextBlock passLabel = Theme.Label("Contraseña de control remoto");
            passLabel.Margin = new Thickness(0, 6, 0, 6);
            tool.Children.Add(passLabel);
            PasswordBox box = new PasswordBox
            {
                Password = _remotePassword,
                IsEnabled = !_remoteConnecting,
                FontFamily = Theme.Font,
                FontSize = 12.5,
                Foreground = Theme.Ink,
                CaretBrush = Theme.Ink,
                Background = Theme.SoftSurface,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                MaxWidth = 320,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            box.Width = 320;
            box.PasswordChanged += (s, e) => _remotePassword = box.Password;
            Button connect = Theme.Button(_remoteConnecting ? "Conectando…" : "Conectar", true);
            connect.MinHeight = 32;
            connect.HorizontalAlignment = HorizontalAlignment.Left;
            connect.Margin = new Thickness(0, 10, 0, 0);
            connect.IsEnabled = !_remoteConnecting && peers.Count > 0 && !_preview;
            connect.Click += (s, e) => ConnectRemote();
            box.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter && connect.IsEnabled) ConnectRemote(); };
            tool.Children.Add(box);
            tool.Children.Add(connect);
            if (_remoteStatus.Length > 0)
            {
                TextBlock status = Theme.Text(_remoteStatus, 12, Theme.Ink, FontWeights.Medium);
                status.TextWrapping = TextWrapping.Wrap;
                status.Margin = new Thickness(0, 8, 0, 0);
                tool.Children.Add(status);
            }
            TextBlock note = Theme.Text("Si la contraseña falla cinco veces seguidas, el otro equipo te bloquea diez minutos. Para entrar, el otro equipo debe tener activado el control remoto en Ajustes.", 10.5, Theme.Muted);
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(0, 10, 0, 0);
            tool.Children.Add(note);
            return tool;
        }

        private void ConnectRemote()
        {
            if (_remoteConnecting || _preview) return;
            Peer peer = _peers.FirstOrDefault(p => p.Id == _remoteTarget && p.Address != null);
            if (peer == null) { _remoteStatus = "Elige un equipo."; RefreshToolsCard(); return; }
            string password = _remotePassword;
            if (string.IsNullOrEmpty(password)) { _remoteStatus = "Escribe la contraseña de control remoto."; RefreshToolsCard(); return; }
            _remoteConnecting = true;
            _remoteStatus = "Conectando con " + peer.Name + "…";
            RefreshToolsCard();
            Task connecting = Task.Run(async () =>
            {
                try
                {
                    SecureConnection connection = await _network.ConnectRemoteAsync(peer, password).ConfigureAwait(false);
                    System.Windows.Threading.DispatcherOperation opened = Dispatcher.BeginInvoke((Action)(() => OpenRemoteViewer(peer, connection)));
                }
                catch (Exception ex)
                {
                    string message = ex.Message;
                    System.Windows.Threading.DispatcherOperation failed = Dispatcher.BeginInvoke((Action)(() =>
                    {
                        _remoteConnecting = false;
                        _remoteStatus = message;
                        RefreshToolsCard();
                    }));
                }
            });
        }

        /// <summary>Abre la ventana de pantallas con el equipo remoto, directamente en modo control y con la resolución ajustada.</summary>
        private void OpenRemoteViewer(Peer peer, SecureConnection connection)
        {
            _remoteConnecting = false;
            _remotePassword = "";
            _remoteStatus = "Conectado a " + peer.Name + ". La pantalla se abrió en otra ventana.";
            if (_stage == null)
            {
                ScreenStageWindow stage = new ScreenStageWindow();
                stage.Closed += (s, e) => { if (_stage == stage) _stage = null; };
                _stage = stage;
            }
            ScreenViewerView view = new ScreenViewerView(peer.Name, "Control remoto") { AutoControl = true, AutoFit = true };
            _stage.Add(view);
            if (!_stage.IsVisible) _stage.Show();
            if (_stage.WindowState == WindowState.Minimized) _stage.WindowState = WindowState.Normal;
            _stage.Activate();
            ScreenViewer sink = new ScreenViewer(view);
            Thread reader = new Thread(() =>
            {
                try { sink.Run(connection.Stream); }
                finally { connection.Dispose(); }
            }) { IsBackground = true, Name = "Lazo remote viewer" };
            reader.Start();
            if (_toolsOpen && _toolId == "remote") RefreshToolsCard();
        }
    }
}
