using static TheCardPlace.Spades.Tests.T;

namespace TheCardPlace.Spades.Tests;

/// <summary>
/// The rules written out as literal data and re-derived by a different method from the one the
/// engine uses, then compared after every move: the port of <c>spades/tests/rules-oracle.js</c>.
/// </summary>
public class RulesOracleTests
{
    // MARK: - The trump order and what beats what, as literal lists

    /// Spades, highest first. Every spade beats every non-spade; within any suit the earlier rank
    /// wins; across two plain suits nothing beats anything.
    private static readonly string[] SpadesHighToLow = ["AS", "KS", "QS", "JS", "TS", "9S", "8S", "7S", "6S", "5S", "4S", "3S", "2S"];
    private static readonly string[] RanksHighToLow = ["A", "K", "Q", "J", "T", "9", "8", "7", "6", "5", "4", "3", "2"];

    [Fact]
    public void Trump_order_is_the_literal_list()
    {
        var spades = SpadesHighToLow.Select(C).ToList();
        for (var i = 0; i < spades.Count; i++)
            for (var j = 0; j < spades.Count; j++)
                if (i != j)
                    Assert.True(SpadesCards.Beats(spades[i], spades[j]) == (i < j), $"{spades[i].Id} against {spades[j].Id}");
        // Every spade beats every card of every other suit, and nothing else ever beats a spade.
        foreach (var s in spades)
        {
            foreach (var other in SpadesCards.NewDeck().Where(c => c.Suit != Suit.Spades))
            {
                Assert.True(SpadesCards.Beats(s, other), $"{s.Id} should beat {other.Id}");
                Assert.False(SpadesCards.Beats(other, s), $"{other.Id} must not beat {s.Id}");
            }
        }
    }

    [Fact]
    public void Plain_suits_rank_ace_high_and_never_cross_suits()
    {
        foreach (var suit in new[] { "C", "D", "H" })
        {
            var run = RanksHighToLow.Select(r => C(r + suit)).ToList();
            for (var i = 0; i < run.Count; i++)
                for (var j = 0; j < run.Count; j++)
                    if (i != j) Assert.Equal(i < j, SpadesCards.Beats(run[i], run[j]));
        }
        // The ace of hearts does not beat the two of clubs, nor the reverse.
        Assert.False(SpadesCards.Beats(C("AH"), C("2C")));
        Assert.False(SpadesCards.Beats(C("2C"), C("AH")));
        // But the two of spades beats the ace of hearts.
        Assert.True(SpadesCards.Beats(C("2S"), C("AH")));
    }

    [Fact]
    public void Describe_and_role()
    {
        Assert.Equal("trump", SpadesCards.Role(C("2S")));
        Assert.Equal("", SpadesCards.Role(C("AH")));
        Assert.Equal("Two of Spades, trump", SpadesCards.Describe(C("2S")));
        Assert.Equal("Ace of Hearts", SpadesCards.Describe(C("AH")));
    }

    [Fact]
    public void Sort_hand_is_clubs_diamonds_hearts_spades_high_to_low()
    {
        var sorted = SpadesCards.SortHand(Cs("2S AH 3C KC 9D AS QD"));
        Assert.Equal(["KC", "3C", "QD", "9D", "AH", "AS", "2S"], sorted.Ids());
    }

    // MARK: - The oracle's own idea of the rules

    /// Legal plays by elimination rather than construction: start from the whole hand, remove
    /// what the rules forbid, put everything back if nothing survives. `broken` is re-derived from
    /// the plays watched.
    private static List<string> OracleLegal(SpadesState state, int seat, bool broken)
    {
        var hand = state.Players[seat].Hand;
        var allowed = hand.ToList();
        if (state.Trick.Count == 0)
        {
            if (!broken)
            {
                var notTrump = allowed.Where(c => c.Suit != Suit.Spades).ToList();
                if (notTrump.Count > 0) allowed = notTrump;
            }
        }
        else
        {
            var led = state.Trick[0].Card.Suit;
            if (hand.Any(c => c.Suit == led)) allowed = allowed.Where(c => c.Suit == led).ToList();
        }
        if (allowed.Count == 0) allowed = hand.ToList();
        return allowed.Ids().Order(StringComparer.Ordinal).ToList();
    }

