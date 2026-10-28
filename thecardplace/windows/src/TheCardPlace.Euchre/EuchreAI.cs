using TheCardPlace.Core;

namespace TheCardPlace.Euchre;

/// <summary>
/// The computer players: the port of <c>euchre/js/ai.js</c>, by way of the Swift <c>EuchreAI</c>.
///
/// IT READS ONLY WHAT ITS OWN SEAT IS ENTITLED TO. Its own hand, the cards already played, the
/// upcard everybody saw, the trump everybody was told, and, if it is the dealer, the card it
/// discarded itself. It never looks at another seat's hand, the kitty, or another seat's discard,
/// and the hidden information test asserts that by scrambling everything it may not see and
/// checking the decision does not change.
///
/// Every heuristic, threshold and difficulty behaviour is the browser game's, including the two
/// bidding thresholds that were arrived at by measurement rather than by reasoning.
/// </summary>
public static class EuchreAI
{
    // MARK: Valuing a hand

    /// <summary>Roughly "tricks I expect this card to be worth", so the numbers can be reasoned about rather than tuned blind.</summary>
    public static double CardValue(Card c, Suit trump)
    {
        switch (EuchreCards.Bower(c, trump))
        {
            case EuchreBower.Right: return 1.0;
            case EuchreBower.Left: return 0.85;
        }
        if (EuchreCards.IsTrump(c, trump))
        {
            return c.Rank switch
            {
                Rank.Ace => 0.65,
                Rank.King => 0.45,
                Rank.Queen => 0.30,
                _ => 0.20,                  // ten, nine: they still draw trump out
            };
        }
        if (c.Rank == Rank.Ace) return 0.50;   // an off-suit ace usually takes a trick
        if (c.Rank == Rank.King) return 0.15;
        return 0.02;
    }

    /// <summary>
    /// What a hand is worth with a given suit as trump.
    ///
    /// Voids are counted, and only voids: a short suit is worth something because it lets you
    /// trump in, and that is only true once you are actually out of it.
    /// </summary>
    public static double HandValue(IReadOnlyList<Card> cards, Suit trump)
    {
        var total = 0.0;
        foreach (var c in cards) total += CardValue(c, trump);

        var have = new Dictionary<Suit, int>();
        foreach (var c in cards)
        {
            var s = EuchreCards.EffectiveSuit(c, trump);
            have[s] = have.GetValueOrDefault(s) + 1;
        }
        var trumps = have.GetValueOrDefault(trump);
        if (trumps > 0)
        {
            foreach (var s in EuchreCards.Suits)
                if (s != trump && have.GetValueOrDefault(s) == 0)
                    total += 0.22;              // void, and something to trump it with
        }
        /* Four or five trump is worth more than the sum of its parts: you can draw everything and
         * the last two are winners by exhaustion. */
        if (trumps >= 4) total += 0.45;
        else if (trumps == 3) total += 0.15;
        return total;
    }

    internal static int TrumpCount(IEnumerable<Card> cards, Suit trump) => cards.Count(c => EuchreCards.IsTrump(c, trump));

    // MARK: The bidding

    /// <summary>
    /// The two numbers that decide how often anybody bids at all, in expected tricks.
    /// <see cref="OrderThreshold"/> is lower than <see cref="CallThreshold"/> on purpose: in round
    /// one somebody is about to be handed a known card; by round two everybody has passed once,
    /// which is real evidence that the hands around the table are poor.
    /// </summary>
    public const double OrderThreshold = 2.00;
    public const double CallThreshold = 2.20;

    /// <summary>
    /// How much a bid threshold moves for this difficulty, plus a little noise so three computer
    /// seats at one table do not all make identical decisions from identical positions.
    /// </summary>
    internal static double BidBias(Difficulty difficulty, RandomSource rng) => difficulty switch
    {
        Difficulty.Easy => -0.45 + rng.NextUnit() * 0.5,      // bids on too little
        Difficulty.Hard => (rng.NextUnit() - 0.5) * 0.12,
        _ => (rng.NextUnit() - 0.5) * 0.30,
    };

    /// <summary>
    /// Whether a hand is worth playing without a partner: a top trump, real length in it, and a
    /// hand that is already strong. Both bowers, or a bower and the ace of trump, is the hand
    /// people actually declare on.
    /// </summary>
    internal static bool GoAlone(IReadOnlyList<Card> cards, Suit trump, double value)
    {
        if (TrumpCount(cards, trump) < 3) return false;
        var right = cards.Any(c => EuchreCards.Bower(c, trump) == EuchreBower.Right);
        var left = cards.Any(c => EuchreCards.Bower(c, trump) == EuchreBower.Left);
        var aceTrump = cards.Any(c => EuchreCards.IsTrump(c, trump) && c.Rank == Rank.Ace);
        var top = (right && left) || (right && aceTrump) || (left && aceTrump);
        return top && value >= 3.10;
    }

