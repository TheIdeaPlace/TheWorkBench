using System.Text.Json.Serialization;

namespace TheCardPlace.Sheephead;

// MARK: - Configuration

/// <summary>What happens when everybody passes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SheepheadAllPass>))]
public enum SheepheadAllPass
{
    /// <summary>No picker, everyone for themselves, fewest points wins.</summary>
    Leaster,
    /// <summary>Throw the hand in and deal again.</summary>
    Redeal,
}

/// <summary>The rule options from the web game's settings dialog, with the same defaults.</summary>
public sealed class SheepheadConfig
{
    public static readonly IReadOnlyList<string> DefaultNames = ["You", "Alice", "Ben", "Cara", "Elle", "Finn"];

    /// <summary>
    /// One per seat; seat 0 is the person playing. Padded from <see cref="DefaultNames"/> if too
    /// short for the table.
    /// </summary>
    public List<string> Names { get; set; } = [.. DefaultNames];

    /// <summary>Table size, three to six.</summary>
    public int Players { get; set; } = 5;

    public Difficulty Difficulty { get; set; } = Difficulty.Normal;

    public SheepheadAllPass AllPass { get; set; } = SheepheadAllPass.Leaster;

    /// <summary>Both black queens in one hand after burying doubles the hand.</summary>
    public bool BlackQueenDoubler { get; set; }

    /// <summary>Both red queens in one hand doubles the hand.</summary>
    public bool RedQueenDoubler { get; set; }

    /// <summary>A redeal doubles the next hand; repeated redeals do not stack.</summary>
    public bool RedealDoubler { get; set; }

    /// <summary>A copy that shares nothing with this one: Swift's config is a value.</summary>
    public SheepheadConfig Copy() => new()
    {
        Names = [.. Names],
        Players = Players,
        Difficulty = Difficulty,
        AllPass = AllPass,
        BlackQueenDoubler = BlackQueenDoubler,
        RedQueenDoubler = RedQueenDoubler,
        RedealDoubler = RedealDoubler,
    };
}

/// <summary>How a table of a given size is dealt.</summary>
/// <param name="Hand">Cards per player.</param>
/// <param name="Blind">Cards in the blind.</param>
/// <param name="Partner">Whether the jack of diamonds names a partner.</param>
/// <param name="Exclude">Cards left out of the deck.</param>
public sealed record SheepheadDealSpec(int Hand, int Blind, bool Partner, IReadOnlyList<Card> Exclude);

// MARK: - State

public sealed class SheepheadPlayer
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public Occupant Occupant { get; set; }
    public List<Card> Hand { get; set; } = [];
    public int TricksWon { get; set; }

    /// <summary>Card points taken in tricks this hand.</summary>
    public int Points { get; set; }

    /// <summary>Running game score, in scoring units.</summary>
    public int Score { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<SheepheadPhase>))]
public enum SheepheadPhase { Idle, Pick, Bury, Play, HandOver }

/// <summary>One card on the table.</summary>
public sealed record SheepheadPlay(int Player, Card Card);

/// <summary>A completed trick.</summary>
/// <param name="Points">Points to the winner, including any from the blind.</param>
/// <param name="FromBlind">The leaster's blind, counted on the last trick.</param>
public sealed record SheepheadTrick(List<SheepheadPlay> Plays, int Winner, int Points, int FromBlind);

public sealed record SheepheadPickEntry(int Player, bool Picked);

[JsonConverter(typeof(JsonStringEnumConverter<SheepheadDoublerKind>))]
public enum SheepheadDoublerKind { Black, Red, Redeal }

/// <param name="Player">Who held the pair; null for the redeal doubler.</param>
/// <param name="Text">"both black queens", "the redeal".</param>
public sealed record SheepheadDoubler(SheepheadDoublerKind Kind, int? Player, string Text);

/// <summary>When and how the sides became public.</summary>
public sealed record SheepheadReveal(int Trick, int Player, bool Alone);

/// <summary>The deal as it was made, so a finished hand can be shown in full.</summary>
public sealed record SheepheadDeal(List<List<Card>> Hands, List<Card> Blind);

/// <param name="Multiplier">1, 2 or 3 for a win; 2, 3 or 4 for a loss. 1 for a leaster.</param>
/// <param name="Factor">The doubler factor: 1, 2 or 4.</param>
/// <param name="Label">"a normal win", "schneider", "the picker went down" …</param>
/// <param name="Deltas">Score change per seat; always sums to zero.</param>
/// <param name="Winners">The leaster's winner, or the picker's side.</param>
/// <param name="Summary">The full accounting, as read out.</param>
public sealed record SheepheadResult(
    bool Leaster,
    int PickerPoints,
    int OpponentPoints,
    int BuriedPoints,
    bool PickerWins,
    int Multiplier,
    int Factor,
    string Label,
    List<int> Deltas,
    List<int> Winners,
    string Summary);

