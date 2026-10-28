using System.Globalization;
using TheCardPlace.Core;
using TheCardPlace.Cribbage;

namespace TheCardPlace.Play.Games;

/// <summary>
/// One game of cribbage from the player's chair. A port of the iOS CribbageSession.
///
/// During the play a card says what it is and, on your turn, whether it can be played; what the
/// count would become is left to the player. The Counting aid review is there for anyone who asks.
/// </summary>
public sealed class CribbageSession : GameSession<CribbageState, CribbageAction>
{
    /// <summary>Cards chosen to throw to the crib, in the order chosen.</summary>
    private readonly List<Card> _selected = [];

    /// <summary>
    /// Games won and the game number, carried into the fresh state a new game is built from, so
    /// starting over keeps the tally as the iOS game does.
    /// </summary>
    private (List<int> Won, int Number)? _carry;

    public CribbageSession(AppSettings settings, Announcer announcer, RandomSource? rng = null)
        : base(GameKind.Cribbage, settings, announcer, new CribbageEngine(), rng) { }

    /// <summary>The table as the settings describe it right now.</summary>
    private CribbageConfig Config()
    {
        var gs = Settings.For(GameKind.Cribbage);
        return new CribbageConfig
        {
            Names = Settings.Names(CribbageGame.Seats),
            Difficulty = gs.Difficulty,
            TargetScore = gs.RuleInt(CribbageRules.Target),
        };
    }

    protected override CribbageState CreateState()
    {
        var state = CribbageGame.CreateGame(Config());
        if (_carry is { } carry)
        {
            state.GamesWon = carry.Won;
            state.GameNumber = carry.Number;
            _carry = null;
        }
        return state;
    }

    protected override CribbageAction StartAction => new CribbageAction.Start();
    protected override CribbageAction NewGameAction => new CribbageAction.NewGame();

    /// <summary>
    /// A new game keeps the games won. One that follows a finished game is the next game; one
    /// abandoned part way is the same game started again.
    /// </summary>
    protected override void OnNewGame()
    {
        _selected.Clear();
        _carry = ([.. State.GamesWon], State.Phase == CribbagePhase.GameOver ? State.GameNumber + 1 : State.GameNumber);
    }

    private CribbagePlayer MePlayer => State.Players[Me];
    private CribbagePlayer Opponent => State.Players[CribbageGame.Other(Me)];
    private string Name(int seat) => State.Players[seat].Name;
    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Cap(string s) => CribbageGame.Cap(s);

    /// <summary>"your" / "Ruth’s".</summary>
    private string Poss(int seat) => seat == Me ? "your" : Name(seat) + "’s";

    private bool HasThrown => State.HasDiscarded(Me);
    private List<Card> LegalPlays => CribbageGame.LegalPlays(State, Me);
    private bool MustSayGo => State.Phase == CribbagePhase.Play && IsMyTurn && LegalPlays.Count == 0;

    public override string Status => CribbageReview.Status(State, Me);

    /// <summary>Six dealt; four kept after the throw.</summary>
    public override int HandCapacity => CribbageGame.Dealt;

    public override bool InProgress => State.Phase is not (CribbagePhase.Idle or CribbagePhase.CutForDeal or CribbagePhase.GameOver);

    public override IReadOnlyList<HandCard> Hand
    {
        get
        {
            var hand = CribbageCards.SortHand(MePlayer.Hand);
            switch (State.Phase)
            {
                case CribbagePhase.Discard when HasThrown:
                    return hand.Select(c => new HandCard(c, CribbageCards.Describe(c), false, "your throw is already in")).ToList();
                case CribbagePhase.Discard:
                    return hand.Select(c => new HandCard(c, CribbageCards.Describe(c), Chosen: _selected.Contains(c))).ToList();
                case CribbagePhase.Play when IsMyTurn:
                    var legal = LegalPlays;
                    return hand.Select(c => new HandCard(c, CribbageCards.Describe(c), legal.Contains(c),
                        legal.Contains(c) ? null : CribbageGame.WhyNot(State, Me, c))).ToList();
                default:
                    // Reading the hand is the activity of these phases, so no card is marked
                    // unavailable; Enter on one says what the phase is instead.
                    return hand.Select(c => new HandCard(c, CribbageCards.Describe(c))).ToList();
            }
        }
    }

