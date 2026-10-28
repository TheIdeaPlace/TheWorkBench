using static TheCardPlace.Sheephead.Tests.Support;

namespace TheCardPlace.Sheephead.Tests;

/// <summary>
/// The rules written out as literal data, from the game's own How to Play, and the engine
/// measured against them. Nothing here asks IsTrump, Power, Beats, EffectiveSuit, Points or
/// LegalPlays what to expect: those are the things on trial.
/// </summary>
public class RulesOracleTests
{
    private static readonly string[] Suits = ["C", "S", "H", "D"];
    private static readonly string[] RankIds = ["7", "8", "9", "T", "J", "Q", "K", "A"];
    private static readonly List<string> Deck = Suits.SelectMany(s => RankIds.Select(r => r + s)).ToList();
    private static readonly Dictionary<char, int> PointTable = new()
    {
        ['A'] = 11, ['T'] = 10, ['K'] = 4, ['Q'] = 3, ['J'] = 2, ['9'] = 0, ['8'] = 0, ['7'] = 0,
    };

    /// <summary>Highest first. Typed out by hand from the sentence in How to Play.</summary>
    private static readonly string[] Trump =
    [
        "QC", "QS", "QH", "QD",
        "JC", "JS", "JH", "JD",
        "AD", "TD", "KD", "9D", "8D", "7D",
    ];

    private static readonly char[] FailRanks = ['A', 'T', 'K', '9', '8', '7'];

    private static bool IsTrump(string id) => Trump.Contains(id);
    private static int Pts(string id) => PointTable[id[0]];
    private static string SuitLed(string id) => IsTrump(id) ? "TRUMP" : id[^1..];
    private static int RankOf(string id) => IsTrump(id) ? Array.IndexOf(Trump, id) : Array.IndexOf(FailRanks, id[0]);

    /// <summary>"Highest trump takes the trick; if no trump is played, the highest card of the led suit takes it."</summary>
    private static int TrickWinner(List<string> ids)
    {
        var led = SuitLed(ids[0]);
        var trumps = ids.Where(IsTrump).ToList();
        var pool = trumps.Count == 0 ? ids.Where(i => SuitLed(i) == led).ToList() : trumps;
        var best = pool[0];
        foreach (var id in pool)
            if (RankOf(id) < RankOf(best)) best = id;
        return ids.IndexOf(best);
    }

    /// <summary>"You must follow the suit that was led if you can."</summary>
    private static List<string> Legal(List<string> hand, string? led)
    {
        if (led is null) return hand;
        var following = hand.Where(h => SuitLed(h) == SuitLed(led)).ToList();
        return following.Count == 0 ? hand : following;
    }

    /// <summary>The scoring thresholds from How to Play.</summary>
    private static (bool Win, int Mult) Score(int pickerPts, int pickerTricks, int totalTricks)
    {
        if (pickerPts >= 61)
        {
            if (pickerTricks == totalTricks) return (true, 3);
            if (pickerPts >= 91) return (true, 2);
            return (true, 1);
        }
        if (pickerTricks == 0) return (false, 4);
        if (pickerPts <= 30) return (false, 3);
        return (false, 2);
    }

    [Fact]
    public void Deck_sizes_and_points()
    {
        var three = SheepheadCards.Deck(3).Ids();
        Assert.Equal(32, three.Count);
        Assert.True(new HashSet<string>(three).SetEquals(Deck));
        var four = SheepheadCards.Deck(4).Ids();
        Assert.Equal(30, four.Count);
        Assert.DoesNotContain("7D", four);
        Assert.DoesNotContain("8D", four);
        for (var n = 3; n <= 6; n++)
            Assert.Equal(120, SheepheadCards.Deck(n).Sum(c => Pts(c.Id)));
    }

    [Fact]
    public void Card_values()
    {
        foreach (var id in Deck)
            Assert.Equal(Pts(id), SheepheadCards.Points(Cd(id)));
    }

    [Fact]
    public void Trump_order()
    {
        Assert.Equal(Trump, SheepheadCards.TrumpOrder.Ids());
        Assert.Equal(14, Trump.Length);
        foreach (var id in Deck)
            Assert.Equal(IsTrump(id), SheepheadCards.IsTrump(Cd(id)));
        Assert.NotEqual(SheepheadCards.EffectiveSuit(Cd("QC")), SheepheadCards.EffectiveSuit(Cd("AC")));
        Assert.Equal(SheepheadCards.EffectiveSuit(Cd("QC")), SheepheadCards.EffectiveSuit(Cd("7D")));
    }