    /// <summary>
    /// Round one. The upcard's suit is on offer, and the dealer gets the card, which is the whole
    /// of round one strategy: the same hand is a clear order when your partner is dealing and a
    /// clear pass when the seat on your left is, because the card changes hands either way.
    /// </summary>
    internal static EuchreAction BidRound1(EuchreState state, int p, RandomSource rng)
    {
        if (state.Upcard is not { } up || state.Dealer is not { } dealer) return new EuchreAction.Pass();
        var hand = state.Players[p].Hand;
        var trump = up.Suit;
        var value = HandValue(hand, trump);

        if (p == dealer)
        {
            /* We will hold the upcard and put our worst card back, so value the hand as it will
             * actually be, not as it is. */
            List<Card> withCard = [.. hand, up];
            var worst = WorstDiscard(withCard, trump);
            value = HandValue(withCard.Removing(worst), trump);
        }
        else if (EuchreGame.PartnerOf(p) == dealer)
        {
            value += CardValue(up, trump) * 0.55;       // it helps our side
        }
        else
        {
            value -= CardValue(up, trump) * 0.75;       // it helps theirs
        }

        var bias = BidBias(state.Config.Difficulty, rng);
        var order = value >= OrderThreshold + bias;

        var alone = false;
        if (order && state.Config.AllowAlone)
        {
            /* Judged on the hand as it will BE, not as it is: the dealer's upcard may be the
             * right bower. */
            List<Card> effective = p == dealer ? [.. hand, up] : [.. hand];
            alone = GoAlone(effective, trump, value);
        }
        return order ? new EuchreAction.OrderUp(alone) : new EuchreAction.Pass();
    }

    /// <summary>Round two. Any suit but the one turned down.</summary>
    internal static EuchreAction BidRound2(EuchreState state, int p, RandomSource rng)
    {
        var hand = state.Players[p].Hand;
        (Suit Suit, double Value)? best = null;
        foreach (var s in EuchreCards.Suits)
        {
            if (s == state.DeniedSuit) continue;
            var v = HandValue(hand, s);
            /* CALLING NEXT. The turned-down suit's own colour is the suit to prefer: everybody
             * just declined the upcard, which is weak evidence that its bowers are not out there.
             * Small, and real. */
            if (state.DeniedSuit is { } denied && s == denied.SameColour()) v += 0.18;
            if (best is null || v > best.Value.Value) best = (s, v);
        }
        if (best is not { } choice) return new EuchreAction.Pass();

        var forced = p == state.Dealer && state.Config.StickTheDealer;
        var bias = BidBias(state.Config.Difficulty, rng);
        /* Higher than round one: nobody gets a card, and having passed once already everybody
         * has told you something about their hand. */
        if (!forced && choice.Value < CallThreshold + bias) return new EuchreAction.Pass();

        var alone = state.Config.AllowAlone && GoAlone(hand, choice.Suit, choice.Value);
        return new EuchreAction.CallSuit(choice.Suit, alone);
    }

    // MARK: The discard

    /// <summary>
    /// Void yourself if you can do it cheaply, otherwise throw the lowest card you hold. Never
    /// throw trump, and never throw an off-suit ace to make a void: the ace is usually a trick and
    /// the void is usually worth less.
    /// </summary>
    public static Card WorstDiscard(IReadOnlyList<Card> cards, Suit trump)
    {
        var counts = new Dictionary<Suit, int>();
        foreach (var c in cards)
        {
            var s = EuchreCards.EffectiveSuit(c, trump);
            counts[s] = counts.GetValueOrDefault(s) + 1;
        }

        (Card Card, double Score)? best = null;
        foreach (var c in cards)
        {
            if (EuchreCards.IsTrump(c, trump)) continue;   // never put trump back
            var score = CardValue(c, trump);
            /* A singleton that is not an ace is the cheapest void available, so it is worth a
             * small discount rather than a large one. */
            if (counts.GetValueOrDefault(EuchreCards.EffectiveSuit(c, trump)) == 1 && c.Rank != Rank.Ace) score -= 0.12;
            if (best is null || score < best.Value.Score) best = (c, score);
        }
        if (best is { } b) return b.Card;

        /* A hand of nothing but trump. Rare, excellent, and it still has to put one back: the
         * lowest trump. */
        return Lowest(cards, trump) ?? cards[0];
    }

    // MARK: Playing a card

