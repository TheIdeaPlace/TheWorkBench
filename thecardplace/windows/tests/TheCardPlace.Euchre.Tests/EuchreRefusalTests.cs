using static TheCardPlace.Euchre.EuchreAction;

namespace TheCardPlace.Euchre.Tests;

/// <summary>
/// The wrong seat, the wrong phase, a card not held: each refused with a reason in the words of
/// the rule, and the state compared before and after so a refusal that quietly changed something
/// is caught.
/// </summary>
public class EuchreRefusalTests
{
    readonly RandomSource rng = new(4242);

    void AssertRefused(EuchreState s, int seat, EuchreAction action, string reason)
    {
        var before = Snapshot.Json(s);
        var phase = s.Phase;
        var r = EuchreGame.ApplyAction(s, seat, action, rng);
        Assert.False(r.IsOk, $"{action} by seat {seat} at {phase} should have been refused");
        Assert.False(r.IsFault);
        Assert.Equal(reason, r.Reason);
        Assert.Equal(before, Snapshot.Json(s));
    }

    [Fact]
    public void Nothing_works_before_the_first_deal()
    {
        var s = EuchreGame.CreateGame(Support.Config());
        var card = Support.C("AS");
        AssertRefused(s, 0, new NextHand(), "the hand is not over");
        AssertRefused(s, 0, new NewGame(), "the game is not over");
        AssertRefused(s, 0, new Play(card), "not the playing phase");
        AssertRefused(s, 0, new Pass(), "there is nothing to pass on");
        AssertRefused(s, 0, new OrderUp(false), "the upcard is not on offer");
        AssertRefused(s, 0, new CallSuit(Suit.Hearts, false), "not the naming round");
        AssertRefused(s, 0, new Discard(card), "nothing to discard");
        AssertRefused(s, 4, new Start(), "not a seat at this table");
        AssertRefused(s, -1, new Start(), "not a seat at this table");
        Assert.Empty(EuchreGame.LegalPlays(s, 0));
        Assert.Equal("no hand has been dealt yet", EuchreGame.WhyNot(s, 0, card));
    }

    [Fact]
    public void Round_one()
    {
        var s = EuchreGame.CreateGame(Support.Config());
        Assert.True(EuchreGame.ApplyAction(s, 0, new Start(), rng).IsOk);
        int turn = s.Turn, other = (turn + 1) % 4;
        var dealer = s.Dealer!.Value;
        AssertRefused(s, 0, new Start(), "the game has already started");
        AssertRefused(s, 0, new NextHand(), "the hand is not over");
        AssertRefused(s, other, new OrderUp(false), "not your turn to bid");
        AssertRefused(s, other, new Pass(), "not your turn to bid");
        AssertRefused(s, turn, new CallSuit(Suit.Hearts, false), "not the naming round");
        AssertRefused(s, dealer, new Discard(s.Players[dealer].Hand[0]), "nothing to discard");
        AssertRefused(s, turn, new Play(s.Players[turn].Hand[0]), "not the playing phase");
        Assert.Empty(EuchreGame.LegalPlays(s, turn));
        Assert.Equal("for review while you decide your bid", EuchreGame.WhyNot(s, turn, s.Players[turn].Hand[0]));
        Assert.Equal($"for review, {s.Players[turn].Name} is bidding", EuchreGame.WhyNot(s, other, s.Players[other].Hand[0]));
    }

    [Fact]
    public void Round_two()
    {
        var s = EuchreGame.CreateGame(Support.Config());
        Assert.True(EuchreGame.ApplyAction(s, 0, new Start(), rng).IsOk);
        for (var i = 0; i < 4; i++) Assert.True(EuchreGame.ApplyAction(s, s.Turn, new Pass(), rng).IsOk);
        Assert.Equal(EuchrePhase.Bid2, s.Phase);
        var denied = s.DeniedSuit!.Value;
        AssertRefused(s, s.Turn, new CallSuit(denied, false), $"{denied.LowerName()} was turned down and cannot be named this hand");
        AssertRefused(s, s.Turn, new OrderUp(false), "the upcard is not on offer");
        AssertRefused(s, (s.Turn + 1) % 4, new CallSuit(denied.SameColour(), false), "not your turn to bid");
        AssertRefused(s, s.Turn, new Discard(s.Players[s.Turn].Hand[0]), "nothing to discard");
    }

