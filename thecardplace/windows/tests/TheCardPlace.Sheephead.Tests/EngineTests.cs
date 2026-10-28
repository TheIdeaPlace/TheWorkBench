using System.Text.Json;
using static TheCardPlace.Sheephead.Tests.Support;

namespace TheCardPlace.Sheephead.Tests;

/// <summary>
/// What the Windows contract adds to the Swift tests: actions survive JSON, states survive
/// Snapshot.Clone, and the IGameEngine adapter can run a table of computers on its own.
/// </summary>
public class EngineTests
{
    private static SheepheadAction Make(string kind) => kind switch
    {
        "start" => new SheepheadAction.Start(),
        "pick" => new SheepheadAction.Pick(),
        "pass" => new SheepheadAction.Pass(),
        "bury" => new SheepheadAction.Bury(Cards("AS", "TD")),
        "play" => new SheepheadAction.Play(Cd("QC")),
        "nextHand" => new SheepheadAction.NextHand(),
        "newGame" => new SheepheadAction.NewGame(),
        _ => throw new ArgumentException(kind),
    };

    private static readonly string[] Kinds = ["start", "pick", "pass", "bury", "play", "nextHand", "newGame"];

    [Theory]
    [InlineData("start")]
    [InlineData("pick")]
    [InlineData("pass")]
    [InlineData("bury")]
    [InlineData("play")]
    [InlineData("nextHand")]
    [InlineData("newGame")]
    public void Every_action_round_trips_through_json(string kind)
    {
        var action = Make(kind);
        var json = Snapshot.Json(action);
        Assert.Contains($"\"type\":\"{kind}\"", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize<SheepheadAction>(json, Snapshot.Options);
        Assert.NotNull(back);
        Assert.Equal(action.GetType(), back.GetType());
        Assert.Equal(json, Snapshot.Json(back));
    }

    [Fact]
    public void The_round_trip_covers_every_action_case()
    {
        var cases = typeof(SheepheadAction).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(SheepheadAction)))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(Kinds.Select(k => Make(k).GetType().Name).Order(StringComparer.Ordinal), cases);
    }

    [Fact]
    public void A_mid_game_state_clones_exactly_and_plays_on_the_same()
    {
        var (state, rng) = NewGame(5, 31, difficulty: Difficulty.Normal, doublers: true);
        // A finished hand in the history, then into the middle of the next one.
        PlayHand(state, rng);
        Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.NextHand(), rng));
        var steps = 0;
        while (state.Phase != SheepheadPhase.Play || state.TrickLog.Count < 2 || state.Trick.Count < 2)
        {
            var seat = SheepheadGame.SeatToAct(state);
            if (seat is null)
            {
                Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.NextHand(), rng));
                continue;
            }
            var action = SheepheadAI.Decide(state, seat.Value, rng)!;
            Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, seat.Value, action, rng));
            Assert.True(++steps < 500, "never reached the middle of a trick");
        }
        Assert.True(state.Picker is not null || state.IsLeaster);
        Assert.Single(state.History);

        var copy = Snapshot.Clone(state);
        Assert.Equal(J(state), J(copy));

        // Every query reads the same from the copy.
        for (var s = 0; s < state.Seats; s++)
        {
            Assert.Equal(SheepheadReview.Status(state, s), SheepheadReview.Status(copy, s));
            Assert.Equal(SheepheadReview.PlayOrder(state, s), SheepheadReview.PlayOrder(copy, s));
            Assert.Equal(SheepheadGame.LegalPlays(state, s), SheepheadGame.LegalPlays(copy, s));
        }

        // And the two play on identically from the same seed.
        var r1 = new RandomSource(77);
        var r2 = new RandomSource(77);
        PlayHand(state, r1);
        PlayHand(copy, r2);
        Assert.Equal(J(state), J(copy));
    }

    [Fact]
    public void Pauses_before_plays_only()
    {
        var engine = new SheepheadEngine();
        var (state, rng) = FiveHanded();
        Assert.False(engine.PausesBefore(state, new SheepheadAction.Pass()));
        Assert.Equal(ActionResult.Ok, engine.Apply(state, 0, new SheepheadAction.Pick(), rng));
        var bury = SheepheadAI.Decide(state, 0, rng)!;
        Assert.IsType<SheepheadAction.Bury>(bury);
        Assert.False(engine.PausesBefore(state, bury));
        Assert.Equal(ActionResult.Ok, engine.Apply(state, 0, bury, rng));
        var play = engine.Decide(state, engine.SeatToAct(state)!.Value, rng)!;
        Assert.IsType<SheepheadAction.Play>(play);
        Assert.True(engine.PausesBefore(state, play));
    }

    public static TheoryData<Difficulty, int> Tables()
    {
        var data = new TheoryData<Difficulty, int>();
        foreach (var d in DifficultyExtensions.All)
            for (var n = 3; n <= 6; n++)
                data.Add(d, n);
        return data;
    }

    /// <summary>
    /// Sheephead has no end to the game: hands go on until the player stops. So "a whole game" is a
    /// fixed run of hands, each reaching its end and the next dealt the way the app's driver does.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tables))]
    public void The_engine_runs_a_table_of_computers_on_its_own(Difficulty difficulty, int players)
    {
        const int hands = 8;
        IGameEngine<SheepheadState, SheepheadAction> engine = new SheepheadEngine();
        var state = SheepheadGame.CreateGame(new SheepheadConfig
        {
            Players = players,
            Difficulty = difficulty,
            BlackQueenDoubler = true,
            RedQueenDoubler = true,
            RedealDoubler = true,
            AllPass = players % 2 == 0 ? SheepheadAllPass.Redeal : SheepheadAllPass.Leaster,
        });
        Assert.Equal(players, engine.Seats(state));
        Assert.Equal(Occupant.Human, engine.OccupantOf(state, 0));
        state.Players[0].Occupant = Occupant.Bot;   // nobody at this PC: the computer plays seat 0 too
        var rng = new RandomSource((ulong)(players * 100 + (int)difficulty));
        Assert.Equal(ActionResult.Ok, engine.Apply(state, 0, new SheepheadAction.Start(), rng));

        var steps = 0;
        while (state.History.Count < hands)
        {
            Assert.True(++steps < hands * 500, "the table stopped making progress");
            if (engine.SeatToAct(state) is not { } seat)
            {
                Assert.Equal(SheepheadPhase.HandOver, state.Phase);
                Assert.True(SheepheadGame.CanDeal(state));
                Assert.Equal(ActionResult.Ok, engine.Apply(state, 0, new SheepheadAction.NextHand(), rng));
                continue;
            }
            Assert.Equal(Occupant.Bot, engine.OccupantOf(state, seat));
            var action = engine.Decide(state, seat, rng);
            Assert.NotNull(action);
            Assert.Equal(action is SheepheadAction.Play, engine.PausesBefore(state, action));
            var r = engine.Apply(state, seat, action, rng);
            Assert.True(r.IsOk, r.Reason);
        }

        Assert.Equal(SheepheadPhase.HandOver, state.Phase);
        Assert.Null(engine.SeatToAct(state));
        Assert.Same(state.Log, engine.Log(state));
        Assert.All(state.History, h => Assert.Empty(h.Problems));
        Assert.Equal(0, state.Players.Sum(p => p.Score));
        Assert.Equal(state.History.Select(h => h.Result.Deltas[0]).Sum(), state.Players[0].Score);
        Assert.Equal(state.History[^1].ScoresAfter, state.Players.Select(p => p.Score));
    }
}
