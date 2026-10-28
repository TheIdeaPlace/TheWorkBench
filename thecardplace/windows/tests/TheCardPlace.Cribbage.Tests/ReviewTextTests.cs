using static TheCardPlace.Cribbage.Tests.T;

namespace TheCardPlace.Cribbage.Tests;

public sealed class ReviewTextTests
{
    /// <summary>
    /// A position in the play: Ruth dealt; Ruth led the Eight of Hearts, you answered the Seven of
    /// Diamonds for fifteen; count 15, your turn.
    /// </summary>
    private static CribbageState MidPlay()
    {
        var s = CribbageGame.CreateGame(Sim.Config(Difficulty.Normal, 121));
        s.Phase = CribbagePhase.Play;
        s.GameNumber = 1;
        s.HandNumber = 3;
        s.Dealer = 1;
        s.Turn = 0;
        s.Starter = C("QS");
        s.Players[0].Kept = Cs("7C", "7D", "KS", "4H");
        s.Players[0].Hand = Cs("7C", "KS", "4H");
        s.Players[0].Played = Cs("7D");
        s.Players[1].Kept = Cs("8H", "5C", "TD", "AS");
        s.Players[1].Hand = Cs("5C", "TD", "AS");
        s.Players[1].Played = Cs("8H");
        s.Pile = [new(1, C("8H")), new(0, C("7D"))];
        s.RunStart = 0;
        s.Count = 15;
        s.Players[0].Score = 45;
        s.Players[1].Score = 39;
        s.Crib = Cs("2C", "3C", "9D", "JH");
        s.Discarded = [Cs("2C", "3C"), Cs("9D", "JH")];
        var used = s.Players[0].Kept.Concat(s.Players[1].Kept).Concat(s.Crib).Append(s.Starter.Value).ToHashSet();
        s.Deck = Card.FullDeck.Where(c => !used.Contains(c)).ToList();
        return s;
    }

    [Fact]
    public void CardLabelsDuringThePlay()
    {
        var s = MidPlay();
        Assert.Equal("Seven of Clubs, worth seven, makes twenty-two, and scores a pair for two",
                     CribbageReview.CardLabel(s, 0, C("7C")));
        Assert.Equal("King of Spades, worth ten, makes twenty-five", CribbageReview.CardLabel(s, 0, C("KS")));
        Assert.Equal("Four of Hearts, worth four, makes nineteen", CribbageReview.CardLabel(s, 0, C("4H")));
        var big = Snapshot.Clone(s);
        big.Count = 24;
        Assert.Equal("King of Spades, cannot be played, it would take the count to thirty-four, past thirty-one",
                     CribbageReview.CardLabel(big, 0, C("KS")));
        Assert.Equal("Seven of Clubs, worth seven, makes thirty-one, and scores thirty-one for two and a pair for two",
                     CribbageReview.CardLabel(big, 0, C("7C")));
        // Not your turn: for review only.
        var theirs = Snapshot.Clone(s);
        theirs.Turn = 1;
        Assert.Equal("Seven of Clubs, for review, Ruth is to play", CribbageReview.CardLabel(theirs, 0, C("7C")));
        // A card that is not yours cannot be played.
        Assert.Equal("Five of Clubs, cannot be played, that card is not in your hand", CribbageReview.CardLabel(s, 0, C("5C")));
    }

    [Fact]
    public void HandDuringThePlay()
    {
        var s = MidPlay();
        Assert.Equal("The count is 15. Your three cards: Four of Hearts, worth four, makes nineteen. " +
                     "Seven of Clubs, worth seven, makes twenty-two, and scores a pair for two. " +
                     "King of Spades, worth ten, makes twenty-five.",
                     CribbageReview.Hand(s, 0));
        var big = Snapshot.Clone(s);
        big.Count = 24;
        Assert.Equal("The count is 24. Your three cards: Four of Hearts, worth four, makes twenty-eight. " +
                     "Seven of Clubs, worth seven, makes thirty-one, and scores thirty-one for two and a pair for two. " +
                     "King of Spades, too big to play.",
                     CribbageReview.Hand(big, 0));
        var empty = Snapshot.Clone(s);
        empty.Players[0].Hand = [];
        Assert.Equal("You have played all four. You kept Four of Hearts, Seven of Clubs, Seven of Diamonds and King of Spades.",
                     CribbageReview.Hand(empty, 0));
    }