    [Fact]
    public void The_discard()
    {
        var s = EuchreGame.CreateGame(Support.Config());
        Assert.True(EuchreGame.ApplyAction(s, 0, new Start(), rng).IsOk);
        Assert.True(EuchreGame.ApplyAction(s, s.Turn, new OrderUp(false), rng).IsOk);
        Assert.Equal(EuchrePhase.Discard, s.Phase);
        int dealer = s.Dealer!.Value, other = (dealer + 1) % 4;
        /* The euchre-specific trap: the discard belongs to the dealer, who is usually not the seat
         * that ordered it up. */
        AssertRefused(s, other, new Discard(s.Players[dealer].Hand[0]), "only the dealer discards");
        AssertRefused(s, dealer, new Discard(s.Players[other].Hand[0]), "that card is not in your hand");
        AssertRefused(s, dealer, new Play(s.Players[dealer].Hand[0]), "not the playing phase");
        AssertRefused(s, dealer, new Pass(), "there is nothing to pass on");
        AssertRefused(s, dealer, new OrderUp(false), "the upcard is not on offer");
        AssertRefused(s, 0, new NextHand(), "the hand is not over");
        Assert.Equal("for review while you choose what to put back", EuchreGame.WhyNot(s, dealer, s.Players[dealer].Hand[0]));
        Assert.Equal($"for review, {s.Players[dealer].Name} is putting a card back", EuchreGame.WhyNot(s, other, s.Players[other].Hand[0]));
    }

    [Fact]
    public void The_play()
    {
        var s = Support.Position(EuchrePhase.Play, dealer: 3, turn: 0, trump: Suit.Spades,
            hands: [["AS", "9H", "TC", "KD", "QD"], ["JC", "9D", "TD", "AH", "KH"],
                    ["JS", "QH", "JH", "AD", "9C"], ["9S", "TS", "QS", "KS", "TH"]],
            upcard: "9S", upcardStatus: EuchreUpcardStatus.Taken, maker: 0);
        Card ah = Support.C("AH"), jc = Support.C("JC");
        AssertRefused(s, 1, new Play(jc), "not your turn");
        AssertRefused(s, 0, new Play(ah), "that card is not in your hand");
        AssertRefused(s, 0, new Play(Support.C("2C")), "that card is not in your hand");
        AssertRefused(s, 0, new OrderUp(false), "the upcard is not on offer");
        AssertRefused(s, 0, new Pass(), "there is nothing to pass on");
        AssertRefused(s, 3, new Discard(Support.C("9S")), "nothing to discard");
        Assert.Equal("not your turn, You is to play", EuchreGame.WhyNot(s, 1, jc));

        // Lead the ace of spades; seat 1 holds the left bower, so it must follow trump.
        Assert.True(EuchreGame.ApplyAction(s, 0, new Play(Support.C("AS")), rng).IsOk);
        Assert.Equal(1, s.Turn);
        AssertRefused(s, 1, new Play(ah), "you must follow trump — you hold Jack of Clubs");
        Assert.Equal("you must follow trump — you hold Jack of Clubs", EuchreGame.WhyNot(s, 1, ah));
        Assert.Null(EuchreGame.WhyNot(s, 1, jc));
        Assert.Equal([jc], EuchreGame.LegalPlays(s, 1));
        Assert.True(EuchreGame.ApplyAction(s, 1, new Play(jc), rng).IsOk);

        // Seat 2 holds the right bower and must follow with it, not a heart.
        AssertRefused(s, 2, new Play(Support.C("QH")), "you must follow trump — you hold Jack of Spades");
        Assert.True(EuchreGame.ApplyAction(s, 2, new Play(Support.C("JS")), rng).IsOk);
        // Seat 3 has four spades; the reason names all of them.
        AssertRefused(s, 3, new Play(Support.C("TH")),
            "you must follow trump — you hold Nine of Spades, Ten of Spades, Queen of Spades and King of Spades");
        Assert.True(EuchreGame.ApplyAction(s, 3, new Play(Support.C("9S")), rng).IsOk);
        Assert.Equal(2, s.LastTrick?.Winner);
        Assert.Equal(2, s.Turn);

        // A plain-suit lead: hearts led, and trump may not be thrown while a heart is held.
        Assert.True(EuchreGame.ApplyAction(s, 2, new Play(Support.C("QH")), rng).IsOk);
        AssertRefused(s, 3, new Play(Support.C("QS")), "you must follow hearts — you hold Ten of Hearts");
        Assert.True(EuchreGame.ApplyAction(s, 3, new Play(Support.C("TH")), rng).IsOk);
        // And seat 0, holding one heart, must follow with it.
        AssertRefused(s, 0, new Play(Support.C("KD")), "you must follow hearts — you hold Nine of Hearts");
    }

