using TheCardPlace.Core;
using static TheCardPlace.Cribbage.CribbageGame;

namespace TheCardPlace.Cribbage;

/// <summary>
/// The review sentences: what a screen reader user asks for most. A port of the strings behind
/// the H/T/L/S/P/C keys in <c>cribbage-multiplayer/js/ui.js</c>, with the key references dropped.
/// Pure functions from state and seat to text.
///
/// CRIBBAGE IS ARITHMETIC PERFORMED OUT LOUD. A sighted player looks at a pile of cards, a running
/// count and their own hand and knows in about a second which of their cards makes fifteen, which
/// pairs, and which takes the count past thirty-one. Done by ear that is a running sum plus four
/// subtractions, repeated on every turn. So the program does the arithmetic and says it: every
/// card in the hand is labelled with what it is worth, what count it would make, and what it would
/// score. It removes arithmetic, not judgement.
/// </summary>
public static class CribbageReview
{
    internal static string OpponentName(CribbageState s, int seat) => s.Players[Other(seat)].Name;

    /// <summary>"you" for the seat asking, the name for anybody else.</summary>
    internal static string NameFor(CribbageState s, int seat, int? i)
    {
        if (i is not { } x || x < 0 || x >= s.Players.Count) return "nobody";
        return x == seat ? "you" : s.Players[x].Name;
    }

    internal static string Num(int n) => CribbageCards.NumberWord(n);

    internal static readonly string[] StageNames = ["the non-dealer’s hand", "the dealer’s hand", "the crib"];

    /// <summary>
    /// Is this seat the one the table is waiting for? Not simply Turn == seat: during the discard
    /// "my move" means "I have not thrown yet".
    /// </summary>
    public static bool IsMyMove(CribbageState state, int seat) => state.Phase switch
    {
        CribbagePhase.CutForDeal => true,
        CribbagePhase.Discard => !state.HasDiscarded(seat),
        CribbagePhase.Play or CribbagePhase.Count => state.Turn == seat,
        CribbagePhase.RoundOver or CribbagePhase.GameOver => true,
        _ => false,
    };

    // MARK: - Card labels

    /// <summary>
    /// The accessibility label for one card in this seat's hand: "Seven of Clubs, worth seven,
    /// makes twenty-two, and scores a pair for two", or "King of Spades, cannot be played, it would
    /// take the count to thirty-four, past thirty-one", or, when the card is only there for review,
    /// why: "Five of Hearts, for review, Ruth is to play".
    /// </summary>
    public static string CardLabel(CribbageState state, int seat, Card card)
    {
        var label = CribbageCards.Describe(card);
        if (state.Phase == CribbagePhase.Play && state.Turn == seat)
        {
            if (WhyNot(state, seat, card) is { } why) return label + ", cannot be played, " + why;
            var got = PointsForPlay(state, card);
            label += ", worth " + Num(CribbageCards.Value(card)) + ", makes " + Num(got.Count);
            if (got.Total > 0) label += ", and scores " + got.Phrase;
            return label;
        }
        if (state.Phase == CribbagePhase.Discard && !state.HasDiscarded(seat)) return label;
        return label + ", " + IdleReason(state, seat);
    }

    /// <summary>Why a card in the hand cannot be chosen right now.</summary>
    public static string IdleReason(CribbageState state, int seat) => state.Phase switch
    {
        CribbagePhase.CutForDeal => "for review, nothing is dealt yet",
        CribbagePhase.Discard => "for review, your throw is already in",
        CribbagePhase.Play => "for review, " + OpponentName(state, seat) + " is to play",
        CribbagePhase.Count => "for review while the hands are counted",
        _ => "not playable right now",
    };

    // MARK: - The review keys

    /// <summary>
    /// H: your hand. During the play, what each card is worth and would make; otherwise the cards,
    /// whose crib two of them go to, and the starter.
    /// </summary>
    public static string Hand(CribbageState state, int seat)
    {
        var me = state.Players[seat];
        var cards = me.Hand;
        if (cards.Count == 0)
        {
            if (me.Kept.Count > 0)
                return "You have played all four. You kept " + CribbageCards.ListNames(CribbageCards.SortHand(me.Kept)) + ".";
            return "Your hand is empty.";
        }
        var sorted = CribbageCards.SortHand(cards);
        var lead = "Your " + Num(sorted.Count) + (sorted.Count == 1 ? " card: " : " cards: ");

        if (state.Phase == CribbagePhase.Play)
        {
            var parts = sorted.Select(c =>
            {
                var to = state.Count + CribbageCards.Value(c);
                if (to > 31) return c.Name + ", too big to play";
                var s = c.Name + ", worth " + Num(CribbageCards.Value(c)) + ", makes " + Num(to);
                if (state.Turn == seat)
                {
                    var got = PointsForPlay(state, c);
                    if (got.Total > 0) s += ", and scores " + got.Phrase;
                }
                return s;
            });
            return $"The count is {N(state.Count)}. " + lead + string.Join(". ", parts) + ".";
        }

        var msg = lead + string.Join(", ", sorted.Select(CribbageCards.Describe)) + ".";
        if (state.Phase == CribbagePhase.Discard && !state.HasDiscarded(seat) && state.Dealer is { } dealer)
            msg += " Two of them go to " + (dealer == seat ? "your own crib." : OpponentName(state, seat) + "’s crib.");
        if (state.Starter is { } st) msg += " The starter is the " + CribbageCards.Describe(st) + ".";
        return msg;
    }

