using System.Globalization;

namespace QuickCalc.Core;

public static class NumberFormatter
{
    /// <summary>
    /// Shows up to 15 significant digits, which hides binary rounding noise (0.1 + 0.2 shows as 0.3).
    /// Very large and very small numbers use scientific notation such as 1.5e+20, which QuickCalc can read back in.
    /// </summary>
    public static string Format(double value)
    {
        if (value == 0)
            return "0"; // never "-0"
        string text = value.ToString("G15", CultureInfo.InvariantCulture);
        return text.Replace("E", "e", StringComparison.Ordinal);
    }

    /// <summary>The number as words a screen reader will say clearly, for example "minus 1.5 times 10 to the power 20".</summary>
    public static string Speak(string formatted)
    {
        string sign = "";
        if (formatted.StartsWith('-'))
        {
            sign = "minus ";
            formatted = formatted[1..];
        }
        int e = formatted.IndexOf('e');
        if (e < 0)
            return sign + formatted;

        string mantissa = formatted[..e];
        string exponent = formatted[(e + 1)..].TrimStart('+');
        bool negative = exponent.StartsWith('-');
        string digits = (negative ? exponent[1..] : exponent).TrimStart('0');
        if (digits.Length == 0)
            digits = "0";
        else if (negative)
            digits = "minus " + digits;
        return $"{sign}{mantissa} times 10 to the power {digits}";
    }
}
