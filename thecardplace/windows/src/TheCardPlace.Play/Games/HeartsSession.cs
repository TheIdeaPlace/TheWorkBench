using System.Globalization;
using TheCardPlace.Core;
using TheCardPlace.Hearts;

namespace TheCardPlace.Play.Games;

/// <summary>One game of hearts from the player's chair. A port of the iOS HeartsSession.</summary>
public sealed class HeartsSession : GameSession<HeartsState, HeartsAction>
{
    /// <summary>Cards chosen to pass, in the order chosen.</summary>
    private readonly List<Card> _selected = [];

    public HeartsSession(AppSettings settings, Announcer announcer, RandomSource? rng = null)
        : base(GameKind.Hearts, settings, announcer, new HeartsEngine(), rng) { }

    protected override HeartsState CreateState()
    {
        var gs = Settings.For(GameKind.Hearts);
        return HeartsGame.CreateGame(new HeartsConfig
        {
            Names = Settings.Names(HeartsGame.Seats),
            Difficulty = gs.Difficulty,
            PointsToWin = gs.RuleInt(HeartsRules.PointsToWin),
        });
    }

    protected override HeartsAction StartAction => new HeartsAction.Start();
    protected override void OnNewGame() => _selected.Clear();

    private HeartsPlayer MePlayer => State.Players[Me];
    private string Name(int seat) => State.Players[seat].Name;
    protected override string SeatName(int seat) => Name(seat);

    public override string Status => HeartsReview.Status(State, Me);
    public override int HandCapacity => HeartsGame.HandSize;
    public override bool InProgress => State.Phase is not (HeartsPhase.GameOver or HeartsPhase.Idle);

    public override IReadOnlyList<HandCard> Hand
    {
        get
        {
            var hand = HeartsCards.SortHand(MePlayer.Hand);
            switch (State.Phase)
            {
                case HeartsPhase.Passing:
                    var passed = State.Passing[Me] is not null;
                    return hand.Select(c => new HandCard(c, HeartsCards.Describe(c), !passed,
                        passed ? "you have already passed" : null, _selected.Contains(c), Badge(c))).ToList();
                case HeartsPhase.Play when IsMyTurn:
                    var legal = HeartsGame.LegalPlays(State, Me);
                    return hand.Select(c => new HandCard(c, HeartsCards.Describe(c), legal.Contains(c),
                        legal.Contains(c) ? null : HeartsGame.WhyNot(State, Me, c), false, Badge(c))).ToList();
                default:
                    return hand.Select(c => new HandCard(c, HeartsCards.Describe(c), Badge: Badge(c))).ToList();
            }
        }
    }

    /// <summary>The points a card carries, printed under it.</summary>
    private static string? Badge(Card c) =>
        c == HeartsCards.QueenOfSpades ? "13 points" : c.Suit == Suit.Hearts ? "1 point" : null;

    public override IReadOnlyList<SeatInfo> Seats
    {
        get
        {
            HeartsTrickPlay? winning = null;
            foreach (var p in State.Trick)
                if (winning is null || HeartsCards.Beats(p.Card, winning.Card)) winning = p;
            var toAct = Engine.SeatToAct(State);
            return State.Players.Select(p =>
            {
                var play = State.Trick.FirstOrDefault(t => t.Seat == p.Index);
                string? note = null;
                if (play is not null)
                {
                    var notes = new List<string>();
                    if (State.Trick.Count > 0 && State.Trick[0].Seat == p.Index) notes.Add("led");
                    if (winning?.Seat == p.Index && State.Trick.Count > 1) notes.Add("winning so far");
                    note = notes.Count > 0 ? string.Join(", ", notes) : null;
                }
                var detail = Prose.Count(p.TakenPoints, "point") + " this hand";
                return new SeatInfo(p.Index, p.Name, PositionOf(p.Index), detail, play?.Card,
                    play is null ? null : HeartsCards.Describe(play.Card), note,
                    State.Phase == HeartsPhase.Play && toAct == p.Index, p.Index == Me, p.Hand.Count);
            }).ToList();
        }
    }

    public override IReadOnlyList<string> TableLines => [HeartsReview.LastTrick(State, Me)];

    public override string TableNote => State.Phase switch
    {
        HeartsPhase.Passing => State.PassDirection == HeartsPassDirection.Hold ? "No pass this hand" : $"Pass three cards to {State.PassDirection.RecipientPhrase()}",
        HeartsPhase.Play => State.HeartsBroken ? "Hearts are broken" : "Hearts not broken",
        _ => "",
    };

