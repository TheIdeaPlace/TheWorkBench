using TheCardPlace.Core;
using static System.FormattableString;

namespace TheCardPlace.Spades;

// The rules of spades, and the only door into them: the port of `spades/js/game.js` by way of
// the Swift SpadesEngine.
//
// Four players in two fixed partnerships: seats 0 and 2 against seats 1 and 3. Thirteen tricks a
// hand, spades always trump. Everybody bids the number of tricks they expect to take, and a
// partnership either makes what the two of them promised between them or loses it all.
//
// The four rules that are actually the game:
//
// 1. THE BID IS A PARTNERSHIP CONTRACT, not a personal one. Two bids are added together and the
//    pair is judged on the total.
// 2. MISSING THE CONTRACT COSTS THE WHOLE BID. Not the difference.
// 3. OVERTRICKS ARE WORTH ONE POINT AND ARE A LIABILITY. Each is a bag; ten bags costs a hundred
//    points.
// 4. NIL. A bid of zero, scored on its own: a hundred if that player takes no trick, minus a
//    hundred if they take one. A failed nil's tricks still count towards the partner's contract.
//
// Every sentence with a number in it is built with FormattableString.Invariant, so a score of
// minus seventy reads "-70" whatever culture the PC is set to (some use U+2212 for the minus).
public static class SpadesGame
{
    public const int Seats = 4;
    public const int HandSize = 13;
    public const int Teams = 2;

    // Defaults, and only defaults. What a given table plays by is in config.
    public const int DefaultTarget = 500;
    public const int DefaultBagLimit = 10;
    public const int DefaultBagPenalty = 100;
    public const int DefaultNilValue = 100;

    /// <summary>
    /// Seats 0 and 2 against 1 and 3. Sitting opposite your partner is the definition here:
    /// everything from the play order to the scoring assumes it.
    /// </summary>
    public static int TeamOf(int seat) => seat % Teams;

    public static int PartnerOf(int seat) => (seat + 2) % Seats;

    // MARK: - The rules this table plays by
    //
    // Read from config every time they are needed, and a nonsense number falls back to the
    // default: the same guard `ruleNumber` gives the web.

    public static int Target(SpadesState state) =>
        state.Config.PointsToWin > 0 ? state.Config.PointsToWin : DefaultTarget;

    public static int BagLimit(SpadesState state) =>
        state.Config.BagLimit > 0 ? state.Config.BagLimit : DefaultBagLimit;

    /// <summary>Zero is allowed: a table that counts bags but does not punish them.</summary>
    public static int BagPenalty(SpadesState state) =>
        state.Config.BagPenalty >= 0 ? state.Config.BagPenalty : DefaultBagPenalty;

    public static int NilValue(SpadesState state) =>
        state.Config.NilValue > 0 ? state.Config.NilValue : DefaultNilValue;

    // MARK: - Creating a game

    public static SpadesState CreateGame(SpadesConfig config)
    {
        var state = new SpadesState();
        Reset(state, config.Copy());
        return state;
    }

    /// <summary>
    /// Every property of a fresh game, in one place, so that <see cref="CreateGame"/> and the
    /// new-game action (Swift's <c>state = createGame(state.config)</c>, which C# cannot do to a
    /// reference it was handed) can never disagree about what a fresh table is.
    /// </summary>
    private static void Reset(SpadesState state, SpadesConfig config)
    {
        var players = new List<SpadesPlayer>();
        for (var i = 0; i < Seats; i++)
        {
            var name = i < config.Names.Count && !string.IsNullOrEmpty(config.Names[i])
                ? config.Names[i] : SpadesConfig.DefaultNames[i];
            players.Add(new SpadesPlayer { Index = i, Name = name, Occupant = i == 0 ? Occupant.Human : Occupant.Bot });
        }
        state.Phase = SpadesPhase.Idle;
        state.Players = players;
        state.Config = config;
        state.Log = new EventLog();
        state.Dealer = null;
        state.DealNumber = 0;
        state.Turn = 0;
        state.Leader = 0;
        state.Trick = [];
        state.TricksPlayed = 0;
        state.SpadesBroken = false;
        state.LastTrick = null;
        state.PlayedThisHand = [];
        state.Scores = [0, 0];
        state.Bags = [0, 0];
        state.History = [];
        state.Winner = null;
    }

    // MARK: - Prose helpers

    /// <summary>A seat's name, for a sentence.</summary>
    public static string Vb(SpadesState state, int seat) =>
        seat >= 0 && seat < state.Players.Count ? state.Players[seat].Name : Invariant($"seat {seat}");

