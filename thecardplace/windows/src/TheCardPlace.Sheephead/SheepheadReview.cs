using C = TheCardPlace.Sheephead.SheepheadCards;

namespace TheCardPlace.Sheephead;

/// <summary>
/// The review sentences — the text behind the web game's H, T, L, S, P, C and O buttons and its
/// status line — as pure functions from state and seat to a string. Ported from
/// <c>sheephead-multiplayer/js/ui.js</c> by way of the Swift <c>SheepheadReview</c>.
///
/// Everything here is written from <c>seat</c>'s point of view and reveals only what that seat is
/// entitled to know: its own cards, the table, and the sides once the jack of diamonds has shown.
/// </summary>
public static class SheepheadReview
{
    private static string Name(SheepheadState state, int i) => state.Players[i].Name;

    private static string Inv(int n) => SheepheadGame.Inv(n);

    private static bool InTrick(SheepheadState state, int p) => state.Trick.Any(t => t.Player == p);

    /// <summary>Which trick we are on, counted from the seat's own remaining cards.</summary>
    public static int TrickNumber(SheepheadState state, int seat)
    {
        var d = state.Spec;
        var held = state.Players[seat].Hand.Count;
        return Math.Min(d.Hand, d.Hand - held + (InTrick(state, seat) ? 0 : 1));
    }

    // MARK: Cards

    /// <summary>
    /// What holding the jack of diamonds means for this seat right now. Only ever describes their
    /// own position, never anybody else's.
    /// </summary>
    public static string PartnerCardMeaning(SheepheadState state, int seat)
    {
        if (state.Picker is not { } picker) return "the partner card";
        if (picker == seat) return "the partner card, so you are playing alone";
        return "the partner card, so you are the picker's partner";
    }

    private static bool HoldsPartnerCard(SheepheadState state, int seat) =>
        state.Spec.Partner && state.Players[seat].Hand.Contains(C.PartnerCard);

    /// <summary>
    /// A card in this seat's own hand, as its accessibility label: the plain description, then
    /// "from the blind" for a card just picked up, then what the jack of diamonds means for this seat.
    /// </summary>
    public static string Describe(Card card, SheepheadState state, int seat)
    {
        var label = C.Describe(card);
        if (state.Phase == SheepheadPhase.Bury && state.Picker == seat && state.PickedUp.Contains(card))
            label += ", from the blind";
        if (card == C.PartnerCard && HoldsPartnerCard(state, seat))
            label += ", " + PartnerCardMeaning(state, seat);
        return label;
    }

    // MARK: H — the hand

    public static string Hand(SheepheadState state, int seat, bool verbose = true)
    {
        var hand = C.SortHand(state.Players[seat].Hand);
        if (hand.Count == 0) return "Your hand is empty.";

        // Just after picking, call out what came from the blind before anything else — that is
        // the thing the picker actually needs to know right now.
        var lead = "";
        if (state.Phase == SheepheadPhase.Bury && state.Picker == seat && state.PickedUp.Count > 0)
            lead = "From the blind: " + string.Join(", ", state.PickedUp.Select(c => c.Name)) + ". Then your hand. ";

        var trump = hand.Where(C.IsTrump).ToList();
        var fail = hand.Where(c => !C.IsTrump(c)).ToList();
        var parts = new List<string>
        {
            trump.Count == 0 ? "No trump" : "Trump: " + string.Join(", ", trump.Select(c => c.Name)),
            fail.Count == 0 ? "No non-trump cards" : "Non-trump: " + string.Join(", ", fail.Select(c => c.Name)),
        };

        var msg = lead + $"Your hand, {Prose.Count(hand.Count, "card")}. " + string.Join(". ", parts) + ".";
        if (verbose) msg += $" Worth {Inv(C.SumPoints(hand))} points.";
        if (HoldsPartnerCard(state, seat))
            msg += " You hold the Jack of Diamonds, " + PartnerCardMeaning(state, seat) + ".";
        return msg;
    }

    // MARK: T — the current trick

    /// <summary>"Trump led. Alice is winning with Queen of Clubs. 14 points in the trick so far."</summary>
    public static string TrickShort(SheepheadState state)
    {
        if (state.Trick.Count == 0) return "Nothing played to this trick yet.";
        var led = C.EffectiveSuit(state.Trick[0].Card);
        var w = state.Trick[SheepheadGame.TrickWinnerIndex(state.Trick)];
        var pts = C.SumPoints(state.Trick.Select(t => t.Card));
        return led.Name + " led. " + Name(state, w.Player) + " is winning with " + w.Card.Name + ". "
            + Prose.Count(pts, "point") + " in the trick so far.";
    }

