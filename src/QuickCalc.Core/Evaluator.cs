namespace QuickCalc.Core;

public enum AngleMode
{
    Degrees,
    Radians,
}

/// <summary>
/// Recursive-descent evaluator. Precedence, lowest first:
/// <c>+ -</c>, then <c>× ÷</c> and implied multiplication (<c>2π</c>, <c>3(4)</c>),
/// then unary minus, then <c>^</c> (right-associative, so <c>-2^2</c> is -4), then postfix <c>%</c> and <c>!</c>.
/// Missing closing parentheses at the end are filled in. In <c>a + b%</c> and <c>a − b%</c>,
/// the percent is a percent of <c>a</c>, so 80 + 15% is 92; anywhere else <c>b%</c> is b ÷ 100.
/// </summary>
public sealed class Evaluator
{
    private static readonly Dictionary<string, string> FunctionNames = new()
    {
        ["sqrt"] = "square root",
        ["cbrt"] = "cube root",
        ["abs"] = "absolute value",
        ["sin"] = "sine",
        ["cos"] = "cosine",
        ["tan"] = "tangent",
        ["asin"] = "inverse sine",
        ["acos"] = "inverse cosine",
        ["atan"] = "inverse tangent",
        ["ln"] = "natural logarithm",
        ["log"] = "logarithm",
        ["exp"] = "exponential",
        ["round"] = "round",
        ["floor"] = "floor",
        ["ceil"] = "ceiling",
    };

    private readonly IReadOnlyList<Token> _tokens;
    private readonly AngleMode _angleMode;
    private readonly double _answer;
    private int _pos;

    private Evaluator(IReadOnlyList<Token> tokens, AngleMode angleMode, double answer)
    {
        _tokens = tokens;
        _angleMode = angleMode;
        _answer = answer;
    }

    public static bool IsFunction(string name) => FunctionNames.ContainsKey(name);

    public static bool IsConstant(string name) => name is "pi" or "e" or "ans";

    /// <summary>Evaluates <paramref name="expression"/>. Throws <see cref="CalcException"/> with a readable message on any problem.</summary>
    public static double Evaluate(string expression, AngleMode angleMode = AngleMode.Degrees, double answer = 0)
    {
        var tokens = Tokenizer.Tokenize(expression);
        if (tokens.Count == 1)
            throw new CalcException("Nothing to calculate", 0);

        var evaluator = new Evaluator(tokens, angleMode, answer);
        double value = evaluator.ParseSum();
        var next = evaluator.Peek;
        if (next.Kind == TokenKind.CloseParen)
            throw new CalcException("There is a closing parenthesis without an opening one", next.Position);
        if (next.Kind != TokenKind.End)
            throw new CalcException($"Did not expect \"{next.Text}\" here", next.Position);
        return Check(value, -1);
    }

    private Token Peek => _tokens[_pos];

    private Token Next() => _tokens[_pos++];

    private double ParseSum()
    {
        double left = ParseProduct(out _);
        while (Peek.IsOperator('+') || Peek.IsOperator('-'))
        {
            var op = Next();
            double right = ParseProduct(out bool isPercent);
            if (isPercent)
                right = left * right; // right is already divided by 100
            left = op.Text == "+" ? left + right : left - right;
            left = Check(left, op.Position);
        }
        return left;
    }

    /// <param name="isPercent">True when the whole product is a single factor ending in %, such as "15%".</param>
    private double ParseProduct(out bool isPercent)
    {
        double left = ParseUnary();
        int factors = 1;
        while (true)
        {
            var token = Peek;
            if (token.IsOperator('*') || token.IsOperator('/'))
            {
                Next();
                double right = ParseUnary();
                if (token.Text == "/")
                {
                    if (right == 0)
                        throw new CalcException("Cannot divide by zero", token.Position);
                    left /= right;
                }
                else
                {
                    left *= right;
                }
            }
            else if (token.Kind is TokenKind.Name or TokenKind.OpenParen)
            {
                left *= ParseUnary(); // implied multiplication: 2π, 3(4), (1)(2)
            }
            else if (token.Kind == TokenKind.Number)
            {
                throw new CalcException("Two numbers need an operator between them", token.Position);
            }
            else
            {
                break;
            }
            factors++;
            left = Check(left, token.Position);
        }
        isPercent = factors == 1 && _tokens[_pos - 1].IsOperator('%');
        return left;
    }

    private double ParseUnary()
    {
        if (Peek.IsOperator('-'))
        {
            Next();
            return -ParseUnary();
        }
        if (Peek.IsOperator('+'))
        {
            Next();
            return ParseUnary();
        }
        return ParsePower();
    }

    private double ParsePower()
    {
        double baseValue = ParsePostfix();
        if (Peek.IsOperator('^'))
        {
            var op = Next();
            double exponent = ParseUnary();
            if (baseValue == 0 && exponent < 0)
                throw new CalcException("Cannot divide by zero", op.Position);
            double result = Math.Pow(baseValue, exponent);
            if (double.IsNaN(result))
                throw new CalcException("A negative number cannot be raised to a fractional power", op.Position);
            return Check(result, op.Position);
        }
        return baseValue;
    }

