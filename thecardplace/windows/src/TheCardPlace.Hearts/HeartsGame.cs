using TheCardPlace.Core;

namespace TheCardPlace.Hearts;

// Hearts - the rules, and the only door into them. A port of `hearts/js/game.js`
// by way of HeartsGame.swift.
//
// Four players, thirteen tricks, no trump. Every heart is worth a point and the
// queen of spades is worth thirteen, and the object is to take none of them. The
// game ends when somebody reaches the target; the LOWEST score wins.
//
// The four rules that are actually the game:
//
// 1. The two of clubs leads the first trick. Whoever holds it plays it.
// 2. Nothing scoring on the first trick, unless a player has nothing else.
// 3. Hearts must be broken before they can be led — unless a player holds
//    nothing but hearts. The rule yields rather than deadlocking.
// 4. Shooting the moon: take all twenty-six and everybody else takes
//    twenty-six instead.
//
// Passing is simultaneous: the engine holds four sets of three and swaps them
// only when all four are in.
public static class HeartsGame
{
    public const int Seats = 4;
    public const int HandSize = 13;
    public const int PassCount = 3;
    public const int DefaultTarget = 100;
    public const int Moon = 26;

    /// left, right, across, hold — and the hold hand is why this is length four.
    public static readonly IReadOnlyList<HeartsPassDirection> PassOrder =
        [HeartsPassDirection.Left, HeartsPassDirection.Right, HeartsPassDirection.Across, HeartsPassDirection.Hold];

    // MARK: - Creation

    public static HeartsState CreateGame(HeartsConfig config)
    {
        // Swift's config is a value; this copies it so a caller changing its own object later
        // cannot change a table already made.
        var cfg = new HeartsConfig
        {
            Names = [.. config.Names ?? []],
            Difficulty = config.Difficulty,
            PointsToWin = config.PointsToWin,
            Occupants = config.Occupants is null ? null : [.. config.Occupants],
        };
        var players = new List<HeartsPlayer>();
        for (var i = 0; i < Seats; i++)
        {
            var name = i < cfg.Names.Count ? cfg.Names[i] : $"Seat {i + 1}";
            var occupant = cfg.Occupants is { } o && i < o.Count ? o[i] : (i == 0 ? Occupant.Human : Occupant.Bot);
            players.Add(new HeartsPlayer { Index = i, Name = name, Occupant = occupant });
        }
        return new HeartsState
        {
            Phase = HeartsPhase.Idle,
            Players = players,
            Config = cfg,
            Log = new EventLog(),
            DealNumber = 0,
            PassDirection = PassOrder[0],
            Passing = NoRows(),
            Received = NoRows(),
            Turn = 0,
            Leader = 0,
            Trick = [],
            TricksPlayed = 0,
            HeartsBroken = false,
            LastTrick = null,
            History = [],
            Winner = null,
        };
    }

    private static List<List<Card>?> NoRows() => Enumerable.Repeat<List<Card>?>(null, Seats).ToList();

    /// The target this table agreed on, not the one this file happens to prefer.
    public static int Target(HeartsState state) =>
        state.Config.PointsToWin > 0 ? state.Config.PointsToWin : DefaultTarget;

    internal static string Name(HeartsState state, int seat) =>
        seat >= 0 && seat < state.Players.Count ? state.Players[seat].Name : $"seat {seat}";

    // MARK: - Scoring facts

    public static bool IsScoring(Card c) => c.Suit == Suit.Hearts || c == HeartsCards.QueenOfSpades;

    public static int PointsOf(IEnumerable<Card> cards)
    {
        var n = 0;
        foreach (var c in cards)
        {
            if (c.Suit == Suit.Hearts) n += 1;
            else if (c == HeartsCards.QueenOfSpades) n += 13;
        }
        return n;
    }

    // MARK: - The only way in

