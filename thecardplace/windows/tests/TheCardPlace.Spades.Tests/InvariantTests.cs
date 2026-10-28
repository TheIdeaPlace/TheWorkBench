using static TheCardPlace.Spades.Tests.T;

namespace TheCardPlace.Spades.Tests;

/// <summary>
/// A few hundred complete hands played by the computer on every difficulty with seeded
/// randomness: never an illegal play, every card accounted for once, tricks total thirteen, the
/// score is the sum of the hands, and the game always reaches an end.
/// </summary>
public class InvariantTests
{
    [Fact]
    public void Hundreds_of_hands_on_every_difficulty()
    {
        var hands = 0;
        var plays = 0;
        for (var d = 0; d < DifficultyExtensions.All.Length; d++)
        {
            var difficulty = DifficultyExtensions.All[d];
            for (var g = 0; g < 12; g++)
            {
                var config = Config(difficulty, g % 2 == 0 ? 500 : 250);
                var (state, rng) = Drive.NewGame(config, (ulong)(1000 * (d + 1) + g));
                var guardCount = 0;
                var handsThisGame = 0;
                while (state.Phase != SpadesPhase.GameOver && guardCount < 40_000)
                {
                    guardCount++;
                    var phaseBefore = state.Phase;
                    if (state.Phase == SpadesPhase.Play)
                    {
                        var seat = state.Turn;
                        var legal = SpadesGame.LegalPlays(state, seat);
                        Assert.True(legal.Count > 0, "no legal play");
                        var action = SpadesAI.Decide(state, seat, rng);
                        var play = Assert.IsType<SpadesAction.Play>(action);
                        Assert.True(legal.Contains(play.Card), $"illegal play {play.Card.Id} chosen");
                        Assert.Null(SpadesGame.WhyNot(state, seat, play.Card));
                        var r = SpadesGame.ApplyAction(state, seat, play, rng);
                        Assert.True(r.IsOk, r.Reason);
                        plays++;

                        // Every card is somewhere exactly once: in a hand, on the table, or
                        // already played this hand.
                        var all = state.Players.SelectMany(p => p.Hand).Concat(state.PlayedThisHand).ToList();
                        Assert.True(all.Count == 52, "cards went missing or doubled");
                        Assert.True(all.Distinct().Count() == 52, "a card exists twice");
                        // The table is a suffix of what was played.
                        Assert.Equal(state.PlayedThisHand.TakeLast(state.Trick.Count), state.Trick.Select(t => t.Card));
                        Assert.Equal(state.TricksPlayed, state.Players.Sum(p => p.Tricks));
                    }
                    else
                    {
                        var r = Drive.Step(state, rng);
                        Assert.True(r?.IsOk ?? false, r?.Reason ?? "no step");
                    }
                    if (phaseBefore == SpadesPhase.Play && state.Phase is SpadesPhase.HandOver or SpadesPhase.GameOver)
                    {
                        handsThisGame++;
                        var h = state.History[^1];
                        Assert.Equal(13, h.Tricks.Sum());
                        Assert.Equal(52, state.PlayedThisHand.Count);
                        Assert.All(state.Players, p => Assert.Empty(p.Hand));
                        for (var t = 0; t < 2; t++)
                        {
                            Assert.Equal(state.History.Sum(x => x.Delta[t]), state.Scores[t]);
                            Assert.Equal(h.Bags[t], state.Bags[t]);
                            Assert.InRange(state.Bags[t], 0, SpadesGame.BagLimit(state) - 1);
                        }
                    }
                }
                Assert.True(state.Phase == SpadesPhase.GameOver, $"a game never finished ({difficulty}, game {g})");
                Assert.Equal(state.DealNumber, state.History.Count);
                Assert.Equal(state.DealNumber, handsThisGame);
                hands += handsThisGame;
            }
        }
        Assert.True(hands > 200, $"only {hands} hands were played");
        Assert.Equal(hands * 52, plays);
    }

