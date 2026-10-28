using static TheCardPlace.Spades.Tests.T;

namespace TheCardPlace.Spades.Tests;

/// <summary>
/// The computer's decision for a seat is identical when every other seat's hand is replaced by a
/// different plausible hand of the same size. A bot that peeks does not throw and does not fail a
/// rules test; it simply plays impossibly well.
/// </summary>
public class HiddenInfoTests
{
    /// The other three hands, redealt among themselves in a different order so each seat still
    /// holds the right number of cards. The log is left out of the copy (the computer never reads
    /// it, and copying it every time is most of the cost of this test).
    private static SpadesState Scramble(SpadesState state, int keeping, RandomSource rng)
    {
        var log = state.Log;
        state.Log = new EventLog();
        var copy = Snapshot.Clone(state);
        state.Log = log;

        var pool = new List<Card>();
        for (var i = 0; i < 4; i++)
            if (i != keeping) pool.AddRange(copy.Players[i].Hand);
        pool = pool.Shuffled(rng);
        var k = 0;
        for (var i = 0; i < 4; i++)
        {
            if (i == keeping) continue;
            var n = copy.Players[i].Hand.Count;
            copy.Players[i].Hand = SpadesCards.SortHand(pool.GetRange(k, n));
            k += n;
        }
        return copy;
    }

    [Fact]
    public void Decisions_do_not_depend_on_other_hands()
    {
        var positions = 0;
        var biddingPositions = 0;
        var scrambleRng = new RandomSource(2026);
        for (var g = 0; g < 6; g++)
        {
            var (state, rng) = Drive.NewGame(Config(pointsToWin: 250), (ulong)(300 + g));
            var guardCount = 0;
            while (state.Phase != SpadesPhase.GameOver && guardCount < 40_000)
            {
                guardCount++;
                if (SpadesGame.SeatToAct(state) is int seat)
                {
                    var original = SpadesAI.Decide(state, seat, new RandomSource(7));
                    for (var i = 0; i < 3; i++)
                    {
                        var other = Scramble(state, seat, scrambleRng);
                        var again = SpadesAI.Decide(other, seat, new RandomSource(7));
                        Assert.True(Equals(original, again),
                            $"seat {seat} decided differently when other hands changed ({state.Phase})");
                    }
                    positions++;
                    if (state.Phase == SpadesPhase.Bidding) biddingPositions++;
                }
                Drive.Step(state, rng);
            }
        }
        Assert.True(positions > 500, $"only {positions} positions");
        Assert.True(biddingPositions > 40, $"only {biddingPositions} bidding positions");
    }

    [Fact]
    public void Unseen_is_the_pack_less_own_hand_and_the_table()
    {
        var state = Position([3, 3, 3, 3], [0, 0, 0, 0], ["AS 2C 3D", "3C 4D KH", "5C 6C QH", "7C 8C JH"], 0, 0);
        Assert.True(SpadesGame.ApplyAction(state, 0, new SpadesAction.Play(C("2C")), new RandomSource(1)).IsOk);
        var @out = SpadesAI.Unseen(state, 1);
        Assert.Equal(52 - 3 - 1, @out.Count);
        Assert.DoesNotContain(C("2C"), @out);
        Assert.DoesNotContain(C("KH"), @out);
        // The ace of spades is in North's hand, and East cannot know that.
        Assert.Contains(C("AS"), @out);
    }

    /// Nor does the computer read PlayedThisHand: the web bots remember only the current trick
    /// and the last one. Emptying it changes nothing.
    [Fact]
    public void Decisions_do_not_read_the_cards_played_this_hand()
    {
        var (state, rng) = Drive.ToPlay(Config(), 404);
        var checkedPositions = 0;
        while (state.Phase == SpadesPhase.Play)
        {
            var seat = state.Turn;
            var original = SpadesAI.Decide(state, seat, new RandomSource(7));
            var forgetful = Snapshot.Clone(state);
            forgetful.PlayedThisHand = [];
            Assert.Equal(original, SpadesAI.Decide(forgetful, seat, new RandomSource(7)));
            checkedPositions++;
            Drive.Step(state, rng);
        }
        Assert.Equal(52, checkedPositions);
    }
}
