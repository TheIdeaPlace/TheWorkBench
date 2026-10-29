using TheCardPlace.Euchre;

namespace TheCardPlace.Play.Tests;

/// <summary>
/// Euchre played the way a person plays it: through the bids beside the hand, the hand itself and
/// the primary button, never by calling the engine. The computer chooses for the person's seat
/// too, so whole games run, but every choice goes in through a control.
/// </summary>
public class EuchreSessionTests
{
    private static AppSettings Rules(bool stick = false, bool alone = true, int points = 10)
    {
        var settings = Table.Settings(GameKind.Euchre);
        var r = settings.For(GameKind.Euchre).Rules;
        r[EuchreRules.StickTheDealer.Key] = stick ? "true" : "false";
        r[EuchreRules.AllowAlone.Key] = alone ? "true" : "false";
        r[EuchreRules.PointsToWin.Key] = points.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return settings;
    }

    private static (EuchreSession Session, RecordingSpeaker Speaker, List<Announcement> Heard) Make(ulong seed, AppSettings settings)
    {
        var (s, _, speaker, heard) = Table.Make<EuchreSession>(GameKind.Euchre, seed, settings: settings);
        return (s, speaker, heard);
    }

    /// <summary>One move for seat 0, made through the session's controls. With <paramref name="alwaysPass"/>, every bid that may be a pass is one.</summary>
    private static void MoveFor(EuchreSession s, bool alwaysPass = false)
    {
        var action = EuchreAI.Decide(s.State, s.Me, new RandomSource(99)) ?? throw new Xunit.Sdk.XunitException("nothing to decide: " + s.Status);
        if (alwaysPass && s.State.Phase is EuchrePhase.Bid1 or EuchrePhase.Bid2 && s.Secondary.Any(c => c.Id == "pass"))
            action = new EuchreAction.Pass();
        if (s.State.Phase is EuchrePhase.Bid1 or EuchrePhase.Bid2) AssertOffersExactlyTheLegalBids(s);
        switch (action)
        {
            case EuchreAction.Pass:
                Table.Control(s, "pass").Action();
                break;
            case EuchreAction.OrderUp o:
                var order = Table.Control(s, o.Alone ? "order-alone" : "order");
                var verb = s.State.Dealer == s.Me ? "Pick it up" : "Order it up";
                Assert.Equal(o.Alone ? verb + " and go alone" : verb, order.Title);
                order.Action();
                break;
            case EuchreAction.CallSuit c:
                if (c.Alone)
                {
                    var toggle = Table.Control(s, "alone");
                    Assert.Equal(ControlKind.Toggle, toggle.Kind);
                    Assert.False(toggle.IsChecked);
                    toggle.OnToggle!(true);
                    Assert.True(Table.Control(s, "alone").IsChecked);
                }
                var call = Table.Control(s, "call-" + c.Suit.LowerName());
                Assert.Equal("Call " + c.Suit.LowerName(), call.Title);
                call.Action();
                break;
            case EuchreAction.Discard d:
                Assert.Equal("Put a card back", s.Primary.Title);
                Assert.Equal(EuchreGame.HandSize + 1, s.Hand.Count);
                s.Activate(Table.CardIn(s, d.Card));
                Assert.Equal(d.Card, s.State.Discard);
                break;
            case EuchreAction.Play p:
                Assert.True(Table.CardIn(s, p.Card).Playable);
                s.Activate(Table.CardIn(s, p.Card));
                break;
            default:
                throw new Xunit.Sdk.XunitException("unexpected " + action);
        }
    }