    /// <summary>"You and South": a partnership named by its people rather than a number.</summary>
    public static string TeamName(SpadesState state, int team) =>
        string.Join(" and ", Enumerable.Range(0, Seats).Where(s => TeamOf(s) == team).Select(s => Vb(state, s)));

    /// <summary>
    /// The partnership's contract: the two bids added together, counting only seats that have bid.
    /// </summary>
    public static int ContractOf(SpadesState state, int team)
    {
        var n = 0;
        for (var i = 0; i < Seats; i++)
            if (TeamOf(i) == team && state.Players[i].Bid is int b) n += b;
        return n;
    }

    /// <summary>The tricks the partnership has taken this hand.</summary>
    public static int TricksOf(SpadesState state, int team)
    {
        var n = 0;
        for (var i = 0; i < Seats; i++)
            if (TeamOf(i) == team) n += state.Players[i].Tricks;
        return n;
    }

    // MARK: - Dealing

    private static void NewHand(SpadesState state, RandomSource rng)
    {
        var deck = SpadesCards.NewDeck().Shuffled(rng);
        for (var i = 0; i < Seats; i++)
        {
            state.Players[i].Hand = SpadesCards.SortHand(deck.GetRange(i * HandSize, HandSize));
            state.Players[i].Bid = null;
            state.Players[i].Tricks = 0;
        }

        state.DealNumber += 1;
        // The very first dealer is drawn at random, then the deal rotates. The dealer bids last,
        // which is the only positional advantage in the game. Drawn from the same generator as
        // the shuffle, after it, so the hands a given seed produces do not move.
        state.Dealer = state.Dealer is int d ? (d + 1) % Seats : rng.NextInt(Seats);
        var dealer = state.Dealer.Value;
        state.Trick = [];
        state.TricksPlayed = 0;
        state.SpadesBroken = false;
        state.LastTrick = null;
        state.PlayedThisHand = [];

        // Bidding starts to the dealer's left and goes once around.
        state.Phase = SpadesPhase.Bidding;
        state.Leader = (dealer + 1) % Seats;
        state.Turn = state.Leader;

        state.Log.Add(EventKind.Deal,
            Invariant($"Hand {state.DealNumber} dealt. {Vb(state, dealer)} dealt; {Vb(state, state.Turn)} bids first."),
            seat: dealer);
    }

    /// <summary>
    /// EXACTLY the phases <see cref="ApplyAction"/> accepts a next-hand in. Idle belongs to the
    /// start action.
    /// </summary>
    public static bool CanDeal(SpadesState state) => state.Phase == SpadesPhase.HandOver;

    // MARK: - Bidding

    /// <summary>Every bid this seat may make: zero through thirteen, always.</summary>
    public static List<int> LegalBids(SpadesState state, int seat)
    {
        if (state.Phase != SpadesPhase.Bidding || state.Turn != seat) return [];
        return Enumerable.Range(0, HandSize + 1).ToList();
    }

    private static ActionResult DoBid(SpadesState state, int seat, int n)
    {
        if (state.Phase != SpadesPhase.Bidding) return ActionResult.Refused("nobody is bidding");
        if (state.Turn != seat) return ActionResult.Refused("not your turn to bid");
        if (n < 0 || n > HandSize) return ActionResult.Refused(Invariant($"a bid is a whole number from zero to {HandSize}"));
        if (state.Players[seat].Bid is not null) return ActionResult.Refused("you have already bid");

        state.Players[seat].Bid = n;
        state.Log.Add(EventKind.Bid, Invariant($"{Vb(state, seat)} bid {(n == 0 ? "nil" : n.ToString(System.Globalization.CultureInfo.InvariantCulture))}."), seat: seat);

        // Round the table once, then play. Counting the bids in rather than counting seats round
        // means an engine that somehow bid twice for one seat stops here instead of dealing a
        // hand nobody bid on.
        if (state.Players.All(p => p.Bid is not null))
        {
            BeginPlay(state);
            return ActionResult.Ok;
        }
        state.Turn = (state.Turn + 1) % Seats;
        return ActionResult.Ok;
    }

    private static void BeginPlay(SpadesState state)
    {
        state.Phase = SpadesPhase.Play;
        state.Leader = ((state.Dealer ?? -1) + 1) % Seats;
        state.Turn = state.Leader;
        state.Trick = [];

        int t0 = ContractOf(state, 0), t1 = ContractOf(state, 1);
        state.Log.Add(EventKind.Info,
            Invariant($"Bidding is done. {TeamName(state, 0)} for {t0}, {TeamName(state, 1)} for {t1}. ")
            + TableShape(t0 + t1) + $" {Vb(state, state.Leader)} leads.");
    }

