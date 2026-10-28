using static TheCardPlace.Sheephead.Tests.Support;

namespace TheCardPlace.Sheephead.Tests;

public class InvariantTests
{
    /// <summary>
    /// An independent reading of the follow-suit rule, so the check is not the engine validating
    /// its own LegalPlays.
    /// </summary>
    private static string SuitKey(Card c) =>
        c.Rank == Rank.Queen || c.Rank == Rank.Jack || c.Suit == Suit.Diamonds ? "T" : c.Suit.Letter();

    private static void CheckHand(SheepheadState state, int n)
    {
        var d = SheepheadGame.DealSpec(n);
        Assert.Equal(SheepheadPhase.HandOver, state.Phase);
        Assert.Equal(d.Hand, state.Players.Sum(p => p.TricksWon));
        Assert.All(state.Players, p => Assert.Empty(p.Hand));
        Assert.Equal(n * d.Hand, state.Played.Count);
        Assert.Equal(state.Played.Count, state.Played.Distinct().Count());   // no duplicate card played
        var taken = state.Players.Sum(p => p.Points);
        var buried = SheepheadCards.SumPoints(state.Buried);
        Assert.Equal(120, taken + buried + SheepheadCards.SumPoints(state.Blind));
        var r = state.Result!;
        Assert.Equal(0, r.Deltas.Sum());   // scoring is zero-sum
        Assert.Empty(state.History.LastOrDefault()?.Problems ?? ["missing"]);
        if (!state.IsLeaster)
        {
            Assert.NotNull(state.Picker);
            Assert.Equal(d.Blind, state.Buried.Count);
            if (state.Alone) Assert.Null(state.Partner);
            else Assert.NotNull(state.Partner);
            Assert.True(state.PartnerRevealed);
            var stake = 2 * r.Multiplier * r.Factor;
            for (var s = 0; s < n; s++)
                if (SheepheadGame.Team(state, s) == SheepheadTeam.Opponent) Assert.Equal(stake, Math.Abs(r.Deltas[s]));
        }
        else
        {
            Assert.Empty(state.Blind);   // the leaster blind must go with the last trick
            Assert.Single(r.Winners);
            Assert.True(state.Players[r.Winners[0]].TricksWon > 0);
        }
    }

