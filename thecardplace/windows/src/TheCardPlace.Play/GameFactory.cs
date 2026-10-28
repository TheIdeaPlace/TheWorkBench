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
        _ => throw new NotSupportedException($"{kind} is not ready yet"),
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
