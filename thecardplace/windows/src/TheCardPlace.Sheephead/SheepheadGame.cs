using System.Globalization;

namespace TheCardPlace.Sheephead;

/// <summary>The rules of sheephead: a port of the Swift <c>SheepheadGame</c>.</summary>
public static class SheepheadGame
{
    public static readonly Card PartnerCard = SheepheadCards.PartnerCard;

    /// <summary>Three to six seats; anything else is clamped.</summary>
    public static int Seats(SheepheadConfig config) => Math.Min(6, Math.Max(3, config.Players));

    private static readonly SheepheadDealSpec Three = new(10, 2, false, []);
    private static readonly SheepheadDealSpec Four = new(7, 2, true, [new Card(Rank.Seven, Suit.Diamonds), new Card(Rank.Eight, Suit.Diamonds)]);
    private static readonly SheepheadDealSpec Five = new(6, 2, true, []);
    private static readonly SheepheadDealSpec Six = new(5, 2, true, []);

    public static SheepheadDealSpec DealSpec(int players) => Math.Min(6, Math.Max(3, players)) switch
    {
        3 => Three,
        4 => Four,
        5 => Five,
        _ => Six,
    };

    public static SheepheadState CreateGame(SheepheadConfig config)
    {
        var n = Seats(config);
        var cfg = config.Copy();
        cfg.Players = n;
        var names = (config.Names ?? []).Take(n).ToList();
        while (names.Count < n)
        {
            var fallback = SheepheadConfig.DefaultNames[names.Count % SheepheadConfig.DefaultNames.Count];
            names.Add(names.Contains(fallback) ? $"Player {Inv(names.Count + 1)}" : fallback);
        }
        cfg.Names = names;
        return new SheepheadState
        {
            Phase = SheepheadPhase.Idle,
            Players = Enumerable.Range(0, n).Select(i => new SheepheadPlayer
            {
                Index = i,
                Name = names[i],
                Occupant = i == 0 ? Occupant.Human : Occupant.Bot,
            }).ToList(),
            Config = cfg,
            Log = new EventLog(),
        };
    }

    // MARK: Queries

    public static int? SeatToAct(SheepheadState state) => state.Phase switch
    {
        SheepheadPhase.Bury => state.Picker,
        SheepheadPhase.Pick or SheepheadPhase.Play => state.Turn,
        _ => null,
    };

    public static bool CanDeal(SheepheadState state) => state.Phase == SheepheadPhase.HandOver;

    /// <summary>The cards a seat may play now; empty when it is not their turn.</summary>
    public static List<Card> LegalPlays(SheepheadState state, int seat)
    {
        if (state.Phase != SheepheadPhase.Play || state.Turn != seat || !HasSeat(state, seat)) return [];
        return LegalCards(state, seat);
    }

    /// <summary>The follow-suit rule alone, without the turn check.</summary>
    internal static List<Card> LegalCards(SheepheadState state, int seat)
    {
        var hand = state.Players[seat].Hand;
        if (state.Trick.Count == 0) return [.. hand];
        var led = SheepheadCards.EffectiveSuit(state.Trick[0].Card);
        var match = hand.Where(c => SheepheadCards.EffectiveSuit(c) == led).ToList();
        return match.Count == 0 ? [.. hand] : match;
    }

    /// <summary>Why a card in this seat's hand cannot be played now, or null if it can.</summary>
    public static string? WhyNot(SheepheadState state, int seat, Card card)
    {
        if (!HasSeat(state, seat)) return "That is not a seat at this table.";
        if (!state.Players[seat].Hand.Contains(card)) return $"You do not hold the {card.Name}.";
        switch (state.Phase)
        {
            case SheepheadPhase.Idle: return "The hand has not been dealt.";
            case SheepheadPhase.HandOver: return "The hand is over.";
            case SheepheadPhase.Pick:
                return state.Turn == seat
                    ? "Cards are for review while you decide whether to pick."
                    : $"Cards are for review; {Name(state, state.Turn)} is deciding whether to pick.";
            case SheepheadPhase.Bury:
                return state.Picker == seat
                    ? "Cards are for review while you choose what to bury."
                    : $"Cards are for review; {Name(state, state.Picker ?? 0)} is burying.";
            default:
                if (state.Turn != seat) return $"It is not your turn; {Name(state, state.Turn)} is to play.";
                if (LegalCards(state, seat).Contains(card)) return null;
                return FollowReason(state);
        }
    }

