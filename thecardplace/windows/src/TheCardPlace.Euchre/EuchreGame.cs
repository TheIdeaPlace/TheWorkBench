using TheCardPlace.Core;

namespace TheCardPlace.Euchre;

/// <summary>
/// The rules: the port of <c>euchre/js/game.js</c>, by way of the Swift <c>EuchreGame</c>.
///
/// Four seats, two fixed partnerships: seats 0 and 2 against seats 1 and 3. That is not
/// configurable and should not become so; the whole shape of the game, from going alone to the
/// scoring table, is built on partners sitting opposite.
/// </summary>
public static class EuchreGame
{
    public const int Seats = 4;
    public const int HandSize = 5;
    public const int KittySize = 3;

    public static int TeamOf(int i) => ((i % Seats) + Seats) % Seats % 2;
    public static int PartnerOf(int i) => (i + 2) % Seats;

    public static EuchreState CreateGame(EuchreConfig config)
    {
        var cfg = (config ?? new EuchreConfig()).Normalized();
        var s = new EuchreState { Config = cfg };
        for (var i = 0; i < Seats; i++)
            s.Players.Add(new EuchrePlayer { Index = i, Name = cfg.Names[i], Occupant = i == 0 ? Occupant.Human : Occupant.Bot });
        Introduce(s);
        return s;
    }

    /// <summary>Who sits where, told to each seat as the game opens.</summary>
    internal static void Introduce(EuchreState s)
    {
        for (var i = 0; i < Seats; i++)
        {
            var partner = PartnerOf(i);
            var others = Enumerable.Range(0, Seats).Where(j => TeamOf(j) != TeamOf(i)).ToList();
            s.Log.Add(EventKind.Info,
                $"Euchre to {s.Config.PointsToWin} points. You are in seat {i + 1}; " +
                $"{Name(s, partner)} is your partner, across the table. " +
                $"{Name(s, others[0])} and {Name(s, others[1])} are against you.",
                seat: i, audience: i);
        }
    }

    // MARK: The only way in

    /// <summary>
    /// Validates the seat, the phase and the action, and refuses with a reason in the words of the
    /// rule. Never throws on bad input, and a refusal leaves the state exactly as it was.
    /// </summary>
    public static ActionResult ApplyAction(EuchreState state, int seat, EuchreAction action, RandomSource rng)
    {
        if (seat < 0 || seat >= state.Players.Count) return ActionResult.Refused("not a seat at this table");

        switch (action)
        {
            case EuchreAction.Start:
                if (state.Phase != EuchrePhase.Idle) return ActionResult.Refused("the game has already started");
                return NewHand(state, rng);

            case EuchreAction.NextHand:
                if (state.Phase == EuchrePhase.GameOver) return ActionResult.Refused("the game is over — start a new game");
                if (state.Phase != EuchrePhase.HandOver) return ActionResult.Refused("the hand is not over");
                return NewHand(state, rng);

            case EuchreAction.NewGame:
                if (state.Phase != EuchrePhase.GameOver) return ActionResult.Refused("the game is not over");
                NewGame(state);
                return ActionResult.Ok;

            case EuchreAction.OrderUp order:
                if (state.Phase != EuchrePhase.Bid1) return ActionResult.Refused("the upcard is not on offer");
                if (state.Turn != seat) return ActionResult.Refused("not your turn to bid");
                return DoOrder(state, seat, order.Alone);

            case EuchreAction.CallSuit call:
                if (state.Phase != EuchrePhase.Bid2) return ActionResult.Refused("not the naming round");
                if (state.Turn != seat) return ActionResult.Refused("not your turn to bid");
                // Swift's Suit cannot hold anything else; a C# enum from the wire can.
                if (!Enum.IsDefined(call.Suit)) return ActionResult.Refused("that is not a suit");
                if (call.Suit == state.DeniedSuit)
                    return ActionResult.Refused($"{call.Suit.LowerName()} was turned down and cannot be named this hand");
                return DoCall(state, seat, call.Suit, call.Alone);

            case EuchreAction.Pass:
                if (state.Phase != EuchrePhase.Bid1 && state.Phase != EuchrePhase.Bid2)
                    return ActionResult.Refused("there is nothing to pass on");
                if (state.Turn != seat) return ActionResult.Refused("not your turn to bid");
                if (state.Phase == EuchrePhase.Bid2 && seat == state.Dealer && state.Config.StickTheDealer)
                    return ActionResult.Refused("stick the dealer is on, so you must name a suit");
                return DoPass(state, seat);

            case EuchreAction.Discard discard:
                if (state.Phase != EuchrePhase.Discard) return ActionResult.Refused("nothing to discard");
                if (seat != state.Dealer) return ActionResult.Refused("only the dealer discards");
                if (!state.Players[seat].Hand.Contains(discard.Card)) return ActionResult.Refused("that card is not in your hand");
                return DoDiscard(state, seat, discard.Card);

            case EuchreAction.Play play:
                if (state.Phase != EuchrePhase.Play) return ActionResult.Refused("not the playing phase");
                if (state.SittingOut == seat) return ActionResult.Refused("you are sitting out this hand");
                if (state.Turn != seat) return ActionResult.Refused("not your turn");
                if (IllegalReason(state, seat, play.Card) is { } why) return ActionResult.Refused(why);
                return DoPlay(state, seat, play.Card);

            default:
                return ActionResult.Refused("that is not something you can do in euchre");
        }
    }