    /// <summary>
    /// Validates the seat, the phase and the action, and refuses with a reason
    /// in the words of the rule. Never throws on bad input. Everything is
    /// validated before anything is written, so a refusal genuinely means
    /// nothing changed.
    /// </summary>
    public static ActionResult ApplyAction(HeartsState state, int seat, HeartsAction action, RandomSource rng)
    {
        if (state.Players is null || state.Players.Count != Seats
            || state.Passing is null || state.Passing.Count != Seats
            || state.Received is null || state.Received.Count != Seats)
            return ActionResult.Faulted("the table does not have four seats");
        if (seat < 0 || seat >= Seats) return ActionResult.Refused("not a seat at this table");

        switch (action)
        {
            case HeartsAction.Start:
                // Deal the FIRST hand, when the people at the table say they are ready.
                if (state.Phase != HeartsPhase.Idle) return ActionResult.Refused("the game has already started");
                state.Phase = HeartsPhase.HandOver;   // so NewHand's own gate is the only one
                return NewHand(state, rng);

            case HeartsAction.Pass pass:
                return DoPass(state, seat, pass.Cards);

            case HeartsAction.Play play:
                return DoPlay(state, seat, play.Card);

            case HeartsAction.NextHand:
                if (!CanDeal(state)) return ActionResult.Refused("the hand is not over");
                return NewHand(state, rng);

            case HeartsAction.NewGame:
                if (state.Phase == HeartsPhase.Idle) return ActionResult.Refused("the game has not started");
                Replace(state, CreateGame(state.Config));
                state.Log.Add(EventKind.Game, "New game. Every score is back to nothing.");
                return ActionResult.Ok;

            default:
                // Not in Swift, whose enum is closed. A null, or an action from a newer version
                // arriving over the wire.
                return ActionResult.Refused("that is not something you can do in hearts");
        }
    }

    /// Swift assigns a fresh value to the inout state; C# changes the caller's object in place.
    private static void Replace(HeartsState state, HeartsState fresh)
    {
        state.Phase = fresh.Phase;
        state.Players = fresh.Players;
        state.Config = fresh.Config;
        state.Log = fresh.Log;
        state.DealNumber = fresh.DealNumber;
        state.PassDirection = fresh.PassDirection;
        state.Passing = fresh.Passing;
        state.Received = fresh.Received;
        state.Turn = fresh.Turn;
        state.Leader = fresh.Leader;
        state.Trick = fresh.Trick;
        state.TricksPlayed = fresh.TricksPlayed;
        state.HeartsBroken = fresh.HeartsBroken;
        state.LastTrick = fresh.LastTrick;
        state.History = fresh.History;
        state.Winner = fresh.Winner;
    }

    /// EXACTLY the phases ApplyAction accepts a NextHand in. Idle belongs to
    /// the Start action.
    public static bool CanDeal(HeartsState state) => state.Phase == HeartsPhase.HandOver;

    /// Passing has no single seat to act — all four choose at once — so this
    /// answers with the first seat that has not passed.
    public static int? SeatToAct(HeartsState state)
    {
        switch (state.Phase)
        {
            case HeartsPhase.Passing:
                var i = state.Passing.FindIndex(p => p is null);
                return i < 0 ? null : i;
            case HeartsPhase.Play:
                return state.Turn;
            default:
                return null;
        }
    }

    // MARK: - Dealing