    /// <summary>"You must follow trump." / "You must follow clubs."</summary>
    internal static string FollowReason(SheepheadState state)
    {
        if (state.Trick.Count == 0) return "That card cannot be played.";
        return $"You must follow {SheepheadCards.EffectiveSuit(state.Trick[0].Card).LowerName}.";
    }

    public static int TrickWinnerIndex(IReadOnlyList<SheepheadPlay> plays)
    {
        var best = 0;
        for (var i = 1; i < plays.Count; i++)
            if (SheepheadCards.Beats(plays[i].Card, plays[best].Card)) best = i;
        return best;
    }

    /// <summary>
    /// Which side a seat is on, from the ground truth. Public information only once the sides are
    /// revealed; the AI goes through <see cref="SheepheadAI"/> instead, which reads only what its
    /// seat may see.
    /// </summary>
    public static SheepheadTeam Team(SheepheadState state, int seat)
    {
        if (state.IsLeaster) return SheepheadTeam.Solo;
        if (seat == state.Picker) return SheepheadTeam.Picker;
        if (!state.Alone && seat == state.Partner) return SheepheadTeam.Picker;
        return SheepheadTeam.Opponent;
    }

    // MARK: The gate

    /// <summary>
    /// THE ONLY WAY IN. Validates the seat, the phase and the action, and refuses with a reason in
    /// the words of the rule. Never throws on bad input, and a refusal leaves the state exactly as
    /// it was: every check comes before the first change.
    /// </summary>
    public static ActionResult ApplyAction(SheepheadState state, int seat, SheepheadAction action, RandomSource rng)
    {
        if (!HasSeat(state, seat)) return ActionResult.Refused("That is not a seat at this table.");
        switch (action)
        {
            case SheepheadAction.Start:
                if (state.Phase != SheepheadPhase.Idle) return ActionResult.Refused("The game has already started.");
                StartHand(state, rng);
                return ActionResult.Ok;

            case SheepheadAction.Pick:
                if (state.Phase != SheepheadPhase.Pick) return ActionResult.Refused("It is not the picking round.");
                if (state.Turn != seat) return ActionResult.Refused("It is not your turn.");
                DoPick(state, seat);
                return ActionResult.Ok;

            case SheepheadAction.Pass:
                if (state.Phase != SheepheadPhase.Pick) return ActionResult.Refused("It is not the picking round.");
                if (state.Turn != seat) return ActionResult.Refused("It is not your turn.");
                DoPass(state, seat, rng);
                return ActionResult.Ok;

            case SheepheadAction.Bury bury:
            {
                if (state.Phase != SheepheadPhase.Bury) return ActionResult.Refused("There is nothing to bury.");
                if (state.Picker != seat) return ActionResult.Refused("Only the picker buries.");
                var want = state.Spec.Blind;
                // A list missing from a message off the wire reads as no cards at all.
                var cards = bury.Cards ?? [];
                if (cards.Count != want) return ActionResult.Refused($"Bury exactly {Prose.Count(want, "card")}.");
                var seen = new List<Card>();
                foreach (var c in cards)
                {
                    if (seen.Contains(c)) return ActionResult.Refused($"The {c.Name} was named twice.");
                    if (!state.Players[seat].Hand.Contains(c)) return ActionResult.Refused($"You do not hold the {c.Name}.");
                    seen.Add(c);
                }
                DoBury(state, seat, seen);
                return ActionResult.Ok;
            }

            case SheepheadAction.Play play:
            {
                var card = play.Card;
                if (state.Phase != SheepheadPhase.Play) return ActionResult.Refused("It is not time to play a card.");
                if (state.Turn != seat) return ActionResult.Refused("It is not your turn.");
                if (!state.Players[seat].Hand.Contains(card)) return ActionResult.Refused($"You do not hold the {card.Name}.");
                if (!LegalCards(state, seat).Contains(card)) return ActionResult.Refused(FollowReason(state));
                return DoPlay(state, seat, card);
            }

            case SheepheadAction.NextHand:
                if (!CanDeal(state)) return ActionResult.Refused("The hand is not over.");
                StartHand(state, rng);
                return ActionResult.Ok;

            case SheepheadAction.NewGame:
                if (state.Phase != SheepheadPhase.Idle && state.Phase != SheepheadPhase.HandOver)
                    return ActionResult.Refused("The hand is not over.");
                Replace(state, CreateGame(state.Config));
                return ActionResult.Ok;

            default:
                // Swift's switch is exhaustive; C# can be handed null or a stranger off the wire.
                return ActionResult.Refused("That is not something you can do in this game.");
        }
    }

