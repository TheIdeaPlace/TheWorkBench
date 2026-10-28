using TheCardPlace.Core;

namespace TheCardPlace.Spades;

/// <summary>
/// The card model for spades: the port of <c>spades/js/cards.js</c>.
///
/// What makes this different from the other games: the trump suit is a constant. Not chosen at
/// the deal like euchre's, not a fourteen-card construction like sheephead's, not absent like
/// hearts'. Spades are trump, in every hand, for ever. So <see cref="Beats"/> knows about spades
/// because in this game every card comparison does, and nothing here takes a trump argument
/// because there is nothing to pass.
/// </summary>
public static class SpadesCards
{
    /// <summary>Named once. Everything below that needs to know reads this.</summary>
    public const Suit Trump = Suit.Spades;

    /// <summary>
    /// Clubs, diamonds, hearts, spades: the order a hand is shown and read in. Trump last,
    /// deliberately: spades are the resource, and a player deciding whether to bid four wants to
    /// find their trump length in one place, at the end, where the hand ends.
    /// </summary>
    public static IReadOnlyList<Suit> SuitOrder { get; } = [Suit.Clubs, Suit.Diamonds, Suit.Hearts, Suit.Spades];

    /// <summary>Ace high, deuce low, as the numbers a player says aloud.</summary>
    public static IReadOnlyList<Rank> RankOrder { get; } =
    [
        Rank.Ace, Rank.King, Rank.Queen, Rank.Jack, Rank.Ten, Rank.Nine, Rank.Eight,
        Rank.Seven, Rank.Six, Rank.Five, Rank.Four, Rank.Three, Rank.Two,
    ];

    public const int DeckSize = 52;
    public const int HandSize = 13;

    public static bool IsTrump(Card c) => c.Suit == Trump;

    /// <summary>
    /// Comparable within a suit only. Deliberately NOT comparable across suits: the two of spades
    /// beats the ace of hearts and no single number can express that while also ranking the ace
    /// above the king. <see cref="Beats"/> is the function that knows what a trick means.
    /// </summary>
    public static int Power(Card c) => (int)c.Rank;

    public static string Name(Card c) => c.Name;

    /// <summary>
    /// What is worth saying about a card beyond its name: "trump" on every spade, and nothing on
    /// anything else. A player hearing "the two of spades" while holding the ace of hearts needs
    /// to know which of those takes the trick, and in this game it is the two.
    /// </summary>
    public static string Role(Card c) => IsTrump(c) ? "trump" : "";

    /// <summary>"Two of Spades, trump" or "Ace of Hearts".</summary>
    public static string Describe(Card c)
    {
        var r = Role(c);
        return r.Length == 0 ? c.Name : c.Name + ", " + r;
    }

    public static string ShortText(Card c) => c.ShortText;

    /// <summary>
    /// Does <paramref name="c"/> beat <paramref name="b"/>, where b is the card currently winning
    /// the trick?
    ///
    /// Three cases and they have to be in this order: a trump against a non-trump wins, whatever
    /// the ranks; a non-trump against a trump loses, whatever the ranks; otherwise the suits must
    /// match and the higher rank wins. The suit check is against the winning card rather than a
    /// remembered lead because the winning card is always either of the led suit or a trump:
    /// nothing else can have taken the lead.
    /// </summary>
    public static bool Beats(Card c, Card b)
    {
        if (IsTrump(c) && !IsTrump(b)) return true;
        if (!IsTrump(c) && IsTrump(b)) return false;
        if (c.Suit != b.Suit) return false;
        return Power(c) > Power(b);
    }

    /// <summary>
    /// Fifty-two cards in the order <c>cards.js</c> builds them: clubs, diamonds, hearts, spades;
    /// ace down to two within each. (Not <see cref="Card.FullDeck"/>, which runs two up to ace: the
    /// shuffle starts from this order so a seed deals the same hands as the Swift engine.)
    /// </summary>
    public static List<Card> NewDeck() => SuitOrder.SelectMany(s => RankOrder.Select(r => new Card(r, s))).ToList();

    /// <summary>Clubs, diamonds, hearts, spades; high to low within each.</summary>
    public static List<Card> SortHand(IEnumerable<Card> cards) =>
        cards.OrderBy(c => SuitIndex(c.Suit)).ThenByDescending(Power).ToList();

    internal static int SuitIndex(Suit s)
    {
        for (var i = 0; i < SuitOrder.Count; i++)
            if (SuitOrder[i] == s) return i;
        return 0;
    }
}
