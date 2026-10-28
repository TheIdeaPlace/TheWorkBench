using TheCardPlace.Core;

namespace TheCardPlace.Hearts;

// Hearts - the computer players. A port of `hearts/js/ai.js` by way of HeartsAI.swift.
//
// Hearts is an avoidance game: there is no trick you want to win. Every
// heuristic here is about ducking — getting under the trick, getting rid of
// dangerous cards while somebody else is committed, and above all not being
// the one holding the queen of spades when she falls.
//
// ONLY WHAT ITS SEAT CAN SEE. Its own hand, the cards on the table, the tricks
// that have been taken, and what it was passed. Not the other three hands.
// `Unseen` is the honest substitute: the whole pack, minus what this seat
// holds, minus everything already played.
//
// The browser AI has no difficulty branching — ui.js always sends 'hard' and
// ai.js never reads it — so neither does this one. It is also fully
// deterministic; `rng` is accepted for the contract and never consulted.
public static class HeartsAI
{
    /// The computer's move for this seat. Null if the seat has nothing to decide.
    public static HeartsAction? Decide(HeartsState state, int seat, RandomSource rng)
    {
        if (seat < 0 || seat >= state.Players.Count) return null;
        switch (state.Phase)
        {
            case HeartsPhase.Passing:
                if (seat >= state.Passing.Count || state.Passing[seat] is not null) return null;
                return new HeartsAction.Pass(ChoosePass(state, seat));
            case HeartsPhase.Play:
                return ChooseCard(state, seat) is { } c ? new HeartsAction.Play(c) : null;
            default:
                return null;
        }
    }

    /// Everything this seat has not seen: the pack minus its own hand minus
    /// every card that has hit the table. Cards in other hands and cards not
    /// yet played are indistinguishable from here, which is exactly the point.
    public static List<Card> Unseen(HeartsState state, int seat)
    {
        var gone = new HashSet<Card>(state.Players[seat].Hand);
        foreach (var p in state.Players) gone.UnionWith(p.Taken);
        foreach (var t in state.Trick) gone.Add(t.Card);
        return Card.FullDeck.Where(c => !gone.Contains(c)).ToList();
    }

    // MARK: - Passing

    public static List<Card> ChoosePass(HeartsState state, int seat)
    {
        var hand = state.Players[seat].Hand;
        var spades = hand.CountOf(Suit.Spades);
        // Highest risk first; ties keep hand order, as the browser's stable sort did.
        return hand.Select((c, i) => (I: i, C: c, Risk: PassRisk(c, hand, spades)))
            .OrderByDescending(x => x.Risk)
            .ThenBy(x => x.I)
            .Take(HeartsGame.PassCount)
            .Select(x => x.C)
            .ToList();
    }

    internal static int PassRisk(Card c, List<Card> hand, int spades)
    {
        // The queen first, and her guards with her. With three or more spades
        // below her you can afford to hold her; with fewer, she comes down on
        // somebody's ace.
        if (c == HeartsCards.QueenOfSpades) return spades >= 4 ? 60 : 100;
        if (c.Suit == Suit.Spades && HeartsCards.Power(c) > HeartsCards.Power(HeartsCards.QueenOfSpades))
            return spades >= 4 ? 55 : 90;

        var len = hand.CountOf(c.Suit);
        var high = HeartsCards.Power(c);

        // A high card in a short suit is a trick you cannot avoid winning.
        var risk = high * 2 - len * 6;

        // Hearts are kept unless they are genuinely high. Passing low hearts
        // away is how the seat on your left ends up with the material to shoot.
        if (c.Suit == Suit.Hearts) risk = high >= 12 ? high * 2 - len * 4 : high - 20;

        return risk;
    }

    // MARK: - Playing

    public static Card? ChooseCard(HeartsState state, int seat)
    {
        var legal = HeartsGame.LegalPlays(state, seat);
        if (legal.Count == 0) return null;
        if (legal.Count == 1) return legal[0];
        return state.Trick.Count == 0 ? ChooseLead(state, seat, legal) : ChooseFollow(state, seat, legal);
    }

    internal static Card ChooseLead(HeartsState state, int seat, List<Card> legal)
    {
        var hand = state.Players[seat].Hand;
        var @out = Unseen(state, seat);

        // If the queen is still out there and this seat does not hold her,
        // leading spades from below her is how she gets flushed.
        var queenGone = !@out.Contains(HeartsCards.QueenOfSpades) && !hand.Contains(HeartsCards.QueenOfSpades);

        var best = legal[0];
        var bestScore = int.MinValue;
        foreach (var c in legal)
        {
            var score = 0;
            var outHigher = @out.Count(o => o.Suit == c.Suit && HeartsCards.Power(o) > HeartsCards.Power(c));

            // Low is good: the more of the suit above it, the less likely this
            // seat takes the trick.
            score += outHigher * 8 - HeartsCards.Power(c) * 2;

            if (c.Suit == Suit.Hearts) score -= 25;                                      // leading hearts gives points away
            if (c == HeartsCards.QueenOfSpades) score -= 200;                            // never
            if (c.Suit == Suit.Spades && !queenGone && HeartsCards.Power(c) > 12) score -= 60;

            // Leading a suit this seat is short in is how you get a void.
            var len = hand.CountOf(c.Suit);
            if (len <= 2 && c.Suit != Suit.Hearts) score += 12;

            if (score > bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    internal static Card ChooseFollow(HeartsState state, int seat, List<Card> legal)
    {
        var led = state.Trick[0].Card.Suit;
        var winning = state.Trick[0].Card;
        foreach (var t in state.Trick)
            if (HeartsCards.Beats(t.Card, winning)) winning = t.Card;

        var following = legal.Where(c => c.Suit == led).ToList();

        if (following.Count > 0)
        {
            var under = following.Where(c => !HeartsCards.Beats(c, winning)).ToList();
            // Duck as high as you can safely go.
            if (under.Count > 0) return Highest(under);
            // Forced to win. Take it with the lowest card that does.
            return Lowest(following);
        }

        // A discard. The best moment in the game to be rid of something.
        if (legal.Contains(HeartsCards.QueenOfSpades)) return HeartsCards.QueenOfSpades;

        var spadesHigh = legal.Where(c => c.Suit == Suit.Spades && HeartsCards.Power(c) > HeartsCards.Power(HeartsCards.QueenOfSpades)).ToList();
        if (spadesHigh.Count > 0) return Highest(spadesHigh);

        var hearts = legal.Where(c => c.Suit == Suit.Hearts).ToList();
        if (hearts.Count > 0) return Highest(hearts);

        return Highest(legal);
    }

    /// First wins ties, as the browser's reduce did.
    internal static Card Highest(List<Card> cards) =>
        cards.Skip(1).Aggregate(cards[0], (a, b) => HeartsCards.Power(b) > HeartsCards.Power(a) ? b : a);

    internal static Card Lowest(List<Card> cards) =>
        cards.Skip(1).Aggregate(cards[0], (a, b) => HeartsCards.Power(b) < HeartsCards.Power(a) ? b : a);
}