    /// <summary>Swift's <c>state = createGame(config)</c>: the same object, every property fresh.</summary>
    private static void Replace(SheepheadState state, SheepheadState fresh)
    {
        state.Phase = fresh.Phase;
        state.Players = fresh.Players;
        state.Config = fresh.Config;
        state.Log = fresh.Log;
        state.Dealer = fresh.Dealer;
        state.HandNumber = fresh.HandNumber;
        state.Turn = fresh.Turn;
        state.Blind = fresh.Blind;
        state.Buried = fresh.Buried;
        state.Picker = fresh.Picker;
        state.Partner = fresh.Partner;
        state.Alone = fresh.Alone;
        state.PartnerRevealed = fresh.PartnerRevealed;
        state.IsLeaster = fresh.IsLeaster;
        state.PassCount = fresh.PassCount;
        state.Trick = fresh.Trick;
        state.Leader = fresh.Leader;
        state.LastTrick = fresh.LastTrick;
        state.Played = fresh.Played;
        state.Result = fresh.Result;
        state.History = fresh.History;
        state.Dealt = fresh.Dealt;
        state.PickLog = fresh.PickLog;
        state.PickedUp = fresh.PickedUp;
        state.TrickLog = fresh.TrickLog;
        state.Doublers = fresh.Doublers;
        state.RedealDoubler = fresh.RedealDoubler;
        state.NextHandDoubler = fresh.NextHandDoubler;
        state.RevealInfo = fresh.RevealInfo;
    }

    private static bool HasSeat(SheepheadState state, int seat) => seat >= 0 && seat < state.Players.Count;

    // MARK: Prose helpers

    internal static string Name(SheepheadState state, int i) => state.Players[i].Name;

    /// <summary>Keeps messages grammatical when the player has left their name as "You".</summary>
    internal static string Vb(SheepheadState state, int i, string third, string second) =>
        state.Players[i].Name.ToLowerInvariant() == "you" ? second : third;

    /// <summary>Digits whatever the culture, so a minus sign is always "-".</summary>
    internal static string Inv(int n) => n.ToString(CultureInfo.InvariantCulture);

    // MARK: Dealing

    private static void Deal(SheepheadState state, RandomSource rng)
    {
        var n = state.Seats;
        var d = state.Spec;
        var deck = SheepheadCards.Deck(n).Shuffled(rng);
        var pos = 0;
        for (var i = 0; i < n; i++)
        {
            state.Players[i].Hand = SheepheadCards.SortHand(deck.GetRange(pos, d.Hand));
            state.Players[i].TricksWon = 0;
            state.Players[i].Points = 0;
            pos += d.Hand;
        }
        state.Blind = deck.GetRange(pos, d.Blind);
        state.Dealt = new SheepheadDeal(state.Players.Select(p => p.Hand.ToList()).ToList(), [.. state.Blind]);
    }

    /// <summary>
    /// Deal a hand, unconditionally. The callers are the start and nextHand actions and the
    /// redeal path in DoPass, which legitimately deals from mid-pick when everybody has passed.
    /// </summary>
    private static void StartHand(SheepheadState state, RandomSource rng)
    {
        var n = state.Seats;
        state.HandNumber += 1;
        // The very first dealer is drawn at random, then the deal rotates.
        var dealer = state.Dealer is { } d ? (d + 1) % n : rng.NextInt(n);
        state.Dealer = dealer;
        state.Buried = [];
        state.Picker = null;
        state.Partner = null;
        state.Alone = false;
        state.PartnerRevealed = false;
        state.IsLeaster = false;
        state.PassCount = 0;
        state.Trick = [];
        state.LastTrick = null;
        state.Played = [];
        state.Result = null;
        state.PickLog = [];
        state.PickedUp = [];
        state.TrickLog = [];
        state.Doublers = [];
        // A redeal doubles the hand that follows it; it does not compound.
        state.RedealDoubler = state.NextHandDoubler;
        state.NextHandDoubler = false;
        state.RevealInfo = null;
        Deal(state, rng);
        state.Phase = SheepheadPhase.Pick;
        state.Leader = (dealer + 1) % n;
        state.Turn = state.Leader;
        state.Log.Add(EventKind.Deal, $"Hand {Inv(state.HandNumber)}. {Name(state, dealer)}{Vb(state, dealer, " deals.", " deal.")}", seat: dealer);
    }

