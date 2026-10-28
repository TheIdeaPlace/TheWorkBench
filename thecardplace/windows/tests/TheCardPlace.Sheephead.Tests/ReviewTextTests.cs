using static TheCardPlace.Sheephead.Tests.Support;

namespace TheCardPlace.Sheephead.Tests;

public class ReviewTextTests
{
    /// <summary>
    /// Five players, seat 4 dealt, trick 1 about to be led by seat 0, with Alice the picker and
    /// Ben her (hidden) partner, and known hands.
    /// </summary>
    private static SheepheadState Position()
    {
        var (state, _) = FiveHanded();
        state.Phase = SheepheadPhase.Play;
        state.Picker = 1;
        state.Partner = 0;
        state.Alone = false;
        state.PartnerRevealed = false;
        state.Buried = Cards("AH", "TH");
        state.Players[0].Hand = SheepheadCards.SortHand(Cards("QC", "AC", "9H", "JD"));
        state.Players[1].Hand = Cards("QS", "TC", "KC", "8H");
        state.Players[2].Hand = Cards("JC", "AS", "TS", "7H");
        state.Players[3].Hand = Cards("QH", "AD", "KS", "9S");
        state.Players[4].Hand = Cards("QD", "TD", "9C", "8S");
        state.Players[0].Hand = Cards("QC", "JD", "AC", "9H");
        state.Trick = [];
        state.Turn = 0;
        state.Leader = 0;
        return state;
    }

    [Fact]
    public void Card_descriptions()
    {
        Assert.Equal("Queen of Clubs, trump, 3 points", SheepheadCards.Describe(Cd("QC")));
        Assert.Equal("Jack of Diamonds, trump, 2 points", SheepheadCards.Describe(Cd("JD")));
        Assert.Equal("Seven of Diamonds, trump, 0 points", SheepheadCards.Describe(Cd("7D")));
        Assert.Equal("Ace of Clubs, Clubs fail, 11 points", SheepheadCards.Describe(Cd("AC")));
        Assert.Equal("Nine of Hearts, Hearts fail, 0 points", SheepheadCards.Describe(Cd("9H")));
        Assert.Equal("Spades fail, 10 points", SheepheadCards.Role(Cd("TS")));
        Assert.Equal(["QC", "JD", "AC", "7C", "TS", "AH", "9H"],
            SheepheadCards.SortHand(Cards("9H", "AC", "JD", "7C", "QC", "AH", "TS")).Ids());
    }

    [Fact]
    public void The_jack_of_diamonds_says_what_it_means_for_your_own_seat()
    {
        var state = Position();
        Assert.Equal("Jack of Diamonds, trump, 2 points, the partner card, so you are the picker's partner",
            SheepheadReview.Describe(Cd("JD"), state, 0));
        Assert.Equal("Queen of Clubs, trump, 3 points", SheepheadReview.Describe(Cd("QC"), state, 0));
        // Asked about a seat that does not hold it, nothing is added.
        Assert.Equal("Jack of Diamonds, trump, 2 points", SheepheadReview.Describe(Cd("JD"), state, 1));
        state.Picker = 0;
        Assert.Equal("Jack of Diamonds, trump, 2 points, the partner card, so you are playing alone",
            SheepheadReview.Describe(Cd("JD"), state, 0));
        state.Picker = null;
        state.Phase = SheepheadPhase.Pick;
        Assert.Equal("Jack of Diamonds, trump, 2 points, the partner card", SheepheadReview.Describe(Cd("JD"), state, 0));
        // At three players there is no partner card to speak of.
        var (three, _) = NewGame(3, 1);
        three.Players[0].Hand = Cards("JD", "AC");
        Assert.Equal("Jack of Diamonds, trump, 2 points", SheepheadReview.Describe(Cd("JD"), three, 0));
    }

