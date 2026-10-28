using static TheCardPlace.Euchre.EuchreAction;

namespace TheCardPlace.Euchre.Tests;

/// <summary>
/// Exact-string checks on the card descriptions and the review sentences from constructed
/// positions. Seat 0 is "You"; Ruth is on the left, Dale is the partner across the table, Marta is
/// on the right.
/// </summary>
public class EuchreReviewTests
{
    static Card C(string id) => Support.C(id);

    // MARK: Card text

    [Fact]
    public void Describe_names_both_bowers()
    {
        Assert.Equal("Jack of Clubs, left bower, second highest trump, counts as spades", EuchreCards.Describe(C("JC"), Suit.Spades));
        Assert.Equal("Jack of Spades, right bower, the highest trump", EuchreCards.Describe(C("JS"), Suit.Spades));
        Assert.Equal("Ace of Spades, trump", EuchreCards.Describe(C("AS"), Suit.Spades));
        Assert.Equal("Ace of Hearts", EuchreCards.Describe(C("AH"), Suit.Spades));
        Assert.Equal("Jack of Clubs", EuchreCards.Describe(C("JC"), null));
        Assert.Equal("Jack of Hearts, left bower, second highest trump, counts as diamonds", EuchreCards.Describe(C("JH"), Suit.Diamonds));
        Assert.Equal("", EuchreCards.Role(C("AH"), Suit.Spades));
        Assert.Equal("trump", EuchreCards.Role(C("9S"), Suit.Spades));
    }

    [Fact]
    public void Sort_hand_files_the_left_bower_under_trump()
    {
        var hand = Support.Cards("9H", "JC", "AS", "KD", "JS");
        Assert.Equal(["JS", "JC", "AS", "9H", "KD"], EuchreCards.SortHand(hand, Suit.Spades).Ids());
        Assert.Equal(["JC", "AS", "JS", "9H", "KD"], EuchreCards.SortHand(hand, null).Ids());
        Assert.Equal(["9H", "JC", "AS", "JS", "KD"], EuchreCards.SortHand(hand, Suit.Hearts).Ids());
    }

    // MARK: H — the hand

    static readonly string[] BidHand = ["JC", "AS", "9H", "KD", "TD"];

    [Fact]
    public void Hand_during_round_one_maps_the_bowers()
    {
        var s = Support.Position(EuchrePhase.Bid1, dealer: 3, turn: 0, hands: [BidHand, [], [], []],
            upcard: "JS", upcardStatus: EuchreUpcardStatus.Up);
        Assert.Equal(
            "With spades as trump you would hold two trump. Trump: Jack of Clubs, the left bower, Ace of Spades. " +
            "The rest: Hearts: Nine of Hearts. Diamonds: King of Diamonds, Ten of Diamonds. " +
            "Marta is dealing, so the Jack of Spades would go to the other side.",
            EuchreReview.Hand(s, 0));

        var partnerDeals = Support.Position(EuchrePhase.Bid1, dealer: 2, turn: 3, hands: [BidHand, [], [], []],
            upcard: "JS", upcardStatus: EuchreUpcardStatus.Up);
        Assert.EndsWith("Your partner is dealing, so the Jack of Spades would go to them.", EuchreReview.Hand(partnerDeals, 0));

        var youDeal = Support.Position(EuchrePhase.Bid1, dealer: 0, turn: 1, hands: [BidHand, [], [], []],
            upcard: "JS", upcardStatus: EuchreUpcardStatus.Up);
        Assert.EndsWith("You are the dealer, so the Jack of Spades would come to you and you would put a card back.",
            EuchreReview.Hand(youDeal, 0));

        var none = Support.Position(EuchrePhase.Bid1, dealer: 3, turn: 0, hands: [["9H", "TH", "KD", "QD", "9C"], [], [], []],
            upcard: "JS", upcardStatus: EuchreUpcardStatus.Up);
        Assert.Equal(
            "With spades as trump you would hold no trump. The rest: Clubs: Nine of Clubs. " +
            "Hearts: Ten of Hearts, Nine of Hearts. Diamonds: King of Diamonds, Queen of Diamonds. " +
            "Marta is dealing, so the Jack of Spades would go to the other side.",
            EuchreReview.Hand(none, 0));

        var allTrump = Support.Position(EuchrePhase.Bid1, dealer: 3, turn: 0, hands: [["JS", "JC", "AS", "KS", "9S"], [], [], []],
            upcard: "TS", upcardStatus: EuchreUpcardStatus.Up);
        Assert.Equal(
            "With spades as trump you would hold five trump. Trump: Jack of Spades, the right bower, " +
            "Jack of Clubs, the left bower, Ace of Spades, King of Spades, Nine of Spades. Nothing else. " +
            "Marta is dealing, so the Ten of Spades would go to the other side.",
            EuchreReview.Hand(allTrump, 0));
    }

