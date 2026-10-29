using System.Globalization;
using TheCardPlace.Core;
using TheCardPlace.Sheephead;

namespace TheCardPlace.Play.Games;

/// <summary>
/// Sheephead from the player's chair. A port of the iOS SheepheadSession.
///
/// Hidden information stays hidden: the hand descriptions, the roles at the table and every
/// review sentence come from <see cref="SheepheadReview"/>, which only says what the player's
/// seat is entitled to know. Nothing here reads who the partner is, or whether the picker is
/// alone, except through it or once the sides are public.
/// </summary>
public sealed class SheepheadSession : GameSession<SheepheadState, SheepheadAction>
{
    /// <summary>Cards chosen to bury, in the order chosen.</summary>
    private readonly List<Card> _selected = [];


    public SheepheadSession(AppSettings settings, Announcer announcer, RandomSource? rng = null)
        : base(GameKind.Sheephead, settings, announcer, new SheepheadEngine(), rng) { }

    /// <summary>The table size the settings ask for, three to six.</summary>
    private int SeatsFromSettings => Math.Clamp(Settings.For(GameKind.Sheephead).RuleInt(SheepheadRules.Players), 3, 6);

    protected override SheepheadState CreateState()
    {
        var gs = Settings.For(GameKind.Sheephead);
        var players = SeatsFromSettings;
        return SheepheadGame.CreateGame(new SheepheadConfig
        {
            Names = Settings.Names(players),
            Players = players,
            Difficulty = gs.Difficulty,
            AllPass = gs.Rule(SheepheadRules.AllPass) == "redeal" ? SheepheadAllPass.Redeal : SheepheadAllPass.Leaster,
            BlackQueenDoubler = gs.RuleBool(SheepheadRules.BlackQueenDoubler),
            RedQueenDoubler = gs.RuleBool(SheepheadRules.RedQueenDoubler),
            RedealDoubler = gs.RuleBool(SheepheadRules.RedealDoubler),
        });
    }

    protected override SheepheadAction StartAction => new SheepheadAction.Start();

    /// <summary>
    /// The table may have changed size in the settings. The new state is built after this, from
    /// the same settings, so the chairs are counted the same way here.
    /// </summary>
    protected override void OnNewGame()
    {
        _selected.Clear();
        Controllers = Enumerable.Range(0, SeatsFromSettings)
            .Select(i => i == Me ? SeatController.Local : SeatController.Computer).ToArray();
    }

    private SheepheadPlayer MePlayer => State.Players[Me];
    private string Name(int seat) => State.Players[seat].Name;
    protected override string SeatName(int seat) => Name(seat);
    private bool IsPicker => State.Picker == Me;
    /// <summary>How many cards the picker takes and buries: two at every table size.</summary>
    private int BuryCount => State.Spec.Blind;
    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>A rule's sentence as a clause to follow a colon: "you must follow trump".</summary>
    private static string Clause(string reason)
    {
        var t = reason.Trim().TrimEnd('.');
        return t.Length > 1 && char.IsLower(t[1]) ? char.ToLowerInvariant(t[0]) + t[1..] : t;
    }

    public override string Status => SheepheadReview.Status(State, Me);

    /// <summary>The deal plus the blind, which the picker holds until the bury.</summary>
    public override int HandCapacity => State.Spec.Hand + State.Spec.Blind;

    /// <summary>There is no end to the game; a hand under way is what leaving would lose.</summary>
    public override bool InProgress => State.Phase is SheepheadPhase.Pick or SheepheadPhase.Bury or SheepheadPhase.Play;

    /// <summary>"trump" or "N pts": short enough to print on the face of the card.</summary>
    private static string Badge(Card c) => SheepheadCards.IsTrump(c) ? "trump" : Num(SheepheadCards.Points(c)) + " pts";

