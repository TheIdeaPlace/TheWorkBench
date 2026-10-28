using static TheCardPlace.Cribbage.Tests.T;

namespace TheCardPlace.Cribbage.Tests;

/// <summary>
/// The headline claim: the computer reads only what its own seat may see. Its decision is identical
/// when the other seat's hand, kept cards and discard, the crib and the undealt pack are all
/// replaced by different plausible cards of the same size.
/// </summary>
public sealed class HiddenInformationTests
{
    private sealed record Position(CribbageState State, int Seat);

    private static readonly Lazy<List<Position>> LazyPositions = new(() =>
    {
        var output = new List<Position>();
        ulong seed = 5000;
        foreach (var difficulty in DifficultyExtensions.All)
        {
            for (var i = 0; i < 4; i++)
            {
                Sim.Play(seed, difficulty, 121, observe: (state, seat) =>
                {
                    if (state.Phase is not (CribbagePhase.Discard or CribbagePhase.Play)) return;
                    if (state.Phase == CribbagePhase.Play && CribbageGame.LegalPlays(state, seat).Count < 2) return;
                    // The Swift copies a value; here the live state is cloned. The log and the
                    // history are dropped from the copy only to keep the clones cheap: scrambling
                    // leaves them alone, so they play no part in what this test proves.
                    var log = state.Log;
                    var history = state.History;
                    state.Log = new EventLog();
                    state.History = [];
                    output.Add(new Position(Snapshot.Clone(state), seat));
                    state.Log = log;
                    state.History = history;
                });
                seed++;
            }
        }
        return output;
    });

    private static List<Position> Positions => LazyPositions.Value;

    /// <summary>Everything the seat may not see, dealt out again at random into the same places.</summary>
    private static CribbageState Scramble(CribbageState state, int p, RandomSource rng)
    {
        var s = Snapshot.Clone(state);
        var o = 1 - p;
        var oppHandCount = s.Players[o].Hand.Count;
        var oppKeptEmpty = s.Players[o].Kept.Count == 0;
        var oppPlayed = s.Players[o].Played.ToList();
        var mine = s.Discarded[p] ?? [];
        var pool = s.Players[o].Hand.Concat(s.Discarded[o] ?? []).Concat(s.Crib).Concat(s.Deck).ToList();
        // The crib holds this seat's own discard too; that stays where it is.
        pool.RemoveAll(mine.Contains);
        pool = pool.Shuffled(rng);
        var at = 0;
        List<Card> Take(int n)
        {
            var slice = pool.GetRange(at, n);
            at += n;
            return slice;
        }
        var newHand = Take(oppHandCount);
        s.Players[o].Hand = newHand;
        s.Players[o].Kept = oppKeptEmpty ? [] : [.. oppPlayed, .. newHand];
        if (s.Discarded[o] is { } d)
        {
            var nd = Take(d.Count);
            s.Discarded[o] = nd;
            s.Crib = [.. mine, .. nd];
        }
        s.Deck = Take(pool.Count - at);
        return s;
    }

    private static HashSet<Card> Hidden(CribbageState s, int seat) =>
        s.Deck.Concat(s.Crib).Concat(s.Players[1 - seat].Hand).Concat(s.Discarded[1 - seat] ?? []).ToHashSet();

    [Fact]
    public void TheComputerNeverReadsWhatItCannotSee()
    {
        var positions = Positions;
        Assert.True(positions.Count > 500, $"only {positions.Count} positions");
        var compared = 0;
        var scrambler = new RandomSource(99);
        foreach (var pos in positions)
        {
            var original = CribbageAI.Decide(pos.State, pos.Seat, new RandomSource(4321));
            Assert.NotNull(original);
            for (var k = 0; k < 3; k++)
            {
                var other = Scramble(pos.State, pos.Seat, scrambler);
                // What this seat can see is untouched.
                Assert.Equal(Json(pos.State.Players[pos.Seat]), Json(other.Players[pos.Seat]));
                Assert.Equal(Json(pos.State.Pile), Json(other.Pile));
                Assert.Equal(pos.State.Starter, other.Starter);
                Assert.Equal(Json(pos.State.Discarded[pos.Seat]), Json(other.Discarded[pos.Seat]));
                Assert.True(Hidden(pos.State, pos.Seat).SetEquals(Hidden(other, pos.Seat)));
                var again = CribbageAI.Decide(other, pos.Seat, new RandomSource(4321));
                Assert.True(Json(original) == Json(again),
                            $"the computer's move changed when the hidden cards changed (seat {pos.Seat}, {pos.State.Phase})");
                compared++;
            }
        }
        Assert.True(compared > 1500);
    }

    [Fact]
    public void UnseenIsExactlyTheDeckLessWhatTheSeatHasSeen()
    {
        var rng = new RandomSource(8);
        var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Hard, 121));
        Assert.True(Advance(state, rng, s => s.Phase == CribbagePhase.Play && s.Pile.Count >= 3));
        for (var p = 0; p < 2; p++)
        {
            var unseen = CribbageAI.Unseen(state, p);
            var me = state.Players[p];
            var seen = me.Hand.Concat(me.Kept).Concat(me.Played).Concat(state.Pile.Select(e => e.Card))
                .Concat(state.Discarded[p] ?? []).Append(state.Starter!.Value).ToHashSet();
            Assert.True(unseen.ToHashSet().SetEquals(Card.FullDeck.Where(c => !seen.Contains(c))));
            // Nothing of the other seat's, the crib's or the pack's is excluded.
            var o = 1 - p;
            var pile = state.Pile.Select(e => e.Card).ToList();
            foreach (var c in state.Players[o].Hand.Where(c => !pile.Contains(c)))
                Assert.True(unseen.Contains(c), $"the computer knows the {c.Id} is in the other hand");
            foreach (var c in state.Discarded[o]!) Assert.Contains(c, unseen);
            foreach (var c in state.Deck) Assert.Contains(c, unseen);
        }
    }

    [Fact]
    public void DifficultyOnlyChangesHowOftenItPlaysAtRandom()
    {
        // On hard, no randomness is drawn: the decision never depends on the generator. On easy
        // and normal a random play is sometimes taken.
        var positions = Positions.Where(p => p.State.Phase == CribbagePhase.Play).Take(400).ToList();
        var differed = new Dictionary<Difficulty, int> { [Difficulty.Easy] = 0, [Difficulty.Normal] = 0, [Difficulty.Hard] = 0 };
        for (var i = 0; i < positions.Count; i++)
        {
            var pos = positions[i];
            foreach (var d in DifficultyExtensions.All)
            {
                var s = Snapshot.Clone(pos.State);
                s.Config.Difficulty = d;
                var x = CribbageAI.Decide(s, pos.Seat, new RandomSource((ulong)(2 * i + 1)));
                var y = CribbageAI.Decide(s, pos.Seat, new RandomSource((ulong)(2 * i + 2)));
                if (Json(x) != Json(y)) differed[d]++;
            }
        }
        Assert.Equal(0, differed[Difficulty.Hard]);
        Assert.True(differed[Difficulty.Easy] > differed[Difficulty.Normal]);
        Assert.True(differed[Difficulty.Normal] > 0);
    }
}