    [Fact]
    public void Hand_during_round_two_counts_each_suit()
    {
        var s = Support.Position(EuchrePhase.Bid2, dealer: 3, turn: 0, hands: [BidHand, [], [], []],
            upcard: "JS", upcardStatus: EuchreUpcardStatus.TurnedDown, deniedSuit: Suit.Spades);
        Assert.Equal(
            "Your hand: Clubs: Jack of Clubs. Spades: Ace of Spades. Hearts: Nine of Hearts. " +
            "Diamonds: King of Diamonds, Ten of Diamonds. Spades cannot be named. " +
            "Trump you would hold: clubs 1, hearts 1, diamonds 2.",
            EuchreReview.Hand(s, 0));
    }

    [Fact]
    public void Hand_during_play_and_while_discarding()
    {
        var play = Support.Position(EuchrePhase.Play, dealer: 3, turn: 0, trump: Suit.Spades, hands: [BidHand, [], [], []],
            upcard: "JS", upcardStatus: EuchreUpcardStatus.Taken, maker: 1);
        Assert.Equal(
            "Your hand, five cards. Trump: Jack of Clubs, the left bower, Ace of Spades. " +
            "Hearts: Nine of Hearts. Diamonds: King of Diamonds, Ten of Diamonds.",
            EuchreReview.Hand(play, 0));

        var discard = Support.Position(EuchrePhase.Discard, dealer: 0, turn: 0, trump: Suit.Spades,
            hands: [[.. BidHand, "JS"], [], [], []], upcard: "JS", upcardStatus: EuchreUpcardStatus.Taken, maker: 1);
        Assert.Equal(
            "You took the Jack of Spades. Your hand, six cards. Trump: Jack of Spades, the right bower, " +
            "Jack of Clubs, the left bower, Ace of Spades. Hearts: Nine of Hearts. " +
            "Diamonds: King of Diamonds, Ten of Diamonds.",
            EuchreReview.Hand(discard, 0));

        var @out = Support.Position(EuchrePhase.Play, dealer: 3, turn: 1, trump: Suit.Spades, hands: [BidHand, [], [], []],
            upcard: "JS", upcardStatus: EuchreUpcardStatus.Taken, maker: 2, alone: true, sittingOut: 0);
        Assert.EndsWith(" These are out of play this hand — Dale is playing alone.", EuchreReview.Hand(@out, 0));

        Assert.Equal("Your hand is empty.", EuchreReview.Hand(Support.Position(EuchrePhase.HandOver), 0));
    }

    // MARK: T, L — the trick and the last trick

    [Fact]
    public void Trick()
    {
        var empty = Support.Position(EuchrePhase.Play, dealer: 0, turn: 1, trump: Suit.Spades);
        Assert.Equal("Trick 1 of 5. Nothing played yet. Ruth to lead.", EuchreReview.Trick(empty, 0));

        var s = Support.Position(EuchrePhase.Play, dealer: 0, turn: 3, leader: 1, trump: Suit.Spades,
            trick: [(1, "AS"), (2, "9S")]);
        Assert.Equal(
            "Trick 1 of 5. Ruth, Ace of Spades, trump. Dale, Nine of Spades, trump. " +
            "Trump led. Ruth is winning with the Ace of Spades, trump.",
            EuchreReview.Trick(s, 0));

        var plain = Support.Position(EuchrePhase.Play, dealer: 0, turn: 0, leader: 2, trump: Suit.Spades,
            trick: [(2, "KH"), (3, "JC")]);
        Assert.Equal(
            "Trick 1 of 5. Dale, King of Hearts. Marta, Jack of Clubs, left bower, second highest trump, counts as spades. " +
            "Hearts led. Marta is winning with the Jack of Clubs, left bower, second highest trump, counts as spades.",
            EuchreReview.Trick(plain, 0));
    }

