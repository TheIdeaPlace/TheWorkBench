using System.Text.Json.Serialization;
using TheCardPlace.Core;

namespace TheCardPlace.Cribbage;

/// <summary>The rule options, each defaulting to the browser game's settings dialog.</summary>
public sealed class CribbageConfig
{
    public static IReadOnlyList<int> Targets { get; } = [121, 61];
    public static IReadOnlyList<string> DefaultNames { get; } = ["You", "Ruth"];

    /// <summary>One per seat; seat 0 is the person playing.</summary>
    public List<string> Names { get; set; } = [.. DefaultNames];
    public Difficulty Difficulty { get; set; } = Difficulty.Normal;
    /// <summary>121 is the standard game; 61 is once round the board.</summary>
    public int TargetScore { get; set; } = 121;
}

public sealed class CribbagePlayer
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public Occupant Occupant { get; set; }
    /// <summary>Cards not yet played.</summary>
    public List<Card> Hand { get; set; } = [];
    /// <summary>The four kept after the discard; what gets counted.</summary>
    public List<Card> Kept { get; set; } = [];
    /// <summary>Played this hand, in order.</summary>
    public List<Card> Played { get; set; } = [];
    public int Score { get; set; }
}

/// <summary>The phases of <c>game.js</c>, by the same names.</summary>
public enum CribbagePhase { Idle, CutForDeal, Discard, Play, Count, RoundOver, GameOver }

public static class CribbagePhaseExtensions
{
    /// <summary>The Swift raw value: "cutForDeal", "roundOver".</summary>
    public static string RawValue(this CribbagePhase p)
    {
        var s = p.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }
}

/// <summary>One card laid during the play.</summary>
public sealed record CribbagePilePlay(int Player, Card Card);

/// <summary>
/// The cut for deal. Both cards are announced and both stay on record while a tie is being re-cut.
/// </summary>
public sealed record CribbageCut(List<Card> Cuts, bool Tie);

public enum CribbageCountKind { Hand, Crib }

public static class CribbageCountKindExtensions
{
    /// <summary>"hand" or "crib", as it is said mid-sentence.</summary>
    public static string RawValue(this CribbageCountKind k) => k == CribbageCountKind.Hand ? "hand" : "crib";
}

/// <summary>One stage of the count, as it was read out.</summary>
public sealed record CribbageCountResult(int Who, CribbageCountKind Kind, ScoreBreakdown Result);

/// <summary>
/// A skunk is being left under half way, a double skunk under a quarter. Reported, not scored: it
/// counts one game won either way.
/// </summary>
public enum CribbageSkunk { Skunk, DoubleSkunk }

public static class CribbageSkunkExtensions
{
    public static string Words(this CribbageSkunk s) => s == CribbageSkunk.Skunk ? "skunk" : "double skunk";
}

public sealed record CribbageHandResult(
    bool IsGameOver, int? Winner, List<int> Scores, CribbageSkunk? Skunk, List<CribbageCountResult> Counts);

/// <summary>What was dealt, for the record.</summary>
public sealed class CribbageDeal
{
    public List<List<Card>> Hands { get; set; } = [];
    public List<Card> Crib { get; set; } = [];
    public Card? Starter { get; set; }
}

/// <summary>The permanent record of one hand, audited.</summary>
public sealed class CribbageHandRecord
{
    public int HandNumber { get; set; }
    public int GameNumber { get; set; }
    public int Dealer { get; set; }
    public Card? Starter { get; set; }
    public CribbageDeal? Dealt { get; set; }
    public List<List<Card>?> Discarded { get; set; } = [];
    public List<List<Card>> Kept { get; set; } = [];
    public List<Card> Crib { get; set; } = [];
    public List<CribbagePilePlay> Pile { get; set; } = [];
    public List<CribbageCountResult> Counts { get; set; } = [];
    public List<int> Scores { get; set; } = [];
    public CribbageHandResult? Result { get; set; }
    public List<string> Problems { get; set; } = [];
}

public sealed class CribbageState
{
    public CribbagePhase Phase { get; set; }
    public List<CribbagePlayer> Players { get; set; } = [];
    public CribbageConfig Config { get; set; } = new();
    /// <summary>Every engine writes events here and nowhere else.</summary>
    public EventLog Log { get; set; } = new();