    public static string Trick(SheepheadState state, int seat, bool verbose = true)
    {
        var head = $"Trick {Inv(TrickNumber(state, seat))} of {Inv(state.Spec.Hand)}. ";
        if (state.Trick.Count == 0) return head + "Nothing played yet. " + Name(state, state.Turn) + " to lead.";
        var list = string.Join(". ", state.Trick.Select(t =>
            Name(state, t.Player) + ", " + (verbose ? C.Describe(t.Card) : t.Card.Name)));
        return head + list + ". " + TrickShort(state);
    }

    // MARK: L — the last trick

    public static string LastTrick(SheepheadState state, int seat)
    {
        if (state.LastTrick is not { } lt) return "No trick has been completed yet this hand.";
        var list = string.Join(". ", lt.Plays.Select(p => Name(state, p.Player) + ", " + p.Card.Name));
        return "Last trick. " + list + ". " + Name(state, lt.Winner) + $" took it for {Inv(lt.Points)} points.";
    }

    // MARK: S — scores

    public static string Scores(SheepheadState state, int seat)
    {
        var hand = string.Join(". ", state.Players.Select(p => p.Name + " " + Inv(p.Points) + ", " + Prose.Count(p.TricksWon, "trick")));
        var running = string.Join(", ", state.Players.Select(p => p.Name + " " + Inv(p.Score)));
        var buried = "";
        if (state.Picker == seat && state.Buried.Count > 0)
            buried = $" You buried {Inv(C.SumPoints(state.Buried))} points.";
        return "This hand: " + hand + "." + buried + " Running score: " + running + ".";
    }

    // MARK: P — the picker and the partner

    public static string Picker(SheepheadState state, int seat)
    {
        if (state.Phase == SheepheadPhase.Idle) return "The hand has not been dealt.";
        if (state.Phase == SheepheadPhase.Pick) return "Nobody has picked yet. " + Name(state, state.Turn) + " is deciding.";
        if (state.IsLeaster)
            return "Leaster. There is no picker; everyone plays for themselves and the fewest points wins. You must take at least one trick to be eligible.";
        if (state.Picker is not { } picker) return "No picker yet.";
        var msg = picker == seat ? "You are the picker." : Name(state, picker) + " is the picker.";
        if (!state.Spec.Partner) return msg + $" With {Inv(state.Seats)} players the picker always plays alone.";

        // Once the jack of diamonds has shown, everything is public.
        if (state.PartnerRevealed)
        {
            if (state.Alone) return msg + " The picker is playing alone.";
            var partner = state.Partner!.Value;
            return msg + " " + (partner == seat ? "You are" : Name(state, partner) + " is") + " the partner.";
        }
        // Still hidden. Only tell the player what their own cards entitle them to know.
        // Known bug, kept to match Swift and the browser: during the bury the sides are not yet
        // assigned (Alone is still false), so a picker who holds the jack of diamonds is told
        // "Somebody else holds" it. Fix it in all three together.
        if (picker == seat)
        {
            return msg + (state.Alone
                ? " You have the Jack of Diamonds yourself, so you are playing alone. Nobody else knows that yet."
                : " Somebody else holds the Jack of Diamonds and is your secret partner.");
        }
        if (state.Players[seat].Hand.Contains(C.PartnerCard))
            return msg + " You hold the Jack of Diamonds, so you are the secret partner. Nobody else knows yet.";
        return msg + " The Jack of Diamonds has not been played, so the partner is still unknown — "
            + "and the picker may be holding it and playing alone.";
    }

    // MARK: C — the counting aid

    /// <summary>
    /// What has been played, using only what the player could legitimately track. Naming the
    /// highest trump still out is the skill the game is made of, so this counts and does not name.
    /// </summary>
    public static string CardsPlayed(SheepheadState state, int seat)
    {
        var trumpTotal = C.TrumpOrder.Count(c => !state.Spec.Exclude.Contains(c));
        var trumpPlayed = state.Played.Count(C.IsTrump);
        var parts = new List<string> { $"Trump played: {Inv(trumpPlayed)} of {Inv(trumpTotal)}." };
        foreach (var s in C.FailSuits)
        {
            var played = state.Played.Count(c => !C.IsTrump(c) && c.Suit == s);
            parts.Add(s.Name() + $": {Inv(played)} of 6 played.");
        }
        return string.Join(" ", parts);
    }

