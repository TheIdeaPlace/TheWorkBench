namespace TheCardPlace.Hearts.Tests;

/// Shared scaffolding for the hearts tests: a seeded game driver and a way to
/// build a position by hand.
internal static class Support
{
    public static readonly string[] Names = ["North", "East", "South", "West"];

    public static HeartsConfig Config(Difficulty difficulty = Difficulty.Hard, int pointsToWin = 100) =>
        new() { Names = [.. Names], Difficulty = difficulty, PointsToWin = pointsToWin };

    public static Card C(string id) => Card.Parse(id);

    public static List<Card> Cards(params string[] ids) => ids.Select(C).ToList();

    /// Sorted ids, for comparing sets of cards.
    public static List<string> IdSet(IEnumerable<Card> cards) => cards.Select(c => c.Id).Order(StringComparer.Ordinal).ToList();

    public static List<string> IdSet(params string[] ids) => ids.Order(StringComparer.Ordinal).ToList();

    /// Plays one whole game with the AI in every seat. `inspect` sees the
    /// state before every action; `afterMove` sees it after each accepted one.
    public static HeartsState PlayGame(ulong seed, HeartsConfig config, int maxSteps = 8000,
                                       Action<HeartsState>? inspect = null,
                                       Action<HeartsState, int, HeartsAction>? afterMove = null)
    {
        var rng = new RandomSource(seed);
        var state = HeartsGame.CreateGame(config);
        var started = HeartsGame.ApplyAction(state, 0, new HeartsAction.Start(), rng);
        Assert.True(started.IsOk, $"start refused: {started.Reason}");
        var steps = 0;
        while (state.Phase != HeartsPhase.GameOver && steps < maxSteps)
        {
            steps++;
            inspect?.Invoke(state);
            if (state.Phase == HeartsPhase.HandOver)
            {
                var r = HeartsGame.ApplyAction(state, 0, new HeartsAction.NextHand(), rng);
                Assert.True(r.IsOk, $"nextHand refused: {r.Reason}");
                continue;
            }
            var seat = HeartsGame.SeatToAct(state);
            Assert.True(seat is not null, $"nobody to act in {state.Phase}");
            var action = HeartsAI.Decide(state, seat.Value, rng);
            Assert.True(action is not null, $"the computer had no move at phase {state.Phase} for seat {seat}");
            var result = HeartsGame.ApplyAction(state, seat.Value, action, rng);
            Assert.True(result.IsOk, $"the computer's move was refused: {result.Reason} ({action} at {state.Phase}, seat {seat})");
            afterMove?.Invoke(state, seat.Value, action);
        }
        Assert.True(state.Phase == HeartsPhase.GameOver, $"the game did not finish within {maxSteps} steps");
        return state;
    }

    /// A play-phase position built by hand. Hands are given as card ids per
    /// seat; the trick as (seat, id) pairs already on the table.
    public static HeartsState PlayState(string[][] hands, int turn, int? leader = null,
                                        (int Seat, string Id)[]? trick = null, int tricksPlayed = 0,
                                        bool heartsBroken = false, string[][]? taken = null,
                                        HeartsConfig? config = null)
    {
        trick ??= [];
        taken ??= [[], [], [], []];
        var s = HeartsGame.CreateGame(config ?? Config());
        s.Phase = HeartsPhase.Play;
        s.DealNumber = 1;
        for (var i = 0; i < 4; i++)
        {
            s.Players[i].Hand = HeartsCards.SortHand(Cards(hands[i]));
            s.Players[i].Taken = Cards(taken[i]);
        }
        s.Turn = turn;
        s.Leader = leader ?? (trick.Length > 0 ? trick[0].Seat : turn);
        s.Trick = trick.Select(t => new HeartsTrickPlay(t.Seat, C(t.Id))).ToList();
        s.TricksPlayed = tricksPlayed;
        s.HeartsBroken = heartsBroken;
        return s;
    }

    /// Every card at the table: hands, taken piles and the trick.
    public static List<Card> AllCards(HeartsState s)
    {
        var all = new List<Card>();
        foreach (var p in s.Players) { all.AddRange(p.Hand); all.AddRange(p.Taken); }
        all.AddRange(s.Trick.Select(t => t.Card));
        return all;
    }

    /// Has any heart hit the table this hand, worked out from the cards rather
    /// than from the engine's flag?
    public static bool HeartEverPlayed(HeartsState s) =>
        s.Trick.Any(t => t.Card.Suit == Suit.Hearts) || s.Players.Any(p => p.Taken.Any(c => c.Suit == Suit.Hearts));

    /// The seat of the first unpassed row, as Swift's firstIndex.
    public static int? FirstUnpassed(HeartsState s)
    {
        var i = s.Passing.FindIndex(p => p is null);
        return i < 0 ? null : i;
    }
}
