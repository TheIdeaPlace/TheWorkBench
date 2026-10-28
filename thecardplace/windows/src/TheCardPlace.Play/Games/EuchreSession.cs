using System.Globalization;
using TheCardPlace.Core;
using TheCardPlace.Euchre;

namespace TheCardPlace.Play.Games;

/// <summary>
/// One game of euchre from the player's chair. A port of the iOS EuchreSession.
///
/// Two things are euchre's own. Trump is unknown for the whole of the bidding, so the hand is
/// described plainly then and with its bowers once a suit is made, and in round one every card
/// says what it would be if the upcard's suit were ordered, which is the question the bid turns
/// on. And while somebody plays alone the player may be sitting out, in which case the whole hand
/// is played by the others and the session only watches.
/// </summary>
public sealed class EuchreSession : GameSession<EuchreState, EuchreAction>
{
    /// <summary>The Go alone switch in the naming round. Applied to the next suit called.</summary>
    private bool _goAlone;

    /// <summary>The last hand whose face-down cards were read out.</summary>
    private int _revealedHand;

    /// <summary>
    /// What a new game keeps from the one before: the games won, the game number, and (after a
    /// finished game) the dealer, so the deal keeps rotating. Null for the very first table.
    /// </summary>
    private (List<int> GamesWon, int GameNumber, int? Dealer)? _carry;

    public EuchreSession(AppSettings settings, Announcer announcer, RandomSource? rng = null)
        : base(GameKind.Euchre, settings, announcer, new EuchreEngine(), rng) { }

    protected override EuchreState CreateState()
    {
        var gs = Settings.For(GameKind.Euchre);
        var state = EuchreGame.CreateGame(new EuchreConfig
        {
            Names = Settings.Names(EuchreGame.Seats),
            Difficulty = gs.Difficulty,
            PointsToWin = gs.RuleInt(EuchreRules.PointsToWin),
            StickTheDealer = gs.RuleBool(EuchreRules.StickTheDealer),
            AllowAlone = gs.RuleBool(EuchreRules.AllowAlone),
        });
        if (_carry is { } c)
        {
            state.GamesWon = [.. c.GamesWon];
            state.GameNumber = c.GameNumber;
            state.Dealer = c.Dealer;
        }
        return state;
    }

    protected override EuchreAction StartAction => new EuchreAction.Start();
    protected override EuchreAction NewGameAction => new EuchreAction.NewGame();

    /// <summary>
    /// The record of games won carries on; the scores do not. After a finished game the next one
    /// is numbered on and the deal rotates on; starting over mid-game replays the same game number.
    /// </summary>
    protected override void OnNewGame()
    {
        _goAlone = false;
        _revealedHand = 0;
        var over = State.Phase == EuchrePhase.GameOver;
        _carry = ([.. State.GamesWon], over ? State.GameNumber + 1 : State.GameNumber, over ? State.Dealer : null);
    }

    private EuchrePlayer MePlayer => State.Players[Me];
    private string Name(int seat) => State.Players[seat].Name;
    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    private bool IsDealer => State.Dealer == Me;
    private bool IsSittingOut => State.SittingOut == Me;
    private bool AllowAlone => State.Config.AllowAlone;

    /// <summary>Stick the dealer, and it has come round to the player: Pass is refused.</summary>
    private bool MustCall => State.Phase == EuchrePhase.Bid2 && IsDealer && State.Config.StickTheDealer;

    /// <summary>The suits that may be named in round two: anything but the turned-down one.</summary>
    private List<Suit> CallableSuits => EuchreCards.Suits.Where(s => s != State.DeniedSuit).ToList();

    private string WaitingFor(string fallback) => Engine.SeatToAct(State) is int seat ? Name(seat) : fallback;

    private string MakerName => State.Maker is int m ? Name(m) : "the maker";

    public override string Status => EuchreReview.Status(State, Me);

    /// <summary>Five, and six for the dealer while choosing what to put back.</summary>
    public override int HandCapacity => EuchreGame.HandSize + 1;

    public override bool InProgress => State.Phase is not (EuchrePhase.GameOver or EuchrePhase.Idle);

