using System.Text.Json.Serialization;

namespace TheCardPlace.Core;

[JsonConverter(typeof(JsonStringEnumConverter<EventKind>))]
public enum EventKind { Deal, Info, Play, Trick, Hand, Game, You, Bid, Score, Moon, Pick, Count, Error }

/// <summary>
/// One thing that happened, in words. The engine writes these; the app speaks them and writes
/// them into the log. Nothing reaches the player that did not first pass through here as text,
/// which is what makes a game playable without seeing it.
/// </summary>
/// <param name="Seat">The seat the event is about, if it is about one.</param>
/// <param name="Cards">The cards the event concerns, so the app can show them as well as read the sentence.</param>
/// <param name="Audience">Null for everybody. A number means only that seat may ever receive it.</param>
public sealed record GameEvent(int Id, EventKind Kind, string Text, int? Seat, IReadOnlyList<Card> Cards, int? Audience);

/// <summary>
/// The event stream every engine keeps. Append-only; ids increase by one, so a reader that
/// remembers the last id it saw can ask for what is new. Public setters exist only so a state can
/// be serialised (to clone it, compare it, or one day send it to another player).
/// </summary>
public sealed class EventLog
{
    public List<GameEvent> Events { get; set; } = [];
    public int NextId { get; set; } = 1;

    public GameEvent Add(EventKind kind, string text, int? seat = null, IEnumerable<Card>? cards = null, int? audience = null)
    {
        var e = new GameEvent(NextId, kind, text, seat, cards?.ToList() ?? [], audience);
        NextId++;
        Events.Add(e);
        return e;
    }

    /// <summary>What one seat is entitled to hear, newer than <paramref name="since"/>.</summary>
    public List<GameEvent> EventsFor(int seat, int since = 0) =>
        Events.Where(e => e.Id > since && (e.Audience is null || e.Audience == seat)).ToList();

    [JsonIgnore]
    public int LastId => Events.Count == 0 ? 0 : Events[^1].Id;
}

/// <summary>
/// The answer to every action. Ok means it happened. A refusal always says why, in the words of
/// the rule, because "nothing happened" is exactly what a dropped key press feels like to someone
/// who cannot see the screen. Fault means the engine itself broke.
/// </summary>
public sealed record ActionResult(bool IsOk, string? Reason, bool IsFault)
{
    public static ActionResult Ok { get; } = new(true, null, false);
    public static ActionResult Refused(string reason) => new(false, reason, false);
    public static ActionResult Faulted(string reason) => new(false, reason, true);
}

/// <summary>
/// Who is in a chair. The engine only needs to know whether a person or the computer decides for
/// a seat; whether that person is at this keyboard or across the internet is the app's business.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Occupant>))]
public enum Occupant { Human, Bot }

/// <summary>How well the computer plays.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Difficulty>))]
public enum Difficulty { Easy, Normal, Hard }

/// <summary>
/// How fast the computer players move. Five rungs, the same in every game. The value is
/// milliseconds; -1 means the game waits for Continue, however long that takes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Pace>))]
public enum Pace { Immediate = 0, Brisk = 900, Comfortable = 2500, Relaxed = 4000, WaitForMe = -1 }

public static class PaceExtensions
{
    public static readonly Pace[] All = [Pace.Immediate, Pace.Brisk, Pace.Comfortable, Pace.Relaxed, Pace.WaitForMe];

    public static string Label(this Pace p) => p switch
    {
        Pace.Immediate => "Immediate",
        Pace.Brisk => "Brisk",
        Pace.Comfortable => "Comfortable",
        Pace.Relaxed => "Relaxed",
        _ => "Wait for me to continue",
    };

    /// <summary>"the next play comes on its own after four seconds."</summary>
    public static string Words(this Pace p) => p switch
    {
        Pace.Immediate => "immediately",
        Pace.Brisk => "after about a second",
        Pace.Comfortable => "after two and a half seconds",
        Pace.Relaxed => "after four seconds",
        _ => "when you press Continue",
    };

    /// <summary>Null for wait-for-me, which is a button and not a timer.</summary>
    public static TimeSpan? Delay(this Pace p) => (int)p < 0 ? null : TimeSpan.FromMilliseconds((int)p);
}

public static class DifficultyExtensions
{
    public static readonly Difficulty[] All = [Difficulty.Easy, Difficulty.Normal, Difficulty.Hard];
    public static string Label(this Difficulty d) => d.ToString();
}

/// <summary>Small pieces of English the engines share, so lists and plurals read alike in every game.</summary>
public static class Prose
{
    /// <summary>"A", "A and B", "A, B and C".</summary>
    public static string List(IEnumerable<string> items)
    {
        var a = items.ToList();
        return a.Count switch
        {
            0 => "",
            1 => a[0],
            _ => string.Join(", ", a.Take(a.Count - 1)) + " and " + a[^1],
        };
    }

    /// <summary>"1 point", "2 points".</summary>
    public static string Count(int n, string singular, string? plural = null) =>
        n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : plural ?? singular + "s");

    private static readonly string[] Words =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
    ];

    /// <summary>"one", "two" … up to twenty, then digits.</summary>
    public static string Number(int n) => n is >= 0 and <= 20 ? Words[n] : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static readonly string[] Ordinals =
        ["zeroth", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth"];

    /// <summary>"first", "second" … "tenth", then "11th", "22nd".</summary>
    public static string Ordinal(int n)
    {
        if (n is >= 0 and <= 10) return Ordinals[n];
        var suffix = (n % 100) switch
        {
            11 or 12 or 13 => "th",
            _ => (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" },
        };
        return n.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix;
    }

    /// <summary>Capitalises the first letter and ends with a full stop if there is no closing punctuation.</summary>
    public static string AsSentence(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return t;
        t = char.ToUpperInvariant(t[0]) + t[1..];
        return t[^1] is '.' or '!' or '?' ? t : t + ".";
    }
}
