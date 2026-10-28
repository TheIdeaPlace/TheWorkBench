using System.Reflection;

namespace TheCardPlace.Sheephead.Tests;

/// <summary>The Swift test target's Support.swift.</summary>
internal static class Support
{
    /// <summary>Swift's <c>card("QC")</c>.</summary>
    public static Card Cd(string id) => Card.Parse(id);

    public static List<Card> Cards(params string[] ids) => ids.Select(Card.Parse).ToList();

    public static string J<T>(T value) => Snapshot.Json(value);

    public static (SheepheadState State, RandomSource Rng) NewGame(
        int players, ulong seed, SheepheadAllPass allPass = SheepheadAllPass.Leaster,
        Difficulty difficulty = Difficulty.Hard, bool doublers = false)
    {
        var config = new SheepheadConfig
        {
            Players = players,
            Difficulty = difficulty,
            AllPass = allPass,
            BlackQueenDoubler = doublers,
            RedQueenDoubler = doublers,
            RedealDoubler = doublers,
        };
        var rng = new RandomSource(seed);
        var state = SheepheadGame.CreateGame(config);
        Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.Start(), rng));
        return (state, rng);
    }

    /// <summary>
    /// A copy of a seeded generator, so a test can replay a decision with the numbers the AI was
    /// handed. Swift copies its value-typed generator with <c>let r = rng</c>; the C# one is a class
    /// with no public way to copy it, so this reads its state. See the note in the port's report.
    /// </summary>
    public static RandomSource CopyOf(RandomSource rng)
    {
        var field = typeof(RandomSource).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RandomSource has no _state field to copy");
        return new RandomSource((ulong)field.GetValue(rng)!);
    }

    public delegate void StepHandler(SheepheadState state, int seat, SheepheadAction action, RandomSource before);

    /// <summary>
    /// Drives every seat with the AI until the hand is over. <paramref name="onStep"/> sees the
    /// state before each decision and a copy of the generator as the AI received it, so a test can
    /// check the position it was made in or replay the decision.
    /// </summary>
    public static ActionResult PlayHand(SheepheadState state, RandomSource rng, StepHandler? onStep = null)
    {
        var guardCount = 0;
        while (state.Phase != SheepheadPhase.HandOver)
        {
            guardCount++;
            if (guardCount > 500) Assert.Fail($"stuck in {state.Phase}");
            if (SheepheadGame.SeatToAct(state) is not { } seat)
            {
                Assert.Fail($"nobody to act in {state.Phase}");
                return ActionResult.Refused("nobody");
            }
            var beforeDecision = onStep is null ? null : CopyOf(rng);   // what the AI was handed
            var action = SheepheadAI.Decide(state, seat, rng);
            if (action is null)
            {
                Assert.Fail("AI had nothing to decide");
                return ActionResult.Refused("no decision");
            }
            onStep?.Invoke(state, seat, action, beforeDecision!);
            var before = Progress(state);
            var r = SheepheadGame.ApplyAction(state, seat, action, rng);
            if (!r.IsOk) Assert.Fail($"AI action {J(action)} refused: {r.Reason}");
            var after = Progress(state);
            Assert.NotEqual(before, after);
        }
        return ActionResult.Ok;
    }

    private static string Progress(SheepheadState s) =>
        $"{s.Phase}:{s.Turn}:{string.Join(",", s.Players.Select(p => p.Hand.Count))}";

    /// <summary>
    /// A five-player game with seat 4 dealing, so seat 0 leads and picks first, and the default
    /// names You, Alice, Ben, Cara, Elle.
    /// </summary>
    public static (SheepheadState State, RandomSource Rng) FiveHanded(ulong seed = 1)
    {
        var (state, rng) = NewGame(5, seed);
        state.Dealer = 4;
        state.Leader = 0;
        state.Turn = 0;
        return (state, rng);
    }
}
