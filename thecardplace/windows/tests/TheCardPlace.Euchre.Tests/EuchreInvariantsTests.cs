using static TheCardPlace.Euchre.EuchreAction;

namespace TheCardPlace.Euchre.Tests;

/// <summary>
/// Hundreds of complete hands played by the AI on every difficulty, every stick-the-dealer and
/// going-alone setting, with seeded randomness: never an illegal play, every card accounted for
/// once, tricks total what they must, scoring is what the table says, the game always reaches an
/// end.
/// </summary>
public class EuchreInvariantsTests
{
    static void CheckHand(EuchreState s, bool stick, bool aloneAllowed)
    {
        Assert.NotEmpty(s.History);
        var h = s.History[^1];
        Assert.True(h.Problems.Count == 0, "the accounting check failed: " + string.Join("; ", h.Problems));

        // Every card dealt exactly once, and the whole deck accounted for.
        var all = h.Dealt.SelectMany(x => x).Concat(h.Kitty).ToList();
        if (h.Upcard is { } up) all.Add(up);
        Assert.Equal(24, all.Count);
        Assert.True(all.ToHashSet().SetEquals(EuchreCards.Deck), "the deal is not the deck");
        Assert.Equal(3, h.Kitty.Count);
        foreach (var hand in h.Dealt) Assert.Equal(5, hand.Count);

        if (h.Result.ThrownIn)
        {
            Assert.False(stick, "a hand was thrown in with stick the dealer on");
            Assert.Empty(h.Tricks);
            Assert.Equal([0, 0], h.Result.Deltas);
            Assert.Null(h.Trump);
            Assert.Null(h.Maker);
            Assert.Equal(EuchrePhase.HandOver, s.Phase);
            return;
        }

        Assert.Equal(5, h.Tricks.Count);
        Assert.Equal(5, h.TricksWon.Sum());
        Assert.NotNull(h.Trump);
        Assert.NotNull(h.Maker);

        // Exactly one side scores, and by 1, 2 or 4.
        var nonZero = h.Result.Deltas.Where(d => d != 0).ToList();
        Assert.True(nonZero.Count == 1, $"exactly one side scores on a hand: {string.Join("/", h.Result.Deltas)}");
        Assert.Contains(nonZero[0], new[] { 1, 2, 4 });
        if (nonZero[0] == 4) Assert.True(h.Alone, "four points only ever follow somebody going alone");
        if (!aloneAllowed) Assert.False(h.Alone, "somebody went alone with going alone off");
        Assert.Equal(s.Scores, h.Scores);

        if (h.Alone)
        {
            Assert.Equal(EuchreGame.PartnerOf(h.Maker!.Value), h.SittingOut);
            foreach (var t in h.Tricks)
            {
                Assert.Equal(3, t.Plays.Count);
                Assert.DoesNotContain(t.Plays, p => p.Player == h.SittingOut);
            }
            Assert.Equal(5, s.Players[h.SittingOut!.Value].Hand.Count);   // the seat sitting out keeps its five cards
        }
        else
        {
            Assert.Null(h.SittingOut);
            foreach (var t in h.Tricks) Assert.Equal(4, t.Plays.Count);
        }
        for (var i = 0; i < 4; i++)
        {
            if (i == h.SittingOut) continue;
            Assert.True(s.Players[i].Hand.Count == 0, $"seat {i} finished the hand holding cards");
        }
        // The dealer always ends up with a discard when the upcard was taken.
        if (!h.TurnedDown) Assert.NotNull(h.Discard);
        else Assert.Null(h.Discard);
    }

