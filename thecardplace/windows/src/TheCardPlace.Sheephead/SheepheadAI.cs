using C = TheCardPlace.Sheephead.SheepheadCards;

namespace TheCardPlace.Sheephead;

/// <summary>
/// The computer players: a port of <c>sheephead-multiplayer/js/ai.js</c>.
///
/// The AI only ever looks at information a person in that seat would have: its own hand, the
/// cards already played, the public identity of the picker, and (if it is the picker) its own
/// buried cards. It never reads other hands, the blind, or the hidden <c>Alone</c> and
/// <c>Partner</c> fields — what it knows about the sides it works out from the jack of diamonds in
/// its own hand, or from the reveal everyone saw.
/// </summary>
public static class SheepheadAI
{
    // MARK: Driver

    public static SheepheadAction? Decide(SheepheadState state, int seat, RandomSource rng)
    {
        if (SheepheadGame.SeatToAct(state) != seat) return null;
        return state.Phase switch
        {
            SheepheadPhase.Pick => ShouldPick(state, seat, rng) ? new SheepheadAction.Pick() : new SheepheadAction.Pass(),
            SheepheadPhase.Bury => new SheepheadAction.Bury(ChooseBury(state, seat)),
            SheepheadPhase.Play => new SheepheadAction.Play(ChooseCard(state, seat, rng)),
            _ => null,
        };
    }

    // MARK: Shared helpers

    /// <summary>
    /// Cards this seat has not seen: the whole deck minus own hand, minus everything played so
    /// far, minus own buried cards. For anyone but the picker the blind is correctly still "unseen".
    /// </summary>
    public static List<Card> Unseen(SheepheadState state, int seat)
    {
        var seen = new HashSet<Card>(state.Players[seat].Hand);
        seen.UnionWith(state.Played);
        if (seat == state.Picker) seen.UnionWith(state.Buried);
        return C.Deck(state.Seats).Where(c => !seen.Contains(c)).ToList();
    }

    /// <summary>True when nothing still out there can beat this card if it were leading.</summary>
    public static bool IsBoss(Card card, IEnumerable<Card> unseen) => !unseen.Any(u => C.Beats(u, card));

    /// <summary>
    /// The first element, in the list's own order, that is smallest under the ordering — what a
    /// stable sort followed by [0] gives in the original.
    /// </summary>
    private static Card? FirstMin(IEnumerable<Card> cards, Func<Card, Card, bool> less)
    {
        Card? best = null;
        foreach (var c in cards)
        {
            if (best is { } b && !less(c, b)) continue;
            best = c;
        }
        return best;
    }

    private static Card LowestByPoints(IEnumerable<Card> cards) =>
        FirstMin(cards, (a, b) =>
        {
            var d = C.Points(a) - C.Points(b);
            return d != 0 ? d < 0 : C.Power(a) < C.Power(b);
        })!.Value;

    private static Card HighestByPoints(IEnumerable<Card> cards) =>
        FirstMin(cards, (a, b) =>
        {
            var d = C.Points(b) - C.Points(a);
            return d != 0 ? d < 0 : C.Power(b) < C.Power(a);
        })!.Value;

    /// <summary>Prefer winning with a fail card; among equals use the weakest card that still wins.</summary>
    private static Card CheapestWinner(IEnumerable<Card> cards) =>
        FirstMin(cards, (a, b) =>
        {
            int ta = C.IsTrump(a) ? 1 : 0, tb = C.IsTrump(b) ? 1 : 0;
            if (ta != tb) return ta < tb;
            return C.Power(a) < C.Power(b);
        })!.Value;

    private static Card HighestPower(IEnumerable<Card> cards) => FirstMin(cards, (a, b) => C.Power(a) > C.Power(b))!.Value;

    private static List<Card> BySuit(IEnumerable<Card> hand, Suit suit) => hand.Where(c => !C.IsTrump(c) && c.Suit == suit).ToList();

    private static List<Card> TrumpOf(IEnumerable<Card> hand) => hand.Where(C.IsTrump).ToList();

    private static List<Card> FailOf(IEnumerable<Card> hand) => hand.Where(c => !C.IsTrump(c)).ToList();