    /// <summary>
    /// The hand, trump first once there is a trump, otherwise by suit. Every card can be read in
    /// every phase; only on the player's turn to play does a card say it cannot be played, and why.
    /// </summary>
    public override IReadOnlyList<HandCard> Hand
    {
        get
        {
            var trump = State.Trump;
            var hand = EuchreCards.SortHand(MePlayer.Hand, trump);
            var myPlay = State.Phase == EuchrePhase.Play && IsMyTurn;
            return hand.Select(c =>
            {
                string? mark = State.Phase switch
                {
                    EuchrePhase.Bid1 when State.Upcard is { } up => Prospect(c, up.Suit),
                    EuchrePhase.Play when IsSittingOut => "out of play",
                    _ => null,
                };
                var description = EuchreCards.Describe(c, trump) + (mark is null ? "" : ", " + mark);
                var why = myPlay ? EuchreGame.WhyNot(State, Me, c) : null;
                return new HandCard(c, description, why is null, why, false, Badge(c, trump) ?? mark);
            }).ToList();
        }
    }

    /// <summary>"right bower", "left bower", "trump": the role with its explanation trimmed off, for the face of the card.</summary>
    private static string? Badge(Card c, Suit? trump)
    {
        var role = EuchreCards.Role(c, trump);
        var cut = role.IndexOf(',', StringComparison.Ordinal);
        var shortRole = cut < 0 ? role : role[..cut];
        return shortRole.Length > 0 ? shortRole : null;
    }

    /// <summary>What a card would be if the upcard's suit were trump.</summary>
    private static string? Prospect(Card c, Suit trump) => EuchreCards.Bower(c, trump) switch
    {
        EuchreBower.Right => "would be the right bower",
        EuchreBower.Left => "would be the left bower",
        _ => EuchreCards.IsTrump(c, trump) ? "would be trump" : null,
    };

    public override IReadOnlyList<SeatInfo> Seats
    {
        get
        {
            var winning = State.Trick.Count > 0 ? EuchreGame.TrickWinnerIndex(State.Trick, State.Trump) : -1;
            var toAct = Engine.SeatToAct(State);
            return State.Players.Select(p =>
            {
                var at = State.Trick.FindIndex(t => t.Player == p.Index);
                var play = at < 0 ? null : State.Trick[at];
                string? note = null;
                if (play is not null)
                {
                    var notes = new List<string>();
                    if (at == 0) notes.Add("led");
                    if (at == winning && State.Trick.Count > 1) notes.Add("winning so far");
                    note = notes.Count > 0 ? string.Join(", ", notes) : null;
                }
                var facts = EuchreReview.RoleTags(State, p.Index, Me);
                facts.Add(Prose.Count(p.TricksWon, "trick"));
                return new SeatInfo(p.Index, p.Name, PositionOf(p.Index), string.Join(", ", facts), play?.Card,
                    play is null ? null : EuchreCards.Describe(play.Card, State.Trump), note,
                    toAct == p.Index, p.Index == Me, p.Hand.Count,
                    State.Phase switch { EuchrePhase.Bid1 or EuchrePhase.Bid2 => "bidding", EuchrePhase.Discard => "putting a card back", _ => "to play" });
            }).ToList();
        }
    }

    public override IReadOnlyList<string> TableLines =>
        State.Phase is EuchrePhase.HandOver or EuchrePhase.GameOver && EuchreReview.DealReveal(State) is { } reveal
            ? [EuchreReview.LastTrick(State, Me), reveal]
            : [EuchreReview.LastTrick(State, Me)];

    public override string TableNote => State.Phase switch
    {
        EuchrePhase.Bid1 when State.Upcard is { } up => "Upcard: " + up.Name,
        EuchrePhase.Bid2 when State.Upcard is { } up => up.Name + " turned down",
        EuchrePhase.Discard or EuchrePhase.Play or EuchrePhase.HandOver or EuchrePhase.GameOver when State.Trump is { } t && State.Maker is int m =>
            $"{t.Name()} {(State.Phase is EuchrePhase.HandOver or EuchrePhase.GameOver ? "were" : "are")} trump, made by " +
            (m == Me ? "you" : Name(m)) + (State.Alone ? ", alone" : ""),
        EuchrePhase.HandOver when State.Result?.ThrownIn == true => "Hand thrown in",
        _ => "",
    };

    /// <summary>Scores belong to a side in euchre, so the table has two rows, your own side first.</summary>
    public override ScoreTable Scores
    {
        get
        {
            var us = EuchreGame.TeamOf(Me);
            return new("Scores", ["Side", "Score", "Tricks this hand", "Games won"],
                new[] { us, 1 - us }.Select(t => (IReadOnlyList<string>)
                [
                    SideName(t), Num(State.Scores[t]), Num(SideTricks(t)), Num(State.GamesWon[t]),
                ]).ToList());
        }
    }

