using TheCardPlace.Hearts;

namespace TheCardPlace.Play.Tests;

/// <summary>
/// Hearts played the way a person plays it: through the hand and the controls the screen draws,
/// never by calling the engine. The computer chooses for the person's seat too, so a whole game
/// runs, but every choice goes in by Activate and the primary button.
/// </summary>
public class HeartsSessionTests
{
    /// <summary>One move for seat 0, made through the session's controls.</summary>
    private static void MoveFor(HeartsSession s)
    {
        var action = HeartsAI.Decide(s.State, s.Me, new RandomSource(99)) ?? throw new Xunit.Sdk.XunitException("nothing to decide");
        switch (action)
        {
            case HeartsAction.Pass p:
                foreach (var c in p.Cards) s.Activate(Table.CardIn(s, c));
                Assert.True(s.Primary.Enabled, "Pass should be ready with three chosen");
                s.Primary.Action();
                break;
            case HeartsAction.Play p:
                Assert.True(Table.CardIn(s, p.Card).Playable);
                s.Activate(Table.CardIn(s, p.Card));
                break;
            default:
                throw new Xunit.Sdk.XunitException("unexpected " + action);
        }
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    public void A_whole_game_through_the_controls(ulong seed)
    {
        var (s, _, speaker, _) = Table.Make<HeartsSession>(GameKind.Hearts, seed);
        s.Start();
        for (var guard = 0; guard < 2000 && s.State.Phase != HeartsPhase.GameOver; guard++)
        {
            if (s.State.Phase == HeartsPhase.HandOver)
            {
                Assert.Equal("Deal the next hand", s.Primary.Title);
                s.Advance();
            }
            else
            {
                Assert.True(s.IsMyTurn || (s.State.Phase == HeartsPhase.Passing && s.State.Passing[0] is null),
                    "the table stopped without it being the player's move: " + s.Status);
                MoveFor(s);
            }
        }
        Assert.Equal(HeartsPhase.GameOver, s.State.Phase);
        Assert.Equal("Start a new game", s.Primary.Title);
        Assert.NotEmpty(s.Log);
        Assert.Contains(speaker.Spoken, x => x.Text.Contains("Your turn", StringComparison.Ordinal));
        Assert.Equal(4, s.Seats.Count);
        Assert.Equal(4, s.Scores.Rows.Count);
        Assert.NotEmpty(s.History.Rows);
    }

    [Fact]
    public void Passing_chooses_and_unchooses_and_says_so()
    {
        var (s, _, _, heard) = Table.Make<HeartsSession>(GameKind.Hearts, 5);
        s.Start();
        Assert.Equal(HeartsPhase.Passing, s.State.Phase);
        Assert.False(s.Primary.Enabled);
        var cards = s.Hand.Take(4).ToList();
        s.Activate(cards[0]);
        Assert.Equal($"{cards[0].Card.Name} chosen. one of three chosen.", heard[^1].Text);
        Assert.Contains(", chosen", Table.CardIn(s, cards[0].Card).AccessibleName, StringComparison.Ordinal);
        s.Activate(Table.CardIn(s, cards[0].Card));
        Assert.Equal($"{cards[0].Card.Name} removed. No cards chosen yet.", heard[^1].Text);
        foreach (var c in cards.Take(3)) s.Activate(Table.CardIn(s, c.Card));
        Assert.EndsWith("Choose Pass to send them.", heard[^1].Text, StringComparison.Ordinal);
        s.Activate(Table.CardIn(s, cards[3].Card));
        Assert.True(heard[^1].Forced);
        Assert.StartsWith("Three cards are already chosen", heard[^1].Text, StringComparison.Ordinal);
        Assert.Equal("Pass 3 of 3", s.Primary.Title);
        Assert.True(s.Primary.Enabled);
    }

    [Fact]
    public void A_card_that_cannot_be_played_says_why_and_nothing_changes()
    {
        var (s, _, _, heard) = Table.Make<HeartsSession>(GameKind.Hearts, 8);
        s.Start();
        while (s.State.Phase == HeartsPhase.Passing) MoveFor(s);
        for (var guard = 0; guard < 60 && !s.Hand.Any(h => !h.Playable); guard++) MoveFor(s);
        var bad = s.Hand.First(h => !h.Playable);
        Assert.Contains("cannot be played, ", bad.AccessibleName, StringComparison.Ordinal);
        var before = Snapshot.Json(s.State);
        s.Activate(bad);
        Assert.Equal(before, Snapshot.Json(s.State));
        Assert.True(heard[^1].Forced);
        Assert.StartsWith($"{bad.Card.Name} cannot be played: ", heard[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Turned_off_categories_are_not_spoken_but_still_logged()
    {
        var settings = Table.Settings(GameKind.Hearts);
        settings.Announcements.Set(AnnouncementCategory.Moves, false);
        var (s, _, speaker, _) = Table.Make<HeartsSession>(GameKind.Hearts, 3, settings: settings);
        s.Start();
        while (s.State.Phase == HeartsPhase.Passing) MoveFor(s);
        for (var i = 0; i < 8; i++) MoveFor(s);
        var others = s.State.Players.Skip(1).Select(p => p.Name + " played").ToList();
        Assert.DoesNotContain(speaker.Spoken, x => others.Any(o => x.Text.Contains(o, StringComparison.Ordinal)));
        Assert.Contains(s.Log, l => others.Any(o => l.Contains(o, StringComparison.Ordinal)));
    }

    [Fact]
    public void Speech_off_still_speaks_refusals_and_answers()
    {
        var settings = Table.Settings(GameKind.Hearts);
        settings.Announcements.Enabled = false;
        var (s, announcer, speaker, _) = Table.Make<HeartsSession>(GameKind.Hearts, 3, settings: settings);
        s.Start();
        Assert.Empty(speaker.Spoken);
        s.Primary.Action();
        Assert.Single(speaker.Spoken);
        Assert.True(speaker.Spoken[0].Interrupt);
        announcer.Request(s.Reviews.First(r => r.Key == 'h').Text());
        Assert.Equal(2, speaker.Spoken.Count);
    }

    [Fact]
    public void Review_keys_do_not_collide_with_the_table_keys()
    {
        var (s, _, _, _) = Table.Make<HeartsSession>(GameKind.Hearts, 1);
        var keys = s.Reviews.Where(r => r.Key is not null).Select(r => r.Key!.Value).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.DoesNotContain('n', keys);
        Assert.DoesNotContain('r', keys);
        Assert.DoesNotContain('g', keys);
    }

    [Fact]
    public void A_remote_seat_waits_and_only_takes_its_own_moves()
    {
        var (s, _, _, _) = Table.Make<HeartsSession>(GameKind.Hearts, 11);
        s.Controllers[2] = SeatController.Remote;
        s.Start();
        // Passing is simultaneous; the remote seat has to pass before play can start.
        while (s.State.Passing[0] is null) MoveFor(s);
        Assert.Null(s.State.Passing.Count > 2 ? null : "x");
        Assert.Equal(HeartsPhase.Passing, s.State.Phase);
        Assert.False(s.ReceiveRemote(1, new HeartsAction.Pass(s.State.Players[1].Hand.Take(3).ToList())).IsOk);
        var pass = HeartsAI.Decide(s.State, 2, new RandomSource(4))!;
        Assert.True(s.ReceiveRemote(2, pass).IsOk);
        Assert.Equal(HeartsPhase.Play, s.State.Phase);
    }

    [Fact]
    public void New_game_starts_again_with_the_rules_from_settings()
    {
        var (s, _, _, _) = Table.Make<HeartsSession>(GameKind.Hearts, 6);
        s.Start();
        MoveFor(s);
        s.Settings.For(GameKind.Hearts).Rules[HeartsRules.PointsToWin.Key] = "50";
        s.NewGame();
        Assert.Equal(50, s.State.Config.PointsToWin);
        Assert.Equal(HeartsPhase.Passing, s.State.Phase);
        Assert.All(s.Hand, h => Assert.False(h.Chosen));
        Assert.Contains("New game.", s.Log);
    }
}