    /// Replay the cards of a trick as two passes: highest spade if any spade is present, otherwise
    /// the highest card of the suit led.
    private static int OracleTrickWinner(List<SpadesPlay> plays)
    {
        var trumps = plays.Where(p => p.Card.Suit == Suit.Spades).ToList();
        if (trumps.Count > 0) return trumps.MaxBy(p => (int)p.Card.Rank)!.Seat;
        var led = plays[0].Card.Suit;
        return plays.Where(p => p.Card.Suit == led).MaxBy(p => (int)p.Card.Rank)!.Seat;
    }

    private sealed class Seen
    {
        public int NilBid, NilMade, NilBroken, SetHands, MadeHands;
        public int BagPenalties, Trumped, SpadeLeads, VoidDiscards;
        public int Tricks, Hands, Games, BothOver;
        public HashSet<int> Targets = [];
    }

    private readonly Seen _seen = new();

    private void PlayGames(int n, SpadesConfig config, ulong seedBase)
    {
        for (var g = 0; g < n; g++)
        {
            var (state, rng) = Drive.NewGame(config, seedBase + (ulong)g);
            var target = config.PointsToWin > 0 ? config.PointsToWin : 500;
            var bagLimit = config.BagLimit > 0 ? config.BagLimit : 10;
            var bagPenalty = config.BagPenalty >= 0 ? config.BagPenalty : 100;
            var nilValue = config.NilValue > 0 ? config.NilValue : 100;
            _seen.Targets.Add(target);

            var broken = false;
            List<int>? handBids = null;
            var guardCount = 0;

            while (state.Phase != SpadesPhase.GameOver && guardCount < 40_000)
            {
                guardCount++;
                if (state.Phase == SpadesPhase.Bidding)
                {
                    var seat = SpadesGame.SeatToAct(state) ?? throw new InvalidOperationException("bidding with nobody to act");
                    // Bidding goes round from the dealer's left; re-derived from the dealer
                    // rather than read from turn.
                    var expected = (state.Dealer!.Value + 1 + state.Players.Count(p => p.Bid is not null)) % 4;
                    Assert.True(expected == seat, "bidding out of order");

                    var bid = SpadesAI.ChooseBid(state, seat);
                    Assert.True(SpadesGame.ApplyAction(state, seat, new SpadesAction.Bid(bid), rng).IsOk);
                    var again = SpadesGame.ApplyAction(state, seat, new SpadesAction.Bid(1), rng);
                    Assert.False(again.IsOk, $"a second bid from seat {seat} was accepted");

                    if (state.Phase == SpadesPhase.Play)
                    {
                        broken = false;
                        handBids = state.Players.Select(p => p.Bid!.Value).ToList();
                        _seen.Hands++;
                        _seen.NilBid += handBids.Count(b => b == 0);
                    }
                    continue;
                }

                if (state.Phase == SpadesPhase.Play)
                {
                    var seat = SpadesGame.SeatToAct(state)!.Value;
                    Assert.Equal(state.Turn, seat);
                    CheckPlayPhase(state, broken);

                    var legal = SpadesGame.LegalPlays(state, seat);
                    Assert.True(legal.Count > 0, $"no legal play for seat {seat}");
                    var pick = SpadesAI.ChooseCard(state, seat) ?? throw new InvalidOperationException("no card chosen");
                    Assert.True(legal.Contains(pick), $"the computer chose an illegal card {pick.Id}");

                    var trickBefore = state.Trick.ToList();
                    Suit? leadSuit = trickBefore.Count > 0 ? trickBefore[0].Card.Suit : null;
                    var heldLed = leadSuit is Suit ls0 && state.Players[seat].Hand.Any(c => c.Suit == ls0);

                    if (pick.Suit == Suit.Spades) broken = true;
                    if (leadSuit is Suit ls && !heldLed)
                    {
                        _seen.VoidDiscards++;
                        if (pick.Suit == Suit.Spades && ls != Suit.Spades) _seen.Trumped++;
                    }
                    if (trickBefore.Count == 0 && pick.Suit == Suit.Spades) _seen.SpadeLeads++;

                    var before = state.TricksPlayed;
                    var r = SpadesGame.ApplyAction(state, seat, new SpadesAction.Play(pick), rng);
                    Assert.True(r.IsOk, $"play refused: {r.Reason}");
                    Assert.True(broken == state.SpadesBroken, $"spadesBroken drifted at trick {state.TricksPlayed}");

                    if (state.TricksPlayed == before + 1)
                    {
                        _seen.Tricks++;
                        var plays = trickBefore.Append(new SpadesPlay(seat, pick)).ToList();
                        Assert.True(OracleTrickWinner(plays) == state.LastTrick?.Winner,
                            $"trick winner for {string.Join(" ", plays.Select(p => p.Card.Id))}");
                    }
                    continue;
                }

                if (state.Phase == SpadesPhase.HandOver)
                {
                    if (handBids is not null)
                    {
                        CheckHandScored(state, handBids, bagLimit, bagPenalty, nilValue);
                        handBids = null;
                    }
                    Assert.True(SpadesGame.ApplyAction(state, 0, new SpadesAction.NextHand(), rng).IsOk);
                    continue;
                }
                break;
            }

            // The last hand of the game, which the loop above cannot reach.
            if (handBids is not null) CheckHandScored(state, handBids, bagLimit, bagPenalty, nilValue);

            Assert.True(state.Phase == SpadesPhase.GameOver, "game did not finish");
            _seen.Games++;
            Assert.True(state.Scores[0] >= target || state.Scores[1] >= target);
            var hi = state.Scores[0] >= state.Scores[1] ? 0 : 1;
            Assert.True(hi == state.Winner, "the winner is not the higher score");
            Assert.True(state.Scores[state.Winner!.Value] >= target);
            // Every hand but the last leaves both sides under the line, except a level tie which
            // plays on.
            foreach (var h in state.History.Take(state.History.Count - 1))
            {
                var over = h.Scores.Count(s => s >= target);
                if (over == 2 && h.Scores[0] == h.Scores[1])
                {
                    _seen.BothOver++;
                    continue;
                }
                Assert.True(over == 0, $"hand {h.Deal} passed the target and the game carried on");
            }
        }
    }

