using static TheCardPlace.Cribbage.Tests.T;

namespace TheCardPlace.Cribbage.Tests;

/// <summary>
/// The only test that knows what cribbage IS. The rules are re-implemented here from the How to
/// Play text, AND DELIBERATELY BY DIFFERENT ALGORITHMS:
///
///   fifteens   the engine enumerates subsets with a bitmask. This recurses.
///   runs       the engine enumerates subsets and counts maximal ones. This builds a rank
///              histogram and multiplies the multiplicities.
///   pairs      the engine groups by rank and looks up 2/6/12. This uses the combination formula.
///
/// This file may not call the engine's scoring to decide what it expects.
/// </summary>
public sealed class RulesOracleTests
{
    // MARK: The rules, written out

    /// <summary>"ace is one, and the ten, jack, queen and king are all ten"</summary>
    private static readonly Dictionary<char, int> VALUE = new()
    {
        ['A'] = 1, ['2'] = 2, ['3'] = 3, ['4'] = 4, ['5'] = 5, ['6'] = 6, ['7'] = 7,
        ['8'] = 8, ['9'] = 9, ['T'] = 10, ['J'] = 10, ['Q'] = 10, ['K'] = 10,
    };

    /// <summary>"ace is one and the king is thirteen, so a ten really is lower than a jack"</summary>
    private static readonly Dictionary<char, int> ORDER = new()
    {
        ['A'] = 1, ['2'] = 2, ['3'] = 3, ['4'] = 4, ['5'] = 5, ['6'] = 6, ['7'] = 7,
        ['8'] = 8, ['9'] = 9, ['T'] = 10, ['J'] = 11, ['Q'] = 12, ['K'] = 13,
    };

    private static readonly char[] SUITS = ['C', 'S', 'H', 'D'];
    private static readonly char[] RANKS = ['A', '2', '3', '4', '5', '6', '7', '8', '9', 'T', 'J', 'Q', 'K'];
    private static readonly List<string> DECK_IDS = SUITS.SelectMany(s => RANKS.Select(r => $"{r}{s}")).ToList();

    private static int V(string id) => VALUE[id[0]];
    private static int O(string id) => ORDER[id[0]];
    private static char SuitOf(string id) => id[^1];

    /// <summary>"2 for every combination adding to 15", by recursion.</summary>
    private static int OracleFifteens(List<string> ids)
    {
        var found = 0;
        void Walk(int i, int sum, int used)
        {
            if (i == ids.Count)
            {
                if (used >= 2 && sum == 15) found++;
                return;
            }
            Walk(i + 1, sum, used);
            Walk(i + 1, sum + V(ids[i]), used + 1);
        }
        Walk(0, 0, 0);
        return found * 2;
    }

    /// <summary>"2 each. Three of a kind is three pairs, so 6; four is 12.", by the combination formula.</summary>
    private static int OraclePairs(List<string> ids) =>
        ids.GroupBy(id => id[0]).Sum(g => g.Count() * (g.Count() - 1) / 2 * 2);

    /// <summary>"one per card, scored once for every distinct set that makes it", by histogram and multiplicity.</summary>
    private static int OracleRuns(List<string> ids)
    {
        var count = new int[15];
        foreach (var id in ids) count[O(id)]++;
        var pts = 0;
        var at = 1;
        while (at <= 13)
        {
            if (count[at] == 0) { at++; continue; }
            int end = at, mult = 1;
            while (end <= 13 && count[end] > 0) { mult *= count[end]; end++; }
            var len = end - at;
            if (len >= 3) pts += len * mult;
            at = end;
        }
        return pts;
    }

    /// <summary>
    /// "4 for all four matching, 5 if the starter matches too. In the crib it must be all five or
    /// it scores nothing."
    /// </summary>
    private static int OracleFlush(List<string> hand4, string? starter, bool isCrib)
    {
        var s = SuitOf(hand4[0]);
        if (!hand4.All(id => SuitOf(id) == s)) return 0;
        var withStarter = starter is not null && SuitOf(starter) == s;
        if (isCrib) return withStarter ? 5 : 0;
        return withStarter ? 5 : 4;
    }

