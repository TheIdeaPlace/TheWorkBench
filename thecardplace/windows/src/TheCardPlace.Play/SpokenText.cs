using System.Text;
using System.Text.RegularExpressions;

namespace TheCardPlace.Play;

/// <summary>
/// Text as it should reach a screen reader. The engines' sentences are written for the eye, with
/// colons, semicolons, dashes and brackets: "Score: Kelly and Marta 4", "took 40 points
/// (including 11 buried); the defenders took 80", "The count is 24 — your turn". JAWS reads those
/// marks aloud at its standard punctuation level ("colon", "semicolon", "em dash", "left paren"),
/// though NVDA and Narrator don't. So everything the app says, and every line in a list it
/// reads, goes through here first: dashes and semicolons end a sentence, a colon becomes a comma,
/// and brackets become commas. The engines keep their wording, which they share with the web and
/// iPhone versions.
/// </summary>
public static partial class SpokenText
{
    public static string Plain(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = text;
        // "(2 cards)" -> ", 2 cards"
        s = Brackets().Replace(s, m => ", " + m.Groups[1].Value.Trim());
        // "a — b", "a; b" -> "a. B"
        s = SentenceBreak().Replace(s, ". ");
        // "Score: Kelly" -> "Score, Kelly" (not a time such as 3:30)
        s = Colon().Replace(s, ", ");
        // Tidy what the replacements leave: ", ." ", ," ". ." and doubled spaces.
        s = CommaThenStop().Replace(s, ".");
        s = DoubleComma().Replace(s, ", ");
        s = DoubleStop().Replace(s, ". ");
        s = Spaces().Replace(s, " ").Trim();
        if (s.EndsWith(',')) s = s[..^1] + ".";
        return CapitaliseSentences(s);
    }

    /// <summary>A capital after every full stop, so a sentence made from a clause still reads as one.</summary>
    private static string CapitaliseSentences(string s)
    {
        var b = new StringBuilder(s);
        for (var i = 2; i < b.Length; i++)
            if (b[i - 2] is '.' or '!' or '?' && b[i - 1] == ' ' && char.IsLower(b[i])) b[i] = char.ToUpperInvariant(b[i]);
        if (b.Length > 0 && char.IsLower(b[0])) b[0] = char.ToUpperInvariant(b[0]);
        return b.ToString();
    }

    [GeneratedRegex(@"\s*\(([^()]*)\)")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s*[—–;]\s*")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"(?<!\d):(?!\d)\s*")]
    private static partial Regex Colon();

    [GeneratedRegex(@",\s*\.")]
    private static partial Regex CommaThenStop();

    [GeneratedRegex(@",\s*,")]
    private static partial Regex DoubleComma();

    [GeneratedRegex(@"\.\s*\.\s*")]
    private static partial Regex DoubleStop();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