    // MARK: Picking

    private static void DoPick(SheepheadState state, int p)
    {
        var d = state.Spec;
        state.Picker = p;
        // The blind goes to the front of the hand, unsorted, so the picker can see at a glance
        // what they just took. The hand is sorted properly after burying.
        var blind = state.Blind;
        state.PickedUp = [.. blind];
        state.Players[p].Hand = [.. blind, .. SheepheadCards.SortHand(state.Players[p].Hand)];
        state.PickLog.Add(new SheepheadPickEntry(p, true));
        state.Log.Add(EventKind.Pick, $"{Name(state, p)}{Vb(state, p, " picks", " pick")} up the blind ({Inv(d.Blind)} cards).", seat: p);
        state.Log.Add(EventKind.You, "From the blind: " + string.Join(", ", blind.Select(c => c.Name)) + ".", seat: p, cards: blind, audience: p);
        state.Blind = [];
        state.Phase = SheepheadPhase.Bury;
        state.Turn = p;
    }

    private static void DoPass(SheepheadState state, int p, RandomSource rng)
    {
        var n = state.Seats;
        state.PickLog.Add(new SheepheadPickEntry(p, false));
        state.Log.Add(EventKind.Pick, $"{Name(state, p)}{Vb(state, p, " passes.", " pass.")}", seat: p);
        state.PassCount += 1;
        if (state.PassCount >= n)
        {
            if (state.Config.AllPass == SheepheadAllPass.Leaster)
            {
                state.IsLeaster = true;
                state.Phase = SheepheadPhase.Play;
                state.Leader = (state.Dealer!.Value + 1) % n;
                state.Turn = state.Leader;
                state.Log.Add(EventKind.Info, "Everyone passed. This hand is a leaster: no picker, everyone plays for themselves, "
                    + "and the fewest points wins. You must take at least one trick to win. "
                    + "The blind goes to whoever takes the last trick.");
                ComputeDoublers(state);
            }
            else
            {
                var willDouble = state.Config.RedealDoubler;
                if (willDouble) state.NextHandDoubler = true;
                state.Log.Add(EventKind.Info, "Everyone passed. Redealing."
                    + (willDouble ? " The next hand is a doubler, worth twice as much." : ""));
                StartHand(state, rng);
            }
        }
        else
        {
            state.Turn = (p + 1) % n;
        }
    }

    // MARK: Burying

    private static void DoBury(SheepheadState state, int p, List<Card> cards)
    {
        var d = state.Spec;
        var hand = state.Players[p].Hand;
        foreach (var c in cards) hand = hand.Removing(c);
        state.Buried = [.. cards];
        state.Players[p].Hand = SheepheadCards.SortHand(hand);
        state.PickedUp = [];
        state.Log.Add(EventKind.Pick, $"{Name(state, p)}{Vb(state, p, " buries ", " bury ")}{Inv(d.Blind)} cards.", seat: p);
        state.Log.Add(EventKind.You, $"You buried {Prose.Count(SheepheadCards.SumPoints(cards), "point")}.", seat: p, cards: cards, audience: p);

        AssignPartner(state);
        ComputeDoublers(state);

        state.Phase = SheepheadPhase.Play;
        state.Leader = (state.Dealer!.Value + 1) % state.Seats;
        state.Turn = state.Leader;
    }