    /// <summary>The bids on offer are exactly the ones the engine would take from this seat now.</summary>
    private static void AssertOffersExactlyTheLegalBids(EuchreSession s)
    {
        Assert.True(s.IsMyTurn);
        var allowAlone = s.State.Config.AllowAlone;
        var expected = new List<string>();
        bool Takes(EuchreAction a) => EuchreGame.ApplyAction(Snapshot.Clone(s.State), s.Me, a, new RandomSource(1)).IsOk;
        if (allowAlone && s.State.Phase == EuchrePhase.Bid2) expected.Add("alone");
        if (Takes(new EuchreAction.OrderUp(false))) expected.Add("order");
        if (allowAlone && Takes(new EuchreAction.OrderUp(true))) expected.Add("order-alone");
        foreach (var suit in EuchreCards.Suits)
            if (Takes(new EuchreAction.CallSuit(suit, false))) expected.Add("call-" + suit.LowerName());
        if (Takes(new EuchreAction.Pass())) expected.Add("pass");
        Assert.Equal(expected, s.Secondary.Select(c => c.Id));
        Assert.All(s.Secondary, c => Assert.Null(c.Key));
        Assert.False(s.Primary.Enabled);
        Assert.Equal("idle", s.Primary.Id);
    }

    /// <summary>Plays on from wherever the table is until <paramref name="until"/> holds. False if the game ended first.</summary>
    private static bool PlayUntil(EuchreSession s, Func<EuchreSession, bool> until, bool alwaysPass = false)
    {
        for (var guard = 0; guard < 3000; guard++)
        {
            if (until(s)) return true;
            if (s.State.Phase == EuchrePhase.GameOver) return false;
            if (s.State.Phase == EuchrePhase.HandOver) s.Advance();
            else
            {
                Assert.True(s.IsMyTurn, "the table stopped without it being the player's move: " + s.Status);
                MoveFor(s, alwaysPass);
            }
        }
        return false;
    }

    /// <summary>A table, from the first seed that reaches the moment wanted.</summary>
    private static (EuchreSession Session, RecordingSpeaker Speaker, List<Announcement> Heard) Find(
        AppSettings settings, Func<EuchreSession, bool> until, bool alwaysPass = true)
    {
        for (var seed = 1UL; seed <= 300; seed++)
        {
            var t = Make(seed, settings);
            t.Session.Start();
            if (PlayUntil(t.Session, until, alwaysPass)) return t;
        }
        throw new Xunit.Sdk.XunitException("no seed reached the moment wanted");
    }

    private static bool MyBid(EuchreSession s, EuchrePhase phase, bool dealer) =>
        s.State.Phase == phase && s.IsMyTurn && (s.State.Dealer == s.Me) == dealer;

    [Theory]
    [InlineData(1UL, false, true)]
    [InlineData(2UL, true, true)]
    [InlineData(3UL, true, false)]
    [InlineData(4UL, false, false)]
    public void A_whole_game_through_the_controls(ulong seed, bool stick, bool alone)
    {
        var (s, speaker, _) = Make(seed, Rules(stick, alone));
        s.Start();
        Assert.False(PlayUntil(s, _ => false));
        Assert.Equal(EuchrePhase.GameOver, s.State.Phase);
        Assert.Equal("Start a new game", s.Primary.Title);
        Assert.Equal('n', s.Primary.Key);
        Assert.Equal(1, s.State.GamesWon.Sum());
        Assert.NotEmpty(s.Log);
        Assert.Contains(speaker.Spoken, x => x.Id == "TheCardPlace.Turn");
        Assert.Equal(4, s.Seats.Count);
        Assert.Equal(2, s.Scores.Rows.Count);
        Assert.Equal(s.State.History.Count, s.History.Rows.Count);
        Assert.NotEmpty(s.History.Rows);
        if (!alone) Assert.All(s.State.History, h => Assert.False(h.Alone));
        if (stick) Assert.All(s.State.History, h => Assert.False(h.Result.ThrownIn));
    }

    [Fact]
    public void Round_one_offers_order_it_up_alone_or_not_and_pass()
    {
        var (s, _, heard) = Find(Rules(), x => MyBid(x, EuchrePhase.Bid1, dealer: false));
        Assert.Equal(["order", "order-alone", "pass"], s.Secondary.Select(c => c.Id));
        Assert.Equal(["Order it up", "Order it up and go alone", "Pass"], s.Secondary.Select(c => c.Title));
        Assert.Equal("Order it up or pass", s.Primary.Title);
        Assert.Equal("Upcard: " + s.State.Upcard!.Value.Name, s.TableNote);
        // Pressing N while bidding reads the status; it never passes.
        var before = Snapshot.Json(s.State);
        s.Advance();
        Assert.Equal(before, Snapshot.Json(s.State));
        Assert.Equal(SpokenText.Plain(s.Status), heard[^1].Text);
    }