    private static void CheckPlayPhase(SpadesState state, bool broken)
    {
        var seat = state.Turn;
        var mine = SpadesGame.LegalPlays(state, seat).Ids().Order(StringComparer.Ordinal).ToList();
        var theirs = OracleLegal(state, seat, broken);
        Assert.Equal(theirs, mine);

        // Hands are the same size, or one apart mid-trick.
        var sizes = state.Players.Select(p => p.Hand.Count).ToList();
        Assert.True(sizes.Max() - sizes.Min() <= 1, $"hand sizes drifted: {string.Join(",", sizes)}");

        // No card exists twice, anywhere.
        var all = new HashSet<Card>();
        var dupes = 0;
        foreach (var p in state.Players)
            foreach (var c in p.Hand)
                if (!all.Add(c)) dupes++;
        foreach (var t in state.Trick)
            if (!all.Add(t.Card)) dupes++;
        Assert.True(dupes == 0, "the same card is in play twice");
    }

    /// Re-derive the score by a method that shares nothing with ScoreHand.
    private void CheckHandScored(SpadesState state, List<int> bids, int bagLimit, int bagPenalty, int nilValue)
    {
        var h = state.History.LastOrDefault() ?? throw new InvalidOperationException("no history row");
        Assert.Equal(13, h.Tricks.Sum());
        Assert.Equal(bids, h.Bids);

        for (var team = 0; team < 2; team++)
        {
            int[] seats = [team, team + 2];
            var contract = seats.Sum(s => bids[s]);
            var took = seats.Sum(s => h.Tricks[s]);
            if (took >= contract) _seen.MadeHands++; else _seen.SetHands++;

            int want = 0, over = 0;
            if (took >= contract)
            {
                want += 10 * contract;
                over = took - contract;
                want += over;
            }
            else
            {
                want -= 10 * contract;
            }
            foreach (var s in seats.Where(s => bids[s] == 0))
            {
                var made = h.Tricks[s] == 0;
                want += made ? nilValue : -nilValue;
                if (made) _seen.NilMade++; else _seen.NilBroken++;
            }
            var prev = state.History.Count > 1 ? state.History[^2].Bags[team] : 0;
            var bags = prev + over;
            var penalties = 0;
            while (bagLimit > 0 && bags >= bagLimit)
            {
                bags -= bagLimit;
                penalties += bagPenalty;
            }
            want -= penalties;
            if (penalties > 0) _seen.BagPenalties++;

            Assert.True(want == h.Delta[team], $"hand {h.Deal} team {team}: bids {string.Join(",", bids)} tricks {string.Join(",", h.Tricks)}");
            Assert.True(bags == h.Bags[team], $"hand {h.Deal} team {team} bags");
        }
        for (var team = 0; team < 2; team++)
        {
            var total = state.History.Sum(r => r.Delta[team]);
            Assert.True(total == h.Scores[team], "the running score drifted from the sum of the hands");
        }
    }

