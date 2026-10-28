using TheCardPlace.Core;

namespace TheCardPlace.Play;

/// <summary>
/// What kind of thing an announcement is, so the player can turn kinds on and off. The first
/// three are the core categories every Idea Place app has (baseline announcements.md); the rest
/// are this app's own, and map onto the kinds of event the engines write.
/// </summary>
public enum AnnouncementCategory
{
    /// <summary>The outcome of what the player just did: "Ten of Hearts chosen", "You play the Ten of Hearts".</summary>
    Result,
    /// <summary>Background progress: an update is available.</summary>
    Status,
    /// <summary>Teaching tips: "Choose Pass to send them."</summary>
    Hint,
    /// <summary>Plays, bids and calls by the other players.</summary>
    Moves,
    /// <summary>Tricks taken, counts, hand and game scores.</summary>
    Scoring,
    /// <summary>Deals, and things told only to this player: "You were passed the Queen of Spades."</summary>
    Table,
    /// <summary>"Your turn. Ruth led the Ten of Hearts."</summary>
    Turn,
    /// <summary>Shown in the status bar and the log, never spoken.</summary>
    Silent,
}

public static class AnnouncementCategories
{
    /// <summary>The ones the player can switch, in the order the settings show them.</summary>
    public static readonly AnnouncementCategory[] Configurable =
    [
        AnnouncementCategory.Result, AnnouncementCategory.Status, AnnouncementCategory.Hint,
        AnnouncementCategory.Moves, AnnouncementCategory.Scoring, AnnouncementCategory.Table, AnnouncementCategory.Turn,
    ];

    /// <summary>The words beside each checkbox. The core three use the baseline's wording.</summary>
    public static string Description(this AnnouncementCategory c) => c switch
    {
        AnnouncementCategory.Result => "Results of what I do",
        AnnouncementCategory.Status => "Background status",
        AnnouncementCategory.Hint => "Hints and instructions",
        AnnouncementCategory.Moves => "Other players' plays, bids and calls",
        AnnouncementCategory.Scoring => "Tricks taken and scores",
        AnnouncementCategory.Table => "Deals and messages to me",
        AnnouncementCategory.Turn => "When it is my turn",
        _ => "Never spoken",
    };

    /// <summary>The settings key, lower case with underscores.</summary>
    public static string Key(this AnnouncementCategory c) => c switch
    {
        AnnouncementCategory.Result => "result",
        AnnouncementCategory.Status => "status",
        AnnouncementCategory.Hint => "hint",
        AnnouncementCategory.Moves => "moves",
        AnnouncementCategory.Scoring => "scoring",
        AnnouncementCategory.Table => "table",
        AnnouncementCategory.Turn => "turn",
        _ => "silent",
    };

    /// <summary>
    /// Results interrupt, because they answer a key the player just pressed. Everything else is a
    /// stream that queues, so a run of plays is heard in order and never cuts off what is being read.
    /// </summary>
    public static bool Interrupts(this AnnouncementCategory c) => c == AnnouncementCategory.Result;

    /// <summary>Which category an engine event belongs to, heard from <paramref name="me"/>'s chair.</summary>
    public static AnnouncementCategory ForEvent(GameEvent e, int me) => e.Kind switch
    {
        EventKind.Play or EventKind.Bid or EventKind.Pick =>
            e.Seat == me ? AnnouncementCategory.Result : AnnouncementCategory.Moves,
        EventKind.Trick or EventKind.Hand or EventKind.Score or EventKind.Moon or EventKind.Count => AnnouncementCategory.Scoring,
        // The end of a game is never something to miss.
        EventKind.Game => AnnouncementCategory.Turn,
        _ => AnnouncementCategory.Table,
    };
}

/// <summary>One thing said, or held back, and why.</summary>
public sealed record Announcement(string Text, AnnouncementCategory Category, bool Forced, bool Interrupt, bool Spoken);

/// <summary>Does the speaking: on Windows, a UI Automation notification. Must never throw.</summary>
public interface ISpeaker
{
    void Speak(string text, bool interrupt, string activityId);
}

/// <summary>
/// The one announcer. Everything the app says without being asked for focus goes through here
/// with a category, and is filtered by the player's settings. Failures, and things the player
/// asked to hear (the review keys), are forced: turning announcements off quiets the game's
/// chatter, it does not hide refusals or answers. Every message, spoken or not, becomes the
/// last message, which the status bar shows and Repeat says again.
/// </summary>
public sealed class Announcer
{
    private readonly AnnouncementSettings _settings;

    public Announcer(AnnouncementSettings settings, ISpeaker? speaker = null)
    {
        _settings = settings;
        Speaker = speaker;
    }

    public ISpeaker? Speaker { get; set; }

    /// <summary>The last thing announced or shown, for the status bar and Repeat.</summary>
    public string LastText { get; private set; } = "";

    /// <summary>Raised for every message, spoken or not. Tests listen here; the status bar does too.</summary>
    public event Action<Announcement>? Announced;

    public bool WouldSpeak(AnnouncementCategory category) =>
        category != AnnouncementCategory.Silent && _settings.Enabled && _settings.IsOn(category);

    public void Say(string text, AnnouncementCategory category = AnnouncementCategory.Result, bool force = false)
    {
        var t = text.Trim();
        if (t.Length == 0) return;
        var spoken = force || WouldSpeak(category);
        var interrupt = force || category.Interrupts();
        LastText = t;
        if (spoken)
        {
            try { Speaker?.Speak(t, interrupt, "TheCardPlace." + category); }
            catch { /* announcing must never end a game */ }
        }
        Announced?.Invoke(new Announcement(t, category, force, interrupt, spoken));
    }

    /// <summary>Something the player asked to hear. Always spoken, and interrupts.</summary>
    public void Request(string text) => Say(text, AnnouncementCategory.Result, force: true);

    /// <summary>A refusal or failure. Always spoken, and interrupts.</summary>
    public void Error(string text) => Say(text, AnnouncementCategory.Result, force: true);

    /// <summary>
    /// Several messages as one, so a run of computer turns is one notification that cannot cut
    /// itself off. Only the parts whose category is switched on are spoken; the whole run is
    /// still the last message. The batch is delivered with the most urgent survivor's category.
    /// </summary>
    public void SayBatch(IEnumerable<(string Text, AnnouncementCategory Category)> parts)
    {
        var all = parts.Where(p => p.Text.Trim().Length > 0).ToList();
        if (all.Count == 0) return;
        var heard = all.Where(p => WouldSpeak(p.Category)).ToList();
        var whole = string.Join(" ", all.Select(p => p.Text.Trim()));
        if (heard.Count == 0)
        {
            LastText = whole;
            Announced?.Invoke(new Announcement(whole, AnnouncementCategory.Silent, false, false, false));
            return;
        }
        var text = string.Join(" ", heard.Select(p => p.Text.Trim()));
        // A batch is a stream: it queues unless every part is a result.
        var category = heard.All(p => p.Category == AnnouncementCategory.Result) ? AnnouncementCategory.Result : heard[^1].Category;
        LastText = whole;
        try { Speaker?.Speak(text, category.Interrupts(), "TheCardPlace." + category); }
        catch { /* never fatal */ }
        Announced?.Invoke(new Announcement(text, category, false, category.Interrupts(), true));
    }

    /// <summary>Says the last message again, forced.</summary>
    public void RepeatLast()
    {
        if (LastText.Length == 0) Request("Nothing has been said yet.");
        else Request(LastText);
    }
}
