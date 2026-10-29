using TheCardPlace.Core;
using static System.FormattableString;
using C = TheCardPlace.Spades.SpadesCards;
using G = TheCardPlace.Spades.SpadesGame;

namespace TheCardPlace.Spades;

/// <summary>
/// The review sentences: the strings behind the H/T/L/B/S/C/O/W buttons in
/// <c>spades/js/ui.js</c>, as pure functions of the state and a seat. Wherever the web wording
/// referred to a key or the mouse it has been dropped.
/// </summary>
public static class SpadesReview
{
    internal static string SeatName(SpadesState state, int i) => G.Vb(state, i);

    public static string TeamName(SpadesState state, int team) => G.TeamName(state, team);

    internal static string ListOf(List<string> items) => items.Count == 0 ? "nothing" : Prose.List(items);

    internal static string BidWord(int? b) => b switch
    {
        null => "still to bid",
        0 => "nil",
        _ => Invariant($"{b}"),
    };

    internal static string BagWord(int n) => Prose.Count(n, "bag");

    // MARK: H, the hand

    /// <summary>
    /// The hand, grouped by suit; the trump suit named once, at the end, with the spade count.
    /// "Clubs: Ace, Ten and Four. Diamonds: King. Spades (trump): Queen and Nine. 2 spades."
    /// </summary>
    public static string Hand(SpadesState state, int seat)
    {
        if (seat < 0 || seat >= state.Players.Count) return "Your hand is empty.";
        var hand = state.Players[seat].Hand;
        if (hand.Count == 0) return "Your hand is empty.";
        var parts = new List<string>();
        var spades = 0;
        foreach (var s in C.SuitOrder)
        {
            var ranks = C.SortHand(hand.Where(c => c.Suit == s)).Select(c => c.Rank.Name()).ToList();
            if (ranks.Count == 0) continue;
            if (s == C.Trump) spades = ranks.Count;
            parts.Add(s.Name() + (s == C.Trump ? " (trump)" : "") + ": " + ListOf(ranks));
        }
        return string.Join(". ", parts) + ". "
            + (spades > 0 ? Prose.Count(spades, "spade") + "." : "No spades.");
    }

    // MARK: T, this trick

    public static string Trick(SpadesState state)
    {
        if (state.Trick.Count == 0) return "Nothing has been played to this trick yet.";
        var parts = state.Trick.Select(p => $"{SeatName(state, p.Seat)} played the {p.Card.Name}");
        var led = state.Trick[0].Card.Suit;
        var w = G.TrickWinner(state.Trick)!;
        return string.Join(", ", parts) + $". {led.Name()} were led. {SeatName(state, w.Seat)} is winning it.";
    }

    // MARK: L, the last trick

    public static string LastTrick(SpadesState state)
    {
        if (state.LastTrick is not { } lt) return "No trick has been completed yet.";
        var parts = lt.Cards.Select(p => $"{SeatName(state, p.Seat)} played the {p.Card.Name}");
        return string.Join(", ", parts) + $". {SeatName(state, lt.Winner)} took it.";
    }

    // MARK: S, scores and bags

    public static string Scores(SpadesState state) =>
        Invariant($"{TeamName(state, 0)} {state.Scores[0]} with {BagWord(state.Bags[0])}, ")
        + Invariant($"{TeamName(state, 1)} {state.Scores[1]} with {BagWord(state.Bags[1])}")
        + Invariant($". Playing to {G.Target(state)}.");

    // MARK: B, bids and contract

    /// <summary>
    /// The single most useful sentence in this game: what each pair promised, what they have, and
    /// what is still needed, your own side first. A negative "needs" is said as bags. Any nil in
    /// play is named, and whether it is still intact.
    /// </summary>
    public static string Contract(SpadesState state, int seat)
    {
        if (state.Phase == SpadesPhase.Bidding)
        {
            var said = state.Players.Where(p => p.Bid is not null).ToList();
            if (said.Count == 0) return "Nobody has bid yet.";
            return string.Join(", ", said.Select(p => $"{SeatName(state, p.Index)} {BidWord(p.Bid)}")) + ".";
        }
        if (state.Phase == SpadesPhase.Idle) return "Nobody has bid yet.";

        var lines = new List<string>();
        for (var t = 0; t < G.Teams; t++)
        {
            int contract = G.ContractOf(state, t), took = G.TricksOf(state, t);
            var need = contract - took;
            string tail;
            if (need > 0) tail = Invariant($"{need} more {(need == 1 ? "trick" : "tricks")} needed");
            else if (need == 0) tail = "made it exactly";
            else tail = Invariant($"{-need} over, {BagWord(-need)} so far");
            lines.Add(Invariant($"{TeamName(state, t)} bid {contract}, took {took} — {tail}"));
        }
        // Your own side first.
        if (G.TeamOf(Math.Max(0, seat)) == 1) lines.Reverse();

        var nils = new List<string>();
        foreach (var p in state.Players)
        {
            if (p.Bid != 0) continue;
            nils.Add(p.Tricks == 0
                ? $"{p.Name} bid nil and has not taken a trick."
                : $"{p.Name} bid nil and has taken {Prose.Count(p.Tricks, "trick")}.");
        }
        return string.Join(". ", lines) + "." + (nils.Count == 0 ? "" : " " + string.Join(" ", nils));
    }

