using System.Globalization;
using TheCardPlace.Core;

namespace TheCardPlace.Cribbage;

/// <summary>
/// Cribbage's card model: a port of <c>cribbage-multiplayer/js/cards.js</c>.
///
/// A whole 52-card deck and no trump: cribbage is the odd one out among the games here in that no
/// suit ever outranks another. Suits matter in exactly two places, a flush and one for his nob,
/// and nowhere else.
///
/// Two different numbers live on every card, and confusing them is the single commonest cribbage
/// bug:
///
///   value  what it counts for during the play and for fifteens. Ace is one, and the ten, jack,
///          queen and king are ALL ten.
///   order  where it sits in a run, and which card is lower when cutting for deal. Ace is one and
///          the king is thirteen, so a ten is genuinely lower than a jack.
///
/// Keeping the two as separately named functions, rather than one with a flag, is the cheapest
/// way to stop the confusion happening again.
/// </summary>
public static class CribbageCards
{
    /// <summary>What a card counts for. Ace one, court cards ten.</summary>
    public static int Value(Card c) => c.Rank switch
    {
        Rank.Ace => 1,
        Rank.Ten or Rank.Jack or Rank.Queen or Rank.King => 10,
        _ => (int)c.Rank,
    };

    /// <summary>
    /// Where a card sits in a run, and which is lower at the cut. Ace one, king thirteen. NOT the
    /// same as <see cref="Value"/>, and the difference is load-bearing.
    /// </summary>
    public static int Order(Card c) => c.Rank == Rank.Ace ? 1 : (int)c.Rank;

    /// <summary>The suit order a hand is sorted by within a rank, so a hand reads the same way twice.</summary>
    public static IReadOnlyList<Suit> SuitOrder { get; } = [Suit.Clubs, Suit.Spades, Suit.Hearts, Suit.Diamonds];

    /// <summary>
    /// Ascending by run order, then by suit. Cribbage hands are counted, not played competitively
    /// by suit, so low-to-high is what a player expects to hear: the opposite of the trick games
    /// next door.
    /// </summary>
    public static List<Card> SortHand(IEnumerable<Card> cards) =>
        cards.OrderBy(Order).ThenBy(c => SuitIndex(c.Suit)).ToList();

    private static int SuitIndex(Suit s)
    {
        for (var i = 0; i < SuitOrder.Count; i++)
            if (SuitOrder[i] == s) return i;
        return 0;
    }

    /// <summary>
    /// A card is its name. Nothing else. Knowing that a king counts ten IS cribbage; a player who
    /// needs telling is being told the game instead of playing it.
    /// </summary>
    public static string Describe(Card c) => c.Name;

    /// <summary>Nothing worth saying beyond the name: no card in cribbage has a role.</summary>
    public static string Role(Card c) => "";

    public static int SumValue(IEnumerable<Card> cards) => cards.Sum(Value);

    /// <summary>
    /// A list of cards, said the way a person would say it: "Ace of Spades", "Ace of Spades and
    /// Five of Hearts", "A, B and C", and "nothing" for an empty list. Used everywhere a score is
    /// broken down, which is most of cribbage.
    /// </summary>
    public static string ListNames(IEnumerable<Card> cards)
    {
        var list = cards.ToList();
        if (list.Count == 0) return "nothing";
        return Prose.List(list.Select(c => c.Name));
    }

    /// <summary>"Fives", "Sixes", "Jacks": the plural a cribbage player says.</summary>
    public static string Plural(Rank rank) => rank == Rank.Six ? "Sixes" : rank.Name() + "s";

    private static readonly string[] Ones = ["no", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];
    private static readonly string[] Teens =
        ["ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    /// <summary>
    /// Numbers as words. The browser game stops at twenty-nine, because that is the best hand in
    /// cribbage; this goes to ninety-nine because a card label also says what count a card would
    /// make ("makes thirty-four") and the count can be pushed to forty by a refused play.
    /// </summary>
    public static string NumberWord(int n)
    {
        if (n < 0) return n.ToString(CultureInfo.InvariantCulture);
        if (n < 10) return Ones[n];
        if (n < 20) return Teens[n - 10];
        if (n < 100)
        {
            var t = Tens[n / 10];
            var o = n % 10;
            return o == 0 ? t : t + "-" + Ones[o];
        }
        return n.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>"no points", "one point", "two points" …</summary>
    public static string PointWords(int n) => n == 1 ? "one point" : NumberWord(n) + " points";
}
