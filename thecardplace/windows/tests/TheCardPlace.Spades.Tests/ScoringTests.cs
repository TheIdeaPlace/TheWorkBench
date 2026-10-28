namespace TheCardPlace.Spades.Tests;

/// <summary>
/// The scoring against worked examples with the arithmetic written out by hand: the port of
/// <c>spades/tests/scoring.js</c>. Seats are 0..3; teams are 0 (seats 0 and 2) and 1 (seats 1 and 3).
/// </summary>
public class ScoringTests
{
    private sealed record Row(string Name, int[] Bids, int[] Tricks, int[] BagsIn, int[] Delta, int[] Bags, string Why,
                              int BagLimit = 10, int BagPenalty = 100, int NilValue = 100);

    private static readonly Row[] Rows =
    [
        new("the ordinary made contract",
            [3, 4, 2, 4], [3, 4, 2, 4], [0, 0], [50, 80], [0, 0],
            "team 0 bid 5 took 5 = +50; team 1 bid 8 took 8 = +80"),
        new("overtricks are worth one point and one bag each",
            [3, 3, 2, 3], [4, 3, 3, 3], [0, 0], [52, 60], [2, 0],
            "team 0 bid 5 took 7 = 50 + 2 overtricks; two bags banked"),
        new("missing by one loses the whole bid, not the difference",
            [4, 3, 3, 3], [3, 4, 3, 3], [0, 0], [-70, 61], [0, 1],
            "team 0 bid 7 took 6 — minus ten a trick for all seven, not for the one; team 1 bid 6 took 7 = 60 + 1 overtrick, and banks the bag that came with it"),
        new("a set hand banks no bags, because there was no contract to be over",
            [6, 2, 5, 0], [2, 5, 3, 3], [4, 0], [-110, -74], [4, 6],
            "team 0 bid 11 took 5 = -110 and its bags are untouched; team 1 bid 2 took 8 = 20 + 6 overtricks = 26, then the nil at seat 3 went down on 3 = -100, so -74, and it banks all six bags"),
        new("the tenth bag costs a hundred and the count carries the remainder",
            [3, 4, 3, 3], [5, 4, 4, 0], [7, 0], [-37, -70], [0, 0],
            "team 0 bid 6 took 9 = 60 + 3 overtricks = 63, but 7 + 3 fills the bin: 63 - 100 = -37, and 10 - 10 leaves 0. Team 1 bid 7 and took 4, so it is set"),
        new("a hand can fill the bin and leave bags over",
            [2, 5, 2, 4], [5, 5, 3, 0], [8, 0], [-56, -90], [2, 0],
            "team 0 bid 4 took 8 = 40 + 4 = 44; 8 + 4 = 12 bags fills one bin, 44 - 100 = -56, and 12 - 10 leaves 2"),
        new("a nil that comes in is a hundred, on top of the partner's contract",
            [0, 4, 4, 4], [0, 4, 5, 4], [0, 0], [141, 80], [1, 0],
            "team 0 contract is 0 + 4 = 4, took 5 = 40 + 1 over = 41, plus 100 for the nil"),
        new("a nil that goes down is minus a hundred, and its tricks still count",
            [0, 4, 4, 4], [2, 4, 3, 4], [0, 0], [-59, 80], [1, 0],
            "team 0 contract 4, took 2 + 3 = 5 — the broken nil's two tricks count, so the contract is MADE: 40 + 1 over = 41, then -100 for the nil = -59"),
        new("a broken nil can carry the contract home on its own tricks",
            [0, 5, 3, 5], [3, 5, 0, 5], [0, 0], [-70, 100], [0, 0],
            "team 0 contract 3, took 3 + 0 = 3 — made exactly, +30, then -100 = -70"),
        new("both partners nil, both in",
            [0, 7, 0, 6], [0, 7, 0, 6], [0, 0], [200, 130], [0, 0],
            "a contract of zero is made by definition — 10 x 0 = 0 — and the two nils are worth a hundred each"),
        new("both partners nil, one down",
            [0, 6, 0, 5], [0, 6, 2, 5], [0, 0], [2, 110], [2, 0],
            "contract 0, took 2, so it is made with two overtricks = +2; one nil in (+100) and one down (-100) cancel"),
        new("thirteen tricks, one partnership, everything",
            [6, 0, 7, 0], [6, 0, 7, 0], [0, 0], [130, 200], [0, 0],
            "team 0 bid 13 took 13 = +130; team 1 both nil and both in = +200"),
        new("a zero-bag-penalty table counts bags and does not punish them",
            [2, 3, 2, 3], [5, 3, 2, 3], [9, 0], [43, 60], [2, 0],
            "bid 4 took 7 = 40 + 3 = 43; 9 + 3 = 12 crosses the limit but costs nothing, and the remainder is still 2",
            BagLimit: 10, BagPenalty: 0, NilValue: 100),
        new("a five-bag table fills its bin twice as often",
            [2, 3, 2, 3], [4, 3, 3, 3], [3, 0], [-7, 60], [1, 0],
            "bid 4 took 7 = 40 + 3 = 43; 3 + 3 = 6 fills a bin of five, so 43 - 50 = -7, and 6 - 5 leaves one bag behind rather than none",
            BagLimit: 5, BagPenalty: 50, NilValue: 100),
    ];

