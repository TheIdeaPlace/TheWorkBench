using System.Globalization;
using static TheCardPlace.Spades.Tests.T;

namespace TheCardPlace.Spades.Tests;

/// <summary>Exact-string checks on the review sentences from constructed positions.</summary>
public class ReviewTests
{
    private static readonly List<string> Names = ["You", "East", "South", "West"];

    private static SpadesConfig Cfg() => Config(names: Names);

    private static void PlayIn(SpadesState state, int seat, string id) =>
        SpadesGame.ApplyAction(state, seat, new SpadesAction.Play(C(id)), new RandomSource(1));

    [Fact]
    public void Hand()
    {
        var state = SpadesGame.CreateGame(Cfg());
        state.Players[0].Hand = SpadesCards.SortHand(Cs("QS 4C AC 9H 2H TC KD 9S"));
        Assert.Equal("Clubs: Ace, Ten and Four. Diamonds: King. Hearts: Nine and Two. Spades (trump): Queen and Nine. 2 spades.",
            SpadesReview.Hand(state, 0));
        state.Players[0].Hand = Cs("AH");
        Assert.Equal("Hearts: Ace. No spades.", SpadesReview.Hand(state, 0));
        state.Players[0].Hand = Cs("3S");
        Assert.Equal("Spades (trump): Three. 1 spade.", SpadesReview.Hand(state, 0));
        state.Players[0].Hand = [];
        Assert.Equal("Your hand is empty.", SpadesReview.Hand(state, 0));
    }

    [Fact]
    public void Trick_and_last_trick()
    {
        var state = Position([3, 3, 3, 3], [0, 0, 0, 0], ["2H 3C", "KH 4C", "2S 5C", "AH 6C"], 0, 0, config: Cfg());
        Assert.Equal("Nothing has been played to this trick yet.", SpadesReview.Trick(state));
        Assert.Equal("No trick has been completed yet.", SpadesReview.LastTrick(state));
        PlayIn(state, 0, "2H");
        PlayIn(state, 1, "KH");
        Assert.Equal("You played the Two of Hearts, East played the King of Hearts. Hearts were led. East is winning it.",
            SpadesReview.Trick(state));
        PlayIn(state, 2, "2S");
        Assert.Equal("You played the Two of Hearts, East played the King of Hearts, South played the Two of Spades. Hearts were led. South is winning it.",
            SpadesReview.Trick(state));
        Assert.Equal("Play goes You, then East, then South, then West.", SpadesReview.PlayOrder(state));
        PlayIn(state, 3, "AH");
        Assert.Equal("You played the Two of Hearts, East played the King of Hearts, South played the Two of Spades, West played the Ace of Hearts. South took it.",
            SpadesReview.LastTrick(state));
        Assert.Equal("Nothing has been played to this trick yet.", SpadesReview.Trick(state));
        Assert.Equal("Play goes South, then West, then You, then East.", SpadesReview.PlayOrder(state));
    }

    [Fact]
    public void Scores()
    {
        var state = SpadesGame.CreateGame(Cfg());
        state.Scores = [120, 80];
        state.Bags = [2, 1];
        Assert.Equal("You and South 120 with 2 bags, East and West 80 with 1 bag. Playing to 500.", SpadesReview.Scores(state));
        state.Config.PointsToWin = 250;
        state.Bags = [0, 0];
        Assert.Equal("You and South 120 with 0 bags, East and West 80 with 0 bags. Playing to 250.", SpadesReview.Scores(state));
    }

    [Fact]
    public void Contract_during_the_bidding()
    {
        var state = SpadesGame.CreateGame(Cfg());
        state.Phase = SpadesPhase.Bidding;
        state.Dealer = 0;
        state.Turn = 1;
        Assert.Equal("Nobody has bid yet.", SpadesReview.Contract(state, 0));
        state.Players[1].Bid = 3;
        state.Players[2].Bid = 0;
        state.Turn = 3;
        Assert.Equal("East 3, South nil.", SpadesReview.Contract(state, 0));
    }

    [Fact]
    public void Contract_at_idle()
    {
        Assert.Equal("Nobody has bid yet.", SpadesReview.Contract(SpadesGame.CreateGame(Cfg()), 0));
    }

    [Fact]
    public void Contract_during_play()
    {
        var state = Position([3, 4, 2, 3], [2, 4, 1, 4], ["2C", "3C", "4C", "5C"], 0, 11, config: Cfg());
        Assert.Equal("You and South bid 5, took 3 — 2 more tricks needed. East and West bid 7, took 8 — 1 over, 1 bag so far.",
            SpadesReview.Contract(state, 0));
        // The other side hears its own contract first.
        Assert.Equal("East and West bid 7, took 8 — 1 over, 1 bag so far. You and South bid 5, took 3 — 2 more tricks needed.",
            SpadesReview.Contract(state, 1));
        state.Players[0].Tricks = 4;
        Assert.Equal("You and South bid 5, took 5 — made it exactly. East and West bid 7, took 8 — 1 over, 1 bag so far.",
            SpadesReview.Contract(state, 0));
        state.Players[2].Tricks = 2;
        state.Players[0].Tricks = 5;
        Assert.Equal("You and South bid 5, took 7 — 2 over, 2 bags so far. East and West bid 7, took 8 — 1 over, 1 bag so far.",
            SpadesReview.Contract(state, 0));
    }