    /// <summary>
    /// Said out loud because it is the single most useful fact about the hand about to be played:
    /// over is a scramble, under means tricks are going begging.
    /// </summary>
    public static string TableShape(int total)
    {
        if (total > HandSize) return Invariant($"That is {total - HandSize} over the {HandSize} available.");
        if (total < HandSize) return Invariant($"That leaves {HandSize - total} spare.");
        return Invariant($"Exactly {HandSize} bid.");
    }

    // MARK: - Play

    /// <summary>
    /// Which cards this seat may legally play right now. Two rules, and both yield rather than
    /// deadlock: follow the suit led if you hold it, and do not LEAD a spade until spades are
    /// broken unless spades are all you have.
    /// </summary>
    public static List<Card> LegalPlays(SpadesState state, int seat)
    {
        if (state.Phase != SpadesPhase.Play || state.Turn != seat || seat < 0 || seat >= state.Players.Count) return [];
        var hand = state.Players[seat].Hand;
        if (hand.Count == 0) return [];

        if (state.Trick.Count == 0)
        {
            if (state.SpadesBroken) return [.. hand];
            var notTrump = hand.Where(c => !SpadesCards.IsTrump(c)).ToList();
            return notTrump.Count == 0 ? [.. hand] : notTrump;
        }

        var led = state.Trick[0].Card.Suit;
        var follow = hand.Where(c => c.Suit == led).ToList();
        // Void in the led suit: anything, and that explicitly includes trumping in.
        return follow.Count == 0 ? [.. hand] : follow;
    }

    /// <summary>
    /// Why a card cannot be played now, in the words of the rule, or null if it can. A card the
    /// seat does not hold gets one fixed answer whatever anybody else holds, so the refusals are
    /// not an oracle for the other three hands.
    /// </summary>
    public static string? WhyNot(SpadesState state, int seat, Card card)
    {
        if (seat < 0 || seat >= state.Players.Count) return "not a seat at this table";
        if (state.Phase != SpadesPhase.Play) return "no trick is in progress";
        if (state.Turn != seat) return "not your turn";
        if (LegalPlays(state, seat).Contains(card)) return null;
        if (!state.Players[seat].Hand.Contains(card)) return "you do not hold that card";
        return RuleStopping(state, seat, card);
    }

    /// <summary>The <c>whyNot</c> of <c>game.js</c>: only ever asked about a card the seat holds.</summary>
    private static string RuleStopping(SpadesState state, int seat, Card card)
    {
        if (state.Trick.Count == 0)
        {
            if (SpadesCards.IsTrump(card) && !state.SpadesBroken) return "spades have not been broken";
            return "not a legal card here";
        }
        var led = state.Trick[0].Card.Suit;
        var canFollow = state.Players[seat].Hand.Any(c => c.Suit == led);
        if (canFollow && card.Suit != led) return $"you must follow {led.LowerName()}";
        return "not a legal card here";
    }

    private static ActionResult DoPlay(SpadesState state, int seat, Card card)
    {
        if (state.Phase != SpadesPhase.Play) return ActionResult.Refused("no trick is in progress");
        if (state.Turn != seat) return ActionResult.Refused("not your turn");

        var legal = LegalPlays(state, seat);
        if (!legal.Contains(card))
        {
            var held = state.Players[seat].Hand.Contains(card);
            return ActionResult.Refused(held ? RuleStopping(state, seat, card) : "you do not hold that card");
        }

        state.Players[seat].Hand = state.Players[seat].Hand.Removing(card);

        // ANY spade reaching the table breaks them, including one LED by a player holding nothing
        // else. Checked before the card joins the trick.
        if (SpadesCards.IsTrump(card) && !state.SpadesBroken)
        {
            state.SpadesBroken = true;
            state.Log.Add(EventKind.Info, "Spades are broken.");
        }

        state.Trick.Add(new SpadesPlay(seat, card));
        state.PlayedThisHand.Add(card);

        state.Log.Add(EventKind.Play, $"{Vb(state, seat)} played the {card.Name}.", seat: seat, cards: [card]);

        if (state.Trick.Count == Seats) return FinishTrick(state);

        state.Turn = (state.Turn + 1) % Seats;
        return ActionResult.Ok;
    }

    /// <summary>Who takes these cards: the first card, unless something later beats it.</summary>
    public static SpadesPlay? TrickWinner(IReadOnlyList<SpadesPlay> plays)
    {
        if (plays.Count == 0) return null;
        var best = plays[0];
        for (var i = 1; i < plays.Count; i++)
            if (SpadesCards.Beats(plays[i].Card, best.Card)) best = plays[i];
        return best;
    }

