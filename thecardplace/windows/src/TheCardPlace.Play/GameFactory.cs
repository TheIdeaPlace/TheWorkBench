using TheCardPlace.Core;
using TheCardPlace.Play.Games;

namespace TheCardPlace.Play;

/// <summary>Makes the session for a game, and hands out each game's rules text.</summary>
public static class GameFactory
{
    public static GameSession Create(GameKind kind, AppSettings settings, Announcer announcer, RandomSource? rng = null) => kind switch
    {
        GameKind.Hearts => new HeartsSession(settings, announcer, rng),
        GameKind.Spades => new SpadesSession(settings, announcer, rng),
        GameKind.Euchre => new EuchreSession(settings, announcer, rng),
        GameKind.Sheephead => new SheepheadSession(settings, announcer, rng),
        GameKind.Cribbage => new CribbageSession(settings, announcer, rng),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a game"),
    };

    public static IReadOnlyList<(string Heading, string Body)> Rules(GameKind kind) => kind switch
    {
        GameKind.Hearts => Hearts.HeartsHelp.Rules,
        GameKind.Spades => Spades.SpadesHelp.Rules,
        GameKind.Euchre => Euchre.EuchreHelp.Rules,
        GameKind.Cribbage => Cribbage.CribbageHelp.Rules,
        _ => Sheephead.SheepheadHelp.Rules,
    };
}
