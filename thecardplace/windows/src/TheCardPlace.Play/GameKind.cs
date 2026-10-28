using TheCardPlace.Core;

namespace TheCardPlace.Play;

/// <summary>The five games, and what the hub says about each. The words are the iOS app's.</summary>
public enum GameKind { Hearts, Euchre, Spades, Cribbage, Sheephead }

public static class GameKinds
{
    public static readonly GameKind[] All = [GameKind.Hearts, GameKind.Euchre, GameKind.Spades, GameKind.Cribbage, GameKind.Sheephead];

    public static string Key(this GameKind g) => g.ToString().ToLowerInvariant();

    public static GameKind? FromKey(string? key) =>
        All.Cast<GameKind?>().FirstOrDefault(g => g!.Value.Key() == key);

    public static string Title(this GameKind g) => g.ToString();

    public static string Tagline(this GameKind g) => g switch
    {
        GameKind.Hearts => "Four players. Every heart is a point and the queen of spades is thirteen, and the lowest score wins.",
        GameKind.Euchre => "Four players in two partnerships, first to ten. The jack of trump and its color partner are the two highest cards.",
        GameKind.Spades => "Four players in two partnerships. Bid before a card is played; missing the contract costs the whole bid.",
        GameKind.Cribbage => "Two players. Pegging, fifteens, runs and the crib, scored to 121, and every count is read out in its parts.",
        _ => "Three to six players. Queens and jacks are trump, the picker takes the blind, and the jack of diamonds is a secret partner.",
    };

    public static string PlayersDescription(this GameKind g) => g switch
    {
        GameKind.Cribbage => "Two players",
        GameKind.Sheephead => "Three to six players",
        _ => "Four players",
    };

    /// <summary>The suit symbol the hub draws beside the name. Decorative.</summary>
    public static string Symbol(this GameKind g) => g switch
    {
        GameKind.Hearts => "♥",
        GameKind.Euchre => "♣",
        GameKind.Spades => "♠",
        GameKind.Cribbage => "15",
        _ => "♦",
    };

    /// <summary>
    /// Whether the computer plays differently by difficulty. Hearts and spades carry the setting
    /// but their originals never read it, so offering it there would be a lie.
    /// </summary>
    public static bool HasDifficulty(this GameKind g) => g is GameKind.Euchre or GameKind.Cribbage or GameKind.Sheephead;

    /// <summary>Hearts and spades open on Brisk; the other three say more per play and open on Relaxed.</summary>
    public static Pace DefaultPace(this GameKind g) => g is GameKind.Hearts or GameKind.Spades ? Pace.Brisk : Pace.Relaxed;

    /// <summary>Each game's own rule options, with the web and iOS defaults.</summary>
    public static IReadOnlyList<RuleOption> Rules(this GameKind g) => g switch
    {
        GameKind.Hearts => [HeartsRules.PointsToWin],
        GameKind.Spades => [SpadesRules.PointsToWin],
        GameKind.Euchre => [EuchreRules.PointsToWin, EuchreRules.StickTheDealer, EuchreRules.AllowAlone],
        GameKind.Cribbage => [CribbageRules.Target],
        _ => [SheepheadRules.Players, SheepheadRules.AllPass, SheepheadRules.BlackQueenDoubler, SheepheadRules.RedQueenDoubler, SheepheadRules.RedealDoubler],
    };
}

public static class HeartsRules
{
    public static readonly RuleOption PointsToWin = RuleOption.Numbers("pointsToWin", "Game ends at", 100, 50, 75, 100, 150);
}

public static class SpadesRules
{
    public static readonly RuleOption PointsToWin = RuleOption.Numbers("pointsToWin", "Points to win", 500, 200, 300, 500);
}

public static class EuchreRules
{
    public static readonly RuleOption PointsToWin = RuleOption.Numbers("pointsToWin", "Points to win", 10, 5, 10, 11, 15);
    public static readonly RuleOption StickTheDealer = RuleOption.Toggle("stickTheDealer", "Stick the dealer", false);
    public static readonly RuleOption AllowAlone = RuleOption.Toggle("allowAlone", "Allow going alone", true);
}

public static class CribbageRules
{
    public static readonly RuleOption Target = RuleOption.Numbers("targetScore", "Game to", 121, 61, 121);
}

public static class SheepheadRules
{
    public static readonly RuleOption Players = RuleOption.Numbers("players", "Players", 5, 3, 4, 5, 6);
    public static readonly RuleOption AllPass = new("allPass", "If everyone passes", "leaster",
        [("leaster", "Play a leaster"), ("redeal", "Deal again")]);
    public static readonly RuleOption BlackQueenDoubler = RuleOption.Toggle("blackQueenDoubler", "Double when one player holds both black queens", false);
    public static readonly RuleOption RedQueenDoubler = RuleOption.Toggle("redQueenDoubler", "Double when one player holds both red queens", false);
    public static readonly RuleOption RedealDoubler = RuleOption.Toggle("redealDoubler", "Double the hand after a redeal", false);
}