    private double ParsePostfix()
    {
        double value = ParsePrimary();
        while (true)
        {
            if (Peek.IsOperator('%'))
            {
                Next();
                value /= 100;
            }
            else if (Peek.IsOperator('!'))
            {
                value = Factorial(value, Next().Position);
            }
            else
            {
                return value;
            }
        }
    }

    private double ParsePrimary()
    {
        var token = Next();
        switch (token.Kind)
        {
            case TokenKind.Number:
                return token.Value;

            case TokenKind.OpenParen:
                double inner = ParseSum();
                ExpectCloseParen();
                return inner;

            case TokenKind.Name when token.Text == "pi":
                return Math.PI;
            case TokenKind.Name when token.Text == "e":
                return Math.E;
            case TokenKind.Name when token.Text == "ans":
                return _answer;

            case TokenKind.Name when IsFunction(token.Text):
                double argument;
                if (Peek.Kind == TokenKind.OpenParen)
                {
                    Next();
                    argument = ParseSum();
                    ExpectCloseParen();
                }
                else if (Peek.Kind == TokenKind.End)
                {
                    throw new CalcException($"{Capitalize(FunctionNames[token.Text])} needs a number after it", Peek.Position);
                }
                else
                {
                    argument = ParseUnary(); // "sqrt 9", "sin 30"
                }
                return ApplyFunction(token, argument);

            case TokenKind.Name:
                throw new CalcException($"\"{token.Text}\" is not a name QuickCalc knows", token.Position);

            case TokenKind.End:
                throw new CalcException("The expression ends before it is complete", token.Position);

            case TokenKind.CloseParen:
                throw new CalcException("Expected a number before the closing parenthesis", token.Position);

            default:
                throw new CalcException($"Expected a number where \"{token.Text}\" is", token.Position);
        }
    }

    private void ExpectCloseParen()
    {
        if (Peek.Kind == TokenKind.CloseParen)
            Next();
        else if (Peek.Kind != TokenKind.End) // missing ")" at the very end is filled in
            throw new CalcException($"Expected a closing parenthesis where \"{Peek.Text}\" is", Peek.Position);
    }

    private double ApplyFunction(Token name, double x)
    {
        int at = name.Position;
        double result = name.Text switch
        {
            "sqrt" => x < 0 ? throw new CalcException("Cannot take the square root of a negative number", at) : Math.Sqrt(x),
            "cbrt" => Math.Cbrt(x),
            "abs" => Math.Abs(x),
            "sin" => Sine(x),
            "cos" => Cosine(x),
            "tan" => Tangent(x, at),
            "asin" => FromRadians(Math.Asin(InUnitRange(x, "inverse sine", at))),
            "acos" => FromRadians(Math.Acos(InUnitRange(x, "inverse cosine", at))),
            "atan" => FromRadians(Math.Atan(x)),
            "ln" => x <= 0 ? throw new CalcException("Logarithms need a number greater than zero", at) : Math.Log(x),
            "log" => x <= 0 ? throw new CalcException("Logarithms need a number greater than zero", at) : Math.Log10(x),
            "exp" => Math.Exp(x),
            "round" => Math.Round(x, MidpointRounding.AwayFromZero),
            "floor" => Math.Floor(x),
            "ceil" => Math.Ceiling(x),
            _ => throw new InvalidOperationException(name.Text),
        };
        return Check(result, at);
    }

    // In degrees, whole multiples of 90 give exact answers rather than tiny rounding leftovers like 6e-17.
    private double Sine(double x)
    {
        if (_angleMode == AngleMode.Degrees && IsMultipleOf(x, 90))
        {
            // IEEERemainder(x, 360) is in [-180, 180], so the quarter turn is -2..2.
            long quarter = (long)Math.Round(Math.IEEERemainder(x, 360) / 90);
            return quarter switch { 1 => 1, -1 => -1, _ => 0 };
        }
        return Math.Sin(ToRadians(x));
    }

    private double Cosine(double x) => _angleMode == AngleMode.Degrees ? Sine(x + 90) : Math.Cos(x);

    private double Tangent(double x, int at)
    {
        if (_angleMode == AngleMode.Radians)
            return Math.Tan(x);
        if (IsMultipleOf(x - 90, 180))
            throw new CalcException($"The tangent of {NumberFormatter.Format(x)} degrees is undefined", at);
        return Sine(x) / Cosine(x);
    }

    private static bool IsMultipleOf(double x, double step) => Math.IEEERemainder(x, step) == 0;

    private double ToRadians(double x) => _angleMode == AngleMode.Degrees ? x * Math.PI / 180 : x;

    private double FromRadians(double x) => _angleMode == AngleMode.Degrees ? x * 180 / Math.PI : x;

    private static double InUnitRange(double x, string what, int at) =>
        x is < -1 or > 1 ? throw new CalcException($"The {what} needs a number from -1 to 1", at) : x;

    private static double Factorial(double n, int at)
    {
        if (n < 0 || n != Math.Floor(n) || n > 170)
            throw new CalcException("Factorial needs a whole number from 0 to 170", at);
        double result = 1;
        for (int k = 2; k <= (int)n; k++)
            result *= k;
        return result;
    }

    private static double Check(double value, int position)
    {
        if (double.IsInfinity(value))
            throw new CalcException("The result is too large", position);
        if (double.IsNaN(value))
            throw new CalcException("The result is not a number", position);
        return value;
    }

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
