using System.Globalization;
using TheCardPlace.Core;
using TheCardPlace.Spades;

namespace TheCardPlace.Play.Games;

/// <summary>One game of spades from the player's chair. A port of the iOS SpadesSession.</summary>
public sealed class SpadesSession : GameSession<SpadesState, SpadesAction>
{
    /// <summary>The number in the bid box. Nil has its own button, so it is never reached by stepping.</summary>
    private int _bid = 3;

    public SpadesSession(AppSettings settings, Announcer announcer, RandomSource? rng = null)
        : base(GameKind.Spades, settings, announcer, new SpadesEngine(), rng) { }

    protected override SpadesState CreateState()
    {
        var gs = Settings.For(GameKind.Spades);
        return SpadesGame.CreateGame(new SpadesConfig
        {
            Names = Settings.Names(SpadesGame.Seats),
            Difficulty = gs.Difficulty,
            PointsToWin = gs.RuleInt(SpadesRules.PointsToWin),
        });
    }

    protected override SpadesAction StartAction => new SpadesAction.Start();
    protected override SpadesAction NewGameAction => new SpadesAction.NewGame();

    private SpadesPlayer MePlayer => State.Players[Me];
    private string Name(int seat) => State.Players[seat].Name;
    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    public override string Status => SpadesReview.Status(State, Me);
    public override int HandCapacity => SpadesGame.HandSize;
    public override bool InProgress => State.Phase is not (SpadesPhase.GameOver or SpadesPhase.Idle);

    /// <summary>The bids the box offers: everything legal except nil.</summary>
    private List<int> BidRange
    {
        get
        {
            var legal = SpadesGame.LegalBids(State, Me).Where(b => b > 0).ToList();
            return legal.Count > 0 ? legal : Enumerable.Range(1, SpadesGame.HandSize).ToList();
        }
    }

    public override IReadOnlyList<HandCard> Hand
    {
        get
        {
            var hand = SpadesCards.SortHand(MePlayer.Hand);
            if (State.Phase == SpadesPhase.Play && IsMyTurn)
            {
                var legal = SpadesGame.LegalPlays(State, Me);
                return hand.Select(c => new HandCard(c, SpadesCards.Describe(c), legal.Contains(c),
                    legal.Contains(c) ? null : SpadesGame.WhyNot(State, Me, c), false, Badge(c))).ToList();
            }
            // Bidding included: the whole hand is readable while you decide.
            return hand.Select(c => new HandCard(c, SpadesCards.Describe(c), Badge: Badge(c))).ToList();
        }
    }

    private static string? Badge(Card c) => SpadesCards.Role(c) is { Length: > 0 } r ? r : null;

    private static string BidWord(int? bid) => bid switch { null => "not yet", 0 => "nil", _ => Num(bid.Value) };

    public override IReadOnlyList<SeatInfo> Seats
    {
        get
        {
            var winning = State.Trick.Count > 0 ? SpadesGame.TrickWinner(State.Trick) : null;
            var toAct = Engine.SeatToAct(State);
            return State.Players.Select(p =>
            {
                var play = State.Trick.FirstOrDefault(t => t.Seat == p.Index);
                string? note = null;
                if (play is not null)
                {
                    var notes = new List<string>();
                    if (State.Trick[0].Seat == p.Index) notes.Add("led");
                    if (winning?.Seat == p.Index && State.Trick.Count > 1) notes.Add("winning so far");
                    note = notes.Count > 0 ? string.Join(", ", notes) : null;
                }
                var facts = new List<string>();
                if (p.Index == SpadesGame.PartnerOf(Me)) facts.Add("partner");
                if (State.Dealer == p.Index) facts.Add("dealer");
                facts.Add("bid " + BidWord(p.Bid));
                facts.Add(Prose.Count(p.Tricks, "trick"));
                return new SeatInfo(p.Index, p.Name, PositionOf(p.Index), string.Join(", ", facts), play?.Card,
                    play is null ? null : SpadesCards.Describe(play.Card), note,
                    State.Phase is SpadesPhase.Play or SpadesPhase.Bidding && toAct == p.Index, p.Index == Me, p.Hand.Count);
            }).ToList();
        }
    }

    public override IReadOnlyList<string> TableLines => [SpadesReview.LastTrick(State)];

    public override string TableNote => State.Phase switch
    {
        SpadesPhase.Bidding => "Bidding",
        SpadesPhase.Play => State.SpadesBroken ? "Spades are broken" : "Spades not broken",
        _ => "",
    };

