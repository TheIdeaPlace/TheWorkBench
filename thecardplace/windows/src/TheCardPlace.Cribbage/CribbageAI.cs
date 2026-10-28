using TheCardPlace.Core;

namespace TheCardPlace.Cribbage;

/// <summary>
/// The computer player: a port of <c>cribbage-multiplayer/js/ai.js</c>.
///
/// THE RULE THIS FILE EXISTS TO KEEP: IT READS ONLY WHAT ITS OWN SEAT IS ENTITLED TO. Its own hand,
/// its own discard, everything on the table, the starter. Never the crib, never the undealt
/// remainder, never the other seat's hand. Instead of asking "can my opponent make thirty-one" it
/// asks "how many of the cards I have not seen would make thirty-one", which is what a good human
/// player does, is often wrong, and is wrong the way a person is wrong. The hidden-information
/// tests enforce it by scrambling everything the computer may not see.
/// </summary>
public static class CribbageAI
{
    /// <summary>
    /// Everything this seat has not seen: the whole deck, less its own cards, less its own discard,
    /// less everything on the table.
    /// </summary>
    public static List<Card> Unseen(CribbageState state, int p)
    {
        var seen = new HashSet<Card>();
        var me = state.Players[p];
        seen.UnionWith(me.Hand);
        seen.UnionWith(me.Kept);
        seen.UnionWith(me.Played);
        seen.UnionWith(state.Pile.Select(x => x.Card));
        if (p < state.Discarded.Count && state.Discarded[p] is { } d) seen.UnionWith(d);
        if (state.Starter is { } s) seen.Add(s);
        return Card.FullDeck.Where(c => !seen.Contains(c)).ToList();
    }

    // MARK: - The discard

    /// <summary>
    /// What four kept cards are worth AVERAGED OVER EVERY STARTER THAT COULD STILL COME, rather
    /// than on the bare four, because a hand's value in cribbage is mostly about what it can become.
    /// </summary>
    public static double AverageHandValue(IReadOnlyList<Card> kept, IReadOnlyList<Card> unseen)
    {
        if (unseen.Count == 0) return CribbageScoring.QuickTotal(kept, null, false);
        var total = 0;
        foreach (var s in unseen) total += CribbageScoring.QuickTotal(kept, s, false);
        return (double)total / unseen.Count;
    }

    /// <summary>
    /// What two cards are worth going into a crib, before anything joins them. A small heuristic:
    /// fives are the whole story; touching cards and pairs are most of the rest.
    /// </summary>
    public static double CribValue(Card a, Card b)
    {
        var v = 0.0;
        int va = CribbageCards.Value(a), vb = CribbageCards.Value(b);
        if (va + vb == 15) v += 2.5;
        if (a.Rank == b.Rank) v += 2.2;
        var gap = Math.Abs(CribbageCards.Order(a) - CribbageCards.Order(b));
        if (gap == 1) v += 1.4;        // touching: a run is one card away
        else if (gap == 2) v += 0.7;   // a gap a single card fills
        if (a.Rank == Rank.Five) v += 1.9;
        if (b.Rank == Rank.Five) v += 1.9;
        // A jack in the crib is a nob about one time in four.
        if (a.Rank == Rank.Jack) v += 0.25;
        if (b.Rank == Rank.Jack) v += 0.25;
        return v;
    }

    /// <summary>
    /// Fifteen ways to throw two of six, each judged on what the four kept are worth, then the crib
    /// added or subtracted depending on whose it is.
    /// </summary>
    public static List<Card> ChooseDiscard(CribbageState state, int p)
    {
        var hand = state.Players[p].Hand;
        if (hand.Count < 2) return hand.ToList();
        var unseenCards = Unseen(state, p);
        var mine = state.Dealer == p;
        double? bestScore = null;
        List<Card>? bestThrown = null;

        for (var i = 0; i < hand.Count; i++)
        {
            for (var j = i + 1; j < hand.Count; j++)
            {
                List<Card> thrown = [hand[i], hand[j]];
                var kept = hand.Where(c => c != thrown[0] && c != thrown[1]).ToList();
                var value = AverageHandValue(kept, unseenCards);
                var crib = CribValue(thrown[0], thrown[1]);
                var score = value + (mine ? crib : -crib);
                if (bestScore is null || score > bestScore)
                {
                    bestScore = score;
                    bestThrown = thrown;
                }
            }
        }
        return bestThrown ?? hand.Take(2).ToList();
    }

    // MARK: - The play