    // MARK: O — the play order

    /// <summary>
    /// The roles a seat may be shown as, from this player's point of view. The one place the
    /// disclosure rule lives.
    /// </summary>
    /// <param name="player">The seat being described (Swift's <c>of:</c>).</param>
    /// <param name="seat">The seat doing the asking.</param>
    public static List<string> RoleTags(SheepheadState state, int player, int seat)
    {
        var i = player;
        var roles = new List<string>();
        if (i == state.Dealer) roles.Add("dealer");
        if (i == state.Picker)
        {
            roles.Add("picker");
            if (state.Alone && (state.PartnerRevealed || i == seat)) roles.Add("alone");
        }
        if (!state.IsLeaster && !state.Alone && state.Partner == i && (state.PartnerRevealed || i == seat))
            roles.Add("partner");
        if (state.IsLeaster) roles.Add("leaster");
        return roles;
    }

    private static readonly string[] PlaceWords = ["", "one place", "two places", "three places", "four places", "five places"];

    private static string Places(int k) => k > 0 && k < PlaceWords.Length ? PlaceWords[k] : $"{Inv(k)} places";

    public static string PlayOrder(SheepheadState state, int seat)
    {
        var n = state.Seats;
        var list = new List<string>();

        if (state.Phase == SheepheadPhase.Pick)
        {
            var decided = new Dictionary<int, bool>();
            foreach (var e in state.PickLog) decided[e.Player] = e.Picked;
            var pickStart = ((state.Dealer ?? -1) + 1) % n;
            for (var k = 0; k < n; k++)
            {
                var i = (pickStart + k) % n;
                var tags = RoleTags(state, i, seat);
                string st;
                if (decided.TryGetValue(i, out var d)) st = d ? "picked" : "passed";
                else st = i == state.Turn ? "deciding now" : "still to decide";
                list.Add($"{Inv(k + 1)}, " + Name(state, i) + (tags.Count == 0 ? "" : ", " + string.Join(", ", tags)) + ", " + st);
            }
            return "Picking order, starting to the dealer's left. " + string.Join(". ", list) + ".";
        }

        if (state.Phase != SheepheadPhase.Play && state.Phase != SheepheadPhase.HandOver)
            return "Seating order: " + string.Join(", ", state.Players.Select(p => p.Name)) + ".";

        var startSeat = state.Trick.Count > 0 ? state.Trick[0].Player : state.Leader;
        var playedBy = new Dictionary<int, Card>();
        foreach (var t in state.Trick) playedBy[t.Player] = t.Card;

        int youAt = -1, pickerAt = -1;
        for (var k = 0; k < n; k++)
        {
            var i = (startSeat + k) % n;
            if (i == seat) youAt = k;
            if (i == state.Picker) pickerAt = k;
            var tags = RoleTags(state, i, seat);
            var line = $"{Inv(k + 1)}, " + Name(state, i) + (tags.Count == 0 ? "" : ", " + string.Join(", ", tags));
            if (state.Phase == SheepheadPhase.Play)
                line += ", " + (playedBy.TryGetValue(i, out var c) ? "played " + c.Name : "to play");
            list.Add(line);
        }

        var msg = "Play order for this trick, starting with the lead. " + string.Join(". ", list) + ".";

        if (youAt == 0) msg += " You lead.";
        else if (youAt == n - 1) msg += " You play last.";

        if (!state.IsLeaster && state.Picker is { } picker)
        {
            if (picker == seat)
            {
                msg += " You are the picker.";
            }
            else if (pickerAt >= 0 && youAt >= 0)
            {
                var delta = pickerAt - youAt;
                msg += delta > 0
                    ? " The picker plays " + Places(delta) + " after you."
                    : " The picker plays " + Places(-delta) + " before you.";
            }
        }

        if (state.Phase == SheepheadPhase.Play && youAt >= 0 && !playedBy.ContainsKey(seat))
        {
            var after = new List<string>();
            for (var k = youAt + 1; k < n; k++) after.Add(Name(state, (startSeat + k) % n));
            msg += after.Count == 0
                ? " Nobody plays after you."
                : $" {Inv(after.Count)} " + (after.Count == 1 ? "player plays" : "players play")
                    + " after you: " + string.Join(", ", after) + ".";
        }
        return msg;
    }