/// <summary>One audited record per completed hand.</summary>
/// <param name="Problems">Empty when the hand adds up.</param>
public sealed record SheepheadHandRecord(
    int HandNumber,
    int Dealer,
    List<string> Names,
    SheepheadDeal Dealt,
    List<SheepheadPickEntry> PickLog,
    int? Picker,
    int? Partner,
    bool Alone,
    bool IsLeaster,
    List<Card> Buried,
    int BuriedPoints,
    List<Card> BlindLeft,
    SheepheadReveal? Reveal,
    List<SheepheadTrick> Tricks,
    List<int> Points,
    List<int> TricksWon,
    SheepheadResult Result,
    List<int> ScoresAfter,
    List<string> Problems);

public sealed class SheepheadState
{
    public SheepheadPhase Phase { get; set; }
    public List<SheepheadPlayer> Players { get; set; } = [];
    public SheepheadConfig Config { get; set; } = new();
    public EventLog Log { get; set; } = new();

    /// <summary>Null until the first hand is dealt; the first dealer is drawn at random.</summary>
    public int? Dealer { get; set; }
    public int HandNumber { get; set; }
    public int Turn { get; set; }
    public List<Card> Blind { get; set; } = [];
    public List<Card> Buried { get; set; } = [];
    public int? Picker { get; set; }

    /// <summary>
    /// The seat holding the jack of diamonds when there is one and the picker does not. Private
    /// until <see cref="PartnerRevealed"/>.
    /// </summary>
    public int? Partner { get; set; }

    /// <summary>The ground truth, private until the jack is played or the hand scored.</summary>
    public bool Alone { get; set; }
    public bool PartnerRevealed { get; set; }
    public bool IsLeaster { get; set; }
    public int PassCount { get; set; }
    public List<SheepheadPlay> Trick { get; set; } = [];
    public int Leader { get; set; }
    public SheepheadTrick? LastTrick { get; set; }
    public List<Card> Played { get; set; } = [];
    public SheepheadResult? Result { get; set; }
    public List<SheepheadHandRecord> History { get; set; } = [];
    public SheepheadDeal? Dealt { get; set; }
    public List<SheepheadPickEntry> PickLog { get; set; } = [];

    /// <summary>The blind as the picker took it, in front of the hand until the bury.</summary>
    public List<Card> PickedUp { get; set; } = [];
    public List<SheepheadTrick> TrickLog { get; set; } = [];
    public List<SheepheadDoubler> Doublers { get; set; } = [];

    /// <summary>This hand is doubled because the last one was thrown in.</summary>
    public bool RedealDoubler { get; set; }
    public bool NextHandDoubler { get; set; }
    public SheepheadReveal? RevealInfo { get; set; }

    [JsonIgnore]
    public int Seats => Players.Count;

    [JsonIgnore]
    public SheepheadDealSpec Spec => SheepheadGame.DealSpec(Players.Count);
}

// MARK: - Actions

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Start), "start")]
[JsonDerivedType(typeof(Pick), "pick")]
[JsonDerivedType(typeof(Pass), "pass")]
[JsonDerivedType(typeof(Bury), "bury")]
[JsonDerivedType(typeof(Play), "play")]
[JsonDerivedType(typeof(NextHand), "nextHand")]
[JsonDerivedType(typeof(NewGame), "newGame")]
public abstract record SheepheadAction
{
    /// <summary>Deal the first hand from idle.</summary>
    public sealed record Start : SheepheadAction;

    /// <summary>Take the blind.</summary>
    public sealed record Pick : SheepheadAction;

    /// <summary>Leave the blind for the next player.</summary>
    public sealed record Pass : SheepheadAction;

    /// <summary>
    /// The picker's bury, exactly as many cards as the blind. Records with lists compare by
    /// reference; compare these by <c>Snapshot.Json</c>.
    /// </summary>
    public sealed record Bury(List<Card> Cards) : SheepheadAction;

    public sealed record Play(Card Card) : SheepheadAction;

    /// <summary>Only when <see cref="SheepheadGame.CanDeal"/>.</summary>
    public sealed record NextHand : SheepheadAction;

    /// <summary>Between hands, back to a fresh idle table with the same names and rules.</summary>
    public sealed record NewGame : SheepheadAction;
}

/// <summary>Which side a seat is on.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SheepheadTeam>))]
public enum SheepheadTeam { Picker, Opponent, Solo }
