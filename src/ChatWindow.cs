using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lazo
{
    internal sealed class ChatWindow : Window
    {
        private readonly NetworkEngine _network;
        private readonly Action _unreadChanged;
        private readonly StackPanel _people;
        private readonly StackPanel _messages;
        private readonly ScrollViewer _messageScroll;
        private readonly TextBox _composer;
        private readonly TextBlock _title;
        private readonly TextBlock _state;
        private readonly Button _send;
        private List<Peer> _peers = new List<Peer>();
        private Guid _open;
        private string _openName = "";

        public ChatWindow(NetworkEngine network, Action unreadChanged)
        {
            _network = network;
            _unreadChanged = unreadChanged;
            Title = "Lazo · chat";
            Width = 700;
            Height = 480;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            FontFamily = Theme.Font;
            Grid outer = new Grid { Margin = new Thickness(10) };
            Content = outer;
            Border shell = new Border
            {
                Background = Theme.ShellSurface(),
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                ClipToBounds = true
            };
            outer.Children.Add(shell);
            Grid root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            shell.Child = root;

            Grid rail = new Grid { Background = Theme.SoftSurface };
            rail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            rail.RowDefinitions.Add(new RowDefinition());
            TextBlock heading = Theme.Text("Compañeros", 13, Theme.Ink, FontWeights.SemiBold);
            heading.Margin = new Thickness(16, 0, 0, 0);
            heading.VerticalAlignment = VerticalAlignment.Center;
            rail.Children.Add(heading);
            _people = new StackPanel();
            ScrollViewer peopleScroll = new ScrollViewer { Content = _people, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(peopleScroll, 1);
            rail.Children.Add(peopleScroll);
            root.Children.Add(rail);
            Border split = new Border { Background = Theme.Line };
            Grid.SetColumn(split, 1);
            root.Children.Add(split);

            Grid talk = new Grid { Margin = new Thickness(16, 12, 16, 12) };
            talk.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            talk.RowDefinitions.Add(new RowDefinition());
            talk.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            Grid.SetColumn(talk, 2);
            root.Children.Add(talk);
            Grid header = new Grid();
            _title = Theme.Text("Chat", 15, Theme.Ink, FontWeights.SemiBold);
            _title.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(_title);
            _state = Theme.Text("", 11, Theme.Muted);
            _state.HorizontalAlignment = HorizontalAlignment.Right;
            _state.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(_state);
            Button dismiss = new Button
            {
                Content = "×",
                Width = 28,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Right,
                FontSize = 16,
                Foreground = Theme.Muted,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 8)
            };
            dismiss.Click += (s, e) => Close();
            header.Children.Add(dismiss);
            _title.Margin = new Thickness(0, 0, 36, 0);
            talk.Children.Add(header);
            _messages = new StackPanel();
            _messageScroll = new ScrollViewer
            {
                Content = _messages,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 8, 0, 8)
            };
            Grid.SetRow(_messageScroll, 1);
            talk.Children.Add(_messageScroll);
            Grid composer = new Grid();
            composer.ColumnDefinitions.Add(new ColumnDefinition());
            composer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            _composer = new TextBox
            {
                FontFamily = Theme.Font,
                FontSize = 13,
                Foreground = Theme.Ink,
                Background = Theme.SoftSurface,
                BorderBrush = Theme.Line,
                Padding = new Thickness(10, 6, 10, 6),
                VerticalContentAlignment = VerticalAlignment.Center,
                CaretBrush = Theme.Ink
            };
            _composer.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    SendCurrent();
                    e.Handled = true;
                }
            };
            composer.Children.Add(_composer);
            _send = Theme.Button("Enviar", true);
            _send.MinHeight = 32;
            _send.Margin = new Thickness(8, 0, 0, 0);
            _send.Click += (s, e) => SendCurrent();
            Grid.SetColumn(_send, 1);
            composer.Children.Add(_send);
            Grid.SetRow(composer, 2);
            talk.Children.Add(composer);

            if (_network != null) _network.PeersChanged += OnPeers;
            Closed += (s, e) => { if (_network != null) _network.PeersChanged -= OnPeers; };
            Loaded += (s, e) =>
            {
                if (_network != null) OnPeers(_network.Snapshot());
                else RenderPeople();
            };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        public bool Viewing(Guid id) { return IsVisible && _open == id; }

        public void Incoming(Guid id, string name, string text)
        {
            if (_open == id) ShowThread(id, name);
            else RenderPeople();
        }

        private void OnPeers(List<Peer> peers)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                _peers = peers ?? new List<Peer>();
                RenderPeople();
                if (_open != Guid.Empty) _state.Text = Online(_open) ? "en línea" : "desconectado";
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
                _people.Children.Add(Person(peer.Id, peer.Name, thread == null ? "" : thread.Preview, thread == null ? 0 : thread.Unread, true));
            }
            foreach (ChatThread thread in known.Values.Where(item => !shown.Contains(item.Id)))
                _people.Children.Add(Person(thread.Id, thread.Name, thread.Preview, thread.Unread, false));
            if (_people.Children.Count == 0)
            {
                TextBlock empty = Theme.Text("Nadie conectado", 12, Theme.Muted);
                empty.Margin = new Thickness(16, 12, 12, 0);
                _people.Children.Add(empty);
            }
        }

        private Border Person(Guid id, string name, string preview, int unread, bool online)
        {
            StackPanel labels = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
            TextBlock title = Theme.Text(name, 13, Theme.Ink, FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            labels.Children.Add(title);
            string second = preview.Length == 0 ? (online ? "en línea" : "sin mensajes") : preview;
            TextBlock sub = Theme.Text(second, 11, unread > 0 ? Theme.Ink : Theme.Muted);
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            sub.Margin = new Thickness(0, 2, 0, 0);
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
            _state.Text = Online(id) ? "en línea" : "desconectado";
            _send.IsEnabled = Online(id);
            _composer.IsEnabled = Online(id);
            ChatStore.MarkRead(id);
            if (_unreadChanged != null) _unreadChanged();
            _messages.Children.Clear();
            List<ChatLine> lines = ChatStore.Messages(id);
            if (lines.Count == 0)
            {
                TextBlock empty = Theme.Text("Aún no hay mensajes.", 12, Theme.Muted);
                empty.HorizontalAlignment = HorizontalAlignment.Center;
                empty.Margin = new Thickness(0, 24, 0, 0);
                _messages.Children.Add(empty);
            }
            foreach (ChatLine line in lines) _messages.Children.Add(Bubble(line.Text, line.Mine, line.When));
            RenderPeople();
            _messageScroll.ScrollToEnd();
            _composer.Focus();
        }

        private Border Bubble(string text, bool mine, DateTime when)
        {
            TextBlock body = Theme.Text(text, 13, mine ? Theme.SelectionText : Theme.Ink);
            body.TextWrapping = TextWrapping.Wrap;
            TextBlock time = Theme.Text(when.ToString("HH:mm"), 10, mine ? Theme.SelectionText : Theme.Muted);
            time.Opacity = 0.7;
            time.Margin = new Thickness(0, 4, 0, 0);
            time.HorizontalAlignment = HorizontalAlignment.Right;
            StackPanel stack = new StackPanel();
            stack.Children.Add(body);
            stack.Children.Add(time);
            Border bubble = new Border
            {
                Child = stack,
                Background = mine ? Theme.SelectionFill : Theme.SoftSurface,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 8, 12, 6),
                Margin = new Thickness(mine ? 48 : 0, 4, mine ? 0 : 48, 4),
                HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 320
            };
            return bubble;
        }

        private bool Online(Guid id)
        {
            return _peers.Any(item => item.Id == id && item.Address != null);
        }

        private async void SendCurrent()
        {
            string text = _composer.Text.Trim();
            if (text.Length == 0 || _open == Guid.Empty) return;
            Peer peer = _peers.FirstOrDefault(item => item.Id == _open);
            if (peer == null) { _state.Text = "desconectado"; return; }
            _composer.Text = "";
            try
            {
                await _network.SendChatAsync(peer, text);
                ChatStore.Append(peer.Id, peer.Name, true, text);
                ShowThread(peer.Id, peer.Name);
            }
            catch (Exception ex) { _state.Text = ex.Message; }
        }
    }
}
