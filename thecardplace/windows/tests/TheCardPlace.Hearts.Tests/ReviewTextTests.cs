using static TheCardPlace.Hearts.Tests.Support;

namespace TheCardPlace.Hearts.Tests;

/// Exact-string checks on the review sentences from constructed positions.
public class ReviewTextTests
{
    [Fact]
    public void Hand_is_grouped_by_suit_high_to_low_with_counts()
    {
        var s = PlayState([["2H", "AS", "TD", "QS", "3C", "KH", "AC", "9D", "2S", "JC"], [], [], []], turn: 0);
        Assert.Equal("Three clubs: Ace, Jack and Three. Two diamonds: Ten and Nine. Three spades: Ace, Queen and Two. Two hearts: King and Two.",
                     HeartsReview.Hand(s, 0));
        var one = PlayState([["7H"], ["2C"], [], []], turn: 0);
        Assert.Equal("One heart: Seven.", HeartsReview.Hand(one, 0));
        Assert.Equal("One club: Two.", HeartsReview.Hand(one, 1));
        Assert.Equal("Your hand is empty.", HeartsReview.Hand(one, 2));
    }

    [Fact]
    public void Trick()
    {
        var empty = PlayState([["2C"], ["3C"], ["4C"], ["5C"]], turn: 0);
        Assert.Equal("Nothing has been played to this trick yet.", HeartsReview.Trick(empty, 0));
        var s = PlayState([["2C"], ["3C"], ["4C"], ["5C"]], turn: 2, trick: [(0, "TH"), (1, "AH")], tricksPlayed: 4, heartsBroken: true);
        Assert.Equal("North played the Ten of Hearts, East played the Ace of Hearts. Hearts were led.", HeartsReview.Trick(s, 0));
    }

    [Fact]
    public void Last_trick()
    {
        var s = PlayState([["2C"], ["3C"], ["4C"], ["5C"]], turn: 0);
        Assert.Equal("No trick has been completed yet.", HeartsReview.LastTrick(s, 0));
        s.LastTrick = new HeartsLastTrick(
        [
            new HeartsTrickPlay(3, C("9S")),
            new HeartsTrickPlay(0, C("QS")),
            new HeartsTrickPlay(1, C("KS")),
            new HeartsTrickPlay(2, C("2S")),
        ], winner: 1, points: 13);
        Assert.Equal("West played the Nine of Spades, North played the Queen of Spades, East played the King of Spades, South played the Two of Spades. East took it with 13 points.",
                     HeartsReview.LastTrick(s, 0));
        s.LastTrick = new HeartsLastTrick(
        [
            new HeartsTrickPlay(0, C("2C")),
            new HeartsTrickPlay(1, C("3C")),
            new HeartsTrickPlay(2, C("4C")),
            new HeartsTrickPlay(3, C("2H")),
        ], winner: 2, points: 1);
        Assert.EndsWith("South took it with 1 point.", HeartsReview.LastTrick(s, 0));
        s.LastTrick = new HeartsLastTrick([.. s.LastTrick.Cards.SkipLast(1), new HeartsTrickPlay(3, C("5C"))], winner: 3, points: 0);
        Assert.EndsWith("West took it, no points.", HeartsReview.LastTrick(s, 0));
    }

    [Fact]
    public void Scores_and_points_so_far()
    {
        var s = PlayState([["2C"], ["3C"], ["4C"], ["5C"]], turn: 0,
                          taken: [["AH", "KH", "3D"], [], ["QS", "2C", "3C", "4C"], ["2H"]]);
        s.Players[0].Score = 12;
        s.Players[2].Score = 30;
        s.Players[3].Score = 4;
        Assert.Equal("North 12, East 0, South 30, West 4. Lowest wins.", HeartsReview.Scores(s, 0));
        Assert.Equal("This hand: North 2 points, East 0 points, South 13 points, West 1 point. South has the queen of spades.",
                     HeartsReview.PointsSoFar(s, 0));
        s.Players[2].Taken = [];
        Assert.Equal("This hand: North 2 points, East 0 points, South 0 points, West 1 point. The queen of spades has not been played.",
                     HeartsReview.PointsSoFar(s, 0));
    }

