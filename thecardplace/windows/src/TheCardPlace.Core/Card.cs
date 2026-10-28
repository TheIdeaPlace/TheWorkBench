using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheCardPlace.Core;

/// <summary>
/// The four suits. The letter is the one used in card ids, so "QS" is the queen of spades and
/// "TD" the ten of diamonds: the same ids the browser and iOS games use, so a transcript reads
/// the same everywhere.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<Suit>))]
public enum Suit { Clubs, Diamonds, Hearts, Spades }

/// <summary>Ranks two to ace. The value is the ace-high order a player says aloud.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Rank>))]
public enum Rank { Two = 2, Three, Four, Five, Six, Seven, Eight, Nine, Ten, Jack, Queen, King, Ace }

public static class SuitExtensions
{
    public static readonly Suit[] All = [Suit.Clubs, Suit.Diamonds, Suit.Hearts, Suit.Spades];

    public static string Letter(this Suit s) => s switch
    {
        Suit.Clubs => "C",
        Suit.Diamonds => "D",
        Suit.Hearts => "H",
        _ => "S",
    };

    public static string Name(this Suit s) => s.ToString();

    /// <summary>The name as it appears mid-sentence: "you must follow hearts".</summary>
    public static string LowerName(this Suit s) => s.ToString().ToLowerInvariant();

    /// <summary>The singular, for "a heart", "no spade".</summary>
    public static string Singular(this Suit s) => s.LowerName().TrimEnd('s');

    public static string Symbol(this Suit s) => s switch
    {
        Suit.Clubs => "♣",
        Suit.Diamonds => "♦",
        Suit.Hearts => "♥",
        _ => "♠",
    };

    public static bool IsRed(this Suit s) => s is Suit.Hearts or Suit.Diamonds;

    /// <summary>
    /// The other suit of the same colour. This is the whole of euchre's left bower rule in one
    /// table, and sheephead's doublers use it too.
    /// </summary>
    public static Suit SameColour(this Suit s) => s switch
    {
        Suit.Clubs => Suit.Spades,
        Suit.Spades => Suit.Clubs,
        Suit.Hearts => Suit.Diamonds,
        _ => Suit.Hearts,
    };

    public static Suit? FromLetter(string letter) => letter switch
    {
        "C" => Suit.Clubs,
        "D" => Suit.Diamonds,
        "H" => Suit.Hearts,
        "S" => Suit.Spades,
        _ => null,
    };
}

public static class RankExtensions
{
    public static readonly Rank[] All = Enum.GetValues<Rank>();

    public static string Name(this Rank r) => r.ToString();

    /// <summary>The single character used in ids; the ten is "T".</summary>
    public static string Letter(this Rank r) => r switch
    {
        Rank.Ten => "T",
        Rank.Jack => "J",
        Rank.Queen => "Q",
        Rank.King => "K",
        Rank.Ace => "A",
        _ => ((int)r).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>The text printed on a card face; the ten is "10".</summary>
    public static string ShortText(this Rank r) => r == Rank.Ten ? "10" : r.Letter();

    public static Rank? FromLetter(string letter)
    {
        foreach (var r in All)
            if (r.Letter() == letter) return r;
        return null;
    }
}

/// <summary>
/// A playing card. Equality is by rank and suit; there is one of each in a pack, so a card is its
/// own identity and <see cref="Id"/> is the two-letter form. Serialises as that id ("QS").
/// </summary>
[JsonConverter(typeof(CardJsonConverter))]
public readonly record struct Card(Rank Rank, Suit Suit)
{
    /// <summary>"QS", "TD", "9C".</summary>
    public string Id => Rank.Letter() + Suit.Letter();

    /// <summary>"Queen of Spades": what a card is called when it is read out.</summary>
    public string Name => $"{Rank.Name()} of {Suit.Name()}";

    /// <summary>"Q♠": what a card face shows.</summary>
    public string ShortText => Rank.ShortText() + Suit.Symbol();

    public bool IsRed => Suit.IsRed();

    public override string ToString() => Id;

    public static bool TryParse(string? id, out Card card)
    {
        card = default;
        if (id is null || id.Length != 2) return false;
        var r = RankExtensions.FromLetter(id[..1]);
        var s = SuitExtensions.FromLetter(id[1..]);
        if (r is null || s is null) return false;
        card = new Card(r.Value, s.Value);
        return true;
    }

    /// <summary>The card for an id, or an exception: for literals in code and tests.</summary>
    public static Card Parse(string id) =>
        TryParse(id, out var c) ? c : throw new FormatException($"'{id}' is not a card id");

    /// <summary>Several cards from a space-separated list of ids: "AS TS KH".</summary>
    public static List<Card> ParseList(string ids) =>
        ids.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Parse).ToList();

    /// <summary>
    /// Fifty-two cards: clubs, diamonds, hearts, spades, two up to ace within each. Games that
    /// use fewer cards take a subset with <see cref="Deck"/>.
    /// </summary>
    public static IReadOnlyList<Card> FullDeck { get; } = Deck(RankExtensions.All);

    public static List<Card> Deck(IEnumerable<Rank> ranks, IEnumerable<Suit>? suits = null)
    {
        var rs = ranks.ToList();
        return (suits ?? SuitExtensions.All).SelectMany(s => rs.Select(r => new Card(r, s))).ToList();
    }
}

public sealed class CardJsonConverter : JsonConverter<Card>
{
    public override Card Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var id = reader.GetString();
        return Card.TryParse(id, out var c) ? c : throw new JsonException($"'{id}' is not a card id");
    }

    public override void Write(Utf8JsonWriter writer, Card value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Id);

    public override Card ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Read(ref reader, typeToConvert, options);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Card value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Id);
}

public static class CardListExtensions
{
    public static List<string> Ids(this IEnumerable<Card> cards) => cards.Select(c => c.Id).ToList();

    /// <summary>"Ace of Spades, Ten of Spades and King of Hearts", one name, or "".</summary>
    public static string SpokenList(this IEnumerable<Card> cards) => Prose.List(cards.Select(c => c.Name));

    public static bool ContainsId(this IEnumerable<Card> cards, string id) => cards.Any(c => c.Id == id);

    public static List<Card> Removing(this IEnumerable<Card> cards, Card card) => cards.Where(c => c != card).ToList();

    public static int CountOf(this IEnumerable<Card> cards, Suit suit) => cards.Count(c => c.Suit == suit);
}
