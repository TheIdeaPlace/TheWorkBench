namespace TheCardPlace.Play.Tests;

/// <summary>Records what would have been spoken, instead of speaking it.</summary>
public sealed class RecordingSpeaker : ISpeaker
{
    public List<(string Text, bool Interrupt, string Id)> Spoken { get; } = [];
    public void Speak(string text, bool interrupt, string activityId) => Spoken.Add((text, interrupt, activityId));
    public string All => string.Join("\n", Spoken.Select(s => s.Text));
}

public static class Table
{
    /// <summary>Settings for a test table: Immediate pace, so the computer players run without waiting.</summary>
    public static AppSettings Settings(GameKind kind, Pace pace = Pace.Immediate)
    {
        var s = new AppSettings { PlayerName = "Kelly" };
        s.For(kind).Pace = pace;
        return s;
    }

    public static (T Session, Announcer Announcer, RecordingSpeaker Speaker, List<Announcement> Heard) Make<T>(
        GameKind kind, ulong seed, Pace pace = Pace.Immediate, AppSettings? settings = null) where T : GameSession
    {
        settings ??= Settings(kind, pace);
        var speaker = new RecordingSpeaker();
        var announcer = new Announcer(settings.Announcements, speaker);
        var heard = new List<Announcement>();
        announcer.Announced += heard.Add;
        var session = (T)GameFactory.Create(kind, settings, announcer, new RandomSource(seed));
        return (session, announcer, speaker, heard);
    }

    /// <summary>The card the hand list holds for an id; fails the test if it is not there.</summary>
    public static HandCard CardIn(GameSession s, Card c) =>
        s.Hand.FirstOrDefault(h => h.Card == c) ?? throw new Xunit.Sdk.XunitException($"{c.Name} is not in the hand");

    /// <summary>The secondary control with this id; fails the test if it is not offered.</summary>
    public static GameControl Control(GameSession s, string id) =>
        s.Secondary.FirstOrDefault(c => c.Id == id) ?? throw new Xunit.Sdk.XunitException(
            $"no control '{id}'; offered: {string.Join(", ", s.Secondary.Select(c => c.Id))}");
}
