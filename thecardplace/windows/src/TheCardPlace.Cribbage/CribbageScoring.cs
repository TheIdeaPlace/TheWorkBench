using System.Numerics;
using System.Text.Json.Serialization;
using TheCardPlace.Core;

namespace TheCardPlace.Cribbage;

public enum ScoreItemKind { Fifteen, ThirtyOne, Pair, Run, Flush, Nob }

/// <summary>
/// One part of a count, in the words a cribbage player uses: "two fifteens for four", "a pair of
/// fives for two", "a run of three for three".
/// </summary>
/// <param name="Label">The words: "fifteen for two", "three fives for six", "one for his nob".</param>
/// <param name="Count">
/// How many: fifteens found, cards of the same rank, or runs of the longest length. One for a
/// flush or a nob.
/// </param>
public sealed record ScoreItem(ScoreItemKind Kind, string Label, int Points, int Count = 1);

/// <summary>
/// A score as its parts, not as a number.
///
/// "You scored eight" is a fact a sighted player can check against the cards in front of them in
/// about a second. Read out on its own it is something you either trust or do not, and cribbage is
/// a game where the whole pleasure is in the counting. So every scoring function returns the parts
/// as well as the total, and the interface reads them out.
/// </summary>
public sealed record ScoreBreakdown(List<ScoreItem> Items, int Total)
{
    /// <summary>A breakdown whose total is the sum of its parts.</summary>
    public static ScoreBreakdown Of(List<ScoreItem> items) => new(items, items.Sum(i => i.Points));

    public static ScoreBreakdown Nothing => new([], 0);

    /// <summary>
    /// Said the way a person counts a hand out loud: "two fifteens for four, a pair of fives for
    /// two, one for his nob — seven", or "nothing".
    /// </summary>
    [JsonIgnore]
    public string Spoken => Total == 0
        ? "nothing"
        : string.Join(", ", Items.Select(i => i.Label)) + " — " + CribbageCards.NumberWord(Total);
}

/// <summary>What laying one card on the current count sequence would score.</summary>
/// <param name="Count">The count the card would make.</param>
public sealed record PlayScore(ScoreBreakdown Breakdown, int Count)
{
    [JsonIgnore]
    public int Total => Breakdown.Total;

    /// <summary>
    /// "fifteen for two and a pair for two": the shout, without the name or the running score
    /// around it. Empty when nothing is scored.
    /// </summary>
    [JsonIgnore]
    public string Phrase => string.Join(" and ", Breakdown.Items.Select(i => i.Label));
}

