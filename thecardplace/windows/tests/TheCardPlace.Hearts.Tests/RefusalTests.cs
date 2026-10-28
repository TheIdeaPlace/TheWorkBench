using static TheCardPlace.Hearts.Tests.Support;

namespace TheCardPlace.Hearts.Tests;

/// Can a seat do something that is not its to do? Mirrors
/// hearts/tests/authorization.js: a refusal happens, a refusal changes
/// NOTHING (the whole state is compared before and after), and a refusal says
/// nothing about cards the asking seat cannot see.
public class RefusalTests
{
    private readonly RandomSource rng = new(90210);

    /// Send an action and insist that nothing moved.
    private ActionResult Refused(HeartsState s, int seat, HeartsAction? action, string why)
    {
        var before = Snapshot.Json(s);
        var r = HeartsGame.ApplyAction(s, seat, action!, rng);
        Assert.False(r.IsOk, $"{why} — the engine ACCEPTED it");
        Assert.False(r.IsFault, $"{why} — a refusal, not a fault");
        Assert.False(string.IsNullOrEmpty(r.Reason), $"{why} — refused without a reason");
        Assert.True(Snapshot.Json(s) == before, $"{why} — refused, but the state changed anyway");
        return r;
    }

    /// Does this refusal tell the asking seat something about somebody else's cards?
    private static void ReasonIsSafe(HeartsState s, int seat, string? reason, string where)
    {
        if (reason is null) return;
        var mine = s.Players[seat].Hand.ToHashSet();
        for (var i = 0; i < s.Players.Count; i++)
        {
            if (i == seat) continue;
            foreach (var c in s.Players[i].Hand.Where(c => !mine.Contains(c)))
                Assert.False(reason.Contains(c.Name, StringComparison.Ordinal),
                    $"{where}: a refusal sent to seat {seat} names the {c.Name}, which is in seat {i}'s hand");
        }
    }

    [Fact]
    public void Before_the_game_starts()
    {
        var s = HeartsGame.CreateGame(Config());
        var three = Cards("2C", "3C", "4C");
        for (var seat = 0; seat < 4; seat++)
        {
            Refused(s, seat, new HeartsAction.Play(HeartsCards.TwoOfClubs), $"seat {seat} played before the deal");
            Refused(s, seat, new HeartsAction.Pass(three), $"seat {seat} passed before the deal");
            Refused(s, seat, new HeartsAction.NextHand(), $"seat {seat} dealt before the game started");
            Refused(s, seat, new HeartsAction.NewGame(), $"seat {seat} started a new game before this one");
        }
        Assert.Equal("the hand is not over", HeartsGame.ApplyAction(s, 0, new HeartsAction.NextHand(), rng).Reason);
        foreach (var bad in new[] { -1, 4, 99, int.MinValue, int.MaxValue })
        {
            var r = Refused(s, bad, new HeartsAction.Start(), $"seat {bad} was allowed to start");
            Assert.Equal("not a seat at this table", r.Reason);
        }
        Assert.Empty(HeartsGame.LegalPlays(s, 0));
        Assert.Empty(HeartsGame.LegalPlays(s, 9));
        Assert.Null(HeartsGame.SeatToAct(s));
        Assert.False(HeartsGame.CanDeal(s));
        Assert.Null(HeartsAI.Decide(s, 0, rng));
        Assert.Null(HeartsAI.Decide(s, 7, rng));
    }

