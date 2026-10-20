using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using QuickCalc.Core;

namespace QuickCalc;

/// <summary>Keypad tags that are actions rather than text to insert.</summary>
public static class KeyCommands
{
    public const string Backspace = "{backspace}";
    public const string Clear = "{clear}";
    public const string Calculate = "{equals}";
}

public partial class MainWindow : Window
{
    private const double MinZoom = 0.8;
    private const double MaxZoom = 3.0;
    private const double ZoomStep = 0.1;

    private readonly CalcSession _session = new();
    private string? _lastResult;
    private bool _settingTextFromHistory;

    public MainWindow()
    {
        InitializeComponent();
        HistoryList.ItemsSource = _session.History;
        // An empty ListBox has no item to take focus, so Tab would skip it. Make the list itself a stop until it has items,
        // so a screen reader user still finds it and hears that it's empty.
        _session.History.CollectionChanged += History_CollectionChanged;
        HistoryList.IsTabStop = true;
        PreviewKeyDown += Window_PreviewKeyDown;
        ExpressionBox.PreviewTextInput += ExpressionBox_PreviewTextInput;
        KeypadGroup.PreviewKeyDown += Keypad_PreviewKeyDown;
    }

    // ---- Calculating ----

    private void Evaluate()
    {
        if (string.IsNullOrWhiteSpace(ExpressionBox.Text))
        {
            // Enter pressed again after a calculation: say the answer again rather than replacing it with an error.
            Announce(_lastResult is null ? "Nothing to calculate" : "equals " + NumberFormatter.Speak(_lastResult));
            return;
        }

        var result = _session.Evaluate(ExpressionBox.Text);
        ResultBox.Text = result.DisplayText;
        if (result.Success)
        {
            _lastResult = result.DisplayText;
            // The calculation is in the history, so the box is cleared for the next one.
            // Starting the next one with an operator ("* 2") carries on from this answer; Up arrow brings it back to edit.
            SetExpression("", 0);
        }
        else if (result.ErrorPosition >= 0)
        {
            ExpressionBox.CaretIndex = Math.Min(result.ErrorPosition, ExpressionBox.Text.Length);
        }
        Announce(result.SpokenText);
    }

    private void SetExpression(string text, int caret)
    {
        ExpressionBox.Text = text;
        ExpressionBox.CaretIndex = Math.Clamp(caret, 0, text.Length);
    }

    private void InsertIntoExpression(string insertion)
    {
        var (text, caret) = CalcSession.Insert(ExpressionBox.Text, ExpressionBox.SelectionStart, ExpressionBox.SelectionLength, insertion);
        SetExpression(text, caret);
    }

    private void ClearExpression()
    {
        SetExpression("", 0);
        Announce("Cleared");
    }

    private void BackspaceExpression()
    {
        var (text, caret) = CalcSession.Backspace(ExpressionBox.Text, ExpressionBox.SelectionStart, ExpressionBox.SelectionLength);
        SetExpression(text, caret);
        Announce(ExpressionText.ToSpeech(text));
    }

    // ---- Expression box ----