    [Fact]
    public void Last_trick()
    {
        var s = Support.Position(EuchrePhase.Play, dealer: 3, turn: 0, trump: Suit.Spades);
        Assert.Equal("No trick has been completed yet this hand.", EuchreReview.LastTrick(s, 0));
        s.LastTrick = new EuchreTrick(1,
            [new EuchrePlay(1, C("AS")), new EuchrePlay(2, C("9S")), new EuchrePlay(3, C("KH")), new EuchrePlay(0, C("JC"))], 0);
        Assert.Equal(
            "Trick 1. Ruth, Ace of Spades. Dale, Nine of Spades. Marta, King of Hearts. You, Jack of Clubs. You took it.",
            EuchreReview.LastTrick(s, 0));
    }

    // MARK: S — the score

    [Fact]
    public void Scores()
    {
        var s = Support.Position(EuchrePhase.Play, dealer: 3, turn: 0, trump: Suit.Spades, maker: 1);
        s.Players[0].TricksWon = 1;
        s.Players[1].TricksWon = 1;
        s.Scores = [3, 2];
        Assert.Equal(
            "Tricks this hand: you and Dale 1, Ruth and Marta 1. Game 1 to 10: you 3, them 2. " +
            "They need two more tricks; two more tricks euchres them.",
            EuchreReview.Scores(s, 0));
        s.Maker = 0;
        Assert.Equal(
            "Tricks this hand: you and Dale 1, Ruth and Marta 1. Game 1 to 10: you 3, them 2. " +
            "You need two more tricks to make it.",
            EuchreReview.Scores(s, 0));
        s.Players[2].TricksWon = 2;
        s.GamesWon = [1, 0];
        Assert.Equal(
            "Tricks this hand: you and Dale 3, Ruth and Marta 1. Game 1 to 10: you 3, them 2. " +
            "Games won: you 1, them 0. You have made it.",
            EuchreReview.Scores(s, 0));
        // From Ruth's chair the same position reads the other way round.
        Assert.Equal(
            "Tricks this hand: you and Marta 1, You and Dale 3. Game 1 to 10: you 2, them 3. " +
            "Games won: you 0, them 1. They have made it.",
            EuchreReview.Scores(s, 1));
    }

    // MARK: P — trump and partners

    [Fact]
    public void Trump_and_partner()
    {
        var alone = Support.Position(EuchrePhase.Play, dealer: 2, turn: 3, trump: Suit.Spades, upcard: "JS",
            upcardStatus: EuchreUpcardStatus.Taken, maker: 1, alone: true, sittingOut: 3);
        Assert.Equal(
            "Your partner is Dale, in seat 3. Spades are trump, made by Ruth, against you. " +
            "The right bower is the Jack of Spades and the left bower is the Jack of Clubs. " +
            "Ruth is playing alone, so Marta is sitting this hand out. The upcard was the Jack of Spades, taken by Dale.",
            EuchreReview.TrumpAndPartner(alone, 0));

        var named = Support.Position(EuchrePhase.Play, dealer: 2, turn: 3, trump: Suit.Hearts, upcard: "JS",
            upcardStatus: EuchreUpcardStatus.TurnedDown, deniedSuit: Suit.Spades, maker: 0);
        Assert.Equal(
            "Your partner is Dale, in seat 3. Hearts are trump, made by you, on your side. " +
            "The right bower is the Jack of Hearts and the left bower is the Jack of Diamonds. " +
            "The upcard was the Jack of Spades, turned down.",
            EuchreReview.TrumpAndPartner(named, 0));

        var bid1 = Support.Position(EuchrePhase.Bid1, dealer: 3, turn: 2, upcard: "JS", upcardStatus: EuchreUpcardStatus.Up);
        Assert.Equal(
            "Your partner is Dale, in seat 3. The upcard is the Jack of Spades, so spades are on offer. " +
            "Marta is dealing. Nobody has bid yet this round.",
            EuchreReview.TrumpAndPartner(bid1, 0));
        bid1.BidLog = [new EuchreBid(0, EuchreBidKind.Pass, 1, Words: "pass"), new EuchreBid(1, EuchreBidKind.Pass, 1, Words: "pass")];
        Assert.EndsWith("Marta is dealing. You, Ruth have passed.", EuchreReview.TrumpAndPartner(bid1, 0));

        var bid2 = Support.Position(EuchrePhase.Bid2, dealer: 3, turn: 0, upcard: "JS", upcardStatus: EuchreUpcardStatus.TurnedDown,
            deniedSuit: Suit.Spades, config: Support.Config(stick: true));
        Assert.Equal(
            "Your partner is Dale, in seat 3. The Jack of Spades was turned down, so spades cannot be named. " +
            "Marta is dealing and must name a suit if it reaches them. Nobody has bid yet this round.",
            EuchreReview.TrumpAndPartner(bid2, 0));
    }