    // MARK: C, cards played

    /// <summary>
    /// What has gone, whether spades can be led yet, how many spades are gone, and the highest
    /// card in each suit that this seat has not yet seen: neither in its own hand nor on the
    /// table this hand.
    /// </summary>
    public static string CardsPlayed(SpadesState state, int seat)
    {
        var played = state.TricksPlayed;
        var left = G.HandSize - played;
        var text = Invariant($"{Prose.Count(played, "trick")} played, {left} to go. Spades ")
            + (state.SpadesBroken ? "are broken." : "have not been broken.");

        var gone = state.PlayedThisHand;
        var spadesGone = gone.CountOf(C.Trump);
        text += spadesGone == 0 ? " No spades have gone." : $" {Prose.Count(spadesGone, "spade")} gone.";

        var mine = seat >= 0 && seat < state.Players.Count ? state.Players[seat].Hand : [];
        var seen = new HashSet<Card>(gone.Concat(mine));
        var highest = new List<string>();
        foreach (var s in C.SuitOrder)
        {
            foreach (var r in C.RankOrder)
            {
                var top = new Card(r, s);
                if (seen.Contains(top)) continue;
                highest.Add(top.Name);
                break;
            }
        }
        if (highest.Count > 0) text += " Highest still out: " + Prose.List(highest) + ".";
        return text;
    }

    // MARK: O, play order

    public static string PlayOrder(SpadesState state)
    {
        var from = state.Trick.Count > 0 ? state.Trick[0].Seat : state.Leader;
        var names = Enumerable.Range(0, G.Seats).Select(i => SeatName(state, (from + i) % G.Seats));
        return "Play goes " + string.Join(", then ", names) + ".";
    }

    // MARK: W, who is here

    /// <summary>Who is here, and who is with whom.</summary>
    public static string WhoIsHere(SpadesState state, int seat)
    {
        var myTeam = G.TeamOf(Math.Max(0, seat));
        var parts = new List<string>();
        for (var t = 0; t < G.Teams; t++)
        {
            var members = state.Players.Where(p => p.Team == t);
            parts.Add(string.Join(" and ", members.Select(p => $"{p.Name} ({(p.Occupant == Occupant.Human ? "a person" : "computer")})"))
                + (t == myTeam ? ", your side" : ""));
        }
        return string.Join(". Against: ", parts) + ".";
    }

    // MARK: The status line

    /// <summary>
    /// One sentence for the status line. The contract travels with the turn line during play,
    /// because it is the number the player needs at exactly the moment they are choosing a card.
    /// </summary>
    public static string Status(SpadesState state, int seat)
    {
        var me = Math.Max(0, seat);
        switch (state.Phase)
        {
            case SpadesPhase.Idle:
                return "Ready to start.";
            case SpadesPhase.Bidding:
                if (state.Turn == me)
                {
                    var partner = G.PartnerOf(me);
                    var partnerBid = state.Players[partner].Bid;
                    return "Your bid. How many tricks will you take? "
                        + (partnerBid is not null
                            ? $"{SeatName(state, partner)} bid {BidWord(partnerBid)}."
                            : "Your partner has not bid yet.");
                }
                return $"Waiting for {SeatName(state, state.Turn)} to bid.";
            case SpadesPhase.Play:
            {
                var team = G.TeamOf(me);
                var need = G.ContractOf(state, team) - G.TricksOf(state, team);
                var tail = need > 0 ? Invariant($" You need {need} more.")
                    : need == 0 ? " Your contract is made."
                    : $" {BagWord(-need)} over.";
                if (state.Turn == me)
                    return "Your turn. " + (state.Trick.Count == 0 ? "You lead." : $"{state.Trick[0].Card.Suit.Name()} were led.") + tail;
                return $"Waiting for {SeatName(state, state.Turn)}." + tail;
            }
            case SpadesPhase.HandOver:
                return Invariant($"Hand {state.DealNumber} is over. ") + Scores(state);
            default:
                if (state.Winner is int w)
                    return Invariant($"{TeamName(state, w)} win, {state.Scores[w]} to {state.Scores[1 - w]}.");
                return "The game is over.";
        }
    }
}
