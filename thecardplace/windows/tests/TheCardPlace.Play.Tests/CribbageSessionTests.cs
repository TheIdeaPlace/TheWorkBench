using TheCardPlace.Cribbage;

namespace TheCardPlace.Play.Tests;

/// <summary>
/// Cribbage played the way a person plays it: through the hand and the controls the screen draws,
/// never by calling the engine. The computer chooses for the person's seat too, so a whole game
/// runs, but every choice goes in by Activate, the primary button and the N key.
/// </summary>
public class CribbageSessionTests
{
    private static AppSettings To(int target)
    {
        var settings = Table.Settings(GameKind.Cribbage);
        settings.For(GameKind.Cribbage).Rules[CribbageRules.Target.Key] = target.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return settings;
    }

    /// <summary>One move for seat 0, made through the session's controls.</summary>
    private static void MoveFor(CribbageSession s)
    {
        var action = CribbageAI.Decide(s.State, s.Me, new RandomSource(99)) ?? throw new Xunit.Sdk.XunitException("nothing to decide: " + s.Status);
        switch (action)
        {
            case CribbageAction.Cut:
                Assert.Equal("Cut for deal", s.Primary.Title);
                s.Advance();
                break;
            case CribbageAction.Discard d:
                foreach (var c in d.Cards) s.Activate(Table.CardIn(s, c));
                Assert.True(s.Primary.Enabled, "Throw should be ready with two chosen");
                Assert.Equal("Throw 2 of 2 to the crib", s.Primary.Title);
                s.Primary.Action();
                break;
            case CribbageAction.Play p:
                Assert.True(Table.CardIn(s, p.Card).Playable);
                s.Activate(Table.CardIn(s, p.Card));
                break;
            case CribbageAction.Go:
                Assert.Equal("Go", s.Primary.Title);
                s.Advance();
                break;
            case CribbageAction.Next:
                Assert.Equal("Next", s.Primary.Title);
                s.Advance();
                break;
            default:
                throw new Xunit.Sdk.XunitException("unexpected " + action);
        }
    }

    /// <summary>What must hold whenever the table stops for the player.</summary>
    private static void CheckTable(CribbageSession s)
    {
        if (s.State.Phase == CribbagePhase.Play && s.IsMyTurn)
        {
            var hand = s.Hand;
            // Go exactly when nothing fits.
            Assert.Equal(hand.All(h => !h.Playable), s.Primary.Title == "Go");
            foreach (var h in hand.Where(h => !h.Playable))
                Assert.Equal(CribbageGame.WhyNot(s.State, s.Me, h.Card), h.Reason);
            Assert.StartsWith($"The count is {s.State.Count}.", s.TableNote, StringComparison.Ordinal);
        }
        Assert.Equal(2, s.Seats.Count);
        Assert.Equal("across", s.Seats[1].Position);
        Assert.Equal(2, s.Scores.Rows.Count);
    }

    private static void PlayToTheEnd(CribbageSession s, int guardLimit = 4000)
    {
        for (var guard = 0; guard < guardLimit && s.State.Phase != CribbagePhase.GameOver; guard++)
        {
            CheckTable(s);
            if (s.State.Phase == CribbagePhase.RoundOver)
            {
                Assert.Equal("Deal the next hand", s.Primary.Title);
                s.Advance();
            }
            else
            {
                Assert.True(CribbageReview.IsMyMove(s.State, s.Me) && CribbageGame.SeatToAct(s.State) == s.Me,
                    "the table stopped without it being the player's move: " + s.Status);
                MoveFor(s);
            }
        }
        Assert.Equal(CribbagePhase.GameOver, s.State.Phase);
    }

    [Theory]
    [InlineData(1UL, 61)]
    [InlineData(2UL, 61)]
    [InlineData(3UL, 61)]
    [InlineData(4UL, 61)]
    [InlineData(5UL, 121)]
    [InlineData(6UL, 121)]
    public void A_whole_game_through_the_controls(ulong seed, int target)
    {
        var (s, _, speaker, _) = Table.Make<CribbageSession>(GameKind.Cribbage, seed, settings: To(target));
        s.Start();
        Assert.Equal(target, s.State.Target);
        PlayToTheEnd(s);
        Assert.Contains(s.State.Players, p => p.Score >= target);
        Assert.Equal("Deal another game", s.Primary.Title);
        Assert.Equal(["new"], s.Secondary.Select(c => c.Id));
        Assert.NotEmpty(s.History.Rows);
        Assert.Contains(s.Log, l => l.Contains(" hand: ", StringComparison.Ordinal));
        Assert.Contains(speaker.Spoken, x => x.Text.Contains("your turn", StringComparison.Ordinal));
        Assert.Equal("Games won", s.Scores.Columns[^1]);
        Assert.False(s.InProgress);

        // Deal another game: straight into the next game's first hand, the tally kept.
        s.Advance();
        Assert.Equal(CribbagePhase.Discard, s.State.Phase);
        Assert.Equal(1, s.State.GamesWon.Sum());
        Assert.All(s.State.Players, p => Assert.Equal(0, p.Score));
        Assert.Empty(s.History.Rows);
        Assert.True(s.InProgress);
    }