public static class CribbageScoring
{
    /// <summary>
    /// Score four cards with the starter as a fifth. <paramref name="isCrib"/> switches on the
    /// crib's flush rule: all five or nothing.
    /// </summary>
    public static ScoreBreakdown ScoreHand(IReadOnlyList<Card> cards4, Card? starter, bool isCrib)
    {
        var cards = cards4.ToList();
        if (starter is { } s0) cards.Add(s0);
        var n = cards.Count;
        var vals = cards.Select(CribbageCards.Value).ToArray();
        var ords = cards.Select(CribbageCards.Order).ToArray();
        var items = new List<ScoreItem>();
        var total = 0;

        // Fifteens. Every subset, because 5-5-5-J is four of them and no shortcut finds all four.
        var fifteens = 0;
        if (n > 0)
        {
            for (var mask = 1; mask < (1 << n); mask++)
            {
                int sum = 0, size = 0;
                for (var j = 0; j < n; j++)
                {
                    if ((mask & (1 << j)) == 0) continue;
                    sum += vals[j];
                    size++;
                }
                if (size >= 2 && sum == 15) fifteens++;
            }
        }
        if (fifteens > 0)
        {
            var pts = fifteens * 2;
            total += pts;
            var label = fifteens == 1
                ? "fifteen for two"
                : CribbageCards.NumberWord(fifteens) + " fifteens for " + CribbageCards.NumberWord(pts);
            items.Add(new ScoreItem(ScoreItemKind.Fifteen, label, pts, fifteens));
        }

        // Pairs, described by rank rather than as a bare count: "three fives for six" is what a
        // cribbage player says and what they can check.
        var byRank = new Dictionary<Rank, int>();
        foreach (var c in cards) byRank[c.Rank] = byRank.GetValueOrDefault(c.Rank) + 1;
        foreach (var rank in byRank.Keys.OrderBy(r => CribbageCards.Order(new Card(r, Suit.Clubs))))
        {
            var k = byRank[rank];
            if (k < 2) continue;
            var pts = k == 2 ? 2 : k == 3 ? 6 : 12;
            total += pts;
            var plural = CribbageCards.Plural(rank).ToLowerInvariant();
            var label = k == 2
                ? "a pair of " + plural + " for two"
                : CribbageCards.NumberWord(k) + " " + plural + " for " + CribbageCards.NumberWord(pts);
            items.Add(new ScoreItem(ScoreItemKind.Pair, label, pts, k));
        }

        // Runs. A run scores once for EVERY distinct set of cards that forms it: 4-5-6-6 is two
        // runs of three for six. Only maximal runs count, which is why the multiplicity is counted
        // at the longest length only.
        int longest = 0, howMany = 0;
        if (n > 0)
        {
            for (var mask = 1; mask < (1 << n); mask++)
            {
                var size = BitOperations.PopCount((uint)mask);
                if (size < 3 || size < longest) continue;
                var bits = 0;
                for (var j = 0; j < n; j++)
                    if ((mask & (1 << j)) != 0) bits |= 1 << ords[j];
                // Distinct and consecutive: as many bits as cards, and contiguous.
                if (BitOperations.PopCount((uint)bits) != size) continue;
                var low = bits & -bits;
                var shifted = bits / low;
                if ((shifted & (shifted + 1)) != 0) continue;
                if (size > longest) { longest = size; howMany = 1; }
                else howMany++;
            }
        }
        if (longest >= 3)
        {
            var pts = longest * howMany;
            total += pts;
            var label = howMany == 1
                ? "a run of " + CribbageCards.NumberWord(longest) + " for " + CribbageCards.NumberWord(longest)
                : CribbageCards.NumberWord(howMany) + " runs of " + CribbageCards.NumberWord(longest) + " for " + CribbageCards.NumberWord(pts);
            items.Add(new ScoreItem(ScoreItemKind.Run, label, pts, howMany));
        }

        // A flush. Four in the hand is four, and the starter matching makes five. IN THE CRIB IT IS
        // ALL FIVE OR NOTHING.
        if (cards4.Count == CribbageGame.Kept && cards4.All(c => c.Suit == cards4[0].Suit))
        {
            var first = cards4[0];
            var withStarter = starter?.Suit == first.Suit;
            if (isCrib)
            {
                if (withStarter)
                {
                    total += 5;
                    items.Add(new ScoreItem(ScoreItemKind.Flush, "a flush of five for five", 5));
                }
            }
            else
            {
                var pts = withStarter ? 5 : 4;
                total += pts;
                var w = CribbageCards.NumberWord(pts);
                items.Add(new ScoreItem(ScoreItemKind.Flush, "a flush of " + w + " for " + w, pts));
            }
        }

        // One for his nob: the jack of the starter's suit, held in the hand. Not the starter
        // itself: that is his heels, paid at the cut.
        if (starter is { } s && cards4.Any(c => c.Rank == Rank.Jack && c.Suit == s.Suit))
        {
            total += 1;
            items.Add(new ScoreItem(ScoreItemKind.Nob, "one for his nob", 1));
        }

        return new ScoreBreakdown(items, total);
    }