    /// <summary>
    /// How many unseen cards would take a count of <paramref name="from"/> to exactly
    /// <paramref name="to"/>. The honest version of "can my opponent make thirty-one".
    /// </summary>
    internal static int UnseenMaking(IEnumerable<Card> unseen, int from, int to) =>
        unseen.Count(c => from + CribbageCards.Value(c) == to);

    internal static int UnseenOfRank(IEnumerable<Card> unseen, Rank r) => unseen.Count(c => c.Rank == r);

    /// <summary>The card to lay, or null when nothing fits under thirty-one.</summary>
    public static Card? ChooseCard(CribbageState state, int p, RandomSource rng)
    {
        var legal = CribbageGame.LegalPlays(state, p);
        if (legal.Count == 0) return null;
        if (legal.Count == 1) return legal[0];

        switch (state.Config.Difficulty)
        {
            case Difficulty.Easy:
                if (rng.Chance(0.35)) return legal[rng.NextInt(legal.Count)];
                break;
            case Difficulty.Normal:
                if (rng.Chance(0.12)) return legal[rng.NextInt(legal.Count)];
                break;
            case Difficulty.Hard:
                break;
        }

        var unseenCards = Unseen(state, p);
        var scale = Math.Max(1.0, unseenCards.Count / 10.0);
        double? bestScore = null;
        Card? bestCard = null;

        foreach (var card in legal)
        {
            var newCount = state.Count + CribbageCards.Value(card);
            var score = (double)CribbageGame.PointsForPlay(state, card).Total * 10;

            // THE TWO COUNTS NEVER TO LEAVE. Five or twenty-one hands the opponent a fifteen or a
            // thirty-one with any of the sixteen ten-cards in the deck.
            if (newCount == 5 || newCount == 21) score -= 9;

            // Everything else, priced by how many unseen cards would punish it.
            score -= UnseenMaking(unseenCards, newCount, 31) * (6 / scale);
            score -= UnseenMaking(unseenCards, newCount, 15) * (4 / scale);
            score -= UnseenOfRank(unseenCards, card.Rank) * 0.6;   // they may pair it

            // Leading. A low card keeps the count out of range of a fifteen and leaves room to
            // answer; a five led is a present.
            if (state.Count == 0)
            {
                if (card.Rank == Rank.Five) score -= 8;
                score -= CribbageCards.Value(card) * 0.25;
                // Leading from a pair is good: if they pair it, you take three of a kind for six.
                var mineSame = state.Players[p].Hand.Count(c => c.Rank == card.Rank);
                if (mineSame >= 2) score += 2.5;
            }

            // Keeping something playable afterwards. Being forced to say go hands over a point and
            // the lead.
            var left = state.Players[p].Hand.Where(c => c != card).ToList();
            var stillPlayable = left.Count(c => newCount + CribbageCards.Value(c) <= 31);
            if (left.Count > 0 && stillPlayable == 0 && newCount < 31) score -= 4;
            else score += stillPlayable * 0.6;

            if (bestScore is null || score > bestScore)
            {
                bestScore = score;
                bestCard = card;
            }
        }
        return bestCard;
    }

    // MARK: - Taking a turn

    /// <summary>The computer's move for this seat, or null if the seat has nothing to decide right now.</summary>
    public static CribbageAction? Decide(CribbageState state, int seat, RandomSource rng)
    {
        if (seat < 0 || seat >= state.Players.Count || CribbageGame.SeatToAct(state) != seat) return null;
        switch (state.Phase)
        {
            case CribbagePhase.CutForDeal: return new CribbageAction.Cut();
            case CribbagePhase.Discard: return new CribbageAction.Discard(ChooseDiscard(state, seat));
            case CribbagePhase.Count: return new CribbageAction.Next();
            case CribbagePhase.Play:
                // No playable card means the only legal thing to do is say go. The engine refuses a
                // go from somebody who can play, so this cannot be used to duck a turn.
                if (ChooseCard(state, seat, rng) is { } card) return new CribbageAction.Play(card);
                return new CribbageAction.Go();
            default: return null;
        }
    }

    /// <summary>
    /// Take one turn for whoever is on move, through the gate like anybody else. Returns the seat
    /// acted for, or null if nobody is on move or the gate refused; a refused bot move is a bug,
    /// and the result says so.
    /// </summary>
    public static (int Seat, ActionResult Result)? Act(CribbageState state, RandomSource rng)
    {
        if (CribbageGame.SeatToAct(state) is not { } p) return null;
        if (Decide(state, p, rng) is not { } action)
            return (p, ActionResult.Faulted($"the computer had no move at phase {state.Phase.RawValue()}"));
        var r = CribbageGame.ApplyAction(state, p, action, rng);
        return (p, r);
    }
}