    [Fact]
    public void The_dealer_picks_it_up_and_then_puts_a_card_back()
    {
        var (s, _, heard) = Find(Rules(), x => MyBid(x, EuchrePhase.Bid1, dealer: true));
        Assert.Equal(["Pick it up", "Pick it up and go alone", "Pass"], s.Secondary.Select(c => c.Title));
        Assert.Equal("Pick it up or pass", s.Primary.Title);
        var up = s.State.Upcard!.Value;
        Table.Control(s, "order").Action();
        Assert.Equal(EuchrePhase.Discard, s.State.Phase);
        Assert.Contains(heard, h => h.Text.Contains("Choose a card to put back", StringComparison.Ordinal));
        Assert.Equal("Put a card back", s.Primary.Title);
        Assert.Empty(s.Secondary);
        Assert.Equal(6, s.Hand.Count);
        Assert.Contains(s.Hand, h => h.Card == up);
        Assert.StartsWith(up.Suit.Name() + " are trump, made by you", s.TableNote, StringComparison.Ordinal);
        var back = s.Hand[^1].Card;
        s.Activate(s.Hand[^1]);
        Assert.Equal(back, s.State.Discard);
        Assert.DoesNotContain(back, s.State.Players[0].Hand);
        Assert.Equal(EuchreGame.HandSize, s.State.Players[0].Hand.Count);
    }

    [Fact]
    public void Without_going_alone_there_is_no_alone_choice()
    {
        var (s, _, _) = Find(Rules(alone: false), x => MyBid(x, EuchrePhase.Bid1, dealer: false));
        Assert.Equal(["order", "pass"], s.Secondary.Select(c => c.Id));
        var (two, _, _) = Find(Rules(alone: false), x => x.State.Phase == EuchrePhase.Bid2 && x.IsMyTurn);
        Assert.DoesNotContain(two.Secondary, c => c.Id == "alone");
        Assert.All(two.Secondary, c => Assert.Equal(ControlKind.Button, c.Kind));
    }

    [Fact]
    public void Round_two_names_any_suit_but_the_one_turned_down_and_the_dealer_may_pass()
    {
        var (s, speaker, _) = Find(Rules(stick: false), x => MyBid(x, EuchrePhase.Bid2, dealer: true));
        var denied = s.State.DeniedSuit!.Value;
        var callable = EuchreCards.Suits.Where(x => x != denied).Select(x => "call-" + x.LowerName());
        Assert.Equal(["alone", .. callable, "pass"], s.Secondary.Select(c => c.Id));
        Assert.Equal("Name a suit or pass", s.Primary.Title);
        Assert.Equal(s.State.Upcard!.Value.Name + " turned down", s.TableNote);
        // The dealer passing in round two throws the hand in.
        Table.Control(s, "pass").Action();
        Assert.Equal(EuchrePhase.HandOver, s.State.Phase);
        Assert.Equal("Hand thrown in", s.TableNote);
        Assert.Equal("Deal the next hand", s.Primary.Title);
        Assert.Equal("thrown in", s.History.Rows[^1][^1]);
        // What was face down is read once, and kept in the log.
        Assert.Contains(speaker.Spoken, x => x.Text.Contains("Kitty, ", StringComparison.Ordinal));
        Assert.Contains(s.Log, l => l.Contains("Kitty, ", StringComparison.Ordinal));
    }

    [Fact]
    public void Stick_the_dealer_takes_pass_away_from_the_dealer()
    {
        var (s, _, heard) = Find(Rules(stick: true), x => MyBid(x, EuchrePhase.Bid2, dealer: true));
        Assert.DoesNotContain(s.Secondary, c => c.Id == "pass");
        Assert.Equal(4, s.Secondary.Count);
        Assert.Equal("Name a suit", s.Primary.Title);
        Assert.Equal("Stick the dealer: you must name a suit", s.Primary.Hint);
        var call = s.Secondary.First(c => c.Id.StartsWith("call-", StringComparison.Ordinal));
        call.Action();
        Assert.Equal(EuchrePhase.Play, s.State.Phase);
        Assert.Equal(0, s.State.Maker);
    }

