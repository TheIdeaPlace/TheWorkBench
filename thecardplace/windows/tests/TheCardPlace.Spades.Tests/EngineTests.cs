using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using static TheCardPlace.Spades.Tests.T;

namespace TheCardPlace.Spades.Tests;

/// <summary>
/// What the Windows contract adds to the Swift tests: actions survive JSON, a state survives a
/// clone, and the <see cref="IGameEngine{TState,TAction}"/> adapter can run a table of computers
/// to the end on every difficulty.
/// </summary>
public class EngineTests
{
    private static readonly SpadesAction[] EveryAction =
    [
        new SpadesAction.Start(),
        new SpadesAction.Bid(0),
        new SpadesAction.Bid(7),
        new SpadesAction.Bid(13),
        new SpadesAction.Play(C("AS")),
        new SpadesAction.Play(C("TD")),
        new SpadesAction.NextHand(),
        new SpadesAction.NewGame(),
    ];

    [Fact]
    public void Every_action_case_round_trips_through_json()
    {
        foreach (var a in EveryAction)
        {
            var json = Snapshot.Json<SpadesAction>(a);
            Assert.Contains("\"type\":", json);
            var back = JsonSerializer.Deserialize<SpadesAction>(json, Snapshot.Options);
            Assert.NotNull(back);
            Assert.Equal(a.GetType(), back.GetType());
            Assert.Equal(json, Snapshot.Json<SpadesAction>(back));
            Assert.Equal(a, back);
        }

        // Every case the type declares is in the list above.
        var declared = typeof(SpadesAction).GetCustomAttributes<JsonDerivedTypeAttribute>().Select(d => d.DerivedType).ToHashSet();
        var nested = typeof(SpadesAction).GetNestedTypes().Where(t => t.IsSubclassOf(typeof(SpadesAction))).ToHashSet();
        Assert.Equal(nested, declared);
        Assert.Equal(declared, EveryAction.Select(a => a.GetType()).ToHashSet());
    }

    [Fact]
    public void The_wire_format_is_readable()
    {
        Assert.Equal("""{"type":"bid","Tricks":0}""", Snapshot.Json(SpadesAction.NilBid));
        Assert.Equal("""{"type":"play","Card":"QS"}""", Snapshot.Json<SpadesAction>(new SpadesAction.Play(C("QS"))));
        Assert.Equal("""{"type":"start"}""", Snapshot.Json<SpadesAction>(new SpadesAction.Start()));
    }

    /// A state in the middle of a hand, with a history, bags, a last trick and cards on the table,
    /// cloned and compared; then both copies played on with the same seed must stay identical.
    [Fact]
    public void A_mid_game_state_clones_exactly_and_plays_on_the_same()
    {
        var (state, rng) = Drive.NewGame(Config(pointsToWin: 500), 2024);
        while (!(state.DealNumber >= 2 && state.Phase == SpadesPhase.Play && state.TricksPlayed >= 4 && state.Trick.Count == 2))
            Assert.True(Drive.Step(state, rng)!.IsOk);
        Assert.NotNull(state.LastTrick);
        Assert.NotEmpty(state.History);

        var copy = Snapshot.Clone(state);
        Assert.Equal(Json(state), Json(copy));
        Assert.NotSame(state.Players, copy.Players);
        Assert.NotSame(state.Log, copy.Log);

        // The copy is a working game in its own right.
        var r1 = new RandomSource(55);
        var r2 = new RandomSource(55);
        for (var i = 0; i < 200 && state.Phase != SpadesPhase.GameOver; i++)
        {
            Drive.Step(state, r1);
            Drive.Step(copy, r2);
        }
        Assert.Equal(Json(state), Json(copy));
    }

    [Fact]
    public void Create_game_does_not_share_the_config()
    {
        var config = Config();
        var state = SpadesGame.CreateGame(config);
        config.PointsToWin = 1;
        config.Names[0] = "Somebody else";
        Assert.Equal(500, state.Config.PointsToWin);
        Assert.Equal("North", state.Config.Names[0]);
    }

    [Fact]
    public void Seats_and_occupants()
    {
        var engine = new SpadesEngine();
        var state = SpadesGame.CreateGame(Config());
        Assert.Equal(4, engine.Seats(state));
        Assert.Equal(Occupant.Human, engine.OccupantOf(state, 0));
        for (var s = 1; s < 4; s++) Assert.Equal(Occupant.Bot, engine.OccupantOf(state, s));
        Assert.Equal(Occupant.Human, engine.OccupantOf(state, 4));
        Assert.Equal(Occupant.Human, engine.OccupantOf(state, -1));
        Assert.Same(state.Log, engine.Log(state));
    }

    [Fact]
    public void The_table_pauses_before_a_computer_card_and_not_before_a_bid()
    {
        var engine = new SpadesEngine();
        var (state, rng) = Drive.NewGame(Config(), 6);
        var bid = engine.Decide(state, engine.SeatToAct(state)!.Value, rng)!;
        Assert.IsType<SpadesAction.Bid>(bid);
        Assert.False(engine.PausesBefore(state, bid));

        while (state.Phase == SpadesPhase.Bidding) Drive.Step(state, rng);
        var play = engine.Decide(state, engine.SeatToAct(state)!.Value, rng)!;
        Assert.IsType<SpadesAction.Play>(play);
        Assert.True(engine.PausesBefore(state, play));
    }

    [Theory]
    [InlineData(Difficulty.Easy)]
    [InlineData(Difficulty.Normal)]
    [InlineData(Difficulty.Hard)]
    public void Bots_alone_play_a_whole_game_through_the_engine(Difficulty difficulty)
    {
        IGameEngine<SpadesState, SpadesAction> engine = new SpadesEngine();
        var rng = new RandomSource(8000 + (ulong)difficulty);
        var state = SpadesGame.CreateGame(Config(difficulty));
        state.Players[0].Occupant = Occupant.Bot;
        Assert.True(engine.Apply(state, 0, new SpadesAction.Start(), rng).IsOk);

        var steps = 0;
        var pauses = 0;
        while (state.Phase != SpadesPhase.GameOver && steps < 40_000)
        {
            steps++;
            if (engine.SeatToAct(state) is not int seat)
            {
                Assert.True(SpadesGame.CanDeal(state), $"nobody to act at {state.Phase}");
                Assert.True(engine.Apply(state, 0, new SpadesAction.NextHand(), rng).IsOk);
                continue;
            }
            Assert.Equal(Occupant.Bot, engine.OccupantOf(state, seat));
            var action = engine.Decide(state, seat, rng);
            Assert.NotNull(action);
            if (engine.PausesBefore(state, action)) pauses++;
            var r = engine.Apply(state, seat, action, rng);
            Assert.True(r.IsOk, r.Reason);
        }
        Assert.Equal(SpadesPhase.GameOver, state.Phase);
        Assert.NotNull(state.Winner);
        Assert.Null(engine.SeatToAct(state));
        Assert.Equal(state.DealNumber * 52, pauses);
        Assert.Equal(EventKind.Game, engine.Log(state).Events[^1].Kind);
    }
}