    [Fact]
    public void HandAtTheDiscard()
    {
        var s = CribbageGame.CreateGame(Sim.Config(Difficulty.Normal, 121));
        s.Phase = CribbagePhase.Discard;
        s.Dealer = 1;
        s.Turn = 0;
        s.Players[0].Hand = Cs("KS", "5C", "AH", "TD", "5D", "9C");
        Assert.Equal("Your six cards: Ace of Hearts, Five of Clubs, Five of Diamonds, Nine of Clubs, Ten of Diamonds, King of Spades. Two of them go to Ruth’s crib.",
                     CribbageReview.Hand(s, 0));
        Assert.Equal("King of Spades", CribbageReview.CardLabel(s, 0, C("KS")));
        Assert.Equal("Throw two cards to Ruth’s crib.", CribbageReview.Status(s, 0));
        Assert.Equal("Choose two cards to throw to Ruth’s crib.", CribbageReview.Prompt(s, 0));
        s.Dealer = 0;
        Assert.Equal("Throw two cards to your crib.", CribbageReview.Status(s, 0));
        s.Discarded[0] = Cs("KS", "TD");
        s.Players[0].Hand = Cs("5C", "AH", "5D", "9C");
        Assert.Equal("Your two are in the crib. Waiting for Ruth.", CribbageReview.Status(s, 0));
        Assert.Equal("Five of Clubs, for review, your throw is already in", CribbageReview.CardLabel(s, 0, C("5C")));
        Assert.Equal("You dealt, so it is your crib. The starter has not been turned yet. Your two are in the crib; waiting for Ruth.",
                     CribbageReview.DealerAndCrib(s, 0));
        Assert.Equal("", CribbageReview.Prompt(s, 0));
    }

    [Fact]
    public void PlayAndCountingAid()
    {
        var s = MidPlay();
        Assert.Equal("The count is 15. Down this run: Ruth the Eight of Hearts, then you the Seven of Diamonds. " +
                     "You could score a pair for two with the Seven of Clubs.",
                     CribbageReview.Play(s, 0));
        Assert.Equal(CribbageReview.Play(s, 0), CribbageReview.Trick(s, 0));
        Assert.Equal("Played this hand: Ruth the Eight of Hearts, you the Seven of Diamonds. The count is 15.",
                     CribbageReview.CountingAid(s, 0));

        // After a reset the earlier cards are only in the counting aid.
        var r = Snapshot.Clone(s);
        r.Pile.Add(new(1, C("TD")));
        r.Pile.Add(new(0, C("4H")));
        r.RunStart = 4;
        r.Count = 0;
        r.Turn = 1;
        r.Players[0].Hand = Cs("7C", "KS");
        Assert.Equal("The count is 0. Nothing down since the count reset. Ruth to lead.", CribbageReview.Play(r, 0));
        Assert.Equal("Played this hand: Ruth the Eight of Hearts, you the Seven of Diamonds, Ruth the Ten of Diamonds, you the Four of Hearts. The count is 0.",
                     CribbageReview.CountingAid(r, 0));
        r.Pile.Add(new(1, C("AS")));
        r.Count = 1;
        r.Turn = 0;
        Assert.Equal("Played this hand: Ruth the Eight of Hearts, you the Seven of Diamonds, Ruth the Ten of Diamonds, you the Four of Hearts, the count reset, then Ruth the Ace of Spades. The count is 1.",
                     CribbageReview.CountingAid(r, 0));
        Assert.Equal("The count is 1. Down this run: Ruth the Ace of Spades.", CribbageReview.Play(r, 0));

        // Nothing fits: you must say go.
        var g = Snapshot.Clone(s);
        g.Count = 28;
        g.Players[0].Hand = Cs("7C", "KS");
        Assert.Equal("The count is 28. Down this run: Ruth the Eight of Hearts, then you the Seven of Diamonds. " +
                     "Nothing in your hand fits under thirty-one, so you must say go.",
                     CribbageReview.Play(g, 0));
        Assert.Equal("You cannot play. Say go.", CribbageReview.Prompt(g, 0));

        var idle = CribbageGame.CreateGame(Sim.Config(Difficulty.Normal, 121));
        Assert.Equal("The play has not started yet.", CribbageReview.Play(idle, 0));
        Assert.Equal("The play has not started yet.", CribbageReview.CountingAid(idle, 0));
    }