    /// <summary>
    /// Whose move the table is waiting for, or null if it is waiting for nobody.
    ///
    /// Not simply <c>Turn</c>: the discard belongs to the dealer, who may not be the seat on turn,
    /// and between hands nobody is on move at all.
    /// </summary>
    public static int? SeatToAct(EuchreState state) => state.Phase switch
    {
        EuchrePhase.Bid1 or EuchrePhase.Bid2 or EuchrePhase.Play => state.Turn,
        EuchrePhase.Discard => state.Dealer,
        _ => null,
    };

    /// <summary>Exactly the phases in which NextHand is accepted. Idle belongs to Start, and a finished game to NewGame.</summary>
    public static bool CanDeal(EuchreState state) => state.Phase == EuchrePhase.HandOver;

    /// <summary>The cards this seat may play right now; empty if it is not their turn.</summary>
    public static List<Card> LegalPlays(EuchreState state, int seat)
    {
        if (seat < 0 || seat >= state.Players.Count) return [];
        if (state.Phase != EuchrePhase.Play || state.Turn != seat || state.SittingOut == seat) return [];
        return FollowingPlays(state, seat);
    }

    /// <summary>
    /// The follow-suit rule on its own, without the turn check: what this seat could play to the
    /// trick as it stands.
    /// </summary>
    internal static List<Card> FollowingPlays(EuchreState state, int seat)
    {
        var hand = state.Players[seat].Hand;
        if (state.SittingOut == seat) return [];
        if (state.Trick.Count == 0) return [.. hand];
        var led = EuchreCards.EffectiveSuit(state.Trick[0].Card, state.Trump);
        var follow = hand.Where(c => EuchreCards.EffectiveSuit(c, state.Trump) == led).ToList();
        return follow.Count == 0 ? [.. hand] : follow;
    }

    /// <summary>
    /// Why a card cannot be played by this seat to the trick as it stands, or null if it can.
    /// Assumes it is the seat's turn; <see cref="WhyNot"/> adds the rest.
    /// </summary>
    public static string? IllegalReason(EuchreState state, int seat, Card card)
    {
        if (seat < 0 || seat >= state.Players.Count) return "not a seat at this table";
        if (state.SittingOut == seat) return "you are sitting out this hand";
        var hand = state.Players[seat].Hand;
        if (!hand.Contains(card)) return "that card is not in your hand";
        if (state.Trick.Count == 0) return null;
        var led = EuchreCards.EffectiveSuit(state.Trick[0].Card, state.Trump);
        if (EuchreCards.EffectiveSuit(card, state.Trump) == led) return null;
        /* Named precisely, because the left bower is exactly where this goes wrong. "You must
         * follow clubs" while holding the jack of clubs and clubs are not trump is a message that
         * reads as a bug unless it says why. */
        var mine = hand.Where(c => EuchreCards.EffectiveSuit(c, state.Trump) == led).ToList();
        if (mine.Count == 0) return null;
        var ledWord = led == state.Trump ? "trump" : led.LowerName();
        return $"you must follow {ledWord} — you hold " + mine.SpokenList();
    }