    [Fact]
    public void Hand()
    {
        var state = Position();
        Assert.Equal("Your hand, 4 cards. Trump: Queen of Clubs, Jack of Diamonds. Non-trump: Ace of Clubs, Nine of Hearts. Worth 16 points. You hold the Jack of Diamonds, the partner card, so you are the picker's partner.",
            SheepheadReview.Hand(state, 0));
        Assert.Equal("Your hand, 4 cards. Trump: Queen of Clubs, Jack of Diamonds. Non-trump: Ace of Clubs, Nine of Hearts. You hold the Jack of Diamonds, the partner card, so you are the picker's partner.",
            SheepheadReview.Hand(state, 0, verbose: false));
        state.Players[0].Hand = Cards("AC", "9H");
        Assert.Equal("Your hand, 2 cards. No trump. Non-trump: Ace of Clubs, Nine of Hearts. Worth 11 points.", SheepheadReview.Hand(state, 0));
        state.Players[0].Hand = Cards("QC");
        Assert.Equal("Your hand, 1 card. Trump: Queen of Clubs. No non-trump cards. Worth 3 points.", SheepheadReview.Hand(state, 0));
        state.Players[0].Hand = [];
        Assert.Equal("Your hand is empty.", SheepheadReview.Hand(state, 0));
    }

