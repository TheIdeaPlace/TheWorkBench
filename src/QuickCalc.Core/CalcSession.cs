using System.Collections.ObjectModel;

namespace QuickCalc.Core;

public sealed record HistoryEntry(string Expression, string Result)
{
    public string DisplayText => $"{Expression} = {Result}";

    public override string ToString() => DisplayText;
}

public sealed record EvaluationResult(bool Success, string DisplayText, string SpokenText, double Value = 0, int ErrorPosition = -1);

/// <summary>Everything the calculator remembers between calculations: the last answer, history, and angle mode.</summary>
public sealed class CalcSession
{
    public const int MaxHistory = 200;

    // -1 means "not browsing history"; otherwise an index into History (0 = newest).
    private int _historyCursor = -1;
    private string _draft = "";

    public AngleMode AngleMode { get; set; } = AngleMode.Degrees;

    /// <summary>The most recent result, available as "ans". Starts at 0.</summary>
    public double Answer { get; private set; }

    /// <summary>Newest first.</summary>
    public ObservableCollection<HistoryEntry> History { get; } = [];

    public EvaluationResult Evaluate(string expression)
    {
        _historyCursor = -1;
        string effective = ApplyAnswerPrefix(expression);
        try
        {
            double value = Evaluator.Evaluate(effective, AngleMode, Answer);
            string formatted = NumberFormatter.Format(value);
            Answer = value;
            History.Insert(0, new HistoryEntry(ExpressionText.ToDisplay(effective), formatted));
            while (History.Count > MaxHistory)
                History.RemoveAt(History.Count - 1);
            return new EvaluationResult(true, formatted, "equals " + NumberFormatter.Speak(formatted), value);
        }
        catch (CalcException ex)
        {
            // Positions refer to the text the person typed, not to the "ans" we may have added in front.
            int position = ex.Position < 0 ? -1 : Math.Max(0, ex.Position - (effective.Length - expression.Length));
            return new EvaluationResult(false, "Error: " + ex.Message, "Error: " + ex.Message, ErrorPosition: position);
        }
    }

    /// <summary>
    /// After a calculation, starting with an operator continues from the last answer: "× 2" means "ans × 2".
    /// A leading minus still means a negative number.
    /// </summary>
    public string ApplyAnswerPrefix(string expression)
    {
        string trimmed = expression.TrimStart();
        if (History.Count > 0 && trimmed.Length > 0 && "+*/^×÷·%".Contains(trimmed[0]))
            return "ans" + expression;
        return expression;
    }

    /// <summary>Up arrow: the next older history expression, or null if there is none. <paramref name="current"/> is kept so Down can return to it.</summary>
    public string? RecallOlder(string current)
    {
        if (_historyCursor + 1 >= History.Count)
            return null;
        if (_historyCursor == -1)
            _draft = current;
        _historyCursor++;
        return History[_historyCursor].Expression;
    }

    /// <summary>Down arrow: the next newer history expression, then what was being typed before browsing, then null.</summary>
    public string? RecallNewer()
    {
        if (_historyCursor < 0)
            return null;
        _historyCursor--;
        return _historyCursor < 0 ? _draft : History[_historyCursor].Expression;
    }

    /// <summary>Stops history browsing, for example after the person edits the recalled text.</summary>
    public void ResetHistoryCursor() => _historyCursor = -1;

    public void ClearHistory()
    {
        History.Clear();
        _historyCursor = -1;
    }

    /// <summary>Puts <paramref name="insertion"/> in place of the selection (or at the caret) and returns the new text and caret.</summary>
    public static (string Text, int Caret) Insert(string text, int selectionStart, int selectionLength, string insertion)
    {
        selectionStart = Math.Clamp(selectionStart, 0, text.Length);
        selectionLength = Math.Clamp(selectionLength, 0, text.Length - selectionStart);
        string result = text[..selectionStart] + insertion + text[(selectionStart + selectionLength)..];
        return (result, selectionStart + insertion.Length);
    }

    /// <summary>Backspace: removes the selection, or the character before the caret. Returns the new text and caret.</summary>
    public static (string Text, int Caret) Backspace(string text, int selectionStart, int selectionLength)
    {
        selectionStart = Math.Clamp(selectionStart, 0, text.Length);
        selectionLength = Math.Clamp(selectionLength, 0, text.Length - selectionStart);
        if (selectionLength > 0)
            return (text.Remove(selectionStart, selectionLength), selectionStart);
        if (selectionStart == 0)
            return (text, 0);
        return (text.Remove(selectionStart - 1, 1), selectionStart - 1);
    }
}
