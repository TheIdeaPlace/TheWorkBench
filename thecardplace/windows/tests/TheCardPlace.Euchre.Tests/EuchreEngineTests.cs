using System.Text.Json;
using static TheCardPlace.Euchre.EuchreAction;

namespace TheCardPlace.Euchre.Tests;

/// <summary>
/// What the Windows contract adds to the Swift tests: actions survive JSON, a state survives
/// Snapshot.Clone unchanged, and the IGameEngine adapter can run a table of computers to the end
/// of a game on every difficulty.
/// </summary>
public class EuchreEngineTests
{
    static readonly EuchreEngine Engine = new();

    public static TheoryData<EuchreAction> EveryAction() =>
    [
        new Start(),
        new OrderUp(false),
        new OrderUp(true),
        new Pass(),
        new CallSuit(Suit.Hearts, false),
        new CallSuit(Suit.Clubs, true),
        new Discard(Card.Parse("9S")),
        new Play(Card.Parse("JC")),
        new NextHand(),
        new NewGame(),
    ];

    [Theory]
    [MemberData(nameof(EveryAction))]
    public void Every_action_round_trips_through_json(EuchreAction action)
    {
        var json = Snapshot.Json(action);
        Assert.StartsWith("{\"type\":", json);
        var back = JsonSerializer.Deserialize<EuchreAction>(json, Snapshot.Options);
        Assert.NotNull(back);
        Assert.Equal(action, back);
        Assert.Equal(json, Snapshot.Json(back));
    }

    [Fact]
    public void The_round_trip_list_covers_every_action_case()
    {
        var cases = typeof(EuchreAction).GetNestedTypes().Where(t => t.IsSubclassOf(typeof(EuchreAction))).ToHashSet();
        var tested = ((IEnumerable<object[]>)EveryAction()).Select(row => row[0].GetType()).ToHashSet();
        Assert.True(cases.SetEquals(tested), "an action case is missing from the round-trip test");
        Assert.Equal(8, cases.Count);
    }

    [Fact]
    public void Action_wire_format_is_readable()
    {
        Assert.Equal("{\"type\":\"play\",\"Card\":\"JC\"}", Snapshot.Json<EuchreAction>(new Play(Card.Parse("JC"))));
        Assert.Equal("{\"type\":\"callSuit\",\"Suit\":\"Hearts\",\"Alone\":true}", Snapshot.Json<EuchreAction>(new CallSuit(Suit.Hearts, true)));
        Assert.Equal("{\"type\":\"pass\"}", Snapshot.Json<EuchreAction>(new Pass()));
    }

    /// <summary>
    /// A mid-game state with everything filled in (history, a result, a last trick, a trick in
    /// progress, a discard, somebody alone if the seed gives it) survives a clone byte for byte,
    /// and the clone then plays on exactly as the original does.
    /// </summary>
    [Theory]
    [InlineData(Difficulty.Easy, 41UL)]
    [InlineData(Difficulty.Normal, 42UL)]
    [InlineData(Difficulty.Hard, 43UL)]
    public void A_mid_game_state_clones_exactly(Difficulty difficulty, ulong seed)
    {
        var rng = new RandomSource(seed);
        var s = EuchreGame.CreateGame(Support.Config(difficulty: difficulty, stick: true));
        for (var i = 0; i < 3; i++) Support.PlayHand(s, rng);
        Assert.Equal(EuchrePhase.HandOver, s.Phase);
        Assert.True(EuchreGame.ApplyAction(s, 0, new NextHand(), rng).IsOk);
        // Into the play, and part way through a trick after at least one has been taken.
        Support.Drive(s, rng, x => x.Phase == EuchrePhase.Play && x.TrickLog.Count >= 1 && x.Trick.Count >= 1);

        Assert.NotEmpty(s.History);
        Assert.NotNull(s.LastTrick);
        Assert.NotNull(s.Trump);
        Assert.NotNull(s.Dealt);
        Assert.NotEmpty(s.BidLog);

        var json = Snapshot.Json(s);
        var copy = Snapshot.Clone(s);
        Assert.Equal(json, Snapshot.Json(copy));
        Assert.Equal(s.Upcard, copy.Upcard);
        Assert.Equal(s.Discard, copy.Discard);
        Assert.Equal(s.Players[0].Hand, copy.Players[0].Hand);

        // The copy shares nothing with the original: changing it leaves the original alone.
        copy.Players[1].Hand.Clear();
        copy.Scores[0] = 99;
        copy.History[0].Problems.Add("changed");
        Assert.Equal(json, Snapshot.Json(s));

        // And a fresh clone plays on identically.
        var twin = Snapshot.Clone(s);
        RandomSource r1 = new(7), r2 = new(7);
        Support.Drive(s, r1, Support.HandIsOver);
        Support.Drive(twin, r2, Support.HandIsOver);
        Assert.Equal(Snapshot.Json(s), Snapshot.Json(twin));
    }