    /// <summary>
    /// Why a card in this seat's hand cannot be played now, or null if it can. Never says "not
    /// your turn" when it IS your turn: while deciding on a bid the cards are for review, which is
    /// a different thing and worth saying.
    /// </summary>
    public static string? WhyNot(EuchreState state, int seat, Card card)
    {
        if (seat < 0 || seat >= state.Players.Count) return "not a seat at this table";
        switch (state.Phase)
        {
            case EuchrePhase.Idle:
                return "no hand has been dealt yet";
            case EuchrePhase.Bid1:
            case EuchrePhase.Bid2:
                return state.Turn == seat
                    ? "for review while you decide your bid"
                    : $"for review, {Name(state, state.Turn)} is bidding";
            case EuchrePhase.Discard:
                return state.Dealer == seat
                    ? "for review while you choose what to put back"
                    : $"for review, {Name(state, state.Dealer ?? state.Turn)} is putting a card back";
            case EuchrePhase.HandOver:
                return "the hand is over";
            case EuchrePhase.GameOver:
                return "the game is over";
            default:
                if (state.SittingOut == seat)
                    return $"you are sitting out while {Name(state, state.Maker ?? state.Turn)} plays alone";
                if (state.Turn != seat) return $"not your turn, {Name(state, state.Turn)} is to play";
                return IllegalReason(state, seat, card);
        }
    }

    // MARK: Names and sides

    internal static string Name(EuchreState s, int i)
    {
        if (i < 0 || i >= s.Players.Count) return "the table";
        return s.Players[i].Name;
    }

    /// <summary>Keeps messages grammatical when a player has left their name as "You".</summary>
    internal static string Vb(EuchreState s, int i, string third, string second)
    {
        if (i < 0 || i >= s.Players.Count) return third;
        return s.Players[i].Name.ToLowerInvariant() == "you" ? second : third;
    }

    /// <summary>
    /// Who a side is, by the names of the two people in it. "Seats 1 and 3" is accurate and
    /// unmemorable; "you and Skipper" is what a player is actually keeping track of.
    /// </summary>
    public static string SideWords(EuchreState s, int team) =>
        string.Join(" and ", Enumerable.Range(0, Seats).Where(i => TeamOf(i) == team).Select(i => Name(s, i)));

    private static readonly string[] PointWordList = ["no points", "one point", "two points", "three points", "four points"];

    /// <summary>Numbers a screen reader says as words, for the only sentence that says who won.</summary>
    internal static string PointWords(int n) => n >= 0 && n < PointWordList.Length ? PointWordList[n] : $"{n} points";

    // MARK: Seating and turn order

    public static bool IsActive(EuchreState s, int i) => i != s.SittingOut;

    /// <summary>
    /// The next seat that is actually holding cards. While somebody plays alone their partner is
    /// skipped entirely: not given an empty turn, skipped.
    /// </summary>
    public static int NextActive(EuchreState s, int i)
    {
        var j = i;
        for (var k = 0; k < Seats; k++)
        {
            j = (j + 1) % Seats;
            if (IsActive(s, j)) return j;
        }
        return i;
    }

    internal static int FirstActive(EuchreState s, int i) => IsActive(s, i) ? i : NextActive(s, i);

    public static int ActiveCount(EuchreState s) => s.SittingOut != null ? Seats - 1 : Seats;

    // MARK: Dealing

    internal static bool Deal(EuchreState s, int dealer, RandomSource rng)
    {
        var deck = EuchreCards.Deck.Shuffled(rng);
        for (var i = 0; i < Seats; i++) s.Players[i].Hand = [];
        /* Three and two, the way it is actually dealt at a table. It makes no difference to a
         * shuffled deck, and it is what somebody learning the game from this program will see
         * described everywhere else. */
        int[] order = [3, 2];
        var at = 0;
        for (var round = 0; round < 2; round++)
        {
            for (var k = 0; k < Seats; k++)
            {
                var seat = (dealer + 1 + k) % Seats;
                var n = order[(round + k) % 2];
                for (var x = 0; x < n; x++)
                {
                    s.Players[seat].Hand.Add(deck[at]);
                    at++;
                }
            }
        }
        for (var q = 0; q < Seats; q++)
            if (s.Players[q].Hand.Count != HandSize) return false;
        var up = deck[at];
        at++;
        s.Upcard = up;
        s.Kitty = deck.GetRange(at, KittySize);
        s.Dealt = new EuchreDeal(s.Players.Select(p => p.Hand.ToList()).ToList(), up, [.. s.Kitty]);
        return true;
    }