    [Fact]
    public void Going_alone_in_round_two_sends_the_partner_out()
    {
        var (s, _, _) = Find(Rules(), x => x.State.Phase == EuchrePhase.Bid2 && x.IsMyTurn);
        Table.Control(s, "alone").OnToggle!(true);
        Assert.True(Table.Control(s, "alone").IsChecked);
        Table.Control(s, "call-" + EuchreCards.Suits.First(x => x != s.State.DeniedSuit).LowerName()).Action();
        Assert.True(s.State.Alone);
        Assert.Equal(2, s.State.SittingOut);
        Assert.Contains("sitting out", s.Seats[2].Detail, StringComparison.Ordinal);
        Assert.Contains("maker, alone", s.Seats[0].Detail, StringComparison.Ordinal);
        Assert.EndsWith("made by you, alone", s.TableNote, StringComparison.Ordinal);
        Assert.Empty(s.Secondary);
    }

    [Fact]
    public void While_the_partner_plays_alone_the_player_sits_out_and_watches()
    {
        // Seed 2 with these rules has a hand where Kelly's partner goes alone. At Wait for me the
        // table stops before each computer play, so the hand can be looked at while Kelly is out.
        var settings = Rules(stick: true, alone: true);
        settings.For(GameKind.Euchre).Pace = Pace.WaitForMe;
        var (s, _, heard) = Make(2, settings);
        s.Start();
        var found = false;
        for (var guard = 0; guard < 3000 && s.State.Phase != EuchrePhase.GameOver && !found; guard++)
        {
            if (s.Gate.IsWaiting)
            {
                if (s.State.Phase == EuchrePhase.Play && s.State.SittingOut == s.Me)
                {
                    found = true;
                    break;
                }
                s.Advance();
                Assert.True(SpinWait.SpinUntil(() => s.Gate.IsWaiting || s.BotsTask.IsCompleted, 5000), "the table did not move on");
            }
            else if (s.State.Phase == EuchrePhase.HandOver) s.Advance();
            else
            {
                Assert.True(s.IsMyTurn, "the table stopped without it being the player's move: " + s.Status);
                MoveFor(s);
            }
        }
        Assert.True(found, "Kelly never sat out a hand");
        var partner = s.State.Players[2].Name;
        Assert.All(s.Hand, h => Assert.EndsWith(", out of play", h.Description, StringComparison.Ordinal));
        Assert.All(s.Hand, h => Assert.True(h.Playable));
        Assert.Contains("sitting out", s.Seats[0].Detail, StringComparison.Ordinal);
        Assert.Contains("maker, alone", s.Seats[2].Detail, StringComparison.Ordinal);
        Assert.EndsWith($"made by {partner}, alone", s.TableNote, StringComparison.Ordinal);
        Assert.StartsWith("Waiting for ", s.Primary.Title, StringComparison.Ordinal);
        Assert.Empty(s.Secondary);
        var before = Snapshot.Json(s.State);
        s.Activate(s.Hand[0]);
        Assert.Equal(before, Snapshot.Json(s.State));
        Assert.Equal($"You are sitting out this hand while {partner} plays alone.", heard[^1].Text);
        s.Stop();
    }

    [Fact]
    public void A_bid_the_engine_refuses_says_why_and_changes_nothing()
    {
        var (s, _, heard) = Find(Rules(stick: true), x => MyBid(x, EuchrePhase.Bid1, dealer: false));
        var pass = Table.Control(s, "pass");
        var order = Table.Control(s, "order");
        pass.Action();
        // The button from the last turn is stale now: round one is over or it is somebody
        // else's bid. Pressing it is refused in words and changes nothing.
        var before = Snapshot.Json(s.State);
        order.Action();
        Assert.Equal(before, Snapshot.Json(s.State));
        Assert.True(heard[^1].Forced);
        Assert.Matches("^(Not your turn|Nobody is bidding|The upcard is not on offer)", heard[^1].Text);
    }