    // MARK: C — cards played

    [Fact]
    public void Cards_played()
    {
        var s = Support.Position(EuchrePhase.Play, dealer: 3, turn: 1, trump: Suit.Spades, upcard: "JS",
            upcardStatus: EuchreUpcardStatus.Taken, maker: 0);
        s.Played = Support.Cards("AS", "9S", "KH", "JC");
        Assert.Equal(
            "Trump played: 3 of 7. Clubs: no cards played. Hearts: one card played. Diamonds: no cards played. " +
            "The upcard was the Jack of Spades.",
            EuchreReview.CardsPlayed(s, 0));
        var bidding = Support.Position(EuchrePhase.Bid1, dealer: 3, turn: 0, upcard: "JS", upcardStatus: EuchreUpcardStatus.Up);
        Assert.Equal(
            "Trump has not been decided yet, so there is nothing to count. The upcard is the Jack of Spades.",
            EuchreReview.CardsPlayed(bidding, 0));
    }

    // MARK: O — play order

    [Fact]
    public void Play_order()
    {
        var s = Support.Position(EuchrePhase.Play, dealer: 0, turn: 2, leader: 1, trump: Suit.Spades, maker: 1, trick: [(1, "AS")]);
        Assert.Equal(
            "Play order for this trick, starting with the lead. 1, Ruth, maker, played the Ace of Spades. " +
            "2, Dale, your partner, to play. 3, Marta, to play. 4, You, dealer, to play. You play last. " +
            "The maker plays three places before you. Nobody plays after you.",
            EuchreReview.PlayOrder(s, 0));

        var lead = Support.Position(EuchrePhase.Play, dealer: 3, turn: 1, leader: 1, trump: Suit.Hearts, maker: 2, alone: true,
            sittingOut: 0, config: Support.Config(names: ["Kim", "Ruth", "Dale", "Marta"]));
        Assert.Equal(
            "Play order for this trick, starting with the lead. 1, Ruth, to play. 2, Dale, maker, alone, your partner, to play. " +
            "3, Marta, dealer, to play. You are sitting out, so there are only three cards to this trick.",
            EuchreReview.PlayOrder(lead, 0));
        Assert.Equal(
            "Play order for this trick, starting with the lead. 1, Ruth, to play. 2, Dale, maker, alone, to play. " +
            "3, Marta, dealer, your partner, to play. Kim is sitting out, so there are only three cards to this trick. " +
            "You lead. The maker plays one place after you. 2 players play after you: Dale, Marta.",
            EuchreReview.PlayOrder(lead, 1));

        var b = Support.Position(EuchrePhase.Bid1, dealer: 3, turn: 2, upcard: "JS", upcardStatus: EuchreUpcardStatus.Up);
        b.BidLog = [new EuchreBid(0, EuchreBidKind.Pass, 1, Words: "pass"), new EuchreBid(1, EuchreBidKind.Pass, 1, Words: "pass")];
        Assert.Equal(
            "Bidding order, starting to the dealer's left. 1, You, passed. 2, Ruth, passed. " +
            "3, Dale, your partner, deciding now. 4, Marta, dealer, still to decide.",
            EuchreReview.PlayOrder(b, 0));

        var discard = Support.Position(EuchrePhase.Discard, dealer: 1, turn: 1, trump: Suit.Spades, maker: 3);
        Assert.Equal("Seating order: You, Ruth, dealer, Dale, your partner, Marta, maker.", EuchreReview.PlayOrder(discard, 0));
    }

