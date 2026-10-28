using static TheCardPlace.Hearts.Tests.Support;

namespace TheCardPlace.Hearts.Tests;

/// The rules of hearts written out as literal data, and the engine checked
/// against them. Nothing here calls the function under test to build its own
/// expectation. The second half mirrors hearts/tests/rules-oracle.js: legal
/// plays re-derived by elimination and compared after every move of many
/// hands played at random.
public class RulesOracleTests
{
    // MARK: - Literal tables

    /// Every card worth anything, and what it is worth. Everything else is nothing.
    private static readonly Dictionary<string, int> ScoringTable = new()
    {
        ["2H"] = 1, ["3H"] = 1, ["4H"] = 1, ["5H"] = 1, ["6H"] = 1, ["7H"] = 1, ["8H"] = 1,
        ["9H"] = 1, ["TH"] = 1, ["JH"] = 1, ["QH"] = 1, ["KH"] = 1, ["AH"] = 1,
        ["QS"] = 13,
    };

    [Fact]
    public void Every_card_is_worth_what_the_table_says()
    {
        var total = 0;
        foreach (var c in Card.FullDeck)
        {
            var want = ScoringTable.GetValueOrDefault(c.Id);
            Assert.Equal(want, HeartsGame.PointsOf([c]));
            Assert.Equal(want > 0, HeartsGame.IsScoring(c));
            total += HeartsGame.PointsOf([c]);
        }
        Assert.Equal(26, total);   // the pack holds twenty-six points
        Assert.Equal(26, HeartsGame.PointsOf(Card.FullDeck));
        Assert.Equal(26, HeartsGame.Moon);
    }

    /// Trick rows: four plays in order, and the position of the winner.
    /// Highest of the suit led; nothing else can win, because there is no trump.
    public static TheoryData<string, int, int> TrickTable => new()
    {
        { "0:2C 1:AC 2:5C 3:9C", 1, 0 },
        { "0:2C 1:AH 2:QS 3:3C", 3, 14 },
        { "2:TD 3:JD 0:2H 1:QD", 1, 1 },
        { "1:KH 2:AH 3:2H 0:3H", 2, 4 },
        { "3:9S 0:QS 1:KS 2:2S", 1, 13 },
        { "0:AS 1:KS 2:QS 3:JS", 0, 13 },
        { "1:3D 2:AC 3:AS 0:AH", 1, 1 },
    };

    [Theory]
    [MemberData(nameof(TrickTable))]
    public void Trick_winner_and_points_match_the_table(string row, int winner, int points)
    {
        var plays = row.Split(' ').Select(p => (Seat: int.Parse(p[..1], System.Globalization.CultureInfo.InvariantCulture), Id: p[2..])).ToList();
        var hands = new[] { new List<string>(), [], [], [] };
        foreach (var (seat, id) in plays) hands[seat].Add(id);
        var s = PlayState(hands.Select(h => h.ToArray()).ToArray(), turn: plays[0].Seat, tricksPlayed: 5, heartsBroken: true);
        var rng = new RandomSource(1);
        foreach (var (seat, id) in plays)
        {
            var r = HeartsGame.ApplyAction(s, seat, new HeartsAction.Play(C(id)), rng);
            Assert.True(r.IsOk, $"{id} refused: {r.Reason}");
        }
        Assert.Equal(winner, s.LastTrick?.Winner);
        Assert.Equal(points, s.LastTrick?.Points);
        Assert.Equal(winner, s.Turn);   // the winner leads next
        Assert.Equal(winner, s.Leader);
        Assert.Equal(4, s.Players[winner].Taken.Count);
        Assert.Equal(6, s.TricksPlayed);
    }

    /// Deal number, direction, and how many seats to the left the cards go.
    private static readonly (int Deal, HeartsPassDirection Dir, int Offset)[] PassTable =
    [
        (1, HeartsPassDirection.Left, 1), (2, HeartsPassDirection.Right, 3), (3, HeartsPassDirection.Across, 2), (4, HeartsPassDirection.Hold, 0),
        (5, HeartsPassDirection.Left, 1), (6, HeartsPassDirection.Right, 3), (7, HeartsPassDirection.Across, 2), (8, HeartsPassDirection.Hold, 0),
    ];