    /// <summary>
    /// Both chairs, each with the card it has down in the current count sequence during the play:
    /// "led", or "last card down" for the most recent card once there is more than one.
    /// </summary>
    public override IReadOnlyList<SeatInfo> Seats
    {
        get
        {
            var seq = State.Phase == CribbagePhase.Play && State.Pile.Count > State.RunStart
                ? State.Pile.Skip(State.RunStart).ToList() : [];
            var toAct = Engine.SeatToAct(State);
            return State.Players.Select(p =>
            {
                var at = seq.FindLastIndex(x => x.Player == p.Index);
                CribbagePilePlay? play = at >= 0 ? seq[at] : null;
                string? note = null;
                if (play is not null)
                {
                    if (at == 0) note = "led";
                    else if (at == seq.Count - 1 && State.LastPlayer == p.Index) note = "last card down";
                }
                var facts = new List<string>();
                if (State.Dealer == p.Index) facts.Add("dealer");
                facts.Add("score " + Num(p.Score));
                switch (State.Phase)
                {
                    case CribbagePhase.Discard when State.HasDiscarded(p.Index):
                        facts.Add("thrown to the crib");
                        break;
                    case CribbagePhase.Play when State.GoSaid[p.Index]:
                        facts.Add("said go");
                        break;
                    case CribbagePhase.Count when toAct == p.Index:
                        facts.Add("counting");
                        break;
                }
                return new SeatInfo(p.Index, p.Name, PositionOf(p.Index), string.Join(", ", facts), play?.Card,
                    play is null ? null : CribbageCards.Describe(play.Card), note,
                    State.Phase == CribbagePhase.Play && toAct == p.Index, p.Index == Me, p.Hand.Count);
            }).ToList();
        }
    }

    /// <summary>The middle of the table: the crib while it is thrown to, the count and the starter in the play, the hand being counted.</summary>
    public override IReadOnlyList<string> TableLines => State.Phase switch
    {
        CribbagePhase.Play => [CribbageReview.Play(State, Me), CribbageReview.DealerAndCrib(State, Me)],
        CribbagePhase.Count or CribbagePhase.RoundOver or CribbagePhase.GameOver => [CribbageReview.LastCount(State, Me), CribbageReview.DealerAndCrib(State, Me)],
        _ => [CribbageReview.DealerAndCrib(State, Me)],
    };

    public override string TableNote => State.Phase switch
    {
        CribbagePhase.Discard => CribText,
        CribbagePhase.Play => CountText + " " + StarterText,
        CribbagePhase.Count => CountStageText,
        CribbagePhase.RoundOver => (CribbageReview.HandSummary(State, Me) is { Length: > 0 } s ? Cap(s) + " " : "") + StarterText,
        CribbagePhase.GameOver => Cap(CribbageReview.HandSummary(State, Me)),
        _ => "",
    };

    private string CountText => $"The count is {Num(State.Count)}.";

    private string StarterText => State.Starter is { } st
        ? $"The starter is the {CribbageCards.Describe(st)}."
        : "The starter has not been turned yet.";

    /// <summary>"your crib" / "Ruth’s crib".</summary>
    private string CribOwner => State.Dealer is { } d ? Poss(d) + " crib" : "nobody's crib";

    /// <summary>The crib is face down until the engine counts it, and that includes the dealer whose crib it is.</summary>
    private bool CribRevealed =>
        State.Crib.Count > 0 && (State.CountStage >= 3 || State.Phase is CribbagePhase.RoundOver or CribbagePhase.GameOver);

    private string CribText
    {
        get
        {
            if (State.Dealer is null) return "Nobody has dealt yet; the lower cut takes the first crib.";
            if (CribRevealed) return Cap(CribOwner) + ": " + CribbageCards.ListNames(CribbageCards.SortHand(State.Crib)) + ".";
            if (State.Crib.Count == 0)
            {
                var thrown = State.SeatsOutstanding.Count == 1 ? " One player has thrown two cards to it." : "";
                return $"{Cap(CribOwner)}. Nothing in it yet.{thrown}";
            }
            return $"{Cap(CribOwner)}, {Prose.Number(State.Crib.Count)} cards in it, face down until it is counted.";
        }
    }

