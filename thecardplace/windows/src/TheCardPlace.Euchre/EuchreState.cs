using System.Text.Json.Serialization;
using TheCardPlace.Core;

namespace TheCardPlace.Euchre;

// MARK: - Configuration

/// <summary>
/// The rules a table plays by. Defaults match the browser game's settings dialog: ten points,
/// stick the dealer off, going alone allowed, normal opponents.
///
/// The Swift initialiser pads the names to four and refuses a target of zero or less;
/// <see cref="EuchreGame.CreateGame"/> does the same here, so a config built with an object
/// initialiser comes out the same.
/// </summary>
public sealed class EuchreConfig
{
    /// <summary>One per seat; seat 0 is the person playing.</summary>
    public List<string> Names { get; set; } = [.. DefaultNames];
    public Difficulty Difficulty { get; set; } = Difficulty.Normal;
    /// <summary>10 is the standard game; 5, 11 and 15 are offered too.</summary>
    public int PointsToWin { get; set; } = 10;
    /// <summary>If everybody passes twice the dealer must name a suit, so no hand is ever thrown in.</summary>
    public bool StickTheDealer { get; set; }
    /// <summary>Whoever makes trump may send their partner out of the hand, for four points if they take all five tricks.</summary>
    public bool AllowAlone { get; set; } = true;

    public static IReadOnlyList<string> DefaultNames { get; } = ["You", "Ruth", "Dale", "Marta"];

    /// <summary>The choices the settings dialog offers, in its order.</summary>
    public static IReadOnlyList<int> PointsToWinChoices { get; } = [10, 11, 15, 5];

    /// <summary>
    /// Names for the computer players, short and distinct so they stay quick to hear and easy to
    /// tell apart when announced.
    /// </summary>
    public static IReadOnlyList<string> CrewNames { get; } =
    [
        "Ruth", "Marta", "Dale", "Otis", "Winnie", "Hal", "June", "Cyrus",
        "Pearl", "Vernon", "Della", "Amos", "Nell", "Gus", "Iris", "Roy",
        "Bea", "Walt", "Etta", "Merle", "Faye", "Cliff", "Norma", "Lloyd",
    ];

    /// <summary>A copy with the Swift initialiser's rules applied: four names, a positive target.</summary>
    public EuchreConfig Normalized()
    {
        var n = (Names ?? []).Take(EuchreGame.Seats).ToList();
        while (n.Count < EuchreGame.Seats) n.Add(DefaultNames[n.Count]);
        return new EuchreConfig
        {
            Names = n,
            Difficulty = Difficulty,
            PointsToWin = PointsToWin > 0 ? PointsToWin : 10,
            StickTheDealer = StickTheDealer,
            AllowAlone = AllowAlone,
        };
    }
}

// MARK: - Pieces of the state

public sealed class EuchrePlayer
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public Occupant Occupant { get; set; }
    public List<Card> Hand { get; set; } = [];
    public int TricksWon { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<EuchrePhase>))]
public enum EuchrePhase { Idle, Bid1, Bid2, Discard, Play, HandOver, GameOver }

/// <summary>
/// Where the upcard is. It stays public for the whole hand: everybody at the table saw it, so its
/// identity is common knowledge from the moment it lands.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<EuchreUpcardStatus>))]
public enum EuchreUpcardStatus { None, Up, Taken, TurnedDown }

public sealed record EuchrePlay(int Player, Card Card);

/// <summary>A finished trick. Records with lists compare by reference; compare by Snapshot.Json.</summary>
public sealed record EuchreTrick(int Number, List<EuchrePlay> Plays, int Winner);

/// <summary>Swift's <c>EuchreBid.Kind</c>; a nested type cannot share the name of the record's Kind property.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EuchreBidKind>))]
public enum EuchreBidKind { Pass, OrderUp, Call }

/// <summary>One thing somebody said in the bidding.</summary>
/// <param name="Round">1 while the upcard is on offer, 2 once it has been turned down.</param>
/// <param name="Words">The words used in the log: "orders it up", "takes it up", "names hearts".</param>
public sealed record EuchreBid(int Player, EuchreBidKind Kind, int Round, Suit? Suit = null, bool Alone = false, string Words = "");

/// <summary>The deal as it left the dealer's hands, kept for the end-of-hand review.</summary>
public sealed record EuchreDeal(List<List<Card>> Hands, Card Upcard, List<Card> Kitty);