    /// <summary>
    /// <see cref="SheepheadState.Alone"/> is the ground truth and must stay private: whether the
    /// picker kept or buried the jack of diamonds is nobody else's business until the card shows
    /// up, or until the hand is scored. The only exception is a table with no partner card at
    /// all, where playing alone is a rule everyone knows in advance.
    /// </summary>
    private static void AssignPartner(SheepheadState state)
    {
        var d = state.Spec;
        var picker = state.Picker!.Value;
        if (!d.Partner)
        {
            state.Alone = true;
            state.Partner = null;
            state.PartnerRevealed = true;
            state.Log.Add(EventKind.Info, $"{Name(state, picker)}{Vb(state, picker, " is", " are")} the picker and {Vb(state, picker, "plays", "play")} alone.", seat: picker);
            return;
        }

        int? holder = state.Players.FindIndex(pl => pl.Hand.Contains(PartnerCard)) is var h and >= 0 ? h : null;
        var buriedPartnerCard = state.Buried.Contains(PartnerCard);
        if (holder is null || holder == picker || buriedPartnerCard)
        {
            state.Alone = true;
            state.Partner = null;
        }
        else
        {
            state.Alone = false;
            state.Partner = holder;
        }
        state.PartnerRevealed = false;

        // Deliberately identical wording either way, so the log gives nothing away.
        state.Log.Add(EventKind.Info, $"{Name(state, picker)}{Vb(state, picker, " is", " are")} the picker. The Jack of Diamonds is the partner card.", seat: picker);

        // And what only the picker is told.
        state.Log.Add(EventKind.You, state.Alone
            ? "You have the Jack of Diamonds yourself, so you are playing alone — nobody else knows that yet."
            : "Somebody else holds it and is your secret partner.",
            seat: picker, audience: picker);
    }

    // MARK: Doublers

    private sealed record QueenPair(SheepheadDoublerKind Kind, Card[] Cards, string Text);

    private static readonly QueenPair[] QueenPairs =
    [
        new(SheepheadDoublerKind.Black, [new Card(Rank.Queen, Suit.Clubs), new Card(Rank.Queen, Suit.Spades)], "both black queens"),
        new(SheepheadDoublerKind.Red, [new Card(Rank.Queen, Suit.Hearts), new Card(Rank.Queen, Suit.Diamonds)], "both red queens"),
    ];

    /// <summary>
    /// A pair only counts in one player's own hand, and only once the hands are final — the
    /// picker's changes when they bury. Who holds what is private until scoring; the only thing
    /// said is to the holder, about their own hand.
    /// </summary>
    private static void ComputeDoublers(SheepheadState state)
    {
        state.Doublers = [];
        foreach (var pair in QueenPairs)
        {
            var on = pair.Kind == SheepheadDoublerKind.Black ? state.Config.BlackQueenDoubler : state.Config.RedQueenDoubler;
            if (!on) continue;
            for (var i = 0; i < state.Players.Count; i++)
            {
                var hand = state.Players[i].Hand;
                if (hand.Contains(pair.Cards[0]) && hand.Contains(pair.Cards[1]))
                {
                    state.Doublers.Add(new SheepheadDoubler(pair.Kind, i, pair.Text));
                    state.Log.Add(EventKind.You, $"You hold {pair.Text}, so this hand counts double.", seat: i, cards: pair.Cards, audience: i);
                    break;   // a pair can only sit in one hand
                }
            }
        }
    }

    /// <summary>Everything that multiplies this hand, including the redeal doubler.</summary>
    public static List<SheepheadDoubler> DoublerList(SheepheadState state)
    {
        var output = state.Doublers.ToList();
        if (state.RedealDoubler) output.Add(new SheepheadDoubler(SheepheadDoublerKind.Redeal, null, "the redeal"));
        return output;
    }

    public static int DoublerFactor(SheepheadState state) => 1 << DoublerList(state).Count;

    internal static string DoublerText(SheepheadState state)
    {
        var list = DoublerList(state);
        if (list.Count == 0) return "";
        var bits = list.Select(dbl => dbl.Player is { } p ? Name(state, p) + " held " + dbl.Text : dbl.Text);
        return " Doubled by " + Prose.List(bits) + $", so the hand is worth {Inv(DoublerFactor(state))} times.";
    }

    // MARK: Playing

    private static ActionResult DoPlay(SheepheadState state, int p, Card card)
    {
        state.Players[p].Hand = state.Players[p].Hand.Removing(card);
        state.Trick.Add(new SheepheadPlay(p, card));
        state.Played.Add(card);

        var text = $"{Name(state, p)}{Vb(state, p, " plays ", " play ")}{SheepheadCards.Describe(card)}.";
        // The partner card hitting the table is what makes the sides public. If the picker plays
        // it themselves, that is the moment everyone learns they are alone.
        if (card == PartnerCard && !state.PartnerRevealed && !state.IsLeaster && state.Spec.Partner)
        {
            state.PartnerRevealed = true;
            var alone = p == state.Picker;
            state.RevealInfo = new SheepheadReveal(state.TrickLog.Count + 1, p, alone);
            text += alone
                ? $" That is the picker's own partner card, so {Name(state, p)}{Vb(state, p, " is", " are")} playing alone."
                : $" {Name(state, p)}{Vb(state, p, " is", " are")} the picker's partner.";
        }
        state.Log.Add(EventKind.Play, text, seat: p, cards: [card]);

        if (state.Trick.Count == state.Seats) return ResolveTrick(state);
        state.Turn = (p + 1) % state.Seats;
        return ActionResult.Ok;
    }