    /// <summary>
    /// Swift loops over the three difficulties in one test; here each is a case. The seeds are the
    /// same ones Swift uses (1001 upward, six games per setting, difficulties in order).
    /// </summary>
    [Theory]
    [InlineData(Difficulty.Easy)]
    [InlineData(Difficulty.Normal)]
    [InlineData(Difficulty.Hard)]
    public void Hundreds_of_hands_on_every_difficulty_and_setting(Difficulty difficulty)
    {
        var seed = 1000UL + (ulong)Array.IndexOf(DifficultyExtensions.All, difficulty) * 24;
        var handsPlayed = 0;
        var gamesPlayed = 0;
        foreach (var stick in new[] { false, true })
        {
            foreach (var aloneAllowed in new[] { true, false })
            {
                for (var n = 0; n < 6; n++)
                {
                    seed++;
                    var rng = new RandomSource(seed);
                    var s = EuchreGame.CreateGame(Support.Config(difficulty: difficulty, stick: stick, alone: aloneAllowed));
                    Assert.Equal(EuchrePhase.Idle, s.Phase);
                    Assert.Null(EuchreGame.SeatToAct(s));
                    var hands = 0;
                    while (s.Phase != EuchrePhase.GameOver && hands < 200)
                    {
                        Support.PlayHand(s, rng);
                        hands++;
                        CheckHand(s, stick, aloneAllowed);
                    }
                    Assert.True(s.Phase == EuchrePhase.GameOver, $"a game never ended (seed {seed})");
                    var win = s.GameWinner!.Value;
                    Assert.True(s.Scores[win] >= 10);
                    Assert.True(s.Scores[win] > s.Scores[1 - win]);
                    Assert.Equal(1, s.GamesWon.Sum());
                    Assert.Equal(hands, s.History.Count);
                    Assert.False(EuchreGame.CanDeal(s));
                    handsPlayed += hands;
                    gamesPlayed++;
                }
            }
        }
        Assert.True(handsPlayed > 100, $"only {handsPlayed} hands");
        Assert.Equal(24, gamesPlayed);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(11)]
    [InlineData(15)]
    public void Other_targets_end_the_game(int points)
    {
        var rng = new RandomSource((ulong)points * 31);
        var s = EuchreGame.CreateGame(Support.Config(points: points));
        var hands = 0;
        while (s.Phase != EuchrePhase.GameOver && hands < 300)
        {
            Support.PlayHand(s, rng);
            hands++;
        }
        Assert.Equal(EuchrePhase.GameOver, s.Phase);
        Assert.True(s.Scores[s.GameWinner ?? 0] >= points);
    }

    [Fact]
    public void Seeded_games_replay_identically()
    {
        static EuchreState Play(ulong seed)
        {
            var rng = new RandomSource(seed);
            var s = EuchreGame.CreateGame(Support.Config(difficulty: Difficulty.Easy));
            while (s.Phase != EuchrePhase.GameOver) Support.PlayHand(s, rng);
            return s;
        }
        EuchreState a = Play(77), b = Play(77), c = Play(78);
        Assert.Equal(Snapshot.Json(a), Snapshot.Json(b));
        Assert.Equal(a.Log.Events.Select(e => e.Text), b.Log.Events.Select(e => e.Text));
        Assert.NotEqual(a.Log.Events.Select(e => e.Text), c.Log.Events.Select(e => e.Text));
    }

    /// <summary>
    /// The dealer takes the upcard whoever ordered it, even when the dealer is sitting out because
    /// their partner went alone. The rule most often got wrong.
    /// </summary>
    [Fact]
    public void Dealer_still_discards_when_sitting_out()
    {
        var rng = new RandomSource(5);
        var s = EuchreGame.CreateGame(Support.Config(names: ["Kim", "Ruth", "Dale", "Marta"]));
        Assert.True(EuchreGame.ApplyAction(s, 0, new Start(), rng).IsOk);
        var dealer = s.Dealer!.Value;
        var partner = EuchreGame.PartnerOf(dealer);
        // Pass round to the dealer's partner.
        while (s.Turn != partner)
            Assert.True(EuchreGame.ApplyAction(s, s.Turn, new Pass(), rng).IsOk);
        var r = EuchreGame.ApplyAction(s, partner, new OrderUp(true), rng);
        Assert.True(r.IsOk);
        Assert.Equal(EuchrePhase.Discard, s.Phase);
        Assert.Equal(partner, s.Maker);
        Assert.True(s.Alone);
        Assert.Equal(dealer, s.SittingOut);
        Assert.Equal(dealer, EuchreGame.SeatToAct(s));   // the discard belongs to the dealer even while sitting out
        Assert.Equal(6, s.Players[dealer].Hand.Count);
        Assert.Contains(s.Upcard!.Value, s.Players[dealer].Hand);
        Assert.Equal(
            $"{s.Players[partner].Name} orders it up for {s.Players[dealer].Name}. " +
            $"{s.Upcard!.Value.Suit.Name()} are trump. {s.Players[partner].Name} is going alone, so " +
            $"{s.Players[dealer].Name} sits out this hand.",
            s.Log.Events.Last(e => e.Kind == EventKind.Bid).Text);

        // A non-dealer may not discard; the dealer may.
        Assert.Equal("only the dealer discards",
            EuchreGame.ApplyAction(s, partner, new Discard(s.Players[dealer].Hand[0]), rng).Reason);
        var d = EuchreAI.Decide(s, dealer, rng);
        var put = Assert.IsType<Discard>(d).Card;
        Assert.False(EuchreCards.IsTrump(put, s.Trump), "the computer never puts trump back");
        Assert.True(EuchreGame.ApplyAction(s, dealer, d, rng).IsOk);
        Assert.Equal(EuchrePhase.Play, s.Phase);
        Assert.Equal(5, s.Players[dealer].Hand.Count);
        Assert.NotEqual(dealer, s.Leader);
        Assert.Empty(EuchreGame.LegalPlays(s, dealer));

        Support.Drive(s, rng, Support.HandIsOver);
        var h = s.History[^1];
        Assert.Empty(h.Problems);
        foreach (var t in h.Tricks)
        {
            Assert.Equal(3, t.Plays.Count);
            Assert.DoesNotContain(t.Plays, p => p.Player == dealer);
        }
    }