    internal static ActionResult NewHand(EuchreState s, RandomSource rng)
    {
        if (s.GameNumber == 0) s.GameNumber = 1;
        var dealer = s.Dealer is { } d ? (d + 1) % Seats : rng.NextInt(Seats);
        s.Dealer = dealer;

        s.HandNumber += 1;
        s.Phase = EuchrePhase.Bid1;
        s.Trump = null;
        s.Maker = null;
        s.Alone = false;
        s.SittingOut = null;
        s.UpcardStatus = EuchreUpcardStatus.Up;
        s.DeniedSuit = null;
        s.Discard = null;
        s.Trick = [];
        s.LastTrick = null;
        s.Played = [];
        s.TrickLog = [];
        s.BidLog = [];
        s.Result = null;
        for (var i = 0; i < Seats; i++) s.Players[i].TricksWon = 0;

        if (!Deal(s, dealer, rng) || s.Upcard is not { } up)
            return ActionResult.Faulted("the deal did not give everybody five cards");

        s.Leader = (dealer + 1) % Seats;
        s.Turn = s.Leader;

        s.Log.Add(EventKind.Deal,
            $"Hand {s.HandNumber}. {Name(s, dealer)}{Vb(s, dealer, " deals", " deal")}. " +
            $"The upcard is the {up.Name}. {Name(s, s.Turn)}{Vb(s, s.Turn, " bids", " bid")} first.",
            seat: dealer, cards: [up]);

        /* Each seat is told its own cards, privately. One event per seat means every seat gets
         * exactly one, so the shape of the log gives nothing away either. */
        for (var i = 0; i < Seats; i++)
        {
            var sorted = EuchreCards.SortHand(s.Players[i].Hand, null);
            s.Log.Add(EventKind.You, "Your hand: " + string.Join(", ", sorted.Select(c => c.Name)) + ".",
                seat: i, cards: sorted, audience: i);
        }
        return ActionResult.Ok;
    }

    /// <summary>
    /// A finished game starts a fresh one rather than continuing past the target. Scores go back
    /// to nothing; the match record does not, and the deal keeps rotating.
    /// </summary>
    internal static void NewGame(EuchreState s)
    {
        s.Scores = [0, 0];
        s.GameWinner = null;
        s.HandNumber = 0;
        s.GameNumber += 1;
        s.Phase = EuchrePhase.Idle;
        s.Trump = null;
        s.Maker = null;
        s.Alone = false;
        s.SittingOut = null;
        s.Upcard = null;
        s.UpcardStatus = EuchreUpcardStatus.None;
        s.DeniedSuit = null;
        s.Kitty = [];
        s.Discard = null;
        s.Trick = [];
        s.LastTrick = null;
        s.Played = [];
        s.TrickLog = [];
        s.BidLog = [];
        s.Dealt = null;
        s.Result = null;
        for (var i = 0; i < Seats; i++)
        {
            s.Players[i].Hand = [];
            s.Players[i].TricksWon = 0;
        }
        s.Log.Add(EventKind.Game, $"A new game, to {s.Config.PointsToWin} points. Games won: " +
            $"{SideWords(s, 0)} {s.GamesWon[0]}, {SideWords(s, 1)} {s.GamesWon[1]}.");
    }

    // MARK: Bidding

    internal static void BeginPlay(EuchreState s)
    {
        s.Phase = EuchrePhase.Play;
        s.Leader = FirstActive(s, ((s.Dealer ?? 0) + 1) % Seats);
        s.Turn = s.Leader;
        s.Trick = [];
        s.Log.Add(EventKind.Info, $"{Name(s, s.Leader)}{Vb(s, s.Leader, " leads", " lead")} to the first trick.",
            seat: s.Leader);
    }