    private static int TrickPoints(IEnumerable<SheepheadPlay> trick) => C.SumPoints(trick.Select(t => t.Card));

    private static SheepheadPlay CurrentBest(List<SheepheadPlay> trick) => trick[SheepheadGame.TrickWinnerIndex(trick)];

    // MARK: What a seat may know about the sides

    /// <summary>
    /// Which side this seat is on, from its own cards and public information only. The picker
    /// knows they picked; whoever holds the jack of diamonds knows they are the partner; after the
    /// reveal everybody knows.
    /// </summary>
    public static SheepheadTeam Side(SheepheadState state, int seat)
    {
        if (state.IsLeaster) return SheepheadTeam.Solo;
        if (seat == state.Picker) return SheepheadTeam.Picker;
        if (state.PartnerRevealed) return SheepheadGame.Team(state, seat);
        if (state.Spec.Partner && state.Players[seat].Hand.Contains(C.PartnerCard)) return SheepheadTeam.Picker;
        return SheepheadTeam.Opponent;
    }

    /// <summary>
    /// How likely <paramref name="other"/> is an ally, judged only from what
    /// <paramref name="viewer"/> is entitled to know. Deliberately does not consult the hidden
    /// <c>Alone</c> and <c>Partner</c> fields until the sides have been revealed; the picker reads
    /// their own hand and bury instead, and the partner their own hand.
    /// </summary>
    public static double AllyProbability(SheepheadState state, int viewer, int other)
    {
        if (viewer == other) return 1;
        if (state.IsLeaster) return 0;
        if (state.Picker is not { } picker) return 0;

        var n = state.Seats;
        var d = state.Spec;

        // Public knowledge: the sides are out, or this table has no partner card.
        if (state.PartnerRevealed || !d.Partner)
            return SheepheadGame.Team(state, viewer) == SheepheadGame.Team(state, other) ? 1 : 0;

        if (viewer == picker)
        {
            // The picker can see their own hand and their own bury.
            var keptIt = state.Players[viewer].Hand.Contains(C.PartnerCard) || state.Buried.Contains(C.PartnerCard);
            if (keptIt) return 0;
            return 1 / (double)(n - 1);              // one of the others holds the card
        }
        if (state.Players[viewer].Hand.Contains(C.PartnerCard))
            return other == picker ? 1 : 0;          // the partner knows both sides

        // A plain opponent. They know they do not hold the partner card themselves, but not
        // whether the picker kept it. Weigh the two possibilities.
        if (other == picker) return 0;
        var u = n - 2;
        if (u <= 0) return 0;
        var pAlone = (double)(d.Hand + d.Blind) / (C.Deck(n).Count - d.Hand);
        return 1 - (1 - pAlone) / u;
    }

    // MARK: Picking

    public static readonly IReadOnlyDictionary<string, double> TrumpValue = new Dictionary<string, double>
    {
        ["QC"] = 4.5, ["QS"] = 4.0, ["QH"] = 3.6, ["QD"] = 3.2,
        ["JC"] = 2.5, ["JS"] = 2.3, ["JH"] = 2.1, ["JD"] = 1.9,
        ["AD"] = 1.6, ["TD"] = 1.2, ["KD"] = 0.9, ["9D"] = 0.6, ["8D"] = 0.5, ["7D"] = 0.4,
    };

    public static double HandStrength(IReadOnlyList<Card> hand)
    {
        var score = 0.0;
        var t = TrumpOf(hand);
        foreach (var c in t) score += TrumpValue.TryGetValue(c.Id, out var v) ? v : 0.5;
        if (t.Count >= 4) score += (t.Count - 3) * 0.8;

        foreach (var s in C.FailSuits)
        {
            var cards = BySuit(hand, s);
            if (cards.Count == 0) { score += 0.7; continue; }        // void: free trumping chance
            if (cards.Count == 1 && cards[0].Rank != Rank.Ace) score += 0.35;
            foreach (var c in cards)
            {
                if (c.Rank == Rank.Ace) score += cards.Count <= 2 ? 0.9 : 0.7;
                else if (c.Rank == Rank.Ten) score += cards.Count <= 2 ? 0.25 : 0.15;
            }
        }
        return score;
    }

