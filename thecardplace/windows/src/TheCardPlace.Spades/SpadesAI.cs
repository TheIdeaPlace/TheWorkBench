using TheCardPlace.Core;
using C = TheCardPlace.Spades.SpadesCards;
using G = TheCardPlace.Spades.SpadesGame;

namespace TheCardPlace.Spades;

/// <summary>
/// The computer players: the port of <c>spades/js/ai.js</c> by way of the Swift SpadesAI.
///
/// Spades is a game about a promise you made before you saw a single card played, and every
/// heuristic below is downstream of that. "Playing well" means steering towards a number, and the
/// same card is right or wrong depending on how many tricks this seat still needs.
///
/// WHAT IT PLAYS HONESTLY WITH: only what its seat can see. Its own hand, the bids (public:
/// spoken aloud in order), the cards on the table, the trick that has just gone, and how many
/// tricks each seat has taken. Not the other three hands, and not <c>PlayedThisHand</c> either:
/// <c>ai.js</c> remembers only the current trick and the last one, and a bot that tracked more
/// here would play a different (better) game than the one on the web.
///
/// DIFFICULTY: <c>ai.js</c> never reads it. The web passes 'hard' for every table and offers no
/// control. Easy, normal and hard therefore play identically, which is faithful rather than an
/// omission. Nor does it draw on the random source: the bots are deterministic given the position.
/// </summary>
public static class SpadesAI
{
    // MARK: - The one entry point

    /// <summary>
    /// The computer's move for this seat, reading only what that seat may see. Null if the seat
    /// has nothing to decide.
    /// </summary>
    public static SpadesAction? Decide(SpadesState state, int seat, RandomSource rng)
    {
        if (G.SeatToAct(state) != seat) return null;
        switch (state.Phase)
        {
            case SpadesPhase.Bidding:
                return new SpadesAction.Bid(ChooseBid(state, seat));
            case SpadesPhase.Play:
                return ChooseCard(state, seat) is Card c ? new SpadesAction.Play(c) : null;
            default:
                return null;
        }
    }

    // MARK: - What this seat has not seen

    /// <summary>
    /// The pack minus this seat's hand minus every card it has watched hit the table: the current
    /// trick and the last completed one. Deliberately weaker than a person tracking every card:
    /// the failure direction is a bot that plays a bit loosely, not one that knows things it never
    /// saw.
    /// </summary>
    public static List<Card> Unseen(SpadesState state, int seat)
    {
        var gone = new HashSet<Card>(state.Players[seat].Hand);
        foreach (var t in state.Trick) gone.Add(t.Card);
        if (state.LastTrick is { } lt)
            foreach (var t in lt.Cards) gone.Add(t.Card);
        return C.NewDeck().Where(c => !gone.Contains(c)).ToList();
    }

    internal static List<Card> SuitCards(IEnumerable<Card> cards, Suit s) => cards.Where(c => c.Suit == s).ToList();

    // MARK: - Bidding

    /// <summary>
    /// Roughly how many tricks this hand takes, in halves so that the rounding is an explicit
    /// decision rather than a side effect of integer arithmetic. Calibrated so that flooring it is
    /// right: the table comes to just under thirteen and the floor takes it to about twelve, which
    /// is what real tables bid.
    /// </summary>
    public static double HandStrength(IReadOnlyList<Card> hand)
    {
        var half = 0;
        var trumps = SuitCards(hand, C.Trump);

        // Trump honours are close to certain.
        foreach (var c in trumps)
        {
            switch (c.Rank)
            {
                case Rank.Ace or Rank.King: half += 2; break;
                case Rank.Queen: half += 1; break;
            }
        }

        // Length: every trump past the third is a trick on its own.
        if (trumps.Count > 3) half += (trumps.Count - 3) * 2;

        foreach (var s in C.SuitOrder)
        {
            if (s == C.Trump) continue;
            var cards = SuitCards(hand, s);
            var len = cards.Count;

            foreach (var c in cards)
            {
                switch (c.Rank)
                {
                    case Rank.Ace: half += 2; break;
                    // A king needs cover.
                    case Rank.King: half += len >= 3 ? 2 : 1; break;
                    // A queen needs two cards behind it to be worth anything.
                    case Rank.Queen: if (len >= 3) half += 1; break;
                }
            }

            // Short side suits plus trump means ruffing, while the trumps last.
            if (trumps.Count >= 3)
            {
                if (len == 0) half += trumps.Count >= 4 ? 2 : 1;
                else if (len == 1 && trumps.Count >= 4) half += 1;
            }

            // A long side suit takes late tricks, but only from the sixth card.
            if (len > 5) half += len - 5;
        }

        return half / 2.0;
    }