    /// <summary>
    /// Somebody has decided what trump is. The one place that happens, so the "going alone"
    /// bookkeeping cannot end up done twice or not at all.
    /// </summary>
    internal static void MakeTrump(EuchreState s, int p, Suit suit, bool alone, string how, EuchreBidKind kind, int round)
    {
        s.Trump = suit;
        s.Maker = p;
        s.Alone = alone && s.Config.AllowAlone;

        var msg = $"{Name(s, p)} {how}. {suit.Name()} are trump.";
        if (s.Alone)
        {
            var @out = PartnerOf(p);
            s.SittingOut = @out;
            msg += $" {Name(s, p)}{Vb(s, p, " is", " are")} going alone, so " +
                $"{Name(s, @out)}{Vb(s, @out, " sits", " sit")} out this hand.";
        }
        s.Log.Add(EventKind.Bid, msg, seat: p);
        s.BidLog.Add(new EuchreBid(p, kind, round, suit, s.Alone, how));

        /* Everybody is told what the left bower now is, because it has changed which suit two
         * cards belong to and that is the single commonest way to lose a trick you meant to win. */
        s.Log.Add(EventKind.Info,
            $"The right bower is the Jack of {suit.Name()}; the left bower is the Jack of " +
            $"{suit.SameColour().Name()}, which counts as {suit.LowerName()} for this hand.",
            cards: [new Card(Rank.Jack, suit), new Card(Rank.Jack, suit.SameColour())]);
    }

    /// <summary>Round one: take the upcard, which makes its suit trump and hands the card to the dealer.</summary>
    internal static ActionResult DoOrder(EuchreState s, int p, bool alone)
    {
        if (s.Dealer is not { } dealer || s.Upcard is not { } up) return ActionResult.Faulted("no upcard to order up");
        var how = p == dealer ? "takes it up"
            : p == PartnerOf(dealer) ? $"orders it up for {Name(s, dealer)}"
            : "orders it up";
        MakeTrump(s, p, up.Suit, alone, how, EuchreBidKind.OrderUp, 1);

        /* The dealer takes the upcard whoever ordered it. Even when the dealer is sitting out
         * because their partner went alone: they still pick up and discard, which is the rule
         * most often got wrong. */
        s.Players[dealer].Hand.Add(up);
        s.Log.Add(EventKind.Info, $"{Name(s, dealer)}{Vb(s, dealer, " takes", " take")} the {up.Name} and must discard.",
            seat: dealer, cards: [up]);
        s.Log.Add(EventKind.You, $"You took the {up.Name}. Choose a card to put back — you have six and may keep only five.",
            seat: dealer, cards: [up], audience: dealer);
        s.UpcardStatus = EuchreUpcardStatus.Taken;
        s.Phase = EuchrePhase.Discard;
        s.Turn = dealer;
        return ActionResult.Ok;
    }

    /// <summary>Round two: name any suit but the one that was turned down.</summary>
    internal static ActionResult DoCall(EuchreState s, int p, Suit suit, bool alone)
    {
        MakeTrump(s, p, suit, alone, $"names {suit.LowerName()}", EuchreBidKind.Call, 2);
        BeginPlay(s);
        return ActionResult.Ok;
    }

    internal static ActionResult DoPass(EuchreState s, int p)
    {
        if (s.Dealer is not { } dealer || s.Upcard is not { } up) return ActionResult.Faulted("no hand in progress");
        var round = s.Phase == EuchrePhase.Bid1 ? 1 : 2;
        s.BidLog.Add(new EuchreBid(p, EuchreBidKind.Pass, round, Words: "pass"));
        s.Log.Add(EventKind.Bid, $"{Name(s, p)}{Vb(s, p, " passes", " pass")}.", seat: p);

        if (p != dealer)
        {
            s.Turn = (p + 1) % Seats;
            return ActionResult.Ok;
        }

        if (s.Phase == EuchrePhase.Bid1)
        {
            s.Phase = EuchrePhase.Bid2;
            s.UpcardStatus = EuchreUpcardStatus.TurnedDown;
            s.DeniedSuit = up.Suit;
            s.Turn = (dealer + 1) % Seats;
            s.Log.Add(EventKind.Info,
                $"Everybody passed. The {up.Name} is turned down, so {up.Suit.LowerName()} cannot be named. " +
                $"{Name(s, s.Turn)} may name any other suit, or pass." +
                (s.Config.StickTheDealer ? " The dealer must name a suit if it comes round to them." : ""),
                cards: [up]);
            return ActionResult.Ok;
        }

        // Round two, everybody passed, and the dealer was allowed to.
        s.Phase = EuchrePhase.HandOver;
        s.Result = new EuchreResult
        {
            ThrownIn = true,
            Deltas = [0, 0],
            Label = "thrown in",
            Summary = "Everybody passed twice. The hand is thrown in and nobody scores.",
            Scores = [.. s.Scores],
        };
        s.Log.Add(EventKind.Hand, "Everybody passed twice. The hand is thrown in and nobody scores. " +
            $"{Name(s, (dealer + 1) % Seats)} deals the next one.");
        RecordHand(s);
        return ActionResult.Ok;
    }