    /// <summary>T: the play. The count, what is down this run, and on your turn what you could score.</summary>
    public static string Play(CribbageState state, int seat)
    {
        if (state.Phase != CribbagePhase.Play && state.Pile.Count == 0) return "The play has not started yet.";
        var seq = state.Pile.Count > state.RunStart ? state.Pile.Skip(state.RunStart).ToList() : [];
        var head = $"The count is {N(state.Count)}. ";
        if (seq.Count == 0)
        {
            return head + (state.Pile.Count == 0
                ? NameFor(state, seat, state.Turn) + " to lead."
                : "Nothing down since the count reset. " + NameFor(state, seat, state.Turn) + " to lead.");
        }
        var down = string.Join(", then ", seq.Select(e => NameFor(state, seat, e.Player) + " the " + e.Card.Name));
        var msg = head + "Down this run: " + down + ".";

        if (state.Phase == CribbagePhase.Play && state.Turn == seat)
        {
            var legal = LegalPlays(state, seat);
            if (legal.Count == 0)
            {
                msg += " Nothing in your hand fits under thirty-one, so you must say go.";
            }
            else
            {
                var scoring = CribbageCards.SortHand(legal)
                    .Select(c => (Card: c, Got: PointsForPlay(state, c)))
                    .Where(x => x.Got.Total > 0)
                    .Select(x => x.Got.Phrase + " with the " + x.Card.Name)
                    .ToList();
                if (scoring.Count > 0) msg += " You could score " + Prose.List(scoring) + ".";
            }
        }
        return msg;
    }

    /// <summary>The same as <see cref="Play"/>; cribbage has no tricks, and this is the name the other engines use.</summary>
    public static string Trick(CribbageState state, int seat) => Play(state, seat);

    /// <summary>L: the last count that was read out.</summary>
    public static string LastCount(CribbageState state, int seat) =>
        state.Log.EventsFor(seat).LastOrDefault(e => e.Kind == EventKind.Count)?.Text ?? "Nothing has been counted yet.";

    /// <summary>S: both scores, and how far each of you has to go.</summary>
    public static string Scores(CribbageState state, int seat)
    {
        var target = state.Config.TargetScore;
        int me = state.Players[seat].Score, them = state.Players[Other(seat)].Score;
        var opp = OpponentName(state, seat);
        var parts = new List<string> { $"You {N(me)}, {opp} {N(them)}, playing to {N(target)}." };
        parts.Add("You need " + Num(Math.Max(0, target - me)) + " more; " + opp + " needs " + Num(Math.Max(0, target - them)) + ".");
        if (state.GamesWon[0] > 0 || state.GamesWon[1] > 0)
            parts.Add($"Games won: you {N(state.GamesWon[seat])}, {opp} {N(state.GamesWon[Other(seat)])}.");
        return string.Join(" ", parts);
    }

    /// <summary>P: who dealt, whose crib it is, and the starter.</summary>
    public static string DealerAndCrib(CribbageState state, int seat)
    {
        if (state.Dealer is not { } dealer) return "Nobody has dealt yet. Cut for deal to begin.";
        var opp = OpponentName(state, seat);
        var msg = dealer == seat ? "You dealt, so it is your crib." : opp + " dealt, so the crib is theirs.";
        msg += " " + (state.Starter is { } st
            ? "The starter is the " + CribbageCards.Describe(st) + "."
            : "The starter has not been turned yet.");
        if (state.Phase == CribbagePhase.Discard)
        {
            msg += state.HasDiscarded(seat)
                ? " Your two are in the crib; waiting for " + opp + "."
                : " You still have to throw two.";
            if (state.SeatsOutstanding.Count == 0) msg += " Both throws are in.";
        }
        if (state.Phase == CribbagePhase.Count && state.CountStage >= 0 && state.CountStage < StageNames.Length)
            msg += " Counting: " + StageNames[state.CountStage] + " next.";
        if (state.Crib.Count > 0 && (state.Phase == CribbagePhase.RoundOver || state.Phase == CribbagePhase.GameOver || state.CountStage >= 3))
            msg += " The crib was " + CribbageCards.ListNames(CribbageCards.SortHand(state.Crib)) + ".";
        return msg;
    }

    /// <summary>
    /// C: what has been played this hand, in the order it went down, with the count reset marked:
    /// the cards both players watched land and either may recall. Nothing derived from it: working
    /// out what is still out is the whole of cribbage.
    /// </summary>
    public static string CountingAid(CribbageState state, int seat)
    {
        if (state.Pile.Count == 0)
            return state.Phase == CribbagePhase.Play ? "Nothing has been played yet this hand." : "The play has not started yet.";
        var parts = state.Pile.Select((e, i) =>
            (i == state.RunStart && i > 0 ? "the count reset, then " : "") + NameFor(state, seat, e.Player) + " the " + e.Card.Name);
        return "Played this hand: " + string.Join(", ", parts) + $". The count is {N(state.Count)}.";
    }

