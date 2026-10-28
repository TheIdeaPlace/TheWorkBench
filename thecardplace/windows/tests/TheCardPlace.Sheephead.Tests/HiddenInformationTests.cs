using static TheCardPlace.Sheephead.Tests.Support;

namespace TheCardPlace.Sheephead.Tests;

/// <summary>
/// Nothing observable to a seat may depend on what that seat cannot see: other hands, the blind,
/// the picker's bury, or whether the picker is secretly alone.
/// </summary>
public class HiddenInformationTests
{
    private static readonly Card Jd = SheepheadCards.PartnerCard;

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Ally_probability_ignores_the_hidden_alone_flag(int n)
    {
        var (state, _) = NewGame(n, (ulong)n);
        state.Phase = SheepheadPhase.Play;
        state.Picker = 1;
        state.PartnerRevealed = false;
        // Seat 2 is a plain opponent looking at seat 3; make sure neither holds the jack.
        foreach (var p in state.Players) p.Hand = p.Hand.Removing(Jd);
        state.Alone = false;
        state.Partner = n - 1;
        state.Players[n - 1].Hand.Add(Jd);
        var withPartner = SheepheadAI.AllyProbability(state, 2, 3);
        state.Alone = true;
        state.Partner = null;
        state.Players[n - 1].Hand = state.Players[n - 1].Hand.Removing(Jd);
        state.Buried.Add(Jd);
        var whenAlone = SheepheadAI.AllyProbability(state, 2, 3);
        Assert.Equal(withPartner, whenAlone);
        Assert.True(whenAlone > 0 && whenAlone < 1, $"{n}p: hidden partner should be uncertain");
        Assert.Equal(0, SheepheadAI.AllyProbability(state, 2, 1));   // the picker is never an ally of a plain opponent
    }

    /// <summary>Replace everything the seat cannot see with a different plausible arrangement.</summary>
    private static SheepheadState Scrambled(SheepheadState state, int viewer, bool flipAlone, ulong seed)
    {
        var s = Snapshot.Clone(state);
        var rng = new RandomSource(seed);
        var isPicker = viewer == state.Picker;
        // Includes other hands, the blind, and the bury unless I am the picker.
        var pool = SheepheadAI.Unseen(state, viewer).Shuffled(rng);
        var pos = 0;
        for (var i = 0; i < state.Seats; i++)
        {
            if (i == viewer) continue;
            var k = state.Players[i].Hand.Count;
            s.Players[i].Hand = SheepheadCards.SortHand(pool.GetRange(pos, k));
            pos += k;
        }
        if (!isPicker)
        {
            var k = state.Buried.Count;
            s.Buried = pool.GetRange(pos, k);
            pos += k;
        }
        s.Blind = pool.GetRange(pos, pool.Count - pos);
        Assert.Equal(state.Blind.Count, s.Blind.Count);
        // Keep the hidden fields plausible for the new arrangement, or flip them.
        if (!state.PartnerRevealed && !state.IsLeaster && state.Spec.Partner && state.Picker is { } picker)
        {
            var h = s.Players.FindIndex(p => p.Hand.Contains(Jd));
            int? holder = h >= 0 ? h : null;
            if (flipAlone)
            {
                s.Alone = !state.Alone;
                s.Partner = s.Alone ? null : holder ?? Enumerable.Range(0, state.Seats).First(i => i != picker && i != viewer);
            }
            else
            {
                s.Alone = holder is null || holder == picker || s.Buried.Contains(Jd);
                s.Partner = s.Alone ? null : holder;
            }
        }
        return s;
    }