    /// <summary>The dealer puts one card back after taking the upcard.</summary>
    internal static ActionResult DoDiscard(EuchreState s, int p, Card card)
    {
        if (s.Players[p].Hand.Count != HandSize + 1)
            return ActionResult.Faulted($"the dealer holds {s.Players[p].Hand.Count} cards, not six");
        s.Players[p].Hand = s.Players[p].Hand.Removing(card);
        s.Discard = card;
        /* Public that it happened, private what it was. Everybody at a real table watches the
         * dealer put a card back and nobody sees which. */
        s.Log.Add(EventKind.Info, $"{Name(s, p)}{Vb(s, p, " discards", " discard")} a card face down.", seat: p);
        s.Log.Add(EventKind.You, $"You put back the {card.Name}.", seat: p, cards: [card], audience: p);
        BeginPlay(s);
        return ActionResult.Ok;
    }

    // MARK: Playing

    public static int TrickWinnerIndex(IReadOnlyList<EuchrePlay> plays, Suit? trump)
    {
        var best = 0;
        if (plays.Count == 0) return 0;
        for (var i = 1; i < plays.Count; i++)
            if (EuchreCards.Beats(plays[i].Card, plays[best].Card, trump)) best = i;
        return best;
    }

    internal static ActionResult DoPlay(EuchreState s, int p, Card card)
    {
        s.Players[p].Hand = s.Players[p].Hand.Removing(card);
        s.Trick.Add(new EuchrePlay(p, card));
        s.Played.Add(card);

        s.Log.Add(EventKind.Play, $"{Name(s, p)}{Vb(s, p, " plays", " play")} the {EuchreCards.Describe(card, s.Trump)}.",
            seat: p, cards: [card]);

        if (s.Trick.Count == ActiveCount(s))
        {
            ResolveTrick(s);
            return ActionResult.Ok;
        }
        s.Turn = NextActive(s, p);
        return ActionResult.Ok;
    }

    internal static void ResolveTrick(EuchreState s)
    {
        var wi = TrickWinnerIndex(s.Trick, s.Trump);
        var winner = s.Trick[wi].Player;
        s.Players[winner].TricksWon += 1;
        var number = s.TrickLog.Count + 1;
        var done = new EuchreTrick(number, [.. s.Trick], winner);
        s.LastTrick = done;
        s.TrickLog.Add(done);

        s.Log.Add(EventKind.Trick, $"{Name(s, winner)}{Vb(s, winner, " takes", " take")} trick {number} with the " +
            $"{EuchreCards.Describe(s.Trick[wi].Card, s.Trump)}.",
            seat: winner, cards: [s.Trick[wi].Card]);

        s.Trick = [];
        if (s.TrickLog.Count == HandSize)
        {
            EndHand(s);
            return;
        }
        s.Leader = winner;
        s.Turn = winner;
    }

    // MARK: Scoring

    /// <summary>
    /// The whole scoring table, written out rather than computed:
    ///
    ///   makers take 3 or 4                        1
    ///   makers take all 5                         2
    ///   makers take all 5, playing alone          4
    ///   makers take 3 or 4, playing alone         1
    ///   makers take fewer than 3 — euchred        2, to the other side
    ///
    /// Returns (to the makers, to the other side).
    /// </summary>
    public static (int Makers, int Others) ScoreTable(int made, bool alone)
    {
        if (made >= 3) return (made == HandSize ? (alone ? 4 : 2) : 1, 0);
        return (0, 2);
    }