    public List<int> GamesWon { get; set; } = [0, 0];
    public int GameNumber { get; set; }
    public bool IsGameOver { get; set; }
    public int? GameWinner { get; set; }

    /// <summary>Null until the cut for deal has decided it.</summary>
    public int? Dealer { get; set; }
    public int HandNumber { get; set; }
    /// <summary>
    /// Whose move. Nobody is "on turn" during the discard (both seats choose at once), so there it
    /// names the seat still being waited for.
    /// </summary>
    public int Turn { get; set; }

    public CribbageCut? CutForDeal { get; set; }

    /// <summary>
    /// The undealt remainder. The interface must never show it: forty cards give away the
    /// opponent's hand by elimination.
    /// </summary>
    public List<Card> Deck { get; set; } = [];

    /// <summary>Face down until the count, including to the dealer whose crib it is.</summary>
    public List<Card> Crib { get; set; } = [];
    /// <summary>What each seat threw, private to that seat until the hand is over.</summary>
    public List<List<Card>?> Discarded { get; set; } = [null, null];
    /// <summary>The cut card, public the moment it is turned.</summary>
    public Card? Starter { get; set; }

    /// <summary>
    /// Every card played this hand, in order, never truncated; <see cref="RunStart"/> is the index
    /// where the current count sequence began.
    /// </summary>
    public List<CribbagePilePlay> Pile { get; set; } = [];
    public int RunStart { get; set; }
    /// <summary>The running total of the play, nought to thirty-one.</summary>
    public int Count { get; set; }
    public List<bool> GoSaid { get; set; } = [false, false];
    public int? LastPlayer { get; set; }

    /// <summary>0 non-dealer's hand, 1 dealer's hand, 2 the crib, 3 done.</summary>
    public int CountStage { get; set; }
    public List<CribbageCountResult> CountResults { get; set; } = [];

    public CribbageDeal? Dealt { get; set; }
    public CribbageHandResult? Result { get; set; }
    public List<CribbageHandRecord> History { get; set; } = [];

    /// <summary>The seats that have not yet thrown to the crib.</summary>
    [JsonIgnore]
    public List<int> SeatsOutstanding =>
        Enumerable.Range(0, Players.Count).Where(i => i >= Discarded.Count || Discarded[i] is null).ToList();

    public bool HasDiscarded(int seat) => seat >= 0 && seat < Discarded.Count && Discarded[seat] is not null;

    /// <summary>
    /// The cards in the current count sequence, which is what pairs and runs may look back at, and
    /// no further.
    /// </summary>
    [JsonIgnore]
    public List<Card> RunCards => Pile.Count > RunStart ? Pile.Skip(RunStart).Select(p => p.Card).ToList() : [];

    [JsonIgnore]
    public int Target => Config.TargetScore;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Start), "start")]
[JsonDerivedType(typeof(Cut), "cut")]
[JsonDerivedType(typeof(Discard), "discard")]
[JsonDerivedType(typeof(Play), "play")]
[JsonDerivedType(typeof(Go), "go")]
[JsonDerivedType(typeof(Next), "next")]
[JsonDerivedType(typeof(NextHand), "nextHand")]
[JsonDerivedType(typeof(NewGame), "newGame")]
public abstract record CribbageAction
{
    /// <summary>From idle to the cut for deal.</summary>
    public sealed record Start : CribbageAction;
    /// <summary>Cut for deal; either seat may.</summary>
    public sealed record Cut : CribbageAction;
    /// <summary>
    /// Exactly two cards to the crib. Simultaneous: each seat's two are held until both are in.
    /// Records with lists compare by reference; compare these by <c>Snapshot.Json</c>.
    /// </summary>
    public sealed record Discard(List<Card> Cards) : CribbageAction;
    public sealed record Play(Card Card) : CribbageAction;
    /// <summary>Only for a seat that genuinely cannot play.</summary>
    public sealed record Go : CribbageAction;
    /// <summary>Count the next hand or crib; the seat whose count it is presses it.</summary>
    public sealed record Next : CribbageAction;
    /// <summary>
    /// Deal the next hand, when <c>CanDeal</c>. From gameOver this starts another game straight
    /// away, as the browser game does.
    /// </summary>
    public sealed record NextHand : CribbageAction;
    /// <summary>From gameOver back to idle, keeping names, config and games won.</summary>
    public sealed record NewGame : CribbageAction;
}