    [Fact]
    public void The_cut_is_the_players_to_make()
    {
        var (s, _, _, heard) = Table.Make<CribbageSession>(GameKind.Cribbage, 7);
        s.Start();
        Assert.Equal(CribbagePhase.CutForDeal, s.State.Phase);
        Assert.Empty(s.Hand);
        Assert.Equal("Cut for deal", s.Primary.Title);
        Assert.Equal('n', s.Primary.Key);
        Assert.False(s.InProgress);
        for (var guard = 0; guard < 10 && s.State.Phase == CribbagePhase.CutForDeal; guard++) s.Advance();
        Assert.Equal(CribbagePhase.Discard, s.State.Phase);
        Assert.Equal(6, s.Hand.Count);
        Assert.Contains(heard, a => a.Text.Contains("Your six cards: ", StringComparison.Ordinal));
        Assert.StartsWith(s.State.Dealer == 0 ? "Your crib." : "Ruth’s crib.", s.TableNote, StringComparison.Ordinal);
    }

    private static CribbageSession AtTheThrow(ulong seed, out List<Announcement> heard)
    {
        var (s, _, _, h) = Table.Make<CribbageSession>(GameKind.Cribbage, seed);
        heard = h;
        s.Start();
        while (s.State.Phase == CribbagePhase.CutForDeal) s.Advance();
        Assert.Equal(CribbagePhase.Discard, s.State.Phase);
        return s;
    }

    [Fact]
    public void The_throw_chooses_and_unchooses_and_refuses_a_third()
    {
        var s = AtTheThrow(5, out var heard);
        Assert.False(s.Primary.Enabled);
        Assert.Equal("Throw 0 of 2 to the crib", s.Primary.Title);
        var cards = s.Hand.Take(3).ToList();
        s.Activate(cards[0]);
        Assert.Equal($"{cards[0].Card.Name} chosen. One of two chosen.", heard[^1].Text);
        Assert.Contains(", chosen", Table.CardIn(s, cards[0].Card).AccessibleName, StringComparison.Ordinal);
        Assert.Equal("Throw 1 of 2 to the crib", s.Primary.Title);
        s.Activate(Table.CardIn(s, cards[0].Card));
        Assert.Equal($"{cards[0].Card.Name} removed. No cards chosen yet.", heard[^1].Text);
        Assert.False(Table.CardIn(s, cards[0].Card).Chosen);

        s.Activate(Table.CardIn(s, cards[0].Card));
        s.Activate(Table.CardIn(s, cards[1].Card));
        Assert.EndsWith("Two of two chosen. Choose Throw to send them to the crib.", heard[^1].Text, StringComparison.Ordinal);
        var before = Snapshot.Json(s.State);
        s.Activate(Table.CardIn(s, cards[2].Card));
        Assert.True(heard[^1].Forced);
        Assert.StartsWith("Two cards are already chosen: ", heard[^1].Text, StringComparison.Ordinal);
        Assert.False(Table.CardIn(s, cards[2].Card).Chosen);
        Assert.Equal(before, Snapshot.Json(s.State));

        Assert.True(s.Primary.Enabled);
        s.Primary.Action();
        Assert.True(s.State.HasDiscarded(0));
        Assert.Equal(4, s.Hand.Count);
        Assert.DoesNotContain(s.Hand, h => h.Card == cards[0].Card || h.Card == cards[1].Card);
        Assert.All(s.Hand, h => Assert.False(h.Chosen));
        // The computer has thrown too, the starter is up and the play has begun.
        Assert.NotNull(s.State.Starter);
        Assert.Contains(s.State.Phase, new[] { CribbagePhase.Play, CribbagePhase.GameOver });
    }

    [Fact]
    public void Throw_with_one_chosen_says_so_and_changes_nothing()
    {
        var s = AtTheThrow(9, out var heard);
        s.Activate(s.Hand[0]);
        var before = Snapshot.Json(s.State);
        s.Primary.Action();
        Assert.True(heard[^1].Forced);
        Assert.Equal("Choose exactly two cards to throw. One of two chosen.", heard[^1].Text);
        Assert.Equal(before, Snapshot.Json(s.State));
        // N with the throw not ready reads the status rather than doing anything.
        s.Advance();
        Assert.Equal(before, Snapshot.Json(s.State));
    }