    [Fact]
    public void ScoresAndStatus()
    {
        var s = MidPlay();
        Assert.Equal("You 45, Ruth 39, playing to 121. You need seventy-six more; Ruth needs eighty-two.",
                     CribbageReview.Scores(s, 0));
        Assert.Equal("The count is 15 — your turn.", CribbageReview.Status(s, 0));
        Assert.Equal("Your turn. The count is 15.", CribbageReview.Prompt(s, 0));
        var t = Snapshot.Clone(s);
        t.Turn = 1;
        Assert.Equal("The count is 15 — Ruth to play.", CribbageReview.Status(t, 0));
        Assert.Equal("", CribbageReview.Prompt(t, 0));
        t.GamesWon = [1, 2];
        Assert.Equal("You 45, Ruth 39, playing to 121. You need seventy-six more; Ruth needs eighty-two. Games won: you 1, Ruth 2.",
                     CribbageReview.Scores(t, 0));

        var idle = CribbageGame.CreateGame(Sim.Config(Difficulty.Normal, 61));
        Assert.Equal("Cribbage, playing to 61. Start the game to cut for deal.", CribbageReview.Status(idle, 0));
        Assert.Equal("Nobody has dealt yet. Cut for deal to begin.", CribbageReview.DealerAndCrib(idle, 0));
        idle.Phase = CribbagePhase.CutForDeal;
        Assert.Equal("Cut for deal — the lower card deals and takes the first crib.", CribbageReview.Status(idle, 0));
        idle.CutForDeal = new CribbageCut(Cs("TC", "TD"), true);
        Assert.Equal("Cut for deal — the lower card deals and takes the first crib. That was a tie; cut again.",
                     CribbageReview.Status(idle, 0));

        var c = Snapshot.Clone(s);
        c.Phase = CribbagePhase.Count;
        c.CountStage = 0;
        c.Turn = 0;
        Assert.Equal("Counting: the non-dealer’s hand — yours to count.", CribbageReview.Status(c, 0));
        Assert.Equal("Your hand to count.", CribbageReview.Prompt(c, 0));
        c.CountStage = 1;
        c.Turn = 1;
        Assert.Equal("Counting: the dealer’s hand — Ruth is counting.", CribbageReview.Status(c, 0));
        c.CountStage = 2;
        Assert.Equal("Counting: the crib — Ruth is counting.", CribbageReview.Status(c, 0));
        Assert.Equal("Ruth dealt, so the crib is theirs. The starter is the Queen of Spades. Counting: the crib next.",
                     CribbageReview.DealerAndCrib(c, 0));

        var over = Snapshot.Clone(s);
        over.Phase = CribbagePhase.RoundOver;
        over.CountStage = 3;
        Assert.Equal("Hand 3 complete. You 45, Ruth 39.", CribbageReview.Status(over, 0));
        Assert.Equal("Ruth dealt, so the crib is theirs. The starter is the Queen of Spades. The crib was Two of Clubs, Three of Clubs, Nine of Diamonds and Jack of Hearts.",
                     CribbageReview.DealerAndCrib(over, 0));

        var won = Snapshot.Clone(s);
        won.Phase = CribbagePhase.GameOver;
        won.GameWinner = 0;
        won.Players[0].Score = 121;
        won.Players[1].Score = 58;
        won.Result = new CribbageHandResult(true, 0, [121, 58], CribbageSkunk.Skunk, []);
        Assert.Equal("You win, 121 to 58.", CribbageReview.Status(won, 0));
        Assert.Equal("You win, 121 to 58 — a skunk.", CribbageReview.HandSummary(won, 0));
        won.GameWinner = 1;
        Assert.Equal("Ruth wins, 58 to 121.", CribbageReview.Status(won, 0));
    }

    [Fact]
    public void PlayOrder()
    {
        var s = MidPlay();
        Assert.Equal("Ruth dealt, so you lead the play and Ruth answers. At the count, you count first, then Ruth, then Ruth’s crib.",
                     CribbageReview.PlayOrder(s, 0));
        var mine = Snapshot.Clone(s);
        mine.Dealer = 0;
        Assert.Equal("You dealt, so Ruth leads the play and you answer. At the count, Ruth counts first, then you, then your crib.",
                     CribbageReview.PlayOrder(mine, 0));
    }