    /// <summary>
    /// Bar for taking the blind, one figure per table size, tuned by simulation until picking came
    /// out close to break-even against the payout table.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, double> PickBase = new Dictionary<int, double>
    {
        [3] = 12.0, [4] = 7.0, [5] = 8.75, [6] = 8.75,
    };

    public static bool ShouldPick(SheepheadState state, int seat, RandomSource rng)
    {
        var n = state.Seats;
        var raw = HandStrength(state.Players[seat].Hand);

        // Later seats have less to lose by taking a marginal hand.
        var frac = n > 1 ? (double)state.PassCount / (n - 1) : 0;
        var threshold = (PickBase.TryGetValue(n, out var b) ? b : 8.5) - 1.5 * frac;

        switch (state.Config.Difficulty)
        {
            case Difficulty.Easy: threshold += rng.NextUnit() * 3 - 1.5; break;
            case Difficulty.Normal: threshold += rng.NextUnit() * 1.2 - 0.6; break;
            case Difficulty.Hard: break;
        }
        return raw >= threshold;
    }

    // MARK: Burying

    private static List<List<Card>> Combinations(IReadOnlyList<Card> arr, int k)
    {
        var output = new List<List<Card>>();
        var cur = new List<Card>();
        void Rec(int start)
        {
            if (cur.Count == k) { output.Add([.. cur]); return; }
            if (start >= arr.Count) return;
            for (var i = start; i < arr.Count; i++)
            {
                cur.Add(arr[i]);
                Rec(i + 1);
                cur.RemoveAt(cur.Count - 1);
            }
        }
        Rec(0);
        return output;
    }

    public static List<Card> ChooseBury(SheepheadState state, int seat)
    {
        var hand = state.Players[seat].Hand;
        var k = state.Spec.Blind;
        List<Card>? best = null;
        var bestScore = double.NegativeInfinity;

        foreach (var set in Combinations(hand, k))
        {
            var score = 0.0;
            foreach (var c in set)
            {
                score += C.Points(c);
                if (C.IsTrump(c)) score -= 45;        // trump is far too useful to throw away
            }
            var kept = hand.Where(c => !set.Contains(c)).ToList();
            foreach (var s in C.FailSuits)
            {
                var had = BySuit(hand, s).Count;
                if (had > 0 && BySuit(kept, s).Count == 0) score += 7;   // a fresh void
            }
            if (score > bestScore) { bestScore = score; best = set; }
        }
        return best ?? hand.Take(k).ToList();
    }

    // MARK: Playing

    public static Card ChooseCard(SheepheadState state, int seat, RandomSource rng)
    {
        var legal = SheepheadGame.LegalCards(state, seat);
        if (legal.Count == 1) return legal[0];

        if (state.Config.Difficulty == Difficulty.Easy && rng.Chance(0.28))
            return legal[rng.NextInt(legal.Count)];

        var unseen = Unseen(state, seat);
        if (state.IsLeaster) return LeasterCard(state, seat, legal, unseen);
        if (state.Trick.Count == 0) return LeadCard(state, seat, legal, unseen);
        return FollowCard(state, seat, legal, unseen);
    }

    private static bool IsLastTrickOfHand(SheepheadState state, int p) => state.Players[p].Hand.Count == 1;

    // MARK: Leading

    private static Card LeadCard(SheepheadState state, int p, List<Card> legal, List<Card> unseen)
    {
        var team = Side(state, p);
        var trump = TrumpOf(legal);
        var fail = FailOf(legal);

        // A boss trump is always a fine lead: it drags out trump and cannot be beaten.
        var bossTrump = trump.Where(c => IsBoss(c, unseen)).ToList();
        var trumpOutstanding = unseen.Count(C.IsTrump);

        if (team == SheepheadTeam.Picker)
        {
            if (bossTrump.Count > 0 && trumpOutstanding > 0) return bossTrump[0];
            if (trump.Count >= 3) return HighestPower(trump);
            // Short on trump: cash a fail ace if it is likely to stand.
            foreach (var c in fail)
                if (c.Rank == Rank.Ace && IsBoss(c, unseen)) return c;
            if (fail.Count > 0) return LowestByPoints(fail);
            return HighestPower(trump);
        }

        // Defence. Pull the picker's trump only when holding the top of what is left.
        if (bossTrump.Count > 0 && trumpOutstanding > 0 && trump.Count >= 3) return bossTrump[0];

        var safeAce = fail.Where(c => c.Rank == Rank.Ace && IsBoss(c, unseen)).ToList();
        if (safeAce.Count > 0)
        {
            // Lead the ace from the longest such suit; it is least likely to be trumped.
            return FirstMin(safeAce, (a, b) => BySuit(legal, a.Suit).Count > BySuit(legal, b.Suit).Count)!.Value;
        }
        if (fail.Count > 0)
        {
            // Lead a low card from the suit where a partner is most likely to help.
            var pool = fail.Where(c => C.Points(c) == 0).ToList();
            if (pool.Count > 0)
                return FirstMin(pool, (a, b) => BySuit(legal, a.Suit).Count > BySuit(legal, b.Suit).Count)!.Value;
            return LowestByPoints(fail);
        }
        return LowestByPoints(legal);
    }