    /// <summary>Can this hand duck thirteen tricks? Deliberately strict.</summary>
    public static bool NilWorthy(IReadOnlyList<Card> hand)
    {
        var trumps = SuitCards(hand, C.Trump);
        if (trumps.Count > 3) return false;
        if (trumps.Any(c => C.Power(c) > C.Power(new Card(Rank.Nine, Suit.Spades)))) return false;

        var bad = 0;
        foreach (var s in C.SuitOrder)
        {
            if (s == C.Trump) continue;
            var cards = SuitCards(hand, s);
            foreach (var c in cards)
            {
                switch (c.Rank)
                {
                    case Rank.Ace: bad += 3; break;
                    case Rank.King: bad += cards.Count >= 4 ? 1 : 3; break;
                    case Rank.Queen: bad += cards.Count >= 3 ? 0 : 2; break;
                }
            }
        }
        return bad == 0;
    }

    public static int ChooseBid(SpadesState state, int seat)
    {
        var hand = state.Players[seat].Hand;

        if (NilWorthy(hand)) return 0;

        var n = (int)Math.Floor(HandStrength(hand));

        // If the partner (or anybody) has spoken and the table would be well over thirteen, shade
        // down by one. Read from bids, the only honest source.
        int table = 0, spoken = 0;
        foreach (var p in state.Players)
        {
            if (p.Bid is int b)
            {
                table += b;
                spoken += 1;
            }
        }
        if (spoken > 0 && table + n > G.HandSize + 1) n = Math.Max(1, n - 1);

        // A partner sitting on nil needs cover, not ambition.
        if (state.Players[G.PartnerOf(seat)].Bid == 0) n = Math.Max(1, n - 1);

        // Never nil by accident.
        if (n < 1) n = 1;
        if (n > G.HandSize) n = G.HandSize;
        return n;
    }

    // MARK: - Playing

    /// <summary>
    /// How many more tricks this seat's PARTNERSHIP wants. Negative means it is already over and
    /// every further trick is a bag.
    /// </summary>
    public static int StillNeeds(SpadesState state, int seat)
    {
        var team = G.TeamOf(seat);
        return G.ContractOf(state, team) - G.TricksOf(state, team);
    }

    internal static bool IsNil(SpadesState state, int seat) => state.Players[seat].Bid == 0;

    public static Card? ChooseCard(SpadesState state, int seat)
    {
        var legal = G.LegalPlays(state, seat);
        if (legal.Count == 0) return null;
        if (legal.Count == 1) return legal[0];

        // On nil, the whole plan is different: take nothing, ever.
        if (IsNil(state, seat)) return NilPlay(state, legal);

        // Partner is on nil: their tricks are the disaster.
        if (IsNil(state, G.PartnerOf(seat))) return CoverPlay(state, seat, legal);

        return state.Trick.Count == 0 ? ChooseLead(state, seat, legal) : ChooseFollow(state, seat, legal);
    }

    // MARK: Playing a nil

    internal static Card NilPlay(SpadesState state, List<Card> legal)
    {
        // Lead the lowest thing available.
        if (state.Trick.Count == 0) return Lowest(legal);
        var winning = CurrentWinner(state);
        var losing = legal.Where(c => !C.Beats(c, winning.Card)).ToList();
        // The highest card that still loses.
        if (losing.Count > 0) return Highest(losing);
        // Forced to win. Do it with the cheapest card.
        return Lowest(legal);
    }

