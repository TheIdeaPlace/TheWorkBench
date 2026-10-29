using System.Collections.Concurrent;
using TheCardPlace.Hearts;
using TheCardPlace.Spades;

namespace TheCardPlace.Play.Tests;

/// <summary>
/// A single thread with a message queue, the way the window runs a session: every continuation
/// and every timer callback the pace gate posts comes back to this thread.
/// </summary>
public sealed class SingleThread : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = [];
    private readonly Thread _thread;

    public SingleThread()
    {
        _thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (cb, state) in _queue.GetConsumingEnumerable()) cb(state);
        }) { IsBackground = true };
        _thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    /// <summary>Runs work on the thread and waits for it, including anything it awaits.</summary>
    public T Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ => { try { done.SetResult(work()); } catch (Exception e) { done.SetException(e); } }, null);
        return done.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    public void Run(Action work) => Run(() => { work(); return 0; });

    /// <summary>Waits until a condition, checked on the thread, holds.</summary>
    public async Task Until(Func<bool> test, string what)
    {
        for (var i = 0; i < 200; i++)
        {
            if (Run(test)) return;
            await Task.Delay(25);
        }
        throw new Xunit.Sdk.XunitException("never: " + what);
    }

    public void Dispose() => _queue.CompleteAdding();
}

public class TableDriverTests
{
    private static void PassFor(HeartsSession s)
    {
        var pass = (HeartsAction.Pass)HeartsAI.Decide(s.State, s.Me, new RandomSource(9))!;
        foreach (var c in pass.Cards) s.Activate(Table.CardIn(s, c));
        s.Primary.Action();
    }

    [Fact]
    public async Task A_timed_pause_is_ended_by_continue_and_moves_on_one_play()
    {
        using var ui = new SingleThread();
        // Seed 22: a computer player leads after the pass, so there is a pause before its play.
        var (s, _, _, _) = ui.Run(() => Table.Make<HeartsSession>(GameKind.Hearts, 22, Pace.Relaxed));
        ui.Run(() => { s.Start(); PassFor(s); });
        await ui.Until(() => s.Gate.IsWaiting, "a pause before the computer's lead");
        var played = ui.Run(() => s.State.Trick.Count);
        ui.Run(() => s.Advance());
        await ui.Until(() => s.State.Trick.Count > played || s.IsMyTurn, "the next play after Continue");
        Assert.True(ui.Run(() => s.State.Trick.Count >= played + 1 || s.State.TricksPlayed > 0));
    }

    [Fact]
    public async Task New_game_during_a_pause_stops_the_old_run_dead()
    {
        using var ui = new SingleThread();
        var (s, _, speaker, _) = ui.Run(() => Table.Make<HeartsSession>(GameKind.Hearts, 22, Pace.WaitForMe));
        ui.Run(() => { s.Start(); PassFor(s); });
        await ui.Until(() => s.Gate.IsWaiting || s.IsMyTurn, "a pause");
        ui.Run(() => s.NewGame());
        var spokenAfter = ui.Run(() => speaker.Spoken.Count);
        Assert.Equal(HeartsPhase.Passing, ui.Run(() => s.State.Phase));
        await Task.Delay(300);
        // The old run, released by the new game, must neither play nor speak.
        Assert.Equal(HeartsPhase.Passing, ui.Run(() => s.State.Phase));
        Assert.Equal(spokenAfter, ui.Run(() => speaker.Spoken.Count));
        Assert.All(ui.Run(() => s.State.Players.Select(p => p.Hand.Count).ToList()), n => Assert.Equal(13, n));
    }

    [Fact]
    public async Task Wait_for_me_waits_until_continue()
    {
        using var ui = new SingleThread();
        var (s, _, _, _) = ui.Run(() => Table.Make<HeartsSession>(GameKind.Hearts, 23, Pace.WaitForMe));
        ui.Run(() => { s.Start(); PassFor(s); });
        await ui.Until(() => s.Gate.IsWaiting || s.IsMyTurn, "a pause");
        if (ui.Run(() => s.IsMyTurn)) return;
        var before = ui.Run(() => Snapshot.Json(s.State));
        await Task.Delay(500);
        Assert.Equal(before, ui.Run(() => Snapshot.Json(s.State)));
        Assert.Equal("continue", ui.Run(() => s.ContinueControl.Id));
        ui.Run(() => s.Advance());
        await ui.Until(() => Snapshot.Json(s.State) != before, "a play after Continue");
    }

