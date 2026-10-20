namespace QuickCalc;

internal static class HelpText
{
    public const string Text =
        """
        Type a calculation in the Expression box and press Enter (or =). The answer is spoken, shown in the Result box, and added to History.

        Keys
        Enter or =: calculate
        Escape: clear the expression
        Up and Down arrows: bring back earlier calculations
        Ctrl+Shift+C: copy the result
        Ctrl+Plus, Ctrl+Minus, Ctrl+0: zoom in, zoom out, reset zoom
        Alt+P: Expression box. Alt+R: Result. Alt+K: Keypad. Alt+Y: History. Alt+A: Angle unit (degrees or radians)

        Keypad
        Tab moves to the keypad as one stop; use the arrow keys to move between keys and Space or Enter to press one. Typing while on the keypad goes straight to the expression.

        History
        Enter puts a calculation back in the expression box. Ctrl+C copies it. Delete removes it.

        What you can type
        + - * / and × ÷ −, ^ for powers, ( ), % for percent, ! for factorial.
        80 + 15% is 92 (15% of 80 added). On its own, 15% is 0.15.
        Start with an operator to continue from the last answer: * 2 doubles it. Or type ans.
        Writing numbers side by side multiplies: 2pi, 3(4 + 1). This comes before × and ÷, so 1/2pi is 1 ÷ (2π).
        Missing closing parentheses at the end are filled in.
        Functions: sqrt, cbrt, abs, sin, cos, tan, asin, acos, atan, ln, log, exp, round, floor, ceil. Parentheses are optional: sqrt 16.
        Constants: pi, e. Scientific notation: 1.5e3.
        """;
}