    public override ScoreTable History => new("Hands played", ["Hand", "Dealer", "Trump", "Maker", "Result"],
        State.History.Select(h => (IReadOnlyList<string>)
        [
            Num(h.HandNumber), Name(h.Dealer), h.Trump?.Name() ?? "none",
            h.Maker is int m ? Name(m) + (h.Alone ? ", alone" : "") : "nobody",
            h.Result.ThrownIn ? "thrown in" : h.Result.Label,
        ]).ToList());

    private string SideName(int team) =>
        team == EuchreGame.TeamOf(Me) ? $"You and {Name(EuchreGame.PartnerOf(Me))}" : EuchreGame.SideWords(State, team);

    private int SideTricks(int team) =>
        State.Players.Where(p => EuchreGame.TeamOf(p.Index) == team).Sum(p => p.TricksWon);

    /// <summary>
    /// The button that moves the game on. While the player bids it only says what to do: every
    /// bid, Pass included, is a choice beside it, so the N key can never pass by accident.
    /// </summary>
    public override GameControl Primary => State.Phase switch
    {
        EuchrePhase.Idle => Idle("Dealing"),
        EuchrePhase.Bid1 when !IsMyTurn => Waiting(WaitingFor("the table")),
        EuchrePhase.Bid1 => Idle((IsDealer ? "Pick it up" : "Order it up") + " or pass", "Choose from the bids beside it"),
        EuchrePhase.Bid2 when !IsMyTurn => Waiting(WaitingFor("the table")),
        EuchrePhase.Bid2 => Idle(MustCall ? "Name a suit" : "Name a suit or pass",
            MustCall ? "Stick the dealer: you must name a suit" : "Choose from the bids beside it"),
        EuchrePhase.Discard => IsDealer ? Idle("Put a card back", "Choose the card from your hand") : Waiting(WaitingFor("the dealer")),
        EuchrePhase.Play when IsSittingOut => Waiting(WaitingFor("the others")),
        EuchrePhase.Play => IsMyTurn ? PlayACard : Waiting(Name(State.Turn)),
        EuchrePhase.HandOver => new GameControl("Deal the next hand", NextHand, "deal", 'n'),
        _ => new GameControl("Start a new game", NewGame, "new", 'n'),
    };

    /// <summary>
    /// The bids. Round one: order it up (the dealer picks it up), alone if allowed, or pass. Round
    /// two: the Go alone switch, a suit to call for each one that may be named, and Pass unless
    /// stick the dealer has reached the player.
    /// </summary>
    public override IReadOnlyList<GameControl> Secondary
    {
        get
        {
            if (!IsMyTurn) return [];
            switch (State.Phase)
            {
                case EuchrePhase.Bid1:
                {
                    var verb = IsDealer ? "Pick it up" : "Order it up";
                    List<GameControl> bids = [new(verb, () => OrderUp(false), "order")];
                    if (AllowAlone) bids.Add(new GameControl($"{verb} and go alone", () => OrderUp(true), "order-alone"));
                    bids.Add(new GameControl("Pass", Pass, "pass"));
                    return bids;
                }
                case EuchrePhase.Bid2:
                {
                    List<GameControl> bids = [];
                    if (AllowAlone)
                    {
                        bids.Add(GameControl.Toggle("Go alone", _goAlone, on =>
                        {
                            _goAlone = on;
                            RaiseChanged();
                        }, "alone"));
                    }
                    bids.AddRange(CallableSuits.Select(s => new GameControl($"Call {s.LowerName()}", () => CallSuit(s), "call-" + s.LowerName())));
                    if (!MustCall) bids.Add(new GameControl("Pass", Pass, "pass"));
                    return bids;
                }
                default:
                    return [];
            }
        }
    }

    public override IReadOnlyList<ReviewItem> Reviews =>
    [
        new("Hand", 'h', () => EuchreReview.Hand(State, Me)),
        new("Trick", 't', () => EuchreReview.Trick(State, Me)),
        new("Last trick", 'l', () => EuchreReview.LastTrick(State, Me)),
        new("Scores", 's', () => EuchreReview.Scores(State, Me)),
        new("Trump and partner", 'p', () => EuchreReview.TrumpAndPartner(State, Me)),
        new("Cards played", 'c', () => EuchreReview.CardsPlayed(State, Me)),
        new("Play order", 'o', () => EuchreReview.PlayOrder(State, Me)),
        new("Who is here", 'w', () => EuchreReview.WhoIsHere(State, Me)),
        new("Status", null, () => Status),
    ];