    /// <summary>Plays on through the controls until <paramref name="until"/> holds on the player's turn in the play.</summary>
    private static bool PlayUntil(CribbageSession s, Func<CribbageSession, bool> until, int limit = 4000)
    {
        for (var guard = 0; guard < limit && s.State.Phase != CribbagePhase.GameOver; guard++)
        {
            if (s.State.Phase == CribbagePhase.Play && s.IsMyTurn && until(s)) return true;
            if (s.State.Phase == CribbagePhase.RoundOver) s.Advance();
            else MoveFor(s);
        }
        return false;
    }

    [Theory]
    [InlineData(8UL)]
    [InlineData(12UL)]
    public void A_card_over_thirty_one_says_why_and_nothing_changes(ulong seed)
    {
        var (s, _, _, heard) = Table.Make<CribbageSession>(GameKind.Cribbage, seed);
        s.Start();
        Assert.True(PlayUntil(s, x => x.Hand.Any(h => !h.Playable)), "never held a card that would pass thirty-one");
        var bad = s.Hand.First(h => !h.Playable);
        Assert.Contains("cannot be played, it would take the count to ", bad.AccessibleName, StringComparison.Ordinal);
        Assert.EndsWith(", past thirty-one", bad.AccessibleName, StringComparison.Ordinal);
        var before = Snapshot.Json(s.State);
        s.Activate(bad);
        Assert.Equal(before, Snapshot.Json(s.State));
        Assert.True(heard[^1].Forced);
        Assert.Equal($"{bad.Card.Name} cannot be played: {CribbageGame.WhyNot(s.State, 0, bad.Card)}.", heard[^1].Text);
    }

    [Theory]
    [InlineData(3UL)]
    [InlineData(10UL)]
    public void Go_is_offered_exactly_when_nothing_fits(ulong seed)
    {
        var (s, _, speaker, _) = Table.Make<CribbageSession>(GameKind.Cribbage, seed, settings: To(121));
        s.Start();
        var sawPlayable = false;
        Assert.True(PlayUntil(s, x =>
        {
            if (x.Hand.Any(h => h.Playable))
            {
                Assert.NotEqual("Go", x.Primary.Title);
                Assert.False(x.Primary.Enabled);
                sawPlayable = true;
                return false;
            }
            return true;
        }), "never had to say go");
        Assert.True(sawPlayable);
        Assert.All(s.Hand, h => Assert.False(h.Playable));
        Assert.Equal("Go", s.Primary.Title);
        Assert.Equal('n', s.Primary.Key);
        Assert.True(s.Primary.Enabled);
        Assert.Contains("You cannot play. Say go.", speaker.All, StringComparison.Ordinal);
        s.Advance();
        Assert.Contains("Kelly says go.", s.Log);
    }

    [Fact]
    public void The_count_is_stepped_by_the_player_and_read_in_its_parts()
    {
        var (s, _, speaker, _) = Table.Make<CribbageSession>(GameKind.Cribbage, 4, settings: To(121));
        s.Start();
        for (var guard = 0; guard < 400 && !(s.State.Phase == CribbagePhase.Count && s.IsMyTurn); guard++)
        {
            if (s.State.Phase == CribbagePhase.RoundOver) s.Advance();
            else MoveFor(s);
        }
        Assert.Equal(CribbagePhase.Count, s.State.Phase);
        Assert.Equal("Next", s.Primary.Title);
        Assert.Equal('n', s.Primary.Key);
        Assert.Contains("yours to count", speaker.Spoken[^1].Text, StringComparison.Ordinal);
        Assert.Contains(" with the ", s.TableNote, StringComparison.Ordinal);
        Assert.Empty(s.Hand);
        var stage = s.State.CountStage;
        s.Advance();
        Assert.True(s.State.CountStage > stage || s.State.Phase is CribbagePhase.RoundOver or CribbagePhase.GameOver);
        // Read in its parts: the cards, the starter, then each way it scores and the total.
        var counted = s.Log.First(l => l.StartsWith("Kelly’s hand: ", StringComparison.Ordinal) || l.StartsWith("Kelly’s crib: ", StringComparison.Ordinal));
        Assert.Contains(" with the ", counted, StringComparison.Ordinal);
        Assert.Contains(s.Log, l => l == s.Reviews.First(r => r.Key == 'l').Text());
    }

