using System.Globalization;
using TheCardPlace.Sheephead;

namespace TheCardPlace.Play.Tests;

/// <summary>
/// Sheephead played the way a person plays it: through the hand and the controls the screen
/// draws, never by calling the engine. The computer chooses for the person's seat too, but every
/// choice goes in by the Pick and Pass controls, Activate and the primary button.
/// </summary>
public class SheepheadSessionTests
{
    private static AppSettings Rules(int players, string allPass = "leaster", bool redealDoubler = false)
    {
        var settings = Table.Settings(GameKind.Sheephead);
        var rules = settings.For(GameKind.Sheephead).Rules;
        rules[SheepheadRules.Players.Key] = players.ToString(CultureInfo.InvariantCulture);
        rules[SheepheadRules.AllPass.Key] = allPass;
        rules[SheepheadRules.RedealDoubler.Key] = redealDoubler ? "true" : "false";
        return settings;
    }

    private static SheepheadSession Make(ulong seed, AppSettings settings) =>
        Table.Make<SheepheadSession>(GameKind.Sheephead, seed, settings: settings).Session;

    /// <summary>One move for seat 0, made through the session's controls.</summary>
    private static void MoveFor(SheepheadSession s, bool alwaysPass = false, bool alwaysPick = false)
    {
        var action = SheepheadAI.Decide(s.State, s.Me, new RandomSource(99)) ?? throw new Xunit.Sdk.XunitException("nothing to decide: " + s.Status);
        if (action is SheepheadAction.Pick or SheepheadAction.Pass && (alwaysPass || alwaysPick))
            action = alwaysPick ? new SheepheadAction.Pick() : new SheepheadAction.Pass();
        switch (action)
        {
            case SheepheadAction.Pick:
                Table.Control(s, "pick").Action();
                break;
            case SheepheadAction.Pass:
                Table.Control(s, "pass").Action();
                break;
            case SheepheadAction.Bury b:
                Assert.False(s.Primary.Enabled);
                foreach (var c in b.Cards) s.Activate(Table.CardIn(s, c));
                Assert.Equal("Bury 2 of 2", s.Primary.Title);
                Assert.True(s.Primary.Enabled, "Bury should be ready with two chosen");
                s.Primary.Action();
                break;
            case SheepheadAction.Play p:
                Assert.True(Table.CardIn(s, p.Card).Playable);
                s.Activate(Table.CardIn(s, p.Card));
                break;
            default:
                throw new Xunit.Sdk.XunitException("unexpected " + action);
        }
    }

    /// <summary>Plays through the controls until <paramref name="hands"/> hands are scored.</summary>
    private static void PlayHands(SheepheadSession s, int hands, Action? check = null, bool alwaysPass = false)
    {
        for (var guard = 0; guard < 5000 && s.State.History.Count < hands; guard++)
        {
            check?.Invoke();
            if (s.State.Phase == SheepheadPhase.HandOver)
            {
                Assert.Equal("Deal the next hand", s.Primary.Title);
                Assert.False(s.InProgress);
                s.Advance();
            }
            else
            {
                Assert.True(s.IsMyTurn, "the table stopped without it being the player's move: " + s.Status);
                Assert.True(s.InProgress);
                MoveFor(s, alwaysPass);
            }
        }
        Assert.True(s.State.History.Count >= hands, $"only {s.State.History.Count} hands were played");
    }