    // MARK: The status line and the turn prompt

    [Fact]
    public void Status_variants()
    {
        Assert.Equal("No hand has been dealt yet.", EuchreReview.Status(EuchreGame.CreateGame(Support.Config()), 0));

        var bid1You = Support.Position(EuchrePhase.Bid1, dealer: 3, turn: 0, upcard: "JS", upcardStatus: EuchreUpcardStatus.Up);
        Assert.Equal("Bidding, round one. The upcard is the Jack of Spades. Your turn: order it up or pass?", EuchreReview.Status(bid1You, 0));
        var bid1Ruth = Support.Position(EuchrePhase.Bid1, dealer: 3, turn: 1, upcard: "JS", upcardStatus: EuchreUpcardStatus.Up);
        Assert.Equal("Bidding, round one. The upcard is the Jack of Spades. Waiting for Ruth.", EuchreReview.Status(bid1Ruth, 0));

        var bid2 = Support.Position(EuchrePhase.Bid2, dealer: 3, turn: 0, upcard: "JS", upcardStatus: EuchreUpcardStatus.TurnedDown,
            deniedSuit: Suit.Spades);
        Assert.Equal("Bidding, round two. Spades turned down. Your turn: name a suit or pass?", EuchreReview.Status(bid2, 0));
        Assert.Equal("Bidding, round two. Spades turned down. Waiting for You.", EuchreReview.Status(bid2, 2));

        Assert.Equal("You took the upcard. Put one card back.",
            EuchreReview.Status(Support.Position(EuchrePhase.Discard, dealer: 0, turn: 0, trump: Suit.Spades), 0));
        Assert.Equal("Ruth took the upcard and is putting a card back.",
            EuchreReview.Status(Support.Position(EuchrePhase.Discard, dealer: 1, turn: 1, trump: Suit.Spades), 0));

        var play = Support.Position(EuchrePhase.Play, dealer: 3, turn: 0, trump: Suit.Spades, maker: 0);
        Assert.Equal("Spades are trump — trick 1 of 5 — your turn to play.", EuchreReview.Status(play, 0));
        var later = Snapshot.Clone(play);
        later.Turn = 1;
        later.TrickLog = [new EuchreTrick(1, [], 1), new EuchreTrick(2, [], 1)];
        Assert.Equal("Spades are trump — trick 3 of 5 — Ruth to play.", EuchreReview.Status(later, 0));
        var @out = Support.Position(EuchrePhase.Play, dealer: 3, turn: 1, trump: Suit.Spades, maker: 2, alone: true, sittingOut: 0);
        Assert.Equal("Spades are trump — trick 1 of 5 — you are sitting out this hand.", EuchreReview.Status(@out, 0));

        var over = Support.Position(EuchrePhase.HandOver);
        over.HandNumber = 3;
        over.Scores = [4, 2];
        Assert.Equal("Hand 3 complete. Score 4 to 2.", EuchreReview.Status(over, 0));
        Assert.Equal("Hand 3 complete. Score 2 to 4.", EuchreReview.Status(over, 1));

        var won = Support.Position(EuchrePhase.GameOver);
        won.HandNumber = 9;
        won.Scores = [10, 7];
        won.GameWinner = 0;
        Assert.Equal("Hand 9 complete. Game over — You and Dale win, 10 to 7.", EuchreReview.Status(won, 0));
    }