    private static ActionResult ResolveTrick(SheepheadState state)
    {
        var plays = state.Trick;
        var wi = TrickWinnerIndex(plays);
        var winner = plays[wi].Player;
        var pts = SheepheadCards.SumPoints(plays.Select(pl => pl.Card));
        var isFinal = state.Players[winner].Hand.Count == 0;

        var extra = 0;
        if (isFinal && state.IsLeaster && state.Blind.Count > 0)
        {
            extra = SheepheadCards.SumPoints(state.Blind);
            state.Blind = [];   // consumed: those points now belong to the trick winner
        }

        state.Players[winner].TricksWon += 1;
        state.Players[winner].Points += pts + extra;

        var record = new SheepheadTrick(plays, winner, pts + extra, extra);
        state.LastTrick = record;
        state.TrickLog.Add(record);
        state.Trick = [];
        state.Turn = winner;
        state.Leader = winner;

        var msg = $"{Name(state, winner)}{Vb(state, winner, " takes", " take")} the trick, {Inv(pts + extra)} points.";
        if (extra > 0) msg += $" That includes {Inv(extra)} points from the blind.";
        state.Log.Add(EventKind.Trick, msg, seat: winner, cards: plays.Select(pl => pl.Card));

        if (isFinal) return EndHand(state);
        return ActionResult.Ok;
    }

    // MARK: Scoring

    private static ActionResult EndHand(SheepheadState state)
    {
        state.Phase = SheepheadPhase.HandOver;
        state.PartnerRevealed = true;   // scoring makes the sides public either way
        var result = state.IsLeaster ? ScoreLeaster(state) : ScoreNormal(state);
        state.Result = result;
        for (var i = 0; i < state.Players.Count; i++) state.Players[i].Score += result.Deltas[i];
        state.Log.Add(EventKind.Score, result.Summary, cards: [.. state.Dealt?.Blind ?? [], .. state.Buried]);

        var record = RecordHand(state);
        state.History.Add(record);
        if (record.Problems.Count > 0)
            return ActionResult.Faulted($"Accounting problem in hand {Inv(record.HandNumber)}: " + string.Join(" ", record.Problems));
        return ActionResult.Ok;
    }

    private static SheepheadHandRecord RecordHand(SheepheadState state)
    {
        var problems = Audit(state);
        var dealt = state.Dealt ?? new SheepheadDeal([], []);
        return new SheepheadHandRecord(
            HandNumber: state.HandNumber,
            Dealer: state.Dealer ?? 0,
            Names: state.Players.Select(p => p.Name).ToList(),
            Dealt: new SheepheadDeal(dealt.Hands.Select(h => h.ToList()).ToList(), [.. dealt.Blind]),
            PickLog: [.. state.PickLog],
            Picker: state.Picker,
            Partner: state.Partner,
            Alone: state.Alone,
            IsLeaster: state.IsLeaster,
            Buried: [.. state.Buried],
            BuriedPoints: SheepheadCards.SumPoints(state.Buried),
            BlindLeft: [.. state.Blind],
            Reveal: state.RevealInfo,
            Tricks: [.. state.TrickLog],
            Points: state.Players.Select(p => p.Points).ToList(),
            TricksWon: state.Players.Select(p => p.TricksWon).ToList(),
            Result: state.Result!,
            ScoresAfter: state.Players.Select(p => p.Score).ToList(),
            Problems: problems);
    }

