using static TheCardPlace.Hearts.Tests.Support;

namespace TheCardPlace.Hearts.Tests;

/// The AI's decision for seat N is identical when every other seat's hand is
/// replaced by a different plausible hand of the same size, and when what the
/// other seats chose to pass or were handed is scrambled. A bot that peeks
/// does not throw and does not fail any other test; it simply plays
/// impossibly well.
public class HiddenInformationTests
{
    /// Redistribute the other three hands among themselves at random, keeping
    /// sizes; scramble the other seats' private pass rows the same way.
    private static HeartsState Scramble(HeartsState s, int seat, RandomSource rng)
    {
        // Everything is copied but the event log, which grows all game and would make each copy
        // slow; the copy shares it instead. Decide never writes to the log, so sharing is safe.
        var log = s.Log;
        s.Log = new EventLog();
        var t = Snapshot.Clone(s);
        s.Log = log;
        t.Log = log;
        var others = Enumerable.Range(0, 4).Where(i => i != seat).ToList();
        var pool = new List<Card>();
        foreach (var i in others) pool.AddRange(t.Players[i].Hand);
        pool = pool.Shuffled(rng);
        foreach (var i in others)
        {
            var n = t.Players[i].Hand.Count;
            t.Players[i].Hand = HeartsCards.SortHand(pool.Take(n));
            pool.RemoveRange(0, n);
            if (t.Passing[i] is not null)
                t.Passing[i] = t.Players[i].Hand.Take(3).ToList();
            if (t.Received[i] is not null)
                t.Received[i] = t.Players[i].Hand.TakeLast(3).ToList();
        }
        return t;
    }

    [Theory]
    [InlineData(1UL)] [InlineData(2UL)] [InlineData(3UL)] [InlineData(4UL)]
    [InlineData(5UL)] [InlineData(6UL)] [InlineData(7UL)] [InlineData(8UL)]
    [InlineData(9UL)] [InlineData(10UL)] [InlineData(11UL)] [InlineData(12UL)]
    public void Decisions_do_not_depend_on_other_hands(ulong seed)
    {
        // Swift counts over all twelve seeds (positions over 500, passes over 30); each seed
        // here is its own case, so the floors are a twelfth of those.
        var positions = 0;
        var passes = 0;
        var scrambleRng = new RandomSource(seed + 1000);
        var decideRng = new RandomSource(1);
        PlayGame(seed, Config(), inspect: s =>
        {
            if (HeartsGame.SeatToAct(s) is not { } seat) return;
            if (HeartsAI.Decide(s, seat, decideRng) is not { } want) return;
            var wantJson = Snapshot.Json(want);
            var mineJson = Snapshot.Json(s.Players[seat]);
            var allIds = IdSet(AllCards(s));
            for (var k = 0; k < 3; k++)
            {
                var alt = Scramble(s, seat, scrambleRng);
                Assert.True(Snapshot.Json(alt.Players[seat]) == mineJson, "the deciding seat must be untouched");
                Assert.True(IdSet(AllCards(alt)).SequenceEqual(allIds), "the scramble lost a card");
                var got = HeartsAI.Decide(alt, seat, decideRng);
                Assert.True(Snapshot.Json(got) == wantJson,
                    $"seed {seed}: seat {seat}'s {s.Phase} decision changed when other hands changed");
                if (Enumerable.Range(0, 4).Any(i => i != seat && !alt.Players[i].Hand.SequenceEqual(s.Players[i].Hand)))
                    positions++;
            }
            if (want is HeartsAction.Pass) passes++;
        });
        Assert.True(positions > 500 / 12, $"too few genuinely different positions were sampled: {positions}");
        Assert.True(passes > 30 / 12, $"the pass was not sampled enough: {passes}");
    }

    [Fact]
    public void The_hands_are_not_even_read_on_the_way_to_unseen()
    {
        // Unseen is what the AI tracks cards with. It must be the pack minus
        // this seat's hand minus what is on the table or taken — and nothing
        // about the other hands can be inferred from it.
        var rng = new RandomSource(3);
        var s = HeartsGame.CreateGame(Config());
        Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Start(), rng).IsOk);
        var @out = HeartsAI.Unseen(s, 0);
        Assert.Equal(39, @out.Count);
        Assert.Empty(@out.Intersect(s.Players[0].Hand));
        var alt = Snapshot.Clone(s);
        alt.Players[1].Hand = HeartsCards.SortHand(s.Players[2].Hand);
        alt.Players[2].Hand = HeartsCards.SortHand(s.Players[1].Hand);
        Assert.Equal(@out, HeartsAI.Unseen(alt, 0));
    }

    [Fact]
    public void AI_passes_are_deterministic_and_from_its_own_hand()
    {
        var rng = new RandomSource(44);
        var s = HeartsGame.CreateGame(Config());
        Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Start(), rng).IsOk);
        for (var seat = 0; seat < 4; seat++)
        {
            var a = HeartsAI.Decide(s, seat, rng);
            var pass = Assert.IsType<HeartsAction.Pass>(a);
            Assert.Equal(3, pass.Cards.Count);
            Assert.Equal(3, pass.Cards.Distinct().Count());
            Assert.All(pass.Cards, c => Assert.Contains(c, s.Players[seat].Hand));
            var again = new RandomSource(999);
            Assert.Equal(Snapshot.Json(a), Snapshot.Json(HeartsAI.Decide(s, seat, again)));   // the pass does not depend on the generator
        }
    }
}
