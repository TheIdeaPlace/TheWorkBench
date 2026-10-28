namespace TheCardPlace.Euchre.Tests;

/// <summary>
/// The only test that knows what euchre IS.
///
/// The rules are written out BY HAND from the How to play text as literal data (the trump order
/// for each suit as seven literal card ids, the non-trump order with the left bower removed, the
/// follow-suit rule and the trick winner re-implemented from the sentence in the rules, the
/// scoring table as literal rows) and the engine is measured against them. Nothing in here calls
/// IsTrump, Power, Beats, EffectiveSuit, LegalPlays or TrickWinnerIndex to build its own answers;
/// those are the things on trial.
/// </summary>
public class EuchreRulesOracleTests
{
    static readonly Suit[] Suits = [Suit.Clubs, Suit.Spades, Suit.Hearts, Suit.Diamonds];
    static readonly string[] Ranks = ["A", "K", "Q", "J", "T", "9"];

    /// <summary>"the jack of the other suit of the same color"</summary>
    static readonly Dictionary<Suit, Suit> SameColour = new()
    {
        [Suit.Clubs] = Suit.Spades, [Suit.Spades] = Suit.Clubs, [Suit.Hearts] = Suit.Diamonds, [Suit.Diamonds] = Suit.Hearts,
    };

    static readonly Dictionary<Suit, string> Letter = new()
    {
        [Suit.Clubs] = "C", [Suit.Spades] = "S", [Suit.Hearts] = "H", [Suit.Diamonds] = "D",
    };

    static Suit SuitOfLetter(string l) => Letter.Single(kv => kv.Value == l).Key;

    /// <summary>
    /// "with spades trump the order is: jack of spades, jack of clubs, ace, king, queen, ten, nine
    /// of spades — then everything else"
    /// </summary>
    static readonly Dictionary<Suit, string[]> TrumpOrder = new()
    {
        [Suit.Spades] = ["JS", "JC", "AS", "KS", "QS", "TS", "9S"],
        [Suit.Clubs] = ["JC", "JS", "AC", "KC", "QC", "TC", "9C"],
        [Suit.Hearts] = ["JH", "JD", "AH", "KH", "QH", "TH", "9H"],
        [Suit.Diamonds] = ["JD", "JH", "AD", "KD", "QD", "TD", "9D"],
    };

    /// <summary>The whole 24-card deck, built from the rules text rather than from the engine.</summary>
    static readonly string[] DeckIds = Suits.SelectMany(s => Ranks.Select(r => r + Letter[s])).ToArray();

    /// <summary>The scoring table, as literal rows: what the makers took, whether they were alone, and what each side gets.</summary>
    static readonly (int Made, bool Alone, int Makers, int Others)[] ScoringTable =
    [
        (3, false, 1, 0), (4, false, 1, 0),
        (5, false, 2, 0),
        (5, true, 4, 0),
        (3, true, 1, 0), (4, true, 1, 0),
        (0, false, 0, 2), (1, false, 0, 2), (2, false, 0, 2),
        (0, true, 0, 2), (1, true, 0, 2), (2, true, 0, 2),
    ];

    /// <summary>"Clubs are down to six cards, because their jack has left."</summary>
    static List<string> PlainOrder(Suit suit, Suit trump)
    {
        var all = Ranks.Select(r => r + Letter[suit]).ToList();
        if (suit == SameColour[trump]) return all.Where(id => id != "J" + Letter[suit]).ToList();
        return all;
    }

    /// <summary>What suit a card counts as. The left bower answers "trump".</summary>
    static string OracleSuit(string id, Suit? trump)
    {
        if (trump is not { } t) return id[1..];
        if (TrumpOrder[t].Contains(id)) return "trump";
        return id[1..];
    }

    /// <summary>Where a card sits in its own order. Lower is stronger.</summary>
    static int OracleRank(string id, Suit trump)
    {
        var t = Array.IndexOf(TrumpOrder[trump], id);
        if (t >= 0) return t;
        return PlainOrder(SuitOfLetter(id[1..]), trump).IndexOf(id);
    }