    /// <summary>What the next press of Next will count: whose cards, and the cards themselves when they are face up.</summary>
    private string CountStageText
    {
        get
        {
            if (State.Dealer is not { } dealer) return "";
            var starter = State.Starter is { } st ? $" with the {CribbageCards.Describe(st)}" : "";
            return State.CountStage switch
            {
                0 => $"{Cap(Poss(CribbageGame.Other(dealer)))} hand: " +
                     CribbageCards.ListNames(CribbageCards.SortHand(State.Players[CribbageGame.Other(dealer)].Kept)) + starter + ".",
                1 => $"{Cap(Poss(dealer))} hand: " + CribbageCards.ListNames(CribbageCards.SortHand(State.Players[dealer].Kept)) + starter + ".",
                2 => $"{Cap(Poss(dealer))} crib, four cards face down{starter}.",
                _ => "Every hand has been counted.",
            };
        }
    }

    /// <summary>
    /// The pegboard as numbers: where each peg stands and how far it has to go. Games won joins
    /// once somebody has won one, as the iOS table shows it.
    /// </summary>
    public override ScoreTable Scores
    {
        get
        {
            var target = State.Target;
            var games = State.GamesWon.Any(g => g > 0);
            List<string> columns = ["Player", "Score", "To go"];
            if (games) columns.Add("Games won");
            return new ScoreTable("Scores", columns, State.Players.Select(p =>
            {
                List<string> row = [p.Name + (p.Index == Me ? " (you)" : ""), Num(p.Score), Num(Math.Max(0, target - p.Score))];
                if (games) row.Add(Num(State.GamesWon[p.Index]));
                return (IReadOnlyList<string>)row;
            }).ToList());
        }
    }

    /// <summary>The hands of this game: the dealer, and both scores after each.</summary>
    public override ScoreTable History => new("Hands played", ["Hand", "Dealer", .. State.Players.Select(p => p.Name)],
        State.History.Where(h => h.GameNumber == State.GameNumber).Select(h => (IReadOnlyList<string>)
        [
            Num(h.HandNumber), Name(h.Dealer), .. h.Scores.Select(Num),
        ]).ToList(), NamedFrom: 2);

    /// <summary>Whatever moves the game on: Cut, Throw, Go, Next and Deal take turns here.</summary>
    public override GameControl Primary => State.Phase switch
    {
        CribbagePhase.Idle => Idle("Starting"),
        CribbagePhase.CutForDeal => new GameControl("Cut for deal", Cut, "cut", 'n'),
        CribbagePhase.Discard when HasThrown => Waiting(Opponent.Name),
        CribbagePhase.Discard => new GameControl($"Throw {Num(_selected.Count)} of 2 to the crib", ThrowSelected, "throw",
            enabled: _selected.Count == 2, hint: "Choose two cards first"),
        CribbagePhase.Play when !IsMyTurn => Waiting(Name(State.Turn)),
        CribbagePhase.Play when MustSayGo => new GameControl("Go", SayGo, "go", 'n'),
        CribbagePhase.Play => PlayACard,
        CribbagePhase.Count when !IsMyTurn => Waiting(Name(State.Turn)),
        CribbagePhase.Count => new GameControl("Next", Next, "next", 'n'),
        CribbagePhase.RoundOver => new GameControl("Deal the next hand", NextHand, "deal", 'n'),
        _ => new GameControl("Deal another game", NextHand, "deal", 'n'),
    };

    public override IReadOnlyList<GameControl> Secondary =>
        State.Phase == CribbagePhase.GameOver ? [new GameControl("Start over from the cut", NewGame, "new")] : [];

    public override IReadOnlyList<ReviewItem> Reviews =>
    [
        new("Hand", 'h', () => CribbageReview.Hand(State, Me)),
        new("The play", 't', () => CribbageReview.Play(State, Me)),
        new("Last count", 'l', () => CribbageReview.LastCount(State, Me)),
        new("Scores", 's', () => CribbageReview.Scores(State, Me)),
        new("Dealer and crib", 'p', () => CribbageReview.DealerAndCrib(State, Me)),
        new("Counting aid", 'c', () => CribbageReview.CountingAid(State, Me)),
        new("Play order", 'o', () => CribbageReview.PlayOrder(State, Me)),
        new("Who is here", 'w', () => string.Join(" ", State.Players.Select(p => $"{p.Name}, {(p.Index == Me ? "you" : "computer")}."))),
        new("Status", null, () => Status),
    ];

