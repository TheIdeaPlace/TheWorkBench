using System.Text.Json.Serialization;
using TheCardPlace.Core;

namespace TheCardPlace.Spades;

/// <summary>
/// The rules this table plays by. Every default matches the web settings dialog; the web offers
/// 250 or 500 points, and the other three numbers are the constants <c>game.js</c> names as
/// defaults.
/// </summary>
public sealed class SpadesConfig
{
    public static IReadOnlyList<string> DefaultNames { get; } = ["You", "East", "South", "West"];

    /// <summary>One per seat; seat 0 is the person at this PC.</summary>
    public List<string> Names { get; set; } = [.. DefaultNames];

    /// <summary>
    /// Carried for the shared contract. <c>ai.js</c> never reads it (the web passes 'hard'
    /// unconditionally and has no control for it), so every level plays the same game.
    /// </summary>
    public Difficulty Difficulty { get; set; } = Difficulty.Normal;

    public int PointsToWin { get; set; } = 500;

    /// <summary>Bags per penalty.</summary>
    public int BagLimit { get; set; } = 10;

    /// <summary>
    /// What a full bag bin costs. Zero is a coherent choice: count the bags, do not punish them.
    /// </summary>
    public int BagPenalty { get; set; } = 100;

    public int NilValue { get; set; } = 100;

    /// <summary>
    /// A separate copy, so a state never shares its rules with the caller that made it (the Swift
    /// config is a value).
    /// </summary>
    public SpadesConfig Copy() => new()
    {
        Names = [.. Names],
        Difficulty = Difficulty,
        PointsToWin = PointsToWin,
        BagLimit = BagLimit,
        BagPenalty = BagPenalty,
        NilValue = NilValue,
    };
}

public sealed class SpadesPlayer
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public Occupant Occupant { get; set; }
    public List<Card> Hand { get; set; } = [];

    /// <summary>
    /// Null until this seat has bid. Not zero: zero is a nil bid, which is a real and very
    /// consequential bid.
    /// </summary>
    public int? Bid { get; set; }

    public int Tricks { get; set; }

    /// <summary>Seats 0 and 2 are team 0; seats 1 and 3 are team 1.</summary>
    [JsonIgnore]
    public int Team => SpadesGame.TeamOf(Index);
}

[JsonConverter(typeof(JsonStringEnumConverter<SpadesPhase>))]
public enum SpadesPhase { Idle, Bidding, Play, HandOver, GameOver }

/// <summary>One card on the table and who put it there.</summary>
public sealed record SpadesPlay(int Seat, Card Card);

/// <summary>The trick that has just gone: its four cards, and who took it.</summary>
public sealed class SpadesCompletedTrick
{
    public List<SpadesPlay> Cards { get; set; } = [];
    public int Winner { get; set; }
}

/// <summary>
/// One finished hand, as the history table shows it. Bids and tricks are by seat; delta, scores
/// and bags are by team.
/// </summary>
public sealed class SpadesHandRecord
{
    public int Deal { get; set; }
    public int Dealer { get; set; }
    public List<int> Bids { get; set; } = [];
    public List<int> Tricks { get; set; } = [];
    public List<int> Delta { get; set; } = [];
    public List<int> Scores { get; set; } = [];
    public List<int> Bags { get; set; } = [];
}

/// <summary>How one seat's nil came out.</summary>
public sealed record SpadesNilResult(int Seat, bool Made, int Tricks);

/// <summary>
/// One partnership's share of a hand's score, term by term, so the interface can explain a score
/// without recomputing it a second way.
/// </summary>
public sealed class SpadesTeamScore
{
    public int Team { get; set; }
    public int Contract { get; set; }
    public int Took { get; set; }
    public List<SpadesNilResult> Nils { get; set; } = [];
    public bool Made { get; set; }
    public int Base { get; set; }
    public int Overtricks { get; set; }
    public int NilPoints { get; set; }
    public int BagPenalty { get; set; }
}

/// <summary>What a hand was worth: per-team deltas, the new bag counts, and the detail.</summary>
public sealed class SpadesHandScore
{
    public List<int> Delta { get; set; } = [];
    public List<int> Bags { get; set; } = [];
    public List<SpadesTeamScore> Detail { get; set; } = [];
}

public sealed class SpadesState
{
    public SpadesPhase Phase { get; set; }
    public List<SpadesPlayer> Players { get; set; } = [];
    public SpadesConfig Config { get; set; } = new();

    /// <summary>Every engine writes events here and nowhere else.</summary>
    public EventLog Log { get; set; } = new();

    /// <summary>Null until the first hand is dealt; the very first dealer is drawn.</summary>
    public int? Dealer { get; set; }
    public int DealNumber { get; set; }
    public int Turn { get; set; }
    public int Leader { get; set; }

    public List<SpadesPlay> Trick { get; set; } = [];
    public int TricksPlayed { get; set; }
    public bool SpadesBroken { get; set; }
    public SpadesCompletedTrick? LastTrick { get; set; }

    /// <summary>
    /// Every card that has reached the table this hand, in the order played. Public information
    /// (every card is played face up) kept for the review text. The computer players deliberately
    /// do not read it: <c>ai.js</c> tracks only the current trick and the last one, and the port
    /// keeps that weaker memory so the bots play the game the web bots play.
    /// </summary>
    public List<Card> PlayedThisHand { get; set; } = [];

    /// <summary>Per TEAM, not per player, because the contract is the partnership's.</summary>
    public List<int> Scores { get; set; } = [0, 0];
    public List<int> Bags { get; set; } = [0, 0];

    public List<SpadesHandRecord> History { get; set; } = [];

    /// <summary>The winning team, or null for nobody yet.</summary>
    public int? Winner { get; set; }
}

/// <summary>
/// Everything a seat can do. Every case serialises (with a "type" discriminator) so an action can
/// travel to another player later. Records holding only scalars and cards compare by value.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Start), "start")]
[JsonDerivedType(typeof(Bid), "bid")]
[JsonDerivedType(typeof(Play), "play")]
[JsonDerivedType(typeof(NextHand), "nextHand")]
[JsonDerivedType(typeof(NewGame), "newGame")]
public abstract record SpadesAction
{
    /// <summary>Deal the first hand from <see cref="SpadesPhase.Idle"/>.</summary>
    public sealed record Start : SpadesAction;

    /// <summary>Zero is nil, exactly as <c>game.js</c> represents it.</summary>
    public sealed record Bid(int Tricks) : SpadesAction;

    public sealed record Play(Card Card) : SpadesAction;

    /// <summary>Only when <see cref="SpadesGame.CanDeal"/>.</summary>
    public sealed record NextHand : SpadesAction;

    /// <summary>Back to a fresh <see cref="SpadesPhase.Idle"/>, keeping the names and the rules.</summary>
    public sealed record NewGame : SpadesAction;

    /// <summary>The nil bid, spelled out.</summary>
    public static SpadesAction NilBid => new Bid(0);
}