    /// <summary>
    /// "Highest trump takes the trick; with no trump in it, the highest card of the suit led."
    /// Expressed as: does a beat b, where b is currently winning?
    /// </summary>
    static bool OracleBeats(string a, string b, Suit trump)
    {
        string sa = OracleSuit(a, trump), sb = OracleSuit(b, trump);
        if (sa == "trump" && sb != "trump") return true;
        if (sa != "trump" && sb == "trump") return false;
        if (sa != sb) return false;
        return OracleRank(a, trump) < OracleRank(b, trump);
    }

    /// <summary>
    /// "You must follow the suit that was led if you can — remembering that the left bower counts
    /// as trump. If you cannot follow, you may play anything."
    /// </summary>
    static List<string> OracleLegal(List<string> hand, string? led, Suit trump)
    {
        if (led is null) return hand;
        var ledSuit = OracleSuit(led, trump);
        var can = hand.Where(id => OracleSuit(id, trump) == ledSuit).ToList();
        return can.Count == 0 ? hand : can;
    }

    static int OracleTrickWinner(IReadOnlyList<string> ids, Suit trump)
    {
        var best = 0;
        for (var i = 1; i < ids.Count; i++)
            if (OracleBeats(ids[i], ids[best], trump)) best = i;
        return best;
    }

    static (int Makers, int Others) OracleScore(int made, bool alone)
    {
        foreach (var row in ScoringTable)
            if (row.Made == made && row.Alone == alone) return (row.Makers, row.Others);
        Assert.Fail($"the scoring table has no row for {made} tricks, alone {alone}");
        return (0, 0);
    }

    // MARK: 1. The deck

    [Fact]
    public void The_deck_is_the_twenty_four_cards_the_rules_describe()
    {
        Assert.Equal(24, EuchreCards.Deck.Count);
        Assert.Equal(DeckIds.Order(), EuchreCards.Deck.Ids().Order());
        Assert.Equal(24, EuchreCards.Deck.Distinct().Count());
    }

    // MARK: 2. Every ordered pair, under every trump

    [Fact]
    public void Every_ordered_pair_under_every_trump()
    {
        var checks = 0;
        foreach (var trump in Suits)
        {
            foreach (var a in DeckIds)
            {
                foreach (var b in DeckIds)
                {
                    if (a == b) continue;
                    var want = OracleBeats(a, b, trump);
                    var got = EuchreCards.Beats(Support.C(a), Support.C(b), trump);
                    if (got != want) Assert.Fail($"beats({a}, {b}) with {trump} trump: engine says {got}, the rules say {want}");
                    checks++;
                }
            }
        }
        Assert.Equal(24 * 23 * 4, checks);
    }

    [Fact]
    public void The_suit_each_card_follows()
    {
        foreach (var trump in Suits)
        {
            foreach (var id in DeckIds)
            {
                var want = OracleSuit(id, trump);
                var wantSuit = want == "trump" ? trump : SuitOfLetter(want);
                var c = Support.C(id);
                Assert.True(EuchreCards.EffectiveSuit(c, trump) == wantSuit, $"effectiveSuit({id}) with {trump} trump");
                Assert.True(EuchreCards.IsTrump(c, trump) == (want == "trump"), $"isTrump({id}) with {trump} trump disagrees with the rules");
            }
            // With no trump decided, every card is its printed suit and nothing is trump.
            foreach (var id in DeckIds)
            {
                var c = Support.C(id);
                Assert.Equal(c.Suit, EuchreCards.EffectiveSuit(c, null));
                Assert.False(EuchreCards.IsTrump(c, null));
                Assert.Null(EuchreCards.Bower(c, null));
            }
        }
    }

    /// <summary>
    /// The left bower, specifically and by name, because it is the rule everybody gets wrong and
    /// the one worth naming in a failure message.
    /// </summary>
    [Theory]
    [InlineData(Suit.Clubs)]
    [InlineData(Suit.Spades)]
    [InlineData(Suit.Hearts)]
    [InlineData(Suit.Diamonds)]
    public void The_left_bower_by_name(Suit trump)
    {
        var other = SameColour[trump];
        var left = Support.C("J" + Letter[other]);
        var right = Support.C("J" + Letter[trump]);
        var aceTrump = Support.C("A" + Letter[trump]);
        var aceOther = Support.C("A" + Letter[other]);
        Assert.Equal(trump, EuchreCards.EffectiveSuit(left, trump));
        Assert.Equal(EuchreBower.Left, EuchreCards.Bower(left, trump));
        Assert.Equal(EuchreBower.Right, EuchreCards.Bower(right, trump));
        Assert.True(EuchreCards.Beats(right, left, trump), "the right bower must beat the left bower");
        Assert.True(EuchreCards.Beats(left, aceTrump, trump), "the left bower must beat the ace of trump");
        Assert.False(EuchreCards.Beats(aceOther, left, trump),
            $"the ace of {other} must not beat the left bower when {trump} is trump");
        Assert.Equal(other, EuchreCards.LeftBowerSuit(trump));
    }