    public override IReadOnlyList<HandCard> Hand
    {
        get
        {
            // The engine's order: after picking, the blind sits at the front until the bury.
            var hand = MePlayer.Hand;
            switch (State.Phase)
            {
                case SheepheadPhase.Bury when IsPicker:
                    return hand.Select(c => new HandCard(c, SheepheadReview.Describe(c, State, Me),
                        Chosen: _selected.Contains(c), Badge: Badge(c))).ToList();
                case SheepheadPhase.Play when IsMyTurn:
                    var legal = SheepheadGame.LegalPlays(State, Me);
                    return hand.Select(c =>
                    {
                        var ok = legal.Contains(c);
                        return new HandCard(c, SheepheadReview.Describe(c, State, Me), ok,
                            ok ? null : SheepheadGame.WhyNot(State, Me, c) is { } why ? Clause(why) : null, false, Badge(c));
                    }).ToList();
                default:
                    // Picking, waiting, somebody else burying: every card is readable and nothing
                    // is marked unavailable. Pressing one says what to do instead.
                    return hand.Select(c => new HandCard(c, SheepheadReview.Describe(c, State, Me), Badge: Badge(c))).ToList();
            }
        }
    }

    public override IReadOnlyList<SeatInfo> Seats
    {
        get
        {
            var winning = State.Trick.Count > 0 ? State.Trick[SheepheadGame.TrickWinnerIndex(State.Trick)] : null;
            var toAct = Engine.SeatToAct(State);
            var showPoints = State.Phase is SheepheadPhase.Play or SheepheadPhase.HandOver;
            return State.Players.Select(p =>
            {
                var play = State.Trick.FirstOrDefault(t => t.Player == p.Index);
                string? note = null;
                if (play is not null)
                {
                    var notes = new List<string>();
                    if (State.Trick[0].Player == p.Index) notes.Add("led");
                    if (winning?.Player == p.Index && State.Trick.Count > 1) notes.Add("winning so far");
                    note = notes.Count > 0 ? string.Join(", ", notes) : null;
                }
                // The review's one disclosure rule; the leaster is said once, in the middle of the table.
                var facts = SheepheadReview.RoleTags(State, p.Index, Me).Where(r => r != "leaster").ToList();
                if (State.Phase == SheepheadPhase.Pick && State.PickLog.Any(e => e.Player == p.Index && !e.Picked)) facts.Add("passed");
                if (showPoints) facts.Add(Prose.Count(p.Points, "point"));
                return new SeatInfo(p.Index, p.Name, PositionOf(p.Index), string.Join(", ", facts), play?.Card,
                    play is null ? null : SheepheadCards.Describe(play.Card), note,
                    toAct == p.Index, p.Index == Me, p.Hand.Count,
                    State.Phase switch { SheepheadPhase.Pick => "deciding", SheepheadPhase.Bury => "burying", _ => "to play" });
            }).ToList();
        }
    }

    public override IReadOnlyList<string> TableLines =>
    [
        .. TrickLines(State.Trick.Select(t => (t.Player, t.Card)).ToList(), SheepheadCards.Describe,
            State.Trick.Count > 0 ? State.Trick[SheepheadGame.TrickWinnerIndex(State.Trick)].Player : null),
        .. State.LastTrick is { } lt
            ? LastTrickLines(lt.Plays.Select(t => (t.Player, t.Card)).ToList(), lt.Winner,
                lt.Points > 0 ? $" with {Prose.Count(lt.Points, "point")}" : ", no points", SheepheadCards.Describe)
            : [],
        .. State.Phase == SheepheadPhase.HandOver ? [SheepheadReview.BlindReveal(State)] : (string[])[],
    ];

    /// <summary>The picker, the partner card or the partner once known, the leaster, the doublers the player knows of.</summary>
    public override string TableNote
    {
        get
        {
            var parts = new List<string>();
            switch (State.Phase)
            {
                case SheepheadPhase.Pick:
                    parts.Add("Picking");
                    break;
                case SheepheadPhase.Bury or SheepheadPhase.Play when State.IsLeaster:
                    parts.Add("Leaster: fewest points wins");
                    break;
                case SheepheadPhase.Bury or SheepheadPhase.Play when State.Picker is { } picker:
                    var who = picker == Me ? "you" : Name(picker);
                    if (!State.Spec.Partner) parts.Add($"Picker: {who}, alone");
                    else if (State.Phase == SheepheadPhase.Play && State.PartnerRevealed)
                        parts.Add(State.Alone ? $"Picker: {who}, alone" : $"Picker: {who}. Partner: {(State.Partner == Me ? "you" : Name(State.Partner!.Value))}");
                    else parts.Add($"Picker: {who}. Partner card: {SheepheadCards.PartnerCard.Name}");
                    break;
                case SheepheadPhase.HandOver:
                    return SheepheadReview.ResultHeadline(State, Me);
                default:
                    return "";
            }
            // Who holds a pair of queens is private until the hand is scored; the redeal is public.
            var doublers = SheepheadGame.DoublerList(State).Where(d => d.Player is null || d.Player == Me).Select(d => d.Text).ToList();
            if (doublers.Count > 0) parts.Add("Doubled: " + Prose.List(doublers));
            return string.Join(". ", parts);
        }
    }

