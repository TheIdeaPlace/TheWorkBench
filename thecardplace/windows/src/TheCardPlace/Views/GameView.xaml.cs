using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using TheCardPlace.Play;

namespace TheCardPlace.App.Views;

/// <summary>
/// One table, for any of the five games. It draws what the session offers and sends key presses
/// back; it knows nothing of any game's rules.
/// </summary>
public partial class GameView : UserControl
{
    private readonly MainWindow _window;
    private readonly ObservableCollection<HandItem> _hand = [];
    private readonly ObservableCollection<SeatItem> _seats = [];
    private readonly ObservableCollection<string> _log = [];
    private readonly Dictionary<string, FrameworkElement> _secondary = [];
    private string _scoreShape = "", _historyShape = "";
    private bool _refreshQueued;

    public GameView(MainWindow window, GameSession session)
    {
        _window = window;
        Session = session;
        InitializeComponent();
        HandList.ItemsSource = _hand;
        TableList.ItemsSource = _seats;
        LogList.ItemsSource = _log;
        PreviewKeyDown += View_PreviewKeyDown;
        PreviewTextInput += View_PreviewTextInput;
        session.Changed += QueueRefresh;
        session.FocusCardRequested += id => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusCard(id));
    }

    public GameSession Session { get; }

    /// <summary>Deals, draws the table, and puts focus in the hand.</summary>
    public void Begin()
    {
        Session.Start();
        Refresh();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!HandList.IsKeyboardFocusWithin) FocusHand();
        });
    }

    public void NewGame()
    {
        Session.NewGame();
        Refresh();
        FocusHand();
    }

    // ---- drawing ----

    /// <summary>
    /// The session may change several times in one burst of computer turns; draw once, after the
    /// burst, rather than for each.
    /// </summary>
    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    public void Refresh()
    {
        StatusLine.Text = Session.Status;
        TableNote.Text = Session.TableNote;
        RefreshHand();
        RefreshSeats();
        RefreshControls();
        RefreshTable(ScoresList, ScoresGrid, Session.Scores, ref _scoreShape);
        ScoresLabel.Content = "_" + Session.Scores.Title.Replace("_", "__");
        RefreshTable(HistoryList, HistoryGrid, Session.History, ref _historyShape);
        HistoryLabel.Content = Session.History.Title.Length > 0 ? AccessText(Session.History.Title, 'p') : "Hands _played";
        RefreshLog();
        RefreshLines();
    }

    private void RefreshLines()
    {
        var lines = Session.TableLines.Where(l => l.Length > 0).ToList();
        if (LinesList.ItemsSource is List<string> old && old.SequenceEqual(lines)) return;
        var focused = LinesList.IsKeyboardFocusWithin ? Math.Max(0, LinesList.SelectedIndex) : -1;
        LinesList.ItemsSource = lines;
        if (focused >= 0 && lines.Count > 0)
        {
            LinesList.SelectedIndex = Math.Min(focused, lines.Count - 1);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusList(LinesList));
        }
    }

    /// <summary>Puts the access key on the given letter if the title has it, else leaves it bare.</summary>
    private static string AccessText(string title, char key)
    {
        var t = title.Replace("_", "__");
        var i = t.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        return i < 0 ? t : t.Insert(i, "_");
    }

    private void RefreshHand()
    {
        var cards = Session.Hand;
        var focusedIndex = -1;
        string? focusedId = null;
        if (HandList.IsKeyboardFocusWithin && Keyboard.FocusedElement is ListBoxItem { DataContext: HandItem f })
        {
            focusedId = f.Id;
            focusedIndex = _hand.IndexOf(f);
        }

        // Remove what has gone, insert what is new, keep the rest in place and update it.
        for (var i = _hand.Count - 1; i >= 0; i--)
            if (!cards.Any(c => c.Id == _hand[i].Id)) _hand.RemoveAt(i);
        for (var i = 0; i < cards.Count; i++)
        {
            var at = IndexOf(_hand, cards[i].Id);
            if (at < 0)
            {
                var item = new HandItem(cards[i].Id);
                _hand.Insert(i, item);
                at = i;
            }
            else if (at != i)
            {
                _hand.Move(at, i);
            }
            _hand[i].Update(cards[i]);
        }

        // The card that had focus was played: focus its neighbour, so focus never falls out of
        // the hand to the window.
        if (focusedId is not null && IndexOf(_hand, focusedId) < 0 && _hand.Count > 0)
        {
            var next = Math.Min(focusedIndex, _hand.Count - 1);
            var id = _hand[next].Id;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusCard(id));
        }
        else if (focusedId is not null && _hand.Count == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusPrimary);
        }
    }

    private static int IndexOf(ObservableCollection<HandItem> items, string id)
    {
        for (var i = 0; i < items.Count; i++)
            if (items[i].Id == id) return i;
        return -1;
    }

    private void RefreshSeats()
    {
        var seats = Session.Seats;
        // The player first, then the others in play order from their chair.
        var mine = seats.ToList().FindIndex(s => s.IsMe);
        var ordered = mine < 0 ? seats.ToList() : seats.Skip(mine).Concat(seats.Take(mine)).ToList();
        if (_seats.Count != ordered.Count || _seats.Select(s => s.Seat).SequenceEqual(ordered.Select(o => o.Seat)) is false)
        {
            _seats.Clear();
            foreach (var s in ordered) _seats.Add(new SeatItem(s.Seat));
        }
        for (var i = 0; i < ordered.Count; i++) _seats[i].Update(ordered[i]);
    }

    private void RefreshControls()
    {
        var primary = Session.Gate.IsWaiting ? Session.ContinueControl : Session.Primary;
        SetButton(PrimaryButton, primary);

        var wanted = Session.Secondary;
        var keep = new HashSet<string>(wanted.Select(KeyOf));
        foreach (var id in _secondary.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            var gone = _secondary[id];
            var hadFocus = gone.IsKeyboardFocusWithin;
            ControlsPanel.Children.Remove(gone);
            _secondary.Remove(id);
            if (hadFocus) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FocusPrimary);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var c = wanted[i];
            var key = KeyOf(c);
            if (!_secondary.TryGetValue(key, out var el))
            {
                el = Create(c);
                _secondary[key] = el;
            }
            else
            {
                Update(el, c);
            }
            var at = ControlsPanel.Children.IndexOf(el);
            if (at != i + 1)
            {
                if (at >= 0) ControlsPanel.Children.RemoveAt(at);
                ControlsPanel.Children.Insert(Math.Min(i + 1, ControlsPanel.Children.Count), el);
            }
        }
    }

    private static string KeyOf(GameControl c) => c.Kind + ":" + c.Id;

    private static void SetButton(Button b, GameControl c)
    {
        b.Content = new AccessText { Text = c.Title.Replace("_", "__") };
        AutomationProperties.SetName(b, c.Title);
        b.Tag = c.Enabled ? null : "dimmed";
        b.DataContext = c;
    }

    private FrameworkElement Create(GameControl c)
    {
        FrameworkElement el = c.Kind switch
        {
            ControlKind.Toggle => new CheckBox { VerticalAlignment = VerticalAlignment.Center, Foreground = (System.Windows.Media.Brush)FindResource("Brush.OnFelt") },
            ControlKind.Choice => new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 8) },
            _ => new Button { Style = (Style)FindResource("SecondaryButton") },
        };
        switch (el)
        {
            case Button b:
                b.Click += (_, _) => (b.DataContext as GameControl)?.Action();
                break;
            case CheckBox box:
                box.Click += (_, _) => (box.DataContext as GameControl)?.OnToggle?.Invoke(box.IsChecked == true);
                break;
            case StackPanel panel:
                var combo = new ComboBox { MinWidth = 120 };
                var label = new Label { Target = combo, Style = (Style)FindResource("FeltLabel"), VerticalAlignment = VerticalAlignment.Center };
                combo.SelectionChanged += (_, _) =>
                {
                    if (panel.Tag is "updating" || combo.SelectedIndex < 0) return;
                    (panel.DataContext as GameControl)?.OnSelect?.Invoke(combo.SelectedIndex);
                };
                panel.Children.Add(label);
                panel.Children.Add(combo);
                break;
        }
        Update(el, c);
        return el;
    }

    private static void Update(FrameworkElement el, GameControl c)
    {
        el.DataContext = c;
        switch (el)
        {
            case Button b:
                SetButton(b, c);
                break;
            case CheckBox box:
                box.Content = c.Title;
                box.IsChecked = c.IsChecked;
                break;
            case StackPanel { Children: [Label label, ComboBox combo] } panel:
                panel.Tag = "updating";
                label.Content = "_" + c.Title.Replace("_", "__");
                AutomationProperties.SetName(combo, c.Title);
                if (!combo.ItemsSource?.Cast<string>().SequenceEqual(c.Options) ?? true) combo.ItemsSource = c.Options.ToList();
                combo.SelectedIndex = c.SelectedIndex;
                panel.Tag = null;
                break;
        }
    }

    private static void RefreshTable(ListView list, GridView grid, ScoreTable table, ref string shape)
    {
        var newShape = string.Join("|", table.Columns);
        if (newShape != shape)
        {
            grid.Columns.Clear();
            for (var i = 0; i < table.Columns.Count; i++)
                grid.Columns.Add(new GridViewColumn { Header = table.Columns[i], DisplayMemberBinding = new Binding($"Cells[{i}]") });
            shape = newShape;
        }
        var rows = table.Rows.Select((r, i) => new TableRow(r, table.RowName(i))).ToList();
        if (list.ItemsSource is List<TableRow> old && old.SequenceEqual(rows, RowComparer.Instance)) return;
        var focused = list.IsKeyboardFocusWithin ? list.SelectedIndex : -1;
        list.ItemsSource = rows;
        if (focused >= 0 && rows.Count > 0)
        {
            list.SelectedIndex = Math.Min(focused, rows.Count - 1);
            list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
                (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as UIElement)?.Focus());
        }
    }

    private sealed class RowComparer : IEqualityComparer<TableRow>
    {
        public static readonly RowComparer Instance = new();
        public bool Equals(TableRow? a, TableRow? b) => a?.AccessibleName == b?.AccessibleName;
        public int GetHashCode(TableRow r) => r.AccessibleName.GetHashCode(StringComparison.Ordinal);
    }

    private void RefreshLog()
    {
        var log = Session.Log;
        if (log.Count < _log.Count)
        {
            _log.Clear();
        }
        var fresh = log.Count - _log.Count;
        for (var i = fresh - 1; i >= 0; i--) _log.Insert(0, log[i]);
    }

    // ---- focus ----

    public void FocusHand()
    {
        var item = HandList.SelectedItem as HandItem ?? _hand.FirstOrDefault();
        if (item is null)
        {
            FocusPrimary();
            return;
        }
        FocusCard(item.Id);
    }

    public void FocusLog() => FocusList(LogList);

    private void FocusPrimary() => PrimaryButton.Focus();

    private void FocusCard(string id)
    {
        var item = _hand.FirstOrDefault(h => h.Id == id);
        if (item is null) return;
        HandList.SelectedItem = item;
        HandList.UpdateLayout();
        if (HandList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem c) c.Focus();
    }

    private static void FocusList(ListBox list)
    {
        if (list.Items.Count == 0)
        {
            list.Focus();
            return;
        }
        if (list.SelectedIndex < 0) list.SelectedIndex = 0;
        list.UpdateLayout();
        list.ScrollIntoView(list.SelectedItem);
        (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as UIElement)?.Focus();
    }

    /// <summary>
    /// A label's access key, or a click on empty space, focuses the list itself, which a screen
    /// reader reads as just "list". Move on to the current item.
    /// </summary>
    private void List_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ListBox list && ReferenceEquals(e.NewFocus, list) && list.Items.Count > 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (ReferenceEquals(Keyboard.FocusedElement, list)) FocusList(list);
            });
    }

    // ---- keys ----

    private HandItem? FocusedCard =>
        Keyboard.FocusedElement is ListBoxItem { DataContext: HandItem h } && HandList.IsKeyboardFocusWithin ? h : null;

    private void HandList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key is Key.Enter or Key.Space && FocusedCard is { } card)
        {
            e.Handled = true;
            Session.Activate(card.Source);
        }
    }

    private void HandList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(HandList, d) is ListBoxItem { DataContext: HandItem h })
            Session.Activate(h.Source);
    }

    /// <summary>Left and Up go back round the table, Right and Down forward, in play order.</summary>
    private void TableList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || _seats.Count == 0) return;
        var step = e.Key switch { Key.Left or Key.Up => -1, Key.Right or Key.Down => 1, _ => 0 };
        if (step == 0) return;
        e.Handled = true;
        var i = Math.Clamp(Math.Max(0, TableList.SelectedIndex) + step, 0, _seats.Count - 1);
        TableList.SelectedIndex = i;
        (TableList.ItemContainerGenerator.ContainerFromIndex(i) as UIElement)?.Focus();
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e) => (PrimaryButton.DataContext as GameControl)?.Action();

    /// <summary>
    /// The game's single keys, the same as the browser games: the review letters, N to move on,
    /// R to repeat, G for the log, 1 to 9 for a card. Not while typing in a box or choosing from
    /// a list, where letters mean what they always mean.
    /// </summary>
    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: false } or ComboBox or ComboBoxItem) return;

        if (e.Key is >= Key.D1 and <= Key.D9 or Key.D0 or >= Key.NumPad1 and <= Key.NumPad9 or Key.NumPad0)
        {
            var n = e.Key switch
            {
                Key.D0 or Key.NumPad0 => 10,
                >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1 + 1,
                _ => e.Key - Key.D1 + 1,
            };
            e.Handled = true;
            if (n <= _hand.Count) FocusCard(_hand[n - 1].Id);
            else Session.Announcer.Request(_hand.Count == 0 ? "Your hand is empty." : $"You hold {_hand.Count} cards.");
            return;
        }

        if (e.Key is < Key.A or > Key.Z) return;
        var ch = (char)('a' + (e.Key - Key.A));
        if (Session.Reviews.FirstOrDefault(r => r.Key == ch) is { } review)
        {
            e.Handled = true;
            Session.Announcer.Request(review.Text());
            return;
        }
        if (Session.Secondary.FirstOrDefault(c => c.Key == ch) is { } control)
        {
            e.Handled = true;
            control.Action();
            return;
        }
        switch (ch)
        {
            case 'n':
                e.Handled = true;
                Session.Advance();
                break;
            case 'r':
                e.Handled = true;
                Session.Announcer.RepeatLast();
                break;
            case 'g':
                e.Handled = true;
                FocusLog();
                break;
        }
    }

    private void View_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text == "?" && Keyboard.FocusedElement is not TextBox { IsReadOnly: false })
        {
            e.Handled = true;
            _window.ShowKeys();
        }
    }
}