    // MARK: 3. Tricks, scored independently

    [Fact]
    public void Random_tricks_scored_independently()
    {
        var rng = new RandomSource(424242);
        for (var n = 0; n < 6000; n++)
        {
            var trump = Suits[rng.NextInt(4)];
            var size = 3 + rng.NextInt(2);         // three cards when somebody is alone
            var picked = DeckIds.Shuffled(rng).Take(size).ToList();
            var want = OracleTrickWinner(picked, trump);
            var plays = picked.Select((id, i) => new EuchrePlay(i, Support.C(id))).ToList();
            var got = EuchreGame.TrickWinnerIndex(plays, trump);
            if (got != want) Assert.Fail($"trick winner with {trump} trump for {string.Join(" ", picked)}: engine {got}, rules {want}");
        }
    }

    // MARK: 4. Following suit

    [Fact]
    public void Following_suit()
    {
        var rng = new RandomSource(8675309);
        for (var n = 0; n < 2500; n++)
        {
            var trump = Suits[rng.NextInt(4)];
            var pool = DeckIds.Shuffled(rng);
            var led = pool[0];
            var hand = pool.GetRange(1, 5);
            var s = Support.Position(EuchrePhase.Play, dealer: 0, turn: 1, leader: 0, trump: trump,
                hands: [[], [.. hand], [], []], trick: [(0, led)]);
            var want = OracleLegal(hand, led, trump).Order().ToList();
            var got = EuchreGame.LegalPlays(s, 1).Ids().Order().ToList();
            Assert.True(want.SequenceEqual(got),
                $"legal plays on a {led} lead with {trump} trump holding {string.Join(" ", hand)}: engine {string.Join(" ", got)}");
            // And WhyNot agrees card by card.
            foreach (var id in hand)
            {
                var ok = want.Contains(id);
                var why = EuchreGame.WhyNot(s, 1, Support.C(id));
                Assert.True((why == null) == ok, $"whyNot({id}) on a {led} lead with {trump} trump: {why ?? "nil"}");
            }
        }
    }

    // MARK: 5. The scoring table

    public static TheoryData<int, bool, int, int> ScoringRows()
    {
        var d = new TheoryData<int, bool, int, int>();
        foreach (var r in ScoringTable) d.Add(r.Made, r.Alone, r.Makers, r.Others);
        return d;
    }

    [Theory]
    [MemberData(nameof(ScoringRows))]
    public void The_scoring_table(int made, bool alone, int makers, int others)
    {
        var got = EuchreGame.ScoreTable(made, alone);
        Assert.Equal(makers, got.Makers);
        Assert.Equal(others, got.Others);
    }

    // MARK: 6. Whole hands, re-scored from the rules