    [Fact]
    public void A_seat_sitting_out_may_not_play()
    {
        var s = Support.Position(EuchrePhase.Play, dealer: 3, turn: 1, trump: Suit.Hearts,
            hands: [["AS", "9H", "TH", "KD", "QD"], ["JC", "9D", "TD", "AH", "KH"],
                    ["JS", "QH", "JH", "AD", "9C"], ["9S", "TS", "QS", "KS", "TC"]],
            upcard: "9H", upcardStatus: EuchreUpcardStatus.Taken, maker: 1, alone: true, sittingOut: 3);
        AssertRefused(s, 3, new Play(Support.C("9S")), "you are sitting out this hand");
        Assert.Empty(EuchreGame.LegalPlays(s, 3));
        Assert.Equal("you are sitting out while Ruth plays alone", EuchreGame.WhyNot(s, 3, Support.C("9S")));
        Assert.Equal(3, EuchreGame.ActiveCount(s));
        Assert.Equal(0, EuchreGame.NextActive(s, 2));
    }

    [Fact]
    public void Between_hands()
    {
        var s = EuchreGame.CreateGame(Support.Config());
        Support.PlayHand(s, rng);
        Assert.Equal(EuchrePhase.HandOver, s.Phase);   // a game to ten cannot end on hand one
        Assert.Null(EuchreGame.SeatToAct(s));
        AssertRefused(s, 0, new Start(), "the game has already started");
        AssertRefused(s, 0, new NewGame(), "the game is not over");
        AssertRefused(s, 0, new Pass(), "there is nothing to pass on");
        AssertRefused(s, 0, new Play(Support.C("AS")), "not the playing phase");
        Assert.Equal("the hand is over", EuchreGame.WhyNot(s, 0, Support.C("AS")));
        Assert.True(EuchreGame.CanDeal(s));
        AssertRefused(s, 9, new NextHand(), "not a seat at this table");
        Assert.True(EuchreGame.ApplyAction(s, 2, new NextHand(), rng).IsOk, "any seat may deal");
    }

    [Fact]
    public void After_the_game_ends()
    {
        var s = EuchreGame.CreateGame(Support.Config(points: 5));
        while (s.Phase != EuchrePhase.GameOver) Support.PlayHand(s, rng);
        AssertRefused(s, 0, new NextHand(), "the game is over — start a new game");
        AssertRefused(s, 0, new Start(), "the game has already started");
        AssertRefused(s, 0, new Play(Support.C("AS")), "not the playing phase");
        Assert.Equal("the game is over", EuchreGame.WhyNot(s, 0, Support.C("AS")));
        Assert.False(EuchreGame.CanDeal(s));
    }

    // Not in the Swift tests: inputs only a C# caller (or the wire) can produce.

    [Fact]
    public void Inputs_Swift_could_not_express_are_refused_too()
    {
        var s = EuchreGame.CreateGame(Support.Config(stick: false));
        Assert.True(EuchreGame.ApplyAction(s, 0, new Start(), rng).IsOk);
        AssertRefused(s, s.Turn, null!, "that is not something you can do in euchre");
        for (var i = 0; i < 4; i++) Assert.True(EuchreGame.ApplyAction(s, s.Turn, new Pass(), rng).IsOk);
        AssertRefused(s, s.Turn, new CallSuit((Suit)9, false), "that is not a suit");
    }
}
