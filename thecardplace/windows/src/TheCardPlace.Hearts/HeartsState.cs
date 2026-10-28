using System.Text.Json.Serialization;
using TheCardPlace.Core;

namespace TheCardPlace.Hearts;

// Hearts - the state of a table, and the actions that change it. A port of the
// types in `HeartsEngine/HeartsGame.swift`, spelled as C# classes with public
// get/set properties so that every one of them survives Snapshot.Clone.

/// <summary>
/// The rule options a table is made with. Defaults match the web settings
/// dialog: the standard hundred-point game.
/// </summary>
public sealed class HeartsConfig
{
    public List<string> Names { get; set; } = ["North", "East", "South", "West"];

    /// The browser always plays 'hard' and the Swift default is hard, so this is too. The computer
    /// plays the same on every level (see HeartsAI), so the setting changes nothing in hearts.
    public Difficulty Difficulty { get; set; } = Difficulty.Hard;

    /// The game ends when somebody reaches this. The web dialog offers 50 and 100.
    public int PointsToWin { get; set; } = HeartsGame.DefaultTarget;

    /// Who sits in each chair. Null, the default, is the person in seat 0 and the computer in the
    /// other three, which is all the Swift engine knows. A list says it seat by seat; seats it
    /// does not reach take the default. Not in Swift: it lets a whole table of computers play
    /// (tests, demos) and is the door for tables with other people later.
    public List<Occupant>? Occupants { get; set; }
}

/// <summary>
/// Where the three cards go this hand. The hold hand is not decoration: a
/// four-hand cycle with no hold means the deal number and the direction stay
/// in lockstep for ever.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<HeartsPassDirection>))]
public enum HeartsPassDirection { Left, Right, Across, Hold }

public static class HeartsPassDirectionExtensions
{
    public static readonly HeartsPassDirection[] All =
        [HeartsPassDirection.Left, HeartsPassDirection.Right, HeartsPassDirection.Across, HeartsPassDirection.Hold];

    /// How many seats to the left the cards travel.
    public static int Offset(this HeartsPassDirection d) => d switch
    {
        HeartsPassDirection.Left => 1,
        HeartsPassDirection.Right => HeartsGame.Seats - 1,
        HeartsPassDirection.Across => 2,
        _ => 0,
    };

    /// "the left", "the right", "the player across" — for "Pass three cards to …".
    public static string RecipientPhrase(this HeartsPassDirection d) => d switch
    {
        HeartsPassDirection.Left => "the left",
        HeartsPassDirection.Right => "the right",
        HeartsPassDirection.Across => "the player across",
        _ => "nobody",
    };

    /// The Swift raw value: "left", "right", "across", "hold". The history table's Pass column
    /// and the status line use it.
    public static string RawValue(this HeartsPassDirection d) => d switch
    {
        HeartsPassDirection.Left => "left",
        HeartsPassDirection.Right => "right",
        HeartsPassDirection.Across => "across",
        _ => "hold",
    };
}

public sealed class HeartsPlayer
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public Occupant Occupant { get; set; }
    public List<Card> Hand { get; set; } = [];

    /// Cards won in tricks this hand.
    public List<Card> Taken { get; set; } = [];

    /// Running total; lowest wins.
    public int Score { get; set; }

    /// This hand only, after the moon adjustment.
    public int HandPoints { get; set; }

    /// Points taken so far this hand, from the tricks won. Public information:
    /// everybody watched every trick.
    [JsonIgnore]
    public int TakenPoints => HeartsGame.PointsOf(Taken);

    /// Public by construction: everybody watched the trick she fell in.
    [JsonIgnore]
    public bool HasQueen => Taken.Contains(HeartsCards.QueenOfSpades);
}

[JsonConverter(typeof(JsonStringEnumConverter<HeartsPhase>))]
public enum HeartsPhase { Idle, Passing, Play, HandOver, GameOver }

/// <summary>One card on the table, and who put it there.</summary>
public sealed record HeartsTrickPlay(int Seat, Card Card);

/// <summary>A completed trick.</summary>
public sealed class HeartsLastTrick
{
    public List<HeartsTrickPlay> Cards { get; set; } = [];
    public int Winner { get; set; }
    public int Points { get; set; }

    public HeartsLastTrick() { }

    public HeartsLastTrick(List<HeartsTrickPlay> cards, int winner, int points)
    {
        Cards = cards;
        Winner = winner;
        Points = points;
    }
}

/// <summary>One finished hand, for the history table.</summary>
public sealed class HeartsHandRecord
{
    public int Deal { get; set; }
    public HeartsPassDirection PassDirection { get; set; }

    /// Each seat's points for the hand, after the moon adjustment.
    public List<int> Points { get; set; } = [];

    /// The seat that shot the moon, if anybody did.
    public int? Shooter { get; set; }

    /// Running totals after this hand.
    public List<int> Scores { get; set; } = [];
}

public sealed class HeartsState
{
    public HeartsPhase Phase { get; set; }
    public List<HeartsPlayer> Players { get; set; } = [];
    public HeartsConfig Config { get; set; } = new();
    public EventLog Log { get; set; } = new();

    public int DealNumber { get; set; }
    public HeartsPassDirection PassDirection { get; set; }

    /// Each seat's chosen three, held here until all four are in. Null for a
    /// seat that has not chosen yet. The interface must never show another
    /// seat's entry; whether it is null is the only public part.
    public List<List<Card>?> Passing { get; set; } = [];

    /// What each seat was handed, after the swap. One seat's own row only.
    public List<List<Card>?> Received { get; set; } = [];

    public int Turn { get; set; }
    public int Leader { get; set; }
    public List<HeartsTrickPlay> Trick { get; set; } = [];
    public int TricksPlayed { get; set; }
    public bool HeartsBroken { get; set; }
    public HeartsLastTrick? LastTrick { get; set; }
    public List<HeartsHandRecord> History { get; set; } = [];

    /// The seat with the lowest score once the game is over; null before then
    /// and for a tie.
    public int? Winner { get; set; }

    /// Who has finished choosing — visible at a real table, because you can
    /// see three cards go face down.
    [JsonIgnore]
    public List<bool> PassedIn => Passing.Select(p => p is not null).ToList();
}

/// <summary>
/// Everything a seat can do. Serialises with a "type" discriminator, which is the wire format
/// for tables with other people later. Records holding a list compare by reference: compare
/// actions by Snapshot.Json.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HeartsAction.Start), "start")]
[JsonDerivedType(typeof(HeartsAction.Pass), "pass")]
[JsonDerivedType(typeof(HeartsAction.Play), "play")]
[JsonDerivedType(typeof(HeartsAction.NextHand), "nextHand")]
[JsonDerivedType(typeof(HeartsAction.NewGame), "newGame")]
public abstract record HeartsAction
{
    /// Deal the first hand, from Idle.
    public sealed record Start : HeartsAction;

    /// Give exactly three cards from your hand, during the pass.
    public sealed record Pass(List<Card> Cards) : HeartsAction;

    public sealed record Play(Card Card) : HeartsAction;

    /// Deal the next hand; accepted exactly when CanDeal.
    public sealed record NextHand : HeartsAction;

    /// Back to a fresh Idle with the scores reset and the config kept.
    public sealed record NewGame : HeartsAction;
}