    public override ScoreTable Scores => new("Scores", ["Player", "Score", "This hand", "Tricks"],
        State.Players.Select(p => (IReadOnlyList<string>)
        [
            p.Name + (p.Index == Me ? " (you)" : ""),
            Num(p.Score), Num(p.TakenPoints), Num(p.Taken.Count / HeartsGame.Seats),
        ]).ToList());

    public override ScoreTable History => new("Hands played", ["Hand", "Pass", .. State.Players.Select(p => p.Name)],
        State.History.Select(h => (IReadOnlyList<string>)
        [
            Num(h.Deal), h.PassDirection.RawValue(),
            .. h.Points.Select((pts, i) => h.Shooter == i ? Num(pts) + ", shot the moon" : Num(pts)),
        ]).ToList(), NamedFrom: 2);

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    public override GameControl Primary => State.Phase switch
    {
        HeartsPhase.Idle => Idle("Dealing"),
        HeartsPhase.Passing when State.Passing[Me] is not null => Idle("Waiting for the others"),
        HeartsPhase.Passing => new GameControl($"Pass {_selected.Count} of {HeartsGame.PassCount}", PassSelected, "pass",
            enabled: _selected.Count == HeartsGame.PassCount, hint: "Choose three cards first"),
        HeartsPhase.Play => IsMyTurn ? PlayACard : Waiting(Name(State.Turn)),
        HeartsPhase.HandOver => new GameControl("Deal the next hand", NextHand, "deal", 'n'),
        _ => new GameControl("Start a new game", NewGame, "new", 'n'),
    };

    public override IReadOnlyList<ReviewItem> Reviews =>
    [
        new("Hand", 'h', () => HeartsReview.Hand(State, Me)),
        new("Trick", 't', () => HeartsReview.Trick(State, Me)),
        new("Last trick", 'l', () => HeartsReview.LastTrick(State, Me)),
        new("Scores", 's', () => HeartsReview.Scores(State, Me)),
        new("Points so far", 'p', () => HeartsReview.PointsSoFar(State, Me)),
        new("Cards played", 'c', () => HeartsReview.CardsPlayed(State, Me)),
        new("Play order", 'o', () => HeartsReview.PlayOrder(State, Me)),
        new("Who is here", 'w', () => HeartsReview.Who(State, Me)),
        new("Status", null, () => Status),
    ];

    public override void Activate(HandCard item)
    {
        switch (State.Phase)
        {
            case HeartsPhase.Passing:
                if (State.Passing[Me] is not null)
                {
                    Announcer.Error("You have already passed. Waiting for the others.");
                    return;
                }
                if (_selected.Remove(item.Card))
                {
                    Announcer.Say($"Removed. {SelectionSummary}");
                }
                else if (_selected.Count >= HeartsGame.PassCount)
                {
                    Announcer.Error($"Three cards are already chosen: {_selected.SpokenList()}. Remove one first.");
                    return;
                }
                else
                {
                    _selected.Add(item.Card);
                    // The card's own name now ends ", chosen", which the screen reader reads; this says only the count.
                    Announcer.Say(SelectionSummary);
                    if (_selected.Count == HeartsGame.PassCount) Announcer.Say("Choose Pass to send them.", AnnouncementCategory.Hint);
                }
                RaiseChanged();
                break;
            case HeartsPhase.Play:
                if (!IsMyTurn)
                {
                    Announcer.Error($"Not your turn. Waiting for {Name(State.Turn)}.");
                    return;
                }
                if (HeartsGame.WhyNot(State, Me, item.Card) is { } why)
                {
                    Announcer.Error($"{item.Card.Name} cannot be played: {why}.");
                    return;
                }
                Act(new HeartsAction.Play(item.Card));
                break;
            default:
                Announcer.Request(Status);
                break;
        }
    }

    private string SelectionSummary
    {
        get
        {
            var n = _selected.Count;
            if (n == 0) return "No cards chosen yet.";
            return Prose.AsSentence($"{Prose.Number(n)} of three chosen");
        }
    }

    private void PassSelected()
    {
        if (_selected.Count != HeartsGame.PassCount)
        {
            Announcer.Error($"Choose exactly three cards to pass. {SelectionSummary}");
            return;
        }
        var cards = _selected.ToList();
        _selected.Clear();
        if (!Act(new HeartsAction.Pass(cards))) _selected.AddRange(cards);
    }

    private void NextHand()
    {
        if (!HeartsGame.CanDeal(State)) return;
        Act(new HeartsAction.NextHand());
    }

    protected override void TurnCameToMe()
    {
        if (State.Phase == HeartsPhase.Passing && State.Passing[Me] is null)
        {
            RequestFocus(HeartsCards.SortHand(MePlayer.Hand).FirstOrDefault().Id);
            return;
        }
        base.TurnCameToMe();
    }
}