    private static ActionResult NewHand(HeartsState state, RandomSource rng)
    {
        if (!CanDeal(state)) return ActionResult.Refused("the hand is not over");

        var deck = Card.FullDeck.Shuffled(rng);
        if (deck.Count != Seats * HandSize) return ActionResult.Faulted("the pack is not fifty-two cards");
        for (var i = 0; i < Seats; i++)
        {
            state.Players[i].Hand = HeartsCards.SortHand(deck.GetRange(i * HandSize, HandSize));
            state.Players[i].Taken = [];
            state.Players[i].HandPoints = 0;
        }

        state.DealNumber += 1;
        state.PassDirection = PassOrder[(state.DealNumber - 1) % PassOrder.Count];
        state.Passing = NoRows();
        state.Received = NoRows();
        state.Trick = [];
        state.TricksPlayed = 0;
        state.HeartsBroken = false;
        state.LastTrick = null;

        state.Log.Add(EventKind.Deal, $"Hand {state.DealNumber} dealt.");

        if (state.PassDirection == HeartsPassDirection.Hold)
        {
            state.Log.Add(EventKind.Info, "Nobody passes this hand.");
            BeginPlay(state);
        }
        else
        {
            state.Phase = HeartsPhase.Passing;
            state.Log.Add(EventKind.Info, $"Pass three cards to {state.PassDirection.RecipientPhrase()}.");
        }
        return ActionResult.Ok;
    }

    // MARK: - Passing

    private static ActionResult DoPass(HeartsState state, int seat, List<Card>? cards)
    {
        if (state.Phase != HeartsPhase.Passing) return ActionResult.Refused("nothing is being passed");
        if (state.Passing[seat] is not null) return ActionResult.Refused("you have already passed");
        if (cards is null || cards.Count != PassCount) return ActionResult.Refused($"pass exactly {PassCount} cards");
        if (cards.Distinct().Count() != cards.Count) return ActionResult.Refused("the same card twice");
        var hand = state.Players[seat].Hand;
        if (!cards.All(hand.Contains)) return ActionResult.Refused("you do not hold that card");

        // Kept in the order they sit in the hand, so the sentence reads the way
        // the hand does.
        var chosen = hand.Where(cards.Contains).ToList();
        state.Passing[seat] = chosen;

        state.Log.Add(EventKind.You, $"You passed {chosen.SpokenList()}.", seat: seat, cards: chosen, audience: seat);

        // Everyone is told THAT a seat has passed, and nothing about what.
        state.Log.Add(EventKind.Info, $"{Name(state, seat)} has passed.", seat: seat);

        if (state.Passing.All(p => p is not null)) SwapPasses(state);
        return ActionResult.Ok;
    }

    private sealed record Moving(int From, int To, List<Card> Cards);

    private static void SwapPasses(HeartsState state)
    {
        var offset = state.PassDirection.Offset();
        var moving = Enumerable.Range(0, Seats)
            .Select(from => new Moving(from, (from + offset) % Seats, state.Passing[from] ?? []))
            .ToList();
        // Removed from every hand BEFORE anything is added to any hand. With
        // `across`, seats 0 and 2 hand each other three cards, and a sequential
        // loop would give seat 0 its own cards back.
        foreach (var m in moving)
            state.Players[m.From].Hand.RemoveAll(m.Cards.Contains);
        foreach (var m in moving)
        {
            state.Players[m.To].Hand = HeartsCards.SortHand(state.Players[m.To].Hand.Concat(m.Cards));
            state.Received[m.To] = [.. m.Cards];
            state.Log.Add(EventKind.You, $"{Name(state, m.From)} passed you {m.Cards.SpokenList()}.",
                          seat: m.From, cards: m.Cards, audience: m.To);
        }
        state.Passing = NoRows();
        BeginPlay(state);
    }

    // MARK: - Play

    private static void BeginPlay(HeartsState state)
    {
        state.Phase = HeartsPhase.Play;
        state.Leader = HolderOfTwoOfClubs(state);
        state.Turn = state.Leader;
        state.Trick = [];
        state.Log.Add(EventKind.Info, $"{Name(state, state.Leader)} has the two of clubs and leads.", seat: state.Leader);
    }

    public static int HolderOfTwoOfClubs(HeartsState state)
    {
        var i = state.Players.FindIndex(p => p.Hand.Contains(HeartsCards.TwoOfClubs));
        return i < 0 ? 0 : i;
    }