    [Fact]
    public void Cards_played()
    {
        var fresh = PlayState([["2C"], ["3C"], ["4C"], ["5C"]], turn: 0);
        Assert.Equal("0 tricks played, 13 to go. Hearts have not been broken. The queen of spades is still out. None of the high hearts have gone.",
                     HeartsReview.CardsPlayed(fresh, 0));
        var mid = PlayState([["2C"], ["3C"], ["4C"], ["5C"]], turn: 1, trick: [(0, "AH")], tricksPlayed: 1, heartsBroken: true,
                            taken: [[], ["2C", "3C", "KH", "QS"], [], []]);
        Assert.Equal("1 trick played, 12 to go. Hearts are broken. The queen of spades has gone. Played so far: two clubs, one spade and two hearts. Of the high hearts, the Ace and King have gone; the Queen and Jack are still out.",
                     HeartsReview.CardsPlayed(mid, 0));
        var late = PlayState([["2C"], ["3C"], ["4C"], ["5C"]], turn: 0, tricksPlayed: 2, heartsBroken: true,
                             taken: [["AH", "KH", "QH", "JH"], ["2D", "3D", "4D", "5D"], [], []]);
        Assert.Equal("2 tricks played, 11 to go. Hearts are broken. The queen of spades is still out. Played so far: four diamonds and four hearts. All four high hearts have gone.",
                     HeartsReview.CardsPlayed(late, 0));
        var single = PlayState([["2C"], ["3C"], ["4C"], ["5C"]], turn: 0, tricksPlayed: 1, heartsBroken: true,
                               taken: [["JH", "2C", "3C", "4C"], [], [], []]);
        Assert.EndsWith("Of the high hearts, the Jack has gone; the Ace, King and Queen are still out.", HeartsReview.CardsPlayed(single, 0));
    }

    [Fact]
    public void Play_order()
    {
        var lead = PlayState([["2C"], ["3C"], ["4C"], ["5C"]], turn: 2, leader: 2);
        Assert.Equal("Play goes South, then West, then North, then East.", HeartsReview.PlayOrder(lead, 0));
        var mid = PlayState([["2C"], ["3C"], ["4C"], ["5C"]], turn: 0, trick: [(3, "9D")], tricksPlayed: 2);
        Assert.Equal("Play goes West, then North, then East, then South.", HeartsReview.PlayOrder(mid, 0));
    }

    [Fact]
    public void Who()
    {
        var s = HeartsGame.CreateGame(Config());
        Assert.Equal("North, you. East, computer. South, computer. West, computer.", HeartsReview.Who(s, 0));
    }

    [Fact]
    public void Status_in_every_phase()
    {
        var rng = new RandomSource(5);
        var s = HeartsGame.CreateGame(Config());
        Assert.Equal("Choose Start to deal the first hand.", HeartsReview.Status(s, 0));

        Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Start(), rng).IsOk);
        Assert.Equal("Choose three cards to pass to the left.", HeartsReview.Status(s, 0));
        s.PassDirection = HeartsPassDirection.Right;
        Assert.Equal("Choose three cards to pass to the right.", HeartsReview.Status(s, 0));
        s.PassDirection = HeartsPassDirection.Across;
        Assert.Equal("Choose three cards to pass across.", HeartsReview.Status(s, 0));
        s.PassDirection = HeartsPassDirection.Left;
        Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Pass(s.Players[0].Hand.Take(3).ToList()), rng).IsOk);
        Assert.Equal("You have passed. Waiting for 3 players.", HeartsReview.Status(s, 0));
        Assert.Equal("Choose three cards to pass to the left.", HeartsReview.Status(s, 1));
        for (var seat = 1; seat <= 2; seat++)
            Assert.True(HeartsGame.ApplyAction(s, seat, HeartsAI.Decide(s, seat, rng)!, rng).IsOk);
        Assert.Equal("You have passed. Waiting for 1 player.", HeartsReview.Status(s, 0));

        var play = PlayState([["2C", "3D"], ["9C", "AH"], ["4C", "2D"], ["5C", "4D"]], turn: 0);
        Assert.Equal("Your turn. You lead.", HeartsReview.Status(play, 0));
        Assert.Equal("Waiting for North.", HeartsReview.Status(play, 1));
        var following = PlayState([["2C", "3D"], ["9C", "AH"], ["4C", "2D"], ["5C", "4D"]], turn: 0,
                                  trick: [(3, "TH")], tricksPlayed: 3, heartsBroken: true);
        Assert.Equal("Your turn. West led the Ten of Hearts.", HeartsReview.Status(following, 0));
        Assert.Equal("Waiting for North.", HeartsReview.Status(following, 2));

        var over = HeartsGame.CreateGame(Config());
        over.Phase = HeartsPhase.HandOver;
        over.DealNumber = 3;
        over.Players[1].Score = 7;
        over.Players[3].Score = 26;
        Assert.Equal("Hand 3 is over. North 0, East 7, South 0, West 26. Lowest wins.", HeartsReview.Status(over, 0));

        over.Phase = HeartsPhase.GameOver;
        over.Players[0].Score = 45;
        over.Players[2].Score = 100;
        over.Winner = 1;
        Assert.Equal("East wins with 7.", HeartsReview.Status(over, 0));
        over.Winner = null;
        over.Players[0].Score = 7;
        Assert.Equal("The game is over. Tied on 7: North and East.", HeartsReview.Status(over, 0));
    }

    [Fact]
    public void Rules_help_is_plain_text()
    {
        Assert.Equal(["The object", "Passing", "The play", "Shooting the moon"], HeartsHelp.Rules.Select(r => r.Heading));
        foreach (var r in HeartsHelp.Rules)
        {
            Assert.DoesNotContain("<", r.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("press", r.Body.ToLowerInvariant(), StringComparison.Ordinal);
            Assert.EndsWith(".", r.Body, StringComparison.Ordinal);
        }
    }
}
