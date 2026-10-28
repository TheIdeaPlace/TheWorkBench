namespace TheCardPlace.Cribbage.Tests;

/// <summary>
/// Whole games played by the computer on both seats, with seeded randomness, shared by the test
/// classes so the simulation runs once.
/// </summary>
internal static class Sim
{
    internal sealed record Played(ulong Seed, Difficulty Difficulty, int Target, CribbageState State, List<string> Refusals, int Steps);

    public static CribbageConfig Config(Difficulty difficulty, int target) =>
        new() { Names = ["You", "Ruth"], Difficulty = difficulty, TargetScore = target };

    /// <summary>
    /// Drive one game to the end. <paramref name="observe"/> sees the state before every computer
    /// decision, with the seat about to act. The state is live: clone it to keep it.
    /// </summary>
    public static Played Play(ulong seed, Difficulty difficulty, int target, int maxSteps = 8000,
                              Action<CribbageState, int>? observe = null)
    {
        var rng = new RandomSource(seed);
        var state = CribbageGame.CreateGame(Config(difficulty, target));
        var refusals = new List<string>();
        var steps = 0;
        var r0 = CribbageGame.ApplyAction(state, 0, new CribbageAction.Start(), rng);
        if (!r0.IsOk) refusals.Add($"start: {r0.Reason}");
        while (!state.IsGameOver && steps < maxSteps)
        {
            steps++;
            if (state.Phase == CribbagePhase.RoundOver)
            {
                var r = CribbageGame.ApplyAction(state, 0, new CribbageAction.NextHand(), rng);
                if (!r.IsOk) refusals.Add($"nextHand: {r.Reason}");
                continue;
            }
            if (CribbageGame.SeatToAct(state) is not { } seat)
            {
                refusals.Add($"nobody to act at {state.Phase}");
                break;
            }
            observe?.Invoke(state, seat);
            if (CribbageAI.Act(state, rng) is not { } acted)
            {
                refusals.Add($"the computer had nobody to act for at {state.Phase}");
                break;
            }
            if (!acted.Result.IsOk)
            {
                refusals.Add($"seat {acted.Seat} at {state.Phase}: {acted.Result.Reason ?? "no reason"}");
                break;
            }
        }
        return new Played(seed, difficulty, target, state, refusals, steps);
    }

    public const int PerCombination = 20;

    private static readonly Lazy<List<Played>> LazyGames = new(() =>
    {
        var output = new List<Played>();
        ulong seed = 1000;
        foreach (var target in CribbageConfig.Targets)
        {
            foreach (var difficulty in DifficultyExtensions.All)
            {
                for (var i = 0; i < PerCombination; i++)
                {
                    output.Add(Play(seed, difficulty, target));
                    seed++;
                }
            }
        }
        return output;
    });

    /// <summary>Every difficulty and both targets.</summary>
    public static List<Played> Games => LazyGames.Value;

    /// <summary>Every finished hand from every game, in order.</summary>
    public static IEnumerable<CribbageHandRecord> Hands => Games.SelectMany(g => g.State.History);
}

internal static class T
{
    public static Card C(string id) => Card.Parse(id);

    public static List<Card> Cs(params string[] ids) => ids.Select(Card.Parse).ToList();

    public static string Json<TValue>(TValue value) => Snapshot.Json(value);

    /// <summary>
    /// Drive with the computer on both seats until the predicate holds or the game ends. Returns
    /// false if it never held.
    /// </summary>
    public static bool Advance(CribbageState state, RandomSource rng, Func<CribbageState, bool> stop, int limit = 4000)
    {
        var steps = 0;
        while (!stop(state) && steps < limit)
        {
            steps++;
            if (state.IsGameOver) return false;
            if (state.Phase == CribbagePhase.Idle)
            {
                CribbageGame.ApplyAction(state, 0, new CribbageAction.Start(), rng);
                continue;
            }
            if (state.Phase == CribbagePhase.RoundOver)
            {
                CribbageGame.ApplyAction(state, 0, new CribbageAction.NextHand(), rng);
                continue;
            }
            if (CribbageAI.Act(state, rng) is not { } acted || !acted.Result.IsOk) return false;
        }
        return stop(state);
    }
}