    // MARK: The status line

    /// <summary>One sentence for the status line.</summary>
    public static string Status(SheepheadState state, int seat)
    {
        var d = state.Spec;
        return state.Phase switch
        {
            SheepheadPhase.Idle => "Ready to start.",
            SheepheadPhase.Pick => state.Turn == seat
                ? $"Your turn: pick up the blind ({Inv(d.Blind)} cards) or pass?"
                : "Waiting for " + Name(state, state.Turn) + " to pick or pass.",
            SheepheadPhase.Bury => state.Picker == seat
                ? $"You picked. Bury {Inv(d.Blind)} cards."
                : Name(state, state.Picker ?? 0) + " picked and is burying.",
            SheepheadPhase.Play => $"Trick {Inv(TrickNumber(state, seat))} of {Inv(d.Hand)} — "
                + (state.Turn == seat ? "your turn to play." : Name(state, state.Turn) + " to play."),
            _ => $"Hand {Inv(state.HandNumber)} complete.",
        };
    }

    /// <summary>What is asked of this seat right now, as it is announced; empty when nothing is.</summary>
    public static string Prompt(SheepheadState state, int seat)
    {
        var d = state.Spec;
        switch (state.Phase)
        {
            case SheepheadPhase.Idle: return "";
            case SheepheadPhase.HandOver: return "Choose Deal next hand.";
            case SheepheadPhase.Pick:
                if (state.Turn != seat) return "";
                return $"Your turn. Pick up the blind of {Inv(d.Blind)} cards, or pass?";
            case SheepheadPhase.Bury:
                if (state.Picker != seat) return "";
                return $"You picked. Choose {Inv(d.Blind)} cards to bury, then choose Bury.";
            default:
                if (state.Turn != seat) return "";
                if (state.Trick.Count == 0) return $"Your lead. Trick {Inv(TrickNumber(state, seat))} of {Inv(d.Hand)}.";
                return "Your turn to play. " + TrickShort(state);
        }
    }

    // MARK: Hand end

    /// <summary>The outcome in one line.</summary>
    public static string ResultHeadline(SheepheadState state, int seat)
    {
        if (state.Result is not { } r) return "";
        if (r.Leaster && r.Winners.Count > 0)
            return Name(state, r.Winners[0]) + " wins the leaster with the fewest points.";
        if (state.Picker is not { } picker) return "";
        // One player takes a singular verb, two take a plural, and "You" takes the second person
        // either way.
        var youAreThem = picker == seat && Name(state, seat).ToLowerInvariant() == "you";
        if (state.Alone)
        {
            return Name(state, picker)
                + (r.PickerWins ? (youAreThem ? " win" : " wins") : (youAreThem ? " lose" : " loses"))
                + " alone — " + r.Label + ".";
        }
        return Name(state, picker) + " and " + Name(state, state.Partner!.Value)
            + (r.PickerWins ? " win" : " lose") + " — " + r.Label + ".";
    }

    /// <summary>
    /// The blind and the bury, shown card by card once the hand is over. Empty until then: during
    /// play it is nobody's business.
    /// </summary>
    public static string BlindReveal(SheepheadState state)
    {
        if (state.Phase != SheepheadPhase.HandOver || state.Dealt is not { } dealt) return "";
        var blind = dealt.Blind;
        string note;
        if (state.IsLeaster)
        {
            var last = state.TrickLog.Count > 0 ? state.TrickLog[^1] : null;
            note = $"Nobody picked, so the blind was worth {Inv(C.SumPoints(blind))} to "
                + (last is null ? "whoever took the last trick" : Name(state, last.Winner)) + " with the last trick.";
            note += " Blind: " + string.Join(", ", blind.Select(c => c.Name)) + ".";
        }
        else
        {
            note = "The blind, and what " + Name(state, state.Picker ?? 0) + " buried ("
                + $"{Inv(C.SumPoints(state.Buried))} points, counted for the picker's team).";
            note += " Blind: " + string.Join(", ", blind.Select(c => c.Name)) + ".";
            note += " Buried: " + string.Join(", ", state.Buried.Select(c => c.Name)) + ".";
        }
        return note;
    }
}
