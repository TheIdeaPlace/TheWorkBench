using static TheCardPlace.Hearts.Tests.Support;

namespace TheCardPlace.Hearts.Tests;

/// Whole games played by the AI on every difficulty with seeded randomness:
/// never an illegal play, every card accounted for once, points total what
/// they must, scoring is what the table says, the game always ends, and the
/// same seed replays identically.
public class InvariantsTests
{
    /// Swift loops over the three difficulties in one test of ninety games; here each
    /// difficulty is its own case of thirty, and the hand count is a third of Swift's 200.
    [Theory]
    [InlineData(Difficulty.Easy)]
    [InlineData(Difficulty.Normal)]
    [InlineData(Difficulty.Hard)]
    public void Many_games_on_every_difficulty_keep_every_invariant(Difficulty difficulty)
    {
        int hands = 0, games = 0;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var lastHandChecked = 0;
            var s0 = seed;
            var end = PlayGame(unchecked(seed * 7919 + (ulong)difficulty.ToString().ToLowerInvariant().Length), Config(difficulty),
                inspect: s =>
                {
                    // Every one of the fifty-two cards is in exactly one place.
                    var all = AllCards(s);
                    Assert.True(all.Count == 52, $"seed {s0}: the pack has {all.Count} cards");
                    Assert.True(all.Distinct().Count() == all.Count, $"seed {s0}: a card is in two places");
                    if (s.Phase == HeartsPhase.Play)
                    {
                        Assert.True(s.HeartsBroken == HeartEverPlayed(s), $"seed {s0}: heartsBroken drifted");
                        Assert.True(HeartsGame.LegalPlays(s, s.Turn).Count > 0, $"seed {s0}: no legal play on turn");
                        Assert.Equal(s.Turn, HeartsGame.SeatToAct(s));
                    }
                    if (s.Phase == HeartsPhase.Passing)
                    {
                        Assert.All(s.Players, p => Assert.Equal(13, p.Hand.Count));
                        Assert.Equal(FirstUnpassed(s), HeartsGame.SeatToAct(s));
                    }
                    if (s.Phase == HeartsPhase.HandOver && s.DealNumber != lastHandChecked)
                    {
                        lastHandChecked = s.DealNumber;
                        hands++;
                        CheckFinishedHand(s, s0);
                        Assert.True(HeartsGame.CanDeal(s));
                    }
                    else
                    {
                        Assert.False(HeartsGame.CanDeal(s));
                    }
                },
                afterMove: (s, seat, action) =>
                {
                    if (action is HeartsAction.Play p)
                    {
                        // The card is on the table or in a pile, and never back in the hand.
                        Assert.DoesNotContain(p.Card, s.Players[seat].Hand);
                    }
                });
            games++;
            CheckFinishedHand(end, seed);
            Assert.Equal(HeartsPhase.GameOver, end.Phase);
            var target = HeartsGame.Target(end);
            Assert.Contains(end.Players, p => p.Score >= target);
            foreach (var h in end.History.SkipLast(1)) Assert.All(h.Scores, x => Assert.True(x < target));
            Assert.Contains(end.Log.Events, e => e.Kind == EventKind.Game);          // the end of the game is announced
            Assert.DoesNotContain(end.Log.Events, e => e.Kind == EventKind.Error);   // refusals are not events
            Assert.Null(HeartsGame.SeatToAct(end));
        }
        Assert.Equal(30, games);
        Assert.True(hands > 200 / 3, $"only {hands} hands");
    }

    internal static void CheckFinishedHand(HeartsState s, ulong seed)
    {
        var raw = s.Players.Select(p => HeartsGame.PointsOf(p.Taken)).ToList();
        Assert.True(raw.Sum() == 26, $"seed {seed}: a finished hand is worth {raw.Sum()}");
        Assert.True(s.Players.Sum(p => p.Taken.Count) == 52, $"seed {seed}");
        Assert.All(s.Players, p => Assert.Empty(p.Hand));
        Assert.True(s.Players.Count(p => p.HasQueen) == 1, $"seed {seed}: the queen ended in the wrong number of piles");
        Assert.Equal(13, s.TricksPlayed);
        Assert.True(s.History.Count > 0, $"seed {seed}: no history row");
        var last = s.History[^1];
        Assert.Equal(s.DealNumber, last.Deal);
        Assert.Equal(s.PassDirection, last.PassDirection);
        var shot = raw.IndexOf(26);
        if (shot >= 0)
        {
            Assert.Equal(shot, last.Shooter);
            Assert.Equal(Enumerable.Range(0, 4).Select(i => i == shot ? 0 : 26), last.Points);
            Assert.Contains(s.Log.Events, e => e.Kind == EventKind.Moon && e.Seat == shot);
        }
        else
        {
            Assert.Null(last.Shooter);
            Assert.Equal(raw, last.Points);
        }
        Assert.Equal(last.Points, s.Players.Select(p => p.HandPoints));
        var sums = new[] { 0, 0, 0, 0 };
        foreach (var h in s.History) for (var i = 0; i < 4; i++) sums[i] += h.Points[i];
        Assert.Equal(sums, s.Players.Select(p => p.Score));   // scores are the sum of the hands
        Assert.Equal(last.Scores, s.Players.Select(p => p.Score));
    }

    [Fact]
    public void The_same_seed_replays_identically()
    {
        foreach (var seed in new ulong[] { 5, 99, 4242 })
        {
            var a = PlayGame(seed, Config(Difficulty.Normal));
            var b = PlayGame(seed, Config(Difficulty.Normal));
            Assert.Equal(Snapshot.Json(a), Snapshot.Json(b));
            Assert.Equal(a.Log.Events.Select(e => e.Text), b.Log.Events.Select(e => e.Text));
        }
        var c = PlayGame(5, Config(Difficulty.Normal));
        var d = PlayGame(6, Config(Difficulty.Normal));
        Assert.NotEqual(c.Log.Events.Select(e => e.Text), d.Log.Events.Select(e => e.Text));   // different seeds should differ
    }

    [Fact]
    public void Can_deal_is_exactly_when_next_hand_is_accepted()
    {
        var rng = new RandomSource(31);
        var s = HeartsGame.CreateGame(Config(pointsToWin: 50));
        var phases = new HashSet<HeartsPhase>();
        var steps = 0;
        while (steps < 4000)
        {
            steps++;
            phases.Add(s.Phase);
            // Swift copies the generator for the probe; C#'s cannot be copied, so the probe has
            // its own. Either way the game's generator is not advanced by asking.
            var copy = Snapshot.Clone(s);
            var probeRng = new RandomSource((ulong)steps);
            var accepted = HeartsGame.ApplyAction(copy, 0, new HeartsAction.NextHand(), probeRng).IsOk;
            Assert.True(HeartsGame.CanDeal(s) == accepted,
                $"phase {s.Phase}: canDeal says {HeartsGame.CanDeal(s)} but nextHand {(accepted ? "is accepted" : "is refused")}");
            if (s.Phase == HeartsPhase.GameOver) break;
            switch (s.Phase)
            {
                case HeartsPhase.Idle:
                    Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Start(), rng).IsOk);
                    break;
                case HeartsPhase.Passing or HeartsPhase.Play:
                    var seat = HeartsGame.SeatToAct(s)!.Value;
                    var action = HeartsAI.Decide(s, seat, rng)!;
                    Assert.True(HeartsGame.ApplyAction(s, seat, action, rng).IsOk);
                    break;
                case HeartsPhase.HandOver:
                    Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.NextHand(), rng).IsOk);
                    break;
            }
        }
        Assert.Equal(
            [HeartsPhase.Idle, HeartsPhase.Passing, HeartsPhase.Play, HeartsPhase.HandOver, HeartsPhase.GameOver],
            phases.Order());
    }

    [Fact]
    public void New_game_goes_back_to_idle_with_scores_reset_and_config_kept()
    {
        var rng = new RandomSource(8);
        var cfg = new HeartsConfig { Names = ["Ruth", "East", "South", "West"], Difficulty = Difficulty.Easy, PointsToWin = 50 };
        var s = PlayGame(8, cfg);
        Assert.Equal(HeartsPhase.GameOver, s.Phase);
        Assert.Contains(s.Players, p => p.Score > 0);
        var r = HeartsGame.ApplyAction(s, 0, new HeartsAction.NewGame(), rng);
        Assert.True(r.IsOk);
        Assert.Equal(HeartsPhase.Idle, s.Phase);
        Assert.Equal(Snapshot.Json(cfg), Snapshot.Json(s.Config));
        Assert.Equal([0, 0, 0, 0], s.Players.Select(p => p.Score));
        Assert.Equal(cfg.Names, s.Players.Select(p => p.Name));
        Assert.Equal(0, s.DealNumber);
        Assert.Empty(s.History);
        Assert.Null(s.Winner);
        Assert.Equal("New game. Every score is back to nothing.", s.Log.Events[^1].Text);
        Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Start(), rng).IsOk);
        Assert.Equal(1, s.DealNumber);
        Assert.Equal(HeartsPassDirection.Left, s.PassDirection);
    }

    [Fact]
    public void Events_say_what_happened()
    {
        var rng = new RandomSource(21);
        var s = HeartsGame.CreateGame(Config());
        Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Start(), rng).IsOk);
        Assert.Equal(["Hand 1 dealt.", "Pass three cards to the left."], s.Log.Events.Select(e => e.Text));
        Assert.Equal(EventKind.Deal, s.Log.Events[0].Kind);

        var mine = s.Players[0].Hand.Take(3).ToList();
        Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Pass(mine), rng).IsOk);
        var you = s.Log.Events.Last(e => e.Kind == EventKind.You);
        Assert.Equal($"You passed {mine.SpokenList()}.", you.Text);
        Assert.Equal(0, you.Audience);
        Assert.Equal<Card>(mine, you.Cards);
        Assert.Equal("North has passed.", s.Log.Events[^1].Text);
        Assert.Null(s.Log.Events[^1].Audience);
        // Nobody but seat 0 hears what seat 0 passed.
        Assert.DoesNotContain(s.Log.EventsFor(1), e => e.Text.StartsWith("You passed", StringComparison.Ordinal));

        for (var seat = 1; seat <= 3; seat++)
        {
            var a = HeartsAI.Decide(s, seat, rng)!;
            Assert.True(HeartsGame.ApplyAction(s, seat, a, rng).IsOk);
        }
        Assert.Equal(HeartsPhase.Play, s.Phase);
        var got = s.Log.Events.First(e => e.Audience == 1 && e.Text.Contains("passed you", StringComparison.Ordinal));
        Assert.Equal($"North passed you {mine.SpokenList()}.", got.Text);
        Assert.Equal<Card>(mine, s.Received[1]!);
        var leader = HeartsGame.HolderOfTwoOfClubs(s);
        Assert.Equal($"{Names[leader]} has the two of clubs and leads.", s.Log.Events[^1].Text);

        Card? lastPlay = null;
        while (s.TricksPlayed == 0)
        {
            var seat = s.Turn;
            var c = HeartsAI.ChooseCard(s, seat)!.Value;
            lastPlay = c;
            Assert.True(HeartsGame.ApplyAction(s, seat, new HeartsAction.Play(c), rng).IsOk);
            if (s.TricksPlayed == 0)
            {
                Assert.Equal($"{Names[seat]} played the {c.Name}.", s.Log.Events[^1].Text);
                Assert.Equal<Card>([c], s.Log.Events[^1].Cards);
                Assert.Equal(seat, s.Log.Events[^1].Seat);
            }
        }
        Assert.NotNull(lastPlay);
        var trick = s.Log.Events[^1];
        Assert.Equal(EventKind.Trick, trick.Kind);
        Assert.Equal($"{Names[s.LastTrick!.Winner]} took the trick, no points.", trick.Text);
        Assert.Equal(4, trick.Cards.Count);
    }

    [Fact]
    public void Hand_over_and_game_events_wording()
    {
        var end = PlayGame(77, Config(pointsToWin: 50));
        var handEvents = end.Log.Events.Where(e => e.Kind == EventKind.Hand).ToList();
        Assert.Equal(end.History.Count, handEvents.Count);
        var h = end.History[0];
        Assert.Equal($"Hand 1 over. This hand, North {h.Points[0]}, East {h.Points[1]}, South {h.Points[2]} and West {h.Points[3]}. "
            + $"Scores, North {h.Scores[0]}, East {h.Scores[1]}, South {h.Scores[2]} and West {h.Scores[3]}.", handEvents[0].Text);
        var game = end.Log.Events.Last(e => e.Kind == EventKind.Game);
        if (end.Winner is { } w)
            Assert.Equal($"{Names[w]} wins with {end.Players[w].Score}. Lowest score wins.", game.Text);
        else
            Assert.StartsWith("Tied on ", game.Text);
    }
}