    /// <summary>
    /// Independent re-check of a finished hand. This does not trust the running totals: it
    /// re-adds the card values straight from the recorded tricks.
    /// </summary>
    internal static List<string> Audit(SheepheadState state)
    {
        var problems = new List<string>();
        var n = state.Seats;
        var d = state.Spec;
        if (state.Dealt is not { } dealt || state.Result is not { } result) return ["The hand was never dealt."];

        var all = dealt.Hands.SelectMany(h => h).Concat(dealt.Blind).ToList();
        var deck = SheepheadCards.Deck(n);
        if (!new HashSet<Card>(all).SetEquals(deck) || all.Count != deck.Count)
            problems.Add("The deal does not match the deck.");

        var byPlayer = new int[n];
        var playedCards = new List<Card>();
        foreach (var t in state.TrickLog)
        {
            var sum = 0;
            foreach (var pl in t.Plays)
            {
                playedCards.Add(pl.Card);
                sum += SheepheadCards.Points(pl.Card);
            }
            if (sum + t.FromBlind != t.Points)
                problems.Add($"Trick points do not add up ({Inv(sum)} plus {Inv(t.FromBlind)} from the blind is not {Inv(t.Points)}).");
            byPlayer[t.Winner] += t.Points;
        }

        if (state.TrickLog.Count != d.Hand)
            problems.Add($"Expected {Inv(d.Hand)} tricks but recorded {Inv(state.TrickLog.Count)}.");
        if (playedCards.Count != n * d.Hand)
            problems.Add($"Expected {Inv(n * d.Hand)} cards played but recorded {Inv(playedCards.Count)}.");
        if (playedCards.Distinct().Count() != playedCards.Count) problems.Add("A card was played more than once.");

        for (var i = 0; i < n; i++)
            if (byPlayer[i] != state.Players[i].Points)
                problems.Add($"Recorded points for {Name(state, i)} ({Inv(state.Players[i].Points)}) do not match the tricks they took ({Inv(byPlayer[i])}).");

        var taken = state.Players.Sum(p => p.Points);
        var buriedPts = SheepheadCards.SumPoints(state.Buried);
        var left = SheepheadCards.SumPoints(state.Blind);
        var total = taken + buriedPts + left;
        if (total != SheepheadCards.TotalPoints)
            problems.Add($"Points do not total {Inv(SheepheadCards.TotalPoints)}: {Inv(taken)} taken plus {Inv(buriedPts)} buried plus {Inv(left)} left in the blind is {Inv(total)}.");

        if (result.Deltas.Sum() != 0)
            problems.Add($"Score changes are not zero sum (they total {Inv(result.Deltas.Sum())}).");

        if (!state.IsLeaster && state.Picker is { } picker)
        {
            var pickerPts = buriedPts + state.Players[picker].Points;
            if (!state.Alone && state.Partner is { } partner) pickerPts += state.Players[partner].Points;
            if (pickerPts != result.PickerPoints)
                problems.Add($"The picker total in the summary ({Inv(result.PickerPoints)}) does not match the tricks and bury ({Inv(pickerPts)}).");
            if (result.PickerPoints + result.OpponentPoints != SheepheadCards.TotalPoints)
                problems.Add($"The two sides in the summary total {Inv(result.PickerPoints + result.OpponentPoints)}, not {Inv(SheepheadCards.TotalPoints)}.");
        }
        return problems;
    }

    internal static SheepheadResult ScoreLeaster(SheepheadState state)
    {
        var n = state.Seats;
        var best = -1;
        for (var i = 0; i < n; i++)
        {
            var pl = state.Players[i];
            if (pl.TricksWon == 0) continue;
            if (best < 0) { best = i; continue; }
            var b = state.Players[best];
            if (pl.Points < b.Points || (pl.Points == b.Points && pl.TricksWon < b.TricksWon)) best = i;
        }
        if (best < 0) best = 0;
        var lf = DoublerFactor(state);
        var deltas = Enumerable.Range(0, n).Select(i => (i == best ? n - 1 : -1) * lf).ToList();

        var lines = string.Join(", ", state.Players.Select(p =>
            p.Name + " " + Inv(p.Points) + (p.TricksWon == 0 ? " (no tricks, not eligible)" : "")));

        // The blind was never picked up, so nobody has seen it until now.
        var blindText = "";
        if (state.Dealt is { } dealt && dealt.Blind.Count > 0)
        {
            var last = state.TrickLog.Count > 0 ? state.TrickLog[^1] : null;
            blindText = " The blind held " + string.Join(" and ", dealt.Blind.Select(c => c.Name))
                + $", worth {Inv(SheepheadCards.SumPoints(dealt.Blind))}, and went to "
                + (last is null ? "the last trick" : Name(state, last.Winner)) + " with the last trick.";
        }

        var summary = "Leaster result: " + lines + "." + blindText + " " + state.Players[best].Name
            + $" takes the fewest points and wins, {Inv((n - 1) * lf)} points." + DoublerText(state);
        return new SheepheadResult(
            Leaster: true, PickerPoints: 0, OpponentPoints: 0, BuriedPoints: 0, PickerWins: false,
            Multiplier: 1, Factor: lf, Label: "leaster", Deltas: deltas, Winners: [best], Summary: summary);
    }

