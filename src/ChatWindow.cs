using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Lazo
{
    /// <summary>
    /// Chat al estilo de Discord: conversaciones a la izquierda; a la derecha, cabecera con acciones
    /// (pantalla compartida, zumbido), mensajes agrupados por autor y una barra de escritura en píldora.
    /// </summary>
    internal sealed class ChatPanel : Grid
    {
        private readonly NetworkEngine _network;
        private Func<Peer, string, Task> _sendChatAsync;
        private readonly Action _unreadChanged;
        private readonly Action _requestClose;
        private readonly Action _nudgeFx;
        private readonly StackPanel _people;
        private readonly StackPanel _messages;
        private readonly ScrollViewer _messageScroll;
        private readonly TextBox _composer;
        private readonly TextBlock _placeholder;
        private readonly TextBlock _title;
        private readonly TextBlock _state;
        private readonly Border _headFace;
        private readonly Border _presenceDot;
        private readonly TextBlock _typingLine;
        private readonly Grid _thread;
        private readonly StackPanel _emptyState;
        private readonly Button _send;
        private readonly Button _shareButton;
        private readonly System.Windows.Shapes.Path _shareIcon;
        private readonly DispatcherTimer _presence;
        private readonly DispatcherTimer _typingStop;
        private readonly DispatcherTimer _pulse;
        private readonly Dictionary<Guid, DateTime> _inChat = new Dictionary<Guid, DateTime>();
        private readonly Dictionary<Guid, DateTime> _typing = new Dictionary<Guid, DateTime>();
        private readonly Dictionary<Guid, string> _drafts = new Dictionary<Guid, string>();
        private List<Peer> _peers = new List<Peer>();
        private Guid _open;
        private string _openName = "";
        private bool _active;
        private bool _sending;
        private bool _loadingDraft;
        private int _shownCount;
        private DateTime _typingSentUtc = DateTime.MinValue;

        // Texto de los mensajes en Segoe UI normal: Bahnschrift se ve demasiado grueso en párrafos.
        private static readonly FontFamily BodyFont = new FontFamily("Segoe UI");

        public event Action<Peer, string[]> SendFiles;
        /// <summary>Pide a la ventana principal compartir pantalla con esta persona (o detenerlo).</summary>
        public event Action<Peer> ShareScreen;
        /// <summary>Indica si ya se está compartiendo pantalla con una persona.</summary>
        public Func<Guid, bool> SharingWith;

        public ChatPanel(NetworkEngine network, Action unreadChanged, Action requestClose, Action nudgeFx)
        {
            _network = network;
            if (network != null) _sendChatAsync = network.SendChatAsync;
            _unreadChanged = unreadChanged;
            _requestClose = requestClose;
            _nudgeFx = nudgeFx;
            Background = Brushes.Transparent;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            ColumnDefinitions.Add(new ColumnDefinition());

            // Lista de conversaciones.
            Border rail = new Border { Background = Theme.SegmentTrack, CornerRadius = new CornerRadius(22, 0, 0, 22) };
            _people = new StackPanel { Margin = new Thickness(8, 10, 8, 10) };
            rail.Child = new ScrollViewer { Content = _people, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Children.Add(rail);

            Grid talk = new Grid();
            Grid.SetColumn(talk, 1);
            Children.Add(talk);
            _thread = new Grid();
            talk.Children.Add(_thread);
            _thread.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });
            _thread.RowDefinitions.Add(new RowDefinition());
            _thread.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Cabecera: avatar, nombre y estado apilados (sin superponerse) y acciones a la derecha.
            Grid header = new Grid { Margin = new Thickness(16, 0, 12, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid faceHost = new Grid { Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center };
            _headFace = new Border { CornerRadius = new CornerRadius(18), Background = Theme.AvatarSurface };
            faceHost.Children.Add(_headFace);
            _presenceDot = new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Theme.Online,
                BorderBrush = Theme.Color(Theme.P.Track), BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -1, -1)
            };
            faceHost.Children.Add(_presenceDot);
            header.Children.Add(faceHost);
            StackPanel names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
            _title = Theme.Text("", 15, Theme.Ink, FontWeights.SemiBold);
            _title.TextTrimming = TextTrimming.CharacterEllipsis;
            _title.TextWrapping = TextWrapping.NoWrap;
            names.Children.Add(_title);
            _state = Theme.Text("", 11.5, Theme.Muted);
            _state.TextWrapping = TextWrapping.NoWrap;
            _state.TextTrimming = TextTrimming.CharacterEllipsis;
            names.Children.Add(_state);
            Grid.SetColumn(names, 1);
            header.Children.Add(names);
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _shareButton = HeaderAction("screen", "Compartir pantalla", ToggleShare, out _shareIcon);
            actions.Children.Add(_shareButton);
            System.Windows.Shapes.Path ignored;
            actions.Children.Add(HeaderAction("zap", "Zumbido", SendNudge, out ignored));
            Border divider = new Border { Width = 1, Height = 22, Background = Theme.Line, Margin = new Thickness(6, 0, 6, 0) };
            actions.Children.Add(divider);
            actions.Children.Add(HeaderAction("close", "Cerrar", () => { if (_requestClose != null) _requestClose(); }, out ignored));
            Grid.SetColumn(actions, 2);
            header.Children.Add(actions);
            _thread.Children.Add(header);
            Border rule = new Border { Height = 1, Background = Theme.Line, VerticalAlignment = VerticalAlignment.Bottom };
            _thread.Children.Add(rule);

            _messages = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            _messageScroll = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(_messageScroll, 1);
            _thread.Children.Add(_messageScroll);

            // Barra de escritura: adjuntar, texto y enviar dentro de una píldora de vidrio.
            StackPanel bottom = new StackPanel { Margin = new Thickness(14, 0, 14, 10) };
            Border composerShell = Theme.Glass(new Border
            {
                Background = Theme.SoftSurface,
                CornerRadius = new CornerRadius(22),
                MinHeight = 44,
                Padding = new Thickness(6, 4, 6, 4)
            });
            Grid composer = new Grid();
            composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            composer.ColumnDefinitions.Add(new ColumnDefinition());
            composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button attach = Round("plus", "Enviar archivo", PickFile, false);
            attach.VerticalAlignment = VerticalAlignment.Bottom;
            composer.Children.Add(attach);
            Grid field = new Grid { Margin = new Thickness(8, 0, 8, 0) };
            _placeholder = Theme.Text("", 13.5, Theme.Muted);
            _placeholder.FontFamily = BodyFont;
            _placeholder.VerticalAlignment = VerticalAlignment.Center;
            _placeholder.IsHitTestVisible = false;
            _placeholder.TextTrimming = TextTrimming.CharacterEllipsis;
            _placeholder.TextWrapping = TextWrapping.NoWrap;
            _placeholder.Margin = new Thickness(2, 0, 0, 0);
            field.Children.Add(_placeholder);
            _composer = new TextBox
            {
                FontFamily = BodyFont, FontWeight = FontWeights.Normal,
                FontSize = 13.5,
                Foreground = Theme.Ink,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 8, 0, 8),
                VerticalContentAlignment = VerticalAlignment.Center,
                CaretBrush = Theme.Ink,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
                MaxHeight = 120
            };
            _composer.TextChanged += (s, e) =>
            {
                _placeholder.Visibility = _composer.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                OnComposerChanged();
            };
            _composer.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    int caret = _composer.CaretIndex;
                    _composer.Text = _composer.Text.Insert(caret, Environment.NewLine);
                    _composer.CaretIndex = caret + Environment.NewLine.Length;
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    SendCurrent();
                    e.Handled = true;
                }
            };
            field.Children.Add(_composer);
            Grid.SetColumn(field, 1);
            composer.Children.Add(field);
            _send = Round("send", "Enviar", SendCurrent, true);
            _send.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(_send, 2);
            composer.Children.Add(_send);
            composerShell.Child = composer;
            bottom.Children.Add(composerShell);
            _typingLine = Theme.Text("", 11, Theme.Muted);
            _typingLine.Margin = new Thickness(14, 4, 0, 0);
            _typingLine.Height = 15;
            bottom.Children.Add(_typingLine);
            Grid.SetRow(bottom, 2);
            _thread.Children.Add(bottom);

            _emptyState = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Border bubble = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = Theme.NavActive, HorizontalAlignment = HorizontalAlignment.Center };
            bubble.Child = Icons.Make("chat", 28, Theme.NavActiveText, 1.8);
            _emptyState.Children.Add(bubble);
            TextBlock pick = Theme.Text("Elige una conversación", 14, Theme.Muted, FontWeights.SemiBold);
            pick.Margin = new Thickness(0, 12, 0, 0);
            pick.HorizontalAlignment = HorizontalAlignment.Center;
            _emptyState.Children.Add(pick);
            Button closeEmpty = Round("close", "Cerrar", () => { if (_requestClose != null) _requestClose(); }, false);
            closeEmpty.HorizontalAlignment = HorizontalAlignment.Right;
            closeEmpty.VerticalAlignment = VerticalAlignment.Top;
            closeEmpty.Margin = new Thickness(0, 12, 12, 0);
            talk.Children.Add(_emptyState);
            talk.Children.Add(closeEmpty);
            closeEmpty.Visibility = Visibility.Visible;
            _thread.IsVisibleChanged += (s, e) => closeEmpty.Visibility = _thread.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            _thread.Visibility = Visibility.Collapsed;

            _presence = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _presence.Tick += (s, e) => { BroadcastPresence(true); RefreshStatus(); };
            _typingStop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _typingStop.Tick += (s, e) => { _typingStop.Stop(); SignalOpen(NetworkEngine.ChatKindTyping, "0"); };
            _pulse = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _pulse.Tick += (s, e) => RefreshStatus();
            _pulse.Start();
            if (_network != null) _network.PeersChanged += OnPeers;
            Loaded += (s, e) => { if (_network != null) OnPeers(_network.Snapshot()); else RenderPeople(); };
            Unloaded += (s, e) =>
            {
                _presence.Stop();
                _typingStop.Stop();
                _pulse.Stop();
                if (_network != null) _network.PeersChanged -= OnPeers;
            };
            SizeChanged += (s, e) =>
                Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 22, 22);
        }

        public bool Viewing(Guid id) { return _active && _open == id; }

        public void SetActive(bool active)
        {
            if (_active == active) return;
            _active = active;
            if (active)
            {
                BroadcastPresence(true);
                _presence.Start();
                if (_network != null) OnPeers(_network.Snapshot());
                if (_open != Guid.Empty) Dispatcher.BeginInvoke((Action)(() => _composer.Focus()), DispatcherPriority.Input);
            }
            else
            {
                _presence.Stop();
                BroadcastPresence(false);
                SignalOpen(NetworkEngine.ChatKindTyping, "0");
            }
        }

        /// <summary>Abre la conversación con una persona (por ejemplo, desde su tarjeta).</summary>
        public void Open(Guid id, string name)
        {
            ShowThread(id, name);
        }

        public void Incoming(Guid id, string name, string text)
        {
            if (_open == id) ShowThread(id, name);
            else RenderPeople();
        }

        public void NoteSignal(Guid id, string name, byte kind, string body)
        {
            if (kind == NetworkEngine.ChatKindTyping)
            {
                if (body == "1") _typing[id] = DateTime.UtcNow.AddSeconds(3);
                else _typing.Remove(id);
            }
            else if (kind == NetworkEngine.ChatKindPresence)
            {
                if (body == "1") _inChat[id] = DateTime.UtcNow.AddSeconds(9);
                else _inChat.Remove(id);
            }
            else if (kind == NetworkEngine.ChatKindNudge)
            {
                ChatStore.AppendNudge(id, name, false);
                if (_nudgeFx != null) _nudgeFx();
                if (_open == id) ShowThread(id, name);
            }
            RefreshStatus();
            RenderPeople();
        }

        public void RefreshOpen()
        {
            if (_open != Guid.Empty) ShowThread(_open, _openName);
            else RenderPeople();
            RefreshShareButton();
        }

        private void OnPeers(List<Peer> peers)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                _peers = peers ?? new List<Peer>();
                RenderPeople();
                RefreshStatus();
            }));
        }

        private void RenderPeople()
        {
            _people.Children.Clear();
            TextBlock heading = Theme.Text("MENSAJES DIRECTOS", 10.5, Theme.Muted, FontWeights.SemiBold);
            heading.Margin = new Thickness(10, 4, 0, 6);
            _people.Children.Add(heading);
            Dictionary<Guid, ChatThread> known = ChatStore.Threads().ToDictionary(item => item.Id);
            List<Guid> shown = new List<Guid>();
            foreach (Peer peer in _peers.OrderBy(item => item.Name))
            {
                shown.Add(peer.Id);
                ChatThread thread;
                known.TryGetValue(peer.Id, out thread);
                _people.Children.Add(Person(peer.Id, peer.Name, peer.Photo, thread == null ? "" : thread.Preview, thread == null ? 0 : thread.Unread, peer.Address != null));
            }
            foreach (ChatThread thread in known.Values.OrderByDescending(item => item.When).Where(item => !shown.Contains(item.Id)))
                _people.Children.Add(Person(thread.Id, thread.Name, "", thread.Preview, thread.Unread, false));
            if (_people.Children.Count == 1)
            {
                TextBlock empty = Theme.Text("Nadie conectado", 12, Theme.Muted);
                empty.Margin = new Thickness(10, 6, 8, 0);
                _people.Children.Add(empty);
            }
        }

        private Border Person(Guid id, string name, string photo, string preview, int unread, bool reachable)
        {
            Grid row = new Grid { Margin = new Thickness(8, 6, 8, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(Face(name, photo, 34, reachable, InChat(id)));
            StackPanel labels = new StackPanel { Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock title = Theme.Text(name, 13, unread > 0 || id == _open ? Theme.Ink : Theme.Color(Theme.P.Dark ? "#E6FFFFFF" : "#DD000000"), unread > 0 ? FontWeights.Bold : FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.TextWrapping = TextWrapping.NoWrap;
            labels.Children.Add(title);
            string presence = InChat(id) ? "En el chat" : reachable ? "En línea" : "Desconectado";
            TextBlock sub = Theme.Text(string.IsNullOrEmpty(preview) ? presence : preview, 11, Theme.Muted);
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            sub.TextWrapping = TextWrapping.NoWrap;
            labels.Children.Add(sub);
            Grid.SetColumn(labels, 1);
            row.Children.Add(labels);
            if (unread > 0)
            {
                Border badge = new Border { Background = Theme.Danger, CornerRadius = new CornerRadius(9), MinWidth = 18, Height = 18, Padding = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
                badge.Child = new TextBlock { Text = unread > 99 ? "99+" : unread.ToString(), FontFamily = Theme.Font, FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(badge, 2);
                row.Children.Add(badge);
            }
            bool selected = id == _open;
            Border item = new Border
            {
                Child = row,
                CornerRadius = new CornerRadius(12),
                Background = selected ? Theme.NavActive : Brushes.Transparent,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 1, 0, 1)
            };
            if (!selected)
            {
                Brush hover = Theme.Color(Theme.P.Dark ? "#12FFFFFF" : "#0D000000");
                item.MouseEnter += (s, e) => item.Background = hover;
                item.MouseLeave += (s, e) => item.Background = Brushes.Transparent;
            }
            item.MouseLeftButtonUp += (s, e) => ShowThread(id, name);
            return item;
        }

        /// <summary>Avatar circular con foto o iniciales y punto de presencia.</summary>
        private static Grid Face(string name, string photo, double size, bool online, bool inChat)
        {
            Grid host = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            Border circle = new Border { CornerRadius = new CornerRadius(size / 2), Background = Theme.AvatarSurface, ClipToBounds = true };
            circle.Child = Photo(name, photo, size);
            host.Children.Add(circle);
            if (online)
            {
                host.Children.Add(new Border
                {
                    Width = 12, Height = 12, CornerRadius = new CornerRadius(6),
                    Background = inChat ? Theme.SelectionFill : Theme.Online,
                    BorderBrush = Theme.Color(Theme.P.Track), BorderThickness = new Thickness(2),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2)
                });
            }
            return host;
        }

        private static FrameworkElement Photo(string name, string photo, double size)
        {
            if (!string.IsNullOrEmpty(photo))
            {
                try
                {
                    byte[] bytes = Convert.FromBase64String(photo);
                    System.Windows.Media.Imaging.BitmapImage bitmap = new System.Windows.Media.Imaging.BitmapImage();
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
                catch { }
            }
            return new TextBlock { Text = MainWindow.Initials(name ?? "?"), FontFamily = Theme.Font, FontSize = Math.Max(9, size * 0.36), FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Ink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        private string PhotoOf(Guid id)
        {
            Peer peer = _peers.FirstOrDefault(item => item.Id == id);
            return peer == null ? "" : peer.Photo;
        }

        private void ShowThread(Guid id, string name)
        {
            bool switching = _open != id;
            SelectDraft(id);
            _openName = name;
            _title.Text = name;
            _headFace.Child = Photo(name, PhotoOf(id), 36);
            _placeholder.Text = "Mensaje para @" + name;
            _thread.Visibility = Visibility.Visible;
            _emptyState.Visibility = Visibility.Collapsed;
            bool reachable = Reachable(id);
            _send.IsEnabled = reachable && !_sending;
            _composer.IsEnabled = reachable;
            ChatStore.MarkRead(id);
            if (_unreadChanged != null) _unreadChanged();
            List<ChatLine> lines = ChatStore.Messages(id);
            int previous = switching ? int.MaxValue : _shownCount;
            _messages.Children.Clear();
            if (lines.Count == 0)
            {
                StackPanel hello = new StackPanel { Margin = new Thickness(18, 18, 18, 0) };
                hello.Children.Add(Face(name, PhotoOf(id), 64, false, false));
                ((Grid)hello.Children[0]).HorizontalAlignment = HorizontalAlignment.Left;
                TextBlock big = Theme.Text(name, 22, Theme.Ink, FontWeights.SemiBold);
                big.Margin = new Thickness(0, 10, 0, 2);
                hello.Children.Add(big);
                hello.Children.Add(Theme.Text("Este es el comienzo de tu conversación con " + name + ".", 12.5, Theme.Muted));
                _messages.Children.Add(hello);
            }
            DateTime lastDay = DateTime.MinValue;
            ChatLine last = null;
            for (int i = 0; i < lines.Count; i++)
            {
                ChatLine line = lines[i];
                if (line.When.Date != lastDay)
                {
                    _messages.Children.Add(DayDivider(line.When));
                    lastDay = line.When.Date;
                    last = null;
                }
                bool grouped = last != null && last.Mine == line.Mine && !last.Nudge && !line.Nudge &&
                               (line.When - last.When).TotalMinutes < 6;
                FrameworkElement message = Message(line, name, id, grouped);
                if (i >= previous) Arrive(message);
                _messages.Children.Add(message);
                last = line;
            }
            _shownCount = lines.Count;
            RefreshStatus();
            RenderPeople();
            RefreshShareButton();
            _messageScroll.ScrollToEnd();
            if (_active) Dispatcher.BeginInvoke((Action)(() => _composer.Focus()), DispatcherPriority.Input);
        }

        private static void Arrive(FrameworkElement element)
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            TranslateTransform shift = new TranslateTransform(0, 10);
            element.RenderTransform = shift;
            element.Opacity = 0;
            element.BeginAnimation(OpacityProperty, Theme.Animation(0, 1, 260));
            shift.BeginAnimation(TranslateTransform.YProperty, Theme.Animation(10, 0, 320));
        }

        private static FrameworkElement DayDivider(DateTime when)
        {
            string label = when.Date == DateTime.Today ? "Hoy" : when.Date == DateTime.Today.AddDays(-1) ? "Ayer" :
                when.ToString("d 'de' MMMM 'de' yyyy", new System.Globalization.CultureInfo("es-ES"));
            Grid row = new Grid { Margin = new Thickness(18, 14, 18, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Border { Height = 1, Background = Theme.Line, VerticalAlignment = VerticalAlignment.Center });
            TextBlock text = Theme.Text(label, 11, Theme.Muted, FontWeights.SemiBold);
            text.Margin = new Thickness(10, 0, 10, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            Border right = new Border { Height = 1, Background = Theme.Line, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(right, 2);
            row.Children.Add(right);
            return row;
        }

        /// <summary>Un mensaje con avatar, autor y hora; los seguidos del mismo autor se agrupan sin cabecera.</summary>
        private FrameworkElement Message(ChatLine line, string peerName, Guid peerId, bool grouped)
        {
            string author = line.Mine ? Identity.Current : peerName;
            Grid row = new Grid { Margin = new Thickness(0, grouped ? 0 : 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            TextBlock hoverTime = null;
            if (!grouped)
            {
                Grid face = Face(author, line.Mine ? ProfilePhoto.Current : PhotoOf(peerId), 38, false, false);
                face.VerticalAlignment = VerticalAlignment.Top;
                face.Margin = new Thickness(0, 2, 0, 0);
                row.Children.Add(face);
            }
            else
            {
                hoverTime = Theme.Text(line.When.ToString("HH:mm"), 10, Theme.Muted);
                hoverTime.HorizontalAlignment = HorizontalAlignment.Center;
                hoverTime.VerticalAlignment = VerticalAlignment.Center;
                hoverTime.Opacity = 0;
                row.Children.Add(hoverTime);
            }
            StackPanel body = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
            if (!grouped)
            {
                TextBlock head = new TextBlock { FontFamily = Theme.Font, TextWrapping = TextWrapping.NoWrap };
                head.Inlines.Add(new System.Windows.Documents.Run(author)
                {
                    FontSize = 13.5, FontWeight = FontWeights.SemiBold,
                    Foreground = line.Mine ? new SolidColorBrush(Theme.AccentReadable()) : Theme.Ink
                });
                head.Inlines.Add(new System.Windows.Documents.Run("  " + Stamp(line.When)) { FontSize = 10.5, Foreground = Theme.Muted });
                body.Children.Add(head);
            }
            if (line.Nudge)
            {
                StackPanel nudge = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                System.Windows.Shapes.Path zap = Icons.Make("zap", 15, new SolidColorBrush(Theme.AccentReadable()), 2);
                nudge.Children.Add(zap);
                TextBlock text = Theme.Text(line.Mine ? "Enviaste un zumbido" : peerName + " te envió un zumbido", 13, Theme.Muted, FontWeights.SemiBold);
                text.Margin = new Thickness(6, 0, 0, 0);
                nudge.Children.Add(text);
                body.Children.Add(nudge);
            }
            else if (!string.IsNullOrEmpty(line.FilePath)) body.Children.Add(Attachment(line));
            else
            {
                TextBox text = new TextBox
                {
                    Text = line.Text,
                    FontFamily = BodyFont, FontWeight = FontWeights.Normal,
                    FontSize = 13.5,
                    Foreground = Theme.Ink,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    Padding = new Thickness(0, 1, 0, 1),
                    Cursor = Cursors.IBeam,
                    Template = PlainText()
                };
                body.Children.Add(text);
            }
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            Border holder = new Border { Child = row, Background = Brushes.Transparent, Padding = new Thickness(0, 1, 0, 1) };
            Brush hover = Theme.Color(Theme.P.Dark ? "#0DFFFFFF" : "#08000000");
            holder.MouseEnter += (s, e) => { holder.Background = hover; if (hoverTime != null) hoverTime.Opacity = 1; };
            holder.MouseLeave += (s, e) => { holder.Background = Brushes.Transparent; if (hoverTime != null) hoverTime.Opacity = 0; };
            return holder;
        }

        private static ControlTemplate _plainText;
        private static ControlTemplate PlainText()
        {
            if (_plainText != null) return _plainText;
            _plainText = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TextBox'>" +
                "<ScrollViewer Name='PART_ContentHost' Padding='{TemplateBinding Padding}' Focusable='False' VerticalScrollBarVisibility='Disabled' HorizontalScrollBarVisibility='Disabled'/></ControlTemplate>");
            return _plainText;
        }

        private static string Stamp(DateTime when)
        {
            if (when.Date == DateTime.Today) return "hoy a las " + when.ToString("HH:mm");
            if (when.Date == DateTime.Today.AddDays(-1)) return "ayer a las " + when.ToString("HH:mm");
            return when.ToString("dd/MM/yyyy HH:mm");
        }

        /// <summary>Tarjeta de archivo adjunto con acceso a su carpeta.</summary>
        private static FrameworkElement Attachment(ChatLine line)
        {
            bool exists = File.Exists(line.FilePath);
            Grid card = new Grid();
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.ColumnDefinitions.Add(new ColumnDefinition());
            Border icon = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(10), Background = Theme.NavActive };
            icon.Child = Icons.Make("file", 20, Theme.NavActiveText, 1.8);
            card.Children.Add(icon);
            StackPanel text = new StackPanel { Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
            TextBlock name = Theme.Text(Path.GetFileName(line.FilePath), 13, exists ? new SolidColorBrush(Theme.AccentReadable()) : Theme.Muted, FontWeights.SemiBold);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.TextWrapping = TextWrapping.NoWrap;
            text.Children.Add(name);
            string detail = exists ? MainWindow.FormatSize(new FileInfo(line.FilePath).Length) + " · Mostrar en carpeta" : "Ya no está en esta ubicación";
            text.Children.Add(Theme.Text(detail, 11, Theme.Muted));
            Grid.SetColumn(text, 1);
            card.Children.Add(text);
            Border frame = Theme.Glass(new Border
            {
                Child = card,
                Background = Theme.SoftSurface,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10, 8, 12, 8),
                Margin = new Thickness(0, 4, 0, 2),
                MaxWidth = 380,
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = exists ? Cursors.Hand : Cursors.Arrow
            });
            string path = line.FilePath;
            if (exists) frame.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
                catch { }
            };
            return frame;
        }

        private void SelectDraft(Guid id)
        {
            if (_open != Guid.Empty && _open != id) _drafts[_open] = _composer.Text;
            _open = id;
            string draft;
            _drafts.TryGetValue(id, out draft);
            if (string.Equals(_composer.Text, draft ?? "", StringComparison.Ordinal)) return;
            _loadingDraft = true;
            try { _composer.Text = draft ?? ""; }
            finally { _loadingDraft = false; }
        }

        private void RefreshStatus()
        {
            if (_open == Guid.Empty) { _state.Text = ""; _typingLine.Text = ""; return; }
            DateTime until;
            bool typing = _typing.TryGetValue(_open, out until) && until > DateTime.UtcNow;
            _typingLine.Text = typing ? _openName + " está escribiendo…" : "";
            bool reachable = Reachable(_open);
            _state.Text = InChat(_open) ? "En el chat" : reachable ? "En línea" : "Desconectado";
            _presenceDot.Visibility = reachable ? Visibility.Visible : Visibility.Collapsed;
            _presenceDot.Background = InChat(_open) ? Theme.SelectionFill : Theme.Online;
        }

        private void RefreshShareButton()
        {
            bool live = _open != Guid.Empty && SharingWith != null && SharingWith(_open);
            _shareButton.ToolTip = live ? "Dejar de compartir pantalla" : "Compartir pantalla";
            _shareIcon.Stroke = live ? Theme.Danger : Theme.Ink;
            _shareButton.IsEnabled = _open != Guid.Empty && Reachable(_open);
        }

        private void ToggleShare()
        {
            Peer peer = _peers.FirstOrDefault(item => item.Id == _open && item.Address != null);
            if (peer == null) { _state.Text = "Desconectado"; return; }
            if (ShareScreen != null) ShareScreen(peer);
        }

        private bool InChat(Guid id)
        {
            DateTime until;
            return _inChat.TryGetValue(id, out until) && until > DateTime.UtcNow;
        }

        private bool Reachable(Guid id)
        {
            return _peers.Any(item => item.Id == id && item.Address != null);
        }

        private void OnComposerChanged()
        {
            if (_open != Guid.Empty) _drafts[_open] = _composer.Text;
            if (_loadingDraft) return;
            if (_open == Guid.Empty || _composer.Text.Length == 0)
            {
                if (_typingStop.IsEnabled) { _typingStop.Stop(); SignalOpen(NetworkEngine.ChatKindTyping, "0"); }
                return;
            }
            if ((DateTime.UtcNow - _typingSentUtc).TotalMilliseconds > 1500)
            {
                _typingSentUtc = DateTime.UtcNow;
                SignalOpen(NetworkEngine.ChatKindTyping, "1");
            }
            _typingStop.Stop();
            _typingStop.Start();
        }

        private async void SendCurrent()
        {
            string draft = _composer.Text;
            string text = draft.Trim();
            if (text.Length == 0 || _open == Guid.Empty || _sending) return;
            Peer peer = _peers.FirstOrDefault(item => item.Id == _open && item.Address != null);
            if (peer == null) { _state.Text = "Desconectado"; return; }
            _sending = true;
            _send.IsEnabled = false;
            SignalOpen(NetworkEngine.ChatKindTyping, "0");
            try
            {
                await _sendChatAsync(peer, text);
                ChatStore.Append(peer.Id, peer.Name, true, text);
                if (_open == peer.Id && string.Equals(_composer.Text, draft, StringComparison.Ordinal))
                {
                    _composer.Text = "";
                    _drafts.Remove(peer.Id);
                }
                else
                {
                    string savedDraft;
                    if (_drafts.TryGetValue(peer.Id, out savedDraft) && string.Equals(savedDraft, draft, StringComparison.Ordinal))
                        _drafts.Remove(peer.Id);
                }
                if (_open == peer.Id) ShowThread(peer.Id, peer.Name);
            }
            catch (Exception ex) { _state.Text = ex.Message; }
            finally { _sending = false; _send.IsEnabled = Reachable(_open); }
        }

        private async void SendNudge()
        {
            if (_open == Guid.Empty) return;
            Peer peer = _peers.FirstOrDefault(item => item.Id == _open && item.Address != null);
            if (peer == null) { _state.Text = "Desconectado"; return; }
            try
            {
                await _network.SendSignalAsync(peer, NetworkEngine.ChatKindNudge, "1");
                ChatStore.AppendNudge(peer.Id, peer.Name, true);
                if (_nudgeFx != null) _nudgeFx();
                ShowThread(peer.Id, peer.Name);
            }
            catch (Exception ex) { _state.Text = ex.Message; }
        }

        private void PickFile()
        {
            if (_open == Guid.Empty) return;
            Peer peer = _peers.FirstOrDefault(item => item.Id == _open && item.Address != null);
            if (peer == null) { _state.Text = "Desconectado"; return; }
            OpenFileDialog dialog = new OpenFileDialog { Title = "Archivo para " + peer.Name, Multiselect = true };
            Window owner = Window.GetWindow(this);
            bool top = owner != null && owner.Topmost;
            if (owner != null) owner.Topmost = false;
            bool? chosen = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            if (owner != null) owner.Topmost = top;
            if (chosen != true) return;
            if (SendFiles != null) SendFiles(peer, dialog.FileNames);
        }

        private void BroadcastPresence(bool open)
        {
            if (_network == null) return;
            foreach (Peer peer in _network.Snapshot())
            {
                if (peer.Address == null) continue;
                Peer target = peer;
                TaskSend(target, NetworkEngine.ChatKindPresence, open ? "1" : "0");
            }
        }

        private void SignalOpen(byte kind, string body)
        {
            if (_open == Guid.Empty) return;
            Peer peer = _peers.FirstOrDefault(item => item.Id == _open && item.Address != null);
            if (peer != null) TaskSend(peer, kind, body);
        }

        private void TaskSend(Peer peer, byte kind, string body)
        {
            if (_network == null) return;
            Peer target = peer;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try { await _network.SendSignalAsync(target, kind, body); }
                catch { }
            });
        }

        /// <summary>Acción de la cabecera: ícono con halo al pasar el cursor.</summary>
        private static Button HeaderAction(string iconName, string tip, Action click, out System.Windows.Shapes.Path icon)
        {
            icon = Icons.Make(iconName, 19, Theme.Ink);
            Border hover = new Border { CornerRadius = new CornerRadius(17), Background = Theme.Color(Theme.P.Dark ? "#1AFFFFFF" : "#12000000"), Opacity = 0, IsHitTestVisible = false };
            Grid face = new Grid { Background = Brushes.Transparent };
            face.Children.Add(hover);
            face.Children.Add(icon);
            Button button = new Button { Content = face, Width = 34, Height = 34, ToolTip = tip, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Margin = new Thickness(2, 0, 2, 0) };
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            ControlTemplate template = new ControlTemplate(typeof(Button)) { VisualTree = presenter };
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.35));
            template.Triggers.Add(disabled);
            button.Template = template;
            button.MouseEnter += (s, e) => hover.BeginAnimation(OpacityProperty, Theme.Animation(hover.Opacity, 1, 150));
            button.MouseLeave += (s, e) => hover.BeginAnimation(OpacityProperty, Theme.Animation(hover.Opacity, 0, 250));
            button.Click += (s, e) => { e.Handled = true; click(); };
            return button;
        }

        /// <summary>Botón redondo de la barra de escritura; el de enviar usa el acento.</summary>
        private static Button Round(string iconName, string tip, Action click, bool primary)
        {
            Button button = new Button
            {
                Content = Icons.Make(iconName, primary ? 17 : 19, primary ? Theme.PrimaryText : Theme.Ink, 2),
                Width = 36,
                Height = 36,
                ToolTip = tip,
                Background = primary ? Theme.Primary : Theme.Color(Theme.P.Dark ? "#14FFFFFF" : "#0D000000"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(0),
                Template = Theme.PillTemplate(18)
            };
            button.Click += (s, e) => { e.Handled = true; click(); };
            return button;
        }
    }
}
