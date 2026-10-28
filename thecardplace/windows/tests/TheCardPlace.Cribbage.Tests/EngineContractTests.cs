using System.Text.Json;
using static TheCardPlace.Cribbage.Tests.T;

namespace TheCardPlace.Cribbage.Tests;

/// <summary>
/// What the Windows contract adds to the Swift tests: actions survive the wire, states survive a
/// clone, and the <see cref="IGameEngine{TState, TAction}"/> adapter can run a whole table alone.
/// </summary>
public sealed class EngineContractTests
{
    private static readonly List<CribbageAction> EveryAction =
    [
        new CribbageAction.Start(),
        new CribbageAction.Cut(),
        new CribbageAction.Discard(Cs("AS", "5H")),
        new CribbageAction.Play(C("TD")),
        new CribbageAction.Go(),
        new CribbageAction.Next(),
        new CribbageAction.NextHand(),
        new CribbageAction.NewGame(),
    ];

    [Fact]
    public void EveryActionRoundTripsThroughJson()
    {
        // Every case of the action is in the list above, so a new case cannot slip past this test.
        var cases = typeof(CribbageAction).GetNestedTypes().Where(t => t.IsSubclassOf(typeof(CribbageAction))).ToList();
        Assert.Equal(cases.Count, EveryAction.Count);
        Assert.Equal(cases.ToHashSet(), EveryAction.Select(a => a.GetType()).ToHashSet());

        foreach (var a in EveryAction)
        {
            var json = Snapshot.Json(a);
            Assert.Contains("\"type\":", json);
            var back = JsonSerializer.Deserialize<CribbageAction>(json, Snapshot.Options);
            Assert.NotNull(back);
            Assert.Equal(a.GetType(), back.GetType());
            Assert.Equal(json, Snapshot.Json(back));
        }
        Assert.Equal("{\"type\":\"discard\",\"Cards\":[\"AS\",\"5H\"]}", Snapshot.Json(EveryAction[2]));
        Assert.Equal("{\"type\":\"play\",\"Card\":\"TD\"}", Snapshot.Json(EveryAction[3]));
        Assert.Equal("{\"type\":\"nextHand\"}", Snapshot.Json(EveryAction[6]));
    }