    private void ExpressionBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                Evaluate();
                e.Handled = true;
                break;
            case Key.Decimal:
                // In many locales the number pad's decimal key types a comma; QuickCalc uses a point.
                InsertIntoExpression(".");
                e.Handled = true;
                break;
            case Key.Escape when ExpressionBox.Text.Length > 0:
                ClearExpression();
                e.Handled = true;
                break;
            case Key.Up:
                RecallHistory(_session.RecallOlder(ExpressionBox.Text), "No earlier calculations");
                e.Handled = true;
                break;
            case Key.Down:
                RecallHistory(_session.RecallNewer(), "No later calculations");
                e.Handled = true;
                break;
        }
    }

    private void ExpressionBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text == "=")
        {
            Evaluate();
            e.Handled = true;
        }
    }

    private void RecallHistory(string? expression, string emptyMessage)
    {
        if (expression is null)
        {
            Announce(emptyMessage);
            return;
        }
        _settingTextFromHistory = true;
        SetExpression(expression, expression.Length);
        _settingTextFromHistory = false;
        // The screen reader doesn't read a programmatic text change, so say it.
        Announce(expression.Length == 0 ? "Empty" : ExpressionText.ToSpeech(expression));
    }

    private void ExpressionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingTextFromHistory)
            _session.ResetHistoryCursor();
    }

    // ---- Keypad ----

    private void Key_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;

        switch (tag)
        {
            case KeyCommands.Calculate:
                Evaluate();
                break;
            case KeyCommands.Clear:
                ClearExpression();
                break;
            case KeyCommands.Backspace:
                BackspaceExpression();
                break;
            default:
                InsertIntoExpression(KeypadInsertion(tag));
                Announce(ExpressionText.ToSpeech(ExpressionBox.Text));
                break;
        }
    }

    /// <summary>Right after a digit, the e key inserts "×e"; a bare "2e3" would be read as scientific notation (2000).</summary>
    private string KeypadInsertion(string tag)
    {
        int caret = ExpressionBox.SelectionStart;
        if (tag == "e" && caret > 0 && ExpressionBox.Text[caret - 1] is (>= '0' and <= '9') or '.')
            return "×e";
        return tag;
    }

    /// <summary>Typing while on the keypad goes to the expression box, so nobody has to tab back first.</summary>
    private void Keypad_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]) || char.IsWhiteSpace(e.Text[0]))
            return;
        e.Handled = true;
        if (e.Text == "=")
        {
            Evaluate();
            return;
        }
        ExpressionBox.Focus();
        InsertIntoExpression(e.Text);
    }

    private void Keypad_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (e.Key == Key.Back)
        {
            BackspaceExpression();
            e.Handled = true;
        }
        else if (e.Key == Key.Decimal)
        {
            ExpressionBox.Focus();
            InsertIntoExpression(".");
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClearExpression();
            e.Handled = true;
        }
    }

    // ---- History ----

    private void History_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HistoryList.IsTabStop = _session.History.Count == 0;
        // Keep the newest calculation selected, so Tab into History lands on it rather than on an older one.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0 && !HistoryList.IsKeyboardFocusWithin)
            HistoryList.SelectedIndex = 0;
    }

    /// <summary>
    /// Alt+Y (and clicking the list's empty area) focuses the list itself. Pass focus on to an entry so the
    /// screen reader reads a calculation, not just "History list".
    /// </summary>
    private void HistoryList_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus != HistoryList || _session.History.Count == 0)
            return;
        if (HistoryList.SelectedIndex < 0)
            HistoryList.SelectedIndex = 0;
        HistoryList.ScrollIntoView(HistoryList.SelectedItem);
        HistoryList.UpdateLayout();
        (HistoryList.ItemContainerGenerator.ContainerFromIndex(HistoryList.SelectedIndex) as ListBoxItem)?.Focus();
    }

    /// <summary>
    /// The entry the screen reader last read: the focused item. Focus and selection can differ
    /// (Ctrl+arrow moves only focus), so keys act on focus, falling back to the selection.
    /// </summary>
    private HistoryEntry? FocusedHistoryEntry() =>
        (Keyboard.FocusedElement as ListBoxItem)?.DataContext as HistoryEntry ?? HistoryList.SelectedItem as HistoryEntry;

    private void HistoryList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (FocusedHistoryEntry() is not HistoryEntry entry)
            return;

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            UseHistoryEntry(entry);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopyText(entry.DisplayText);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            int index = _session.History.IndexOf(entry);
            _session.History.RemoveAt(index);
            _session.ResetHistoryCursor();
            if (_session.History.Count == 0)
            {
                ExpressionBox.Focus();
                Announce("Removed. History is empty");
            }
            else
            {
                // No announcement here: focus lands on the next entry and the screen reader reads it, as in File Explorer.
                // An announcement would cut that off.
                HistoryList.SelectedIndex = Math.Min(index, _session.History.Count - 1);
                HistoryList.UpdateLayout();
                (HistoryList.ItemContainerGenerator.ContainerFromIndex(HistoryList.SelectedIndex) as ListBoxItem)?.Focus();
            }
            e.Handled = true;
        }
    }

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryEntry entry)
            UseHistoryEntry(entry);
    }

    private void UseHistoryEntry(HistoryEntry entry)
    {
        ExpressionBox.Focus();
        SetExpression(entry.Expression, entry.Expression.Length);
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        // If focus was on an entry, it is about to disappear; put it somewhere useful first.
        bool focusWasInHistory = HistoryList.IsKeyboardFocusWithin;
        _session.ClearHistory();
        if (focusWasInHistory)
            ExpressionBox.Focus();
        Announce("History cleared");
    }

    // ---- Window-wide keys, menus ----

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.F1 && modifiers == ModifierKeys.None)
        {
            ShowHelp();
            e.Handled = true;
        }
        else if (e.Key == Key.C && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            CopyResult();
            e.Handled = true;
        }
        else if (modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift) && e.Key is Key.OemPlus or Key.Add)
        {
            ChangeZoom(ZoomStep);
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key is Key.OemMinus or Key.Subtract)
        {
            ChangeZoom(-ZoomStep);
            e.Handled = true;
        }
        else if (modifiers == ModifierKeys.Control && e.Key is Key.D0 or Key.NumPad0)
        {
            SetZoom(1.0);
            e.Handled = true;
        }
    }

    private void AngleUnit_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _session.AngleMode = ((ComboBox)sender).SelectedIndex == 1 ? AngleMode.Radians : AngleMode.Degrees;
    }

    private void CopyResult()
    {
        if (_lastResult is null)
            Announce("Nothing to copy yet");
        else
            CopyText(_lastResult);
    }

    private void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            Announce("Copied " + text);
        }
        catch (COMException)
        {
            // Another program can hold the clipboard open for a moment.
            Announce("Could not copy. Another program is using the clipboard. Try again");
        }
    }

    private void ChangeZoom(double delta) => SetZoom(Zoom.ScaleX + delta);

    private void SetZoom(double scale)
    {
        scale = Math.Round(Math.Clamp(scale, MinZoom, MaxZoom), 1);
        Zoom.ScaleX = scale;
        Zoom.ScaleY = scale;
        Announce($"Zoom {scale * 100:0} percent");
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e) => CopyResult();

    private void ClearExpression_Click(object sender, RoutedEventArgs e)
    {
        ExpressionBox.Focus();
        ClearExpression();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ChangeZoom(ZoomStep);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ChangeZoom(-ZoomStep);

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Help_Click(object sender, RoutedEventArgs e) => ShowHelp();

    private void ShowHelp() => MessageBox.Show(this, HelpText.Text, "QuickCalc help", MessageBoxButton.OK, MessageBoxImage.Information);

    private void About_Click(object sender, RoutedEventArgs e)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
        MessageBox.Show(this, $"QuickCalc {version}\nAn accessible calculator for Windows.", "About QuickCalc",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Speaks <paramref name="message"/> through the screen reader (JAWS, NVDA, Narrator) using a UI Automation notification.
    /// A newer announcement replaces one that hasn't been spoken yet, so fast keypad use doesn't queue up stale speech.
    /// </summary>
    private void Announce(string message)
    {
        var peer = UIElementAutomationPeer.FromElement(ResultBox) ?? UIElementAutomationPeer.CreatePeerForElement(ResultBox);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.ImportantMostRecent,
            message,
            "QuickCalc.Announcement");
    }
}