    /// <summary>"the jack of the starter's suit, in your hand"</summary>
    private static int OracleNob(List<string> hand4, string? starter)
    {
        if (starter is null) return 0;
        return hand4.Any(id => id[0] == 'J' && SuitOf(id) == SuitOf(starter)) ? 1 : 0;
    }

    private static int OracleScore(List<string> hand4, string? starter, bool isCrib)
    {
        var all = starter is null ? hand4 : [.. hand4, starter];
        return OracleFifteens(all) + OraclePairs(all) + OracleRuns(all)
            + OracleFlush(hand4, starter, isCrib) + OracleNob(hand4, starter);
    }

    /// <summary>The play: fifteen 2, thirty-one 2, pairs by the formula, the longest run at the tail.</summary>
    private static int OraclePlayPoints(List<string> seq, int count)
    {
        var pts = 0;
        if (count == 15) pts += 2;
        if (count == 31) pts += 2;
        var last = seq[^1];
        var same = 0;
        for (var i = seq.Count - 1; i >= 0; i--)
        {
            if (seq[i][0] == last[0]) same++;
            else break;
        }
        if (same >= 2) pts += same * (same - 1) / 2 * 2;
        for (var len = seq.Count; len >= 3; len--)
        {
            var tail = seq.Skip(seq.Count - len).Select(O).Order().ToList();
            var run = true;
            for (var i = 1; i < tail.Count; i++)
                if (tail[i] != tail[i - 1] + 1) { run = false; break; }
            if (run) { pts += len; break; }
        }
        return pts;
    }

    // MARK: 1. The deck and the two numbers

    [Fact]
    public void TheDeckAndTheTwoNumbers()
    {
        Assert.Equal(52, Card.FullDeck.Count);
        Assert.True(Card.FullDeck.Select(c => c.Id).ToHashSet().SetEquals(DECK_IDS));
        foreach (var id in DECK_IDS)
        {
            var c = C(id);
            Assert.True(V(id) == CribbageCards.Value(c), $"{id} counts wrong");
            Assert.True(O(id) == CribbageCards.Order(c), $"{id} sits wrong in a run");
        }
        Assert.True(CribbageCards.Order(C("TC")) < CribbageCards.Order(C("JC")),
                    "a ten must be lower than a jack when cutting for deal");
        Assert.True(CribbageCards.Value(C("TC")) == CribbageCards.Value(C("JC")),
                    "a ten and a jack must count the same during the play");
    }

    // MARK: 2. Known hands

    [Theory]
    [InlineData("5C 5S 5H JD", "5D", false, 29, "the perfect hand")]
    [InlineData("5C 5S 5H 5D", "JC", false, 28, "four fives and a jack cut")]
    [InlineData("4C 5S 6H 6D", "KC", false, 14, "a double run of three with a fifteen")]
    [InlineData("4C 5S 6H 7D", "8C", false, 9, "a run of five and two fifteens")]
    [InlineData("AC 2S 3H KD", "5C", false, 7, "a run of three, and two fifteens with it")]
    [InlineData("AC 3S 7D JH", "KS", false, 0, "a hand worth absolutely nothing")]
    [InlineData("4C 6C 8C KC", "2C", false, 5, "a five-card flush")]
    [InlineData("4C 6C 8C KC", "2H", false, 4, "a four-card flush in the hand")]
    [InlineData("4C 6C 8C KC", "2H", true, 0, "a four-card flush in the CRIB scores nothing")]
    [InlineData("4C 6C 8C KC", "2C", true, 5, "a five-card flush in the crib does score")]
    [InlineData("JC 2S 4H 9D", "KC", false, 3, "a fifteen and one for his nob")]
    [InlineData("JC 2S 4H 9D", "KH", false, 2, "the same hand, but the jack is the wrong suit for a nob")]
    [InlineData("7C 8S 7H 8D", "9C", false, 24, "the double-double run")]
    [InlineData("3C 4S 5H 6D", "3D", false, 14, "a double run of four with fifteens")]
    public void KnownHands(string hand, string starter, bool crib, int want, string label)
    {
        var ids = hand.Split(' ').ToList();
        var oracle = OracleScore(ids, starter, crib);
        Assert.True(want == oracle, $"THE ORACLE ITSELF is wrong about {label}");
        var got = CribbageScoring.ScoreHand(Card.ParseList(hand), C(starter), crib);
        Assert.True(want == got.Total, $"{label}: {got.Spoken}");
        Assert.True(got.Total == got.Items.Sum(i => i.Points), $"{label}: the breakdown does not add up");
    }