    [Fact]
    public void Going_alone_is_ignored_when_the_rule_is_off()
    {
        var rng = new RandomSource(9);
        var s = EuchreGame.CreateGame(Support.Config(alone: false));
        Assert.True(EuchreGame.ApplyAction(s, 0, new Start(), rng).IsOk);
        var r = EuchreGame.ApplyAction(s, s.Turn, new OrderUp(true), rng);
        Assert.True(r.IsOk);
        Assert.False(s.Alone);
        Assert.Null(s.SittingOut);
        Assert.Equal(4, EuchreGame.ActiveCount(s));
    }

    [Fact]
    public void Stick_the_dealer_forces_the_dealer_to_name()
    {
        var rng = new RandomSource(11);
        var s = EuchreGame.CreateGame(Support.Config(stick: true));
        Assert.True(EuchreGame.ApplyAction(s, 0, new Start(), rng).IsOk);
        for (var i = 0; i < 7; i++) Assert.True(EuchreGame.ApplyAction(s, s.Turn, new Pass(), rng).IsOk);
        Assert.Equal(EuchrePhase.Bid2, s.Phase);
        Assert.Equal(s.Dealer, s.Turn);
        var before = Snapshot.Json(s);
        var r = EuchreGame.ApplyAction(s, s.Dealer!.Value, new Pass(), rng);
        Assert.Equal("stick the dealer is on, so you must name a suit", r.Reason);
        Assert.Equal(before, Snapshot.Json(s));
        var d = EuchreAI.Decide(s, s.Dealer!.Value, rng);
        var call = Assert.IsType<CallSuit>(d);   // the stuck dealer must name a suit
        Assert.NotEqual(s.DeniedSuit, call.Suit);
        Assert.True(EuchreGame.ApplyAction(s, s.Dealer!.Value, d, rng).IsOk);
        Assert.Equal(EuchrePhase.Play, s.Phase);
    }

    [Fact]
    public void Everybody_passing_twice_throws_the_hand_in()
    {
        var rng = new RandomSource(12);
        var s = EuchreGame.CreateGame(Support.Config(stick: false));
        Assert.True(EuchreGame.ApplyAction(s, 0, new Start(), rng).IsOk);
        var dealer = s.Dealer!.Value;
        for (var i = 0; i < 8; i++) Assert.True(EuchreGame.ApplyAction(s, s.Turn, new Pass(), rng).IsOk);
        Assert.Equal(EuchrePhase.HandOver, s.Phase);
        Assert.True(s.Result?.ThrownIn ?? false);
        Assert.Equal([0, 0], s.Scores);
        Assert.True(EuchreGame.CanDeal(s));
        Assert.Equal(
            "Everybody passed twice. The hand is thrown in and nobody scores. " +
            $"{s.Players[(dealer + 1) % 4].Name} deals the next one.",
            s.Log.Events[^1].Text);
        Assert.True(EuchreGame.ApplyAction(s, 0, new NextHand(), rng).IsOk);
        Assert.Equal((dealer + 1) % 4, s.Dealer);
        Assert.Equal(2, s.HandNumber);
    }

    [Fact]
    public void New_game_keeps_the_match_record()
    {
        var rng = new RandomSource(21);
        var s = EuchreGame.CreateGame(Support.Config(points: 5));
        while (s.Phase != EuchrePhase.GameOver) Support.PlayHand(s, rng);
        var won = s.GamesWon.ToList();
        var games = s.GameNumber;
        var dealer = s.Dealer;
        Assert.Equal("the game is over — start a new game", EuchreGame.ApplyAction(s, 0, new NextHand(), rng).Reason);
        Assert.True(EuchreGame.ApplyAction(s, 0, new NewGame(), rng).IsOk);
        Assert.Equal(EuchrePhase.Idle, s.Phase);
        Assert.Equal([0, 0], s.Scores);
        Assert.Equal(won, s.GamesWon);
        Assert.Equal(games + 1, s.GameNumber);
        Assert.Equal(0, s.HandNumber);
        Assert.Equal(dealer, s.Dealer);
        Assert.Null(s.GameWinner);
        Assert.Equal(Support.Names, s.Players.Select(p => p.Name));
        Assert.True(EuchreGame.ApplyAction(s, 0, new Start(), rng).IsOk);
        Assert.Equal(EuchrePhase.Bid1, s.Phase);
        Assert.Equal(1, s.HandNumber);
    }