    [Fact]
    public void Engine_agrees_with_the_oracle_across_many_games()
    {
        PlayGames(30, Config(), 20260825);
        PlayGames(10, Config(pointsToWin: 250), 991);
        PlayGames(6, Config(pointsToWin: 250, bagLimit: 5, bagPenalty: 50, nilValue: 50), 4242);
        PlayGames(4, Config(pointsToWin: 250, bagPenalty: 0), 777);

        // A run that never met the interesting cases has not tested them.
        Assert.True(_seen.NilMade + _seen.NilBroken == _seen.NilBid, "some hands were never scored");
        Assert.True(_seen.Hands > 0);
        Assert.True(_seen.SetHands > 0, "no contract was ever set");
        Assert.True(_seen.MadeHands > 0, "no contract was ever made");
        Assert.True(_seen.NilBid > 0, "nobody ever bid nil");
        Assert.True(_seen.NilMade > 0, "no nil ever came in");
        Assert.True(_seen.NilBroken > 0, "no nil was ever broken");
        Assert.True(_seen.BagPenalties > 0, "the bag bin never filled");
        Assert.True(_seen.Trumped > 0, "nobody ever ruffed");
        Assert.True(_seen.SpadeLeads > 0, "a spade was never led");
        Assert.True(_seen.Targets.Count >= 2, "every game ran to the same target");
    }

    // MARK: - The forced spade lead

    [Fact]
    public void A_hand_of_nothing_but_spades_may_lead_one_and_that_breaks_them()
    {
        var (state, rng) = Drive.NewGame(5);
        while (state.Phase == SpadesPhase.Bidding)
            Assert.True(SpadesGame.ApplyAction(state, state.Turn, new SpadesAction.Bid(2), rng).IsOk);
        var lead = state.Turn;
        var rest = Enumerable.Range(0, 4).Where(s => s != lead).ToList();
        state.Players[lead].Hand = Cs("2S 3S");
        state.Players[rest[0]].Hand = Cs("AS 4S KH");
        state.Players[rest[1]].Hand = Cs("QS QH");
        state.Players[rest[2]].Hand = Cs("JS JH");

        Assert.Equal(["2S", "3S"], SpadesGame.LegalPlays(state, lead).Ids().Order(StringComparer.Ordinal));
        Assert.True(SpadesGame.ApplyAction(state, lead, new SpadesAction.Play(C("2S")), rng).IsOk);
        Assert.True(state.SpadesBroken, "a spade was led and spades are still not broken");

        for (var i = 0; i < 3; i++)
        {
            var seat = state.Turn;
            var sp = state.Players[seat].Hand.First(c => c.Suit == Suit.Spades);
            Assert.True(SpadesGame.ApplyAction(state, seat, new SpadesAction.Play(sp), rng).IsOk);
        }
        // The winner, holding a spade and a heart, may now lead either.
        var winner = state.Turn;
        Assert.Equal(state.Players[winner].Hand.Count, SpadesGame.LegalPlays(state, winner).Count);
    }