    [Theory]
    [InlineData(3, "leaster", 1UL)]
    [InlineData(4, "leaster", 2UL)]
    [InlineData(5, "leaster", 3UL)]
    [InlineData(6, "leaster", 4UL)]
    [InlineData(3, "redeal", 5UL)]
    [InlineData(4, "redeal", 6UL)]
    [InlineData(5, "redeal", 7UL)]
    [InlineData(6, "redeal", 8UL)]
    public void Six_hands_through_the_controls(int players, string allPass, ulong seed)
    {
        var settings = Rules(players, allPass);
        var (s, _, speaker, _) = Table.Make<SheepheadSession>(GameKind.Sheephead, seed, settings: settings);
        s.Start();
        PlayHands(s, 6);
        Assert.Equal(players, s.Seats.Count);
        Assert.Equal(players, s.Controllers.Length);
        Assert.Equal(players, s.Scores.Rows.Count);
        Assert.Equal(6, s.History.Rows.Count);
        Assert.All(s.State.History, h => Assert.Empty(h.Problems));
        Assert.Equal(0, s.State.Players.Sum(p => p.Score));
        Assert.Contains(speaker.Spoken, x => x.Id == "TheCardPlace.Turn");
        // The blind and the bury are said once at the end of each hand, in the hand summary,
        // and kept in On the table; the second read-out is gone.
        Assert.Contains(speaker.Spoken, x => x.Text.Contains("The blind held ", StringComparison.Ordinal));
        Assert.DoesNotContain(speaker.Spoken, x => x.Text.StartsWith("The blind, and what", StringComparison.Ordinal));
        if (allPass == "redeal") Assert.DoesNotContain(s.State.History, h => h.IsLeaster);
        Assert.Equal(["", "left"], s.Seats.Take(2).Select(x => x.Position));
        Assert.Equal("right", s.Seats[^1].Position);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void When_everyone_passes_a_leaster_is_played(int players)
    {
        var s = Make(21, Rules(players, "leaster"));
        s.Start();
        var sawNote = false;
        for (var guard = 0; guard < 40 && !s.State.History.Any(h => h.IsLeaster); guard++)
        {
            var target = s.State.History.Count + 1;
            PlayHands(s, target, () =>
            {
                if (s.State.IsLeaster && s.State.Phase == SheepheadPhase.Play)
                {
                    Assert.StartsWith("Leaster", s.TableNote, StringComparison.Ordinal);
                    Assert.All(s.Seats, x => Assert.DoesNotContain("picker", x.Detail, StringComparison.Ordinal));
                    sawNote = true;
                }
            }, alwaysPass: true);
        }
        var leaster = s.State.History.First(h => h.IsLeaster);
        Assert.True(sawNote);
        Assert.Equal("nobody, leaster", s.History.Rows[s.State.History.IndexOf(leaster)][1]);
        Assert.Contains(s.Log, l => l.StartsWith("Everyone passed. This hand is a leaster", StringComparison.Ordinal));
    }

    [Fact]
    public void When_everyone_passes_with_redeal_the_hand_is_dealt_again_and_doubled()
    {
        var s = Make(31, Rules(3, "redeal", redealDoubler: true));
        s.Start();
        var sawDoubled = false;
        for (var guard = 0; guard < 3000 && !(sawDoubled && s.State.History.Count >= 1); guard++)
        {
            if (s.State.RedealDoubler && s.State.Phase is SheepheadPhase.Pick or SheepheadPhase.Play)
            {
                Assert.Contains("Doubled: the redeal", s.TableNote, StringComparison.Ordinal);
                sawDoubled = true;
            }
            if (s.State.Phase == SheepheadPhase.HandOver) s.Advance();
            else MoveFor(s, alwaysPass: true);
        }
        Assert.True(sawDoubled, "no redeal happened");
        Assert.Contains(s.Log, l => l.StartsWith("Everyone passed. Redealing.", StringComparison.Ordinal));
        Assert.DoesNotContain(s.State.History, h => h.IsLeaster);
        Assert.True(s.State.HandNumber > s.State.History.Count, "a thrown-in hand still counts as a deal");
    }

    [Fact]
    public void Picking_offers_pick_and_pass_and_the_primary_only_says_what_to_do()
    {
        var (s, _, _, heard) = Table.Make<SheepheadSession>(GameKind.Sheephead, 4, settings: Rules(5));
        s.Start();
        for (var guard = 0; guard < 200 && !(s.State.Phase == SheepheadPhase.Pick && s.IsMyTurn); guard++)
        {
            if (s.State.Phase == SheepheadPhase.HandOver) s.Advance();
            else MoveFor(s);
        }
        Assert.Equal(SheepheadPhase.Pick, s.State.Phase);
        Assert.Equal(["pick", "pass"], s.Secondary.Select(c => c.Id));
        Assert.Equal(["Pick up the blind", "Pass"], s.Secondary.Select(c => c.Title));
        Assert.False(s.Primary.Enabled);
        Assert.Equal("idle", s.Primary.Id);
        Assert.All(s.Hand, h => Assert.True(h.Playable));
        Assert.True(s.InProgress);

        var before = Snapshot.Json(s.State);
        s.Advance();
        Assert.Equal(before, Snapshot.Json(s.State));
        Assert.Equal(SpokenText.Plain(s.Status), heard[^1].Text);
        s.Activate(s.Hand[0]);
        Assert.Equal(before, Snapshot.Json(s.State));
        Assert.StartsWith("Pick or pass first.", heard[^1].Text, StringComparison.Ordinal);
        Assert.True(heard[^1].Forced);

        Table.Control(s, "pass").Action();
        Assert.Contains(s.Log, l => l == "Kelly passes.");
        if (!(s.State.Phase == SheepheadPhase.Pick && s.IsMyTurn)) Assert.Empty(s.Secondary);
    }

    /// <summary>Plays on, picking whenever asked, until the player is burying.</summary>
    private static void UntilIBury(SheepheadSession s)
    {
        for (var guard = 0; guard < 2000 && !(s.State.Phase == SheepheadPhase.Bury && s.State.Picker == 0); guard++)
        {
            if (s.State.Phase == SheepheadPhase.HandOver) s.Advance();
            else MoveFor(s, alwaysPick: true);
        }
        Assert.Equal(SheepheadPhase.Bury, s.State.Phase);
    }

    [Fact]
    public void The_bury_chooses_and_unchooses_and_refuses_a_third()
    {
        var (s, _, _, heard) = Table.Make<SheepheadSession>(GameKind.Sheephead, 5, settings: Rules(5));
        s.Start();
        UntilIBury(s);
        Assert.Equal("Bury 0 of 2", s.Primary.Title);
        Assert.False(s.Primary.Enabled);
        Assert.Empty(s.Secondary);
        // The blind is at the front of the hand and says so.
        Assert.EndsWith(", from the blind", s.Hand[0].Description.Split(", the partner card")[0], StringComparison.Ordinal);

        var cards = s.Hand.Take(3).Select(h => h.Card).ToList();
        s.Activate(Table.CardIn(s, cards[0]));
        Assert.Equal($"One of two chosen.", heard[^1].Text);
        Assert.Contains(", chosen", Table.CardIn(s, cards[0]).AccessibleName, StringComparison.Ordinal);
        Assert.Equal("Bury 1 of 2", s.Primary.Title);

        s.Activate(Table.CardIn(s, cards[0]));
        Assert.Equal($"Removed. No cards chosen yet.", heard[^1].Text);
        Assert.False(Table.CardIn(s, cards[0]).Chosen);

        // Bury before two are chosen: refused, and the table is unchanged.
        s.Activate(Table.CardIn(s, cards[0]));
        var before = Snapshot.Json(s.State);
        s.Primary.Action();
        Assert.Equal(before, Snapshot.Json(s.State));
        Assert.StartsWith("Choose exactly two cards to bury.", heard[^1].Text, StringComparison.Ordinal);

        s.Activate(Table.CardIn(s, cards[1]));
        Assert.EndsWith("Two of two chosen.", heard[^2].Text, StringComparison.Ordinal);
        Assert.Equal(("Choose Bury to put them down.", AnnouncementCategory.Hint), (heard[^1].Text, heard[^1].Category));
        s.Activate(Table.CardIn(s, cards[2]));
        Assert.True(heard[^1].Forced);
        Assert.Equal($"Two cards are already chosen, {cards[0].Name} and {cards[1].Name}. Remove one first.", heard[^1].Text);
        Assert.False(Table.CardIn(s, cards[2]).Chosen);

        Assert.Equal("Bury 2 of 2", s.Primary.Title);
        Assert.True(s.Primary.Enabled);
        s.Primary.Action();
        Assert.Equal(cards.Take(2), s.State.Buried);
        Assert.NotEqual(SheepheadPhase.Bury, s.State.Phase);
        Assert.DoesNotContain(s.Hand, h => cards.Take(2).Contains(h.Card));
        Assert.Contains(s.Log, l => l.StartsWith("You buried ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_card_that_cannot_be_played_says_why_and_nothing_changes()
    {
        var (s, _, _, heard) = Table.Make<SheepheadSession>(GameKind.Sheephead, 8, settings: Rules(3));
        s.Start();
        for (var guard = 0; guard < 2000 && !(s.IsMyTurn && s.State.Phase == SheepheadPhase.Play && s.Hand.Any(h => !h.Playable)); guard++)
        {
            if (s.State.Phase == SheepheadPhase.HandOver) s.Advance();
            else MoveFor(s);
        }
        var bad = s.Hand.First(h => !h.Playable);
        Assert.Matches("^you must follow (trump|clubs|spades|hearts)$", bad.Reason!);
        Assert.Contains(", cannot be played, you must follow ", bad.AccessibleName, StringComparison.Ordinal);
        Assert.NotNull(bad.Badge);
        var before = Snapshot.Json(s.State);
        s.Activate(bad);
        Assert.Equal(before, Snapshot.Json(s.State));
        Assert.True(heard[^1].Forced);
        Assert.Equal(SpokenText.Plain($"{bad.Card.Name} cannot be played: {bad.Reason}."), heard[^1].Text);
    }

    [Fact]
    public void The_seats_show_the_trick_and_whose_turn()
    {
        var s = Make(12, Rules(5));
        s.Start();
        for (var guard = 0; guard < 2000 && !(s.IsMyTurn && s.State.Phase == SheepheadPhase.Play && s.State.Trick.Count >= 2); guard++)
        {
            if (s.State.Phase == SheepheadPhase.HandOver) s.Advance();
            else MoveFor(s);
        }
        var seats = s.Seats;
        var leader = s.State.Trick[0].Player;
        Assert.StartsWith("led", seats[leader].PlayedNote, StringComparison.Ordinal);
        Assert.Single(seats, x => x.PlayedNote?.Contains("winning so far", StringComparison.Ordinal) == true);
        Assert.True(seats[0].IsTurn);
        Assert.True(seats[0].IsMe);
        Assert.Single(seats, x => x.IsTurn);
        Assert.All(seats, x => Assert.Matches("[0-9]+ points?$", x.Detail));
        Assert.Contains("dealer", seats[s.State.Dealer!.Value].Detail, StringComparison.Ordinal);
        if (s.State.Picker is { } picker) Assert.Contains("picker", seats[picker].Detail, StringComparison.Ordinal);
        Assert.Equal("left", seats[1].Position);
        Assert.Equal("second to your left", seats[2].Position);
        Assert.Equal("right", seats[4].Position);
    }

    /// <summary>
    /// The jack of diamonds is a secret. Until it is played, nothing on the screen may tell the
    /// player who holds it, or that the picker is alone, unless it is the player's own seat.
    /// </summary>
    [Theory]
    [InlineData(4, 41UL)]
    [InlineData(5, 42UL)]
    [InlineData(6, 43UL)]
    public void The_secret_partner_is_never_shown_before_it_is_revealed(int players, ulong seed)
    {
        var s = Make(seed, Rules(players));
        s.Start();
        int hidden = 0, shown = 0;
        PlayHands(s, 8, () =>
        {
            var st = s.State;
            if (st.Phase != SheepheadPhase.Play || st.IsLeaster || st.Picker is not { } picker) return;
            var text = string.Join("\n", s.Seats.Select(x => x.AccessibleName)) + "\n" + s.TableNote + "\n" + s.Status;
            if (!st.PartnerRevealed)
            {
                if (st.Partner is { } partner && partner != s.Me)
                {
                    hidden++;
                    Assert.DoesNotContain("partner", s.Seats[partner].Detail, StringComparison.Ordinal);
                    Assert.DoesNotContain("Partner: " + st.Players[partner].Name, text, StringComparison.Ordinal);
                    Assert.DoesNotContain(st.Players[partner].Name + " is the partner", s.Reviews.First(r => r.Key == 'p').Text(), StringComparison.Ordinal);
                    Assert.DoesNotContain("partner", s.Scores.Rows[partner][1], StringComparison.Ordinal);
                }
                if (st.Alone && picker != s.Me)
                {
                    Assert.DoesNotContain("alone", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("alone", s.Scores.Rows[picker][1], StringComparison.Ordinal);
                }
                Assert.Contains("Partner card: Jack of Diamonds", s.TableNote, StringComparison.Ordinal);
            }
            else if (st.Partner is { } partner)
            {
                shown++;
                Assert.Contains("partner", s.Seats[partner].Detail, StringComparison.Ordinal);
                Assert.Contains("Partner: " + (partner == s.Me ? "you" : st.Players[partner].Name), s.TableNote, StringComparison.Ordinal);
            }
        });
        Assert.True(hidden > 0, "the partner was never hidden from the player");
        Assert.True(shown > 0, "the partner was never revealed");
    }

    [Fact]
    public void Changing_the_player_count_then_new_game_sets_a_table_of_that_size()
    {
        var settings = Rules(5);
        var (s, _, _, _) = Table.Make<SheepheadSession>(GameKind.Sheephead, 9, settings: settings);
        s.Start();
        Assert.Equal(5, s.Seats.Count);
        PlayHands(s, 1);

        settings.For(GameKind.Sheephead).Rules[SheepheadRules.Players.Key] = "3";
        s.NewGame();
        Assert.Equal(3, s.State.Seats);
        Assert.Equal(3, s.Controllers.Length);
        Assert.Equal(3, s.Seats.Count);
        Assert.Equal(["", "left", "right"], s.Seats.Select(x => x.Position));
        Assert.Contains("New game.", s.Log);
        Assert.Equal(12, s.HandCapacity);
        PlayHands(s, 2);
        Assert.All(s.State.History, h => Assert.Equal(3, h.Names.Count));

        // Bigger, and in the middle of a hand.
        settings.For(GameKind.Sheephead).Rules[SheepheadRules.Players.Key] = "6";
        s.NewGame();
        Assert.Equal(6, s.State.Seats);
        Assert.Equal(6, s.Controllers.Length);
        Assert.Equal(6, s.Seats.Count);
        Assert.Equal(7, s.HandCapacity);
        Assert.Equal(6, s.Scores.Rows.Count);
        Assert.Empty(s.History.Rows);
        PlayHands(s, 2);
    }

    [Fact]
    public void The_bury_is_forgotten_by_a_new_game()
    {
        var s = Make(5, Rules(5));
        s.Start();
        UntilIBury(s);
        s.Activate(s.Hand[0]);
        Assert.Contains(s.Hand, h => h.Chosen);
        s.NewGame();
        UntilIBury(s);
        Assert.DoesNotContain(s.Hand, h => h.Chosen);
        Assert.Equal("Bury 0 of 2", s.Primary.Title);
    }

    [Fact]
    public void Review_keys_do_not_collide_with_the_table_keys()
    {
        var s = Make(1, Rules(5));
        s.Start();
        var keys = s.Reviews.Where(r => r.Key is not null).Select(r => r.Key!.Value).ToList();
        Assert.Equal(['h', 't', 'l', 's', 'p', 'c', 'o', 'w'], keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.DoesNotContain('n', keys);
        Assert.DoesNotContain('r', keys);
        Assert.DoesNotContain('g', keys);
        Assert.DoesNotContain(keys, char.IsDigit);
        Assert.Contains(s.Reviews, r => r.Title == "Status" && r.Key is null);
        Assert.All(s.Reviews, r => Assert.NotEmpty(r.Text()));
        Assert.StartsWith("Five players: Kelly (you)", s.Reviews.First(r => r.Key == 'w').Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void Score_rows_are_read_with_their_column_names()
    {
        var s = Make(4, Rules(4));
        s.Start();
        Assert.Matches("^Kelly, you, role [a-z, ]+, tricks 0, points this hand 0, score 0$", s.Scores.RowName(0));
        PlayHands(s, 1);
        Assert.Matches("^1, picker [^,]+(, leaster)?, partner [^,]+, result [a-z ]+, Kelly [+-]?[0-9]+", s.History.RowName(0));
    }

    [Fact]
    public void Numbers_are_written_the_same_in_any_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            // Swedish writes a minus sign that is not "-".
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            Assert.Contains('−', (-1).ToString(CultureInfo.CurrentCulture));
            {
                var s = Make(14, Rules(5));
                s.Start();
                PlayHands(s, 3);
                var text = string.Join("\n", s.History.Rows.SelectMany(r => r).Concat(s.Scores.Rows.SelectMany(r => r))
                    .Concat(s.Log).Concat(s.Reviews.Select(r => r.Text())).Append(s.Status).Append(s.TableNote));
                Assert.DoesNotContain('−', text);
                Assert.Contains(s.History.Rows, r => r.Skip(4).Any(v => v.StartsWith('-')));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