    [Fact]
    public void KnownHandsInWords()
    {
        var perfect = CribbageScoring.ScoreHand(Cs("5C", "5S", "5H", "JD"), C("5D"), false);
        Assert.Equal("eight fifteens for sixteen, four fives for twelve, one for his nob — twenty-nine", perfect.Spoken);
        var dbl = CribbageScoring.ScoreHand(Cs("4C", "5S", "6H", "6D"), C("KC"), false);
        Assert.Equal("three fifteens for six, a pair of sixes for two, two runs of three for six — fourteen", dbl.Spoken);
        var nothing = CribbageScoring.ScoreHand(Cs("AC", "3S", "7D", "JH"), C("KS"), false);
        Assert.Equal("nothing", nothing.Spoken);
        Assert.Equal("fifteen for two, one for his nob — three",
                     CribbageScoring.ScoreHand(Cs("JC", "2S", "4H", "9D"), C("KC"), false).Spoken);
    }

    // MARK: 3. Every hand, against the other algorithm

    [Fact]
    public void RandomHandsAgainstTheOracle()
    {
        var rng = new RandomSource(8675309);
        var reached = new Dictionary<string, int>
        {
            ["flush4"] = 0, ["flush5"] = 0, ["nob"] = 0, ["doubleRun"] = 0, ["tripleRun"] = 0, ["big"] = 0, ["zero"] = 0,
        };
        var checkedCount = 0;

        for (var n = 0; n < 20000; n++)
        {
            var picked = DECK_IDS.Shuffled(rng).Take(5).ToList();
            var hand = picked.Take(4).ToList();
            var starter = picked[4];
            var isCrib = n % 3 == 0;
            var want = OracleScore(hand, starter, isCrib);
            var got = CribbageScoring.ScoreHand(hand.Select(C).ToList(), C(starter), isCrib);
            checkedCount++;
            if (want != got.Total)
                Assert.Fail($"{string.Join(" ", hand)} + {starter}{(isCrib ? " (crib)" : "")}: engine {got.Total}, rules {want} — {got.Spoken}");
            Assert.Equal(got.Total, got.Items.Sum(i => i.Points));
            Assert.True(want == CribbageScoring.QuickTotal(hand.Select(C).ToList(), C(starter), isCrib),
                        "the computer's quick total disagrees with the rules");
            if (OracleFlush(hand, starter, false) == 4) reached["flush4"]++;
            if (OracleFlush(hand, starter, false) == 5) reached["flush5"]++;
            if (OracleNob(hand, starter) > 0) reached["nob"]++;
            var runPts = OracleRuns([.. hand, starter]);
            if (runPts is 6 or 8) reached["doubleRun"]++;
            if (runPts >= 9) reached["tripleRun"]++;
            if (want >= 20) reached["big"]++;
            if (want == 0) reached["zero"]++;
        }

        // Deliberately weighted decks, because a uniform sample almost never produces the hands
        // where the scoring is hard.
        char[] nasty = ['4', '5', '5', '5', '6', '6', '7', '7', '8', 'J', 'T', '5'];
        var pool = nasty.SelectMany(r => SUITS.Select(s => $"{r}{s}")).ToList();
        for (var n = 0; n < 8000; n++)
        {
            var picked = new List<string>();
            var seen = new HashSet<string>();
            foreach (var id in pool.Shuffled(rng))
            {
                if (!seen.Add(id)) continue;
                picked.Add(id);
                if (picked.Count == 5) break;
            }
            var hand = picked.Take(4).ToList();
            var starter = picked[4];
            var isCrib = n % 4 == 0;
            var want = OracleScore(hand, starter, isCrib);
            var got = CribbageScoring.ScoreHand(hand.Select(C).ToList(), C(starter), isCrib);
            checkedCount++;
            if (want != got.Total)
                Assert.Fail($"weighted {string.Join(" ", hand)} + {starter}: engine {got.Total}, rules {want}");
            Assert.Equal(want, CribbageScoring.QuickTotal(hand.Select(C).ToList(), C(starter), isCrib));
            var runPts = OracleRuns([.. hand, starter]);
            if (runPts is 6 or 8) reached["doubleRun"]++;
            if (runPts >= 9) reached["tripleRun"]++;
            if (want >= 20) reached["big"]++;
        }

        foreach (var (name, min) in new[] { ("flush4", 10), ("flush5", 1), ("nob", 400), ("doubleRun", 200),
                                            ("tripleRun", 20), ("big", 50), ("zero", 100) })
            Assert.True(reached[name] >= min, $"only {reached[name]} hands reached {name}");
        Assert.Equal(28000, checkedCount);
    }

