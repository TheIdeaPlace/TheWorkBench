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
    private Action? _pendingFocus;
    private string? _pendingCard;
    private readonly ObservableCollection<RowItem> _scores = [];
    private readonly ObservableCollection<RowItem> _history = [];
    private readonly ObservableCollection<RowItem> _lines = [];

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
        // A turn's focus move waits for the redraw that goes with it, and runs inside it, after
        // the hand and before the controls: the main button is renamed ("Play a card") only once
        // focus has left it, so the rename isn't read out on the way to the cards.
        session.FocusCardRequested += id =>
        {
            _pendingFocus = () => FocusCard(id);
            _pendingCard = id;
            QueueRefresh();
        };
        session.FocusChoicesRequested += () =>
        {
            _pendingFocus = FocusChoices;
            _pendingCard = null;
            QueueRefresh();
        };
    }

    public GameSession Session { get; }

    /// <summary>
    /// A turn moves focus to the cards or the choices, but not out of a menu the player has open
    /// or a dialog in front: that would close the menu under them. The turn is still announced.
    /// </summary>
    private bool MayMoveFocus => _window.IsActive && !_window.MenuHasFocus;


    /// <summary>
    /// A choice's label, without an access key: the choices are reached with Tab from the hand
    /// and get focus when it's the player's turn, and a key letter was read out ("cap eye").
    /// </summary>
    private static string ChoiceLabel(string title) => title.Replace("_", "__");

    /// <summary>
    /// Deals, draws the table, and puts focus in it straight away: on the card or choice the
    /// turn asked for, else the hand. Called once the view is on screen and laid out.
    /// </summary>
    public void Begin()
    {
        Session.Start();
        Refresh();
        if (!IsKeyboardFocusWithin) FocusHand();
    }

    public void NewGame()
    {
        Session.NewGame();
        // If the player acts first, the new game's turn has already asked for focus (the bid,
        // Pick up the blind) and Refresh moves it there; only otherwise does it go to the hand.
        var turnAsked = _pendingFocus is not null;
        Refresh();
        if (!turnAsked) FocusHand();
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
        StatusLine.Text = SpokenText.Plain(Session.Status);
        TableNote.Text = SpokenText.Plain(Session.TableNote);
        TableNote.Visibility = Session.TableNote.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshHand();
        RefreshSeats();
        if (_pendingFocus is { } focus)
        {
            _pendingFocus = null;
            _pendingCard = null;
            if (MayMoveFocus)
            {
                // The choices have to exist before focus can go to them.
                if (focus == FocusChoices) RefreshControls();
                focus();
            }
        }
        RefreshControls();
        RefreshTable(ScoresList, ScoresGrid, Session.Scores, ref _scoreShape, _scores);
        ScoresLabel.Content = "_" + Session.Scores.Title.Replace("_", "__");
        RefreshTable(HistoryList, HistoryGrid, Session.History, ref _historyShape, _history);
        HistoryLabel.Content = Session.History.Title.Length > 0 ? AccessText(Session.History.Title, 'p') : "Hands _played";
        RefreshLog();
        RefreshLines();
        // A list with no items has no item to take focus, so Tab would skip it. Until it has
        // some, the list itself is the stop, so it can still be found and heard to be empty.
        foreach (var list in new ItemsControl[] { HandList, TableList, LinesList, ScoresList, HistoryList, LogList })
            list.IsTabStop = list.Items.Count == 0;
    }

    private void RefreshLines()
    {
        var lines = new[] { Session.TableNote }.Concat(Session.TableLines).Where(l => l.Length > 0).Select(SpokenText.Plain).ToList();
        if (!ReferenceEquals(LinesList.ItemsSource, _lines)) LinesList.ItemsSource = _lines;
        Sync(_lines, lines.Count, (item, i) => item.Update([lines[i]], lines[i]), LinesList);
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
        var keep = new HashSet<string>(cards.Select(c => c.Id));

        // If the card that has focus is about to go (played, thrown, buried), move focus first:
        // to the card the turn has asked for, else the next card that stays, else the one before.
        // Removing it first would drop focus onto the window for a moment, and JAWS reads the
        // whole window.
        if (HandList.IsKeyboardFocusWithin
            && Keyboard.FocusedElement is ListBoxItem { DataContext: HandItem f }
            && _hand.IndexOf(f) is var gone and >= 0
            && !keep.Contains(f.Id))
        {
            var next = (_pendingCard is { } asked && keep.Contains(asked) ? _hand.FirstOrDefault(h => h.Id == asked) : null)
                       ?? _hand.Skip(gone + 1).FirstOrDefault(h => keep.Contains(h.Id))
                       ?? _hand.Take(gone).LastOrDefault(h => keep.Contains(h.Id));
            if (next is not null)
            {
                FocusCard(next.Id);
            }
            else if (cards.Count > 0)
            {
                // A whole new hand (a new game mid-hand): add the new cards, focus one, and only
                // then take the old ones away.
                var old = _hand.Count;
                foreach (var c in cards)
                {
                    var item = new HandItem(c.Id);
                    item.Update(c);
                    _hand.Add(item);
                }
                FocusCard(_pendingCard is { } id && keep.Contains(id) ? id : cards[0].Id);
                for (var i = old - 1; i >= 0; i--) _hand.RemoveAt(i);
                return;
            }
            else
            {
                FocusPrimary();
            }
        }

        // Remove what has gone, insert what is new, keep the rest in place and update it.
        for (var i = _hand.Count - 1; i >= 0; i--)
            if (!keep.Contains(_hand[i].Id)) _hand.RemoveAt(i);
        var focused = HandList.IsKeyboardFocusWithin && Keyboard.FocusedElement is ListBoxItem { DataContext: HandItem fi } ? fi : null;
        for (var i = 0; i < cards.Count; i++)
        {
            var at = IndexOf(_hand, cards[i].Id);
            if (at < 0)
            {
                _hand.Insert(i, new HandItem(cards[i].Id));
            }
            else if (at != i && ReferenceEquals(_hand[at], focused))
            {
                // Moving a card takes it out of the panel for a moment, and the focused one would
                // drop focus to the window (a Euchre hand re-sorted once trump is named). Move the
                // cards in front of it to the back instead; later passes put them in their places.
                for (; at > i; at--) _hand.Move(i, _hand.Count - 1);
            }
            else if (at != i)
            {
                _hand.Move(at, i);
            }
            _hand[i].Update(cards[i]);
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
        ControlsPanel.Visibility = wanted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var keep = new HashSet<string>(wanted.Select(KeyOf));
        foreach (var id in _secondary.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            var gone = _secondary[id];
            // Focus moves before the control goes, never after, so it never falls to the window.
            if (gone.IsKeyboardFocusWithin) FocusPrimary();
            ControlsPanel.Children.Remove(gone);
            _secondary.Remove(id);
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
            if (at > i && el.IsKeyboardFocusWithin)
            {
                // As in the hand: the focused control stays put and the ones in front go back.
                for (; at > i; at--)
                {
                    var front = ControlsPanel.Children[i];
                    ControlsPanel.Children.RemoveAt(i);
                    ControlsPanel.Children.Add(front);
                }
            }
            else if (at != i)
            {
                if (at >= 0) ControlsPanel.Children.RemoveAt(at);
                ControlsPanel.Children.Insert(Math.Min(i, ControlsPanel.Children.Count), el);
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
            ControlKind.Toggle => new CheckBox { VerticalAlignment = VerticalAlignment.Center },
            ControlKind.Choice => new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 8) },
            _ => new Button { Style = (Style)FindResource("SecondaryButton") },
        };
        switch (el)
        {
            case Button b:
                b.Click += (_, _) => (b.DataContext as GameControl)?.Action();
                break;
            case CheckBox box:
                // A resource reference, not a copy, so a change of theme recolours it.
                box.SetResourceReference(ForegroundProperty, "Brush.OnFelt");
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
                label.Content = ChoiceLabel(c.Title);
                AutomationProperties.SetName(combo, c.Title);
                if (!combo.ItemsSource?.Cast<string>().SequenceEqual(c.Options) ?? true) combo.ItemsSource = c.Options.ToList();
                combo.SelectedIndex = c.SelectedIndex;
                panel.Tag = null;
                break;
        }
    }

    private static void RefreshTable(ListView list, GridView grid, ScoreTable table, ref string shape, ObservableCollection<RowItem> rows)
    {
        var newShape = string.Join("|", table.Columns);
        if (newShape != shape)
        {
            grid.Columns.Clear();
            for (var i = 0; i < table.Columns.Count; i++)
                grid.Columns.Add(new GridViewColumn { Header = table.Columns[i], DisplayMemberBinding = new Binding($"Cells[{i}]") });
            shape = newShape;
        }
        if (!ReferenceEquals(list.ItemsSource, rows)) list.ItemsSource = rows;
        Sync(rows, table.Rows.Count, (item, i) => item.Update(table.Rows[i], table.RowName(i)), list);
    }

    /// <summary>
    /// Brings a list of rows to <paramref name="count"/> items, updating the ones that stay in
    /// place. Rows are only ever added or removed at the end, and if the row that has focus is
    /// about to go, focus moves to the new last row first.
    /// </summary>
    private static void Sync(ObservableCollection<RowItem> rows, int count, Action<RowItem, int> update, ItemsControl list)
    {
        if (rows.Count > count && list.IsKeyboardFocusWithin
            && Keyboard.FocusedElement is FrameworkElement { DataContext: RowItem f } && rows.IndexOf(f) >= count)
        {
            // To the new last row, or to the list itself when none are left (it is then its own
            // Tab stop, read as empty).
            if (count > 0) (list.ItemContainerGenerator.ContainerFromIndex(count - 1) as UIElement)?.Focus();
            else list.Focus();
        }
        while (rows.Count > count) rows.RemoveAt(rows.Count - 1);
        while (rows.Count < count) rows.Add(new RowItem());
        for (var i = 0; i < count; i++) update(rows[i], i);
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

    /// <summary>The first of the phase's choices: the bid amount, Order it up, Pick up the blind.</summary>
    private void FocusChoices()
    {
        ControlsPanel.UpdateLayout();
        foreach (var el in ControlsPanel.Children.OfType<FrameworkElement>())
        {
            var target = el is StackPanel { Children: [_, ComboBox combo] } ? combo : el;
            if (target.Focusable && target.IsVisible && target.Focus()) return;
        }
        FocusPrimary();
    }

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

    /// <summary>
    /// The hand's keys, by position in the hand rather than by where cards fall on screen (they
    /// overlap): Left and Up the previous card, Right and Down the next, Home and End the ends.
    /// Enter or Space plays or chooses the card.
    /// </summary>
    private void HandList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || FocusedCard is not { } card) return;
        var i = _hand.IndexOf(card);
        int? to = e.Key switch
        {
            Key.Left or Key.Up => Math.Max(0, i - 1),
            Key.Right or Key.Down => Math.Min(_hand.Count - 1, i + 1),
            Key.Home => 0,
            Key.End => _hand.Count - 1,
            _ => null,
        };
        if (to is { } t)
        {
            e.Handled = true;
            if (t != i) FocusCard(_hand[t].Id);
            return;
        }
        if (e.Key is Key.Enter or Key.Space)
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
