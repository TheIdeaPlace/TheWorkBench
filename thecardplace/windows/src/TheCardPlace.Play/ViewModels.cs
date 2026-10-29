using TheCardPlace.Core;

namespace TheCardPlace.Play;

/// <summary>
/// One card in the player's hand, as the screen shows it and the screen reader names it.
/// A card that may not be played now stays in the hand and says why, rather than being disabled:
/// a disabled item is one a screen reader user may never learn exists.
/// </summary>
/// <param name="Description">The engine's words for the card: "Jack of Clubs, left bower, second highest trump".</param>
/// <param name="Badge">A word or two printed under the face: "trump", "13". Visual only; the description carries it.</param>
public sealed record HandCard(Card Card, string Description, bool Playable = true, string? Reason = null, bool Chosen = false, string? Badge = null)
{
    public string Id => Card.Id;

    /// <summary>What the screen reader says. Short: the description, then only what differs.</summary>
    public string AccessibleName
    {
        get
        {
            var name = Description;
            if (Chosen) name += ", chosen";
            if (!Playable && !string.IsNullOrEmpty(Reason)) name += ", cannot be played, " + Reason;
            return name;
        }
    }
}

/// <summary>A chair at the table: who sits there and what they have in front of them now.</summary>
/// <param name="Position">"left", "across", "right" from the player's chair; "" for the player.</param>
/// <param name="Detail">Short facts: "dealer, bid 4, 2 tricks".</param>
/// <param name="Played">The card this seat has put on the current trick, if any.</param>
/// <param name="PlayedNote">"led", "winning so far".</param>
public sealed record SeatInfo(int Seat, string Name, string Position, string Detail, Card? Played, string? PlayedDescription,
    string? PlayedNote, bool IsTurn, bool IsMe, int CardsHeld, string TurnWord = "to play")
{
    public string AccessibleName
    {
        get
        {
            var parts = new List<string> { IsMe ? Name + " (you)" : Name };
            if (Position.Length > 0) parts[0] += ", " + Position;
            if (Played is not null) parts.Add("Played " + (PlayedDescription ?? Played.Value.Name) + (PlayedNote is { Length: > 0 } n ? ", " + n : ""));
            if (IsTurn) parts.Add(char.ToUpperInvariant(TurnWord[0]) + TurnWord[1..]);
            if (Detail.Length > 0) parts.Add(char.ToUpperInvariant(Detail[0]) + Detail[1..]);
            // Sentences, not semicolons: JAWS says "semicolon" aloud at its standard punctuation.
            return string.Join(". ", parts) + ".";
        }
    }
}

/// <summary>
/// A table of numbers that belong to players. Read row by row, with the first column unnamed and
/// every other value preceded by its column name: "Ruth (you), score 12, tricks 3". Spoken order is
/// on-screen order. Fixed headings are said in lower case mid-row ("score 12"); columns from
/// <paramref name="NamedFrom"/> on are players' or sides' names and keep their capitals.
/// </summary>
public sealed record ScoreTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, int NamedFrom = int.MaxValue)
{
    public static ScoreTable Empty(string title) => new(title, [], []);

    public string RowName(int row)
    {
        var r = Rows[row];
        var parts = new List<string>();
        for (var i = 0; i < r.Count; i++)
        {
            if (i == 0 || i >= Columns.Count || Columns[i].Length == 0) parts.Add(r[i]);
            else parts.Add((i >= NamedFrom ? Columns[i] : Columns[i].ToLowerInvariant()) + " " + r[i]);
        }
        return string.Join(", ", parts);
    }
}

public enum ControlKind { Button, Toggle, Choice }

/// <summary>
/// One thing a player can press or set. The screen draws it; the session says what it is called
/// and what it does. <see cref="Enabled"/> false dims the control but does not disable it: the
/// action still runs and says why nothing happened, so a screen reader user can find and
/// understand it.
/// </summary>
public sealed class GameControl
{
    public GameControl(string title, Action action, string? id = null, char? key = null, bool enabled = true, string hint = "")
    {
        Title = title;
        Action = action;
        Id = id ?? title;
        Key = key;
        Enabled = enabled;
        Hint = hint;
    }

    public string Id { get; }
    public string Title { get; }
    public Action Action { get; }
    /// <summary>A key without modifiers that presses it from anywhere in the game.</summary>
    public char? Key { get; }
    public bool Enabled { get; }
    /// <summary>
    /// Why a dimmed control is not ready, in a few words. Not attached to the control (a tooltip
    /// becomes help text, which screen readers read on every visit); pressing the control says
    /// the full reason instead. Kept so a front end with room for it can show it.
    /// </summary>
    public string Hint { get; }
    public ControlKind Kind { get; init; } = ControlKind.Button;

    // Toggle
    public bool IsChecked { get; init; }
    public Action<bool>? OnToggle { get; init; }

    // Choice
    public IReadOnlyList<string> Options { get; init; } = [];
    public int SelectedIndex { get; init; }
    public Action<int>? OnSelect { get; init; }

    public static GameControl Toggle(string title, bool isChecked, Action<bool> onToggle, string? id = null) =>
        new(title, () => onToggle(!isChecked), id) { Kind = ControlKind.Toggle, IsChecked = isChecked, OnToggle = onToggle };

    public static GameControl Choice(string title, IReadOnlyList<string> options, int selected, Action<int> onSelect, string? id = null) =>
        new(title, () => { }, id) { Kind = ControlKind.Choice, Options = options, SelectedIndex = selected, OnSelect = onSelect };
}

/// <summary>A review command: H for the hand, T for the trick. Speaks the engine's sentence.</summary>
public sealed record ReviewItem(string Title, char? Key, Func<string> Text);
