using TheCardPlace.Core;
using TheCardPlace.Play.Games;

namespace TheCardPlace.Play.Tests;

/// <summary>
/// Plays a game to the end through its session, the way a player at the table would: the seat's
/// move comes from the game's own computer player and is made with the hand and the controls.
/// Sheephead has no end, so it stops after <c>sheepheadHands</c> hands.
/// </summary>
public static class WholeGame
{
    /// <param name="step">Called before each move, with the session as it is then.</param>
    /// <returns>True if the game reached its end (or Sheephead its last hand).</returns>
    public static bool Play(GameSession session, ulong seed, Action? step = null, int sheepheadHands = 3)
    {
        var ai = new RandomSource(seed * 7919 + 1);
        var hands = 1;
        for (var guard = 0; guard < 4000; guard++)
        {
            step?.Invoke();
            switch (session)
            {
                case HeartsSession h:
                {
                    if (h.State.Phase == TheCardPlace.Hearts.HeartsPhase.GameOver) return true;
                    if (h.State.Phase == TheCardPlace.Hearts.HeartsPhase.HandOver) { session.Advance(); break; }
                    var a = TheCardPlace.Hearts.HeartsAI.Decide(h.State, 0, ai)!;
                    if (a is TheCardPlace.Hearts.HeartsAction.Pass pass)
                    {
                        foreach (var c in pass.Cards) Card(c);
                        session.Primary.Action();
                    }
                    else if (a is TheCardPlace.Hearts.HeartsAction.Play pl) Card(pl.Card);
                    break;
                }
                case SpadesSession s:
                {
                    if (s.State.Phase == TheCardPlace.Spades.SpadesPhase.GameOver) return true;
                    if (s.State.Phase == TheCardPlace.Spades.SpadesPhase.HandOver) { session.Advance(); break; }
                    var a = TheCardPlace.Spades.SpadesAI.Decide(s.State, 0, ai)!;
                    if (a is TheCardPlace.Spades.SpadesAction.Bid { Tricks: 0 }) Choice(c => c.Id == "nil");
                    else if (a is TheCardPlace.Spades.SpadesAction.Bid b)
                    {
                        var amount = s.Secondary.First(c => c.Id == "amount");
                        amount.OnSelect!(amount.Options.ToList().IndexOf(Prose.Count(b.Tricks, "trick")));
                        session.Primary.Action();
                    }
                    else if (a is TheCardPlace.Spades.SpadesAction.Play pl) Card(pl.Card);
                    break;
                }
                case EuchreSession e:
                {
                    if (e.State.Phase == TheCardPlace.Euchre.EuchrePhase.GameOver) return true;
                    if (e.State.Phase == TheCardPlace.Euchre.EuchrePhase.HandOver) { session.Advance(); break; }
                    switch (TheCardPlace.Euchre.EuchreAI.Decide(e.State, 0, ai)!)
                    {
                        case TheCardPlace.Euchre.EuchreAction.OrderUp o: Choice(c => c.Id == (o.Alone ? "order-alone" : "order")); break;
                        case TheCardPlace.Euchre.EuchreAction.Pass: Choice(c => c.Id == "pass"); break;
                        case TheCardPlace.Euchre.EuchreAction.CallSuit cs:
                            if (cs.Alone) { var t = session.Secondary.First(c => c.Id == "alone"); t.OnToggle!(true); }
                            Choice(c => c.Id == "call-" + cs.Suit.LowerName());
                            break;
                        case TheCardPlace.Euchre.EuchreAction.Discard d: Card(d.Card); break;
                        case TheCardPlace.Euchre.EuchreAction.Play pl: Card(pl.Card); break;
                        case var other: throw new InvalidOperationException("euchre " + other);
                    }
                    break;
                }
                case CribbageSession cr:
                {
                    if (cr.State.Phase == TheCardPlace.Cribbage.CribbagePhase.GameOver) return true;
                    var a = TheCardPlace.Cribbage.CribbageAI.Decide(cr.State, 0, ai);
                    switch (a)
                    {
                        case null or TheCardPlace.Cribbage.CribbageAction.NextHand:
                            if (session.Primary.Key != 'n') throw new InvalidOperationException("cribbage stuck: " + cr.Status);
                            session.Advance();
                            break;
                        case TheCardPlace.Cribbage.CribbageAction.Cut or TheCardPlace.Cribbage.CribbageAction.Go or TheCardPlace.Cribbage.CribbageAction.Next:
                            session.Advance();
                            break;
                        case TheCardPlace.Cribbage.CribbageAction.Discard d:
                            foreach (var c in d.Cards) Card(c);
                            session.Primary.Action();
                            break;
                        case TheCardPlace.Cribbage.CribbageAction.Play pl: Card(pl.Card); break;
                        default: throw new InvalidOperationException("cribbage " + a);
                    }
                    break;
                }
                case SheepheadSession sh:
                {
                    if (sh.State.Phase == TheCardPlace.Sheephead.SheepheadPhase.HandOver)
                    {
                        if (hands >= sheepheadHands) return true;
                        hands++;
                        session.Advance();
                        break;
                    }
                    switch (TheCardPlace.Sheephead.SheepheadAI.Decide(sh.State, 0, ai)!)
                    {
                        case TheCardPlace.Sheephead.SheepheadAction.Pick: Choice(c => c.Id == "pick"); break;
                        case TheCardPlace.Sheephead.SheepheadAction.Pass: Choice(c => c.Id == "pass"); break;
                        case TheCardPlace.Sheephead.SheepheadAction.Bury b:
                            foreach (var c in b.Cards) Card(c);
                            session.Primary.Action();
                            break;
                        case TheCardPlace.Sheephead.SheepheadAction.Play pl: Card(pl.Card); break;
                        case var other: throw new InvalidOperationException("sheephead " + other);
                    }
                    break;
                }
            }
        }
        return false;

        void Card(Card c) => session.Activate(session.Hand.First(h => h.Card == c));

        void Choice(Func<GameControl, bool> which) => session.Secondary.First(which).Action();
    }
}