    [Fact]
    public void Beats_every_ordered_pair()
    {
        var pairs = 0;
        foreach (var a in Deck)
        {
            foreach (var b in Deck)
            {
                if (a == b) continue;
                var expected = IsTrump(a)
                    ? !IsTrump(b) || RankOf(a) < RankOf(b)
                    : !IsTrump(b) && a[^1] == b[^1] && RankOf(a) < RankOf(b);
                Assert.True(expected == SheepheadCards.Beats(Cd(a), Cd(b)), $"{a} over {b}");
                pairs++;
            }
        }
        Assert.Equal(32 * 31, pairs);
        Assert.True(SheepheadCards.Beats(Cd("7D"), Cd("AC")));
        Assert.False(SheepheadCards.Beats(Cd("AC"), Cd("7D")));
        Assert.True(SheepheadCards.Beats(Cd("QC"), Cd("QS")));
        Assert.True(SheepheadCards.Beats(Cd("TC"), Cd("KC")), "the ten outranks the king");
        Assert.False(SheepheadCards.Beats(Cd("AS"), Cd("KC")), "a spade cannot beat a winning club");
        Assert.True(SheepheadCards.Beats(Cd("JD"), Cd("AD")));
    }

    private static List<SheepheadPlay> Plays(IEnumerable<string> ids) =>
        ids.Select((id, i) => new SheepheadPlay(i, Cd(id))).ToList();

    [Theory]
    [InlineData("AC TC KC", 0, "ace is the highest club")]
    [InlineData("KC TC AC", 2, "ace still wins from third seat")]
    [InlineData("AC TC 7D", 2, "the lowest trump beats every fail card")]
    [InlineData("AC QC KC", 1, "the queen of clubs is trump, not a club")]
    [InlineData("AC AS AH", 0, "off-suit aces cannot win")]
    [InlineData("QD QC", 1, "queen of clubs is the highest card in the deck")]
    [InlineData("7D AD JD", 2, "jacks outrank the ace of diamonds")]
    [InlineData("JD QD", 1, "every queen outranks every jack")]
    [InlineData("TS KS 9S", 0, "the ten is the top card left in a fail suit")]
    [InlineData("9H 8H 7H", 0, "nine is the highest of three worthless hearts")]
    [InlineData("AH 7C 8S", 0, "nobody followed hearts, so the lead holds")]
    public void Trick_winner_cases(string ids, int win, string why)
    {
        var plays = Plays(ids.Split(' '));
        Assert.True(win == SheepheadGame.TrickWinnerIndex(plays), why);
    }

    [Fact]
    public void Trick_winner_matches_the_oracle_on_random_tricks()
    {
        var rng = new RandomSource(20260811);
        for (var t = 0; t < 5000; t++)
        {
            var size = 3 + rng.NextInt(4);
            var ids = Deck.Shuffled(rng).Take(size).ToList();
            Assert.True(TrickWinner(ids) == SheepheadGame.TrickWinnerIndex(Plays(ids)), string.Join(" ", ids));
        }
    }

    private static List<string> LegalFrom(List<string> hand, string? led)
    {
        var (state, _) = FiveHanded();
        state.Phase = SheepheadPhase.Play;
        state.Players[0].Hand = hand.Select(Cd).ToList();
        state.Trick = led is null ? [] : [new SheepheadPlay(1, Cd(led))];
        state.Turn = 0;
        return SheepheadGame.LegalPlays(state, 0).Ids().Order(StringComparer.Ordinal).ToList();
    }

    [Theory]
    [InlineData("AC KC AS", "TC", "AC KC", "must follow clubs")]
    [InlineData("AC KC AS", "QH", "", "no trump in hand, so anything goes")]
    [InlineData("QC JS 9D AC", "TD", "QC JS 9D", "a diamond lead is a trump lead")]
    [InlineData("QC AC KC", "AS", "", "no spades, and the queen of clubs is not a club")]
    [InlineData("QC AC KC", "TC", "AC KC", "the queen of clubs does not follow clubs")]
    [InlineData("JD", "AC", "", "the only card is always playable")]
    [InlineData("AH 7H QD", "9H", "AH 7H", "must follow hearts")]
    public void Legal_plays(string hand, string led, string want, string why)
    {
        var h = hand.Split(' ').ToList();
        var w = want.Length == 0 ? h : want.Split(' ').ToList();
        Assert.True(w.Order(StringComparer.Ordinal).SequenceEqual(LegalFrom(h, led)), why);
    }

    [Fact]
    public void Legal_plays_match_the_oracle_on_random_hands()
    {
        var rng = new RandomSource(77777);
        for (var t = 0; t < 1000; t++)
        {
            var pool = Deck.Shuffled(rng);
            var size = 1 + rng.NextInt(7);
            var hand = pool.Take(size).ToList();
            var led = pool[size];
            Assert.Equal(Legal(hand, led).Order(StringComparer.Ordinal), LegalFrom(hand, led));
        }
    }

