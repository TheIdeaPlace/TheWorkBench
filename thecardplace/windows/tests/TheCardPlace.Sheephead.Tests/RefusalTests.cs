using static TheCardPlace.Sheephead.Tests.Support;

namespace TheCardPlace.Sheephead.Tests;

public class RefusalTests
{
    private static void Refuse(SheepheadState state, int seat, SheepheadAction action, string reason)
    {
        var before = J(state);
        var r = SheepheadGame.ApplyAction(state, seat, action, new RandomSource(1));
        Assert.Equal(ActionResult.Refused(reason), r);
        Assert.Equal(before, J(state));   // the state is unchanged on refusal
    }

    [Fact]
    public void Idle_and_start()
    {
        var rng = new RandomSource(1);
        var state = SheepheadGame.CreateGame(new SheepheadConfig());
        Assert.Null(SheepheadGame.SeatToAct(state));
        Assert.False(SheepheadGame.CanDeal(state));
        Refuse(state, 0, new SheepheadAction.NextHand(), "The hand is not over.");
        Refuse(state, 0, new SheepheadAction.Pick(), "It is not the picking round.");
        Refuse(state, 0, new SheepheadAction.Play(Cd("QC")), "It is not time to play a card.");
        Refuse(state, 9, new SheepheadAction.Start(), "That is not a seat at this table.");
        Refuse(state, -1, new SheepheadAction.Start(), "That is not a seat at this table.");
        Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.Start(), rng));
        Assert.Equal(SheepheadPhase.Pick, state.Phase);
        Refuse(state, 0, new SheepheadAction.Start(), "The game has already started.");
    }

    [Fact]
    public void Picking_round()
    {
        var (state, _) = FiveHanded();
        Refuse(state, 1, new SheepheadAction.Pick(), "It is not your turn.");
        Refuse(state, 1, new SheepheadAction.Pass(), "It is not your turn.");
        Refuse(state, 0, new SheepheadAction.Bury([]), "There is nothing to bury.");
        Refuse(state, 0, new SheepheadAction.Play(state.Players[0].Hand[0]), "It is not time to play a card.");
        Refuse(state, 0, new SheepheadAction.NextHand(), "The hand is not over.");
        Refuse(state, 0, new SheepheadAction.NewGame(), "The hand is not over.");
        Assert.Empty(SheepheadGame.LegalPlays(state, 0));
        Assert.Equal("Cards are for review while you decide whether to pick.",
            SheepheadGame.WhyNot(state, 0, state.Players[0].Hand[0]));
        Assert.Equal("Cards are for review; You is deciding whether to pick.",
            SheepheadGame.WhyNot(state, 1, state.Players[1].Hand[0]));
    }

    [Fact]
    public void Burying()
    {
        var (state, rng) = FiveHanded();
        Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.Pick(), rng));
        Assert.Equal(SheepheadPhase.Bury, state.Phase);
        Assert.Equal(0, SheepheadGame.SeatToAct(state));
        Assert.Equal(8, state.Players[0].Hand.Count);
        var hand = state.Players[0].Hand.ToList();
        var notHeld = SheepheadCards.Deck(5).First(c => !hand.Contains(c));
        Refuse(state, 1, new SheepheadAction.Bury([hand[0], hand[1]]), "Only the picker buries.");
        Refuse(state, 0, new SheepheadAction.Bury([hand[0]]), "Bury exactly 2 cards.");
        Refuse(state, 0, new SheepheadAction.Bury([hand[0], hand[1], hand[2]]), "Bury exactly 2 cards.");
        Refuse(state, 0, new SheepheadAction.Bury([hand[0], hand[0]]), $"The {hand[0].Name} was named twice.");
        Refuse(state, 0, new SheepheadAction.Bury([hand[0], notHeld]), $"You do not hold the {notHeld.Name}.");
        Refuse(state, 0, new SheepheadAction.Pick(), "It is not the picking round.");
        Refuse(state, 0, new SheepheadAction.Play(hand[0]), "It is not time to play a card.");
        Assert.Equal("Cards are for review while you choose what to bury.", SheepheadGame.WhyNot(state, 0, hand[0]));
        Assert.Equal("Cards are for review; You is burying.", SheepheadGame.WhyNot(state, 1, state.Players[1].Hand[0]));
        Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.Bury([hand[6], hand[7]]), rng));
        Assert.Equal([hand[6], hand[7]], state.Buried);
        Assert.Equal(6, state.Players[0].Hand.Count);
        Assert.Equal(SheepheadPhase.Play, state.Phase);
        Assert.Equal(0, state.Turn);
    }

    [Fact]
    public void A_bury_list_changed_afterwards_does_not_change_the_bury()
    {
        // C# only: the action's list is the caller's, so the engine keeps its own copy.
        var (state, rng) = FiveHanded();
        Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.Pick(), rng));
        var hand = state.Players[0].Hand.ToList();
        var chosen = new List<Card> { hand[6], hand[7] };
        Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.Bury(chosen), rng));
        chosen.Clear();
        Assert.Equal([hand[6], hand[7]], state.Buried);
    }

    [Fact]
    public void Missing_or_strange_actions_are_refused_without_throwing()
    {
        // C# only: an action off the wire may carry a null list, or be null itself.
        var (state, rng) = FiveHanded();
        Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.Pick(), rng));
        Refuse(state, 0, new SheepheadAction.Bury(null!), "Bury exactly 2 cards.");
        Refuse(state, 0, null!, "That is not something you can do in this game.");
    }

    [Fact]
    public void Playing()
    {
        var (state, _) = FiveHanded();
        state.Phase = SheepheadPhase.Play;
        state.Picker = 1;
        state.Partner = 2;
        state.Players[0].Hand = Cards("QC", "AC", "KC", "9H");
        state.Players[1].Hand = Cards("TC", "AS", "7H", "JD");
        state.Trick = [new SheepheadPlay(4, Cd("TS")), new SheepheadPlay(0, Cd("KC"))];
        state.Turn = 1;
        Refuse(state, 0, new SheepheadAction.Play(Cd("AC")), "It is not your turn.");
        Refuse(state, 1, new SheepheadAction.Play(Cd("QS")), "You do not hold the Queen of Spades.");
        Refuse(state, 1, new SheepheadAction.Play(Cd("TC")), "You must follow spades.");
        Assert.Equal("You must follow spades.", SheepheadGame.WhyNot(state, 1, Cd("TC")));
        Assert.Null(SheepheadGame.WhyNot(state, 1, Cd("AS")));
        Assert.Equal("It is not your turn; Alice is to play.", SheepheadGame.WhyNot(state, 0, Cd("AC")));
        Assert.Equal("You do not hold the Queen of Spades.", SheepheadGame.WhyNot(state, 0, Cd("QS")));
        Assert.Equal(Cards("AS"), SheepheadGame.LegalPlays(state, 1));
        Assert.Empty(SheepheadGame.LegalPlays(state, 0));

        // A trump lead: queens and jacks must follow, and the reason says trump.
        state.Trick = [new SheepheadPlay(4, Cd("9D"))];
        Refuse(state, 1, new SheepheadAction.Play(Cd("AS")), "You must follow trump.");
        Assert.Equal(Cards("JD"), SheepheadGame.LegalPlays(state, 1));
        state.Turn = 0;
        Assert.Equal("You must follow trump.", SheepheadGame.WhyNot(state, 0, Cd("AC")));
        Assert.Null(SheepheadGame.WhyNot(state, 0, Cd("QC")));
    }

    [Fact]
    public void Hand_over_and_new_game()
    {
        var (state, rng) = NewGame(4, 2);
        PlayHand(state, rng);
        Assert.Null(SheepheadGame.SeatToAct(state));
        Refuse(state, 0, new SheepheadAction.Pick(), "It is not the picking round.");
        Refuse(state, 0, new SheepheadAction.Play(Cd("QC")), "It is not time to play a card.");
        Refuse(state, 0, new SheepheadAction.Start(), "The game has already started.");
        Assert.Equal("You do not hold the Queen of Clubs.", SheepheadGame.WhyNot(state, 0, Cd("QC")));
        var names = state.Players.Select(p => p.Name).ToList();
        var config = J(state.Config);
        Assert.Equal(ActionResult.Ok, SheepheadGame.ApplyAction(state, 0, new SheepheadAction.NewGame(), rng));
        Assert.Equal(SheepheadPhase.Idle, state.Phase);
        Assert.Equal(0, state.HandNumber);
        Assert.Equal(names, state.Players.Select(p => p.Name));
        Assert.Equal(config, J(state.Config));
        Assert.All(state.Players, p => Assert.True(p.Score == 0 && p.Hand.Count == 0));
        Assert.Empty(state.Log.Events);
        // C# only: newGame refills the same object, so check nothing was left behind.
        Assert.Equal(J(SheepheadGame.CreateGame(state.Config)), J(state));
    }

    [Fact]
    public void Config_is_clamped()
    {
        Assert.Equal(3, SheepheadGame.Seats(new SheepheadConfig { Players = 2 }));
        Assert.Equal(6, SheepheadGame.Seats(new SheepheadConfig { Players = 9 }));
        var s = SheepheadGame.CreateGame(new SheepheadConfig { Names = ["Kelly"], Players = 6 });
        Assert.Equal(6, s.Players.Count);
        Assert.Equal("Kelly", s.Players[0].Name);
        Assert.Equal(Occupant.Human, s.Players[0].Occupant);
        Assert.All(s.Players.Skip(1), p => Assert.Equal(Occupant.Bot, p.Occupant));
        Assert.Equal(6, s.Players.Select(p => p.Name).Distinct().Count());   // names are distinct
        var d = new SheepheadConfig();
        Assert.Equal(5, d.Players);
        Assert.Equal(Difficulty.Normal, d.Difficulty);
        Assert.Equal(SheepheadAllPass.Leaster, d.AllPass);
        Assert.False(d.BlackQueenDoubler || d.RedQueenDoubler || d.RedealDoubler);
    }

    [Fact]
    public void Create_game_does_not_change_the_config_it_was_given()
    {
        // C# only: Swift's config is a value; here CreateGame must work on a copy.
        var config = new SheepheadConfig { Names = ["Kelly"], Players = 9 };
        var s = SheepheadGame.CreateGame(config);
        Assert.Equal(["Kelly"], config.Names);
        Assert.Equal(9, config.Players);
        Assert.Equal(6, s.Config.Players);
        Assert.Equal(6, s.Config.Names.Count);
    }
}