    [Fact]
    public void Pass_direction_rotates_as_the_table_says()
    {
        var rng = new RandomSource(7);
        var s = HeartsGame.CreateGame(Config());
        Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Start(), rng).IsOk);
        foreach (var row in PassTable)
        {
            Assert.Equal(row.Deal, s.DealNumber);
            Assert.Equal(row.Dir, s.PassDirection);
            Assert.Equal(row.Offset, s.PassDirection.Offset());
            Assert.Equal(row.Dir == HeartsPassDirection.Hold ? HeartsPhase.Play : HeartsPhase.Passing, s.Phase);
            if (row.Dir != HeartsPassDirection.Hold)
            {
                // The cards themselves move to the seat the table names, and
                // nobody keeps what they gave.
                var gave = s.Players.Select(p => p.Hand.Take(3).ToList()).ToList();
                for (var seat = 0; seat < 4; seat++)
                {
                    Assert.True(HeartsGame.ApplyAction(s, seat, new HeartsAction.Pass(gave[seat]), rng).IsOk);
                    if (seat < 3)
                    {
                        Assert.True(s.Phase == HeartsPhase.Passing, "the swap happened before everybody chose");
                        Assert.All(s.Players, p => Assert.Equal(13, p.Hand.Count));   // a hand changed size mid-pass
                    }
                }
                Assert.Equal(HeartsPhase.Play, s.Phase);
                for (var from = 0; from < 4; from++)
                {
                    var to = (from + row.Offset) % 4;
                    foreach (var c in gave[from])
                    {
                        Assert.Contains(c, s.Players[to].Hand);         // seat `to` received it from `from`
                        Assert.DoesNotContain(c, s.Players[from].Hand); // and `from` does not still hold it
                    }
                    Assert.Equal<Card>(gave[from], s.Received[to]!);
                }
                Assert.All(s.Players, p => Assert.Equal(13, p.Hand.Count));
            }
            // Re-deal rather than play the hand out; the direction is a
            // function of the deal number.
            s.Phase = HeartsPhase.HandOver;
            Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.NextHand(), rng).IsOk);
        }
    }

    /// The moon, as literal rows: raw points from the cards, and what each
    /// seat is charged.
    public static TheoryData<int[], int[], int?> MoonTable => new()
    {
        { [26, 0, 0, 0], [0, 26, 26, 26], 0 },
        { [0, 0, 26, 0], [26, 26, 0, 26], 2 },
        { [13, 13, 0, 0], [13, 13, 0, 0], null },
        { [25, 1, 0, 0], [25, 1, 0, 0], null },
        { [0, 14, 7, 5], [0, 14, 7, 5], null },
    };

    [Theory]
    [MemberData(nameof(MoonTable))]
    public void Moon_scoring_matches_the_table(int[] raw, int[] charged, int? shooter)
    {
        var hearts = Card.Deck(RankExtensions.All, [Suit.Hearts]);
        var queen = HeartsCards.QueenOfSpades;
        // Build twelve finished tricks so that each seat's pile is worth
        // exactly the raw points, then play a pointless last trick.
        var taken = new[] { new List<Card>(), [], [], [] };
        var pool = new List<Card>(hearts);
        var queenUsed = false;
        for (var seat = 0; seat < 4; seat++)
        {
            var need = raw[seat];
            if (need >= 13 && !queenUsed) { taken[seat].Add(queen); need -= 13; queenUsed = true; }
            for (var k = 0; k < need; k++) { taken[seat].Add(pool[0]); pool.RemoveAt(0); }
        }
        Assert.True(pool.Count == 0 && queenUsed, "the row does not use every point card");
        string[] last = ["2C", "3C", "4C", "5C"];
        // The other thirty-four cards, dealt round the table so that every
        // card is in exactly one pile. Pile sizes do not matter to the rule.
        var filler = Card.FullDeck.Where(c => c.Suit != Suit.Hearts && c != queen && !last.Contains(c.Id)).ToList();
        for (var i = 0; i < filler.Count; i++) taken[i % 4].Add(filler[i]);
        Assert.Equal(48, taken.Sum(t => t.Count));
        var s = PlayState(last.Select(id => new[] { id }).ToArray(), turn: 0, tricksPlayed: 12, heartsBroken: true,
                          taken: taken.Select(t => t.Select(c => c.Id).ToArray()).ToArray());
        s.Players[0].Score = 10;
        var rng = new RandomSource(3);
        for (var seat = 0; seat < 4; seat++)
            Assert.True(HeartsGame.ApplyAction(s, seat, new HeartsAction.Play(C(last[seat])), rng).IsOk);
        Assert.Equal(HeartsPhase.HandOver, s.Phase);
        Assert.Equal(charged, s.Players.Select(p => p.HandPoints));
        Assert.Equal([10 + charged[0], charged[1], charged[2], charged[3]], s.Players.Select(p => p.Score));
        Assert.Equal(shooter, s.History[^1].Shooter);
        Assert.Equal(charged, s.History[^1].Points);
        var moonEvents = s.Log.Events.Where(e => e.Kind == EventKind.Moon).ToList();
        Assert.Equal(shooter is null ? 0 : 1, moonEvents.Count);
        if (shooter is { } sh)
            Assert.Equal($"{Names[sh]} shot the moon — everybody else takes 26.", moonEvents[0].Text);
    }

    [Fact]
    public void Sort_order_is_clubs_diamonds_spades_hearts_high_to_low()
    {
        var hand = Cards("2H", "AS", "TD", "QS", "3C", "KH", "AC", "9D", "2S", "JC");
        string[] want = ["AC", "JC", "3C", "TD", "9D", "AS", "QS", "2S", "KH", "2H"];
        Assert.Equal(want, HeartsCards.SortHand(hand).Ids());
        Assert.Equal(["AC", "KC", "QC"], HeartsCards.SortHand(Card.FullDeck).Take(3).Ids());
        Assert.Equal(["3H", "2H"], HeartsCards.SortHand(Card.FullDeck).TakeLast(2).Ids());
    }

    [Fact]
    public void Card_text_is_the_name_and_nothing_else()
    {
        Assert.Equal("Queen of Spades", HeartsCards.Describe(HeartsCards.QueenOfSpades));
        Assert.Equal("", HeartsCards.Role(HeartsCards.QueenOfSpades));
        Assert.Equal("Ten of Hearts", HeartsCards.Describe(C("TH")));
        Assert.True(HeartsCards.Beats(C("AC"), C("KC")));
        Assert.False(HeartsCards.Beats(C("AH"), C("2C")), "no suit beats a suit it is not");
        Assert.False(HeartsCards.Beats(C("KC"), C("AC")));
    }

    // MARK: - Legal plays, as literal positions

    [Fact]
    public void The_two_of_clubs_leads_the_first_trick_and_is_not_a_choice()
    {
        var s = PlayState([["2C", "AC", "AH", "QS"], ["3C"], ["4C"], ["5C"]], turn: 0);
        Assert.Equal(["2C"], HeartsGame.LegalPlays(s, 0).Ids());
        Assert.Equal("the two of clubs must be led first", HeartsGame.WhyNot(s, 0, C("AC")));
        Assert.Null(HeartsGame.WhyNot(s, 0, C("2C")));
        Assert.Equal(0, HeartsGame.HolderOfTwoOfClubs(s));
    }

    [Fact]
    public void You_must_follow_suit()
    {
        var s = PlayState([["2C"], ["9C", "AH", "3D"], ["4C"], ["5C"]], turn: 1, trick: [(0, "2C")]);
        Assert.Equal(["9C"], HeartsGame.LegalPlays(s, 1).Ids());
        Assert.Equal("you must follow clubs", HeartsGame.WhyNot(s, 1, C("3D")));
        Assert.Equal("you must follow clubs", HeartsGame.WhyNot(s, 1, C("AH")));
    }

    [Fact]
    public void No_points_on_the_first_trick_unless_that_is_all_you_hold()
    {
        var s = PlayState([["2C"], ["QS", "AH", "3D", "KS"], ["4C"], ["5C"]], turn: 1, trick: [(0, "2C")]);
        Assert.Equal(IdSet("3D", "KS"), IdSet(HeartsGame.LegalPlays(s, 1)));
        Assert.Equal("no points on the first trick", HeartsGame.WhyNot(s, 1, C("QS")));
        Assert.Equal("no points on the first trick", HeartsGame.WhyNot(s, 1, C("AH")));

        var all = PlayState([["2C"], ["QS", "AH", "2H"], ["4C"], ["5C"]], turn: 1, trick: [(0, "2C")]);
        Assert.Equal(IdSet("QS", "AH", "2H"), IdSet(HeartsGame.LegalPlays(all, 1)));   // the rule yields
    }

    [Fact]
    public void Hearts_must_be_broken_before_they_are_led_unless_that_is_all_you_hold()
    {
        var s = PlayState([["AH", "2H", "3D", "KS"], ["9C"], ["4C"], ["5C"]], turn: 0, tricksPlayed: 3);
        Assert.Equal(IdSet("3D", "KS"), IdSet(HeartsGame.LegalPlays(s, 0)));
        Assert.Equal("hearts have not been broken", HeartsGame.WhyNot(s, 0, C("AH")));

        var broken = Snapshot.Clone(s);
        broken.HeartsBroken = true;
        Assert.Equal(IdSet("AH", "2H", "3D", "KS"), IdSet(HeartsGame.LegalPlays(broken, 0)));
        Assert.Null(HeartsGame.WhyNot(broken, 0, C("AH")));

        var only = PlayState([["AH", "2H"], ["9C"], ["4C"], ["5C"]], turn: 0, tricksPlayed: 3);
        Assert.Equal(IdSet("AH", "2H"), IdSet(HeartsGame.LegalPlays(only, 0)));   // the rule yields

        // The queen of spades may be led any time after the first trick.
        var queen = PlayState([["QS", "2H"], ["9C"], ["4C"], ["5C"]], turn: 0, tricksPlayed: 3);
        Assert.Equal(["QS"], HeartsGame.LegalPlays(queen, 0).Ids());
    }

    [Fact]
    public void Discarding_a_heart_breaks_them()
    {
        var s = PlayState([["2C", "3D"], ["9C", "AH"], ["4C", "2D"], ["5C", "4D"]], turn: 0);
        var rng = new RandomSource(1);
        Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Play(C("2C")), rng).IsOk);
        // First trick: seat 1 cannot follow and may not discard the heart.
        Assert.Equal(["9C"], HeartsGame.LegalPlays(s, 1).Ids());
        Assert.True(HeartsGame.ApplyAction(s, 1, new HeartsAction.Play(C("9C")), rng).IsOk);
        Assert.True(HeartsGame.ApplyAction(s, 2, new HeartsAction.Play(C("4C")), rng).IsOk);
        Assert.True(HeartsGame.ApplyAction(s, 3, new HeartsAction.Play(C("5C")), rng).IsOk);
        Assert.Equal(1, s.Turn);
        Assert.False(s.HeartsBroken);
        Assert.True(HeartsGame.ApplyAction(s, 1, new HeartsAction.Play(C("AH")), rng).IsOk,
                    "nothing but a heart: the lead rule yields");
        Assert.True(s.HeartsBroken);
        Assert.Contains(s.Log.Events, e => e.Kind == EventKind.Info && e.Text == "Hearts are broken.");
    }

    [Theory]
    [InlineData(50, 11UL)]
    [InlineData(50, 12UL)]
    [InlineData(50, 13UL)]
    [InlineData(100, 11UL)]
    [InlineData(100, 12UL)]
    [InlineData(100, 13UL)]
    public void The_game_ends_on_the_hand_somebody_reaches_the_target(int target, ulong seed)
    {
        var end = PlayGame(seed, Config(pointsToWin: target));
        Assert.Contains(end.Players, p => p.Score >= target);
        foreach (var h in end.History.SkipLast(1))
            Assert.True(h.Scores.All(x => x < target), $"hand {h.Deal} left somebody at or past {target} and the game carried on");
        var low = end.Players.Min(p => p.Score);
        var winners = end.Players.Where(p => p.Score == low).ToList();
        Assert.Equal(winners.Count == 1 ? winners[0].Index : null, end.Winner);   // lowest score wins
    }

    // MARK: - The oracle, by elimination, over random legal play

    /// Start from the whole hand and remove what the rules forbid, then put
    /// everything back if nothing survives. The engine does it the other way.
    private static HashSet<Card> OracleLegal(HeartsState s, int seat)
    {
        var hand = s.Players[seat].Hand;
        var leading = s.Trick.Count == 0;
        var first = s.TricksPlayed == 0;
        if (leading && first && hand.Contains(HeartsCards.TwoOfClubs)) return [HeartsCards.TwoOfClubs];
        var allowed = hand.ToList();
        if (!leading)
        {
            var led = s.Trick[0].Card.Suit;
            if (hand.Any(c => c.Suit == led)) allowed = allowed.Where(c => c.Suit == led).ToList();
        }
        if (leading && !first && !HeartEverPlayed(s))
        {
            var notHearts = allowed.Where(c => c.Suit != Suit.Hearts).ToList();
            if (notHearts.Count > 0) allowed = notHearts;
        }
        if (first)
        {
            var safe = allowed.Where(c => HeartsGame.PointsOf([c]) == 0).ToList();
            if (safe.Count > 0) allowed = safe;
        }
        if (allowed.Count == 0) allowed = hand.ToList();
        return [.. allowed];
    }

    /// Two low, ace high, written out rather than asked of the engine.
    private static readonly Rank[] LiteralRankOrder =
        [Rank.Two, Rank.Three, Rank.Four, Rank.Five, Rank.Six, Rank.Seven, Rank.Eight, Rank.Nine, Rank.Ten, Rank.Jack, Rank.Queen, Rank.King, Rank.Ace];

    private static int OracleWinner(List<HeartsTrickPlay> plays)
    {
        var led = plays[0].Card.Suit;
        var best = plays[0];
        foreach (var p in plays.Skip(1))
            if (p.Card.Suit == led && Array.IndexOf(LiteralRankOrder, p.Card.Rank) > Array.IndexOf(LiteralRankOrder, best.Card.Rank))
                best = p;
        return best.Seat;
    }

    [Fact]
    public void Engine_agrees_with_the_oracle_over_random_legal_play()
    {
        int heartsLed = 0, firstTrickDiscards = 0, hands = 0, tricks = 0, queens = 0;
        var dirs = new HashSet<HeartsPassDirection>();
        var rng = new RandomSource(20260821);
        foreach (var (games, target) in new[] { (24, 100), (10, 50) })
        {
            for (var g = 0; g < games; g++)
            {
                var s = HeartsGame.CreateGame(Config(pointsToWin: target));
                Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.Start(), rng).IsOk);
                var guard = 0;
                while (s.Phase != HeartsPhase.GameOver && guard < 6000)
                {
                    guard++;
                    switch (s.Phase)
                    {
                        case HeartsPhase.Passing:
                        {
                            var seat = HeartsGame.SeatToAct(s);
                            Assert.True(seat is not null, "passing with nobody to act");
                            var chosen = s.Players[seat.Value].Hand.Take(3).ToList();
                            var r = HeartsGame.ApplyAction(s, seat.Value, new HeartsAction.Pass(chosen), rng);
                            Assert.True(r.IsOk, r.Reason);
                            if (s.Phase == HeartsPhase.Passing) Assert.All(s.Players, p => Assert.Equal(13, p.Hand.Count));
                            break;
                        }
                        case HeartsPhase.Play:
                        {
                            var seat = s.Turn;
                            var mine = HeartsGame.LegalPlays(s, seat).ToHashSet();
                            var theirs = OracleLegal(s, seat);
                            Assert.Equal(IdSet(theirs), IdSet(mine));
                            Assert.Equal(HeartEverPlayed(s), s.HeartsBroken);   // heartsBroken drifted from the cards
                            var all = AllCards(s);
                            Assert.Equal(52, all.Count);
                            Assert.Equal(52, all.Distinct().Count());           // a card is in two places at once
                            if (s.TricksPlayed == 0 && s.Trick.Count == 0) Assert.Equal(HeartsGame.HolderOfTwoOfClubs(s), s.Turn);
                            if (s.Trick.Count == 0 && s.TricksPlayed > 0 && s.HeartsBroken && mine.Any(c => c.Suit == Suit.Hearts)) heartsLed++;
                            if (s.TricksPlayed == 0 && s.Trick.Count > 0 && !s.Players[seat].Hand.Any(c => c.Suit == s.Trick[0].Card.Suit)) firstTrickDiscards++;

                            var legal = theirs.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
                            var pick = legal[rng.NextInt(legal.Count)];
                            var before = s.Trick.ToList();
                            var r = HeartsGame.ApplyAction(s, seat, new HeartsAction.Play(pick), rng);
                            Assert.True(r.IsOk, $"legal play refused: {pick.Id} — {r.Reason}");
                            if (before.Count == 3)
                            {
                                tricks++;
                                var plays = before.Append(new HeartsTrickPlay(seat, pick)).ToList();
                                Assert.Equal(OracleWinner(plays), s.LastTrick?.Winner);
                                Assert.Equal(plays.Sum(p => HeartsGame.PointsOf([p.Card])), s.LastTrick?.Points);
                            }
                            break;
                        }
                        case HeartsPhase.HandOver or HeartsPhase.GameOver:
                        {
                            hands++;
                            dirs.Add(s.PassDirection);
                            var raw = s.Players.Select(p => p.Taken.Sum(c => HeartsGame.PointsOf([c]))).ToList();
                            Assert.Equal(26, raw.Sum());
                            Assert.Equal(52, s.Players.Sum(p => p.Taken.Count));
                            var qs = s.Players.Where(p => p.HasQueen).ToList();
                            Assert.Single(qs);
                            queens += qs.Count;
                            Assert.NotEmpty(s.History);
                            var last = s.History[^1];
                            var shot = raw.IndexOf(26);
                            if (shot >= 0)
                            {
                                Assert.Equal(shot, last.Shooter);
                                Assert.Equal(Enumerable.Range(0, 4).Select(i => i == shot ? 0 : 26), last.Points);
                            }
                            else
                            {
                                Assert.Null(last.Shooter);
                                Assert.Equal(raw, last.Points);
                            }
                            var sums = new[] { 0, 0, 0, 0 };
                            foreach (var h in s.History) for (var i = 0; i < 4; i++) sums[i] += h.Points[i];
                            Assert.Equal(sums, s.Players.Select(p => p.Score));
                            if (s.Phase == HeartsPhase.HandOver)
                                Assert.True(HeartsGame.ApplyAction(s, 0, new HeartsAction.NextHand(), rng).IsOk);
                            break;
                        }
                        default:
                            Assert.Fail("back to idle mid-game");
                            return;
                    }
                }
                Assert.True(s.Phase == HeartsPhase.GameOver, "game did not finish");
            }
        }
        Assert.True(hands > 0);
        Assert.True(tricks > 100);
        Assert.True(queens > 0, "the queen of spades was never taken");
        Assert.True(heartsLed > 0, "a heart was never led, so the broken-hearts rule went untested");
        Assert.True(firstTrickDiscards > 0, "nobody failed to follow on the first trick");
        Assert.Equal(4, dirs.Count);   // not every passing direction came up
    }
}