    [Fact]
    public async Task With_speak_each_play_off_a_timed_run_is_one_message()
    {
        using var ui = new SingleThread();
        var settings = Table.Settings(GameKind.Hearts, Pace.Brisk);
        settings.SpeakEveryPlay = false;
        var (s, _, speaker, _) = ui.Run(() => Table.Make<HeartsSession>(GameKind.Hearts, 24, settings: settings));
        ui.Run(() => { s.Start(); PassFor(s); });
        // Continue through every pause at once rather than wait out the Brisk timer.
        for (var i = 0; i < 40 && !ui.Run(() => s.IsMyTurn); i++)
        {
            await ui.Until(() => s.Gate.IsWaiting || s.IsMyTurn, "a pause or the turn");
            ui.Run(() => { if (s.Gate.IsWaiting) s.Gate.Continue(); });
        }
        var plays = ui.Run(() => speaker.Spoken.Where(x => x.Text.Contains(" played the ", StringComparison.Ordinal)).ToList());
        Assert.NotEmpty(plays);
        // Every message that mentions a play mentions all of that run's plays together.
        Assert.All(plays, p => Assert.False(p.Interrupt));
        var trick = ui.Run(() => s.State.Trick.Count);
        if (trick > 1) Assert.Contains(plays, p => p.Text.Split(" played the ").Length - 1 >= trick);
    }

    [Fact]
    public void N_never_bids_in_spades()
    {
        var (s, _, _, heard) = Table.Make<SpadesSession>(GameKind.Spades, 4);
        s.Start();
        Assert.Equal(SpadesPhase.Bidding, s.State.Phase);
        Assert.True(s.IsMyTurn);
        s.Advance();
        s.Advance();
        Assert.Null(s.State.Players[0].Bid);
        Assert.Equal(s.Status, heard[^1].Text);
    }

    [Fact]
    public void N_never_passes_in_hearts()
    {
        var (s, _, _, _) = Table.Make<HeartsSession>(GameKind.Hearts, 4);
        s.Start();
        foreach (var c in s.Hand.Take(3).ToList()) s.Activate(c);
        s.Advance();
        Assert.Null(s.State.Passing[0]);
        Assert.Equal(3, s.Hand.Count(h => h.Chosen));
    }

    [Fact]
    public void The_pass_hint_is_a_hint_and_can_be_turned_off()
    {
        var settings = Table.Settings(GameKind.Hearts);
        settings.Announcements.Set(AnnouncementCategory.Hint, false);
        var (s, _, speaker, heard) = Table.Make<HeartsSession>(GameKind.Hearts, 5, settings: settings);
        s.Start();
        foreach (var c in s.Hand.Take(3).ToList()) s.Activate(c);
        Assert.Contains(heard, a => a.Category == AnnouncementCategory.Hint && a.Text == "Choose Pass to send them." && !a.Spoken);
        Assert.DoesNotContain(speaker.Spoken, x => x.Text.Contains("Choose Pass", StringComparison.Ordinal));
    }

    [Fact]
    public void Who_won_is_scoring_not_turn()
    {
        var e = new GameEvent(1, EventKind.Game, "Ruth wins.", null, [], null);
        Assert.Equal(AnnouncementCategory.Scoring, AnnouncementCategories.ForEvent(e, 0));
    }

    [Fact]
    public void Game_events_queue_and_answers_interrupt()
    {
        var (s, announcer, speaker, _) = Table.Make<HeartsSession>(GameKind.Hearts, 5);
        s.Start();
        Assert.All(speaker.Spoken, x => Assert.False(x.Interrupt));
        announcer.Request("Your hand.");
        Assert.True(speaker.Spoken[^1].Interrupt);
        announcer.Say("Back at the games.", followsFocus: true);
        Assert.False(speaker.Spoken[^1].Interrupt);
    }

    [Theory]
    [InlineData("""{ "Games": null, "Announcements": null, "Zoom": 0 }""")]
    [InlineData("""{ "Games": { "hearts": null }, "PlayerName": null }""")]
    [InlineData("not json")]
    [InlineData("")]
    public void Damaged_settings_files_give_working_settings(string json)
    {
        var s = AppSettings.FromJson(json);
        Assert.InRange(s.Zoom, 0.8, 2.0);
        Assert.Equal(Pace.Brisk, s.For(GameKind.Hearts).Pace);
        Assert.True(s.Announcements.IsOn(AnnouncementCategory.Moves));
        Assert.NotEmpty(s.Names(4)[0]);
    }