    // MARK: 4. The play

    [Fact]
    public void PlayPointsAgainstTheOracle()
    {
        var rng = new RandomSource(4242);
        var reached = new Dictionary<string, int>
        {
            ["fifteen"] = 0, ["thirtyone"] = 0, ["pair"] = 0, ["three"] = 0, ["four"] = 0, ["run"] = 0,
        };
        var checks = 0;

        for (var k = 0; k < 10000; k++)
        {
            var bag = DECK_IDS.Shuffled(rng);
            var seq = new List<string>();
            var count = 0;
            var wanted = 2 + rng.NextInt(5);
            foreach (var id in bag)
            {
                if (count + V(id) > 31) continue;
                seq.Add(id);
                count += V(id);
                if (seq.Count >= wanted) break;
            }
            if (seq.Count < 2) continue;
            var candidate = seq[^1];
            var before = seq.Take(seq.Count - 1).ToList();
            var beforeCount = count - V(candidate);

            var want = OraclePlayPoints(seq, count);
            var pure = CribbageScoring.PointsForPlay(before.Select(C).ToList(), beforeCount, C(candidate));

            // And through a state the engine accepts, with the sequence down.
            var st = CribbageGame.CreateGame(Sim.Config(Difficulty.Hard, 121));
            st.Phase = CribbagePhase.Play;
            st.Count = beforeCount;
            st.RunStart = 0;
            st.Pile = before.Select((id, i) => new CribbagePilePlay(i % 2, C(id))).ToList();
            var got = CribbageGame.PointsForPlay(st, C(candidate));

            checks++;
            if (want != got.Total || want != pure.Total)
                Assert.Fail($"play {string.Join(" ", before)} then {candidate} at {beforeCount}: engine {got.Total}, rules {want}");
            Assert.Equal(count, got.Count);
            Assert.True(got.Total == 0 || got.Breakdown.Items.Count > 0);
            Assert.Equal(got.Total, got.Breakdown.Items.Sum(i => i.Points));

            if (count == 15) reached["fifteen"]++;
            if (count == 31) reached["thirtyone"]++;
            var same = 0;
            for (var i = seq.Count - 1; i >= 0; i--)
            {
                if (seq[i][0] == candidate[0]) same++;
                else break;
            }
            if (same == 2) reached["pair"]++;
            if (same == 3) reached["three"]++;
            if (same == 4) reached["four"]++;
            if (want > 0 && same < 2 && count != 15 && count != 31) reached["run"]++;
        }
        foreach (var (name, min) in new[] { ("fifteen", 60), ("thirtyone", 30), ("pair", 60), ("run", 15) })
            Assert.True(reached[name] >= min, $"only {reached[name]} plays reached {name}");
        Assert.True(checks > 9000);
    }