    /// <summary>
    /// The table driver's loop, written the way the app will write it: ask whose turn it is, check
    /// the occupant, let the computer decide, apply. Seat 0 is human by default, so it is handed to
    /// the computer here.
    /// </summary>
    [Theory]
    [InlineData(Difficulty.Easy)]
    [InlineData(Difficulty.Normal)]
    [InlineData(Difficulty.Hard)]
    public void Bots_alone_play_a_whole_game_through_the_engine(Difficulty difficulty)
    {
        var rng = new RandomSource(2026 + (ulong)difficulty);
        var s = EuchreGame.CreateGame(Support.Config(difficulty: difficulty));
        Assert.Equal(Occupant.Human, Engine.OccupantOf(s, 0));
        s.Players[0].Occupant = Occupant.Bot;
        Assert.Equal(4, Engine.Seats(s));
        Assert.Same(s.Log, Engine.Log(s));

        var start = Engine.Apply(s, 0, new Start(), rng);
        Assert.True(start.IsOk);
        int moves = 0, hands = 1, pauses = 0;
        while (s.Phase != EuchrePhase.GameOver)
        {
            if (Engine.SeatToAct(s) is not { } seat)
            {
                Assert.Equal(EuchrePhase.HandOver, s.Phase);
                Assert.True(Engine.Apply(s, 0, new NextHand(), rng).IsOk);
                hands++;
                continue;
            }
            Assert.Equal(Occupant.Bot, Engine.OccupantOf(s, seat));
            var action = Engine.Decide(s, seat, rng);
            Assert.NotNull(action);
            if (Engine.PausesBefore(s, action)) pauses++;
            var r = Engine.Apply(s, seat, action, rng);
            Assert.True(r.IsOk, r.Reason);
            Assert.True(++moves < 20_000, "the game never ended");
        }
        Assert.Null(Engine.SeatToAct(s));
        Assert.NotNull(s.GameWinner);
        Assert.True(s.Scores[s.GameWinner.Value] >= 10);
        Assert.Equal(hands, s.History.Count);
        Assert.All(s.History, h => Assert.Empty(h.Problems));
        // Every card played paused, and nothing else did.
        Assert.Equal(s.History.Sum(h => h.Tricks.Sum(t => t.Plays.Count)), pauses);
    }

    [Fact]
    public void The_pace_applies_to_plays_and_not_to_bids_or_the_discard()
    {
        var bid = Support.Position(EuchrePhase.Bid1, upcard: "JS", upcardStatus: EuchreUpcardStatus.Up);
        Assert.False(Engine.PausesBefore(bid, new Pass()));
        Assert.False(Engine.PausesBefore(bid, new OrderUp(false)));
        var bid2 = Support.Position(EuchrePhase.Bid2, upcard: "JS", upcardStatus: EuchreUpcardStatus.TurnedDown, deniedSuit: Suit.Spades);
        Assert.False(Engine.PausesBefore(bid2, new CallSuit(Suit.Hearts, false)));
        var discard = Support.Position(EuchrePhase.Discard, dealer: 0, trump: Suit.Spades);
        Assert.False(Engine.PausesBefore(discard, new Discard(Card.Parse("9H"))));
        var play = Support.Position(EuchrePhase.Play, trump: Suit.Spades);
        Assert.True(Engine.PausesBefore(play, new Play(Card.Parse("9H"))));
    }

    [Fact]
    public void A_seat_outside_the_table_has_nothing_to_decide()
    {
        var s = EuchreGame.CreateGame(Support.Config());
        Assert.Equal(Occupant.Bot, Engine.OccupantOf(s, 7));
        Assert.Null(Engine.Decide(s, 7, new RandomSource(1)));
        Assert.Null(Engine.Decide(s, -1, new RandomSource(1)));
        Assert.Equal("not a seat at this table", Engine.Apply(s, 7, new Start(), new RandomSource(1)).Reason);
    }

    [Fact]
    public void A_config_is_normalised_as_the_Swift_initialiser_does()
    {
        var s = EuchreGame.CreateGame(new EuchreConfig { Names = ["Kim"], PointsToWin = 0 });
        Assert.Equal(["Kim", "Ruth", "Dale", "Marta"], s.Players.Select(p => p.Name));
        Assert.Equal(10, s.Config.PointsToWin);
        var many = EuchreGame.CreateGame(new EuchreConfig { Names = ["A", "B", "C", "D", "E"] });
        Assert.Equal(4, many.Config.Names.Count);
    }
}