    [Fact]
    public void The_pass()
    {
        var s = HeartsGame.CreateGame(Config());
        Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Start(), rng).IsOk);
        Assert.Equal(HeartsPhase.Passing, s.Phase);
        Refused(s, 0, new HeartsAction.Start(), "the game started twice");
        Assert.Equal("the game has already started", HeartsGame.ApplyAction(s, 0, new HeartsAction.Start(), rng).Reason);

        for (var seat = 0; seat < 4; seat++)
        {
            var mine = s.Players[seat].Hand.ToHashSet();
            var others = new List<Card>();
            for (var i = 0; i < 4; i++) if (i != seat) others.AddRange(s.Players[i].Hand);
            var notMine = others.Where(c => !mine.Contains(c)).Take(3).ToList();

            var r = Refused(s, seat, new HeartsAction.Pass(notMine), $"seat {seat} passed cards from another hand");
            Assert.Equal("you do not hold that card", r.Reason);
            ReasonIsSafe(s, seat, r.Reason, "pass of another seat's cards");

            Assert.Equal("pass exactly 3 cards", Refused(s, seat, new HeartsAction.Pass([]), $"seat {seat} passed nothing").Reason);
            Assert.Equal("pass exactly 3 cards",
                Refused(s, seat, new HeartsAction.Pass(s.Players[seat].Hand.Take(4).ToList()), $"seat {seat} passed four").Reason);
            Assert.Equal("pass exactly 3 cards",
                Refused(s, seat, new HeartsAction.Pass(s.Players[seat].Hand.Take(1).ToList()), $"seat {seat} passed one").Reason);
            var h = s.Players[seat].Hand;
            Assert.Equal("the same card twice",
                Refused(s, seat, new HeartsAction.Pass([h[0], h[0], h[1]]), $"seat {seat} passed the same card twice").Reason);
            // Two held and one not: still refused, still nothing moved.
            Refused(s, seat, new HeartsAction.Pass([h[0], h[1], notMine[0]]), $"seat {seat} passed a card it does not hold");

            Assert.Equal("no trick is in progress", Refused(s, seat, new HeartsAction.Play(h[0]), $"seat {seat} played during the pass").Reason);
            Assert.Equal("the hand is not over", Refused(s, seat, new HeartsAction.NextHand(), $"seat {seat} dealt during the pass").Reason);
            Assert.Equal("no trick is in progress", HeartsGame.WhyNot(s, seat, h[0]));
        }

        // A legal pass goes through, and only once.
        for (var seat = 0; seat < 4; seat++)
        {
            var cards = s.Players[seat].Hand.Take(3).ToList();
            var r = HeartsGame.ApplyAction(s, seat, new HeartsAction.Pass(cards), rng);
            Assert.True(r.IsOk, $"a legal pass from seat {seat} was refused: {r.Reason}");
            if (s.Phase == HeartsPhase.Passing)
            {
                var again = Refused(s, seat, new HeartsAction.Pass(s.Players[seat].Hand.Take(3).ToList()), $"seat {seat} passed twice");
                Assert.Equal("you have already passed", again.Reason);
                Assert.Null(HeartsAI.Decide(s, seat, rng));   // a seat that has passed has nothing to decide
                Assert.Equal(seat + 1, HeartsGame.SeatToAct(s));
            }
        }
        Assert.Equal(HeartsPhase.Play, s.Phase);   // four passes should have started the play
        Assert.Equal("nothing is being passed",
            Refused(s, 0, new HeartsAction.Pass(s.Players[0].Hand.Take(3).ToList()), "passed during play").Reason);
    }

    [Fact]
    public void The_play()
    {
        var s = HeartsGame.CreateGame(Config());
        Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Start(), rng).IsOk);
        while (s.Phase == HeartsPhase.Passing)
        {
            var seat = HeartsGame.SeatToAct(s)!.Value;
            Assert.True(HeartsGame.ApplyAction(s, seat, HeartsAI.Decide(s, seat, rng)!, rng).IsOk);
        }
        Assert.Equal(HeartsPhase.Play, s.Phase);

        string[] rules =
        [
            "you must follow clubs", "you must follow diamonds", "you must follow spades",
            "you must follow hearts", "no points on the first trick",
            "the two of clubs must be led first", "hearts have not been broken",
        ];
        var moves = 0;
        while (s.Phase == HeartsPhase.Play && moves < 60)
        {
            var onTurn = s.Turn;

            // EVERY other seat tries its cards. None may move the game.
            for (var seat = 0; seat < 4; seat++)
            {
                if (seat == onTurn) continue;
                var hand = s.Players[seat].Hand.ToList();
                foreach (var c in hand.Take(4))
                {
                    var r = Refused(s, seat, new HeartsAction.Play(c), $"seat {seat} played out of turn ({c.Id})");
                    Assert.Equal("not your turn", r.Reason);
                    ReasonIsSafe(s, seat, r.Reason, "out-of-turn play");
                    Assert.Equal("not your turn", HeartsGame.WhyNot(s, seat, c));
                }
                Assert.Empty(HeartsGame.LegalPlays(s, seat));
                Refused(s, seat, new HeartsAction.Pass(hand.Take(3).ToList()), $"seat {seat} passed during the play");
                Refused(s, seat, new HeartsAction.NextHand(), $"seat {seat} dealt mid-hand");
                Refused(s, seat, new HeartsAction.Start(), $"seat {seat} restarted mid-hand");
            }

            // The seat on turn may not play a card it does not hold, nor an
            // illegal one, and neither refusal may move anything.
            var held = s.Players[onTurn].Hand.ToHashSet();
            foreach (var c in Card.FullDeck.Where(c => !held.Contains(c)).Take(5))
            {
                var r = Refused(s, onTurn, new HeartsAction.Play(c), $"seat {onTurn} played {c.Id}, which it does not hold");
                Assert.Equal("you do not hold that card", r.Reason);
                ReasonIsSafe(s, onTurn, r.Reason, "playing a card not held");
                Assert.Equal("you do not hold that card", HeartsGame.WhyNot(s, onTurn, c));
            }
            var legal = HeartsGame.LegalPlays(s, onTurn).ToHashSet();
            foreach (var c in s.Players[onTurn].Hand.Where(c => !legal.Contains(c)).Take(3).ToList())
            {
                var r = Refused(s, onTurn, new HeartsAction.Play(c), $"seat {onTurn} played {c.Id}, which the rules forbid here");
                var why = HeartsGame.WhyNot(s, onTurn, c);
                Assert.NotNull(why);
                Assert.Equal(why, r.Reason);   // the refusal is the rule
                Assert.Contains(why, rules);
                ReasonIsSafe(s, onTurn, r.Reason, "illegal play");
            }
            foreach (var c in legal) Assert.Null(HeartsGame.WhyNot(s, onTurn, c));

            var a = HeartsAI.Decide(s, onTurn, rng)!;
            Assert.True(HeartsGame.ApplyAction(s, onTurn, a, rng).IsOk);
            moves++;
        }
        Assert.True(moves > 20);
    }

    [Fact]
    public void Between_hands_and_after_the_game()
    {
        var s = HeartsGame.CreateGame(Config(pointsToWin: 50));
        Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Start(), rng).IsOk);
        var sawHandOver = false;
        var guard = 0;
        var three = Cards("2C", "3C", "4C");
        while (s.Phase != HeartsPhase.GameOver && guard < 5000)
        {
            guard++;
            if (s.Phase == HeartsPhase.HandOver)
            {
                sawHandOver = true;
                for (var seat = 0; seat < 4; seat++)
                {
                    Refused(s, seat, new HeartsAction.Play(HeartsCards.TwoOfClubs), $"seat {seat} played between hands");
                    Refused(s, seat, new HeartsAction.Pass(three), $"seat {seat} passed between hands");
                    Refused(s, seat, new HeartsAction.Start(), $"seat {seat} restarted a game in progress");
                }
                // Dealing the next hand is open to any seated player.
                Assert.True(HeartsGame.ApplyAction(s, 2, new HeartsAction.NextHand(), rng).IsOk);
                continue;
            }
            var seatToAct = HeartsGame.SeatToAct(s)!.Value;
            Assert.True(HeartsGame.ApplyAction(s, seatToAct, HeartsAI.Decide(s, seatToAct, rng)!, rng).IsOk);
        }
        Assert.True(sawHandOver);
        Assert.Equal(HeartsPhase.GameOver, s.Phase);

        // A finished game is finished.
        for (var seat = 0; seat < 4; seat++)
        {
            Refused(s, seat, new HeartsAction.Play(HeartsCards.TwoOfClubs), $"seat {seat} played after the game");
            Refused(s, seat, new HeartsAction.NextHand(), $"seat {seat} dealt after the game");
            Refused(s, seat, new HeartsAction.Pass(three), $"seat {seat} passed after the game");
            Refused(s, seat, new HeartsAction.Start(), $"seat {seat} started over a finished game");
        }
        Assert.False(HeartsGame.CanDeal(s));
        Assert.Null(HeartsGame.SeatToAct(s));
        // The only way on is a new game.
        Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.NewGame(), rng).IsOk);
        Assert.Equal(HeartsPhase.Idle, s.Phase);
    }

    [Fact]
    public void A_broken_table_is_a_fault_not_a_refusal()
    {
        var s = HeartsGame.CreateGame(Config());
        s.Players.RemoveAt(s.Players.Count - 1);
        var r = HeartsGame.ApplyAction(s, 0, new HeartsAction.Start(), rng);
        Assert.False(r.IsOk);
        Assert.True(r.IsFault);
    }

    // Not in Swift, whose action enum is closed and whose arrays cannot be nil: what can arrive
    // over the wire in C# and must still be refused without a throw.

    [Fact]
    public void A_null_action_or_a_pass_with_no_cards_is_refused_not_thrown()
    {
        var s = HeartsGame.CreateGame(Config());
        Assert.Equal("that is not something you can do in hearts", Refused(s, 0, null, "a null action").Reason);
        Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Start(), rng).IsOk);
        Assert.Equal("pass exactly 3 cards", Refused(s, 0, new HeartsAction.Pass(null!), "a pass with a null list").Reason);
        var fromWire = System.Text.Json.JsonSerializer.Deserialize<HeartsAction>("""{"type":"pass"}""", Snapshot.Options);
        Assert.Equal("pass exactly 3 cards", Refused(s, 0, fromWire, "a pass with no cards from the wire").Reason);
    }
}
