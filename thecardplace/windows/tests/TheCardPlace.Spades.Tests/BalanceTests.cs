using System.Globalization;

namespace TheCardPlace.Spades.Tests;

/// <summary>
/// Is this a game, or is it four bots agreeing with each other? The port of
/// <c>spades/tests/balance.js</c>: the same measured bands, on two seeds, so a drift in the
/// bidding is caught rather than a crash.
/// </summary>
public class BalanceTests
{
    private sealed class Stats
    {
        public int Games, Hands, TableBid, Set, Made, BagPens, Nils, NilsMade, Blowouts;
        public int[] Wins = new int[2];
        public HashSet<int> BidCounts = [];
        public int[] Openers = new int[4];
    }

    private static Stats Run(ulong seed, int games)
    {
        var s = new Stats();
        for (var g = 0; g < games; g++)
        {
            var (state, rng) = Drive.NewGame(unchecked(seed + (ulong)g * 7919));
            s.Openers[state.Dealer!.Value]++;
            var guardCount = 0;
            while (state.Phase != SpadesPhase.GameOver && guardCount < 40_000)
            {
                guardCount++;
                var r = Drive.Step(state, rng);
                Assert.True(r is { IsOk: true }, "a move was refused");
            }
            Assert.True(state.Phase == SpadesPhase.GameOver, "a game never finished");

            s.Games++;
            s.Hands += state.DealNumber;
            s.Wins[state.Winner!.Value]++;
            if (Math.Abs(state.Scores[0] - state.Scores[1]) > 400) s.Blowouts++;

            var prevBags = new List<int> { 0, 0 };
            foreach (var h in state.History)
            {
                s.TableBid += h.Bids.Sum();
                for (var i = 0; i < h.Bids.Count; i++)
                {
                    var b = h.Bids[i];
                    s.BidCounts.Add(b);
                    if (b == 0)
                    {
                        s.Nils++;
                        if (h.Tricks[i] == 0) s.NilsMade++;
                    }
                }
                for (var t = 0; t < 2; t++)
                {
                    var bid = h.Bids[t] + h.Bids[t + 2];
                    var took = h.Tricks[t] + h.Tricks[t + 2];
                    if (took >= bid) s.Made++; else s.Set++;
                    if (h.Bags[t] < prevBags[t]) s.BagPens++;
                }
                prevBags = h.Bags;
            }
        }
        return s;
    }

    private static void Band(string name, double value, double lo, double hi, string why) =>
        Assert.True(value >= lo && value <= hi,
            string.Create(CultureInfo.InvariantCulture, $"{name} is {Math.Round(value * 100) / 100}, outside {lo}–{hi}. {why}"));

    [Theory]
    [InlineData(20260825UL)]
    [InlineData(991UL)]
    public void The_bidding_is_a_decision_and_the_contracts_are_at_risk(ulong seed)
    {
        var s = Run(seed, 120);
        Assert.True(s.Games > 0);
        double games = s.Games, hands = s.Hands;

        Band("table bid (of 13)", s.TableBid / hands, 11.0, 12.8,
            "Under 11 the bots leave tricks on the table and win on bag penalties; over 12.8 they bid more than exists.");
        Band("contracts set (%)", 100.0 * s.Set / (s.Set + s.Made), 10, 30,
            "This is the tension in the game.");
        Band("hands per game", hands / games, 7, 16,
            "Too few and the game is over before it starts; too many and it outstays its welcome.");
        Band("bag penalties per game", s.BagPens / games, 0.15, 2.0,
            "Zero means the bag rule is decoration; more than two a game means the game is decided by sandbagging.");
        Band("hands with a nil (%)", 100.0 * s.Nils / hands, 2, 25,
            "Never bidding nil wastes a rule; bidding it constantly means nilWorthy is not strict enough.");
        if (s.Nils > 0)
            Band("nils made (%)", 100.0 * s.NilsMade / s.Nils, 70, 100, "nilWorthy is deliberately strict.");
        Band("seats 1+3 win rate (%)", 100.0 * s.Wins[1] / games, 33, 67,
            "The two sides run identical code; a lean means something depends on seat order.");
        Band("rarest opening dealer (%)", 100.0 * s.Openers.Min() / games, 8, 25,
            $"Seats {string.Join(",", s.Openers)} dealt the opening hand; a seat that never does means the first dealer is fixed.");
        Band("distinct bids used", s.BidCounts.Count, 5, 14,
            "If the bots only ever bid two or three, the count is not reading the hand.");
        Band("blowouts (%)", 100.0 * s.Blowouts / games, 0, 45,
            "A game where one side is never in it. Some are expected; mostly is not.");
    }
}