    /// <summary>One row per partnership, your own side first.</summary>
    public override ScoreTable Scores => new("Scores", ["Side", "Score", "Bags", "Contract", "Tricks"],
        Enumerable.Range(0, SpadesGame.Teams).Select(t =>
        {
            var bid = State.Players.Where(p => p.Team == t).All(p => p.Bid is null) ? "not yet" : Num(SpadesGame.ContractOf(State, t));
            return (IReadOnlyList<string>)
            [
                SpadesGame.TeamName(State, t), Num(State.Scores[t]), Num(State.Bags[t]), bid, Num(SpadesGame.TricksOf(State, t)),
            ];
        }).ToList());

    public override ScoreTable History => new("Hands played", ["Hand", "Dealer", SpadesGame.TeamName(State, 0), SpadesGame.TeamName(State, 1)],
        State.History.Select(h => (IReadOnlyList<string>)
        [
            Num(h.Deal), Name(h.Dealer),
            .. Enumerable.Range(0, SpadesGame.Teams).Select(t => $"{SpadesGame.Signed(h.Delta[t])}, score {Num(h.Scores[t])}, {Prose.Count(h.Bags[t], "bag")}"),
        ]).ToList(), NamedFrom: 2);

    public override GameControl Primary => State.Phase switch
    {
        SpadesPhase.Idle => Idle("Dealing"),
        SpadesPhase.Bidding when !IsMyTurn => Waiting(Name(State.Turn)),
        SpadesPhase.Bidding => new GameControl($"Bid {Num(ClampedBid)}", () => Bid(ClampedBid), "bid"),
        SpadesPhase.Play => IsMyTurn ? PlayACard : Waiting(Name(State.Turn)),
        SpadesPhase.HandOver => new GameControl("Deal the next hand", NextHand, "deal", 'n'),
        _ => new GameControl("Start a new game", NewGame, "new", 'n'),
    };

    private int ClampedBid
    {
        get
        {
            var range = BidRange;
            return Math.Clamp(_bid, range.Min(), range.Max());
        }
    }

    public override IReadOnlyList<GameControl> Secondary
    {
        get
        {
            if (State.Phase != SpadesPhase.Bidding || !IsMyTurn) return [];
            var range = BidRange;
            return
            [
                GameControl.Choice("Bid amount", range.Select(b => Prose.Count(b, "trick")).ToList(), range.IndexOf(ClampedBid), i =>
                {
                    _bid = range[i];
                    RaiseChanged();
                }, "amount"),
                new GameControl("Bid nil", () => Bid(0), "nil"),
            ];
        }
    }

    public override IReadOnlyList<ReviewItem> Reviews =>
    [
        new("Hand", 'h', () => SpadesReview.Hand(State, Me)),
        new("Trick", 't', () => SpadesReview.Trick(State)),
        new("Last trick", 'l', () => SpadesReview.LastTrick(State)),
        new("Scores", 's', () => SpadesReview.Scores(State)),
        new("Contract and bids", 'b', () => SpadesReview.Contract(State, Me)),
        new("Cards played", 'c', () => SpadesReview.CardsPlayed(State, Me)),
        new("Play order", 'o', () => SpadesReview.PlayOrder(State)),
        new("Who is here", 'w', () => SpadesReview.WhoIsHere(State, Me)),
        new("Status", null, () => Status),
    ];

    public override void Activate(HandCard item)
    {
        switch (State.Phase)
        {
            case SpadesPhase.Bidding:
                Announcer.Error(IsMyTurn ? "Bid first. The bid amount and Bid nil are in the controls." : $"Bidding. Waiting for {Name(State.Turn)}.");
                break;
            case SpadesPhase.Play:
                if (!IsMyTurn)
                {
                    Announcer.Error($"Not your turn. Waiting for {Name(State.Turn)}.");
                    return;
                }
                if (SpadesGame.WhyNot(State, Me, item.Card) is { } why)
                {
                    Announcer.Error($"{item.Card.Name} cannot be played: {why}.");
                    return;
                }
                Act(new SpadesAction.Play(item.Card));
                break;
            default:
                Announcer.Request(Status);
                break;
        }
    }

    private void Bid(int n)
    {
        if (State.Phase != SpadesPhase.Bidding)
        {
            Announcer.Error("Nobody is bidding.");
            return;
        }
        if (!IsMyTurn)
        {
            Announcer.Error($"Not your turn. Waiting for {Name(State.Turn)} to bid.");
            return;
        }
        Act(new SpadesAction.Bid(n));
    }

    private void NextHand()
    {
        if (!SpadesGame.CanDeal(State)) return;
        Act(new SpadesAction.NextHand());
    }

    /// <summary>At the bid, focus goes to the bid box rather than the cards; the hand is still there to read.</summary>
    protected override void TurnCameToMe()
    {
        if (State.Phase == SpadesPhase.Bidding && IsMyTurn)
        {
            _bid = ClampedBid;
            Announcer.Say(Status, AnnouncementCategory.Turn);
            return;
        }
        base.TurnCameToMe();
    }
}