    [Fact]
    public void Worked_examples()
    {
        Assert.Equal(14, Rows.Length);
        foreach (var row in Rows)
        {
            Assert.True(row.Tricks.Sum() == 13, $"{row.Name}: the case itself deals the wrong number of tricks");
            var r = SpadesGame.ScoreHand(row.Bids, row.Tricks, row.BagsIn, row.BagLimit, row.BagPenalty, row.NilValue);
            Assert.True(row.Delta.SequenceEqual(r.Delta), $"{row.Name}: {row.Why} (got {string.Join(",", r.Delta)})");
            Assert.True(row.Bags.SequenceEqual(r.Bags), $"{row.Name}: bags — {row.Why} (got {string.Join(",", r.Bags)})");
        }
    }

    [Fact]
    public void Score_hand_is_pure_and_repeatable()
    {
        int[] bagsIn = [7, 3];
        int[] bids = [3, 3, 3, 3];
        var a = SpadesGame.ScoreHand(bids, [4, 3, 3, 3], bagsIn, 10, 100, 100);
        var b = SpadesGame.ScoreHand(bids, [4, 3, 3, 3], bagsIn, 10, 100, 100);
        Assert.Equal(T.Json(a), T.Json(b));
        Assert.Equal(new[] { 7, 3 }, bagsIn);
        Assert.Equal(new[] { 8, 3 }, a.Bags);
        Assert.Equal(new[] { 61, 60 }, a.Delta);
        Assert.Equal(1, a.Detail[0].Overtricks);
        Assert.Equal(60, a.Detail[0].Base);
        Assert.True(a.Detail[0].Made);
    }

    /// <summary>
    /// Every split of thirteen tricks against every plausible contract: peel the nils and the bag
    /// penalty off the total and what is left must be exactly the contract.
    /// </summary>
    [Fact]
    public void Every_bid_and_trick_combination_has_the_right_shape()
    {
        var cases = 0;
        for (var b0 = 0; b0 <= 7; b0++)
        for (var b1 = 0; b1 <= 7; b1++)
        for (var t0 = 0; t0 <= 13; t0++)
        {
            int[] bids = [b0, b1, 0, 0];
            int[] tricks = [t0, 13 - t0, 0, 0];
            var r = SpadesGame.ScoreHand(bids, tricks, [0, 0], 10, 100, 100);
            cases++;
            for (var team = 0; team < 2; team++)
            {
                var contract = bids[team] + bids[team + 2];
                var took = tricks[team] + tricks[team + 2];
                var nilSeats = new[] { team, team + 2 }.Where(s => bids[s] == 0).ToList();
                var nilsMade = nilSeats.Count(s => tricks[s] == 0);
                var nilPart = nilsMade * 100 - (nilSeats.Count - nilsMade) * 100;
                var over = took >= contract ? took - contract : 0;
                var penalty = over / 10 * 100;
                var rest = r.Delta[team] - nilPart + penalty;
                if (took >= contract)
                {
                    Assert.True(10 * contract + over == rest, $"made: bid {contract} took {took}");
                    Assert.Equal(over % 10, r.Bags[team]);
                }
                else
                {
                    Assert.True(-10 * contract == rest, $"set: bid {contract} took {took}");
                    Assert.True(r.Bags[team] == 0, "a set hand banked bags");
                }
            }
        }
        Assert.Equal(8 * 8 * 14, cases);
    }
}