    internal static EuchreResult ScoreHand(EuchreState s)
    {
        var maker = s.Maker ?? 0;
        var makerTeam = TeamOf(maker);
        var made = Enumerable.Range(0, Seats).Where(i => TeamOf(i) == makerTeam).Sum(i => s.Players[i].TricksWon);

        List<int> deltas = [0, 0];
        string label;
        var euchred = false;
        var table = ScoreTable(made, s.Alone);
        if (made >= 3)
        {
            deltas[makerTeam] = table.Makers;
            label = made == HandSize
                ? (s.Alone ? "a march, alone — four" : "a march — two")
                : $"made it with {made} — one";
        }
        else
        {
            euchred = true;
            deltas[1 - makerTeam] = table.Others;
            label = "euchred — two to the other side";
        }
        return new EuchreResult
        {
            ThrownIn = false, MakerTeam = makerTeam, Made = made, Euchred = euchred,
            Alone = s.Alone, Trump = s.Trump, Maker = maker, Deltas = deltas, Label = label,
        };
    }

    internal static void EndHand(EuchreState s)
    {
        var r = ScoreHand(s);
        s.Scores[0] += r.Deltas[0];
        s.Scores[1] += r.Deltas[1];

        var target = s.Config.PointsToWin;
        int? winSide = null;
        if (s.Scores[0] >= target && s.Scores[0] > s.Scores[1]) winSide = 0;
        else if (s.Scores[1] >= target && s.Scores[1] > s.Scores[0]) winSide = 1;
        else if (s.Scores[0] >= target && s.Scores[1] >= target)
        {
            /* Both over the line at once cannot happen (only one side scores on a hand), but if
             * it ever does, the higher score wins. */
            winSide = s.Scores[0] >= s.Scores[1] ? 0 : 1;
        }

        var maker = r.Maker ?? 0;
        var makerTeam = r.MakerTeam ?? 0;
        var trumpWord = s.Trump?.LowerName() ?? "trump";
        var summary = $"{Name(s, maker)}{Vb(s, maker, " made", " made")} {trumpWord}" +
            (s.Alone ? " alone" : "") + $" and took {r.Made}" + (r.Made == 1 ? " trick" : " tricks") + ". ";
        if (r.Euchred)
        {
            summary += $"{Name(s, maker)}{Vb(s, maker, " was", " were")} euchred: two points to " +
                $"{SideWords(s, 1 - makerTeam)}.";
        }
        else
        {
            summary += $"{PointWords(r.Deltas[makerTeam])} to {SideWords(s, makerTeam)}.";
        }
        summary += $" Score: {SideWords(s, 0)} {s.Scores[0]}, {SideWords(s, 1)} {s.Scores[1]}.";

        r.Summary = summary;
        r.Scores = [.. s.Scores];
        s.Result = r;
        s.Phase = EuchrePhase.HandOver;

        s.Log.Add(EventKind.Hand, summary, seat: maker);

        if (winSide is { } win)
        {
            s.Phase = EuchrePhase.GameOver;
            s.GameWinner = win;
            s.GamesWon[win] += 1;
            r.GameOver = true;
            r.GameWinner = win;
            s.Log.Add(EventKind.Game,
                $"{SideWords(s, win)} win game {s.GameNumber}, {s.Scores[win]} to {s.Scores[1 - win]}. " +
                $"Games: {SideWords(s, 0)} {s.GamesWon[0]}, {SideWords(s, 1)} {s.GamesWon[1]}. " +
                "Start a new game to play another.");
        }
        RecordHand(s);
    }

    // MARK: The permanent record

    internal static void RecordHand(EuchreState s)
    {
        if (s.Result is not { } result) return;
        var rec = new EuchreHandRecord
        {
            HandNumber = s.HandNumber,
            GameNumber = s.GameNumber,
            Dealer = s.Dealer ?? 0,
            Trump = s.Trump,
            Maker = s.Maker,
            Alone = s.Alone,
            SittingOut = s.SittingOut,
            Upcard = s.Dealt?.Upcard,
            TurnedDown = s.UpcardStatus == EuchreUpcardStatus.TurnedDown,
            Discard = s.Discard,
            Kitty = s.Dealt is { } dk ? [.. dk.Kitty] : [],
            Dealt = s.Dealt is { } dh ? dh.Hands.Select(h => h.ToList()).ToList() : [],
            Tricks = [.. s.TrickLog],
            TricksWon = s.Players.Select(p => p.TricksWon).ToList(),
            Result = result.Copy(),
            Scores = [.. s.Scores],
            Problems = [],
        };
        rec.Problems = AuditHand(rec);
        if (rec.Problems.Count > 0)
        {
            s.Log.Add(EventKind.Info, $"Accounting check failed on hand {rec.HandNumber}: " +
                string.Join("; ", rec.Problems) + ".");
        }
        s.History.Add(rec);
    }

