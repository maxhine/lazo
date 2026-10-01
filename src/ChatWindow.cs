using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Lazo
{
    internal sealed class ChatPanel : Grid
    {
        private readonly NetworkEngine _network;
        private readonly Action _unreadChanged;
        private readonly Action _requestClose;
        private readonly Action _nudgeFx;
        private readonly StackPanel _people;
        private readonly StackPanel _messages;
        private readonly ScrollViewer _messageScroll;
        private readonly TextBox _composer;
        private readonly TextBlock _title;
        private readonly TextBlock _state;
        private readonly Button _send;
        private readonly DispatcherTimer _presence;
        private readonly DispatcherTimer _typingStop;
        private readonly DispatcherTimer _pulse;
        private readonly Dictionary<Guid, DateTime> _inChat = new Dictionary<Guid, DateTime>();
        private readonly Dictionary<Guid, DateTime> _typing = new Dictionary<Guid, DateTime>();
        private List<Peer> _peers = new List<Peer>();
        private Guid _open;
        private string _openName = "";
        private bool _active;
        private bool _sending;
        private DateTime _typingSentUtc = DateTime.MinValue;

        public ChatPanel(NetworkEngine network, Action unreadChanged, Action requestClose, Action nudgeFx)
        {
            _network = network;
            _unreadChanged = unreadChanged;
            _requestClose = requestClose;
            _nudgeFx = nudgeFx;
            Background = Brushes.Transparent;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            ColumnDefinitions.Add(new ColumnDefinition());

            Grid rail = new Grid { Background = Theme.SoftSurface };
            rail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            rail.RowDefinitions.Add(new RowDefinition());
            TextBlock heading = Theme.Text("Chat", 13, Theme.Ink, FontWeights.SemiBold);
            heading.Margin = new Thickness(12, 0, 0, 0);
            heading.VerticalAlignment = VerticalAlignment.Center;
            rail.Children.Add(heading);
            _people = new StackPanel();
            ScrollViewer peopleScroll = new ScrollViewer { Content = _people, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(peopleScroll, 1);
            rail.Children.Add(peopleScroll);
            Children.Add(rail);
            Border split = new Border { Background = Theme.Line };
            Grid.SetColumn(split, 1);
            Children.Add(split);

            Grid talk = new Grid { Margin = new Thickness(12, 8, 12, 10) };
            talk.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            talk.RowDefinitions.Add(new RowDefinition());
            talk.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            Grid.SetColumn(talk, 2);
            Children.Add(talk);

            Grid header = new Grid();
            _title = Theme.Text("Elige a alguien", 14, Theme.Ink, FontWeights.SemiBold);
            _title.VerticalAlignment = VerticalAlignment.Center;
            _title.TextTrimming = TextTrimming.CharacterEllipsis;
            _title.Margin = new Thickness(0, 0, 28, 0);
            header.Children.Add(_title);
            _state = Theme.Text("", 11, Theme.Muted);
            _state.Margin = new Thickness(0, 16, 28, 0);
            header.Children.Add(_state);
            Button dismiss = Icon("×", false);
            dismiss.HorizontalAlignment = HorizontalAlignment.Right;
            dismiss.Click += (s, e) => { if (_requestClose != null) _requestClose(); };
            header.Children.Add(dismiss);
            talk.Children.Add(header);

            _messages = new StackPanel();
            _messageScroll = new ScrollViewer
            {
                Content = _messages,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 4, 0, 6)
            };
            Grid.SetRow(_messageScroll, 1);
            talk.Children.Add(_messageScroll);

            Grid composer = new Grid();
            composer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            composer.ColumnDefinitions.Add(new ColumnDefinition());
            composer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            composer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            Button attach = Icon("\uE16C", true);
            attach.ToolTip = "Enviar archivo";
            attach.Click += (s, e) => PickFile();
            composer.Children.Add(attach);
            _composer = new TextBox
            {
                FontFamily = Theme.Font,
                FontSize = 13,
                Foreground = Theme.Ink,
                Background = Theme.SoftSurface,
                BorderBrush = Theme.Line,
                Padding = new Thickness(8, 4, 8, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
                CaretBrush = Theme.Ink,
                Margin = new Thickness(6, 0, 6, 0)
            };
            _composer.TextChanged += (s, e) => OnComposerChanged();
            _composer.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
                {
                    SendCurrent();
                    e.Handled = true;
                }
            };
            Grid.SetColumn(_composer, 1);
            composer.Children.Add(_composer);
            Button nudge = Icon("\uE115", true);
            nudge.ToolTip = "Zumbido";
            nudge.Click += (s, e) => SendNudge();
            Grid.SetColumn(nudge, 2);
            composer.Children.Add(nudge);
            _send = Theme.Button("Enviar", true);
            _send.MinHeight = 30;
            _send.Click += (s, e) => SendCurrent();
            Grid.SetColumn(_send, 3);
            composer.Children.Add(_send);
            Grid.SetRow(composer, 2);
            talk.Children.Add(composer);

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
            }
            else
            {
                _presence.Stop();
                BroadcastPresence(false);
                SignalOpen(NetworkEngine.ChatKindTyping, "0");
            }
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
            Dictionary<Guid, ChatThread> known = ChatStore.Threads().ToDictionary(item => item.Id);
            List<Guid> shown = new List<Guid>();
            foreach (Peer peer in _peers.OrderBy(item => item.Name))
            {
                shown.Add(peer.Id);
                ChatThread thread;
                known.TryGetValue(peer.Id, out thread);
                _people.Children.Add(Person(peer.Id, peer.Name, thread == null ? "" : thread.Preview, thread == null ? 0 : thread.Unread, peer.Address != null));
            }
            foreach (ChatThread thread in known.Values.Where(item => !shown.Contains(item.Id)))
                _people.Children.Add(Person(thread.Id, thread.Name, thread.Preview, thread.Unread, false));
            if (_people.Children.Count == 0)
            {
                TextBlock empty = Theme.Text("Nadie conectado", 12, Theme.Muted);
                empty.Margin = new Thickness(12, 10, 8, 0);
                _people.Children.Add(empty);
            }
        }

        private Border Person(Guid id, string name, string preview, int unread, bool reachable)
        {
            StackPanel labels = new StackPanel { Margin = new Thickness(10, 7, 8, 7) };
            TextBlock title = Theme.Text(name, 12.5, Theme.Ink, unread > 0 ? FontWeights.Bold : FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            labels.Children.Add(title);
            string presence = InChat(id) ? "en el chat" : reachable ? "conectado" : "desconectado";
            string second = string.IsNullOrEmpty(preview) ? presence : preview;
            TextBlock sub = Theme.Text(second, 10.5, unread > 0 ? Theme.Ink : Theme.Muted);
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            sub.Margin = new Thickness(0, 1, 0, 0);
            labels.Children.Add(sub);
            Border row = new Border
            {
                Child = labels,
                Background = id == _open ? Theme.CardSurface : Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            row.MouseLeftButtonUp += (s, e) => ShowThread(id, name);
            return row;
        }

        private void ShowThread(Guid id, string name)
        {
            _open = id;
            _openName = name;
            _title.Text = name;
            bool reachable = Reachable(id);
            _send.IsEnabled = reachable && !_sending;
            _composer.IsEnabled = reachable;
            ChatStore.MarkRead(id);
            if (_unreadChanged != null) _unreadChanged();
            _messages.Children.Clear();
            List<ChatLine> lines = ChatStore.Messages(id);
            if (lines.Count == 0)
            {
                TextBlock empty = Theme.Text("Aún no hay mensajes.", 12, Theme.Muted);
                empty.HorizontalAlignment = HorizontalAlignment.Center;
                empty.Margin = new Thickness(0, 18, 0, 0);
                _messages.Children.Add(empty);
            }
            foreach (ChatLine line in lines) _messages.Children.Add(Bubble(line));
            RefreshStatus();
            RenderPeople();
            _messageScroll.ScrollToEnd();
        }

        private void RefreshStatus()
        {
            if (_open == Guid.Empty) { _state.Text = ""; return; }
            DateTime until;
            if (_typing.TryGetValue(_open, out until) && until > DateTime.UtcNow)
            {
                _state.Text = "Escribiendo...";
                return;
            }
            if (InChat(_open)) _state.Text = "en el chat";
            else if (Reachable(_open)) _state.Text = "conectado";
            else _state.Text = "desconectado";
        }

        private Border Bubble(ChatLine line)
        {
            StackPanel stack = new StackPanel();
            if (line.Nudge)
            {
                TextBlock buzz = Theme.Text("Zumbido", 13, line.Mine ? Theme.SelectionText : Theme.Ink, FontWeights.SemiBold);
                stack.Children.Add(buzz);
            }
            else if (!string.IsNullOrEmpty(line.FilePath))
            {
                bool exists = File.Exists(line.FilePath);
                TextBlock link = Theme.Text(exists ? line.Text : line.Text + " · ya no está", 13, line.Mine ? Theme.SelectionText : Theme.Ink, FontWeights.SemiBold);
                link.TextDecorations = exists ? TextDecorations.Underline : null;
                link.Cursor = exists ? Cursors.Hand : Cursors.Arrow;
                link.TextWrapping = TextWrapping.Wrap;
                string path = line.FilePath;
                if (exists) link.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
                    catch { }
                };
                stack.Children.Add(link);
            }
            else
            {
                TextBlock body = Theme.Text(line.Text, 13, line.Mine ? Theme.SelectionText : Theme.Ink);
                body.TextWrapping = TextWrapping.Wrap;
                stack.Children.Add(body);
            }
            TextBlock time = Theme.Text(line.When.ToString("HH:mm"), 10, line.Mine ? Theme.SelectionText : Theme.Muted);
            time.Opacity = 0.7;
            time.Margin = new Thickness(0, 3, 0, 0);
            time.HorizontalAlignment = HorizontalAlignment.Right;
            stack.Children.Add(time);
            return new Border
            {
                Child = stack,
                Background = line.Mine ? Theme.SelectionFill : Theme.SoftSurface,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 7, 10, 6),
                Margin = new Thickness(line.Mine ? 36 : 0, 3, line.Mine ? 0 : 36, 3),
                HorizontalAlignment = line.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 280
            };
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
            string text = _composer.Text.Trim();
            if (text.Length == 0 || _open == Guid.Empty || _sending) return;
            Peer peer = _peers.FirstOrDefault(item => item.Id == _open && item.Address != null);
            if (peer == null) { _state.Text = "desconectado"; return; }
            _sending = true;
            _send.IsEnabled = false;
            _composer.Text = "";
            SignalOpen(NetworkEngine.ChatKindTyping, "0");
            try
            {
                await _network.SendChatAsync(peer, text);
                ChatStore.Append(peer.Id, peer.Name, true, text);
                ShowThread(peer.Id, peer.Name);
            }
            catch (Exception ex) { _state.Text = ex.Message; }
            finally { _sending = false; _send.IsEnabled = Reachable(_open); }
        }

        private async void SendNudge()
        {
            if (_open == Guid.Empty) return;
            Peer peer = _peers.FirstOrDefault(item => item.Id == _open && item.Address != null);
            if (peer == null) { _state.Text = "desconectado"; return; }
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
            if (peer == null) { _state.Text = "desconectado"; return; }
            OpenFileDialog dialog = new OpenFileDialog { Title = "Archivo para " + peer.Name, Multiselect = true };
            Window owner = Window.GetWindow(this);
            bool top = owner != null && owner.Topmost;
            if (owner != null) owner.Topmost = false;
            bool? chosen = owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
            if (owner != null) owner.Topmost = top;
            if (chosen != true) return;
            if (SendFiles != null) SendFiles(peer, dialog.FileNames);
        }

        public event Action<Peer, string[]> SendFiles;

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
            Peer target = peer;
            System.Threading.Tasks.Task.Run(async () =>
            {
                try { await _network.SendSignalAsync(target, kind, body); }
                catch { }
            });
        }

        private static Button Icon(string glyph, bool symbol)
        {
            TextBlock icon = new TextBlock
            {
                Text = glyph,
                FontFamily = symbol ? new FontFamily("Segoe MDL2 Assets") : Theme.Font,
                FontSize = symbol ? 14 : 16,
                Foreground = Theme.Ink,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Button button = new Button
            {
                Content = icon,
                Width = 28,
                Height = 28,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(Border));
            frame.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            frame.AppendChild(content);
            template.VisualTree = frame;
            button.Template = template;
            return button;
        }
    }
}