    [Fact]
    public void Hand_after_picking_leads_with_the_blind()
    {
        var (state, rng) = FiveHanded();
        state.Blind = Cards("AS", "TS");
        state.Players[0].Hand = SheepheadCards.SortHand(Cards("QC", "JD", "AC", "9H", "KS", "7C"));
        Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.Pick(), rng));
        Assert.Equal(["AS", "TS"], state.Players[0].Hand.Take(2).Ids());   // the blind sits at the front, unsorted
        Assert.Equal("From the blind: Ace of Spades, Ten of Spades. Then your hand. Your hand, 8 cards. Trump: Queen of Clubs, Jack of Diamonds. Non-trump: Ace of Clubs, Seven of Clubs, Ace of Spades, Ten of Spades, King of Spades, Nine of Hearts. Worth 41 points. You hold the Jack of Diamonds, the partner card, so you are playing alone.",
            SheepheadReview.Hand(state, 0));
        Assert.Equal("Ace of Spades, Spades fail, 11 points, from the blind", SheepheadReview.Describe(Cd("AS"), state, 0));
        Assert.Equal("You picked. Bury 2 cards.", SheepheadReview.Status(state, 0));
        Assert.Equal("You picked and is burying.", SheepheadReview.Status(state, 1));
        Assert.Equal("You picked. Choose 2 cards to bury, then choose Bury.", SheepheadReview.Prompt(state, 0));
        Assert.Equal("From the blind: Ace of Spades, Ten of Spades.", state.Log.EventsFor(0)[^1].Text);
        Assert.Equal("You pick up the blind (2 cards).", state.Log.EventsFor(1)[^1].Text);
        Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.Bury(Cards("KS", "7C")), rng));
        Assert.Equal(["QC", "JD", "AC", "AS", "TS", "9H"], state.Players[0].Hand.Ids());   // sorted again after the bury
        Assert.Equal("Ace of Spades, Spades fail, 11 points", SheepheadReview.Describe(Cd("AS"), state, 0));
        Assert.Equal(
            [
                "You bury 2 cards.",
                "You buried 4 points.",
                "You are the picker. The Jack of Diamonds is the partner card.",
                "You have the Jack of Diamonds yourself, so you are playing alone — nobody else knows that yet.",
            ],
            state.Log.EventsFor(0).Select(e => e.Text).TakeLast(4));
        Assert.Equal("This hand: You 0, 0 tricks. Alice 0, 0 tricks. Ben 0, 0 tricks. Cara 0, 0 tricks. Elle 0, 0 tricks. You buried 4 points. Running score: You 0, Alice 0, Ben 0, Cara 0, Elle 0.",
            SheepheadReview.Scores(state, 0));
    }

    [Fact]
    public void Trick_and_last_trick()
    {
        var state = Position();
        Assert.Equal("Trick 3 of 6. Nothing played yet. You to lead.", SheepheadReview.Trick(state, 0));
        Assert.Equal("Nothing played to this trick yet.", SheepheadReview.TrickShort(state));
        Assert.Equal("No trick has been completed yet this hand.", SheepheadReview.LastTrick(state, 0));
        state.Trick = [new SheepheadPlay(1, Cd("QS")), new SheepheadPlay(2, Cd("AS"))];
        state.Turn = 3;
        Assert.Equal("Trick 3 of 6. Alice, Queen of Spades, trump, 3 points. Ben, Ace of Spades, Spades fail, 11 points. Trump led. Alice is winning with Queen of Spades. 14 points in the trick so far.",
            SheepheadReview.Trick(state, 0));
        Assert.Equal("Trick 3 of 6. Alice, Queen of Spades. Ben, Ace of Spades. Trump led. Alice is winning with Queen of Spades. 14 points in the trick so far.",
            SheepheadReview.Trick(state, 0, verbose: false));
        state.Trick = [new SheepheadPlay(1, Cd("KC"))];
        Assert.Equal("Clubs led. Alice is winning with King of Clubs. 4 points in the trick so far.", SheepheadReview.TrickShort(state));
        state.LastTrick = new SheepheadTrick([new SheepheadPlay(0, Cd("9H")), new SheepheadPlay(1, Cd("8H"))], 0, 0, 0);
        Assert.Equal("Last trick. You, Nine of Hearts. Alice, Eight of Hearts. You took it for 0 points.", SheepheadReview.LastTrick(state, 0));
    }

    [Fact]
    public void Scores()
    {
        var state = Position();
        state.Players[1].Points = 14;
        state.Players[1].TricksWon = 1;
        state.Players[3].Score = -4;
        state.Players[1].Score = 4;
        Assert.Equal("This hand: You 0, 0 tricks. Alice 14, 1 trick. Ben 0, 0 tricks. Cara 0, 0 tricks. Elle 0, 0 tricks. Running score: You 0, Alice 4, Ben 0, Cara -4, Elle 0.",
            SheepheadReview.Scores(state, 0));
    }

    [Fact]
    public void Picker()
    {
        var state = Position();
        Assert.Equal("Alice is the picker. You hold the Jack of Diamonds, so you are the secret partner. Nobody else knows yet.", SheepheadReview.Picker(state, 0));
        Assert.Equal("Alice is the picker. The Jack of Diamonds has not been played, so the partner is still unknown — and the picker may be holding it and playing alone.", SheepheadReview.Picker(state, 3));
        Assert.Equal("You are the picker. Somebody else holds the Jack of Diamonds and is your secret partner.", SheepheadReview.Picker(state, 1));
        state.PartnerRevealed = true;
        Assert.Equal("Alice is the picker. You are the partner.", SheepheadReview.Picker(state, 0));
        Assert.Equal("Alice is the picker. You is the partner.", SheepheadReview.Picker(state, 3));
        state.Alone = true;
        state.Partner = null;
        Assert.Equal("Alice is the picker. The picker is playing alone.", SheepheadReview.Picker(state, 3));
        state.PartnerRevealed = false;
        state.Picker = 0;
        state.Players[0].Hand = Cards("QC", "AC");
        Assert.Equal("You are the picker. You have the Jack of Diamonds yourself, so you are playing alone. Nobody else knows that yet.", SheepheadReview.Picker(state, 0));
        state.Phase = SheepheadPhase.Pick;
        state.Turn = 2;
        Assert.Equal("Nobody has picked yet. Ben is deciding.", SheepheadReview.Picker(state, 0));
        state.Phase = SheepheadPhase.Play;
        state.IsLeaster = true;
        Assert.Equal("Leaster. There is no picker; everyone plays for themselves and the fewest points wins. You must take at least one trick to be eligible.", SheepheadReview.Picker(state, 0));
        var (three, _) = NewGame(3, 1);
        three.Phase = SheepheadPhase.Play;
        three.Picker = 2;
        three.Alone = true;
        three.PartnerRevealed = true;
        Assert.Equal("Ben is the picker. With 3 players the picker always plays alone.", SheepheadReview.Picker(three, 0));
    }

    [Fact]
    public void Cards_played()
    {
        var state = Position();
        Assert.Equal("Trump played: 0 of 14. Clubs: 0 of 6 played. Spades: 0 of 6 played. Hearts: 0 of 6 played.", SheepheadReview.CardsPlayed(state, 0));
        state.Played = Cards("QS", "AS", "KC", "7D", "AH");
        Assert.Equal("Trump played: 2 of 14. Clubs: 1 of 6 played. Spades: 1 of 6 played. Hearts: 1 of 6 played.", SheepheadReview.CardsPlayed(state, 0));
        var (four, _) = NewGame(4, 1);
        four.Played = Cards("QC");
        Assert.Equal("Trump played: 1 of 12. Clubs: 0 of 6 played. Spades: 0 of 6 played. Hearts: 0 of 6 played.", SheepheadReview.CardsPlayed(four, 0));
    }

    [Fact]
    public void Play_order()
    {
        var state = Position();
        Assert.Equal("Play order for this trick, starting with the lead. 1, You, partner, to play. 2, Alice, picker, to play. 3, Ben, to play. 4, Cara, to play. 5, Elle, dealer, to play. You lead. The picker plays one place after you. 4 players play after you: Alice, Ben, Cara, Elle.",
            SheepheadReview.PlayOrder(state, 0));
        state.Trick = [new SheepheadPlay(0, Cd("AC")), new SheepheadPlay(1, Cd("KC"))];
        state.Turn = 2;
        Assert.Equal("Play order for this trick, starting with the lead. 1, You, played Ace of Clubs. 2, Alice, picker, played King of Clubs. 3, Ben, to play. 4, Cara, to play. 5, Elle, dealer, to play. The picker plays two places before you. 1 player plays after you: Elle.",
            SheepheadReview.PlayOrder(state, 3));
        state.PartnerRevealed = true;
        state.Trick = [new SheepheadPlay(4, Cd("9C"))];
        state.Turn = 0;
        Assert.Equal("Play order for this trick, starting with the lead. 1, Elle, dealer, played Nine of Clubs. 2, You, partner, to play. 3, Alice, picker, to play. 4, Ben, to play. 5, Cara, to play. You are the picker. 2 players play after you: Ben, Cara.",
            SheepheadReview.PlayOrder(state, 1));
        // Seat 3 plays last: "You play last" and nobody after.
        Assert.Equal("Play order for this trick, starting with the lead. 1, Elle, dealer, played Nine of Clubs. 2, You, partner, to play. 3, Alice, picker, to play. 4, Ben, to play. 5, Cara, to play. You play last. The picker plays two places before you. Nobody plays after you.",
            SheepheadReview.PlayOrder(state, 3));

        var (pick, _) = FiveHanded();
        pick.PickLog = [new SheepheadPickEntry(0, false)];
        pick.Turn = 1;
        pick.PassCount = 1;
        Assert.Equal("Picking order, starting to the dealer's left. 1, You, passed. 2, Alice, deciding now. 3, Ben, still to decide. 4, Cara, still to decide. 5, Elle, dealer, still to decide.",
            SheepheadReview.PlayOrder(pick, 0));
        pick.Phase = SheepheadPhase.Bury;
        Assert.Equal("Seating order: You, Alice, Ben, Cara, Elle.", SheepheadReview.PlayOrder(pick, 0));
    }

    [Fact]
    public void Status_and_prompt()
    {
        var idle = SheepheadGame.CreateGame(new SheepheadConfig());
        Assert.Equal("Ready to start.", SheepheadReview.Status(idle, 0));
        Assert.Equal("", SheepheadReview.Prompt(idle, 0));

        var (state, _) = FiveHanded();
        Assert.Equal("Your turn: pick up the blind (2 cards) or pass?", SheepheadReview.Status(state, 0));
        Assert.Equal("Waiting for You to pick or pass.", SheepheadReview.Status(state, 1));
        Assert.Equal("Your turn. Pick up the blind of 2 cards, or pass?", SheepheadReview.Prompt(state, 0));
        Assert.Equal("", SheepheadReview.Prompt(state, 1));
        state.Turn = 2;
        Assert.Equal("Waiting for Ben to pick or pass.", SheepheadReview.Status(state, 0));

        var play = Position();
        Assert.Equal("Trick 3 of 6 — your turn to play.", SheepheadReview.Status(play, 0));
        Assert.Equal("Your lead. Trick 3 of 6.", SheepheadReview.Prompt(play, 0));
        play.Turn = 1;
        Assert.Equal("Trick 3 of 6 — Alice to play.", SheepheadReview.Status(play, 0));
        play.Trick = [new SheepheadPlay(1, Cd("TC"))];
        play.Turn = 0;
        Assert.Equal("Your turn to play. Clubs led. Alice is winning with Ten of Clubs. 10 points in the trick so far.", SheepheadReview.Prompt(play, 0));
        play.Phase = SheepheadPhase.HandOver;
        play.HandNumber = 2;
        Assert.Equal("Hand 2 complete.", SheepheadReview.Status(play, 0));
        Assert.Equal("Choose Deal next hand.", SheepheadReview.Prompt(play, 0));
    }

    [Fact]
    public void Hand_over_summary_shows_the_blind_and_the_bury()
    {
        var (state, rng) = FiveHanded(12);
        state.Blind = Cards("AS", "TS");
        state.Players[0].Hand = SheepheadCards.SortHand(Cards("QC", "QS", "JC", "JD", "AC", "9H"));
        // Give the rest of the deck out so the deal still adds up.
        var rest = SheepheadCards.Deck(5).Where(c => !state.Blind.Contains(c) && !state.Players[0].Hand.Contains(c)).ToList();
        for (var i = 1; i < 5; i++) state.Players[i].Hand = SheepheadCards.SortHand(rest.GetRange((i - 1) * 6, 6));
        state.Dealt = new SheepheadDeal(state.Players.Select(p => p.Hand.ToList()).ToList(), [.. state.Blind]);
        Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.Pick(), rng));
        Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.Bury(Cards("AS", "TS")), rng));
        PlayHand(state, rng);
        var r = state.Result!;
        Assert.False(r.Leaster);
        Assert.StartsWith($"Hand over. You alone took {r.PickerPoints} points (including 21 buried); the defenders took {r.OpponentPoints}. The blind held Ace of Spades and Ten of Spades. Buried: Ace of Spades, Ten of Spades (21 points). The picker's team {(r.PickerWins ? "wins" : "loses")} — {r.Label}. ",
            r.Summary, StringComparison.Ordinal);
        Assert.EndsWith(".", r.Summary, StringComparison.Ordinal);
        Assert.Equal("The blind, and what You buried (21 points, counted for the picker's team). Blind: Ace of Spades, Ten of Spades. Buried: Ace of Spades, Ten of Spades.",
            SheepheadReview.BlindReveal(state));
        Assert.Equal($"You {(r.PickerWins ? "win" : "lose")} alone — {r.Label}.", SheepheadReview.ResultHeadline(state, 0));
        Assert.Equal($"You {(r.PickerWins ? "wins" : "loses")} alone — {r.Label}.", SheepheadReview.ResultHeadline(state, 1));
        Assert.Equal(EventKind.Score, state.Log.Events[^1].Kind);
        Assert.Equal(Cards("AS", "TS", "AS", "TS"), state.Log.Events[^1].Cards);
    }

    [Fact]
    public void Leaster_reveal()
    {
        var (state, rng) = NewGame(5, 11);
        for (var k = 0; k < 5; k++)
        {
            var seat = SheepheadGame.SeatToAct(state)!.Value;
            Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, seat, new SheepheadAction.Pass(), rng));
        }
        var blind = state.Blind.ToList();
        PlayHand(state, rng);
        var winner = state.Players[state.TrickLog[^1].Winner].Name;
        Assert.Equal($"Nobody picked, so the blind was worth {SheepheadCards.SumPoints(blind)} to {winner} with the last trick. Blind: {string.Join(", ", blind.Select(c => c.Name))}.",
            SheepheadReview.BlindReveal(state));
        Assert.Contains($"The blind held {blind[0].Name} and {blind[1].Name}, worth {SheepheadCards.SumPoints(blind)}, and went to {winner} with the last trick.",
            state.Result!.Summary, StringComparison.Ordinal);
        Assert.Equal($"{state.Players[state.Result.Winners[0]].Name} wins the leaster with the fewest points.", SheepheadReview.ResultHeadline(state, 0));
        Assert.Equal("", SheepheadReview.BlindReveal(Position()));
    }

    [Fact]
    public void Help()
    {
        Assert.Equal(8, SheepheadHelp.Rules.Count);
        Assert.Equal("The idea", SheepheadHelp.Rules[0].Heading);
        Assert.Contains("queen of clubs, queen of spades, queen of hearts, queen of diamonds, jack of clubs, jack of spades, jack of hearts, jack of diamonds, then ace, ten, king, nine, eight and seven of diamonds",
            SheepheadHelp.Rules[1].Body, StringComparison.Ordinal);
        foreach (var (_, body) in SheepheadHelp.Rules)
        {
            Assert.DoesNotContain("<", body, StringComparison.Ordinal);   // no markup
            Assert.DoesNotContain("press ", body.ToLowerInvariant(), StringComparison.Ordinal);   // no key references
        }
    }
}