    public static TheoryData<int, Difficulty> Tables()
    {
        var data = new TheoryData<int, Difficulty>();
        for (var n = 3; n <= 6; n++)
            foreach (var d in new[] { Difficulty.Hard, Difficulty.Normal })
                data.Add(n, d);
        return data;
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void Decisions_do_not_depend_on_what_the_seat_cannot_see(int n, Difficulty difficulty)
    {
        var positions = 0;
        for (var i = 0; i < 10; i++)
        {
            var (state, rng) = NewGame(n, (ulong)(n * 100 + i), difficulty: difficulty);
            PlayHand(state, rng, (st, seat, action, before) =>
            {
                var viewerHoldsJack = st.Players[seat].Hand.Contains(Jd);
                var mayFlip = seat != st.Picker && !viewerHoldsJack;
                foreach (var flip in mayFlip ? new[] { false, true } : [false])
                {
                    var other = Scrambled(st, seat, flip, (ulong)(positions + 7));
                    Assert.Equal(J(st.Players[seat]), J(other.Players[seat]));
                    var again = SheepheadAI.Decide(st, seat, CopyOf(before));
                    var scrambledDecision = SheepheadAI.Decide(other, seat, CopyOf(before));
                    Assert.Equal(J(action), J(again));   // the decision must replay
                    Assert.True(J(action) == J(scrambledDecision),
                        $"{n}p {st.Phase} seat {seat}: decision changed when unseen cards changed (flip {flip})");
                }
                positions++;
            });
        }
        // Swift asks for more than 500 positions across all eight tables.
        Assert.True(positions > 500 / 8);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void No_event_leaks_the_sides_before_the_reveal(int n)
    {
        int aloneHands = 0, partneredHands = 0;
        for (var i = 0; i < 60; i++)
        {
            var (state, rng) = NewGame(n, (ulong)(n * 1000 + i));
            var lastId = state.Log.LastId;
            int? revealedAt = null;
            PlayHand(state, rng, (st, _, _, _) =>
            {
                if (revealedAt is null && st.PartnerRevealed && st.Picker is not null) revealedAt = st.Log.LastId;
            });
            if (state.IsLeaster) continue;
            var picker = state.Picker!.Value;
            var bystander = picker == 0 ? 1 : 0;
            var cutoff = revealedAt ?? state.Log.Events.First(e => e.Kind == EventKind.Score).Id;
            var visible = state.Log.EventsFor(bystander, lastId).Where(e => e.Id < cutoff).ToList();
            // The reveal itself is a play event that is allowed to say it; stop before it.
            var text = string.Join(" ", visible
                .Where(e => !(e.Kind == EventKind.Play && e.Cards.SequenceEqual([Jd])))
                .Select(e => e.Text)).ToLowerInvariant();
            Assert.False(text.Contains("alone", StringComparison.Ordinal), $"{n}p: alone leaked to seat {bystander}: {text}");
            Assert.False(text.Contains("secret partner", StringComparison.Ordinal), $"{n}p: partner hint leaked");
            Assert.Contains(visible, e =>
                e.Text == $"{state.Players[picker].Name} is the picker. The Jack of Diamonds is the partner card."
                || e.Text == "You are the picker. The Jack of Diamonds is the partner card.");   // the neutral announcement
            // The picker was told, privately.
            var told = state.Log.Events.Where(e => e.Audience == picker
                && (e.Text.Contains("playing alone", StringComparison.Ordinal) || e.Text.Contains("secret partner", StringComparison.Ordinal)));
            Assert.Single(told);
            if (state.Alone) aloneHands++;
            else partneredHands++;
        }
        Assert.True(aloneHands > 0);
        Assert.True(partneredHands > 0);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void The_reveal_says_the_right_thing(int n)
    {
        int alone = 0, partnered = 0;
        for (var i = 0; i < 60; i++)
        {
            var (state, rng) = NewGame(n, (ulong)(n * 2000 + i));
            PlayHand(state, rng);
            if (state.IsLeaster) continue;
            var plays = state.Log.Events.Where(e => e.Kind == EventKind.Play).ToList();
            var jd = plays.FirstOrDefault(e => e.Cards.SequenceEqual([Jd]));
            if (jd is null)
            {
                Assert.True(state.Buried.Contains(Jd), "the jack was neither played nor buried");
                continue;
            }
            var before = string.Join(" ", plays.TakeWhile(e => e.Id != jd.Id).Select(e => e.Text)).ToLowerInvariant();
            Assert.False(before.Contains("alone", StringComparison.Ordinal) || before.Contains("partner", StringComparison.Ordinal),
                "sides leaked in play events before the jack");
            if (state.Alone)
            {
                alone++;
                Assert.Contains("playing alone", jd.Text, StringComparison.Ordinal);
                Assert.Equal(state.Picker, jd.Seat);
            }
            else
            {
                partnered++;
                Assert.True(jd.Text.EndsWith("is the picker's partner.", StringComparison.Ordinal)
                    || jd.Text.EndsWith("are the picker's partner.", StringComparison.Ordinal), jd.Text);
                Assert.Equal(state.Partner, jd.Seat);
            }
            Assert.Equal(jd.Seat, state.RevealInfo?.Player);
            Assert.Equal(state.Alone, state.RevealInfo?.Alone);
        }
        Assert.True(alone > 0);
        Assert.True(partnered > 0);
    }

    /// <summary>
    /// The computer players price trump at -45 when burying, so they never choose this
    /// themselves; force it, and the hand must stay silent.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void A_buried_jack_stays_secret_all_hand(int n)
    {
        for (var i = 0; i < 15; i++)
        {
            var (state, rng) = NewGame(n, (ulong)(n * 3000 + i));
            state.Turn = 0;
            state.Leader = 0;
            state.Dealer = n - 1;
            if (!state.Players[0].Hand.Contains(Jd))
            {
                // Swap it into seat 0 from wherever it is.
                var swap = state.Players[0].Hand[0];
                state.Players[0].Hand[0] = Jd;
                var h = Enumerable.Range(1, n - 1).Where(k => state.Players[k].Hand.Contains(Jd)).Cast<int?>().FirstOrDefault();
                if (h is { } holder) state.Players[holder].Hand = [.. state.Players[holder].Hand.Removing(Jd), swap];
                else state.Blind = [.. state.Blind.Removing(Jd), swap];
            }
            state.Players[0].Name = "P0";
            state.Players[0].Occupant = Occupant.Bot;
            Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.Pick(), rng));
            var other = state.Players[0].Hand.First(c => c != Jd);
            Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.Bury([Jd, other]), rng));
            Assert.True(state.Alone);
            Assert.False(state.PartnerRevealed);
            PlayHand(state, rng, (st, _, _, _) => Assert.False(st.PartnerRevealed, "buried jack revealed mid-hand"));
            var scoreId = state.Log.Events.First(e => e.Kind == EventKind.Score).Id;
            var texts = state.Log.EventsFor(1).Where(e => e.Id < scoreId).Select(e => e.Text).ToList();
            const string neutral = "P0 is the picker. The Jack of Diamonds is the partner card.";
            Assert.Contains(neutral, texts);
            var rest = string.Join(" ", texts.Where(t => t != neutral)).ToLowerInvariant();
            Assert.False(rest.Contains("alone", StringComparison.Ordinal) || rest.Contains("partner", StringComparison.Ordinal), $"buried jack leaked: {rest}");
            Assert.Contains("P0 alone (the Jack of Diamonds was buried)", state.Result!.Summary, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_human_picker_is_told_their_own_situation()
    {
        int toldAlone = 0, toldPartnered = 0;
        for (var i = 0; i < 60; i++)
        {
            var (state, rng) = FiveHanded((ulong)(4000 + i));
            Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.Pick(), rng));
            var since = state.Log.LastId;
            var hand = state.Players[0].Hand;
            Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.Bury([hand[^1], hand[^2]]), rng));
            var mine = string.Join(" ", state.Log.EventsFor(0, since).Select(e => e.Text));
            var theirs = string.Join(" ", state.Log.EventsFor(1, since).Select(e => e.Text));
            if (state.Alone)
            {
                toldAlone++;
                Assert.Contains("you are playing alone", mine, StringComparison.Ordinal);
            }
            else
            {
                toldPartnered++;
                Assert.Contains("secret partner", mine, StringComparison.Ordinal);
            }
            Assert.Contains("You are the picker. The Jack of Diamonds is the partner card.", theirs, StringComparison.Ordinal);
            Assert.False(theirs.ToLowerInvariant().Contains("alone", StringComparison.Ordinal) || theirs.Contains("secret partner", StringComparison.Ordinal));
            Assert.DoesNotContain("You buried", theirs, StringComparison.Ordinal);
            Assert.DoesNotContain("From the blind", theirs, StringComparison.Ordinal);
        }
        Assert.True(toldAlone > 0);
        Assert.True(toldPartnered > 0);
    }

    [Fact]
    public void Doubler_holders_are_told_privately()
    {
        var found = 0;
        for (var i = 0; i < 80; i++)
        {
            var (state, rng) = NewGame(3, (ulong)(6000 + i), doublers: true);
            PlayHand(state, rng);
            foreach (var e in state.Log.Events.Where(e => e.Text.Contains("counts double", StringComparison.Ordinal)))
            {
                found++;
                Assert.NotNull(e.Audience);
                Assert.Equal(e.Seat, e.Audience);
            }
        }
        Assert.True(found > 0);
    }
}