    [Fact]
    public void The_deal_rotates_and_the_first_dealer_is_drawn()
    {
        var openers = new int[4];
        for (var g = 0; g < 40; g++)
        {
            var (state, rng) = Drive.NewGame((ulong)(500 + g));
            openers[state.Dealer!.Value]++;
            var last = state.Dealer.Value;
            var guardCount = 0;
            while (state.Phase != SpadesPhase.GameOver && guardCount < 40_000 && state.DealNumber < 4)
            {
                guardCount++;
                var before = state.DealNumber;
                Drive.Step(state, rng);
                if (state.DealNumber == before + 1)
                {
                    Assert.True((last + 1) % 4 == state.Dealer, "the deal did not rotate");
                    Assert.True((state.Dealer!.Value + 1) % 4 == state.Turn, "bidding does not start left of the dealer");
                    last = state.Dealer.Value;
                }
            }
        }
        Assert.True(openers.All(n => n > 0), $"the opening dealer is fixed: {string.Join(",", openers)}");
    }

    [Fact]
    public void Same_seed_replays_identically()
    {
        var a = Drive.PlayGame(Config(pointsToWin: 250), 31337);
        var b = Drive.PlayGame(Config(pointsToWin: 250), 31337);
        Assert.Equal(Json(a), Json(b));
        Assert.Equal(a.Log.Events.Select(e => e.Text), b.Log.Events.Select(e => e.Text));
        var c = Drive.PlayGame(Config(pointsToWin: 250), 31338);
        Assert.True(Json(a.History) != Json(c.History), "two seeds produced the same game");
    }

    [Fact]
    public void Difficulty_does_not_change_the_decision()
    {
        // ai.js never reads the difficulty; the port is faithful to that.
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var states = DifficultyExtensions.All.Select(d => Drive.ToPlay(Config(d), seed).State).ToList();
            foreach (var s in states.Skip(1))
            {
                Assert.Equal(states[0].Players.Select(p => p.Bid), s.Players.Select(p => p.Bid));
                var rng = new RandomSource(1);
                Assert.Equal(SpadesAI.Decide(states[0], states[0].Turn, rng), SpadesAI.Decide(s, s.Turn, rng));
            }
        }
    }

    [Fact]
    public void The_bidding_heuristics_on_literal_hands()
    {
        // No trump honours, three low spades, nothing above a queen with cover: nil-worthy.
        Assert.True(SpadesAI.NilWorthy(Cs("2S 5S 8S 3C 4C 7C 9C 2D 6D TD 3H 8H JH")));
        // A bare king is a trick you cannot avoid.
        Assert.False(SpadesAI.NilWorthy(Cs("2S 5S 8S KC 4C 7C 9C 2D 6D TD 3H 8H JH")));
        // Any spade above the nine says no.
        Assert.False(SpadesAI.NilWorthy(Cs("2S 5S TS 3C 4C 7C 9C 2D 6D TD 3H 8H JH")));
        // Ace, king of spades (2 + 2), five spades (two past the third: 4), ace of hearts (2), king
        // of clubs with cover (2), a void in diamonds with five trumps (2): fourteen halves, seven
        // tricks.
        Assert.Equal(7, SpadesAI.HandStrength(Cs("AS KS 7S 5S 2S AH 4H 3H KC 9C 2C 8H 6H")));
        // Nothing at all: three spades and no honours, and no void.
        Assert.Equal(0, SpadesAI.HandStrength(Cs("2C 3C 4C 5C 2D 3D 4D 2H 3H 4H 2S 3S 4S")));
    }

    [Fact]
    public void Choose_bid_shades_down_when_the_table_is_over_and_for_a_partner_on_nil()
    {
        var state = SpadesGame.CreateGame(Config());
        state.Phase = SpadesPhase.Bidding;
        state.Dealer = 3;
        state.Turn = 2;
        state.Players[2].Hand = Cs("AS KS 7S 5S 2S AH 4H 3H KC 9C 2C 8H 6H");   // worth seven
        Assert.Equal(7, SpadesAI.ChooseBid(state, 2));
        // Somebody has spoken and the table would be 8 + 7 = 15, over 14: shade by one.
        state.Players[0].Bid = 4;
        state.Players[1].Bid = 4;
        Assert.Equal(6, SpadesAI.ChooseBid(state, 2));
        // The partner is on nil: one less again.
        state.Players[0].Bid = 0;
        state.Players[1].Bid = 4;
        Assert.Equal(6, SpadesAI.ChooseBid(state, 2));
        state.Players[1].Bid = 8;
        Assert.Equal(5, SpadesAI.ChooseBid(state, 2));
    }
}