    // MARK: - CanDeal and SeatToAct through every phase

    [Fact]
    public void Can_deal_is_exactly_what_apply_action_accepts()
    {
        var (state, rng) = Drive.NewGame(Config(pointsToWin: 100), 11);
        var phases = new HashSet<SpadesPhase>();
        void Probe(SpadesState s)
        {
            phases.Add(s.Phase);
            var copy = Snapshot.Clone(s);
            var did = SpadesGame.ApplyAction(copy, 0, new SpadesAction.NextHand(), new RandomSource(1)).IsOk;
            Assert.True(SpadesGame.CanDeal(s) == did, $"CanDeal disagrees with ApplyAction at {s.Phase}");
        }
        Probe(SpadesGame.CreateGame(new SpadesConfig()));
        var guardCount = 0;
        while (state.Phase != SpadesPhase.GameOver && guardCount < 40_000)
        {
            guardCount++;
            Probe(state);
            Drive.Step(state, rng);
        }
        Probe(state);
        Assert.Equal(
            new HashSet<SpadesPhase> { SpadesPhase.Idle, SpadesPhase.Bidding, SpadesPhase.Play, SpadesPhase.HandOver, SpadesPhase.GameOver },
            phases);
    }

    [Fact]
    public void Seat_to_act_is_null_in_every_dead_phase()
    {
        Assert.Null(SpadesGame.SeatToAct(SpadesGame.CreateGame(new SpadesConfig())));
        var (state, rng) = Drive.NewGame(Config(pointsToWin: 100), 12);
        var guardCount = 0;
        while (state.Phase != SpadesPhase.GameOver && guardCount < 40_000)
        {
            guardCount++;
            var s = SpadesGame.SeatToAct(state);
            if (state.Phase is SpadesPhase.Bidding or SpadesPhase.Play)
            {
                Assert.NotNull(s);
                Assert.Equal(state.Turn, s);
            }
            else
            {
                Assert.Null(s);
            }
            Drive.Step(state, rng);
        }
        Assert.Null(SpadesGame.SeatToAct(state));
    }

    // MARK: - Event wording

    [Fact]
    public void Deal_and_bid_events()
    {
        var rng = new RandomSource(3);
        var state = SpadesGame.CreateGame(Config());
        Assert.True(SpadesGame.ApplyAction(state, 0, new SpadesAction.Start(), rng).IsOk);
        var dealer = state.Dealer!.Value;
        var first = (dealer + 1) % 4;
        Assert.Equal($"Hand 1 dealt. {TestNames[dealer]} dealt; {TestNames[first]} bids first.", state.Log.Events[^1].Text);
        Assert.Equal(EventKind.Deal, state.Log.Events[^1].Kind);

        Assert.True(SpadesGame.ApplyAction(state, first, SpadesAction.NilBid, rng).IsOk);
        Assert.Equal($"{TestNames[first]} bid nil.", state.Log.Events[^1].Text);
        Assert.Equal(EventKind.Bid, state.Log.Events[^1].Kind);
        Assert.Equal(0, state.Players[first].Bid);

        Assert.True(SpadesGame.ApplyAction(state, (first + 1) % 4, new SpadesAction.Bid(4), rng).IsOk);
        Assert.Equal($"{TestNames[(first + 1) % 4]} bid 4.", state.Log.Events[^1].Text);
    }