    /// <summary>
    /// Every finished hand is re-derived: the tricks are re-won from the recorded plays using the
    /// oracle's own comparison, the makers' count is re-added, and the score is looked up in the
    /// literal table. Nothing here reads the engine's verdict except to disagree with it.
    /// </summary>
    [Fact]
    public void Whole_hands_rescored_from_the_rules()
    {
        var reached = new Dictionary<string, int>
        {
            ["one"] = 0, ["march"] = 0, ["aloneMarch"] = 0, ["aloneOne"] = 0, ["euchred"] = 0,
            ["thrown"] = 0, ["leftBowerPlayed"] = 0, ["leftBowerWon"] = 0,
        };

        for (var g = 0; g < 360; g++)
        {
            var rng = new RandomSource(100_000 + (ulong)g);
            var s = EuchreGame.CreateGame(Support.Config(
                difficulty: DifficultyExtensions.All[g % 3], stick: g % 3 == 0, alone: true, names: ["N", "E", "S", "W"]));
            Support.PlayHand(s, rng);
            Assert.True(Support.HandIsOver(s), "a hand never finished");
            var h = s.History[^1];

            if (h.Result.ThrownIn)
            {
                reached["thrown"]++;
                Assert.Empty(h.Tricks);
                Assert.Equal([0, 0], h.Result.Deltas);
                Assert.Null(h.Trump);
                continue;
            }
            if (h.Trump is not { } trump || h.Maker is not { } maker)
            {
                Assert.Fail($"hand {h.HandNumber} finished with no trump or maker");
                continue;
            }

            var counted = new int[4];
            var leader = h.Tricks.Count > 0 && h.Tricks[0].Plays.Count > 0 ? h.Tricks[0].Plays[0].Player : -1;
            foreach (var t in h.Tricks)
            {
                var ids = t.Plays.Select(p => p.Card.Id).ToList();
                var wi = OracleTrickWinner(ids, trump);
                var winner = t.Plays[wi].Player;
                Assert.True(winner == t.Winner,
                    $"hand {h.HandNumber} trick {t.Number}: engine gave it to seat {t.Winner + 1}, " +
                    $"the rules give it to seat {winner + 1} ({string.Join(" ", ids)}, {trump} trump)");
                counted[winner]++;

                Assert.True(t.Plays[0].Player == leader, $"hand {h.HandNumber} trick {t.Number} was led by the wrong seat");
                leader = winner;

                var ledSuit = OracleSuit(ids[0], trump);
                foreach (var pl in t.Plays.Where(pl => OracleSuit(pl.Card.Id, trump) != ledSuit))
                {
                    /* Not following. Legal only if that seat held nothing of the led suit at that
                     * moment, reconstructed from the deal and what they had already played. */
                    var dealt = h.Dealt[pl.Player].Ids();
                    if (pl.Player == h.Dealer && h.Upcard is { } up && !h.TurnedDown) dealt.Add(up.Id);
                    List<string> alreadyPlayed = [];
                    foreach (var earlier in h.Tricks.Where(e => e.Number < t.Number))
                        foreach (var p2 in earlier.Plays.Where(p2 => p2.Player == pl.Player))
                            alreadyPlayed.Add(p2.Card.Id);
                    var held = dealt.Where(id => id != h.Discard?.Id && !alreadyPlayed.Contains(id)).ToList();
                    var couldFollow = held.Where(id => OracleSuit(id, trump) == ledSuit).ToList();
                    Assert.True(couldFollow.Count == 0,
                        $"hand {h.HandNumber} trick {t.Number}: seat {pl.Player + 1} played {pl.Card.Id} " +
                        $"off a {ledSuit} lead while holding {string.Join(" ", couldFollow)}");
                }

                var leftId = "J" + Letter[SameColour[trump]];
                if (ids.Contains(leftId))
                {
                    reached["leftBowerPlayed"]++;
                    if (ids[wi] == leftId) reached["leftBowerWon"]++;
                }
            }

            Assert.Equal(5, counted.Sum());

            var makerTeam = maker % 2;
            var made = counted[makerTeam] + counted[makerTeam + 2];
            var table = OracleScore(made, h.Alone);
            List<int> want = [0, 0];
            want[makerTeam] = table.Makers;
            want[1 - makerTeam] = table.Others;
            Assert.True(want.SequenceEqual(h.Result.Deltas),
                $"hand {h.HandNumber}: makers took {made}{(h.Alone ? " alone" : "")}, the table says {string.Join("/", want)}");

            if (table.Others == 2) reached["euchred"]++;
            else if (made == 5) reached[h.Alone ? "aloneMarch" : "march"]++;
            else if (h.Alone) reached["aloneOne"]++;
            else reached["one"]++;

            if (h.Alone)
            {
                Assert.Equal((maker + 2) % 4, h.SittingOut);
                foreach (var t in h.Tricks) Assert.Equal(3, t.Plays.Count);
            }
        }

        /* A scoring branch that never came up has not been tested, however green the run looks. */
        foreach (var (name, min) in new[] { ("one", 20), ("march", 8), ("euchred", 8), ("aloneMarch", 1), ("thrown", 2), ("leftBowerWon", 8) })
        {
            Assert.True(reached[name] >= min,
                $"only {reached[name]} hands reached the \"{name}\" case; that branch is effectively untested");
        }
    }
}
