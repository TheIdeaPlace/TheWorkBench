using System.Text.Json;
using System.Text.Json.Serialization;
using TheCardPlace.Core;

namespace TheCardPlace.Play;

/// <summary>Settings ▸ Screen Reader Announcements. Everything on by default.</summary>
public sealed class AnnouncementSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Category key to on/off. A category missing from the file is on.</summary>
    [JsonPropertyName("categories")]
    public Dictionary<string, bool> Categories { get; set; } = [];

    public bool IsOn(AnnouncementCategory c) => !Categories.TryGetValue(c.Key(), out var on) || on;

    public void Set(AnnouncementCategory c, bool on) => Categories[c.Key()] = on;
}

/// <summary>
/// Everything the player has chosen, remembered between launches. One store for all five games:
/// the name, the pace and the focus behaviour mean the same in every game and should not have to
/// be set five times. Each game keeps its own rule options under its own key.
/// </summary>
public sealed class AppSettings
{
    public string PlayerName { get; set; } = "Player";

    /// <summary>Move focus to the cards when it becomes the player's turn.</summary>
    public bool MoveFocusOnTurn { get; set; } = true;

    /// <summary>
    /// Announce each computer play as it happens (true) or gather a run of them into one message
    /// (false). Immediate pace always gathers, because there is no time between plays for a
    /// separate sentence to finish.
    /// </summary>
    public bool SpeakEveryPlay { get; set; } = true;

    public AnnouncementSettings Announcements { get; set; } = new();

    /// <summary>Per game: pace, difficulty and the game's own rule options as strings.</summary>
    public Dictionary<string, GameSettings> Games { get; set; } = [];

    /// <summary>Text size in the game window, as a scale factor. 1 is the normal size.</summary>
    public double Zoom { get; set; } = 1.0;

    /// <summary>The game the hub had selected last time.</summary>
    public string? LastGame { get; set; }

    public GameSettings For(GameKind g)
    {
        if (!Games.TryGetValue(g.Key(), out var s))
        {
            s = new GameSettings { Pace = g.DefaultPace() };
            Games[g.Key()] = s;
        }
        return s;
    }

    /// <summary>
    /// The names the computer players sit down with, none of them the player's own: the same list
    /// the browser and iOS games use.
    /// </summary>
    public static readonly string[] Crew = ["Ruth", "Marta", "Dale", "Otis", "Winnie", "Hal", "June", "Cyrus"];

    public List<string> Names(int seats)
    {
        var me = PlayerName.Trim();
        if (me.Length == 0) me = "Player";
        var names = new List<string> { me };
        foreach (var n in Crew)
        {
            if (names.Count >= seats) break;
            if (!string.Equals(n, me, StringComparison.OrdinalIgnoreCase)) names.Add(n);
        }
        return names;
    }

    // ---- storage ----

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Settings from a file's text. Anything unreadable gives the defaults rather than an error.</summary>
    public static AppSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }
}

public sealed class GameSettings
{
    public Pace Pace { get; set; } = Pace.Brisk;
    public Difficulty Difficulty { get; set; } = Difficulty.Normal;

    /// <summary>Rule option key to its chosen value, as text. See <see cref="RuleOption"/>.</summary>
    public Dictionary<string, string> Rules { get; set; } = [];

    public string Rule(RuleOption o) =>
        Rules.TryGetValue(o.Key, out var v) && o.Choices.Any(c => c.Value == v) ? v : o.Default;

    public int RuleInt(RuleOption o) => int.TryParse(Rule(o), out var n) ? n : int.Parse(o.Default, System.Globalization.CultureInfo.InvariantCulture);

    public bool RuleBool(RuleOption o) => Rule(o) == "true";
}

/// <summary>
/// One of a game's rule options, described so the hub can draw it without knowing the game: a
/// list of choices, or (with the two choices "true" and "false") a checkbox.
/// </summary>
public sealed record RuleOption(string Key, string Label, string Default, IReadOnlyList<(string Value, string Label)> Choices)
{
    public bool IsToggle => Choices.Count == 2 && Choices.Any(c => c.Value == "true") && Choices.Any(c => c.Value == "false");

    public static RuleOption Toggle(string key, string label, bool on) =>
        new(key, label, on ? "true" : "false", [("true", "On"), ("false", "Off")]);

    public static RuleOption Numbers(string key, string label, int def, params int[] values) =>
        new(key, label, def.ToString(System.Globalization.CultureInfo.InvariantCulture),
            values.Select(v => (v.ToString(System.Globalization.CultureInfo.InvariantCulture), v.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList());
}