    [Fact]
    public void Turn_prompt()
    {
        var bid1 = Support.Position(EuchrePhase.Bid1, dealer: 3, turn: 0, upcard: "JS", upcardStatus: EuchreUpcardStatus.Up);
        Assert.Equal("Your turn to bid. The upcard is the Jack of Spades. Order it up to make spades trump, or pass.",
            EuchreReview.TurnPrompt(bid1, 0));
        Assert.Equal("", EuchreReview.TurnPrompt(bid1, 1));
        var bid2 = Support.Position(EuchrePhase.Bid2, dealer: 3, turn: 0, upcard: "JS", upcardStatus: EuchreUpcardStatus.TurnedDown,
            deniedSuit: Suit.Spades);
        Assert.Equal("Your turn to bid. Spades were turned down. Name another suit, or pass.", EuchreReview.TurnPrompt(bid2, 0));
        Assert.Equal("You took the upcard. Choose one card to put back.",
            EuchreReview.TurnPrompt(Support.Position(EuchrePhase.Discard, dealer: 0, turn: 0, trump: Suit.Spades), 0));
        Assert.Equal("Your lead. Trick 1 of 5.",
            EuchreReview.TurnPrompt(Support.Position(EuchrePhase.Play, dealer: 3, turn: 0, trump: Suit.Spades, maker: 0), 0));
        var follow = Support.Position(EuchrePhase.Play, dealer: 3, turn: 0, leader: 1, trump: Suit.Spades, maker: 1,
            trick: [(1, "KH"), (2, "AH"), (3, "9H")]);
        Assert.Equal("Your turn to play. Hearts led. Dale is winning with the Ace of Hearts.", EuchreReview.TurnPrompt(follow, 0));
        Assert.Equal("Choose Deal next hand.", EuchreReview.TurnPrompt(Support.Position(EuchrePhase.HandOver), 0));
        Assert.Equal("Choose Start a new game.", EuchreReview.TurnPrompt(Support.Position(EuchrePhase.GameOver), 0));
        var @out = Support.Position(EuchrePhase.Play, dealer: 3, turn: 1, trump: Suit.Spades, maker: 2, alone: true, sittingOut: 0);
        Assert.Equal("You are sitting out this hand while Dale plays alone.", EuchreReview.TurnPrompt(@out, 0));
    }

    // MARK: The end of the hand

    [Fact]
    public void Hand_result_and_the_deal_reveal()
    {
        var s = Support.Position(EuchrePhase.HandOver, dealer: 1, trump: Suit.Spades, upcard: "JS",
            upcardStatus: EuchreUpcardStatus.Taken, maker: 0, alone: true);
        s.Discard = C("9H");
        s.Dealt = new EuchreDeal([[], [], [], []], C("JS"), Support.Cards("TC", "QD", "AH"));
        s.Result = new EuchreResult
        {
            ThrownIn = false, MakerTeam = 0, Made = 5, Euchred = false, Alone = true, Trump = Suit.Spades,
            Maker = 0, Deltas = [4, 0], Label = "a march, alone — four", Scores = [4, 0],
        };
        Assert.Equal("You made spades, alone and took five tricks. Four points to you.", EuchreReview.HandResult(s, 0));
        Assert.Equal("You made spades, alone and took five tricks. Four points to them.", EuchreReview.HandResult(s, 1));
        Assert.Equal(
            "Ruth took the Jack of Spades and put back the Nine of Hearts. " +
            "Upcard: Jack of Spades, right bower, the highest trump. Put back: Nine of Hearts. " +
            "Kitty: Ten of Clubs, Queen of Diamonds and Ace of Hearts.",
            EuchreReview.DealReveal(s));

        s.Result = new EuchreResult
        {
            ThrownIn = false, MakerTeam = 1, Made = 2, Euchred = true, Alone = false, Trump = Suit.Spades,
            Maker = 1, Deltas = [2, 0], Label = "euchred — two to the other side", Scores = [2, 0],
        };
        s.Maker = 1;
        s.Alone = false;
        Assert.Equal("Ruth made spades and took two tricks. Euchred — two points to you.", EuchreReview.HandResult(s, 0));
        Assert.Equal("You made spades and took two tricks. You were euchred — two points against you.", EuchreReview.HandResult(s, 1));

        var thrown = Support.Position(EuchrePhase.HandOver, dealer: 1, upcard: "JS", upcardStatus: EuchreUpcardStatus.TurnedDown,
            deniedSuit: Suit.Spades);
        thrown.Dealt = new EuchreDeal([[], [], [], []], C("JS"), Support.Cards("TC", "QD", "AH"));
        thrown.Result = new EuchreResult { ThrownIn = true, Label = "thrown in" };
        Assert.Equal("Everybody passed twice, so the hand was thrown in. Nobody scored.", EuchreReview.HandResult(thrown, 0));
        Assert.Equal(
            "Nobody took the Jack of Spades and nobody named a suit, so the hand was thrown in. " +
            "Upcard: Jack of Spades. Kitty: Ten of Clubs, Queen of Diamonds and Ace of Hearts.",
            EuchreReview.DealReveal(thrown));

        Assert.Null(EuchreReview.DealReveal(Support.Position(EuchrePhase.Play, trump: Suit.Spades)));
    }