    public static string CardsPlayed(CribbageState state, int seat) => CountingAid(state, seat);

    /// <summary>O: the order of play this hand: who leads, and who counts first.</summary>
    public static string PlayOrder(CribbageState state, int seat)
    {
        if (state.Dealer is not { } dealer)
            return "Nobody has dealt yet. The lower cut deals first and takes the first crib.";
        var nd = Other(dealer);
        string dealerName = NameFor(state, seat, dealer), leaderName = NameFor(state, seat, nd);
        var dealerPoss = dealer == seat ? "your" : dealerName + "’s";
        return Cap(dealerName) + " dealt, so " + leaderName + (nd == seat ? " lead" : " leads") +
               " the play and " + dealerName + (dealer == seat ? " answer" : " answers") +
               ". At the count, " + leaderName + (nd == seat ? " count" : " counts") + " first, then " +
               dealerName + ", then " + dealerPoss + " crib.";
    }

    /// <summary>The headline between hands: each count's total, or who won.</summary>
    public static string HandSummary(CribbageState state, int seat)
    {
        var opp = OpponentName(state, seat);
        if (state.Phase == CribbagePhase.GameOver && state.Result is { } r && state.GameWinner is { } w)
        {
            return (w == seat ? "You win" : opp + " wins") + $", {N(state.Players[w].Score)} to {N(state.Players[Other(w)].Score)}" +
                   (r.Skunk is { } sk ? " — a " + sk.Words() + "." : ".");
        }
        if (state.Result is not { } res || res.Counts.Count == 0) return "";
        return string.Join(", ", res.Counts.Select(c =>
            (c.Who == seat ? "your " : opp + "’s ") + c.Kind.RawValue() + " " + N(c.Result.Total))) + ".";
    }

    /// <summary>
    /// What this seat should do now, if anything: "Choose two cards to throw to your own crib."
    /// Empty when it is not their move.
    /// </summary>
    public static string Prompt(CribbageState state, int seat)
    {
        var opp = OpponentName(state, seat);
        switch (state.Phase)
        {
            case CribbagePhase.GameOver: return "Start a new game, or deal to play another.";
            case CribbagePhase.RoundOver: return "Deal the next hand.";
            case CribbagePhase.Idle: return "Start the game to cut for deal.";
            case CribbagePhase.CutForDeal: return "Cut for deal. The lower card deals.";
            case CribbagePhase.Discard:
                if (state.HasDiscarded(seat)) return "";
                return "Choose two cards to throw to " + (state.Dealer == seat ? "your own crib." : opp + "’s crib.");
            case CribbagePhase.Count:
                if (state.Turn != seat) return "";
                return state.CountStage == 2 ? "Your crib to count. Turn it over." : "Your hand to count.";
            case CribbagePhase.Play:
                if (state.Turn != seat) return "";
                if (LegalPlays(state, seat).Count == 0) return "You cannot play. Say go.";
                return $"Your turn. The count is {N(state.Count)}.";
            default:
                return "";
        }
    }

    /// <summary>One sentence for the status line.</summary>
    public static string Status(CribbageState state, int seat)
    {
        var opp = OpponentName(state, seat);
        switch (state.Phase)
        {
            case CribbagePhase.Idle:
                return $"Cribbage, playing to {N(state.Config.TargetScore)}. Start the game to cut for deal.";
            case CribbagePhase.CutForDeal:
            {
                var s = "Cut for deal — the lower card deals and takes the first crib.";
                if (state.CutForDeal?.Tie == true) s += " That was a tie; cut again.";
                return s;
            }
            case CribbagePhase.Discard:
                return state.HasDiscarded(seat)
                    ? "Your two are in the crib. Waiting for " + opp + "."
                    : "Throw two cards to " + (state.Dealer == seat ? "your crib." : opp + "’s crib.");
            case CribbagePhase.Play:
                return $"The count is {N(state.Count)} — " + (state.Turn == seat ? "your turn." : opp + " to play.");
            case CribbagePhase.Count:
            {
                var stage = state.CountStage >= 0 && state.CountStage < StageNames.Length ? StageNames[state.CountStage] : "the crib";
                return "Counting: " + stage + (state.Turn == seat ? " — yours to count." : " — " + opp + " is counting.");
            }
            case CribbagePhase.RoundOver:
                return $"Hand {N(state.HandNumber)} complete. You {N(state.Players[seat].Score)}, {opp} {N(state.Players[Other(seat)].Score)}.";
            case CribbagePhase.GameOver:
                if (state.GameWinner is not { } w) return "Game over.";
                return (w == seat ? "You win" : opp + " wins") + $", {N(state.Players[w].Score)} to {N(state.Players[Other(w)].Score)}.";
            default:
                return "";
        }
    }
}
