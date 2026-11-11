using System.Text;

namespace QuickCalc.Core;

/// <summary>Turns an expression into tidy display text (for history) and into words (for screen reader announcements).</summary>
public static class ExpressionText
{
    private static readonly Dictionary<string, string> SpokenNames = new()
    {
        ["pi"] = "pi",
        ["e"] = "e",
        ["ans"] = "answer",
        ["sqrt"] = "square root of",
        ["cbrt"] = "cube root of",
        ["abs"] = "absolute value of",
        ["sin"] = "sine",
        ["cos"] = "cosine",
        ["tan"] = "tangent",
        ["asin"] = "inverse sine",
        ["acos"] = "inverse cosine",
        ["atan"] = "inverse tangent",
        ["ln"] = "natural log",
        ["log"] = "log",
        ["exp"] = "exp",
        ["round"] = "round",
        ["floor"] = "floor",
        ["ceil"] = "ceiling",
    };

    /// <summary>
    /// Display form with spaced binary operators and the symbols × ÷ − π, e.g. "12 × (3 − 1)".
    /// Screen readers say these symbols as "times", "divided by" and "minus". Closing parentheses that were left off
    /// at the end are added. When <paramref name="answer"/> is given, "ans" is shown as that number, so a calculation
    /// recalled from history gives the same result later. Returns the input unchanged if it can't be read.
    /// </summary>
    /// <param name="exactAnswer">Write "ans" with every digit (round-trip) instead of the 15-digit display form.</param>
    public static string ToDisplay(string expression, double? answer = null, bool exactAnswer = false)
    {
        if (!TryTokenize(expression, out var tokens))
            return expression.Trim();

        var sb = new StringBuilder();
        Token? previous = null;
        int openParens = 0;
        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.End)
                break;
            if (token.Kind == TokenKind.OpenParen)
                openParens++;
            else if (token.Kind == TokenKind.CloseParen && openParens > 0)
                openParens--;

            bool binary = token.Kind == TokenKind.Operator && token.Text is "+" or "-" or "*" or "/" or "^" && !IsUnary(token, previous);
            if (sb.Length > 0 && (binary || NeedsSpaceBefore(token, previous!)))
                sb.Append(' ');
            sb.Append(token.Kind switch
            {
                TokenKind.Operator => token.Text switch { "*" => "×", "/" => "÷", "-" => "−", _ => token.Text },
                TokenKind.Name when token.Text == "pi" => "π",
                TokenKind.Name when token.Text == "ans" && answer is double value => AnswerText(value, previous, exactAnswer),
                _ => token.Text,
            });
            if (binary)
                sb.Append(' ');
            previous = token;
        }
        sb.Append(')', openParens);
        return sb.ToString();
    }

    // Parentheses keep the meaning: "ans^2" with ans = −3 is (−3)^2, and "2ans" with ans = 5 is 2 (5), not "2 5".
    private static string AnswerText(double answer, Token? previous, bool exact)
    {
        string text = exact ? NumberFormatter.FormatExact(answer) : NumberFormatter.Format(answer);
        bool impliedMultiplication = previous is not null &&
            (previous.Kind is TokenKind.Number or TokenKind.Name or TokenKind.CloseParen || previous.IsOperator('%') || previous.IsOperator('!'));
        if (text.StartsWith('-'))
            return "(−" + text[1..] + ")";
        return impliedMultiplication || text.Contains('e') ? "(" + text + ")" : text;
    }

    /// <summary>Words for an announcement, e.g. "12 times open paren 3 minus 1 close paren". "Empty" for blank input.</summary>
    public static string ToSpeech(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return "Empty";
        if (!TryTokenize(expression, out var tokens))
            return expression.Trim();

        var words = new List<string>();
        Token? previous = null;
        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.End)
                break;
            words.Add(token.Kind switch
            {
                TokenKind.Number => NumberFormatter.Speak(token.Text),
                TokenKind.Name => SpokenNames.GetValueOrDefault(token.Text, token.Text),
                TokenKind.OpenParen => "open paren",
                TokenKind.CloseParen => "close paren",
                _ => token.Text switch
                {
                    "+" => "plus",
                    "-" => IsUnary(token, previous) ? "negative" : "minus",
                    "*" => "times",
                    "/" => "divided by",
                    "^" => "to the power",
                    "%" => "percent",
                    "!" => "factorial",
                    _ => token.Text,
                },
            });
            previous = token;
        }
        return string.Join(' ', words);
    }

    private static bool IsUnary(Token token, Token? previous) =>
        token.Text is "-" or "+" &&
        (previous is null || previous.Kind == TokenKind.OpenParen ||
         (previous.Kind == TokenKind.Operator && previous.Text is not ("%" or "!")) ||
         (previous.Kind == TokenKind.Name && Evaluator.IsFunction(previous.Text)));

    private static bool NeedsSpaceBefore(Token token, Token previous)
    {
        // A number or name right after another name or number needs a gap so it reads back the same way: "2 sin(30)", "pi e".
        if (token.Kind is not (TokenKind.Name or TokenKind.Number))
            return false;
        if (previous.Kind == TokenKind.Name)
            return true;
        if (previous.Kind == TokenKind.Number || previous.Kind == TokenKind.CloseParen || previous.IsOperator('%') || previous.IsOperator('!'))
            return token.Text != "pi";
        return false;
    }

    private static bool TryTokenize(string expression, out IReadOnlyList<Token> tokens)
    {
        try
        {
            tokens = Tokenizer.Tokenize(expression);
            return true;
        }
        catch (CalcException)
        {
            tokens = [];
            return false;
        }
    }
}
