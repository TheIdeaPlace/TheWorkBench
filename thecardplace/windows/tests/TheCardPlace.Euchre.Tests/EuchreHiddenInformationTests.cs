using static TheCardPlace.Euchre.EuchreAction;

namespace TheCardPlace.Euchre.Tests;

/// <summary>
/// Does the computer cheat? For a sample of positions the decision for seat N must be identical
/// when every other seat's hand, the kitty and the dealer's discard are replaced by a different
/// plausible arrangement of the same cards. Matters more with three computers and one person at
/// the table than it sounds: a cheating AI plays legal cards and simply wins more than it should,
/// and nothing else would notice.
/// </summary>
public class EuchreHiddenInformationTests
{
    static HashSet<Card> Everything(EuchreState s)
    {
        var all = s.Players.SelectMany(p => p.Hand).Concat(s.Kitty).ToHashSet();
        if (s.Discard is { } d) all.Add(d);
        return all;
    }

    [Fact]
    public void Decisions_do_not_depend_on_cards_the_seat_cannot_see()
    {
        var positions = 0;
        var byPhase = new Dictionary<EuchrePhase, int>();
        var dealerDiscardPositions = 0;

        for (var g = 0; g < 15; g++)
        {
            var rng = new RandomSource(5000 + (ulong)g);
            var s = EuchreGame.CreateGame(Support.Config(
                difficulty: DifficultyExtensions.All[g % 3], stick: g % 2 == 0, alone: true));
            Assert.True(EuchreGame.ApplyAction(s, 0, new Start(), rng).IsOk);

            while (s.Phase != EuchrePhase.GameOver && positions < 3000)
            {
                if (s.Phase == EuchrePhase.HandOver)
                {
                    Assert.True(EuchreGame.ApplyAction(s, 0, new NextHand(), rng).IsOk);
                    continue;
                }
                var seat = EuchreGame.SeatToAct(s);
                Assert.NotNull(seat);

                /* The decision under test, made from a fixed seed so the AI's own noise is the
                 * same in both worlds. */
                var decisionSeed = 777 + (ulong)positions;
                var a1 = EuchreAI.Decide(s, seat.Value, new RandomSource(decisionSeed));
                Assert.NotNull(a1);

                for (var k = 0; k < 3; k++)
                {
                    var scrambleRng = new RandomSource(9000 + (ulong)(positions * 3 + k));
                    var alt = Support.Scramble(s, seat.Value, scrambleRng);
                    Assert.Equal(s.Players[seat.Value].Hand, alt.Players[seat.Value].Hand);
                    Assert.True(Everything(alt).SetEquals(Everything(s)), "the scramble must move cards, not invent them");
                    var a2 = EuchreAI.Decide(alt, seat.Value, new RandomSource(decisionSeed));
                    Assert.True(a1 == a2,
                        $"seat {seat} at {s.Phase} decided {a1} but {a2} when the cards it cannot see were rearranged");
                }
                positions++;
                byPhase[s.Phase] = byPhase.GetValueOrDefault(s.Phase) + 1;
                if (s.Phase == EuchrePhase.Play && s.Discard != null && seat != s.Dealer) dealerDiscardPositions++;

                var action = EuchreAI.Decide(s, seat.Value, rng);
                Assert.NotNull(action);
                var r = EuchreGame.ApplyAction(s, seat.Value, action, rng);
                Assert.True(r.IsOk, r.Reason ?? "");
            }
        }
        Assert.True(positions > 1500, $"only {positions} positions");
        foreach (var phase in new[] { EuchrePhase.Bid1, EuchrePhase.Bid2, EuchrePhase.Discard, EuchrePhase.Play })
            Assert.True(byPhase.GetValueOrDefault(phase) > 20, $"too few {phase} positions were watched");
        Assert.True(dealerDiscardPositions > 200, "the dealer's discard was rarely in play when another seat decided");
    }

