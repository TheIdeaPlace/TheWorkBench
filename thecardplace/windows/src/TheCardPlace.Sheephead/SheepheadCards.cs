namespace TheCardPlace.Sheephead;

/// <summary>
/// The suit a card follows. Trump is its own suit: the queen of clubs does not follow a lead of
/// clubs. <see cref="Fail"/> is null for trump.
/// </summary>
public readonly record struct SheepheadEffectiveSuit(Suit? Fail)
{
    public static SheepheadEffectiveSuit Trump => new(null);

    public bool IsTrump => Fail is null;

    /// <summary>"trump" or "clubs", as it reads mid-sentence.</summary>
    public string LowerName => Fail is { } s ? s.LowerName() : "trump";

    /// <summary>"Trump" or "Clubs", as it reads at the start of a sentence.</summary>
    public string Name => Fail is { } s ? s.Name() : "Trump";
}

/// <summary>
/// The sheephead card model: a port of <c>sheephead-multiplayer/js/cards.js</c>.
///
/// Trump is a single fourteen-card suit — every queen, then every jack, then the whole diamond
/// suit — and it outranks every fail card. Fail suits are clubs, spades and hearts, ranked ace,
/// ten, king, nine, eight, seven.
/// </summary>
public static class SheepheadCards
{
    /// <summary>Seven up to ace: the thirty-two card pack.</summary>
    public static readonly IReadOnlyList<Rank> Ranks =
        [Rank.Seven, Rank.Eight, Rank.Nine, Rank.Ten, Rank.Jack, Rank.Queen, Rank.King, Rank.Ace];

    /// <summary>The suits that are not trump, in the order a hand is read out.</summary>
    public static readonly IReadOnlyList<Suit> FailSuits = [Suit.Clubs, Suit.Spades, Suit.Hearts];

    /// <summary>Trump, highest first.</summary>
    public static readonly IReadOnlyList<Card> TrumpOrder =
        Card.ParseList("QC QS QH QD JC JS JH JD AD TD KD 9D 8D 7D");

    /// <summary>Within a fail suit, highest first. The ten sits above the king.</summary>
    public static readonly IReadOnlyList<Rank> FailRankOrder =
        [Rank.Ace, Rank.Ten, Rank.King, Rank.Nine, Rank.Eight, Rank.Seven];

    public const int TotalPoints = 120;

    /// <summary>The jack of diamonds: whoever holds it is the picker's partner at four players or more.</summary>
    public static readonly Card PartnerCard = new(Rank.Jack, Suit.Diamonds);

    /// <summary>All thirty-two cards.</summary>
    public static readonly IReadOnlyList<Card> FullDeck = Card.Deck(Ranks);

    public static bool IsTrump(Card c) => c.Rank == Rank.Queen || c.Rank == Rank.Jack || c.Suit == Suit.Diamonds;

    /// <summary>A single comparable number: every trump outranks every fail card.</summary>
    public static int Power(Card c)
    {
        for (var i = 0; i < TrumpOrder.Count; i++)
            if (TrumpOrder[i] == c) return 200 - i;
        for (var i = 0; i < FailRankOrder.Count; i++)
            if (FailRankOrder[i] == c.Rank) return 100 - i;
        return 100 - 99;
    }

    public static SheepheadEffectiveSuit EffectiveSuit(Card c) =>
        IsTrump(c) ? SheepheadEffectiveSuit.Trump : new SheepheadEffectiveSuit(c.Suit);

    public static int Points(Card c) => c.Rank switch
    {
        Rank.Ace => 11,
        Rank.Ten => 10,
        Rank.King => 4,
        Rank.Queen => 3,
        Rank.Jack => 2,
        _ => 0,
    };

    public static int SumPoints(IEnumerable<Card> cards) => cards.Sum(Points);

    /// <summary>
    /// "trump, 3 points" or "Clubs fail, 11 points". Every sheephead card has something worth
    /// saying beyond its name, so this is never empty.
    /// </summary>
    public static string Role(Card c)
    {
        var p = Points(c);
        return (IsTrump(c) ? "trump" : c.Suit.Name() + " fail") + ", " + Prose.Count(p, "point");
    }

    /// <summary>"Queen of Clubs, trump, 3 points".</summary>
    public static string Describe(Card c) => c.Name + ", " + Role(c);

    /// <summary>Does <paramref name="c"/> beat <paramref name="b"/>, given that b is the card currently winning the trick?</summary>
    public static bool Beats(Card c, Card b)
    {
        if (IsTrump(c)) return !IsTrump(b) || Power(c) > Power(b);
        return !IsTrump(b) && c.Suit == b.Suit && Power(c) > Power(b);
    }

    /// <summary>
    /// The deck for a table: thirty-two cards, or thirty at four players, where the seven and
    /// eight of diamonds come out so a full hand still leaves a two-card blind. Both are worth
    /// nothing, so a hand is 120 points at every table size.
    /// </summary>
    public static List<Card> Deck(int players)
    {
        var exclude = SheepheadGame.DealSpec(players).Exclude;
        return FullDeck.Where(c => !exclude.Contains(c)).ToList();
    }

    private static int Group(Card c)
    {
        if (IsTrump(c)) return 0;
        return c.Suit switch
        {
            Suit.Clubs => 1,
            Suit.Spades => 2,
            _ => 3,
        };
    }

    /// <summary>Trump high-to-low, then clubs, spades and hearts, each high-to-low.</summary>
    public static List<Card> SortHand(IEnumerable<Card> cards) =>
        cards.OrderBy(Group).ThenByDescending(Power).ToList();
}