    [Fact]
    public void Review_keys_are_distinct_and_do_not_collide_with_the_table_keys()
    {
        var (s, _, _, _) = Table.Make<CribbageSession>(GameKind.Cribbage, 1);
        var keys = s.Reviews.Where(r => r.Key is not null).Select(r => r.Key!.Value).ToList();
        Assert.Equal(['h', 't', 'l', 's', 'p', 'c', 'o', 'w'], keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.DoesNotContain('n', keys);
        Assert.DoesNotContain('r', keys);
        Assert.DoesNotContain('g', keys);
        Assert.DoesNotContain(keys, char.IsDigit);
        Assert.Contains(s.Reviews, r => r.Key is null && r.Title == "Status");
        s.Start();
        Assert.All(s.Reviews, r => Assert.False(string.IsNullOrWhiteSpace(r.Text())));
        Assert.Equal("Kelly, you. Ruth, computer.", s.Reviews.First(r => r.Key == 'w').Text());
    }

    [Fact]
    public void Scores_read_as_the_pegboard()
    {
        var (s, _, _, _) = Table.Make<CribbageSession>(GameKind.Cribbage, 2, settings: To(61));
        s.Start();
        Assert.Equal("Kelly (you), score 0, to go 61", s.Scores.RowName(0));
        Assert.Equal("Ruth, score 0, to go 61", s.Scores.RowName(1));
    }

    [Fact]
    public void Starting_over_keeps_the_games_won_and_takes_the_new_target()
    {
        var (s, _, _, _) = Table.Make<CribbageSession>(GameKind.Cribbage, 3, settings: To(61));
        s.Start();
        PlayToTheEnd(s);
        var won = s.State.GamesWon.ToList();
        s.Settings.For(GameKind.Cribbage).Rules[CribbageRules.Target.Key] = "121";
        Table.Control(s, "new").Action();
        Assert.Equal(CribbagePhase.CutForDeal, s.State.Phase);
        Assert.Equal(121, s.State.Target);
        Assert.Equal(won, s.State.GamesWon);
        Assert.Equal(2, s.State.GameNumber);
        Assert.Contains("New game.", s.Log);
        Assert.Null(s.State.Dealer);

        // Part way through, a new game starts again with the same tally.
        while (s.State.Phase == CribbagePhase.CutForDeal) s.Advance();
        s.Activate(s.Hand[0]);
        s.NewGame();
        Assert.Equal(won, s.State.GamesWon);
        Assert.Equal(2, s.State.GameNumber);
        Assert.Equal(CribbagePhase.CutForDeal, s.State.Phase);
        while (s.State.Phase == CribbagePhase.CutForDeal) s.Advance();
        Assert.All(s.Hand, h => Assert.False(h.Chosen));
    }

    [Fact]
    public void Deal_another_game_takes_the_new_target()
    {
        var (s, _, _, _) = Table.Make<CribbageSession>(GameKind.Cribbage, 6, settings: To(61));
        s.Start();
        PlayToTheEnd(s);
        s.Settings.For(GameKind.Cribbage).Rules[CribbageRules.Target.Key] = "121";
        s.Advance();
        Assert.Equal(121, s.State.Target);
        Assert.Equal(CribbagePhase.Discard, s.State.Phase);
    }

    [Fact]
    public async Task At_wait_for_me_the_computer_waits_and_a_card_out_of_turn_says_whose_move_it_is()
    {
        var (s, _, _, heard) = Table.Make<CribbageSession>(GameKind.Cribbage, 2, Pace.WaitForMe);
        s.Start();
        while (s.State.Phase == CribbagePhase.CutForDeal) s.Advance();
        // The throw happens at once, whoever deals: no pause before the computer's two.
        MoveFor(s);
        Assert.True(s.State.HasDiscarded(1));
        Assert.Equal(CribbagePhase.Play, s.State.Phase);
        if (s.IsMyTurn) MoveFor(s);
        Assert.False(s.IsMyTurn);
        Assert.True(s.Gate.IsWaiting);
        Assert.Equal("Waiting for Ruth", s.Primary.Title);
        Assert.Equal("idle", s.Primary.Id);

        var before = Snapshot.Json(s.State);
        s.Activate(s.Hand[0]);
        Assert.True(heard[^1].Forced);
        Assert.Equal("Not your turn. Waiting for Ruth.", heard[^1].Text);
        Assert.Equal(before, Snapshot.Json(s.State));

        var pile = s.State.Pile.Count;
        s.Advance();
        await s.BotsTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(pile + 1, s.State.Pile.Count);
        Assert.True(s.IsMyTurn);
        s.Stop();
    }
}