    /// <summary>
    /// Two worlds differing only in which card the dealer put back: every other seat's next
    /// decision, and every public event, must be identical.
    /// </summary>
    [Fact]
    public void The_dealers_discard_is_invisible_to_the_other_seats()
    {
        var checkedCount = 0;
        for (var g = 0; g < 40; g++)
        {
            var rng = new RandomSource(31_000 + (ulong)g);
            var s = EuchreGame.CreateGame(Support.Config(difficulty: Difficulty.Hard));
            Assert.True(EuchreGame.ApplyAction(s, 0, new Start(), rng).IsOk);
            Support.Drive(s, rng, x => x.Phase != EuchrePhase.Bid1);
            if (s.Phase != EuchrePhase.Discard || s.Dealer is not { } dealer) continue;
            var hand = s.Players[dealer].Hand;
            var choices = hand.Where(c => !EuchreCards.IsTrump(c, s.Trump)).ToList();
            if (choices.Count < 2) continue;

            EuchreState a = Snapshot.Clone(s), b = Snapshot.Clone(s);
            RandomSource ra = new(1), rb = new(1);
            Assert.True(EuchreGame.ApplyAction(a, dealer, new Discard(choices[0]), ra).IsOk);
            Assert.True(EuchreGame.ApplyAction(b, dealer, new Discard(choices[1]), rb).IsOk);
            Assert.NotEqual(a.Discard, b.Discard);

            for (var seat = 0; seat < 4; seat++)
            {
                if (seat == dealer) continue;
                Assert.Equal(a.Log.EventsFor(seat).Select(e => e.Text), b.Log.EventsFor(seat).Select(e => e.Text));
            }
            // Play on until the dealer is about to play; every other seat's decision must match.
            while (a.Phase == EuchrePhase.Play && a.Turn != dealer && b.Turn == a.Turn)
            {
                var ma = EuchreAI.Decide(a, a.Turn, new RandomSource(2));
                var mb = EuchreAI.Decide(b, b.Turn, new RandomSource(2));
                Assert.True(ma == mb, $"seat {a.Turn} played differently depending on the dealer's hidden discard");
                checkedCount++;
                if (ma is null) break;
                Assert.True(EuchreGame.ApplyAction(a, a.Turn, ma, ra).IsOk);
                Assert.True(EuchreGame.ApplyAction(b, b.Turn, ma, rb).IsOk);
            }
        }
        Assert.True(checkedCount > 20, $"only {checkedCount} decisions checked");
    }

    /// <summary>
    /// The AI's own knowledge, by contrast, is used: the dealer remembers its discard and does not
    /// count it among the unseen cards.
    /// </summary>
    [Fact]
    public void Unseen_cards_are_exactly_what_the_seat_cannot_see()
    {
        var rng = new RandomSource(64);
        var s = EuchreGame.CreateGame(Support.Config());
        Assert.True(EuchreGame.ApplyAction(s, 0, new Start(), rng).IsOk);
        Support.Drive(s, rng, x => x.Phase == EuchrePhase.Play || Support.HandIsOver(x));
        Assert.Equal(EuchrePhase.Play, s.Phase);   // Swift returns quietly here; this seed does reach play
        var up = s.Upcard!.Value;
        for (var seat = 0; seat < 4; seat++)
        {
            var u = EuchreAI.Unseen(s, seat).ToHashSet();
            foreach (var c in s.Players[seat].Hand) Assert.DoesNotContain(c, u);
            foreach (var c in s.Played) Assert.DoesNotContain(c, u);
            Assert.DoesNotContain(up, u);
            if (s.Discard is { } d)
                Assert.True(u.Contains(d) == (seat != s.Dealer), "only the dealer knows the discard");
            // What the seat has seen: its hand, the upcard (which may be in that hand), and its own discard.
            var seen = s.Players[seat].Hand.Concat(s.Played).Append(up).ToHashSet();
            if (seat == s.Dealer && s.Discard is { } own) seen.Add(own);
            Assert.Equal(24 - seen.Count, u.Count);
            Assert.False(u.Overlaps(seen));
        }
    }
}