public sealed class EuchreResult
{
    public bool ThrownIn { get; set; }
    public int? MakerTeam { get; set; }
    public int Made { get; set; }
    public bool Euchred { get; set; }
    public bool Alone { get; set; }
    public Suit? Trump { get; set; }
    public int? Maker { get; set; }
    /// <summary>Points to each side, in team order.</summary>
    public List<int> Deltas { get; set; } = [0, 0];
    public string Label { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<int> Scores { get; set; } = [0, 0];
    public bool GameOver { get; set; }
    public int? GameWinner { get; set; }

    /// <summary>A copy that shares no lists, standing in for Swift's value semantics.</summary>
    public EuchreResult Copy()
    {
        var r = (EuchreResult)MemberwiseClone();
        r.Deltas = [.. Deltas];
        r.Scores = [.. Scores];
        return r;
    }
}

/// <summary>The permanent record of one finished hand, audited the moment it ends.</summary>
public sealed class EuchreHandRecord
{
    public int HandNumber { get; set; }
    public int GameNumber { get; set; }
    public int Dealer { get; set; }
    public Suit? Trump { get; set; }
    public int? Maker { get; set; }
    public bool Alone { get; set; }
    public int? SittingOut { get; set; }
    public Card? Upcard { get; set; }
    public bool TurnedDown { get; set; }
    public Card? Discard { get; set; }
    public List<Card> Kitty { get; set; } = [];
    public List<List<Card>> Dealt { get; set; } = [];
    public List<EuchreTrick> Tricks { get; set; } = [];
    public List<int> TricksWon { get; set; } = [];
    public EuchreResult Result { get; set; } = new();
    public List<int> Scores { get; set; } = [];
    /// <summary>Empty when the accounting check passed.</summary>
    public List<string> Problems { get; set; } = [];
}

// MARK: - State

public sealed class EuchreState
{
    public EuchrePhase Phase { get; set; } = EuchrePhase.Idle;
    public List<EuchrePlayer> Players { get; set; } = [];
    public EuchreConfig Config { get; set; } = new();
    /// <summary>Every engine writes events here and nowhere else.</summary>
    public EventLog Log { get; set; } = new();

    /// <summary>Per SIDE, and reset when somebody wins; <see cref="GamesWon"/> is the running match and is not.</summary>
    public List<int> Scores { get; set; } = [0, 0];
    public List<int> GamesWon { get; set; } = [0, 0];
    public int GameNumber { get; set; }
    public int? GameWinner { get; set; }

    public int? Dealer { get; set; }
    public int HandNumber { get; set; }
    public int Turn { get; set; }
    public int Leader { get; set; }

    /// <summary>Null during the bidding; every card helper answers sensibly with null.</summary>
    public Suit? Trump { get; set; }
    public int? Maker { get; set; }
    public bool Alone { get; set; }
    /// <summary>The maker's partner, while somebody plays alone.</summary>
    public int? SittingOut { get; set; }

    public Card? Upcard { get; set; }
    public EuchreUpcardStatus UpcardStatus { get; set; } = EuchreUpcardStatus.None;
    /// <summary>The turned-down suit, which may not be named in round two.</summary>
    public Suit? DeniedSuit { get; set; }
    /// <summary>The three face-down leftovers. Nobody sees these until the hand is over.</summary>
    public List<Card> Kitty { get; set; } = [];
    /// <summary>What the dealer put back. The dealer's business only, until the hand is over.</summary>
    public Card? Discard { get; set; }

    public List<EuchrePlay> Trick { get; set; } = [];
    public EuchreTrick? LastTrick { get; set; }
    public List<Card> Played { get; set; } = [];
    public List<EuchreTrick> TrickLog { get; set; } = [];
    public List<EuchreBid> BidLog { get; set; } = [];

    public EuchreDeal? Dealt { get; set; }
    public EuchreResult? Result { get; set; }
    public List<EuchreHandRecord> History { get; set; } = [];

    [JsonIgnore]
    public bool IsGameOver => Phase == EuchrePhase.GameOver;
}

// MARK: - Actions

/// <summary>
/// Everything a seat can do. Each case serialises with a "type" discriminator, which is the wire
/// format for playing with other people later.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(EuchreAction.Start), "start")]
[JsonDerivedType(typeof(EuchreAction.OrderUp), "orderUp")]
[JsonDerivedType(typeof(EuchreAction.Pass), "pass")]
[JsonDerivedType(typeof(EuchreAction.CallSuit), "callSuit")]
[JsonDerivedType(typeof(EuchreAction.Discard), "discard")]
[JsonDerivedType(typeof(EuchreAction.Play), "play")]
[JsonDerivedType(typeof(EuchreAction.NextHand), "nextHand")]
[JsonDerivedType(typeof(EuchreAction.NewGame), "newGame")]
public abstract record EuchreAction
{
    /// <summary>Deal the first hand from Idle.</summary>
    public sealed record Start : EuchreAction;
    /// <summary>Round one: take the upcard's suit as trump. The dealer takes the card.</summary>
    public sealed record OrderUp(bool Alone) : EuchreAction;
    public sealed record Pass : EuchreAction;
    /// <summary>Round two: name any suit but the one turned down.</summary>
    public sealed record CallSuit(Suit Suit, bool Alone) : EuchreAction;
    /// <summary>The dealer puts one card back after taking the upcard.</summary>
    public sealed record Discard(Card Card) : EuchreAction;
    public sealed record Play(Card Card) : EuchreAction;
    /// <summary>Only when <see cref="EuchreGame.CanDeal"/>.</summary>
    public sealed record NextHand : EuchreAction;
    /// <summary>From GameOver back to a fresh idle table, keeping names, config and the games-won record.</summary>
    public sealed record NewGame : EuchreAction;
}