    internal static SheepheadResult ScoreNormal(SheepheadState state)
    {
        var n = state.Seats;
        var picker = state.Picker!.Value;
        var buriedPts = SheepheadCards.SumPoints(state.Buried);
        var pickerTeam = new List<int> { picker };
        if (!state.Alone && state.Partner is { } partner) pickerTeam.Add(partner);

        var pickerPts = buriedPts;
        var pickerTricks = 0;
        foreach (var s in pickerTeam)
        {
            pickerPts += state.Players[s].Points;
            pickerTricks += state.Players[s].TricksWon;
        }
        var oppPts = SheepheadCards.TotalPoints - pickerPts;
        var totalTricks = state.Spec.Hand;

        bool pickerWins;
        int mult;
        string label;
        if (pickerPts >= 61)
        {
            pickerWins = true;
            if (pickerTricks == totalTricks) { mult = 3; label = "no tricks for the other team"; }
            else if (pickerPts >= 91) { mult = 2; label = "schneider"; }
            else { mult = 1; label = "a normal win"; }
        }
        else
        {
            pickerWins = false;
            if (pickerTricks == 0) { mult = 4; label = "the picker took no tricks"; }
            else if (pickerPts <= 30) { mult = 3; label = "schneider against the picker"; }
            else { mult = 2; label = "the picker went down"; }
        }

        var isOpp = Enumerable.Range(0, n).Select(i => !pickerTeam.Contains(i)).ToList();
        var oppCount = isOpp.Count(o => o);

        // Doublers multiply the whole hand, win or lose, rather than rewarding the holder — so
        // holding both black queens and going down costs double too.
        var factor = DoublerFactor(state);
        var stake = 2 * mult * factor;
        var pot = stake * oppCount;
        var partnerShare = pickerTeam.Count > 1 ? pot / 3 : 0;
        var pickerShare = pot - partnerShare;

        var deltas = new List<int>(new int[n]);
        var sign = pickerWins ? 1 : -1;
        deltas[picker] = sign * pickerShare;
        if (pickerTeam.Count > 1) deltas[pickerTeam[1]] = sign * partnerShare;
        for (var m = 0; m < n; m++)
            if (isOpp[m]) deltas[m] = -sign * stake;

        // The defending side is named neutrally: there is one summary and it outlives the
        // moment, so it has to be true from every seat.
        const string defenceText = "the defenders";

        var teamText = state.Alone
            ? Name(state, picker) + " alone"
            : Name(state, picker) + " and " + Name(state, state.Partner!.Value);
        if (state.Alone && state.Spec.Partner && state.Buried.Contains(PartnerCard))
            teamText += " (the Jack of Diamonds was buried)";

        // Name the cards, not just the total — at hand end everything is public, and "21 buried"
        // tells you nothing about what actually went down.
        var buriedText = state.Buried.Count == 0 ? ""
            : " Buried: " + string.Join(", ", state.Buried.Select(c => c.Name)) + $" ({Prose.Count(buriedPts, "point")}).";
        var blindText = state.Dealt is { } dealt && dealt.Blind.Count > 0
            ? " The blind held " + string.Join(" and ", dealt.Blind.Select(c => c.Name)) + "."
            : "";

        var summary = "Hand over. " + teamText + $" took {Inv(pickerPts)} points (including {Inv(buriedPts)} buried); "
            + defenceText + $" took {Inv(oppPts)}." + blindText + buriedText + " "
            + (pickerWins ? "The picker's team wins" : "The picker's team loses") + " — " + label + "."
            + DoublerText(state) + " "
            + string.Join(", ", state.Players.Select((p, i) => p.Name + " " + (deltas[i] >= 0 ? "+" : "") + Inv(deltas[i])))
            + ".";

        return new SheepheadResult(
            Leaster: false, PickerPoints: pickerPts, OpponentPoints: oppPts, BuriedPoints: buriedPts,
            PickerWins: pickerWins, Multiplier: mult, Factor: factor, Label: label, Deltas: deltas,
            Winners: pickerWins ? pickerTeam : Enumerable.Range(0, n).Where(i => isOpp[i]).ToList(), Summary: summary);
    }
}