    [Fact]
    public void A_card_during_the_bidding_is_for_reading_and_says_bid_first()
    {
        var (s, _, heard) = Find(Rules(), x => MyBid(x, EuchrePhase.Bid1, dealer: false));
        Assert.All(s.Hand, h => Assert.True(h.Playable));
        var before = Snapshot.Json(s.State);
        s.Activate(s.Hand[0]);
        Assert.Equal(before, Snapshot.Json(s.State));
        Assert.True(heard[^1].Forced);
        Assert.StartsWith("Bid first.", heard[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void In_round_one_each_card_says_what_it_would_be_if_ordered()
    {
        var (s, _, _) = Find(Rules(), x => x.State.Phase == EuchrePhase.Bid1 && x.IsMyTurn
            && x.State.Players[0].Hand.Any(c => EuchreCards.IsTrump(c, x.State.Upcard!.Value.Suit)));
        var up = s.State.Upcard!.Value.Suit;
        foreach (var h in s.Hand)
        {
            var mark = EuchreCards.Bower(h.Card, up) switch
            {
                EuchreBower.Right => "would be the right bower",
                EuchreBower.Left => "would be the left bower",
                _ => EuchreCards.IsTrump(h.Card, up) ? "would be trump" : null,
            };
            Assert.Equal(mark is null ? h.Card.Name : h.Card.Name + ", " + mark, h.Description);
            Assert.Equal(mark, h.Badge);
        }
    }

    [Fact]
    public void Once_trump_is_made_the_hand_names_the_bowers_and_sorts_trump_first()
    {
        var (s, _, _) = Find(Rules(), x => x.State.Phase == EuchrePhase.Play && x.IsMyTurn
            && x.State.Players[0].Hand.Any(c => EuchreCards.IsTrump(c, x.State.Trump)), alwaysPass: false);
        var trump = s.State.Trump;
        Assert.Equal(EuchreCards.SortHand(s.State.Players[0].Hand, trump), s.Hand.Select(h => h.Card));
        foreach (var h in s.Hand)
        {
            Assert.StartsWith(EuchreCards.Describe(h.Card, trump), h.Description, StringComparison.Ordinal);
            if (EuchreCards.IsTrump(h.Card, trump)) Assert.NotNull(h.Badge);
            else Assert.Null(h.Badge);
        }
        Assert.Contains(s.Hand, h => h.Badge is "trump" or "right bower" or "left bower");
    }

    [Fact]
    public void A_card_that_cannot_be_played_says_why_and_nothing_changes()
    {
        var (s, _, heard) = Find(Rules(), x => x.State.Phase == EuchrePhase.Play && x.IsMyTurn && x.Hand.Any(h => !h.Playable),
            alwaysPass: false);
        var bad = s.Hand.First(h => !h.Playable);
        Assert.StartsWith("you must follow ", bad.Reason, StringComparison.Ordinal);
        Assert.Contains("cannot be played, you must follow", bad.AccessibleName, StringComparison.Ordinal);
        var before = Snapshot.Json(s.State);
        s.Activate(bad);
        Assert.Equal(before, Snapshot.Json(s.State));
        Assert.True(heard[^1].Forced);
        Assert.StartsWith($"{bad.Card.Name} cannot be played, you must follow ", heard[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_seats_show_the_trick_and_who_is_winning_it()
    {
        var (s, _, _) = Find(Rules(), x => x.State.Phase == EuchrePhase.Play && x.IsMyTurn && x.State.Trick.Count >= 2,
            alwaysPass: false);
        var seats = s.Seats;
        var leader = s.State.Trick[0].Player;
        Assert.StartsWith("led", seats[leader].PlayedNote, StringComparison.Ordinal);
        Assert.Single(seats, x => x.PlayedNote?.Contains("winning so far", StringComparison.Ordinal) == true);
        Assert.True(seats[0].IsTurn);
        Assert.Single(seats, x => x.IsTurn);
        Assert.Equal("across", seats[2].Position);
        Assert.Contains("your partner", seats[2].Detail, StringComparison.Ordinal);
        Assert.Matches("(^|, )[a-z0-9]+ tricks?$", seats[1].Detail);
    }

    [Fact]
    public void Turn_at_the_bid_is_announced_without_moving_focus_and_play_moves_it()
    {
        var settings = Rules();
        for (var seed = 1UL; seed <= 50; seed++)
        {
            var (s, _, heard) = Make(seed, settings);
            var focused = new List<string>();
            s.FocusCardRequested += focused.Add;
            s.Start();
            if (!(s.State.Phase == EuchrePhase.Bid1 && s.IsMyTurn)) continue;
            Assert.Empty(focused);
            Assert.Equal(AnnouncementCategory.Turn, heard[^1].Category);
            Assert.StartsWith("Your turn to bid.", heard[^1].Text, StringComparison.Ordinal);
            Assert.True(PlayUntil(s, x => x.State.Phase == EuchrePhase.Play && x.IsMyTurn));
            Assert.NotEmpty(focused);
            Assert.Equal(AnnouncementCategory.Turn, heard[^1].Category);
            return;
        }
        throw new Xunit.Sdk.XunitException("no seed opened on the player's bid");
    }

    [Fact]
    public void Score_rows_are_by_side_with_your_side_first()
    {
        var (s, _, _) = Make(4, Rules());
        s.Start();
        var partner = s.State.Players[2].Name;
        Assert.Equal($"You and {partner}, score 0, tricks this hand 0, games won 0", s.Scores.RowName(0));
        Assert.Equal($"{s.State.Players[1].Name} and {s.State.Players[3].Name}, score 0, tricks this hand 0, games won 0", s.Scores.RowName(1));
    }

    [Fact]
    public void Review_keys_do_not_collide_with_the_table_keys()
    {
        var (s, _, _) = Make(1, Rules());
        var keys = s.Reviews.Where(r => r.Key is not null).Select(r => r.Key!.Value).ToList();
        Assert.Equal(['h', 't', 'l', 's', 'p', 'c', 'o', 'w'], keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.DoesNotContain('n', keys);
        Assert.DoesNotContain('r', keys);
        Assert.DoesNotContain('g', keys);
        Assert.Contains(s.Reviews, r => r.Title == "Status" && r.Key is null);
        s.Start();
        Assert.All(s.Reviews, r => Assert.NotNull(r.Text()));
    }

    [Fact]
    public void A_new_game_after_a_finished_one_keeps_the_games_won()
    {
        var (s, _, _) = Make(5, Rules(points: 5));
        s.Start();
        Assert.False(PlayUntil(s, _ => false));
        var won = s.State.GamesWon.ToList();
        Assert.Equal(1, won.Sum());
        s.NewGame();
        Assert.Equal(won, s.State.GamesWon);
        Assert.Equal(2, s.State.GameNumber);
        Assert.Equal([0, 0], s.State.Scores);
        Assert.Equal(EuchrePhase.Bid1, s.State.Phase);
        Assert.Contains("New game.", s.Log);
        Assert.Contains(", games won " + won[EuchreGame.TeamOf(0)].ToString(System.Globalization.CultureInfo.InvariantCulture), s.Scores.RowName(0), StringComparison.Ordinal);
    }

    [Fact]
    public void New_game_takes_the_rules_from_settings()
    {
        var (s, _, _) = Make(6, Rules());
        s.Start();
        s.Settings.For(GameKind.Euchre).Rules[EuchreRules.StickTheDealer.Key] = "true";
        s.Settings.For(GameKind.Euchre).Rules[EuchreRules.PointsToWin.Key] = "15";
        s.NewGame();
        Assert.True(s.State.Config.StickTheDealer);
        Assert.Equal(15, s.State.Config.PointsToWin);
        Assert.Equal(1, s.State.GameNumber);
    }
}