    public static TheoryData<int, SheepheadAllPass, Difficulty> Tables()
    {
        var data = new TheoryData<int, SheepheadAllPass, Difficulty>();
        for (var n = 3; n <= 6; n++)
            foreach (var allPass in Enum.GetValues<SheepheadAllPass>())
                foreach (var difficulty in DifficultyExtensions.All)
                    data.Add(n, allPass, difficulty);
        return data;
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void Complete_hands_on_every_table_size_and_difficulty(int n, SheepheadAllPass allPass, Difficulty difficulty)
    {
        for (var i = 0; i < 25; i++)
        {
            var (state, rng) = NewGame(n, (ulong)(n * 10_000 + i * 7 + (allPass == SheepheadAllPass.Leaster ? 0 : 1)),
                allPass, difficulty);
            PlayHand(state, rng, (st, seat, action, _) =>
            {
                if (action is not SheepheadAction.Play { Card: var c }) return;
                var legal = SheepheadGame.LegalPlays(st, seat);
                Assert.Contains(c, legal);   // the AI chose a legal card
                if (st.Trick.Count > 0)
                {
                    var led = SuitKey(st.Trick[0].Card);
                    if (st.Players[seat].Hand.Any(h => SuitKey(h) == led))
                        Assert.Equal(led, SuitKey(c));   // the AI followed suit
                }
            });
            CheckHand(state, n);
        }
    }

    [Fact]
    public void Leasters_happen_somewhere_in_the_sweep()
    {
        // Swift counts leasters across the whole sweep above; a theory splits it, so count here.
        var leasters = 0;
        for (var n = 3; n <= 6; n++)
        {
            foreach (var difficulty in DifficultyExtensions.All)
            {
                for (var i = 0; i < 25; i++)
                {
                    var (state, rng) = NewGame(n, (ulong)(n * 10_000 + i * 7), SheepheadAllPass.Leaster, difficulty);
                    PlayHand(state, rng);
                    if (state.IsLeaster) leasters++;
                }
            }
        }
        Assert.True(leasters > 0);
    }

    [Fact]
    public void Seeded_game_replays_identically()
    {
        static SheepheadState Run()
        {
            var (state, rng) = NewGame(5, 99, difficulty: Difficulty.Normal, doublers: true);
            for (var h = 0; h < 4; h++)
            {
                PlayHand(state, rng);
                Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.NextHand(), rng));
            }
            PlayHand(state, rng);
            return state;
        }
        var a = Run();
        var b = Run();
        Assert.Equal(J(a), J(b));
        Assert.Equal(a.Log.Events.Select(e => e.Text), b.Log.Events.Select(e => e.Text));
        Assert.Equal(5, a.HandNumber);
        Assert.Equal(5, a.History.Count);
    }

    [Fact]
    public void Deal_rotates_and_scores_accumulate()
    {
        var (state, rng) = NewGame(4, 5);
        var first = state.Dealer!.Value;
        var totals = new int[4];
        for (var h = 0; h < 6; h++)
        {
            Assert.Equal((first + h) % 4, state.Dealer);
            Assert.Equal((state.Dealer!.Value + 1) % 4, state.Leader);
            PlayHand(state, rng);
            for (var i = 0; i < 4; i++) totals[i] += state.Result!.Deltas[i];
            Assert.Equal(totals, state.Players.Select(p => p.Score));
            Assert.Equal(0, state.Players.Sum(p => p.Score));
            Assert.True(SheepheadGame.CanDeal(state));
            Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.NextHand(), rng));
            Assert.False(SheepheadGame.CanDeal(state));
        }
    }

    [Fact]
    public void Queen_doublers_multiply_the_hand()
    {
        var factors = new HashSet<int>();
        for (var i = 0; i < 150; i++)
        {
            var (state, rng) = NewGame(3, (ulong)(500 + i), doublers: true);
            PlayHand(state, rng);
            var r = state.Result!;
            var expected = 1 << state.Doublers.Count;
            Assert.Equal(expected, r.Factor);
            factors.Add(r.Factor);
            foreach (var dbl in state.Doublers)
            {
                var hand = state.Dealt!.Hands[dbl.Player!.Value];
                var pair = dbl.Kind == SheepheadDoublerKind.Black ? Cards("QC", "QS") : Cards("QH", "QD");
                // The pair was in that player's final hand: dealt, or picked up and not buried.
                var finalHand = state.Picker == dbl.Player
                    ? hand.Concat(state.Dealt.Blind).Where(c => !state.Buried.Contains(c)).ToList()
                    : hand;
                Assert.All(pair, c => Assert.Contains(c, finalHand));
                var told = state.Log.Events.Where(e => e.Audience == dbl.Player && e.Text.Contains(dbl.Text, StringComparison.Ordinal));
                Assert.Single(told);   // the holder is told once, privately
            }
            if (r.Factor > 1)
            {
                Assert.Contains("Doubled by", r.Summary, StringComparison.Ordinal);
                Assert.Contains($"worth {r.Factor} times", r.Summary, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("Doubled", r.Summary, StringComparison.Ordinal);
            }
        }
        Assert.Contains(2, factors);   // a doubled hand in 150 three-handed deals
        Assert.Contains(4, factors);   // and a quadrupled one
    }

    [Fact]
    public void Redeal_doubles_the_next_hand_only()
    {
        var (state, rng) = NewGame(4, 3, SheepheadAllPass.Redeal, doublers: true);
        state.Config.BlackQueenDoubler = false;
        state.Config.RedQueenDoubler = false;
        var hand = state.HandNumber;
        for (var k = 0; k < 4; k++)
        {
            var seat = SheepheadGame.SeatToAct(state)!.Value;
            Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, seat, new SheepheadAction.Pass(), rng));
        }
        Assert.Equal(hand + 1, state.HandNumber);
        Assert.Equal(SheepheadPhase.Pick, state.Phase);
        Assert.True(state.RedealDoubler);
        Assert.False(state.NextHandDoubler);
        Assert.Contains(state.Log.Events, e => e.Text == "Everyone passed. Redealing. The next hand is a doubler, worth twice as much.");
        // A second redeal does not stack.
        for (var k = 0; k < 4; k++)
        {
            var seat = SheepheadGame.SeatToAct(state)!.Value;
            Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, seat, new SheepheadAction.Pass(), rng));
        }
        Assert.Equal(2, SheepheadGame.DoublerFactor(state));
        PlayHand(state, rng);
        Assert.Equal(2, state.Result!.Factor);
        Assert.Contains("Doubled by the redeal, so the hand is worth 2 times.", state.Result.Summary, StringComparison.Ordinal);
        Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.NextHand(), rng));
        Assert.False(state.RedealDoubler);
    }

    [Fact]
    public void Redeal_without_the_doubler_rule()
    {
        var (state, rng) = NewGame(5, 8, SheepheadAllPass.Redeal);
        for (var k = 0; k < 5; k++)
        {
            var seat = SheepheadGame.SeatToAct(state)!.Value;
            Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, seat, new SheepheadAction.Pass(), rng));
        }
        Assert.Equal(2, state.HandNumber);
        Assert.False(state.RedealDoubler);
        Assert.Contains(state.Log.Events, e => e.Text == "Everyone passed. Redealing.");
    }

    [Fact]
    public void Leaster_blind_goes_with_the_last_trick()
    {
        var (state, rng) = NewGame(5, 11);
        var blind = state.Blind.ToList();
        for (var k = 0; k < 5; k++)
        {
            var seat = SheepheadGame.SeatToAct(state)!.Value;
            Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, seat, new SheepheadAction.Pass(), rng));
        }
        Assert.True(state.IsLeaster);
        Assert.Equal(SheepheadPhase.Play, state.Phase);
        PlayHand(state, rng);
        var last = state.TrickLog[^1];
        Assert.Equal(SheepheadCards.SumPoints(blind), last.FromBlind);
        Assert.StartsWith("Leaster result: ", state.Result!.Summary, StringComparison.Ordinal);
        Assert.Contains("takes the fewest points and wins, 4 points.", state.Result.Summary, StringComparison.Ordinal);
        if (last.FromBlind > 0)
            Assert.Contains(state.Log.Events, e => e.Text.Contains($"That includes {last.FromBlind} points from the blind.", StringComparison.Ordinal));
    }

    [Fact]
    public void Three_players_picker_is_always_alone()
    {
        for (var i = 0; i < 20; i++)
        {
            var (state, rng) = NewGame(3, (ulong)(70 + i));
            PlayHand(state, rng);
            if (state.IsLeaster) continue;
            Assert.True(state.Alone);
            Assert.Null(state.Partner);
            var p = state.Picker!.Value;
            Assert.Contains(state.Log.Events, e =>
                e.Text == $"{state.Players[p].Name} is the picker and plays alone." || e.Text == "You are the picker and play alone.");
        }
    }
}