    /// <summary>
    /// Cards this seat has not seen: the whole deck, less its own hand, less everything played,
    /// less the upcard if its whereabouts are known, less its own discard. Public information and
    /// its own, and nothing else.
    /// </summary>
    public static List<Card> Unseen(EuchreState state, int seat)
    {
        var p = seat;
        var seen = new HashSet<Card>();
        foreach (var c in state.Players[p].Hand) seen.Add(c);
        foreach (var c in state.Played) seen.Add(c);
        foreach (var t in state.Trick) seen.Add(t.Card);
        if (state.Upcard is { } up && state.UpcardStatus != EuchreUpcardStatus.None) seen.Add(up);
        if (p == state.Dealer && state.Discard is { } d) seen.Add(d);
        return EuchreCards.Deck.Where(c => !seen.Contains(c)).ToList();
    }

    /// <summary>The first card of the highest power, in hand order: the same answer a stable sort gives, without depending on one.</summary>
    internal static Card? Highest(IEnumerable<Card> cards, Suit? trump)
    {
        Card? best = null;
        foreach (var c in cards)
            if (best is not { } b || EuchreCards.Power(c, trump) > EuchreCards.Power(b, trump)) best = c;
        return best;
    }

    internal static Card? Lowest(IEnumerable<Card> cards, Suit? trump)
    {
        Card? best = null;
        foreach (var c in cards)
            if (best is not { } b || EuchreCards.Power(c, trump) < EuchreCards.Power(b, trump)) best = c;
        return best;
    }

    /// <summary>A stable sort, so ties keep hand order the way the browser's sort does.</summary>
    internal static List<Card> StableSorted(IEnumerable<Card> cards, Func<Card, Card, bool> less)
    {
        var indexed = cards.Select((c, i) => (Card: c, Offset: i)).ToList();
        indexed.Sort((a, b) =>
        {
            if (less(a.Card, b.Card)) return -1;
            if (less(b.Card, a.Card)) return 1;
            return a.Offset.CompareTo(b.Offset);
        });
        return indexed.Select(x => x.Card).ToList();
    }

    /// <summary>The cheapest card to throw away when this trick is lost or already won. Off-suit first, lowest first, and hold trump back.</summary>
    internal static Card? ThrowAway(IReadOnlyList<Card> cards, Suit? trump)
    {
        var off = cards.Where(c => !EuchreCards.IsTrump(c, trump)).ToList();
        if (off.Count > 0 && trump is { } t)
        {
            /* Keep aces, and keep the last card of a suit you might otherwise still guard. Sorting
             * by value rather than by rank is what stops it pitching an off-suit ace to keep a
             * nine. */
            return StableSorted(off, (a, b) => CardValue(a, t) < CardValue(b, t))[0];
        }
        return Lowest(cards, trump);
    }

    /// <summary>Is the trick currently being won by my partner?</summary>
    internal static bool PartnerWinning(EuchreState state, int p)
    {
        if (state.Trick.Count == 0) return false;
        var wi = EuchreGame.TrickWinnerIndex(state.Trick, state.Trump);
        return state.Trick[wi].Player == EuchreGame.PartnerOf(p);
    }

    /// <summary>
    /// Can anybody still to play in this trick beat what is winning it? Answered from unseen cards
    /// and from who has yet to play, never by looking at their hands. Wrong sometimes, which is
    /// the point.
    /// </summary>
    internal static bool SafeToDuck(EuchreState state, int p)
    {
        var yetToPlay = EuchreGame.ActiveCount(state) - state.Trick.Count - 1;
        if (yetToPlay <= 0) return true;
        if (state.Trick.Count == 0) return false;
        var first = state.Trick[0];
        var wi = EuchreGame.TrickWinnerIndex(state.Trick, state.Trump);
        var winning = state.Trick[wi].Card;
        var led = EuchreCards.EffectiveSuit(first.Card, state.Trump);
        var beaters = Unseen(state, p).Where(c =>
        {
            if (EuchreCards.IsTrump(c, state.Trump)) return EuchreCards.Beats(c, winning, state.Trump);
            return EuchreCards.EffectiveSuit(c, state.Trump) == led && EuchreCards.Beats(c, winning, state.Trump);
        });
        return !beaters.Any();
    }

    internal static Card? ChooseLead(EuchreState state, int p)
    {
        var hand = state.Players[p].Hand;
        var trump = state.Trump;
        var mine = hand.Where(c => EuchreCards.IsTrump(c, trump)).ToList();
        var off = hand.Where(c => !EuchreCards.IsTrump(c, trump)).ToList();
        var iAmMaker = state.Maker is { } m && EuchreGame.TeamOf(p) == EuchreGame.TeamOf(m);
        var haveRight = mine.Any(c => EuchreCards.Bower(c, trump) == EuchreBower.Right);

        /* The makers lead trump to draw it out, provided they have enough of it that running out
         * first is not the likely outcome. */
        if (iAmMaker && mine.Count >= 3) return Highest(mine, trump);
        if (iAmMaker && haveRight && mine.Count >= 2) return Highest(mine, trump);

        /* Otherwise cash an off-suit ace while it is still good. */
        foreach (var c in off)
            if (c.Rank == Rank.Ace) return c;

        /* Defending, with the right bower and nothing else to do: take it out from under whoever
         * is counting on it. */
        if (!iAmMaker && haveRight && mine.Count >= 3) return Highest(mine, trump);

        if (off.Count > 0)
        {
            /* Lead from a short suit rather than a long one: it gets us void sooner. Among equals,
             * the lowest card. */
            var counts = new Dictionary<Suit, int>();
            foreach (var c in off) counts[c.Suit] = counts.GetValueOrDefault(c.Suit) + 1;
            return StableSorted(off, (a, b) =>
            {
                int ca = counts.GetValueOrDefault(a.Suit), cb = counts.GetValueOrDefault(b.Suit);
                if (ca != cb) return ca < cb;
                return EuchreCards.Power(a, trump) < EuchreCards.Power(b, trump);
            })[0];
        }
        return Lowest(mine, trump);
    }