    /// <summary>
    /// Which cards this seat may legally play right now. The three yielding
    /// rules all live here, and they all yield the same way: if applying the
    /// restriction would leave nothing, the restriction does not apply.
    /// </summary>
    public static List<Card> LegalPlays(HeartsState state, int seat)
    {
        if (state.Phase != HeartsPhase.Play || state.Turn != seat || seat < 0 || seat >= state.Players.Count) return [];
        var hand = state.Players[seat].Hand;
        if (hand.Count == 0) return [];

        var firstTrick = state.TricksPlayed == 0;
        var leading = state.Trick.Count == 0;

        if (leading)
        {
            if (firstTrick)
            {
                var two = hand.Where(c => c == HeartsCards.TwoOfClubs).ToList();
                if (two.Count > 0) return two;          // not a choice
            }
            if (!state.HeartsBroken)
            {
                var notHearts = hand.Where(c => c.Suit != Suit.Hearts).ToList();
                if (notHearts.Count > 0) return notHearts;
                return [.. hand];                       // nothing but hearts: the rule yields
            }
            return [.. hand];
        }

        var led = state.Trick[0].Card.Suit;
        var follow = hand.Where(c => c.Suit == led).ToList();
        if (follow.Count > 0) return follow;

        if (firstTrick)
        {
            var safe = hand.Where(c => !IsScoring(c)).ToList();
            if (safe.Count > 0) return safe;
            return [.. hand];                           // all points: the rule yields
        }
        return [.. hand];
    }

    /// Why a card in your hand cannot be played, said in the words of the rule.
    /// Null when it can.
    public static string? WhyNot(HeartsState state, int seat, Card card)
    {
        if (seat < 0 || seat >= state.Players.Count) return "not a seat at this table";
        if (state.Phase != HeartsPhase.Play) return "no trick is in progress";
        if (state.Turn != seat) return "not your turn";
        if (!state.Players[seat].Hand.Contains(card)) return "you do not hold that card";
        if (LegalPlays(state, seat).Contains(card)) return null;

        var leading = state.Trick.Count == 0;
        if (!leading)
        {
            var led = state.Trick[0].Card.Suit;
            var canFollow = state.Players[seat].Hand.Any(c => c.Suit == led);
            if (canFollow && card.Suit != led)
                return $"you must follow {led.LowerName()}";
            if (state.TricksPlayed == 0 && IsScoring(card))
                return "no points on the first trick";
            return "not a legal card here";
        }
        if (state.TricksPlayed == 0) return "the two of clubs must be led first";
        if (card.Suit == Suit.Hearts && !state.HeartsBroken) return "hearts have not been broken";
        return "not a legal card here";
    }

    private static ActionResult DoPlay(HeartsState state, int seat, Card card)
    {
        if (state.Phase != HeartsPhase.Play) return ActionResult.Refused("no trick is in progress");
        if (state.Turn != seat) return ActionResult.Refused("not your turn");

        var legal = LegalPlays(state, seat);
        if (!legal.Contains(card))
        {
            var held = state.Players[seat].Hand.Contains(card);
            return ActionResult.Refused(held ? (WhyNot(state, seat, card) ?? "not a legal card here")
                                             : "you do not hold that card");
        }

        state.Players[seat].Hand.RemoveAll(c => c == card);
        state.Trick.Add(new HeartsTrickPlay(seat, card));

        if (card.Suit == Suit.Hearts && !state.HeartsBroken)
        {
            state.HeartsBroken = true;
            state.Log.Add(EventKind.Info, "Hearts are broken.");
        }

        state.Log.Add(EventKind.Play, $"{Name(state, seat)} played the {HeartsCards.Name(card)}.",
                      seat: seat, cards: [card]);

        if (state.Trick.Count == Seats) return FinishTrick(state);

        state.Turn = (state.Turn + 1) % Seats;
        return ActionResult.Ok;
    }