    [Fact]
    public void Event_wording_on_the_deal()
    {
        var rng = new RandomSource(3);
        var s = EuchreGame.CreateGame(Support.Config());
        Assert.Equal(
            ["Euchre to 10 points. You are in seat 1; Dale is your partner, across the table. Ruth and Marta are against you."],
            s.Log.EventsFor(0).Select(e => e.Text));
        Assert.True(EuchreGame.ApplyAction(s, 0, new Start(), rng).IsOk);
        int dealer = s.Dealer!.Value, first = (dealer + 1) % 4;
        var up = s.Upcard!.Value;
        var dealLine = s.Log.Events.First(e => e.Kind == EventKind.Deal);
        Assert.Equal(
            $"Hand 1. {s.Players[dealer].Name}{(dealer == 0 ? " deal" : " deals")}. " +
            $"The upcard is the {up.Name}. {s.Players[first].Name}{(first == 0 ? " bid" : " bids")} first.",
            dealLine.Text);
        Assert.Equal([up], dealLine.Cards);
        var privateHands = s.Log.Events.Where(e => e.Kind == EventKind.You).ToList();
        Assert.Equal(4, privateHands.Count);
        foreach (var e in privateHands)
        {
            Assert.NotNull(e.Audience);
            Assert.Equal(
                "Your hand: " + string.Join(", ", EuchreCards.SortHand(s.Players[e.Audience.Value].Hand, null).Select(c => c.Name)) + ".",
                e.Text);
        }
        // Seat 0 hears only its own hand.
        Assert.Single(s.Log.EventsFor(0), e => e.Kind == EventKind.You);

        // Ordering up names both bowers for the whole table.
        Assert.True(EuchreGame.ApplyAction(s, first, new OrderUp(false), rng).IsOk);
        Assert.Contains(s.Log.Events, e => e.Text ==
            $"The right bower is the Jack of {up.Suit.Name()}; the left bower is the Jack of {up.Suit.SameColour().Name()}, " +
            $"which counts as {up.Suit.LowerName()} for this hand.");
        Assert.Contains(s.Log.Events, e => e.Text ==
            $"{s.Players[dealer].Name}{(dealer == 0 ? " take" : " takes")} the {up.Name} and must discard.");
        Assert.Contains(s.Log.Events, e => e.Audience == dealer && e.Text ==
            $"You took the {up.Name}. Choose a card to put back — you have six and may keep only five.");
    }

    [Fact]
    public void Play_and_trick_events_name_the_bowers()
    {
        var s = Support.Position(EuchrePhase.Play, dealer: 3, turn: 0, trump: Suit.Spades,
            hands: [["JC", "9H"], ["JS", "TH"], ["AS", "KH"], ["9S", "QH"]],
            upcard: "TS", upcardStatus: EuchreUpcardStatus.Taken, maker: 0);
        var rng = new RandomSource(1);
        Assert.True(EuchreGame.ApplyAction(s, 0, new Play(Support.C("JC")), rng).IsOk);
        Assert.Equal("You play the Jack of Clubs, left bower, second highest trump, counts as spades.", s.Log.Events[^1].Text);
        Assert.True(EuchreGame.ApplyAction(s, 1, new Play(Support.C("JS")), rng).IsOk);
        Assert.Equal("Ruth plays the Jack of Spades, right bower, the highest trump.", s.Log.Events[^1].Text);
        Assert.True(EuchreGame.ApplyAction(s, 2, new Play(Support.C("AS")), rng).IsOk);
        Assert.Equal("Dale plays the Ace of Spades, trump.", s.Log.Events[^1].Text);
        Assert.True(EuchreGame.ApplyAction(s, 3, new Play(Support.C("9S")), rng).IsOk);
        Assert.Equal("Ruth takes trick 1 with the Jack of Spades, right bower, the highest trump.", s.Log.Events[^1].Text);
        Assert.Equal(1, s.Turn);
        Assert.Equal(1, s.LastTrick?.Winner);
        Assert.Equal(1, s.Players[1].TricksWon);
    }
}