    [Fact]
    public void Bidding_done_event_says_the_table_shape()
    {
        var rng = new RandomSource(9);
        var state = SpadesGame.CreateGame(Config());
        SpadesGame.ApplyAction(state, 0, new SpadesAction.Start(), rng);
        var dealer = state.Dealer!.Value;
        var order = Enumerable.Range(1, 4).Select(i => (dealer + i) % 4).ToList();
        int[] bids = [3, 4, 3, 5];   // in bidding order
        for (var i = 0; i < order.Count; i++)
            Assert.True(SpadesGame.ApplyAction(state, order[i], new SpadesAction.Bid(bids[i]), rng).IsOk);
        Assert.Equal(SpadesPhase.Play, state.Phase);
        int t0 = SpadesGame.ContractOf(state, 0), t1 = SpadesGame.ContractOf(state, 1);
        Assert.Equal(15, t0 + t1);
        Assert.Equal(
            $"Bidding is done. North and South for {t0}, East and West for {t1}. That is 2 over the 13 available. {TestNames[order[0]]} leads.",
            state.Log.Events[^1].Text);
        Assert.Equal(order[0], state.Leader);
    }

    private static void PlayOut(SpadesState state, params string[] ids)
    {
        var rng = new RandomSource(1);
        for (var seat = 0; seat < ids.Length; seat++)
        {
            var r = SpadesGame.ApplyAction(state, seat, new SpadesAction.Play(C(ids[seat])), rng);
            Assert.True(r.IsOk, r.Reason);
        }
    }

    [Fact]
    public void Play_trick_and_score_events_at_the_end_of_a_hand()
    {
        // Bids 3,4,2,4; tricks before the last one 3,4,2,3; West takes the last trick with the ace
        // of clubs, so both sides make exactly.
        var state = Position([3, 4, 2, 4], [3, 4, 2, 3], ["2C", "3C", "4C", "AC"], 0, 12, config: Config());
        PlayOut(state, "2C", "3C", "4C", "AC");
        var texts = state.Log.Events.Select(e => e.Text).ToList();
        Assert.Contains("North played the Two of Clubs.", texts);
        Assert.Contains("West took the trick with the Ace of Clubs.", texts);
        Assert.Contains("North and South bid 5, took 5 — made it, +50.", texts);
        Assert.Contains("East and West bid 8, took 8 — made it, +80.", texts);
        Assert.Contains("Hand 1 over. North and South 50, East and West 80.", texts);
        Assert.Equal(SpadesPhase.HandOver, state.Phase);
        Assert.Equal(new[] { 50, 80 }, state.Scores);
        Assert.Single(state.History);
        var play = state.Log.Events.First(e => e.Kind == EventKind.Play);
        Assert.Equal(new[] { C("2C") }, play.Cards);
        Assert.Equal(0, play.Seat);
    }

    [Fact]
    public void Nil_bag_and_game_over_events()
    {
        // South is on nil and takes the last trick: the nil goes down, said the moment it is
        // certain and again at the scoring. North and South are set (bid 5, took 3+1=4). East and
        // West, bid 4, took 9: five overtricks on eight bags fills the bin.
        var state = Position([5, 2, 0, 2], [3, 4, 0, 5], ["2H", "3H", "AH", "4H"], 0, 12,
                             scores: [400, 480], bags: [0, 8], config: Config());
        PlayOut(state, "2H", "3H", "AH", "4H");
        var texts = state.Log.Events.Select(e => e.Text).ToList();
        Assert.Contains("South bid nil and has taken a trick.", texts);
        Assert.Contains("North and South bid 5, took 4 — set, -150.", texts);
        Assert.Contains("South’s nil went down on 1 trick.", texts);
        Assert.Contains("East and West bid 4, took 9 — made it, -55.", texts);
        Assert.Contains("East and West filled the bag bin — 100 off.", texts);
        Assert.Contains("Hand 1 over. North and South 250, East and West 425.", texts);
        Assert.Equal(new[] { 0, 3 }, state.Bags);
        Assert.Equal(SpadesPhase.HandOver, state.Phase);
    }