    [Fact]
    public void AMidGameStateClonesExactly()
    {
        var rng = new RandomSource(42);
        var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Normal, 121));
        // Into the third hand, part way through the play, with history, a crib, a starter and a
        // count sequence that has reset at least once.
        Assert.True(Advance(state, rng, s => s.HandNumber >= 3 && s.Phase == CribbagePhase.Play && s.Pile.Count >= 4 && s.RunStart > 0));
        Assert.NotEmpty(state.History);
        Assert.NotNull(state.Starter);
        Assert.NotEmpty(state.Crib);

        var copy = Snapshot.Clone(state);
        Assert.Equal(Json(state), Json(copy));

        // And the copy plays on exactly as the original does.
        var r1 = new RandomSource(7);
        var r2 = new RandomSource(7);
        Assert.True(Advance(state, r1, s => s.Phase == CribbagePhase.GameOver));
        Assert.True(Advance(copy, r2, s => s.Phase == CribbagePhase.GameOver));
        Assert.Equal(Json(state), Json(copy));
    }

    [Fact]
    public void AStateInEveryPhaseClonesExactly()
    {
        var rng = new RandomSource(12);
        var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Hard, 61));
        var phases = new HashSet<CribbagePhase>();
        var steps = 0;
        while (steps++ < 4000)
        {
            phases.Add(state.Phase);
            Assert.Equal(Json(state), Json(Snapshot.Clone(state)));
            if (state.Phase == CribbagePhase.GameOver) break;
            if (state.Phase == CribbagePhase.Idle) { CribbageGame.ApplyAction(state, 0, new CribbageAction.Start(), rng); continue; }
            if (state.Phase == CribbagePhase.RoundOver) { CribbageGame.ApplyAction(state, 0, new CribbageAction.NextHand(), rng); continue; }
            Assert.True(CribbageAI.Act(state, rng)?.Result.IsOk);
        }
        Assert.Equal(Enum.GetValues<CribbagePhase>().Length, phases.Count);
    }

    [Theory]
    [InlineData(Difficulty.Easy)]
    [InlineData(Difficulty.Normal)]
    [InlineData(Difficulty.Hard)]
    public void TheAdapterPlaysAWholeGameWithBotsAlone(Difficulty difficulty)
    {
        IGameEngine<CribbageState, CribbageAction> engine = new CribbageEngine();
        var rng = new RandomSource(900 + (ulong)difficulty);
        var state = CribbageGame.CreateGame(Sim.Config(difficulty, 121));
        foreach (var p in state.Players) p.Occupant = Occupant.Bot;
        Assert.Equal(2, engine.Seats(state));
        Assert.Same(state.Log, engine.Log(state));

        Assert.Equal(ActionResult.Ok, engine.Apply(state, 0, new CribbageAction.Start(), rng));
        var steps = 0;
        var paused = 0;
        while (state.Phase != CribbagePhase.GameOver && steps++ < 8000)
        {
            if (engine.SeatToAct(state) is not { } seat)
            {
                Assert.True(CribbageGame.CanDeal(state), $"nobody to act at {state.Phase}");
                Assert.Equal(ActionResult.Ok, engine.Apply(state, 0, new CribbageAction.NextHand(), rng));
                continue;
            }
            Assert.Equal(Occupant.Bot, engine.OccupantOf(state, seat));
            var action = engine.Decide(state, seat, rng);
            Assert.NotNull(action);
            if (engine.PausesBefore(state, action)) paused++;
            var r = engine.Apply(state, seat, action, rng);
            Assert.True(r.IsOk, r.Reason);
        }
        Assert.Equal(CribbagePhase.GameOver, state.Phase);
        Assert.NotNull(state.GameWinner);
        Assert.True(state.Players[state.GameWinner.Value].Score >= 121);
        Assert.True(paused > 0);
        Assert.Null(engine.SeatToAct(state));
    }

    /// <summary>
    /// The iOS session waits its pace before a play, a go, or a step of the count; the cut and the
    /// throw to the crib happen at once.
    /// </summary>
    [Fact]
    public void PausesBeforeThePlayAndTheCountOnly()
    {
        var engine = new CribbageEngine();
        var rng = new RandomSource(4);
        var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Normal, 121));
        var seen = new Dictionary<Type, bool>();
        CribbageGame.ApplyAction(state, 0, new CribbageAction.Start(), rng);
        for (var steps = 0; steps < 400 && state.Phase != CribbagePhase.GameOver; steps++)
        {
            if (engine.SeatToAct(state) is not { } seat)
            {
                engine.Apply(state, 0, new CribbageAction.NextHand(), rng);
                continue;
            }
            var action = engine.Decide(state, seat, rng)!;
            var pauses = engine.PausesBefore(state, action);
            if (seen.TryGetValue(action.GetType(), out var was)) Assert.Equal(was, pauses);
            seen[action.GetType()] = pauses;
            Assert.True(engine.Apply(state, seat, action, rng).IsOk);
        }
        Assert.False(seen[typeof(CribbageAction.Cut)]);
        Assert.False(seen[typeof(CribbageAction.Discard)]);
        Assert.True(seen[typeof(CribbageAction.Play)]);
        Assert.True(seen[typeof(CribbageAction.Go)]);
        Assert.True(seen[typeof(CribbageAction.Next)]);
    }

    [Fact]
    public void TheAdapterSaysWhoSitsWhere()
    {
        var engine = new CribbageEngine();
        var state = CribbageGame.CreateGame(new CribbageConfig());
        Assert.Equal(Occupant.Human, engine.OccupantOf(state, 0));
        Assert.Equal(Occupant.Bot, engine.OccupantOf(state, 1));
        Assert.Null(engine.SeatToAct(state));
        Assert.Null(engine.Decide(state, 0, new RandomSource(1)));
    }
}
