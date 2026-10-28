using TheCardPlace.Core;

namespace TheCardPlace.Hearts;

/// <summary>
/// Hearts - card model. A port of `hearts/js/cards.js` by way of HeartsCards.swift.
///
/// There is no trump. Every card follows its printed suit, a trick is won by
/// the highest card of the suit led, and nothing ever beats a suit it does not
/// belong to. So Power and Beats take a card and nothing else: in hearts a
/// card's rank is a property of the card.
///
/// The two cards that matter more than their rank suggests — the queen of
/// spades at thirteen points and every heart at one — are NOT special-cased
/// here. They are worth points, which is scoring, and scoring lives in
/// HeartsGame.
/// </summary>
public static class HeartsCards
{
    /// Clubs, diamonds, spades, hearts: alternating colours, hearts last
    /// because they are what the hand is about.
    public static readonly IReadOnlyList<Suit> SuitOrder = [Suit.Clubs, Suit.Diamonds, Suit.Spades, Suit.Hearts];

    public static readonly Card TwoOfClubs = new(Rank.Two, Suit.Clubs);
    public static readonly Card QueenOfSpades = new(Rank.Queen, Suit.Spades);

    /// Comparable within a suit only. Ace high, deuce low.
    public static int Power(Card c) => (int)c.Rank;

    public static string Name(Card c) => c.Name;

    /// A card is its name. Nothing else. This used to add "one point" to
    /// every heart and "thirteen points" to the queen of spades — that is the
    /// entire game stated back to the player on most of the cards in the deck.
    /// The test is whether the name alone would mislead, and in hearts it never
    /// does.
    public static string Role(Card c) => "";

    public static string Describe(Card c)
    {
        var r = Role(c);
        return r.Length == 0 ? Name(c) : Name(c) + ", " + r;
    }

    /// Does c beat b, where b is the card currently winning the trick?
    /// A card that is not of the led suit cannot win, however high.
    public static bool Beats(Card c, Card b)
    {
        if (c.Suit != b.Suit) return false;
        return Power(c) > Power(b);
    }

    /// Clubs, diamonds, spades, hearts; high to low within each.
    public static List<Card> SortHand(IEnumerable<Card> cards) =>
        cards.OrderBy(c => SuitIndex(c.Suit)).ThenByDescending(Power).ToList();

    private static int SuitIndex(Suit s)
    {
        for (var i = 0; i < SuitOrder.Count; i++)
            if (SuitOrder[i] == s) return i;
        return 0;
    }
}