    private static ActionResult FinishTrick(SpadesState state)
    {
        var best = TrickWinner(state.Trick);
        if (best is null || state.Trick.Count != Seats)
            return ActionResult.Faulted("the game hit a fault and cannot continue");

        state.Players[best.Seat].Tricks += 1;
        state.LastTrick = new SpadesCompletedTrick { Cards = [.. state.Trick], Winner = best.Seat };
        state.TricksPlayed += 1;

        state.Log.Add(EventKind.Trick, $"{Vb(state, best.Seat)} took the trick with the {best.Card.Name}.",
            seat: best.Seat, cards: [best.Card]);

        // A nil going down is the loudest thing that happens in this game and it happens silently
        // otherwise. Said the moment it is certain.
        if (state.Players[best.Seat].Bid == 0 && state.Players[best.Seat].Tricks == 1)
            state.Log.Add(EventKind.Bid, $"{Vb(state, best.Seat)} bid nil and has taken a trick.", seat: best.Seat);

        state.Trick = [];
        state.Leader = best.Seat;
        state.Turn = best.Seat;

        if (state.TricksPlayed == HandSize) return FinishHand(state);
        return ActionResult.Ok;
    }

    // MARK: - Scoring

    /// <summary>
    /// A pure function of a hand's bids and tricks, so the oracle can check it against worked
    /// examples without playing a hand to get there.
    /// </summary>
    /// <param name="bids">Four bids, by seat (zero is nil).</param>
    /// <param name="tricks">Four trick counts, by seat.</param>
    /// <param name="bagsIn">The two bag counts BEFORE this hand. Not changed.</param>
    public static SpadesHandScore ScoreHand(IReadOnlyList<int> bids, IReadOnlyList<int> tricks, IReadOnlyList<int> bagsIn,
                                            int bagLimit, int bagPenalty, int nilValue)
    {
        var delta = new List<int> { 0, 0 };
        var bags = new List<int> { bagsIn.Count > 0 ? bagsIn[0] : 0, bagsIn.Count > 1 ? bagsIn[1] : 0 };
        var detail = new List<SpadesTeamScore>();

        for (var team = 0; team < Teams; team++)
        {
            int contract = 0, took = 0;
            var nils = new List<SpadesNilResult>();
            for (var i = 0; i < Seats; i++)
            {
                if (TeamOf(i) != team) continue;
                var b = i < bids.Count ? bids[i] : 0;
                var t = i < tricks.Count ? tricks[i] : 0;
                contract += b;
                took += t;
                if (b == 0) nils.Add(new SpadesNilResult(i, t == 0, t));
            }

            // Rule 2: the whole contract, not the difference. A contract of zero (both partners
            // nil) is made by definition.
            var made = took >= contract;
            var @base = made ? 10 * contract : -10 * contract;
            var overtricks = made ? took - contract : 0;

            // Rule 4: nil is its own bet, settled separately.
            var nilPoints = 0;
            foreach (var n in nils) nilPoints += n.Made ? nilValue : -nilValue;

            // Rule 3. Bags are only earned on a made contract. The penalty can fire more than once
            // in a hand: eight bags plus five overtricks is one bin emptied and three left over.
            var penalty = 0;
            bags[team] += overtricks;
            while (bagLimit > 0 && bags[team] >= bagLimit)
            {
                bags[team] -= bagLimit;
                penalty += bagPenalty;
            }

            delta[team] = @base + overtricks + nilPoints - penalty;
            detail.Add(new SpadesTeamScore
            {
                Team = team, Contract = contract, Took = took, Nils = nils,
                Made = made, Base = @base, Overtricks = overtricks,
                NilPoints = nilPoints, BagPenalty = penalty,
            });
        }
        return new SpadesHandScore { Delta = delta, Bags = bags, Detail = detail };
    }

