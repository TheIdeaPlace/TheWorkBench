namespace TheCardPlace.Spades.Tests;

internal static class T
{
    /// <summary>"AS", "TD": a card from its id, for literal positions in tests.</summary>
    public static Card C(string id) => Card.Parse(id);

    public static List<Card> Cs(string ids) => Card.ParseList(ids);

    public static readonly List<string> TestNames = ["North", "East", "South", "West"];

    public static SpadesConfig Config(Difficulty difficulty = Difficulty.Normal, int pointsToWin = 500,
                                      int bagLimit = 10, int bagPenalty = 100, int nilValue = 100,
                                      List<string>? names = null) => new()
    {
        Names = [.. names ?? TestNames],
        Difficulty = difficulty,
        PointsToWin = pointsToWin,
        BagLimit = bagLimit,
        BagPenalty = bagPenalty,
        NilValue = nilValue,
    };

    /// <summary>
    /// A play-phase position built by hand: bids and trick counts per seat, the hands, who leads,
    /// and how many tricks have gone. Everything else takes the value a real game would have.
    /// </summary>
    public static SpadesState Position(int[] bids, int[] tricks, string[] hands, int leader,
                                       int tricksPlayed, bool spadesBroken = false,
                                       int[]? scores = null, int[]? bags = null, SpadesConfig? config = null)
    {
        var state = SpadesGame.CreateGame(config ?? new SpadesConfig());
        state.Phase = SpadesPhase.Play;
        state.Dealer = (leader + 3) % 4;
        state.DealNumber = 1;
        for (var i = 0; i < 4; i++)
        {
            state.Players[i].Bid = bids[i];
            state.Players[i].Tricks = tricks[i];
            state.Players[i].Hand = SpadesCards.SortHand(Cs(hands[i]));
        }
        state.Leader = leader;
        state.Turn = leader;
        state.TricksPlayed = tricksPlayed;
        state.SpadesBroken = spadesBroken;
        state.Scores = [.. scores ?? [0, 0]];
        state.Bags = [.. bags ?? [0, 0]];
        return state;
    }

    public static string Json(object? value) => Snapshot.Json(value);
}

/// <summary>Drives games with the computer players, seeded, so a failure can be replayed.</summary>
internal static class Drive
{
    public static (SpadesState State, RandomSource Rng) NewGame(SpadesConfig? config, ulong seed)
    {
        var rng = new RandomSource(seed);
        var state = SpadesGame.CreateGame(config ?? T.Config());
        var r = SpadesGame.ApplyAction(state, 0, new SpadesAction.Start(), rng);
        if (!r.IsOk) throw new InvalidOperationException($"start refused: {r.Reason}");
        return (state, rng);
    }

    public static (SpadesState State, RandomSource Rng) NewGame(ulong seed) => NewGame(null, seed);

    /// <summary>
    /// One step: the computer's move for the seat to act, or the next deal. Returns the result,
    /// or null when the game is over.
    /// </summary>
    public static ActionResult? Step(SpadesState state, RandomSource rng)
    {
        if (state.Phase == SpadesPhase.GameOver) return null;
        if (state.Phase == SpadesPhase.HandOver)
            return SpadesGame.ApplyAction(state, 0, new SpadesAction.NextHand(), rng);
        if (SpadesGame.SeatToAct(state) is not int seat) return null;
        var action = SpadesAI.Decide(state, seat, rng);
        if (action is null) return ActionResult.Faulted($"the computer had no move at {state.Phase}");
        return SpadesGame.ApplyAction(state, seat, action, rng);
    }

    /// <summary>Start a game and bid it through with the computer, leaving the play about to begin.</summary>
    public static (SpadesState State, RandomSource Rng) ToPlay(SpadesConfig? config, ulong seed)
    {
        var (state, rng) = NewGame(config, seed);
        while (state.Phase == SpadesPhase.Bidding) Step(state, rng);
        return (state, rng);
    }

    /// <summary>Play a whole game out, checking nothing. Returns the finished state.</summary>
    public static SpadesState PlayGame(SpadesConfig? config, ulong seed, int guardSteps = 40_000)
    {
        var (state, rng) = NewGame(config, seed);
        var guardCount = 0;
        while (state.Phase != SpadesPhase.GameOver && guardCount < guardSteps)
        {
            guardCount++;
            if (Step(state, rng) is not { } r) break;
            if (!r.IsOk) throw new InvalidOperationException($"the computer's move was refused: {r.Reason} at {state.Phase}");
        }
        return state;
    }
}