    [Fact]
    public void LastCountAndTheCountEvents()
    {
        var rng = new RandomSource(21);
        var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Normal, 121));
        Assert.Equal("Nothing has been counted yet.", CribbageReview.LastCount(state, 0));
        Assert.True(Advance(state, rng, s => s.Phase == CribbagePhase.Count));
        Assert.Equal("Nothing has been counted yet.", CribbageReview.LastCount(state, 0));
        var nonDealer = 1 - state.Dealer!.Value;
        Assert.Equal(ActionResult.Ok, CribbageGame.ApplyAction(state, nonDealer, new CribbageAction.Next(), rng));
        var kept = CribbageCards.SortHand(state.Players[nonDealer].Kept);
        var r = CribbageScoring.ScoreHand(kept, state.Starter, false);
        var whose = nonDealer == 0 ? "Your" : "Ruth’s";
        var want = $"{whose} hand: " + CribbageCards.ListNames(kept) + " with the " + state.Starter!.Value.Name + ". " +
                   CribbageGame.Cap(r.Spoken) + ".";
        Assert.Equal(want, CribbageReview.LastCount(state, 0));
        var countEvent = state.Log.Events.Last(e => e.Kind == EventKind.Count);
        Assert.Equal(kept, countEvent.Cards);
        if (r.Total > 0)
        {
            var scoreEvent = state.Log.Events.Last(e => e.Kind == EventKind.Score);
            var name = nonDealer == 0 ? "You score " : "Ruth scores ";
            Assert.StartsWith(name + CribbageCards.NumberWord(r.Total) + " for the hand. ", scoreEvent.Text);
            Assert.EndsWith($"You {state.Players[0].Score}, Ruth {state.Players[1].Score}.", scoreEvent.Text);
        }
    }

    [Fact]
    public void EventWording()
    {
        var rng = new RandomSource(33);
        var state = CribbageGame.CreateGame(Sim.Config(Difficulty.Normal, 121));
        Assert.Equal(ActionResult.Ok, CribbageGame.ApplyAction(state, 0, new CribbageAction.Start(), rng));
        Assert.Equal("Cut for deal. The lower card deals first and takes the first crib.", state.Log.Events[^1].Text);
        Assert.True(Advance(state, rng, s => s.Phase == CribbagePhase.Discard));
        var texts = state.Log.Events.Select(e => e.Text).ToList();
        Assert.Contains(texts, t => t.StartsWith("You cut the ", StringComparison.Ordinal) && t.Contains(". Ruth cuts the "));
        var dealer = state.Dealer!.Value;
        if (dealer == 0)
        {
            Assert.Contains("You have the lower card and deal first.", texts);
            Assert.Contains("Hand 1. You deal, so it is your crib. Both players throw two cards to it.", texts);
        }
        else
        {
            Assert.Contains("Ruth has the lower card and deals first.", texts);
            Assert.Contains("Hand 1. Ruth deals, so it is Ruth’s crib. Both players throw two cards to it.", texts);
        }
        var six = state.Log.Events.Last(e => e.Audience == 0);
        Assert.StartsWith("Your six cards: ", six.Text);
        Assert.Equal(6, six.Cards.Count);

        Assert.True(Advance(state, rng, s => s.Phase == CribbagePhase.Play));
        var after = state.Log.Events.Select(e => e.Text).ToList();
        Assert.Contains(after, t => t.StartsWith("You threw the ", StringComparison.Ordinal) && t.Contains(" crib. You keep "));
        Assert.Contains("You have thrown two cards to the crib.", after);
        Assert.Contains("Ruth has thrown two cards to the crib.", after);
        Assert.Contains(after, t => t.StartsWith("The starter is the ", StringComparison.Ordinal));
        Assert.Contains(dealer == 0 ? "Ruth leads. The count starts at nothing." : "You lead. The count starts at nothing.", after);

        Assert.True(Advance(state, rng, s => s.Phase == CribbagePhase.Play && s.Pile.Count > 0));
        var play = state.Log.Events.Last(e => e.Kind == EventKind.Play);
        var leader = 1 - dealer;
        Assert.StartsWith(leader == 0 ? "You play the " : "Ruth plays the ", play.Text);
        Assert.EndsWith($". The count is {state.Count}.", play.Text);
        Assert.Equal(new[] { state.Pile[0].Card }, play.Cards);
        Assert.Equal(leader, play.Seat);
    }

    [Fact]
    public void HelpAndCards()
    {
        Assert.Equal(6, CribbageHelp.Rules.Count);
        Assert.Equal("How to play Cribbage", CribbageHelp.Rules[0].Heading);
        foreach (var r in CribbageHelp.Rules)
        {
            Assert.DoesNotContain("<", r.Body);
            Assert.False(string.IsNullOrEmpty(r.Body));
        }
        Assert.Equal("King of Clubs", CribbageCards.Describe(C("KC")));
        Assert.Equal("", CribbageCards.Role(C("KC")));
        Assert.Equal(new[] { "AC", "AS", "TD", "JC", "KS" }, CribbageCards.SortHand(Cs("KS", "AC", "TD", "JC", "AS")).Ids());
        Assert.Equal("nothing", CribbageCards.ListNames([]));
        Assert.Equal("Ace of Spades", CribbageCards.ListNames(Cs("AS")));
        Assert.Equal("Ace of Spades and Five of Hearts", CribbageCards.ListNames(Cs("AS", "5H")));
        Assert.Equal("no", CribbageCards.NumberWord(0));
        Assert.Equal("twenty-nine", CribbageCards.NumberWord(29));
        Assert.Equal("forty", CribbageCards.NumberWord(40));
        Assert.Equal("121", CribbageCards.NumberWord(121));
        Assert.Equal(121, new CribbageConfig().TargetScore);
        Assert.Equal(Difficulty.Normal, new CribbageConfig().Difficulty);
        Assert.Equal(new[] { "You", "Ruth" }, new CribbageConfig().Names);
    }
}