    /// <summary>The iOS players table: role, tricks, points this hand and the running score.</summary>
    public override ScoreTable Scores => new("Scores", ["Player", "Role", "Tricks", "Points this hand", "Score"],
        State.Players.Select(p =>
        {
            var roles = SheepheadReview.RoleTags(State, p.Index, Me);
            return (IReadOnlyList<string>)
            [
                p.Name + (p.Index == Me ? " (you)" : ""),
                roles.Count == 0 ? "none" : string.Join(", ", roles),
                Num(p.TricksWon), Num(p.Points), Num(p.Score),
            ];
        }).ToList());

    public override ScoreTable History => new("Hands played", ["Hand", "Picker", "Partner", "Result", .. State.Players.Select(p => p.Name)],
        State.History.Select(h =>
        {
            string picker, partner;
            if (h.IsLeaster)
            {
                picker = "nobody, leaster";
                partner = "none";
            }
            else
            {
                picker = h.Picker is { } pk ? h.Names[pk] : "nobody";
                partner = h.Alone ? "alone" : h.Partner is { } pt ? h.Names[pt] : "none";
            }
            return (IReadOnlyList<string>)
            [
                Num(h.HandNumber), picker, partner, h.Result.Label,
                .. h.Result.Deltas.Select(d => d > 0 ? "+" + Num(d) : Num(d)),
            ];
        }).ToList(), NamedFrom: 4);

    public override GameControl Primary => State.Phase switch
    {
        SheepheadPhase.Idle => Idle("Dealing"),
        SheepheadPhase.Pick when IsMyTurn => Idle("Pick up the blind or pass", "Choose Pick up the blind or Pass"),
        SheepheadPhase.Pick => Waiting(Name(State.Turn)),
        SheepheadPhase.Bury when IsPicker => new GameControl($"Bury {Num(_selected.Count)} of {Num(BuryCount)}", BurySelected, "bury",
            enabled: _selected.Count == BuryCount, hint: $"Choose {Prose.Number(BuryCount)} cards first"),
        SheepheadPhase.Bury => Waiting(Name(State.Picker ?? State.Turn)),
        SheepheadPhase.Play => IsMyTurn ? PlayACard : Waiting(Name(State.Turn)),
        _ => new GameControl("Deal the next hand", NextHand, "deal", 'n'),
    };

    public override IReadOnlyList<GameControl> Secondary =>
        State.Phase == SheepheadPhase.Pick && IsMyTurn
            ? [new GameControl("Pick up the blind", Pick, "pick"), new GameControl("Pass", Pass, "pass")]
            : [];

    public override IReadOnlyList<ReviewItem> Reviews =>
    [
        new("Hand", 'h', () => SheepheadReview.Hand(State, Me)),
        new("Trick", 't', () => SheepheadReview.Trick(State, Me)),
        new("Last trick", 'l', () => SheepheadReview.LastTrick(State, Me)),
        new("Scores", 's', () => SheepheadReview.Scores(State, Me)),
        new("Picker and partner", 'p', () => SheepheadReview.Picker(State, Me)),
        new("Cards played", 'c', () => SheepheadReview.CardsPlayed(State, Me)),
        new("Play order", 'o', () => SheepheadReview.PlayOrder(State, Me)),
        new("Who is here", 'w', () => Who),
        new("Status", null, () => Status),
    ];

    private string Who
    {
        get
        {
            var list = State.Players.Select(p => p.Name + (p.Index == Me ? " (you)" : "") + (p.Index == State.Dealer ? ", dealer" : ""));
            var d = State.Spec;
            var msg = $"{Cap(Prose.Number(State.Seats))} players: {Prose.List(list)}.";
            msg += $" {Prose.Count(d.Hand, "card")} each and {Prose.Count(d.Blind, "card")} in the blind.";
            msg += d.Partner ? " The Jack of Diamonds names the picker's partner." : " The picker always plays alone.";
            return msg;
        }
    }

