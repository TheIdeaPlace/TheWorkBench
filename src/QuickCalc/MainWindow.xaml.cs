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
        _session.History.CollectionChanged += (_, _) => HistoryList.IsTabStop = _session.History.Count == 0;
        HistoryList.IsTabStop = true;
        PreviewKeyDown += Window_PreviewKeyDown;
        ExpressionBox.PreviewTextInput += ExpressionBox_PreviewTextInput;
        KeypadGroup.PreviewKeyDown += Keypad_PreviewKeyDown;
    }

    // ---- Calculating ----

    private void Evaluate()
    {
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
                InsertIntoExpression(tag);
                Announce(ExpressionText.ToSpeech(ExpressionBox.Text));
                break;
        }
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
        else if (e.Key == Key.Escape)
        {
            ClearExpression();
            e.Handled = true;
        }
    }

    // ---- History ----

    private void HistoryList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryEntry entry)
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
            int index = HistoryList.SelectedIndex;
            _session.History.RemoveAt(index);
            _session.ResetHistoryCursor();
            if (_session.History.Count == 0)
            {
                Announce("Removed. History is empty");
                ExpressionBox.Focus();
            }
            else
            {
                HistoryList.SelectedIndex = Math.Min(index, _session.History.Count - 1);
                (HistoryList.ItemContainerGenerator.ContainerFromIndex(HistoryList.SelectedIndex) as ListBoxItem)?.Focus();
                Announce("Removed");
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
        _session.ClearHistory();
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
        else if (modifiers == ModifierKeys.Control && e.Key is Key.OemPlus or Key.Add)
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