    [Fact]
    public void Game_over_event_and_winner()
    {
        var state = Position([3, 4, 2, 4], [3, 4, 2, 3], ["2C", "3C", "4C", "AC"], 0, 12,
                             scores: [460, 300], config: Config());
        PlayOut(state, "2C", "3C", "4C", "AC");
        Assert.Equal(SpadesPhase.GameOver, state.Phase);
        Assert.Equal(0, state.Winner);
        Assert.Equal("North and South win, 510 to 380.", state.Log.Events[^1].Text);
        Assert.Equal(EventKind.Game, state.Log.Events[^1].Kind);
        Assert.Null(SpadesGame.SeatToAct(state));
    }

    [Fact]
    public void Both_over_and_level_plays_another_hand()
    {
        var state = Position([3, 4, 2, 4], [3, 4, 2, 3], ["2C", "3C", "4C", "AC"], 0, 12,
                             scores: [480, 450], config: Config());
        PlayOut(state, "2C", "3C", "4C", "AC");
        Assert.Equal(new[] { 530, 530 }, state.Scores);
        Assert.Equal(SpadesPhase.HandOver, state.Phase);
        Assert.Null(state.Winner);
        Assert.Equal("Both partnerships passed 500 and are level. Another hand decides it.", state.Log.Events[^1].Text);
        Assert.True(SpadesGame.CanDeal(state));
    }

    [Fact]
    public void Spades_broken_event_comes_before_the_play()
    {
        var state = Position([3, 3, 3, 3], [0, 0, 0, 0], ["2C 3D", "AS 4D", "5C 6C", "7C 8C"], 0, 0, config: Config());
        var rng = new RandomSource(1);
        Assert.True(SpadesGame.ApplyAction(state, 0, new SpadesAction.Play(C("2C")), rng).IsOk);
        Assert.True(SpadesGame.ApplyAction(state, 1, new SpadesAction.Play(C("AS")), rng).IsOk);
        var texts = state.Log.Events.TakeLast(2).Select(e => e.Text);
        Assert.Equal(["Spades are broken.", "East played the Ace of Spades."], texts);
        Assert.True(state.SpadesBroken);
    }

    [Fact]
    public void New_game_keeps_names_and_rules_and_goes_back_to_idle()
    {
        var config = Config(pointsToWin: 250, bagLimit: 5);
        var state = Drive.PlayGame(config, 21);
        Assert.Equal(SpadesPhase.GameOver, state.Phase);
        var rng = new RandomSource(1);
        Assert.True(SpadesGame.ApplyAction(state, 0, new SpadesAction.NewGame(), rng).IsOk);
        Assert.Equal(SpadesPhase.Idle, state.Phase);
        Assert.Equal(Json(config), Json(state.Config));
        Assert.Equal(TestNames, state.Players.Select(p => p.Name));
        Assert.Equal(new[] { 0, 0 }, state.Scores);
        Assert.Empty(state.History);
        Assert.Null(state.Dealer);
        Assert.True(SpadesGame.ApplyAction(state, 0, new SpadesAction.Start(), rng).IsOk);
        Assert.Equal(1, state.DealNumber);
    }

    [Fact]
    public void Rules_of_the_table_fall_back_from_nonsense()
    {
        var s = SpadesGame.CreateGame(new SpadesConfig { PointsToWin = 0, BagLimit = -1, BagPenalty = -5, NilValue = 0 });
        Assert.Equal(500, SpadesGame.Target(s));
        Assert.Equal(10, SpadesGame.BagLimit(s));
        Assert.Equal(100, SpadesGame.BagPenalty(s));
        Assert.Equal(100, SpadesGame.NilValue(s));
        var zero = SpadesGame.CreateGame(new SpadesConfig { BagPenalty = 0 });
        Assert.True(SpadesGame.BagPenalty(zero) == 0, "a zero bag penalty is a real choice");
    }

    [Fact]
    public void Help_text_is_present_and_plain()
    {
        Assert.Equal(["The object", "The bidding", "The play", "The scoring", "Nil"], SpadesHelp.Rules.Select(r => r.Heading));
        foreach (var r in SpadesHelp.Rules)
        {
            Assert.False(r.Body.Contains('<'), "HTML in the help text");
            Assert.False(r.Body.Contains("press ", StringComparison.OrdinalIgnoreCase), "a key reference in the help text");
        }
    }
}