    // MARK: 5. The count reset

    [Fact]
    public void TheCountReset()
    {
        var st = CribbageGame.CreateGame(Sim.Config(Difficulty.Hard, 121));
        st.Phase = CribbagePhase.Play;
        // Kings, so the count cannot reach fifteen or thirty-one and confuse the two points a pair
        // is worth with the two a fifteen is.
        st.Pile = [new(0, C("KC")), new(1, C("KS"))];
        st.RunStart = 0;
        st.Count = 20;
        Assert.True(CribbageGame.PointsForPlay(st, C("KH")).Total == 6,
                    "three kings in one sequence must be three of a kind for six");
        st.RunStart = 2;
        st.Count = 0;
        Assert.True(CribbageGame.PointsForPlay(st, C("KH")).Total == 0,
                    "a king laid after a count reset must NOT pair with kings from before it");

        st.Pile = [new(0, C("AC")), new(1, C("AS")), new(0, C("AH"))];
        st.RunStart = 0;
        st.Count = 3;
        var four = CribbageGame.PointsForPlay(st, C("AD"));
        Assert.True(four.Total == 12, "four of a kind during the play is twelve");
        Assert.Equal("four of a kind for twelve", four.Phrase);

        st.Pile = [new(0, C("KC")), new(1, C("2S"))];
        st.RunStart = 0;
        st.Count = 12;
        Assert.True(CribbageGame.PointsForPlay(st, C("KH")).Total == 0, "a king, then a two, then a king is not a pair");

        st.Pile = [new(0, C("3C")), new(1, C("4S"))];
        st.RunStart = 0;
        st.Count = 7;
        Assert.True(CribbageGame.PointsForPlay(st, C("5H")).Total == 3, "three, four, five is a run of three");
        st.RunStart = 2;
        st.Count = 0;
        Assert.True(CribbageGame.PointsForPlay(st, C("5H")).Total == 0, "a run must not be built across a count reset");

        // Fifteen and a pair together, said the way a player says it.
        st.Pile = [new(0, C("5C")), new(1, C("5S"))];
        st.RunStart = 0;
        st.Count = 10;
        var both = CribbageGame.PointsForPlay(st, C("5H"));
        Assert.Equal(8, both.Total);
        Assert.Equal("fifteen for two and three of a kind for six", both.Phrase);
    }

    // MARK: 6. Whole hands, re-scored by the oracle

    [Fact]
    public void WholeHandsRescored()
    {
        var seen = new Dictionary<string, int> { ["heels"] = 0, ["go"] = 0, ["lastCard"] = 0, ["thirtyOne"] = 0 };
        foreach (var g in Sim.Games)
        {
            Assert.True(g.State.IsGameOver, $"a game never finished (seed {g.Seed})");
            foreach (var h in g.State.History)
            {
                if (h.Starter is not { } starter) continue;
                foreach (var c in h.Counts)
                {
                    var cs = c.Kind == CribbageCountKind.Crib ? h.Crib : h.Kept[c.Who];
                    if (cs.Count != 4) continue;
                    var want = OracleScore(cs.Select(x => x.Id).ToList(), starter.Id, c.Kind == CribbageCountKind.Crib);
                    Assert.True(want == c.Result.Total,
                                $"hand {h.HandNumber}: the {c.Kind} scored {c.Result.Total}, the rules give {want}");
                }
                var running = 0;
                foreach (var e in h.Pile)
                {
                    var v = V(e.Card.Id);
                    if (running + v > 31) running = 0;
                    running += v;
                    Assert.True(running <= 31);
                    if (running == 31) seen["thirtyOne"]++;
                }
                if (starter.Rank == Rank.Jack) seen["heels"]++;
            }
            foreach (var e in g.State.Log.Events)
            {
                if (e.Text.Contains("for the go")) seen["go"]++;
                if (e.Text.Contains("for the last card")) seen["lastCard"]++;
            }
        }
        foreach (var (name, min) in new[] { ("heels", 5), ("go", 50), ("lastCard", 50), ("thirtyOne", 20) })
            Assert.True(seen[name] >= min, $"only {seen[name]} hands reached {name}");
    }

