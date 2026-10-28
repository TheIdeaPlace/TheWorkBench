using TheCardPlace.Core;

namespace TheCardPlace.Core.Tests;

public class CardTests
{
    [Theory]
    [InlineData("QS", Rank.Queen, Suit.Spades, "Queen of Spades", "Q♠")]
    [InlineData("TD", Rank.Ten, Suit.Diamonds, "Ten of Diamonds", "10♦")]
    [InlineData("2C", Rank.Two, Suit.Clubs, "Two of Clubs", "2♣")]
    [InlineData("AH", Rank.Ace, Suit.Hearts, "Ace of Hearts", "A♥")]
    public void Ids_names_and_faces(string id, Rank rank, Suit suit, string name, string face)
    {
        var c = Card.Parse(id);
        Assert.Equal(new Card(rank, suit), c);
        Assert.Equal(id, c.Id);
        Assert.Equal(name, c.Name);
        Assert.Equal(face, c.ShortText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Q")]
    [InlineData("1S")]
    [InlineData("QX")]
    [InlineData("QSS")]
    [InlineData(null)]
    public void Not_a_card(string? id) => Assert.False(Card.TryParse(id, out _));

    [Fact]
    public void Full_deck_has_every_card_once()
    {
        Assert.Equal(52, Card.FullDeck.Count);
        Assert.Equal(52, Card.FullDeck.Distinct().Count());
        Assert.Equal("2C", Card.FullDeck[0].Id);
        Assert.Equal("AS", Card.FullDeck[^1].Id);
    }

    [Fact]
    public void Same_colour_suits()
    {
        Assert.Equal(Suit.Spades, Suit.Clubs.SameColour());
        Assert.Equal(Suit.Diamonds, Suit.Hearts.SameColour());
        Assert.True(Suit.Diamonds.IsRed());
        Assert.False(Suit.Clubs.IsRed());
    }

    [Fact]
    public void Cards_serialise_as_their_ids()
    {
        var cards = Card.ParseList("AS TS KH");
        var json = Snapshot.Json(cards);
        Assert.Equal("[\"AS\",\"TS\",\"KH\"]", json);
        Assert.Equal(cards, Snapshot.Clone(cards));
        var byCard = new Dictionary<Card, int> { [Card.Parse("QS")] = 13 };
        Assert.Equal(13, Snapshot.Clone(byCard)[Card.Parse("QS")]);
    }

    [Fact]
    public void Spoken_lists() =>
        Assert.Equal("Ace of Spades, Ten of Spades and King of Hearts", Card.ParseList("AS TS KH").SpokenList());
}

public class ProseTests
{
    [Fact]
    public void Lists()
    {
        Assert.Equal("", Prose.List([]));
        Assert.Equal("A", Prose.List(["A"]));
        Assert.Equal("A and B", Prose.List(["A", "B"]));
        Assert.Equal("A, B and C", Prose.List(["A", "B", "C"]));
    }

    [Fact]
    public void Counts_numbers_and_ordinals()
    {
        Assert.Equal("1 point", Prose.Count(1, "point"));
        Assert.Equal("2 points", Prose.Count(2, "point"));
        Assert.Equal("0 tricks", Prose.Count(0, "trick"));
        Assert.Equal("twenty", Prose.Number(20));
        Assert.Equal("21", Prose.Number(21));
        Assert.Equal("third", Prose.Ordinal(3));
        Assert.Equal("11th", Prose.Ordinal(11));
        Assert.Equal("22nd", Prose.Ordinal(22));
        Assert.Equal("101st", Prose.Ordinal(101));
    }

    [Fact]
    public void Sentences()
    {
        Assert.Equal("You must follow hearts.", Prose.AsSentence("you must follow hearts"));
        Assert.Equal("Done!", Prose.AsSentence("done!"));
        Assert.Equal("", Prose.AsSentence("  "));
    }
}

public class RandomSourceTests
{
    [Fact]
    public void Seeded_is_splitmix64()
    {
        // The published first outputs of SplitMix64 from seed 0, which the iOS engines also produce.
        var r = new RandomSource(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, r.Next());
        Assert.Equal(0x6E789E6AA1B965F4UL, r.Next());
    }

    [Fact]
    public void Same_seed_same_shuffle()
    {
        var a = Card.FullDeck.Shuffled(new RandomSource(42)).Ids();
        var b = Card.FullDeck.Shuffled(new RandomSource(42)).Ids();
        var c = Card.FullDeck.Shuffled(new RandomSource(43)).Ids();
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(52, a.Distinct().Count());
    }

    [Fact]
    public void Bounds()
    {
        var r = new RandomSource(7);
        for (var i = 0; i < 1000; i++)
        {
            var n = r.NextInt(5);
            Assert.InRange(n, 0, 4);
            var u = r.NextUnit();
            Assert.True(u is >= 0 and < 1);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => r.NextInt(0));
        var system = new RandomSource();
        Assert.InRange(system.NextInt(3), 0, 2);
    }
}

public class EventLogTests
{
    [Fact]
    public void Audience_limits_who_hears()
    {
        var log = new EventLog();
        log.Add(EventKind.Deal, "Dealt.");
        log.Add(EventKind.You, "You were passed the Queen of Spades.", audience: 1);
        log.Add(EventKind.Play, "Ruth plays the Two of Clubs.", seat: 2, cards: [Card.Parse("2C")]);
        Assert.Equal(3, log.EventsFor(1).Count);
        Assert.Equal(2, log.EventsFor(0).Count);
        Assert.Single(log.EventsFor(0, since: 1));
        Assert.Equal(3, log.LastId);
        var copy = Snapshot.Clone(log);
        Assert.Equal(Snapshot.Json(log), Snapshot.Json(copy));
        Assert.Equal("2C", copy.Events[2].Cards[0].Id);
    }

    [Fact]
    public void Paces()
    {
        Assert.Null(Pace.WaitForMe.Delay());
        Assert.Equal(TimeSpan.FromMilliseconds(900), Pace.Brisk.Delay());
        Assert.Equal("after four seconds", Pace.Relaxed.Words());
    }
}