    [Fact]
    public void Deal_reveal_after_a_real_hand()
    {
        var rng = new RandomSource(8);
        var s = EuchreGame.CreateGame(Support.Config());
        Support.PlayHand(s, rng);
        var text = EuchreReview.DealReveal(s);
        Assert.NotNull(text);
        var dealt = s.Dealt;
        Assert.NotNull(dealt);
        foreach (var k in dealt.Kitty) Assert.Contains(k.Name, text);
        Assert.Contains(dealt.Upcard.Name, text);
        if (s.Discard is { } d) Assert.Contains($"put back the {d.Name}", text);
    }

    [Fact]
    public void Hand_summary_event_wording()
    {
        var s = Support.Position(EuchrePhase.Play, dealer: 3, turn: 0, trump: Suit.Spades, hands: [["AS"], ["9H"], ["TH"], ["KD"]],
            upcard: "TS", upcardStatus: EuchreUpcardStatus.Taken, maker: 1);
        s.TrickLog = Enumerable.Range(1, 4).Select(n => new EuchreTrick(n, [], 0)).ToList();
        s.Players[0].TricksWon = 4;
        var rng = new RandomSource(1);
        foreach (var (seat, id) in new[] { (0, "AS"), (1, "9H"), (2, "TH"), (3, "KD") })
            Assert.True(EuchreGame.ApplyAction(s, seat, new Play(C(id)), rng).IsOk);
        Assert.Equal(EuchrePhase.HandOver, s.Phase);
        Assert.Equal([2, 0], s.Result?.Deltas ?? []);
        Assert.Equal(
            "Ruth made spades and took 0 tricks. Ruth was euchred: two points to You and Dale. " +
            "Score: You and Dale 2, Ruth and Marta 0.",
            s.Log.Events.Last(e => e.Kind == EventKind.Hand).Text);
    }

    [Fact]
    public void Who_is_here()
    {
        var s = Support.Position(EuchrePhase.Play, dealer: 3, turn: 1, trump: Suit.Spades, maker: 0);
        Assert.Equal(
            "You are playing against the computer. Your partner is Dale. Seat 1, You, you. " +
            "Seat 2, Ruth, the computer, against you. Seat 3, Dale, the computer, your partner. " +
            "Seat 4, Marta, the computer, against you. Waiting for Ruth.",
            EuchreReview.WhoIsHere(s, 0));
    }

    [Fact]
    public void Help_text_is_plain_and_complete()
    {
        Assert.Equal(9, EuchreHelp.Rules.Count);
        Assert.Equal("How to play Euchre", EuchreHelp.Rules[0].Heading);
        foreach (var r in EuchreHelp.Rules)
        {
            Assert.DoesNotContain("<", r.Body);
            Assert.DoesNotContain("press ", r.Body.ToLowerInvariant());
        }
        Assert.Contains(EuchreHelp.Rules, r => r.Body.Contains("All five, playing alone: 4 points to the makers."));
    }

    // Not in the Swift tests: the play hint, which Swift ports but never checks.

    [Fact]
    public void Play_hint()
    {
        Assert.Equal("", EuchreReview.PlayHint(Support.Position(EuchrePhase.Bid1), 0));
        Assert.Equal("Lead any card from your hand. Spades are trump.",
            EuchreReview.PlayHint(Support.Position(EuchrePhase.Play, trump: Suit.Spades), 0));
        Assert.Equal("Trump was led. Follow with trump if you have any — including the left bower, the Jack of Clubs.",
            EuchreReview.PlayHint(Support.Position(EuchrePhase.Play, turn: 1, trump: Suit.Spades, trick: [(0, "JC")]), 1));
        Assert.Equal("Follow hearts if you can.",
            EuchreReview.PlayHint(Support.Position(EuchrePhase.Play, turn: 1, trump: Suit.Spades, trick: [(0, "AH")]), 1));
    }
}