    /// <summary>
    /// The total alone, by a different route (a subset-sum table for the fifteens and a rank
    /// histogram for the pairs and runs) with no breakdown built. The computer averages a kept four
    /// over every starter that could still come, forty-six times fifteen ways per discard, and
    /// building words for each of them is most of the cost of a decision. The rules oracle tests
    /// hold this equal to <c>ScoreHand(...).Total</c>.
    /// </summary>
    public static int QuickTotal(IReadOnlyList<Card> cards4, Card? starter, bool isCrib)
    {
        Span<int> vals = stackalloc int[5];
        Span<int> hist = stackalloc int[14];
        Span<int> ways = stackalloc int[16];
        vals.Clear(); hist.Clear(); ways.Clear();
        var n = 0;
        foreach (var c in cards4) { vals[n] = CribbageCards.Value(c); hist[CribbageCards.Order(c)]++; n++; }
        if (starter is { } st) { vals[n] = CribbageCards.Value(st); hist[CribbageCards.Order(st)]++; n++; }

        // Fifteens: ways[t] = number of subsets summing to t.
        ways[0] = 1;
        for (var i = 0; i < n; i++)
        {
            var v = vals[i];
            if (v > 15) continue;
            for (var t = 15; t >= v; t--) ways[t] += ways[t - v];
        }
        var total = ways[15] * 2;

        // Pairs from the histogram: 2 per pair, C(k,2) * 2 = k(k-1).
        for (var r = 1; r <= 13; r++) total += hist[r] * (hist[r] - 1);

        // Runs: every maximal stretch of consecutive ranks three or more long, once for each way of
        // choosing one card from each rank.
        var at = 1;
        while (at <= 13)
        {
            if (hist[at] == 0) { at++; continue; }
            int end = at, mult = 1;
            while (end <= 13 && hist[end] > 0) { mult *= hist[end]; end++; }
            var len = end - at;
            if (len >= 3) total += len * mult;
            at = end;
        }

        if (cards4.Count == CribbageGame.Kept && cards4.All(c => c.Suit == cards4[0].Suit))
        {
            var withStarter = starter?.Suit == cards4[0].Suit;
            if (isCrib) { if (withStarter) total += 5; }
            else total += withStarter ? 5 : 4;
        }
        if (starter is { } s && cards4.Any(c => c.Rank == Rank.Jack && c.Suit == s.Suit)) total += 1;
        return total;
    }

    /// <summary>
    /// Distinct, consecutive run orders. Duplicates fail naturally: two fives sort to 5,5 and 5 is
    /// not 5+1.
    /// </summary>
    public static bool IsRun(IEnumerable<Card> cards)
    {
        var v = cards.Select(CribbageCards.Order).Order().ToList();
        if (v.Count < 2) return true;
        for (var i = 1; i < v.Count; i++)
            if (v[i] != v[i - 1] + 1) return false;
        return true;
    }

    /// <summary>
    /// What laying <paramref name="card"/> after <paramref name="sequence"/> (the current count
    /// sequence, never the whole pile) at <paramref name="count"/> would score, and why. PURE: it
    /// awards nothing and says nothing, which is what lets the engine, the computer and the
    /// interface share it.
    /// </summary>
    public static PlayScore PointsForPlay(IReadOnlyList<Card> sequence, int count, Card card)
    {
        var seq = sequence.Append(card).ToList();
        var newCount = count + CribbageCards.Value(card);
        var items = new List<ScoreItem>();
        var total = 0;

        if (newCount == 15) { total += 2; items.Add(new ScoreItem(ScoreItemKind.Fifteen, "fifteen for two", 2)); }
        if (newCount == 31) { total += 2; items.Add(new ScoreItem(ScoreItemKind.ThirtyOne, "thirty-one for two", 2)); }

        // Pairs, walking back through cards of the same rank. Stops at the first different one, so
        // there is no way to over-count.
        if (seq.Count >= 2)
        {
            var same = 1;
            for (var i = seq.Count - 2; i >= 0; i--)
            {
                if (seq[i].Rank == card.Rank) same++;
                else break;
            }
            if (same == 2) { total += 2; items.Add(new ScoreItem(ScoreItemKind.Pair, "a pair for two", 2, 2)); }
            else if (same == 3) { total += 6; items.Add(new ScoreItem(ScoreItemKind.Pair, "three of a kind for six", 6, 3)); }
            else if (same == 4) { total += 12; items.Add(new ScoreItem(ScoreItemKind.Pair, "four of a kind for twelve", 12, 4)); }
        }

        // Runs during the play do not have to arrive in order (5, 3, 4 is a run of three) but they
        // must be the last N cards with nothing repeated. Longest wins.
        for (var len = seq.Count; len >= 3; len--)
        {
            if (IsRun(seq.Skip(seq.Count - len)))
            {
                total += len;
                var w = CribbageCards.NumberWord(len);
                items.Add(new ScoreItem(ScoreItemKind.Run, "a run of " + w + " for " + w, len));
                break;
            }
        }
        return new PlayScore(new ScoreBreakdown(items, total), newCount);
    }
}