    [Fact]
    public void Settings_round_trip()
    {
        var s = new AppSettings { PlayerName = "Kelly", SpeakEveryPlay = false, Zoom = 1.5 };
        s.For(GameKind.Euchre).Pace = Pace.WaitForMe;
        s.For(GameKind.Euchre).Rules[EuchreRules.StickTheDealer.Key] = "true";
        s.Announcements.Set(AnnouncementCategory.Moves, false);
        var back = AppSettings.FromJson(s.ToJson());
        Assert.Equal("Kelly", back.PlayerName);
        Assert.False(back.SpeakEveryPlay);
        Assert.Equal(1.5, back.Zoom);
        Assert.Equal(Pace.WaitForMe, back.For(GameKind.Euchre).Pace);
        Assert.True(back.For(GameKind.Euchre).RuleBool(EuchreRules.StickTheDealer));
        Assert.False(back.Announcements.IsOn(AnnouncementCategory.Moves));
    }
}

public class PassSpeechTests
{
    /// <summary>After the player's pass, the others' passes and what was passed to the player are sent, then the turn.</summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(4UL)]
    [InlineData(5UL)]
    [InlineData(6UL)]
    public void The_others_passes_are_spoken_after_the_players_own(ulong seed)
    {
        var (s, _, speaker, _) = Table.Make<HeartsSession>(GameKind.Hearts, seed);
        s.Start();
        foreach (var c in s.Hand.Take(3).ToList()) s.Activate(c);
        speaker.Spoken.Clear();
        s.Primary.Action();
        var all = speaker.All;
        Assert.Contains("You passed", all, StringComparison.Ordinal);
        Assert.Contains("passed you", all, StringComparison.Ordinal);
        Assert.Contains("Your turn", all, StringComparison.Ordinal);
    }
}

/// <summary>The On the table list holds the cards one line each, so it can be arrowed card by card.</summary>
public class OnTheTableTests
{
    [Fact]
    public void Hearts_lists_each_card_of_the_trick_and_of_the_last_trick()
    {
        var (s, _, _, _) = Table.Make<HeartsSession>(GameKind.Hearts, 22);
        s.Start();
        foreach (var c in s.Hand.Take(3).ToList()) s.Activate(c);
        s.Primary.Action();
        // At Immediate pace the computer players have played up to the player's turn.
        var trick = s.State.Trick;
        var lines = s.TableLines;
        Assert.Equal(trick.Count, lines.Count(l => l.Contains(" led the ", StringComparison.Ordinal) || l.Contains(" played the ", StringComparison.Ordinal)));
        if (trick.Count > 0) Assert.Equal($"{s.State.Players[trick[0].Seat].Name} led the {trick[0].Card.Name}", lines[0]);
        if (trick.Count > 1) Assert.Single(lines, l => l.EndsWith(", winning so far", StringComparison.Ordinal));

        // Finish the trick: the last trick is a heading line, then one line per card.
        var card = s.Hand.First(h => h.Playable);
        s.Activate(card);
        lines = s.TableLines;
        var last = s.State.LastTrick!;
        var at = lines.ToList().FindIndex(l => l.StartsWith("Last trick: ", StringComparison.Ordinal));
        Assert.True(at >= 0, string.Join(" | ", lines));
        Assert.Contains(" took it", lines[at], StringComparison.Ordinal);
        Assert.Equal(4, lines.Skip(at + 1).Take(4).Count(l => last.Cards.Any(p => l.Contains(p.Card.Name, StringComparison.Ordinal))));
        Assert.Contains(lines.Skip(at + 1), l => l.StartsWith("You, ", StringComparison.Ordinal));
    }

    [Fact]
    public void Sheephead_lists_the_cards_on_the_table()
    {
        var (s, _, _, _) = Table.Make<SheepheadSession>(GameKind.Sheephead, 7);
        s.Start();
        for (var guard = 0; guard < 200 && s.State.Phase != TheCardPlace.Sheephead.SheepheadPhase.Play; guard++)
        {
            var pass = s.Secondary.FirstOrDefault(c => c.Title == "Pass");
            if (pass is not null) { pass.Action(); continue; }
            var action = TheCardPlace.Sheephead.SheepheadAI.Decide(s.State, s.Me, new RandomSource(3));
            if (action is TheCardPlace.Sheephead.SheepheadAction.Bury b)
            {
                foreach (var c in b.Cards) s.Activate(Table.CardIn(s, c));
                s.Primary.Action();
            }
            else if (action is TheCardPlace.Sheephead.SheepheadAction.Pick) s.Secondary.First(c => c.Title.StartsWith("Pick", StringComparison.Ordinal)).Action();
            else break;
        }
        Assert.Equal(TheCardPlace.Sheephead.SheepheadPhase.Play, s.State.Phase);
        var played = s.State.Trick.Count;
        Assert.Equal(played, s.TableLines.Count(l => l.Contains(" led the ", StringComparison.Ordinal) || l.Contains(" played the ", StringComparison.Ordinal)));
    }
}