    // MARK: Following

    private static Card FollowCard(SheepheadState state, int p, List<Card> legal, List<Card> unseen)
    {
        var best = CurrentBest(state.Trick);
        var pts = TrickPoints(state.Trick);
        var isLast = state.Trick.Count == state.Seats - 1;
        var prob = AllyProbability(state, p, best.Player);
        var allyWinning = prob >= 0.55;
        var winners = legal.Where(c => C.Beats(c, best.Card)).ToList();
        var losers = legal.Where(c => !C.Beats(c, best.Card)).ToList();
        var lastTrick = IsLastTrickOfHand(state, p);

        if (allyWinning)
        {
            // "Safe" means no card still unaccounted for — including my own — can beat it.
            var safe = isLast || IsBoss(best.Card, unseen.Concat(state.Players[p].Hand));
            if (losers.Count > 0 && (safe || pts >= 4)) return HighestByPoints(losers);
            if (losers.Count > 0)
            {
                // Not confident the trick is theirs yet — feed a middling card, keep the ace.
                var modest = losers.Where(c => C.Points(c) < 10).ToList();
                return HighestByPoints(modest.Count == 0 ? losers : modest);
            }
            return LowestByPoints(legal);   // forced to overtake a friend
        }

        if (winners.Count > 0)
        {
            var take = CheapestWinner(winners);
            var gain = pts + C.Points(take);
            var costlyTrump = C.IsTrump(take) && C.Power(take) >= C.Power(C.PartnerCard);
            var worth =
                lastTrick ||
                (isLast && pts >= 4) ||
                !C.IsTrump(take) ||                        // winning in suit costs nothing
                gain >= 10 ||
                (IsBoss(take, unseen) && pts >= 4) ||
                (Side(state, p) == SheepheadTeam.Picker && pts >= 7);
            if (costlyTrump && pts < 7 && !lastTrick && !IsBoss(take, unseen)) worth = false;
            if (worth) return take;
        }

        if (losers.Count > 0) return LowestByPoints(losers);
        return LowestByPoints(legal);
    }

    // MARK: Leaster: take as few points as possible, but take one trick

    private static Card LeasterCard(SheepheadState state, int p, List<Card> legal, List<Card> unseen)
    {
        var me = state.Players[p];
        var lastTrick = IsLastTrickOfHand(state, p);
        var mustWin = lastTrick && me.TricksWon == 0;

        if (state.Trick.Count == 0)
        {
            if (mustWin)
            {
                var boss = legal.Where(c => IsBoss(c, unseen)).ToList();
                if (boss.Count > 0) return LowestByPoints(boss);
            }
            var zero = legal.Where(c => C.Points(c) == 0).ToList();
            return FirstMin(zero.Count == 0 ? legal : zero, (a, b) => C.Power(a) < C.Power(b))!.Value;
        }

        var best = CurrentBest(state.Trick);
        var winners = legal.Where(c => C.Beats(c, best.Card)).ToList();
        var losers = legal.Where(c => !C.Beats(c, best.Card)).ToList();

        if (mustWin && winners.Count > 0) return CheapestWinner(winners);
        if (losers.Count > 0) return HighestByPoints(losers);   // dump points on somebody else
        return LowestByPoints(winners.Count == 0 ? legal : winners);
    }
}
