using TheCardPlace.Spades;

namespace TheCardPlace.Play.Tests;

public class SpadesSessionTests
{
    private static void MoveFor(SpadesSession s)
    {
        var action = SpadesAI.Decide(s.State, s.Me, new RandomSource(99)) ?? throw new Xunit.Sdk.XunitException("nothing to decide");
        switch (action)
        {
            case SpadesAction.Bid { Tricks: 0 }:
                Table.Control(s, "nil").Action();
                break;
            case SpadesAction.Bid b:
                var amount = Table.Control(s, "amount");
                var label = Prose.Count(b.Tricks, "trick");
                amount.OnSelect!(amount.Options.ToList().IndexOf(label));
                Assert.Equal($"Bid {b.Tricks}", s.Primary.Title);
                s.Primary.Action();
                break;
            case SpadesAction.Play p:
                s.Activate(Table.CardIn(s, p.Card));
                break;
            default:
                throw new Xunit.Sdk.XunitException("unexpected " + action);
        }
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    public void A_whole_game_through_the_controls(ulong seed)
    {
        var settings = Table.Settings(GameKind.Spades);
        settings.For(GameKind.Spades).Rules[SpadesRules.PointsToWin.Key] = "200";
        var (s, _, speaker, _) = Table.Make<SpadesSession>(GameKind.Spades, seed, settings: settings);
        s.Start();
        for (var guard = 0; guard < 3000 && s.State.Phase != SpadesPhase.GameOver; guard++)
        {
            if (s.State.Phase == SpadesPhase.HandOver) s.Advance();
            else
            {
                Assert.True(s.IsMyTurn, "the table stopped without it being the player's move: " + s.Status);
                MoveFor(s);
            }
        }
        Assert.Equal(SpadesPhase.GameOver, s.State.Phase);
        Assert.Equal(2, s.Scores.Rows.Count);
        Assert.NotEmpty(s.History.Rows);
        Assert.Contains(speaker.Spoken, x => x.Id == "TheCardPlace.Turn");
    }

    [Fact]
    public void Bidding_offers_an_amount_and_nil_and_the_hand_is_readable()
    {
        var (s, _, _, heard) = Table.Make<SpadesSession>(GameKind.Spades, 4);
        s.Start();
        Assert.Equal(SpadesPhase.Bidding, s.State.Phase);
        Assert.True(s.IsMyTurn);
        Assert.All(s.Hand, h => Assert.True(h.Playable));
        Assert.Equal(["amount", "nil"], s.Secondary.Select(c => c.Id));
        s.Activate(s.Hand[0]);
        Assert.StartsWith("Bid first.", heard[^1].Text, StringComparison.Ordinal);
        Table.Control(s, "nil").Action();
        Assert.Equal(0, s.State.Players[0].Bid);
        Assert.Empty(s.Secondary);
    }

    [Fact]
    public void Score_rows_are_read_with_their_column_names()
    {
        var (s, _, _, _) = Table.Make<SpadesSession>(GameKind.Spades, 4);
        s.Start();
        var row = s.Scores.RowName(0);
        Assert.Matches("^[^,]+, score 0, bags 0, contract (not yet|[0-9]+), tricks 0$", row);
    }
}