    public override void Activate(HandCard item)
    {
        switch (State.Phase)
        {
            case CribbagePhase.Discard:
                if (HasThrown)
                {
                    Announcer.Error($"Your two are already in the crib. Waiting for {Opponent.Name}.");
                    return;
                }
                if (_selected.Remove(item.Card))
                {
                    Announcer.Say($"{item.Card.Name} removed. {SelectionSummary}");
                }
                else if (_selected.Count >= 2)
                {
                    Announcer.Error($"Two cards are already chosen: {_selected.SpokenList()}. Remove one first.");
                    return;
                }
                else
                {
                    _selected.Add(item.Card);
                    Announcer.Say($"{item.Card.Name} chosen. {SelectionSummary}");
                }
                RaiseChanged();
                break;
            case CribbagePhase.Play:
                if (!IsMyTurn)
                {
                    Announcer.Error($"Not your turn. Waiting for {Name(State.Turn)}.");
                    return;
                }
                if (CribbageGame.WhyNot(State, Me, item.Card) is { } why)
                {
                    Announcer.Error($"{item.Card.Name} cannot be played: {why}.");
                    return;
                }
                Act(new CribbageAction.Play(item.Card));
                break;
            default:
                Announcer.Error($"{item.Card.Name} is {CribbageReview.IdleReason(State, Me)}.");
                break;
        }
    }

    private string SelectionSummary
    {
        get
        {
            var n = _selected.Count;
            if (n == 0) return "No cards chosen yet.";
            return $"{Cap(Prose.Number(n))} of two chosen" + (n == 2 ? ". Choose Throw to send them to the crib." : ".");
        }
    }

    private void Cut()
    {
        if (State.Phase != CribbagePhase.CutForDeal) return;
        Act(new CribbageAction.Cut());
    }

    private void ThrowSelected()
    {
        if (_selected.Count != 2)
        {
            Announcer.Error($"Choose exactly two cards to throw. {SelectionSummary}");
            return;
        }
        var cards = _selected.ToList();
        _selected.Clear();
        if (!Act(new CribbageAction.Discard(cards))) _selected.AddRange(cards);
    }

    private void SayGo()
    {
        if (State.Phase != CribbagePhase.Play || !IsMyTurn) return;
        Act(new CribbageAction.Go());
    }

    private void Next()
    {
        if (State.Phase != CribbagePhase.Count || !IsMyTurn) return;
        Act(new CribbageAction.Next());
    }

    /// <summary>
    /// Deals the next hand, or from game over the first hand of another game. The computer's skill
    /// takes effect from the next hand, and the target from the next game.
    /// </summary>
    private void NextHand()
    {
        if (!CribbageGame.CanDeal(State)) return;
        _selected.Clear();
        var gs = Settings.For(GameKind.Cribbage);
        State.Config.Difficulty = gs.Difficulty;
        if (State.Phase == CribbagePhase.GameOver) State.Config.TargetScore = gs.RuleInt(CribbageRules.Target);
        Act(new CribbageAction.NextHand());
    }

    /// <summary>
    /// In the play, says it is your turn (and that you must say go when nothing fits) and moves to
    /// the first card you can play. At the throw, moves to the hand without a word: the deal has
    /// just been read. At your count, says so and leaves focus where it is.
    /// </summary>
    protected override void TurnCameToMe()
    {
        switch (State.Phase)
        {
            case CribbagePhase.Play when IsMyTurn:
                var words = Status;
                var legal = LegalPlays;
                if (legal.Count == 0) words += " " + CribbageReview.Prompt(State, Me);
                Announcer.Say(words, AnnouncementCategory.Turn);
                var hand = CribbageCards.SortHand(MePlayer.Hand);
                Card? first = hand.Where(legal.Contains).Cast<Card?>().FirstOrDefault() ?? hand.Cast<Card?>().FirstOrDefault();
                RequestFocus(first?.Id);
                break;
            case CribbagePhase.Discard when !HasThrown:
                RequestFocus(CribbageCards.SortHand(MePlayer.Hand).Cast<Card?>().FirstOrDefault()?.Id);
                break;
            case CribbagePhase.Count when IsMyTurn:
                Announcer.Say(Status, AnnouncementCategory.Turn);
                break;
        }
    }
}