    private static ActionResult FinishTrick(HeartsState state)
    {
        if (state.Trick.Count != Seats) return ActionResult.Faulted($"a trick finished with {state.Trick.Count} cards");
        var best = state.Trick[0];
        foreach (var t in state.Trick.Skip(1))
            if (HeartsCards.Beats(t.Card, best.Card)) best = t;
        var cards = state.Trick.Select(t => t.Card).ToList();
        var pts = PointsOf(cards);

        state.Players[best.Seat].Taken.AddRange(cards);
        state.LastTrick = new HeartsLastTrick([.. state.Trick], best.Seat, pts);
        state.TricksPlayed += 1;

        state.Log.Add(EventKind.Trick, $"{Name(state, best.Seat)} took the trick{PointsTail(pts)}.",
                      seat: best.Seat, cards: cards);

        state.Trick = [];
        state.Leader = best.Seat;
        state.Turn = best.Seat;

        if (state.TricksPlayed == HandSize) return FinishHand(state);
        return ActionResult.Ok;
    }

    /// " with 3 points" / " with 1 point" / ", no points".
    public static string PointsTail(int pts) => pts > 0 ? $" with {Prose.Count(pts, "point")}" : ", no points";

    private static ActionResult FinishHand(HeartsState state)
    {
        var raw = state.Players.Select(p => PointsOf(p.Taken)).ToList();
        var total = raw.Sum();
        if (total != Moon) return ActionResult.Faulted($"a finished hand is worth {total} points, not {Moon}");

        // The moon. Checked against the total rather than a count of hearts so
        // that a bug in PointsOf cannot make a false moon.
        int? shooter = null;
        if (total == Moon)
        {
            for (var i = 0; i < Seats; i++)
                if (raw[i] == Moon) shooter = i;
        }

        for (var i = 0; i < Seats; i++)
        {
            var pts = shooter is { } s ? (i == s ? 0 : Moon) : raw[i];
            state.Players[i].HandPoints = pts;
            state.Players[i].Score += pts;
        }

        if (shooter is { } sh)
            state.Log.Add(EventKind.Moon, $"{Name(state, sh)} shot the moon — everybody else takes {Moon}.", seat: sh);

        state.History.Add(new HeartsHandRecord
        {
            Deal = state.DealNumber,
            PassDirection = state.PassDirection,
            Points = state.Players.Select(p => p.HandPoints).ToList(),
            Shooter = shooter,
            Scores = state.Players.Select(p => p.Score).ToList(),
        });

        // The hand's points and then the running scores: without the scores a player can't tell
        // where anyone stands, or why the game has just ended, without asking.
        state.Log.Add(EventKind.Hand, $"Hand {state.DealNumber} over. This hand, "
            + Prose.List(state.Players.Select(p => $"{p.Name} {p.HandPoints}")) + ". Scores, "
            + Prose.List(state.Players.Select(p => $"{p.Name} {p.Score}")) + ".");

        var target = Target(state);
        if (state.Players.Any(p => p.Score >= target))
        {
            var low = state.Players.Count == 0 ? 0 : state.Players.Min(p => p.Score);
            var winners = state.Players.Where(p => p.Score == low).ToList();
            state.Winner = winners.Count == 1 ? winners[0].Index : null;
            state.Phase = HeartsPhase.GameOver;
            var over = state.Players.Where(p => p.Score >= target).ToList();
            state.Log.Add(EventKind.Game, $"{Prose.List(over.Select(p => p.Name))} reached {target}, so the game is over.");
            if (winners.Count == 1)
                state.Log.Add(EventKind.Game, $"{winners[0].Name} wins with {low}. Lowest score wins.", seat: winners[0].Index);
            else
                state.Log.Add(EventKind.Game, $"Tied on {low}: " + Prose.List(winners.Select(p => p.Name)) + ".");
        }
        else
        {
            state.Phase = HeartsPhase.HandOver;
        }
        return ActionResult.Ok;
    }
}