    internal static Card? ChooseFollow(EuchreState state, int p)
    {
        var trump = state.Trump;
        var legal = EuchreGame.FollowingPlays(state, p);
        if (state.Trick.Count == 0) return null;
        var first = state.Trick[0];
        var wi = EuchreGame.TrickWinnerIndex(state.Trick, trump);
        var winning = state.Trick[wi].Card;
        var winners = legal.Where(c => EuchreCards.Beats(c, winning, trump)).ToList();
        var last = state.Trick.Count == EuchreGame.ActiveCount(state) - 1;

        if (PartnerWinning(state, p))
        {
            /* Partner has it. Do not overtrump your own side; throw the cheapest thing that is not
             * trump, unless partner's card is weak and somebody after us is likely to take it
             * anyway. */
            if (last || SafeToDuck(state, p)) return ThrowAway(legal, trump);
            if (winners.Count > 0) return Lowest(winners, trump);
            return ThrowAway(legal, trump);
        }

        if (winners.Count == 0) return ThrowAway(legal, trump);

        /* Playing last, take it as cheaply as possible. */
        if (last) return Lowest(winners, trump);

        /* Somebody is still to play. Winning cheaply invites being overtrumped, so take it with
         * something that will hold up: the lowest card that nothing unseen beats, or failing that
         * the highest we have. */
        var unseenCards = Unseen(state, p);
        var led = EuchreCards.EffectiveSuit(first.Card, trump);
        var ranked = StableSorted(winners, (a, b) => EuchreCards.Power(a, trump) < EuchreCards.Power(b, trump));
        foreach (var cand in ranked)
        {
            var beatable = unseenCards.Any(o =>
                EuchreCards.IsTrump(o, trump)
                    ? EuchreCards.Beats(o, cand, trump)
                    : EuchreCards.EffectiveSuit(o, trump) == led && EuchreCards.Beats(o, cand, trump));
            if (!beatable) return cand;
        }
        return Highest(winners, trump);
    }

    internal static Card? ChooseCard(EuchreState state, int p, RandomSource rng)
    {
        var legal = EuchreGame.FollowingPlays(state, p);
        if (legal.Count == 0) return null;

        /* Easy opponents throw a legal card at random some of the time. Deliberately a legal one:
         * an opponent who revokes is not an easier opponent, it is a broken game. */
        if (state.Config.Difficulty == Difficulty.Easy && rng.NextUnit() < 0.30)
            return legal[rng.NextInt(legal.Count)];
        if (state.Config.Difficulty == Difficulty.Normal && rng.NextUnit() < 0.10)
            return legal[rng.NextInt(legal.Count)];

        var pick = state.Trick.Count == 0 ? ChooseLead(state, p) : ChooseFollow(state, p);
        /* Belt and braces. Everything above is written to return a legal card; falling back to
         * one is a much better failure than a refused move. */
        if (pick is not { } c || !legal.Contains(c)) pick = legal[0];
        return pick;
    }

    // MARK: Taking a turn

    /// <summary>The computer's move for this seat, reading only what that seat may see. Null if the seat has nothing to decide.</summary>
    public static EuchreAction? Decide(EuchreState state, int seat, RandomSource rng)
    {
        if (seat < 0 || seat >= state.Players.Count || EuchreGame.SeatToAct(state) != seat) return null;
        switch (state.Phase)
        {
            case EuchrePhase.Bid1:
                return BidRound1(state, seat, rng);
            case EuchrePhase.Bid2:
                return BidRound2(state, seat, rng);
            case EuchrePhase.Discard:
                if (state.Trump is not { } trump || state.Players[seat].Hand.Count == 0) return null;
                return new EuchreAction.Discard(WorstDiscard(state.Players[seat].Hand, trump));
            case EuchrePhase.Play:
                return ChooseCard(state, seat, rng) is { } card ? new EuchreAction.Play(card) : null;
            default:
                return null;
        }
    }
}