    private static ActionResult FinishHand(SpadesState state)
    {
        var bids = state.Players.Select(p => p.Bid ?? 0).ToList();
        var tricks = state.Players.Select(p => p.Tricks).ToList();
        if (!state.Players.All(p => p.Bid is not null) || tricks.Sum() != HandSize)
            return ActionResult.Faulted("the game hit a fault and cannot continue");

        var r = ScoreHand(bids, tricks, state.Bags, BagLimit(state), BagPenalty(state), NilValue(state));

        state.Scores[0] += r.Delta[0];
        state.Scores[1] += r.Delta[1];
        state.Bags = [.. r.Bags];

        foreach (var d in r.Detail)
        {
            var who = TeamName(state, d.Team);
            state.Log.Add(EventKind.Score,
                Invariant($"{who} bid {d.Contract}, took {d.Took} — {(d.Made ? "made it" : "set")}, {Signed(r.Delta[d.Team])}."));
            foreach (var n in d.Nils)
            {
                state.Log.Add(EventKind.Bid,
                    $"{Vb(state, n.Seat)}’s nil " + (n.Made ? "came in." : $"went down on {Prose.Count(n.Tricks, "trick")}."),
                    seat: n.Seat);
            }
            if (d.BagPenalty != 0)
                state.Log.Add(EventKind.Score, Invariant($"{who} filled the bag bin — {d.BagPenalty} off."));
        }

        state.History.Add(new SpadesHandRecord
        {
            Deal = state.DealNumber, Dealer = state.Dealer ?? 0,
            Bids = bids, Tricks = tricks, Delta = [.. r.Delta],
            Scores = [.. state.Scores], Bags = [.. state.Bags],
        });

        state.Log.Add(EventKind.Hand,
            Invariant($"Hand {state.DealNumber} over. {TeamName(state, 0)} {state.Scores[0]}, {TeamName(state, 1)} {state.Scores[1]}."));

        var target = Target(state);
        bool over0 = state.Scores[0] >= target, over1 = state.Scores[1] >= target;
        if (over0 || over1)
        {
            // Both across the line in the same hand is possible; the higher score wins. Equal and
            // both over is a genuine tie and plays another hand rather than inventing a winner.
            if (over0 && over1 && state.Scores[0] == state.Scores[1])
            {
                state.Log.Add(EventKind.Info, Invariant($"Both partnerships passed {target} and are level. Another hand decides it."));
                state.Phase = SpadesPhase.HandOver;
                return ActionResult.Ok;
            }
            var w = state.Scores[0] > state.Scores[1] ? 0 : 1;
            state.Winner = w;
            state.Phase = SpadesPhase.GameOver;
            state.Log.Add(EventKind.Game, Invariant($"{TeamName(state, w)} win, {state.Scores[w]} to {state.Scores[1 - w]}."));
        }
        else
        {
            state.Phase = SpadesPhase.HandOver;
        }
        return ActionResult.Ok;
    }

    /// <summary>"+50", "+0", "-70": a hand's score change as the log says it.</summary>
    public static string Signed(int n) => n >= 0 ? Invariant($"+{n}") : Invariant($"{n}");

    // MARK: - Who is to move

    /// <summary>
    /// Null for nobody, and the dead phases matter: a room that asks a bot to move during
    /// hand-over gets a seat number still valid from the last trick.
    /// </summary>
    public static int? SeatToAct(SpadesState state) => state.Phase switch
    {
        SpadesPhase.Bidding or SpadesPhase.Play => state.Turn,
        _ => null,
    };

    // MARK: - The only way in

    /// <summary>
    /// THE ONLY WAY IN. Validates the seat, the phase and the action, and refuses with a reason in
    /// the words of the rule. Never throws on bad input, and a refusal leaves the state exactly
    /// as it was: every check happens before anything is changed.
    /// </summary>
    public static ActionResult ApplyAction(SpadesState state, int seat, SpadesAction action, RandomSource rng)
    {
        if (state.Players is not { Count: Seats } || state.Scores is not { Count: Teams } || state.Bags is not { Count: Teams })
            return ActionResult.Faulted("the table is not four seats in two partnerships");
        if (seat < 0 || seat >= Seats) return ActionResult.Refused("not a seat at this table");

        switch (action)
        {
            case SpadesAction.Start:
                // Deal the FIRST hand, when the people at the table say they are ready.
                if (state.Phase != SpadesPhase.Idle) return ActionResult.Refused("the game has already started");
                NewHand(state, rng);
                return ActionResult.Ok;

            case SpadesAction.Bid bid:
                return DoBid(state, seat, bid.Tricks);

            case SpadesAction.Play play:
                return DoPlay(state, seat, play.Card);

            case SpadesAction.NextHand:
                if (!CanDeal(state)) return ActionResult.Refused("the hand is not over");
                NewHand(state, rng);
                return ActionResult.Ok;

            case SpadesAction.NewGame:
                if (state.Phase == SpadesPhase.Idle) return ActionResult.Refused("the game has not started");
                Reset(state, state.Config.Copy());
                state.Log.Add(EventKind.Info, "A new game is ready.");
                return ActionResult.Ok;

            default:
                // Not in Swift, whose enum is closed: a null, or a case from somewhere else.
                return ActionResult.Refused("that is not a move in spades");
        }
    }
}
