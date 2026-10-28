using static TheCardPlace.Cribbage.Tests.T;

namespace TheCardPlace.Cribbage.Tests;

/// <summary>
/// The wrong seat, the wrong phase, a card not held: each refused with a reason, and the state
/// compared before and after.
/// </summary>
public sealed class RefusalTests
{
    private static void Refuse(CribbageState state, RandomSource rng, int seat, CribbageAction action, string reason)
    {
        var before = Json(state);
        var r = CribbageGame.ApplyAction(state, seat, action, rng);
        Assert.False(r.IsOk, $"{action} was accepted");
        Assert.False(r.IsFault);
        Assert.Equal(reason, r.Reason);
        Assert.Equal(before, Json(state));
    }

    [Fact]
    public void BeforeTheGameStarts()
    {
        var rng = new RandomSource(1);
        var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Normal, 121));
        Refuse(state, rng, 2, new CribbageAction.Start(), "not a seat at this table");
        Refuse(state, rng, -1, new CribbageAction.Start(), "not a seat at this table");
        Refuse(state, rng, 0, new CribbageAction.Cut(), "there is nothing to cut for");
        Refuse(state, rng, 0, new CribbageAction.Discard(Cs("AS", "2S")), "it is not the discard");
        Refuse(state, rng, 0, new CribbageAction.Play(C("AS")), "it is not the play");
        Refuse(state, rng, 0, new CribbageAction.Go(), "it is not the play");
        Refuse(state, rng, 0, new CribbageAction.Next(), "there is nothing to count");
        Refuse(state, rng, 0, new CribbageAction.NextHand(), "the hand is not over");
        Refuse(state, rng, 0, new CribbageAction.NewGame(), "the game is not over");
        Assert.Null(CribbageGame.SeatToAct(state));
        Assert.False(CribbageGame.CanDeal(state));
        Assert.Empty(CribbageGame.LegalPlays(state, 0));
        Assert.Equal("it is not the play", CribbageGame.WhyNot(state, 0, C("AS")));

        Assert.Equal(ActionResult.Ok, CribbageGame.ApplyAction(state, 0, new CribbageAction.Start(), rng));
        Refuse(state, rng, 0, new CribbageAction.Start(), "the game has already started");
        Refuse(state, rng, 1, new CribbageAction.NextHand(), "the hand is not over");
    }

    [Fact]
    public void TheDiscard()
    {
        var rng = new RandomSource(3);
        var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Normal, 121));
        Assert.True(Advance(state, rng, s => s.Phase == CribbagePhase.Discard && s.SeatsOutstanding.Count == 2));
        var hand = state.Players[0].Hand.ToList();
        var notMine = state.Players[1].Hand[0];
        Refuse(state, rng, 0, new CribbageAction.Discard([hand[0]]), "choose exactly two cards");
        Refuse(state, rng, 0, new CribbageAction.Discard([hand[0], hand[1], hand[2]]), "choose exactly two cards");
        Refuse(state, rng, 0, new CribbageAction.Discard([hand[0], hand[0]]), "choose two different cards");
        Refuse(state, rng, 0, new CribbageAction.Discard([hand[0], notMine]), $"the {notMine.Name} is not in your hand");
        Refuse(state, rng, 0, new CribbageAction.Play(hand[0]), "it is not the play");
        Refuse(state, rng, 0, new CribbageAction.Go(), "it is not the play");
        Refuse(state, rng, 0, new CribbageAction.Cut(), "there is nothing to cut for");
        Refuse(state, rng, 0, new CribbageAction.Next(), "there is nothing to count");
        Refuse(state, rng, 0, new CribbageAction.NextHand(), "the hand is not over");
        Assert.Equal(0, CribbageGame.SeatToAct(state));

        Assert.Equal(ActionResult.Ok, CribbageGame.ApplyAction(state, 0, new CribbageAction.Discard([hand[0], hand[1]]), rng));
        Assert.True(state.Phase == CribbagePhase.Discard, "the hand waits for the other throw");
        Assert.Equal(4, state.Players[0].Hand.Count);
        Assert.True(state.Crib.Count == 0, "the crib is not formed until both are in");
        Assert.Equal(1, CribbageGame.SeatToAct(state));
        Refuse(state, rng, 0, new CribbageAction.Discard([hand[2], hand[3]]), "you have already thrown to the crib");
        // Seat 0 cannot throw for seat 1.
        var theirs = state.Players[1].Hand.ToList();
        Refuse(state, rng, 0, new CribbageAction.Discard([theirs[0], theirs[1]]), "you have already thrown to the crib");
        Assert.Equal(ActionResult.Ok, CribbageGame.ApplyAction(state, 1, new CribbageAction.Discard([theirs[0], theirs[1]]), rng));
        Assert.Equal(CribbagePhase.Play, state.Phase);
        Assert.Equal(4, state.Crib.Count);
        Assert.NotNull(state.Starter);
        Assert.Equal(39, state.Deck.Count);
        Assert.Equal(1 - state.Dealer!.Value, state.Turn);
    }

    [Fact]
    public void ThePlay()
    {
        var rng = new RandomSource(11);
        var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Hard, 121));
        Assert.True(Advance(state, rng, s => s.Phase == CribbagePhase.Play && s.Pile.Count == 0));
        var me = state.Turn;
        var them = 1 - me;
        var mine = state.Players[me].Hand[0];
        var theirs = state.Players[them].Hand[0];
        Refuse(state, rng, them, new CribbageAction.Play(theirs), "not your turn");
        Refuse(state, rng, them, new CribbageAction.Go(), "not your turn");
        Refuse(state, rng, me, new CribbageAction.Play(theirs), "that card is not in your hand");
        Refuse(state, rng, me, new CribbageAction.Go(), "you have a card you can play, so you must play it");
        Refuse(state, rng, me, new CribbageAction.Discard([mine, state.Players[me].Hand[1]]), "it is not the discard");
        Refuse(state, rng, me, new CribbageAction.Next(), "there is nothing to count");
        Refuse(state, rng, me, new CribbageAction.NextHand(), "the hand is not over");
        Refuse(state, rng, me, new CribbageAction.NewGame(), "the game is not over");
        Assert.Empty(CribbageGame.LegalPlays(state, them));
        Assert.Equal("it is not your turn", CribbageGame.WhyNot(state, them, theirs));
        Assert.Null(CribbageGame.WhyNot(state, me, mine));
    }

    [Fact]
    public void ACardThatWouldPassThirtyOne()
    {
        // Find a position where the seat to act holds a card too big to play.
        var found = false;
        for (ulong seed = 20; seed < 80 && !found; seed++)
        {
            var rng = new RandomSource(seed);
            var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Hard, 121));
            var ok = Advance(state, rng, s =>
                s.Phase == CribbagePhase.Play && s.Players[s.Turn].Hand.Any(c => s.Count + CribbageCards.Value(c) > 31)
                && CribbageGame.LegalPlays(s, s.Turn).Count > 0);
            if (!ok) continue;
            found = true;
            var me = state.Turn;
            var big = state.Players[me].Hand.First(c => state.Count + CribbageCards.Value(c) > 31);
            var to = state.Count + CribbageCards.Value(big);
            var reason = "it would take the count to " + CribbageCards.NumberWord(to) + ", past thirty-one";
            Refuse(state, rng, me, new CribbageAction.Play(big), reason);
            Assert.Equal(reason, CribbageGame.WhyNot(state, me, big));
            Assert.DoesNotContain(big, CribbageGame.LegalPlays(state, me));
            Refuse(state, rng, me, new CribbageAction.Go(), "you have a card you can play, so you must play it");
        }
        Assert.True(found, "no position with an unplayable card was found");
    }

    [Fact]
    public void TheGoIsOnlyForSomebodyWhoCannotPlay()
    {
        var found = false;
        for (ulong seed = 100; seed < 160 && !found; seed++)
        {
            var rng = new RandomSource(seed);
            var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Hard, 121));
            var ok = Advance(state, rng, s =>
                s.Phase == CribbagePhase.Play && s.Players[s.Turn].Hand.Count > 0 && CribbageGame.LegalPlays(s, s.Turn).Count == 0);
            if (!ok) continue;
            found = true;
            var me = state.Turn;
            var held = state.Players[me].Hand[0];
            Refuse(state, rng, me, new CribbageAction.Play(held),
                   "it would take the count to " + CribbageCards.NumberWord(state.Count + CribbageCards.Value(held)) + ", past thirty-one");
            Refuse(state, rng, 1 - me, new CribbageAction.Go(), "not your turn");
            var scoreBefore = state.Players.Select(p => p.Score).ToList();
            Assert.Equal(ActionResult.Ok, CribbageGame.ApplyAction(state, me, new CribbageAction.Go(), rng));
            Assert.True(state.Log.Events[^1].Text.EndsWith("go.", StringComparison.Ordinal)
                        || state.Log.Events.Exists(e => e.Text.Contains(" go.")));
            // A go itself scores nobody anything; only a mutual go pays the point.
            var after = state.Players.Select(p => p.Score).ToList();
            Assert.True(after[0] + after[1] >= scoreBefore[0] + scoreBefore[1]);
        }
        Assert.True(found);
    }

    [Fact]
    public void TheCount()
    {
        var rng = new RandomSource(9);
        var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Normal, 121));
        Assert.True(Advance(state, rng, s => s.Phase == CribbagePhase.Count));
        var dealer = state.Dealer!.Value;
        Assert.True(state.Turn == 1 - dealer, "the non-dealer counts first");
        Assert.Equal(0, state.CountStage);
        Refuse(state, rng, dealer, new CribbageAction.Next(), "it is not your count");
        Refuse(state, rng, 1 - dealer, new CribbageAction.Play(state.Players[1 - dealer].Kept[0]), "it is not the play");
        Refuse(state, rng, 1 - dealer, new CribbageAction.NextHand(), "the hand is not over");
        Assert.Empty(CribbageGame.LegalPlays(state, 1 - dealer));
        Assert.Equal(ActionResult.Ok, CribbageGame.ApplyAction(state, 1 - dealer, new CribbageAction.Next(), rng));
        if (state.Phase == CribbagePhase.Count)
        {
            Assert.Equal(dealer, state.Turn);
            Assert.Equal(1, state.CountStage);
            Refuse(state, rng, 1 - dealer, new CribbageAction.Next(), "it is not your count");
            Assert.Equal(ActionResult.Ok, CribbageGame.ApplyAction(state, dealer, new CribbageAction.Next(), rng));
        }
        if (state.Phase == CribbagePhase.Count)
        {
            Assert.Equal(2, state.CountStage);
            Assert.Equal(ActionResult.Ok, CribbageGame.ApplyAction(state, dealer, new CribbageAction.Next(), rng));
        }
        Assert.True(state.Phase is CribbagePhase.RoundOver or CribbagePhase.GameOver);
        Assert.True(CribbageGame.CanDeal(state));
        Assert.Null(CribbageGame.SeatToAct(state));
        Refuse(state, rng, 0, new CribbageAction.Next(), "there is nothing to count");
        Refuse(state, rng, 0, new CribbageAction.Play(C("AS")), "it is not the play");
        if (state.Phase == CribbagePhase.RoundOver)
        {
            Refuse(state, rng, 0, new CribbageAction.NewGame(), "the game is not over");
            Assert.Single(state.History);
            Assert.True(state.Dealer == 1 - dealer, "the deal passes");
        }
    }

    [Fact]
    public void ABrokenStateFaults()
    {
        var rng = new RandomSource(1);
        var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Normal, 121));
        state.Players.RemoveAt(state.Players.Count - 1);
        var r = CribbageGame.ApplyAction(state, 0, new CribbageAction.Start(), rng);
        Assert.True(r.IsFault);
        Assert.False(r.IsOk);
    }

    /// <summary>Not in the Swift: C# can be handed a null where Swift's enum cannot.</summary>
    [Fact]
    public void ANullActionOrANullDiscardIsRefusedNotThrown()
    {
        var rng = new RandomSource(3);
        var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Normal, 121));
        Refuse(state, rng, 0, null!, "that is not a cribbage action");
        Assert.True(Advance(state, rng, s => s.Phase == CribbagePhase.Discard && s.SeatsOutstanding.Count == 2));
        Refuse(state, rng, 0, new CribbageAction.Discard(null!), "choose exactly two cards");
    }
}
