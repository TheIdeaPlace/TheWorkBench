using static TheCardPlace.Spades.Tests.T;

namespace TheCardPlace.Spades.Tests;

/// <summary>
/// The wrong seat, the wrong phase, a card not held: each refused with a reason and the state
/// unchanged. A refusal that has already happened is not a refusal, so every one is compared
/// against the state before it.
/// </summary>
public class RefusalTests
{
    private static ActionResult MustRefuse(SpadesState state, int seat, SpadesAction? action, string what)
    {
        var before = Json(state);
        var r = SpadesGame.ApplyAction(state, seat, action!, new RandomSource(99));
        Assert.False(r.IsOk, $"{what} was accepted");
        Assert.False(r.IsFault, $"{what} faulted rather than refused");
        Assert.False(string.IsNullOrEmpty(r.Reason), $"{what} was refused with no reason");
        Assert.True(before == Json(state), $"{what} was refused but the state changed");
        return r;
    }

    [Fact]
    public void Nothing_is_accepted_before_the_game_starts()
    {
        var state = SpadesGame.CreateGame(Config());
        Assert.Equal("no trick is in progress", MustRefuse(state, 0, new SpadesAction.Play(C("AS")), "a card at idle").Reason);
        Assert.Equal("nobody is bidding", MustRefuse(state, 0, new SpadesAction.Bid(3), "a bid at idle").Reason);
        Assert.Equal("the hand is not over", MustRefuse(state, 0, new SpadesAction.NextHand(), "a deal at idle").Reason);
        Assert.Equal("the game has not started", MustRefuse(state, 0, new SpadesAction.NewGame(), "a new game at idle").Reason);
        Assert.Equal("not a seat at this table", MustRefuse(state, 9, new SpadesAction.Start(), "seat 9").Reason);
        Assert.Equal("not a seat at this table", MustRefuse(state, -1, new SpadesAction.Start(), "seat -1").Reason);
        Assert.Equal("not a seat at this table", MustRefuse(state, 4, new SpadesAction.Bid(1), "seat 4").Reason);
    }

    /// Not in Swift, whose action enum is closed: C# can be handed a null.
    [Fact]
    public void A_null_action_is_refused_not_thrown()
    {
        var state = SpadesGame.CreateGame(Config());
        Assert.Equal("that is not a move in spades", MustRefuse(state, 0, null, "a null action").Reason);
        var (playing, _) = Drive.ToPlay(null, 80);
        MustRefuse(playing, playing.Turn, null, "a null action in play");
    }

    [Fact]
    public void The_bidding()
    {
        var (state, rng) = Drive.NewGame(77);
        Assert.Equal(SpadesPhase.Bidding, state.Phase);
        Assert.Equal("the game has already started", MustRefuse(state, 0, new SpadesAction.Start(), "a second start").Reason);

        for (var round = 0; round < 4; round++)
        {
            var turn = state.Turn;
            for (var other = 0; other < 4; other++)
            {
                if (other == turn) continue;
                foreach (var n in new[] { 0, 1, 5, 13 })
                    Assert.Equal("not your turn to bid",
                        MustRefuse(state, other, new SpadesAction.Bid(n), $"seat {other} bidding out of turn").Reason);
                Assert.Equal("no trick is in progress",
                    MustRefuse(state, other, new SpadesAction.Play(state.Players[other].Hand[0]), "a card during the bidding").Reason);
            }
            foreach (var bad in new[] { -1, 14, 100 })
                Assert.Equal("a bid is a whole number from zero to 13",
                    MustRefuse(state, turn, new SpadesAction.Bid(bad), $"a bid of {bad}").Reason);
            Assert.Null(state.Players[turn].Bid);
            MustRefuse(state, turn, new SpadesAction.Play(state.Players[turn].Hand[0]), "the bidder playing a card");
            Assert.Equal("the hand is not over", MustRefuse(state, turn, new SpadesAction.NextHand(), "a deal during the bidding").Reason);

            Assert.True(SpadesGame.ApplyAction(state, turn, new SpadesAction.Bid(round), rng).IsOk);
            if (state.Phase == SpadesPhase.Bidding)
                Assert.Equal("not your turn to bid", MustRefuse(state, turn, new SpadesAction.Bid(2), "bidding twice").Reason);
        }
        Assert.Equal(SpadesPhase.Play, state.Phase);
        Assert.Empty(SpadesGame.LegalBids(state, state.Turn));
    }

    [Fact]
    public void Legal_bids_are_zero_to_thirteen_for_the_bidder_only()
    {
        var (state, _) = Drive.NewGame(77);
        Assert.Equal(Enumerable.Range(0, 14), SpadesGame.LegalBids(state, state.Turn));
        Assert.Empty(SpadesGame.LegalBids(state, (state.Turn + 1) % 4));
    }