    public override void Activate(HandCard item)
    {
        switch (State.Phase)
        {
            case EuchrePhase.Idle:
                Announcer.Error("No hand has been dealt yet.");
                break;
            case EuchrePhase.Bid1:
            case EuchrePhase.Bid2:
                Announcer.Error(IsMyTurn ? "Bid first. The bids are beside the cards." : $"Not your turn. Waiting for {Name(State.Turn)}.");
                break;
            case EuchrePhase.Discard:
                if (!IsDealer)
                {
                    Announcer.Error($"Not your turn. Waiting for {Name(State.Dealer ?? State.Turn)} to put a card back.");
                    return;
                }
                Act(new EuchreAction.Discard(item.Card));
                break;
            case EuchrePhase.Play:
                if (IsSittingOut)
                {
                    Announcer.Error($"You are sitting out this hand while {MakerName} plays alone.");
                    return;
                }
                if (!IsMyTurn)
                {
                    Announcer.Error($"Not your turn. Waiting for {Name(State.Turn)}.");
                    return;
                }
                if (EuchreGame.IllegalReason(State, Me, item.Card) is { } why)
                {
                    Announcer.Error($"{item.Card.Name} cannot be played: {why}.");
                    return;
                }
                Act(new EuchreAction.Play(item.Card));
                break;
            case EuchrePhase.HandOver:
                Announcer.Error("The hand is over. Choose Deal the next hand.");
                break;
            default:
                Announcer.Error("The game is over. Choose Start a new game.");
                break;
        }
    }

    /// <summary>A bid pressed when it is not the player's to make: say why rather than send it.</summary>
    private bool CanBid()
    {
        if (State.Phase is not (EuchrePhase.Bid1 or EuchrePhase.Bid2))
        {
            Announcer.Error("Nobody is bidding.");
            return false;
        }
        if (!IsMyTurn)
        {
            Announcer.Error($"Not your turn. Waiting for {Name(State.Turn)} to bid.");
            return false;
        }
        return true;
    }

    /// <summary>Round one: take the upcard's suit. The dealer picks it up.</summary>
    private void OrderUp(bool alone)
    {
        if (!CanBid()) return;
        Act(new EuchreAction.OrderUp(alone && AllowAlone));
    }

    /// <summary>Round two: name a suit, alone if the switch is on.</summary>
    private void CallSuit(Suit suit)
    {
        if (!CanBid()) return;
        var alone = _goAlone && AllowAlone;
        _goAlone = false;
        if (!Act(new EuchreAction.CallSuit(suit, alone))) _goAlone = alone;
    }

    private void Pass()
    {
        if (!CanBid()) return;
        Act(new EuchreAction.Pass());
    }

    private void NextHand()
    {
        if (!EuchreGame.CanDeal(State)) return;
        _goAlone = false;
        // The computer players' skill takes effect from the next hand.
        State.Config.Difficulty = Settings.For(GameKind.Euchre).Difficulty;
        Act(new EuchreAction.NextHand());
    }

    /// <summary>
    /// Says what the table wants from the player. While bidding focus stays where it is, since the
    /// bids are controls and the hand is there to read; to put a card back or to play, focus goes
    /// to the hand. Between hands, what was face down is read out once and kept in the log.
    /// </summary>
    protected override void TurnCameToMe()
    {
        switch (State.Phase)
        {
            case EuchrePhase.Bid1 or EuchrePhase.Bid2 when IsMyTurn:
                Announcer.Say(Prompt, AnnouncementCategory.Turn);
                RequestFocusChoices();
                break;
            case EuchrePhase.Discard when IsDealer:
                // The engine has already said "You took the upcard. Choose a card to put back."
                RequestFocus(Hand.FirstOrDefault()?.Id);
                break;
            case EuchrePhase.Play when IsMyTurn:
                Announcer.Say(Prompt, AnnouncementCategory.Turn);
                RequestFocus((Hand.FirstOrDefault(c => c.Playable) ?? Hand.FirstOrDefault())?.Id);
                break;
            case EuchrePhase.HandOver or EuchrePhase.GameOver:
                if (_revealedHand != State.HandNumber && EuchreReview.DealReveal(State) is { } reveal)
                {
                    _revealedHand = State.HandNumber;
                    AddLog(reveal);
                    Announcer.Say(reveal, AnnouncementCategory.Table);
                    RaiseChanged();
                }
                break;
        }
    }

    /// <summary>The engine's turn prompt, or the status when it has nothing to add.</summary>
    private string Prompt => EuchreReview.TurnPrompt(State, Me) is { Length: > 0 } p ? p : Status;
}
