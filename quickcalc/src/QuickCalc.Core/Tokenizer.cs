using System.Globalization;
using System.Text;

namespace QuickCalc.Core;

public enum TokenKind
{
    Number,
    Name,
    Operator,
    OpenParen,
    CloseParen,
    End,
}

/// <summary>One piece of an expression. <see cref="Position"/> is the zero-based index in the original text.</summary>
public sealed record Token(TokenKind Kind, string Text, int Position, double Value = 0)
{
    public bool IsOperator(char op) => Kind == TokenKind.Operator && Text[0] == op;
}

/// <summary>Splits an expression into tokens, normalizing the typographic symbols the keypad inserts.</summary>
public static class Tokenizer
{
    public static IReadOnlyList<Token> Tokenize(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var tokens = new List<Token>();
        int i = 0;
        while (i < expression.Length)
        {
            char c = expression[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < expression.Length && char.IsAsciiDigit(expression[i + 1])))
            {
                tokens.Add(ReadNumber(expression, ref i));
                continue;
            }

            if (char.IsAsciiLetter(c))
            {
                int start = i;
                while (i < expression.Length && char.IsAsciiLetter(expression[i]))
                    i++;
                tokens.Add(new Token(TokenKind.Name, expression[start..i].ToLowerInvariant(), start));
                continue;
            }

            switch (c)
            {
                case 'π':
                    tokens.Add(new Token(TokenKind.Name, "pi", i));
                    break;
                case '√':
                    tokens.Add(new Token(TokenKind.Name, "sqrt", i));
                    break;
                case '(':
                    tokens.Add(new Token(TokenKind.OpenParen, "(", i));
                    break;
                case ')':
                    tokens.Add(new Token(TokenKind.CloseParen, ")", i));
                    break;
                case '*' when i + 1 < expression.Length && expression[i + 1] == '*':
                    tokens.Add(new Token(TokenKind.Operator, "^", i));
                    i++;
                    break;
                case '+' or '-' or '*' or '/' or '^' or '%' or '!':
                    tokens.Add(new Token(TokenKind.Operator, c.ToString(), i));
                    break;
                case '−' or '–':
                    tokens.Add(new Token(TokenKind.Operator, "-", i));
                    break;
                case '×' or '·':
                    tokens.Add(new Token(TokenKind.Operator, "*", i));
                    break;
                case '÷':
                    tokens.Add(new Token(TokenKind.Operator, "/", i));
                    break;
                case '.':
                    throw new CalcException("A decimal point needs a digit after it", i);
                default:
                    throw new CalcException($"\"{c}\" is not something QuickCalc understands", i);
            }
            i++;
        }
        tokens.Add(new Token(TokenKind.End, "", expression.Length));
        return tokens;
    }

    private static Token ReadNumber(string s, ref int i)
    {
        int start = i;
        var sb = new StringBuilder();
        bool seenPoint = false;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.'))
        {
            if (s[i] == '.')
            {
                if (seenPoint)
                    throw new CalcException("A number can only have one decimal point", i);
                seenPoint = true;
            }
            sb.Append(s[i]);
            i++;
        }

        // Scientific notation, such as 1.5e+20. A lone "e" is the constant e (so "2e" means 2 × e).
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            if (j < s.Length && (s[j] == '+' || s[j] == '-'))
                j++;
            if (j < s.Length && char.IsAsciiDigit(s[j]))
            {
                sb.Append('e').Append(s, i + 1, j - i - 1);
                i = j;
                while (i < s.Length && char.IsAsciiDigit(s[i]))
                    sb.Append(s[i++]);
            }
        }

        string text = sb.ToString();
        double value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (double.IsInfinity(value))
            throw new CalcException("That number is too large", start);
        return new Token(TokenKind.Number, s[start..i], start, value);
    }
}