    [Fact]
    public void Contract_names_a_nil_and_whether_it_is_intact()
    {
        var state = Position([3, 4, 0, 3], [2, 4, 0, 4], ["2C", "3C", "4C", "5C"], 0, 10, config: Cfg());
        Assert.Equal("You and South bid 3, took 2 — 1 more trick needed. East and West bid 7, took 8 — 1 over, 1 bag so far. South bid nil and has not taken a trick.",
            SpadesReview.Contract(state, 0));
        state.Players[2].Tricks = 2;
        Assert.Equal("You and South bid 3, took 4 — 1 over, 1 bag so far. East and West bid 7, took 8 — 1 over, 1 bag so far. South bid nil and has taken 2 tricks.",
            SpadesReview.Contract(state, 0));
    }

    [Fact]
    public void Cards_played()
    {
        var state = Position([3, 3, 3, 3], [0, 0, 0, 0], ["AS 2C 3D 4H", "3C 4D KH 2S", "5C 6C QH 3S", "7D 8D JH 4S"], 0, 0, config: Cfg());
        Assert.Equal("0 tricks played, 13 to go. Spades have not been broken. No spades have gone. Highest still out: Ace of Clubs, Ace of Diamonds, Ace of Hearts and King of Spades.",
            SpadesReview.CardsPlayed(state, 0));
        string[] ids = ["2C", "3C", "5C", "4S"];
        for (var seat = 0; seat < 4; seat++)
            Assert.True(SpadesGame.ApplyAction(state, seat, new SpadesAction.Play(C(ids[seat])), new RandomSource(1)).IsOk);
        Assert.Equal(1, state.TricksPlayed);
        Assert.Equal("1 trick played, 12 to go. Spades are broken. 1 spade gone. Highest still out: Ace of Clubs, Ace of Diamonds, Ace of Hearts and King of Spades.",
            SpadesReview.CardsPlayed(state, 0));
        // East, who does not hold the ace of spades, is told it is still out.
        Assert.Equal("1 trick played, 12 to go. Spades are broken. 1 spade gone. Highest still out: Ace of Clubs, Ace of Diamonds, Ace of Hearts and Ace of Spades.",
            SpadesReview.CardsPlayed(state, 1));
    }

    [Fact]
    public void Who_is_here()
    {
        var state = SpadesGame.CreateGame(Cfg());
        Assert.Equal("You (a person) and South (computer), your side. Against: East (computer) and West (computer).",
            SpadesReview.WhoIsHere(state, 0));
    }

    [Fact]
    public void Status_in_every_phase()
    {
        var state = SpadesGame.CreateGame(Cfg());
        Assert.Equal("Ready to start.", SpadesReview.Status(state, 0));

        state.Phase = SpadesPhase.Bidding;
        state.Dealer = 3;
        state.DealNumber = 1;
        state.Turn = 0;
        Assert.Equal("Your bid. How many tricks will you take? Your partner has not bid yet.", SpadesReview.Status(state, 0));
        state.Turn = 2;
        Assert.Equal("Waiting for South to bid.", SpadesReview.Status(state, 0));
        state.Players[2].Bid = 0;
        state.Turn = 0;
        state.Dealer = 1;
        Assert.Equal("Your bid. How many tricks will you take? South bid nil.", SpadesReview.Status(state, 0));
        state.Players[2].Bid = 4;
        Assert.Equal("Your bid. How many tricks will you take? South bid 4.", SpadesReview.Status(state, 0));

        state = Position([3, 4, 2, 3], [2, 4, 1, 4], ["2C", "3C", "4C", "5C"], 0, 11, config: Cfg());
        Assert.Equal("Your turn. You lead. You need 2 more.", SpadesReview.Status(state, 0));
        PlayIn(state, 0, "2C");
        Assert.Equal("Waiting for East. You need 2 more.", SpadesReview.Status(state, 0));
        Assert.Equal("Your turn. Clubs were led. 1 bag over.", SpadesReview.Status(state, 1));
        state.Players[0].Tricks = 4;
        Assert.Equal("Waiting for East. Your contract is made.", SpadesReview.Status(state, 0));
        state.Players[0].Tricks = 6;
        Assert.Equal("Waiting for East. 2 bags over.", SpadesReview.Status(state, 0));

        state.Phase = SpadesPhase.HandOver;
        state.DealNumber = 3;
        state.Scores = [50, 80];
        Assert.Equal("Hand 3 is over. You and South 50 with 0 bags, East and West 80 with 0 bags. Playing to 500.",
            SpadesReview.Status(state, 0));

        state.Phase = SpadesPhase.GameOver;
        state.Winner = 1;
        state.Scores = [380, 510];
        Assert.Equal("East and West win, 510 to 380.", SpadesReview.Status(state, 0));
        state.Winner = null;
        Assert.Equal("The game is over.", SpadesReview.Status(state, 0));
    }

    /// Not in Swift: C# formats numbers by the PC's culture unless told not to, and some cultures
    /// write the minus sign as U+2212. A score a screen reader reads must not depend on that.
    [Fact]
    public void Negative_scores_read_the_same_in_every_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            var state = Position([5, 2, 0, 2], [3, 4, 0, 5], ["2H", "3H", "AH", "4H"], 0, 12,
                                 scores: [-100, 480], bags: [0, 8], config: Config());
            PlayIn(state, 0, "2H");
            PlayIn(state, 1, "3H");
            PlayIn(state, 2, "AH");
            PlayIn(state, 3, "4H");
            var texts = state.Log.Events.Select(e => e.Text).ToList();
            Assert.Contains("North and South bid 5, took 4 — set, -150.", texts);
            Assert.Contains("Hand 1 over. North and South -250, East and West 425.", texts);
            Assert.Equal("North and South -250 with 0 bags, East and West 425 with 3 bags. Playing to 500.", SpadesReview.Scores(state));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
