namespace QuickCalc.Core;

/// <summary>A calculation problem worded for the person using the calculator.</summary>
public sealed class CalcException(string message, int position = -1) : Exception(message)
{
    /// <summary>Zero-based index in the expression where the problem was found, or -1 if it has no single place.</summary>
    public int Position { get; } = position;
}
