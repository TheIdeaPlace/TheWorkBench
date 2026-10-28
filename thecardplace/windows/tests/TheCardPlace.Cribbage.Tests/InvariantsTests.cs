using System.Globalization;
using Xunit.Abstractions;
using static TheCardPlace.Cribbage.Tests.T;

namespace TheCardPlace.Cribbage.Tests;

public sealed class InvariantsTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryGameFinishesWithNothingRefused()
    {
        Assert.Equal(Sim.PerCombination * DifficultyExtensions.All.Length * CribbageConfig.Targets.Count, Sim.Games.Count);
        foreach (var g in Sim.Games)
        {
            Assert.True(g.Refusals.Count == 0, $"seed {g.Seed}: {string.Join("; ", g.Refusals)}");
            Assert.True(g.State.IsGameOver, $"seed {g.Seed} never finished in {g.Steps} steps");
            Assert.Equal(CribbagePhase.GameOver, g.State.Phase);
            Assert.NotNull(g.State.GameWinner);
            var w = g.State.GameWinner.Value;
            var loser = 1 - w;
            Assert.True(g.State.Players[w].Score >= g.Target);
            Assert.True(g.State.Players[loser].Score < g.Target);
            Assert.Equal(1, g.State.GamesWon[w]);
            Assert.Equal(0, g.State.GamesWon[loser]);
            Assert.NotEmpty(g.State.History);
            // The skunk line, from the literal thresholds.
            var theirs = g.State.Players[loser].Score;
            CribbageSkunk? expect = theirs < g.Target / 2 + 1
                ? (theirs < g.Target / 4 + 1 ? CribbageSkunk.DoubleSkunk : CribbageSkunk.Skunk)
                : null;
            Assert.Equal(expect, g.State.Result?.Skunk);
            // The deal passed after the last hand, however it ended.
            Assert.Equal(1 - g.State.History[^1].Dealer, g.State.Dealer);
        }
    }

    [Fact]
    public void EveryCardAccountedForOnce()
    {
        foreach (var h in Sim.Hands)
        {
            Assert.True(h.Problems.Count == 0, $"hand {h.HandNumber}: {string.Join("; ", h.Problems)}");
            Assert.NotNull(h.Dealt);
            var dealt = h.Dealt;
            Assert.Equal(2, dealt.Hands.Count);
            var all = dealt.Hands.SelectMany(x => x).ToList();
            if (h.Starter is { } s) all.Add(s);
            Assert.True(all.Distinct().Count() == all.Count, "a card was dealt twice");
            foreach (var hand in dealt.Hands) Assert.Equal(6, hand.Count);
            for (var i = 0; i < 2; i++)
            {
                if (h.Discarded[i] is not { } thrown) continue;
                Assert.True(h.Kept[i].Concat(thrown).ToHashSet().SetEquals(dealt.Hands[i]), "kept plus thrown is not what was dealt");
                Assert.Equal(4, h.Kept[i].Count);
            }
            if (h.Discarded[0] is { } a && h.Discarded[1] is { } b)
            {
                Assert.True(h.Crib.ToHashSet().SetEquals(a.Concat(b)));
                Assert.Equal(4, h.Crib.Count);
            }
            var played = h.Pile.Select(e => e.Card).ToList();
            Assert.True(played.Distinct().Count() == played.Count, "a card was played twice");
            foreach (var e in h.Pile) Assert.Contains(e.Card, h.Kept[e.Player]);
            foreach (var sc in h.Scores) Assert.True(sc >= 0);
        }
    }

    [Fact]
    public void ScoresOnlyRiseAndBreakdownsAddUp()
    {
        foreach (var g in Sim.Games)
        {
            var prev = new List<int> { 0, 0 };
            foreach (var h in g.State.History)
            {
                Assert.True(h.Scores[0] >= prev[0]);
                Assert.True(h.Scores[1] >= prev[1]);
                prev = h.Scores;
                foreach (var c in h.Counts)
                    Assert.Equal(c.Result.Total, c.Result.Items.Sum(i => i.Points));
                // The order of the count: non-dealer's hand, dealer's hand, crib.
                (int, CribbageCountKind)[] expected =
                    [(1 - h.Dealer, CribbageCountKind.Hand), (h.Dealer, CribbageCountKind.Hand), (h.Dealer, CribbageCountKind.Crib)];
                for (var i = 0; i < h.Counts.Count; i++)
                {
                    Assert.Equal(expected[i].Item1, h.Counts[i].Who);
                    Assert.Equal(expected[i].Item2, h.Counts[i].Kind);
                }
            }
            Assert.Equal(new[] { g.State.Players[0].Score, g.State.Players[1].Score }, prev);
        }
    }

    [Fact]
    public void AverageHandAndCribScores()
    {
        int handTotal = 0, handN = 0, cribTotal = 0, cribN = 0;
        foreach (var h in Sim.Hands)
        {
            foreach (var c in h.Counts)
            {
                if (c.Kind == CribbageCountKind.Hand) { handTotal += c.Result.Total; handN++; }
                else { cribTotal += c.Result.Total; cribN++; }
            }
        }
        var hand = (double)handTotal / Math.Max(1, handN);
        var crib = (double)cribTotal / Math.Max(1, cribN);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"cribbage invariants: {Sim.Hands.Count()} hands over {Sim.Games.Count} games; average hand {hand:F2} over {handN}, average crib {crib:F2} over {cribN}"));
        Assert.True(handN > 400);
        Assert.True(hand > 6.5, "the average hand is far below the published figure");
        Assert.True(hand < 9.5, "the average hand is far above the published figure");
        Assert.True(crib > 3.5);
        Assert.True(crib < 6.0);
    }

    [Fact]
    public void PrivateEventsHaveAnAudienceAndPublicOnesDoNot()
    {
        foreach (var g in Sim.Games.Take(6))
        {
            foreach (var e in g.State.Log.Events)
            {
                if (e.Text.StartsWith("Your six cards", StringComparison.Ordinal) || e.Text.StartsWith("You threw", StringComparison.Ordinal))
                {
                    Assert.True(e.Audience is not null, e.Text);
                    Assert.Equal(EventKind.You, e.Kind);
                }
                else
                {
                    Assert.True(e.Audience is null, e.Text);
                }
                Assert.NotEqual(EventKind.Error, e.Kind);
            }
            // Seat 1 never receives seat 0's private lines.
            Assert.DoesNotContain(g.State.Log.EventsFor(1), e => e.Audience == 0);
        }
    }

    [Fact]
    public void TheSameSeedReplaysIdentically()
    {
        var a = Sim.Play(77, Difficulty.Easy, 61);
        var b = Sim.Play(77, Difficulty.Easy, 61);
        Assert.Equal(Json(a.State), Json(b.State));
        var c = Sim.Play(78, Difficulty.Easy, 61);
        Assert.NotEqual(a.State.Log.Events.Select(e => e.Text), c.State.Log.Events.Select(e => e.Text));
    }

    [Fact]
    public void AnotherGameFollowsAGameOver()
    {
        var rng = new RandomSource(5);
        var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Normal, 61));
        Assert.True(Advance(state, rng, s => s.Phase == CribbagePhase.GameOver));
        var dealerAfter = state.Dealer;
        Assert.True(CribbageGame.CanDeal(state));
        Assert.Null(CribbageGame.SeatToAct(state));
        Assert.Equal(ActionResult.Ok, CribbageGame.ApplyAction(state, 0, new CribbageAction.NextHand(), rng));
        Assert.Equal(CribbagePhase.Discard, state.Phase);
        Assert.Equal(2, state.GameNumber);
        Assert.Equal(1, state.HandNumber);
        Assert.Equal(dealerAfter, state.Dealer);
        Assert.Equal(new[] { 0, 0 }, state.Players.Select(p => p.Score));
        Assert.Contains(state.Log.Events, e => e.Text.StartsWith("A new game. Both scores back to nothing, and", StringComparison.Ordinal));

        // And the other road: newGame back to idle, keeping what was won.
        Assert.True(Advance(state, rng, s => s.Phase == CribbagePhase.GameOver));
        var won = state.GamesWon.ToList();
        Assert.Equal(2, won.Sum());
        Assert.Equal(ActionResult.Ok, CribbageGame.ApplyAction(state, 0, new CribbageAction.NewGame(), rng));
        Assert.Equal(CribbagePhase.Idle, state.Phase);
        Assert.Null(state.Dealer);
        Assert.Equal(won, state.GamesWon);
        Assert.Equal(new[] { 0, 0 }, state.Players.Select(p => p.Score));
        Assert.Equal(new[] { "You", "Ruth" }, state.Config.Names);
        Assert.Equal(ActionResult.Ok, CribbageGame.ApplyAction(state, 0, new CribbageAction.Start(), rng));
        Assert.Equal(CribbagePhase.CutForDeal, state.Phase);
    }

    [Fact]
    public void TheCutDecidesTheDealerByRunOrder()
    {
        // Find a seed where the first cut is not a tie, and check the lower ORDER dealt: a ten
        // below a jack, not equal to it.
        int ties = 0, decided = 0;
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var rng = new RandomSource(seed);
            var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Hard, 121));
            CribbageGame.ApplyAction(state, 0, new CribbageAction.Start(), rng);
            Assert.Equal(ActionResult.Ok, CribbageGame.ApplyAction(state, 0, new CribbageAction.Cut(), rng));
            Assert.NotNull(state.CutForDeal);
            var cut = state.CutForDeal;
            if (cut.Tie)
            {
                ties++;
                Assert.Equal(CribbagePhase.CutForDeal, state.Phase);
                Assert.Equal(CribbageCards.Order(cut.Cuts[0]), CribbageCards.Order(cut.Cuts[1]));
                continue;
            }
            decided++;
            Assert.Equal(CribbagePhase.Discard, state.Phase);
            var lower = CribbageCards.Order(cut.Cuts[0]) < CribbageCards.Order(cut.Cuts[1]) ? 0 : 1;
            Assert.Equal(lower, state.Dealer);
            Assert.Equal(1, state.HandNumber);
            Assert.Equal(6, state.Players[0].Hand.Count);
            Assert.Equal(6, state.Players[1].Hand.Count);
            Assert.Equal(40, state.Deck.Count);
        }
        Assert.True(decided > 40);
        Assert.Equal(60, ties + decided);
    }
}