    [Fact]
    public void Partner_card_and_deal()
    {
        Assert.Equal("JD", SheepheadGame.PartnerCard.Id);
        Assert.False(SheepheadGame.DealSpec(3).Partner);
        for (var n = 4; n <= 6; n++) Assert.True(SheepheadGame.DealSpec(n).Partner);
        var hands = new Dictionary<int, int> { [3] = 10, [4] = 7, [5] = 6, [6] = 5 };
        for (var n = 3; n <= 6; n++)
        {
            var d = SheepheadGame.DealSpec(n);
            Assert.Equal(hands[n], d.Hand);
            Assert.Equal(2, d.Blind);
            Assert.Equal(SheepheadCards.Deck(n).Count, d.Hand * n + d.Blind);
        }
    }

    /// <summary>
    /// The payout table as literal rows: picker points, picker tricks, then the outcome How to
    /// Play promises. Five players, a partner, no doublers: each of three opponents settles two
    /// units times the multiplier, the partner gets a third of the pot, the picker the rest.
    /// </summary>
    [Theory]
    [InlineData(61, 3, true, 1, 4, 2, -2)]
    [InlineData(60, 3, false, 2, -8, -4, 4)]
    [InlineData(91, 5, true, 2, 8, 4, -4)]
    [InlineData(90, 5, true, 1, 4, 2, -2)]
    [InlineData(120, 6, true, 3, 12, 6, -6)]
    [InlineData(100, 6, true, 3, 12, 6, -6)]
    [InlineData(30, 1, false, 3, -12, -6, 6)]
    [InlineData(31, 1, false, 2, -8, -4, 4)]
    [InlineData(0, 0, false, 4, -16, -8, 8)]
    [InlineData(20, 0, false, 4, -16, -8, 8)]
    public void Scoring_table(int pts, int tricks, bool win, int mult, int picker, int partner, int opp)
    {
        var (state, _) = FiveHanded();
        state.Phase = SheepheadPhase.Play;
        state.Picker = 1;
        state.Partner = 2;
        state.Alone = false;
        state.Buried = [];
        foreach (var p in state.Players) { p.Points = 0; p.TricksWon = 0; }
        state.Players[1].Points = pts;
        state.Players[1].TricksWon = tricks;
        state.Players[0].Points = 120 - pts;
        state.Players[0].TricksWon = 6 - tricks;
        var r = SheepheadGame.ScoreNormal(state);
        var want = Score(pts, tricks, 6);
        Assert.Equal(want.Win, r.PickerWins);
        Assert.Equal(want.Mult, r.Multiplier);
        Assert.Equal(win, r.PickerWins);
        Assert.Equal(mult, r.Multiplier);
        Assert.Equal([opp, picker, partner, opp, opp], r.Deltas);
        Assert.Equal(0, r.Deltas.Sum());
    }

    [Fact]
    public void Scoring_alone_takes_the_whole_pot()
    {
        // Alone: the picker takes the whole pot from four opponents.
        var (state, _) = FiveHanded();
        state.Phase = SheepheadPhase.Play;
        state.Picker = 1;
        state.Partner = null;
        state.Alone = true;
        state.Players[1].Points = 70;
        state.Players[1].TricksWon = 4;
        state.Players[0].Points = 50;
        state.Players[0].TricksWon = 2;
        var r = SheepheadGame.ScoreNormal(state);
        Assert.Equal([-2, 8, -2, -2, -2], r.Deltas);
    }

    [Fact]
    public void Scoring_rederived_over_play()
    {
        int scored = 0, wins = 0, losses = 0, schneiders = 0;
        for (var n = 3; n <= 6; n++)
        {
            for (var i = 0; i < 120; i++)
            {
                var (state, rng) = NewGame(n, (ulong)(1000 * n + i));
                PlayHand(state, rng);
                if (state.IsLeaster) continue;
                var r = state.Result!;
                var team = new List<int> { state.Picker!.Value };
                if (!state.Alone && state.Partner is { } partner) team.Add(partner);
                var pts = state.Buried.Sum(c => Pts(c.Id));
                var tricks = 0;
                foreach (var s in team) { pts += state.Players[s].Points; tricks += state.Players[s].TricksWon; }
                var want = Score(pts, tricks, SheepheadGame.DealSpec(n).Hand);
                scored++;
                if (want.Win) { wins++; if (want.Mult == 2) schneiders++; }
                else losses++;
                Assert.True(want.Win == r.PickerWins, $"{n}p: picker had {pts} in {tricks} tricks");
                Assert.Equal(want.Mult, r.Multiplier);
                Assert.Equal(pts, r.PickerPoints);
                var stake = 2 * want.Mult * r.Factor;
                for (var seat = 0; seat < n; seat++)
                    if (!team.Contains(seat)) Assert.Equal(stake, Math.Abs(r.Deltas[seat]));
                Assert.Equal(want.Win, r.Deltas[state.Picker.Value] > 0);
            }
        }
        Assert.True(scored > 300);
        Assert.True(wins > 0);
        Assert.True(losses > 0, "the picker never lost, so the losing multipliers went untested");
        Assert.True(schneiders > 0, "never saw a schneider");
    }
}