    [Fact]
    public void Every_seat_is_offered_every_card_at_every_position_of_a_hand()
    {
        var (state, rng) = Drive.ToPlay(Config(pointsToWin: 250), 78);
        var positions = 0;
        string[] heldReasons =
        [
            "you must follow clubs", "you must follow diamonds", "you must follow hearts",
            "you must follow spades", "spades have not been broken", "not a legal card here",
        ];
        while (state.Phase == SpadesPhase.Play)
        {
            positions++;
            var turn = state.Turn;
            var legal = SpadesGame.LegalPlays(state, turn).ToHashSet();

            for (var other = 0; other < 4; other++)
            {
                if (other == turn) continue;
                foreach (var c in state.Players[other].Hand.Take(3).ToList())
                    Assert.Equal("not your turn", MustRefuse(state, other, new SpadesAction.Play(c), $"seat {other} playing out of turn").Reason);
                Assert.Equal("nobody is bidding", MustRefuse(state, other, new SpadesAction.Bid(1), "a bid during play").Reason);
                Assert.Equal("the hand is not over", MustRefuse(state, other, new SpadesAction.NextHand(), "a deal mid-hand").Reason);
            }

            var held = state.Players[turn].Hand.ToHashSet();
            foreach (var c in SpadesCards.NewDeck().Where(c => !legal.Contains(c)))
            {
                var r = MustRefuse(state, turn, new SpadesAction.Play(c), $"playing {c.Id}");
                if (held.Contains(c))
                {
                    Assert.Contains(r.Reason ?? "", heldReasons);
                    Assert.Equal(r.Reason, SpadesGame.WhyNot(state, turn, c));
                }
                else
                {
                    Assert.Equal("you do not hold that card", r.Reason);
                    Assert.Equal("you do not hold that card", SpadesGame.WhyNot(state, turn, c));
                }
            }
            foreach (var c in legal) Assert.Null(SpadesGame.WhyNot(state, turn, c));

            var pick = SpadesAI.ChooseCard(state, turn)!.Value;
            Assert.True(SpadesGame.ApplyAction(state, turn, new SpadesAction.Play(pick), rng).IsOk);
        }
        Assert.Equal(52, positions);
        Assert.True(state.Phase is SpadesPhase.HandOver or SpadesPhase.GameOver);
        for (var s = 0; s < 4; s++)
        {
            MustRefuse(state, s, new SpadesAction.Bid(3), "a bid at handOver");
            MustRefuse(state, s, new SpadesAction.Play(C("AS")), "a card at handOver");
            MustRefuse(state, s, new SpadesAction.Start(), "a start at handOver");
        }
        if (state.Phase == SpadesPhase.HandOver)
            Assert.True(SpadesGame.ApplyAction(state, 3, new SpadesAction.NextHand(), rng).IsOk, "any seat may deal");
    }

    [Fact]
    public void Why_not_names_the_rule()
    {
        // North leads, spades not broken, holding a spade and a club.
        var state = Position([3, 3, 3, 3], [0, 0, 0, 0], ["AS 2C", "3C 4D", "5C 6C", "7C 8C"], 0, 0);
        Assert.Equal("spades have not been broken", SpadesGame.WhyNot(state, 0, C("AS")));
        Assert.Null(SpadesGame.WhyNot(state, 0, C("2C")));
        Assert.Equal("not your turn", SpadesGame.WhyNot(state, 1, C("3C")));
        Assert.Equal("not a seat at this table", SpadesGame.WhyNot(state, 7, C("3C")));
        var rng = new RandomSource(1);
        Assert.Equal("spades have not been broken", SpadesGame.ApplyAction(state, 0, new SpadesAction.Play(C("AS")), rng).Reason);
        Assert.True(SpadesGame.ApplyAction(state, 0, new SpadesAction.Play(C("2C")), rng).IsOk);
        // East holds a club and a diamond; clubs were led.
        Assert.Equal("you must follow clubs", SpadesGame.WhyNot(state, 1, C("4D")));
        Assert.Equal("you must follow clubs", SpadesGame.ApplyAction(state, 1, new SpadesAction.Play(C("4D")), rng).Reason);
        Assert.True(state.Players[1].Hand.Count == 2, "a refused play removed the card");
    }

    /// A refusal may not name a card the asker cannot see: the same refusal for every card not
    /// held, and the same words whatever the other seats hold.
    [Fact]
    public void Refusals_are_not_a_side_channel()
    {
        var (state, _) = Drive.ToPlay(null, 79);
        var turn = state.Turn;
        var mine = state.Players[turn].Hand.ToHashSet();
        foreach (var c in SpadesCards.NewDeck().Where(c => !mine.Contains(c)))
        {
            var probe = Snapshot.Clone(state);
            var r = SpadesGame.ApplyAction(probe, turn, new SpadesAction.Play(c), new RandomSource(1));
            Assert.False(r.IsOk);
            Assert.True(r.Reason == "you do not hold that card", $"asking for {c.Id} said more than that it is not held");
        }

        // Move the other three hands around and ask again.
        var scrambled = Snapshot.Clone(state);
        var pool = new List<Card>();
        for (var i = 0; i < 4; i++)
            if (i != turn) pool.AddRange(scrambled.Players[i].Hand);
        pool.Reverse();
        var k = 0;
        for (var i = 0; i < 4; i++)
        {
            if (i == turn) continue;
            var n = scrambled.Players[i].Hand.Count;
            scrambled.Players[i].Hand = pool.GetRange(k, n);
            k += n;
        }
        foreach (var c in SpadesCards.NewDeck())
        {
            var a = Snapshot.Clone(state);
            var b = Snapshot.Clone(scrambled);
            var ra = SpadesGame.ApplyAction(a, turn, new SpadesAction.Play(c), new RandomSource(1));
            var rb = SpadesGame.ApplyAction(b, turn, new SpadesAction.Play(c), new RandomSource(1));
            Assert.Equal(ra.IsOk, rb.IsOk);
            Assert.True(ra.Reason == rb.Reason, $"the refusal for {c.Id} changed when other seats' cards moved");
        }
    }
}