    /// <summary>Who the table is waiting on, for a refusal.</summary>
    private string WaitingFor => $"Not your turn. Waiting for {Name(Engine.SeatToAct(State) ?? State.Turn)}.";

    public override void Activate(HandCard item)
    {
        switch (State.Phase)
        {
            case SheepheadPhase.Pick:
                Announcer.Error(IsMyTurn ? "Pick or pass first. Pick up the blind and Pass are in the controls." : WaitingFor);
                break;
            case SheepheadPhase.Bury:
                if (!IsPicker)
                {
                    Announcer.Error(WaitingFor);
                    return;
                }
                if (_selected.Remove(item.Card))
                {
                    Announcer.Say($"Removed. {SelectionSummary}");
                }
                else if (_selected.Count >= BuryCount)
                {
                    Announcer.Error($"{Cap(Prose.Number(BuryCount))} cards are already chosen: {_selected.SpokenList()}. Remove one first.");
                    return;
                }
                else
                {
                    _selected.Add(item.Card);
                    // The card's own name now ends ", chosen", which the screen reader reads; this says only the count.
                    Announcer.Say(SelectionSummary);
                    if (_selected.Count == BuryCount) Announcer.Say("Choose Bury to put them down.", AnnouncementCategory.Hint);
                }
                RaiseChanged();
                break;
            case SheepheadPhase.Play:
                if (!IsMyTurn)
                {
                    Announcer.Error(WaitingFor);
                    return;
                }
                if (SheepheadGame.WhyNot(State, Me, item.Card) is { } why)
                {
                    Announcer.Error($"{item.Card.Name} cannot be played: {Clause(why)}.");
                    return;
                }
                Act(new SheepheadAction.Play(item.Card));
                break;
            case SheepheadPhase.HandOver:
                Announcer.Error("The hand is over. Choose Deal the next hand.");
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
            return $"{Cap(Prose.Number(n))} of {Prose.Number(BuryCount)} chosen.";
        }
    }

    private void Pick()
    {
        if (State.Phase != SheepheadPhase.Pick || !IsMyTurn)
        {
            Announcer.Error(WaitingFor);
            return;
        }
        _selected.Clear();
        Act(new SheepheadAction.Pick());
    }

    private void Pass()
    {
        if (State.Phase != SheepheadPhase.Pick || !IsMyTurn)
        {
            Announcer.Error(WaitingFor);
            return;
        }
        Act(new SheepheadAction.Pass());
    }

    private void BurySelected()
    {
        if (State.Phase != SheepheadPhase.Bury || !IsPicker)
        {
            Announcer.Error(WaitingFor);
            return;
        }
        if (_selected.Count != BuryCount)
        {
            Announcer.Error($"Choose exactly {Prose.Number(BuryCount)} cards to bury. {SelectionSummary}");
            return;
        }
        var cards = _selected.ToList();
        _selected.Clear();
        if (!Act(new SheepheadAction.Bury(cards))) _selected.AddRange(cards);
    }

    private void NextHand()
    {
        if (!SheepheadGame.CanDeal(State)) return;
        // The computer's skill takes effect from the next hand.
        State.Config.Difficulty = Settings.For(GameKind.Sheephead).Difficulty;
        Act(new SheepheadAction.NextHand());
    }

    /// <summary>
    /// At the pick and the bury the base says the status and focuses the first card, the blind's
    /// first after picking, so the hand can be read before deciding. When a hand ends, the blind
    /// and the bury are read out once.
    /// </summary>
    protected override void TurnCameToMe()
    {
        // The engine's hand summary has just named the blind and the buried cards, and On the
        // table keeps them; saying them again here was the same list twice.
        if (State.Phase == SheepheadPhase.HandOver) return;
        // At the pick, the choice is a button, not a card: focus goes to Pick up the blind.
        if (State.Phase == SheepheadPhase.Pick && IsMyTurn)
        {
            Announcer.Say(Status, AnnouncementCategory.Turn);
            RequestFocusChoices();
            return;
        }
        base.TurnCameToMe();
    }
}