    // MARK: Covering a partner's nil

    internal static Card CoverPlay(SpadesState state, int seat, List<Card> legal)
    {
        // Lead high. Taking the trick yourself is how the partner never has to.
        if (state.Trick.Count == 0) return Highest(legal);
        var winning = CurrentWinner(state);
        var partner = G.PartnerOf(seat);
        var partnerIn = state.Trick.Any(t => t.Seat == partner);
        var partnerWinning = winning.Seat == partner;

        if (partnerWinning)
        {
            // Take it off them, as cheaply as possible.
            var over = legal.Where(c => C.Beats(c, winning.Card)).ToList();
            if (over.Count > 0) return Lowest(over);
            return Lowest(legal);
        }

        // Partner still to play: cover by taking it now if that is cheap.
        if (!partnerIn)
        {
            var beat = legal.Where(c => C.Beats(c, winning.Card)).ToList();
            if (beat.Count > 0) return Lowest(beat);
        }
        return Lowest(legal);
    }

    // MARK: The ordinary case

    internal static SpadesPlay CurrentWinner(SpadesState state)
    {
        var best = state.Trick[0];
        for (var i = 1; i < state.Trick.Count; i++)
            if (C.Beats(state.Trick[i].Card, best.Card)) best = state.Trick[i];
        return best;
    }

    internal static Card ChooseLead(SpadesState state, int seat, List<Card> legal)
    {
        var need = StillNeeds(state, seat);
        var @out = Unseen(state, seat);

        Card? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var c in legal)
        {
            var score = 0.0;
            var outHigher = @out.Count(o => o.Suit == c.Suit && C.Power(o) > C.Power(c));

            if (need > 0)
            {
                // Chasing tricks: lead winners.
                score += (13 - outHigher) * 6 + C.Power(c);
                // Do not lead trump while chasing unless it is genuinely high.
                if (C.IsTrump(c)) score += C.Power(c) > 11 ? 10 : -30;
            }
            else
            {
                // At or past the contract: every further trick is a bag.
                score += outHigher * 8 - C.Power(c) * 3;
                if (C.IsTrump(c)) score -= 60;   // never spend trump when ducking
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }
        return best ?? legal[0];
    }

    internal static Card ChooseFollow(SpadesState state, int seat, List<Card> legal)
    {
        var need = StillNeeds(state, seat);
        var winning = CurrentWinner(state);
        var partnerWinning = winning.Seat == G.PartnerOf(seat);

        var canBeat = legal.Where(c => C.Beats(c, winning.Card)).ToList();
        var canDuck = legal.Where(c => !C.Beats(c, winning.Card)).ToList();

        // The partner has it. Do not take a trick off your own side.
        if (partnerWinning && canDuck.Count > 0) return Lowest(canDuck);

        if (need > 0)
        {
            if (canBeat.Count == 0) return Lowest(legal);
            // Take it as cheaply as it can be taken.
            return Lowest(canBeat);
        }

        // At or over the contract: duck everything possible.
        if (canDuck.Count > 0) return Highest(canDuck);
        return Lowest(legal);
    }

    /// <summary>The highest by rank; on a tie, the earlier card (as Swift's reduce keeps it).</summary>
    internal static Card Highest(List<Card> cards)
    {
        var best = cards[0];
        for (var i = 1; i < cards.Count; i++)
            if (C.Power(cards[i]) > C.Power(best)) best = cards[i];
        return best;
    }

    /// <summary>The lowest by rank; on a tie, the earlier card.</summary>
    internal static Card Lowest(List<Card> cards)
    {
        var best = cards[0];
        for (var i = 1; i < cards.Count; i++)
            if (C.Power(cards[i]) < C.Power(best)) best = cards[i];
        return best;
    }
}