    // MARK: 7. The ledger

    /// <summary>
    /// Replay a completed hand from the permanent record and derive every point from the rules
    /// (his heels, every card of the pegging, the go, the last card, both hands and the crib),
    /// re-deriving WHOSE TURN it was from the cards each player still held.
    /// </summary>
    private static (int[] Got, List<string> Notes) ReplayHand(CribbageHandRecord h)
    {
        var got = new[] { 0, 0 };
        var notes = new List<string>();
        if (h.Starter is not { } starter) return (got, ["no starter"]);
        if (starter.Rank == Rank.Jack) got[h.Dealer] += 2;

        var remaining = new[] { h.Kept[0].Select(c => c.Id).ToHashSet(), h.Kept[1].Select(c => c.Id).ToHashSet() };
        var turn = 1 - h.Dealer;
        var count = 0;
        var seq = new List<string>();

        for (var i = 0; i < h.Pile.Count; i++)
        {
            var e = h.Pile[i];
            var id = e.Card.Id;
            if (e.Player != turn)
            {
                notes.Add($"card {i + 1} was laid by seat {e.Player + 1}, the rules say seat {turn + 1}");
                return (got, notes);
            }
            if (!remaining[e.Player].Contains(id))
            {
                notes.Add($"card {i + 1} ({id}) was not in seat {e.Player + 1}'s hand");
                return (got, notes);
            }
            var v = V(id);
            if (count + v > 31)
            {
                notes.Add($"card {i + 1} ({id}) took the count to {count + v}");
                return (got, notes);
            }
            count += v;
            seq.Add(id);
            got[e.Player] += OraclePlayPoints(seq, count);
            remaining[e.Player].Remove(id);

            if (remaining[0].Count == 0 && remaining[1].Count == 0)
            {
                if (count != 31) got[e.Player] += 1;
                break;
            }
            var opp = 1 - e.Player;
            var c0 = count;
            bool CanPlay(int s) => remaining[s].Any(x => c0 + V(x) <= 31);
            int LeadAfterReset() => remaining[opp].Count == 0 ? e.Player : opp;

            if (count == 31) { count = 0; seq = []; turn = LeadAfterReset(); continue; }
            if (CanPlay(opp)) { turn = opp; continue; }
            if (CanPlay(e.Player)) { turn = e.Player; continue; }
            got[e.Player] += 1;
            count = 0; seq = []; turn = LeadAfterReset();
        }

        foreach (var c in h.Counts)
        {
            var cs = c.Kind == CribbageCountKind.Crib ? h.Crib : h.Kept[c.Who];
            if (cs.Count != 4) continue;
            got[c.Who] += OracleScore(cs.Select(x => x.Id).ToList(), starter.Id, c.Kind == CribbageCountKind.Crib);
        }
        return (got, notes);
    }

    [Fact]
    public void TheLedger()
    {
        var ledgered = 0;
        foreach (var g in Sim.Games)
        {
            var prev = new[] { 0, 0 };
            foreach (var h in g.State.History)
            {
                var complete = h.Counts.Count == 3 && !(h.Result?.IsGameOver ?? false);
                if (!complete) { prev = [.. h.Scores]; continue; }
                var (got, notes) = ReplayHand(h);
                ledgered++;
                if (notes.Count > 0)
                    Assert.Fail($"hand {h.HandNumber} (seed {g.Seed}): " + string.Join("; ", notes));
                int[] moved = [h.Scores[0] - prev[0], h.Scores[1] - prev[1]];
                Assert.True(got.SequenceEqual(moved),
                            $"hand {h.HandNumber} (seed {g.Seed}): the rules give [{string.Join(", ", got)}] but the scores moved [{string.Join(", ", moved)}]");
                prev = [.. h.Scores];
            }
        }
        Assert.True(ledgered > 300, $"only {ledgered} hands were put through the ledger");
    }
}