    /// <summary>
    /// Re-derive the hand from the cards rather than trusting the running totals. The point of an
    /// audit is that it must be able to disagree with the thing it is auditing.
    /// </summary>
    public static List<string> AuditHand(EuchreHandRecord rec)
    {
        List<string> bad = [];

        if (rec.Result.ThrownIn)
        {
            if (rec.Tricks.Count > 0) bad.Add($"a thrown-in hand recorded {rec.Tricks.Count} tricks");
            if (rec.Result.Deltas[0] != 0 || rec.Result.Deltas[1] != 0) bad.Add("a thrown-in hand scored");
            return bad;
        }

        if (rec.Tricks.Count != HandSize)
            bad.Add($"recorded {rec.Tricks.Count} tricks instead of {HandSize}");

        var seen = new HashSet<Card>();
        int dup = 0, total = 0;
        var expectPerTrick = rec.SittingOut != null ? Seats - 1 : Seats;
        foreach (var t in rec.Tricks)
        {
            if (t.Plays.Count != expectPerTrick)
                bad.Add($"trick {t.Number} has {t.Plays.Count} cards, expected {expectPerTrick}");
            foreach (var pl in t.Plays)
            {
                total += 1;
                if (!seen.Add(pl.Card)) dup += 1;
                if (pl.Player == rec.SittingOut) bad.Add("a seat that was sitting out played a card");
            }
        }
        if (dup > 0) bad.Add($"{dup} card" + (dup == 1 ? " was" : "s were") + " played more than once");
        if (total != HandSize * expectPerTrick)
            bad.Add($"{total} cards played, expected {HandSize * expectPerTrick}");

        // The deal must be a subset of the deck with nothing repeated.
        var dealtSeen = new HashSet<Card>();
        int dealtDup = 0, dealtCount = 0;
        var deckSet = new HashSet<Card>(EuchreCards.Deck);
        foreach (var h in rec.Dealt)
        {
            foreach (var c in h)
            {
                dealtCount += 1;
                if (!dealtSeen.Add(c)) dealtDup += 1;
                if (!deckSet.Contains(c)) bad.Add($"{c.Id} is not a card in the deck");
            }
        }
        foreach (var c in rec.Kitty)
        {
            dealtCount += 1;
            if (!dealtSeen.Add(c)) dealtDup += 1;
        }
        if (rec.Upcard is { } up)
        {
            dealtCount += 1;
            if (!dealtSeen.Add(up)) dealtDup += 1;
        }
        if (dealtDup > 0) bad.Add($"{dealtDup} card" + (dealtDup == 1 ? "" : "s") + " dealt twice");
        if (dealtCount != EuchreCards.DeckSize)
            bad.Add($"the deal accounts for {dealtCount} cards, not {EuchreCards.DeckSize}");

        // Trick counts must match who actually took them.
        var counted = new int[Seats];
        foreach (var t in rec.Tricks)
            if (t.Winner >= 0 && t.Winner < Seats) counted[t.Winner] += 1;
        for (var i = 0; i < Seats; i++)
        {
            if (i < rec.TricksWon.Count && counted[i] != rec.TricksWon[i])
                bad.Add($"seat {i + 1} is credited with {rec.TricksWon[i]} tricks but took {counted[i]}");
        }
        var sum = counted.Sum();
        if (sum != HandSize) bad.Add($"tricks taken total {sum} instead of {HandSize}");

        // And the score must follow from those tricks, re-derived from the table.
        if (rec.Maker is { } maker)
        {
            var mt = TeamOf(maker);
            var made = Enumerable.Range(0, Seats).Where(i => TeamOf(i) == mt).Sum(i => counted[i]);
            List<int> want = [0, 0];
            var table = ScoreTable(made, rec.Alone);
            if (made >= 3) want[mt] = table.Makers; else want[1 - mt] = table.Others;
            if (!want.SequenceEqual(rec.Result.Deltas))
            {
                bad.Add($"the score change was {string.Join("/", rec.Result.Deltas)} " +
                    $"but the tricks give {string.Join("/", want)}");
            }
            if (want[0] != 0 && want[1] != 0) bad.Add("both sides scored on one hand");
        }
        return bad;
    }
}
