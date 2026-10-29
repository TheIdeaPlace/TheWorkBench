using TheCardPlace.Core;

namespace TheCardPlace.Hearts;

/// <summary>
/// The review sentences — the strings behind the browser's H/T/L/S/P/C/O
/// buttons in `hearts/js/ui.js` — as pure functions from state and seat to a
/// complete sentence. They are what a screen reader user asks for most.
/// </summary>
public static class HeartsReview
{
    private static string Name(HeartsState state, int seat) =>
        seat >= 0 && seat < state.Players.Count ? state.Players[seat].Name : $"seat {seat}";

    private static HeartsPlayer? Player(HeartsState state, int seat) =>
        seat >= 0 && seat < state.Players.Count ? state.Players[seat] : null;

    /// "one club" / "three clubs" / "one heart" / "four hearts".
    private static string SuitCount(int n, Suit suit)
    {
        var word = n == 1 ? suit.LowerName()[..^1] : suit.LowerName();
        return $"{Prose.Number(n)} {word}";
    }

    private static string Capitalised(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// Grouped by suit rather than read as thirteen names in a row, high to
    /// low within each, saying how many of each:
    /// "Three clubs: Ace, Ten and Two. One diamond: King. …"
    public static string Hand(HeartsState state, int seat)
    {
        if (Player(state, seat) is not { } p) return "There is no such seat.";
        if (p.Hand.Count == 0) return "Your hand is empty.";
        var sorted = HeartsCards.SortHand(p.Hand);
        var parts = new List<string>();
        foreach (var suit in HeartsCards.SuitOrder)
        {
            var ranks = sorted.Where(c => c.Suit == suit).Select(c => c.Rank.Name()).ToList();
            if (ranks.Count == 0) continue;
            parts.Add(Capitalised(SuitCount(ranks.Count, suit)) + ": " + Prose.List(ranks) + ".");
        }
        return string.Join(" ", parts);
    }

    /// "North played the Two of Clubs, East played the Ace of Clubs. Clubs was led."
    public static string Trick(HeartsState state, int seat)
    {
        if (state.Trick.Count == 0) return "Nothing has been played to this trick yet.";
        var parts = state.Trick.Select(t => $"{Name(state, t.Seat)} played the {HeartsCards.Name(t.Card)}");
        var led = state.Trick[0].Card.Suit;
        return string.Join(", ", parts) + $". {led.Name()} were led.";
    }

    /// The last completed trick, which is the question a player asks most
    /// often after "what is in my hand".
    public static string LastTrick(HeartsState state, int seat)
    {
        if (state.LastTrick is not { } lt) return "No trick has been completed yet.";
        var parts = lt.Cards.Select(t => $"{Name(state, t.Seat)} played the {HeartsCards.Name(t.Card)}");
        return string.Join(", ", parts) + $". {Name(state, lt.Winner)} took it{HeartsGame.PointsTail(lt.Points)}.";
    }

    /// "North 12, East 0, South 30, West 4. Lowest wins."
    public static string Scores(HeartsState state, int seat) =>
        string.Join(", ", state.Players.Select(p => $"{p.Name} {p.Score}")) + ". Lowest wins.";

    /// Points each player has taken this hand from the tricks won — public
    /// information — and who has the queen.
    public static string PointsSoFar(HeartsState state, int seat)
    {
        var parts = state.Players.Select(p => $"{p.Name} {Prose.Count(p.TakenPoints, "point")}");
        var q = state.Players.FirstOrDefault(p => p.HasQueen);
        var tail = q is not null
            ? $" {q.Name} has the queen of spades."
            : " The queen of spades has not been played.";
        return "This hand: " + string.Join(", ", parts) + "." + tail;
    }

    /// The counting aid: what has gone, and the facts that decide how the rest
    /// of the hand plays — whether hearts are live, whether the queen is still
    /// out, and which of the high hearts have gone.
    public static string CardsPlayed(HeartsState state, int seat)
    {
        var played = state.TricksPlayed;
        var left = HeartsGame.HandSize - played;
        var @out = $"{Prose.Count(played, "trick")} played, {left} to go.";
        @out += " Hearts " + (state.HeartsBroken ? "are broken." : "have not been broken.");

        var gone = new List<Card>();
        foreach (var p in state.Players) gone.AddRange(p.Taken);
        gone.AddRange(state.Trick.Select(t => t.Card));

        @out += " The queen of spades " + (gone.Contains(HeartsCards.QueenOfSpades) ? "has gone." : "is still out.");

        if (gone.Count > 0)
        {
            var counts = HeartsCards.SuitOrder
                .Select(suit => (Suit: suit, N: gone.CountOf(suit)))
                .Where(x => x.N > 0)
                .Select(x => SuitCount(x.N, x.Suit));
            @out += " Played so far: " + Prose.List(counts) + ".";
        }

        Rank[] highHearts = [Rank.Ace, Rank.King, Rank.Queen, Rank.Jack];
        var goneHigh = highHearts.Where(r => gone.Contains(new Card(r, Suit.Hearts))).ToList();
        var outHigh = highHearts.Where(r => !gone.Contains(new Card(r, Suit.Hearts))).ToList();
        if (goneHigh.Count == 0)
        {
            @out += " None of the high hearts have gone.";
        }
        else if (outHigh.Count == 0)
        {
            @out += " All four high hearts have gone.";
        }
        else
        {
            @out += $" Of the high hearts, the {Prose.List(goneHigh.Select(r => r.Name()))} " + (goneHigh.Count == 1 ? "has" : "have")
                + $" gone; the {Prose.List(outHigh.Select(r => r.Name()))} " + (outHigh.Count == 1 ? "is" : "are") + " still out.";
        }
        return @out;
    }

    /// Who plays after whom, from the current leader round.
    public static string PlayOrder(HeartsState state, int seat)
    {
        var from = state.Trick.Count > 0 ? state.Trick[0].Seat : state.Leader;
        var n = state.Players.Count;
        if (n <= 0) return "Nobody is at the table.";
        var names = Enumerable.Range(0, n).Select(i => Name(state, (from + i) % n));
        return "Play goes " + string.Join(", then ", names) + ".";
    }

    /// "North, you. East, computer. …"
    public static string Who(HeartsState state, int seat) =>
        string.Join(" ", state.Players.Select(p =>
            $"{p.Name}, {(p.Index == seat ? "you" : (p.Occupant == Occupant.Human ? "a person" : "computer"))}."));

    /// One sentence for the status line.
    public static string Status(HeartsState state, int seat)
    {
        switch (state.Phase)
        {
            case HeartsPhase.Idle:
                return "Choose Start to deal the first hand.";
            case HeartsPhase.Passing:
                if (seat >= 0 && seat < state.Passing.Count && state.Passing[seat] is not null)
                {
                    var waiting = state.Passing.Count(p => p is null);
                    return $"You have passed. Waiting for {Prose.Count(waiting, "player")}.";
                }
                var dir = state.PassDirection == HeartsPassDirection.Across ? "across" : $"to the {state.PassDirection.RawValue()}";
                return $"Choose three cards to pass {dir}.";
            case HeartsPhase.Play:
                if (state.Turn == seat)
                {
                    if (state.Trick.Count > 0)
                    {
                        var first = state.Trick[0];
                        return $"Your turn. {Name(state, first.Seat)} led the {HeartsCards.Name(first.Card)}.";
                    }
                    return "Your turn. You lead.";
                }
                return $"Waiting for {Name(state, state.Turn)}.";
            case HeartsPhase.HandOver:
                return $"Hand {state.DealNumber} is over. " + Scores(state, seat);
            default:
                if (state.Winner is { } w && Player(state, w) is { } p)
                    return $"{p.Name} wins with {p.Score}.";
                var low = state.Players.Count == 0 ? 0 : state.Players.Min(x => x.Score);
                var tied = state.Players.Where(x => x.Score == low).Select(x => x.Name);
                return $"The game is over. Tied on {low}: " + Prose.List(tied) + ".";
        }
    }
}
