using System.Text.Json.Serialization;
using TheCardPlace.Core;

namespace TheCardPlace.Euchre;

/// <summary>
/// Right or left. Swift nests this as <c>EuchreCards.Bower</c>; C# cannot have a nested type and
/// a method of the same name in one class, and <see cref="EuchreCards.Bower"/> is the method a
/// reader expects, so the type lives here.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<EuchreBower>))]
public enum EuchreBower { Right, Left }

/// <summary>
/// The deck, the bowers and trump ordering: the port of <c>euchre/js/cards.js</c>.
///
/// THE ONE THING THAT MAKES EUCHRE DIFFERENT FROM EVERY OTHER TRICK GAME, and the reason almost
/// every function here takes a <c>trump</c> argument:
///
///   The jack of the trump suit is the highest card (the RIGHT BOWER), and the jack of the other
///   suit of the same colour is the second highest (the LEFT BOWER), and while it is the left
///   bower it is not a card of its printed suit at all. With spades trump, the jack of clubs is a
///   spade. It follows spades, it may not be played on a club lead if you hold a spade, and a
///   player who thinks of it as a club will revoke.
///
/// <c>trump</c> is nullable, and null is a real state rather than an oversight: while the bidding
/// is going on nobody knows what trump is. With null trump nothing is trump, every card follows
/// its printed suit, and a hand sorts by suit. That is exactly what a player looking at their
/// cards before bidding needs.
/// </summary>
public static class EuchreCards
{
    /// <summary>Nine up to ace in each suit.</summary>
    public static IReadOnlyList<Rank> Ranks { get; } = [Rank.Nine, Rank.Ten, Rank.Jack, Rank.Queen, Rank.King, Rank.Ace];

    /// <summary>The twenty-four cards.</summary>
    public static IReadOnlyList<Card> Deck { get; } = Card.Deck(Ranks);

    public const int DeckSize = 24;

    /// <summary>
    /// The suits in the order the browser game lists them. It decides the display order of a hand
    /// and the order in which the computer considers suits in round two, so it is kept as data
    /// rather than left to <see cref="Suit"/>.
    /// </summary>
    public static IReadOnlyList<Suit> Suits { get; } = [Suit.Clubs, Suit.Spades, Suit.Hearts, Suit.Diamonds];

    /// <summary>The suit whose jack becomes the left bower, or null while trump is unknown.</summary>
    public static Suit? LeftBowerSuit(Suit? trump) => trump?.SameColour();

    /// <summary>Right, left, or null. Nothing is a bower while trump is unknown.</summary>
    public static EuchreBower? Bower(Card c, Suit? trump)
    {
        if (trump is not { } t || c.Rank != Rank.Jack) return null;
        if (c.Suit == t) return EuchreBower.Right;
        if (c.Suit == t.SameColour()) return EuchreBower.Left;
        return null;
    }

    public static bool IsTrump(Card c, Suit? trump)
    {
        if (trump is not { } t) return false;
        return c.Suit == t || Bower(c, t) == EuchreBower.Left;
    }

    /// <summary>
    /// The suit a card belongs to for the purposes of following. The left bower answers with the
    /// trump suit, which is the entire point.
    /// </summary>
    public static Suit EffectiveSuit(Card c, Suit? trump)
    {
        if (trump is { } t && IsTrump(c, t)) return t;
        return c.Suit;
    }

    /// <summary>Rank order within a suit, highest first, for cards that are not bowers.</summary>
    private static readonly Rank[] PlainOrder = [Rank.Ace, Rank.King, Rank.Queen, Rank.Jack, Rank.Ten, Rank.Nine];
    /// <summary>The jack has left; it is the right bower.</summary>
    private static readonly Rank[] TrumpPlainOrder = [Rank.Ace, Rank.King, Rank.Queen, Rank.Ten, Rank.Nine];

    /// <summary>
    /// A single comparable number, meaningful only within one trump suit.
    ///
    /// Every trump outranks every non-trump, so the two bands cannot collide, and the bowers sit
    /// above the ace of trump because that is what they are. A rank outside the euchre deck sorts
    /// below the nine rather than trapping.
    /// </summary>
    public static int Power(Card c, Suit? trump)
    {
        switch (Bower(c, trump))
        {
            case EuchreBower.Right: return 300;
            case EuchreBower.Left: return 290;
        }
        if (IsTrump(c, trump))
        {
            var i = Array.IndexOf(TrumpPlainOrder, c.Rank);
            if (i < 0) i = TrumpPlainOrder.Length;
            return 200 + (TrumpPlainOrder.Length - i);
        }
        var j = Array.IndexOf(PlainOrder, c.Rank);
        if (j < 0) j = PlainOrder.Length;
        return 100 + (PlainOrder.Length - j);
    }

    /// <summary>
    /// What is worth SAYING about a card beyond its name, given what trump is.
    ///
    /// Empty for an ordinary card, and that is deliberate. The name already contains the suit, so
    /// appending it again produced "Queen of Hearts, hearts" on every single play: a redundancy
    /// that is merely untidy on screen and genuinely wearing when every card of every trick is
    /// read aloud.
    ///
    /// The bowers are always named as bowers, because "Jack of Clubs" is actively misleading when
    /// clubs are not trump and that jack is the second highest card in the game.
    /// </summary>
    public static string Role(Card c, Suit? trump)
    {
        switch (Bower(c, trump))
        {
            case EuchreBower.Right: return "right bower, the highest trump";
            case EuchreBower.Left: return "left bower, second highest trump, counts as " + (trump?.LowerName() ?? "");
        }
        if (IsTrump(c, trump)) return "trump";
        return "";
    }

    /// <summary>"Jack of Clubs, left bower, second highest trump, counts as spades".</summary>
    public static string Describe(Card c, Suit? trump)
    {
        var r = Role(c, trump);
        return r.Length == 0 ? c.Name : c.Name + ", " + r;
    }

    /// <summary>Does <paramref name="c"/> beat <paramref name="b"/>, given that b is the card currently winning the trick?</summary>
    public static bool Beats(Card c, Card b, Suit? trump)
    {
        bool ct = IsTrump(c, trump), bt = IsTrump(b, trump);
        if (ct) return !bt || Power(c, trump) > Power(b, trump);
        if (bt) return false;
        return c.Suit == b.Suit && Power(c, trump) > Power(b, trump);
    }

    internal static int SuitGroup(Suit s, Suit? trump)
    {
        if (trump is { } t && s == t) return -1;
        for (var i = 0; i < Suits.Count; i++)
            if (Suits[i] == s) return i;
        return Suits.Count;
    }

    /// <summary>
    /// Trump high to low, then the other suits high to low in a fixed order.
    ///
    /// The left bower sorts into the trump group, which is where a player expects to find it and
    /// where a screen reader will read it out. A hand sorted by printed suit would file it under
    /// its own colour and quietly hide the second best card in the game among the rubbish.
    /// </summary>
    public static List<Card> SortHand(IEnumerable<Card> cards, Suit? trump = null) =>
        cards
            .OrderBy(c => SuitGroup(EffectiveSuit(c, trump), trump))
            .ThenByDescending(c => Power(c, trump))
            .ToList();
}
