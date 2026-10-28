using System.Text.Json;
using static TheCardPlace.Hearts.Tests.Support;

namespace TheCardPlace.Hearts.Tests;

/// What the Windows contract adds to the Swift tests: actions survive JSON, a state survives
/// Snapshot.Clone, and the IGameEngine adapter can drive a table of computers to the end.
public class EngineContractTests
{
    public static TheoryData<string> ActionCases => new() { "start", "pass", "play", "nextHand", "newGame" };

    private static HeartsAction Make(string kind) => kind switch
    {
        "start" => new HeartsAction.Start(),
        "pass" => new HeartsAction.Pass(Cards("QS", "AH", "2C")),
        "play" => new HeartsAction.Play(C("TD")),
        "nextHand" => new HeartsAction.NextHand(),
        "newGame" => new HeartsAction.NewGame(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(ActionCases))]
    public void Every_action_round_trips_through_json(string kind)
    {
        var action = Make(kind);
        var json = Snapshot.Json(action);
        Assert.Contains($"\"type\":\"{kind}\"", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize<HeartsAction>(json, Snapshot.Options);
        Assert.NotNull(back);
        Assert.Equal(action.GetType(), back.GetType());
        Assert.Equal(json, Snapshot.Json(back));
    }

    [Fact]
    public void The_wire_format_is_card_ids()
    {
        Assert.Equal("""{"type":"pass","Cards":["QS","AH","2C"]}""", Snapshot.Json<HeartsAction>(new HeartsAction.Pass(Cards("QS", "AH", "2C"))));
        Assert.Equal("""{"type":"play","Card":"TD"}""", Snapshot.Json<HeartsAction>(new HeartsAction.Play(C("TD"))));
    }

    /// Plays forward from a seeded start until `stop` says so.
    private static (HeartsState State, RandomSource Rng) PlayUntil(ulong seed, Func<HeartsState, bool> stop)
    {
        var rng = new RandomSource(seed);
        var s = HeartsGame.CreateGame(Config());
        Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Start(), rng).IsOk);
        for (var i = 0; i < 5000 && !stop(s); i++)
        {
            if (s.Phase == HeartsPhase.HandOver)
            {
                Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.NextHand(), rng).IsOk);
                continue;
            }
            var seat = HeartsGame.SeatToAct(s)!.Value;
            Assert.True(HeartsGame.ApplyAction(s, seat, HeartsAI.Decide(s, seat, rng)!, rng).IsOk);
        }
        Assert.True(stop(s), "never reached the position asked for");
        return (s, rng);
    }

    [Fact]
    public void A_mid_game_state_clones_exactly_and_plays_on_the_same()
    {
        // Second hand, mid-trick: history, a last trick, taken piles, scores and a part trick.
        var (s, _) = PlayUntil(17, x => x.DealNumber == 2 && x.Phase == HeartsPhase.Play && x.TricksPlayed == 5 && x.Trick.Count == 2);
        Assert.NotEmpty(s.History);
        Assert.NotNull(s.LastTrick);
        Assert.Contains(s.Players, p => p.Score > 0);
        Assert.Contains(s.Received, r => r is not null);

        var copy = Snapshot.Clone(s);
        Assert.NotSame(s, copy);
        Assert.Equal(Snapshot.Json(s), Snapshot.Json(copy));
        Assert.Equal(Snapshot.Json(copy), Snapshot.Json(Snapshot.Clone(copy)));
        Assert.Equal(s.Players.Select(p => p.TakenPoints), copy.Players.Select(p => p.TakenPoints));
        Assert.Equal(s.Players.Select(p => p.HasQueen), copy.Players.Select(p => p.HasQueen));
        Assert.Equal(s.Log.LastId, copy.Log.LastId);
        Assert.Equal(HeartsGame.LegalPlays(s, s.Turn), HeartsGame.LegalPlays(copy, copy.Turn));
        Assert.Equal(HeartsReview.CardsPlayed(s, 0), HeartsReview.CardsPlayed(copy, 0));

        // The copy is not tied to the original: change one, the other stays.
        copy.Players[0].Hand.Clear();
        Assert.NotEmpty(s.Players[0].Hand);

        // And both play on identically from the same generator.
        var a = Snapshot.Clone(s);
        var b = Snapshot.Clone(s);
        var ra = new RandomSource(5);
        var rb = new RandomSource(5);
        for (var i = 0; i < 400 && a.Phase != HeartsPhase.GameOver; i++)
        {
            foreach (var (st, r) in new[] { (a, ra), (b, rb) })
            {
                if (st.Phase == HeartsPhase.HandOver) { Assert.True(HeartsGame.ApplyAction(st, 0, new HeartsAction.NextHand(), r).IsOk); continue; }
                var seat = HeartsGame.SeatToAct(st)!.Value;
                Assert.True(HeartsGame.ApplyAction(st, seat, HeartsAI.Decide(st, seat, r)!, r).IsOk);
            }
        }
        Assert.Equal(Snapshot.Json(a), Snapshot.Json(b));
    }

    [Fact]
    public void A_part_pass_clones_exactly()
    {
        var (s, _) = PlayUntil(23, x => x.Phase == HeartsPhase.Passing && x.Passing.Count(p => p is not null) == 2);
        var copy = Snapshot.Clone(s);
        Assert.Equal(Snapshot.Json(s), Snapshot.Json(copy));
        Assert.Equal(s.PassedIn, copy.PassedIn);
        Assert.Equal(HeartsGame.SeatToAct(s), HeartsGame.SeatToAct(copy));
        Assert.Equal(HeartsReview.Status(s, 0), HeartsReview.Status(copy, 0));
    }

    [Theory]
    [InlineData(Difficulty.Easy)]
    [InlineData(Difficulty.Normal)]
    [InlineData(Difficulty.Hard)]
    public void The_engine_adapter_drives_a_table_of_computers_to_the_end(Difficulty difficulty)
    {
        IGameEngine<HeartsState, HeartsAction> engine = new HeartsEngine();
        var cfg = Config(difficulty);
        cfg.Occupants = [Occupant.Bot, Occupant.Bot, Occupant.Bot, Occupant.Bot];
        var s = HeartsGame.CreateGame(cfg);
        var rng = new RandomSource(4000 + (ulong)difficulty);
        Assert.Equal(4, engine.Seats(s));
        for (var seat = 0; seat < 4; seat++) Assert.Equal(Occupant.Bot, engine.OccupantOf(s, seat));
        Assert.Equal(Occupant.Bot, engine.OccupantOf(s, 9));
        Assert.True(engine.Apply(s, 0, new HeartsAction.Start(), rng).IsOk);

        int steps = 0, pauses = 0;
        while (s.Phase != HeartsPhase.GameOver && steps < 8000)
        {
            steps++;
            if (engine.SeatToAct(s) is not { } seat)
            {
                Assert.True(HeartsGame.CanDeal(s), $"nobody to act in {s.Phase}");
                Assert.True(engine.Apply(s, 0, new HeartsAction.NextHand(), rng).IsOk);
                continue;
            }
            Assert.Equal(Occupant.Bot, engine.OccupantOf(s, seat));
            var action = engine.Decide(s, seat, rng);
            Assert.NotNull(action);
            var pauses_ = engine.PausesBefore(s, action);
            Assert.Equal(action is HeartsAction.Play, pauses_);
            if (pauses_) pauses++;
            var r = engine.Apply(s, seat, action, rng);
            Assert.True(r.IsOk, r.Reason);
        }
        Assert.Equal(HeartsPhase.GameOver, s.Phase);
        Assert.Null(engine.SeatToAct(s));
        Assert.Contains(s.Players, p => p.Score >= HeartsGame.Target(s));
        Assert.Contains(engine.Log(s).Events, e => e.Kind == EventKind.Game);
        Assert.Equal(s.History.Count * 52, pauses);   // every card played paused; nothing else did

        // A new game keeps the table as it was seated.
        Assert.True(engine.Apply(s, 0, new HeartsAction.NewGame(), rng).IsOk);
        for (var seat = 0; seat < 4; seat++) Assert.Equal(Occupant.Bot, engine.OccupantOf(s, seat));
    }

    [Fact]
    public void Pauses_come_before_plays_and_nothing_else()
    {
        var engine = new HeartsEngine();
        var passing = HeartsGame.CreateGame(Config());
        Assert.True(HeartsGame.ApplyAction(passing, 0, new HeartsAction.Start(), new RandomSource(2)).IsOk);
        Assert.Equal(HeartsPhase.Passing, passing.Phase);
        Assert.False(engine.PausesBefore(passing, HeartsAI.Decide(passing, 1, new RandomSource(1))!));

        var play = PlayState([["2C"], ["3C"], ["4C"], ["5C"]], turn: 0);
        Assert.True(engine.PausesBefore(play, new HeartsAction.Play(C("2C"))));

        var over = HeartsGame.CreateGame(Config());
        over.Phase = HeartsPhase.HandOver;
        Assert.False(engine.PausesBefore(over, new HeartsAction.NextHand()));
        Assert.False(engine.PausesBefore(HeartsGame.CreateGame(Config()), new HeartsAction.Start()));
    }

    [Fact]
    public void Seats_default_to_the_person_in_seat_zero()
    {
        var s = HeartsGame.CreateGame(Config());
        Assert.Equal([Occupant.Human, Occupant.Bot, Occupant.Bot, Occupant.Bot], s.Players.Select(p => p.Occupant));
        var partial = Config();
        partial.Occupants = [Occupant.Bot];
        Assert.Equal([Occupant.Bot, Occupant.Bot, Occupant.Bot, Occupant.Bot],
                     HeartsGame.CreateGame(partial).Players.Select(p => p.Occupant));
        var named = HeartsGame.CreateGame(new HeartsConfig { Names = ["Ruth"] });
        Assert.Equal(["Ruth", "Seat 2", "Seat 3", "Seat 4"], named.Players.Select(p => p.Name));
    }

    [Fact]
    public void The_table_does_not_share_the_callers_config()
    {
        var cfg = Config();
        var s = HeartsGame.CreateGame(cfg);
        cfg.Names[0] = "Changed";
        cfg.PointsToWin = 50;
        Assert.Equal("North", s.Config.Names[0]);
        Assert.Equal(100, HeartsGame.Target(s));
    }
}
