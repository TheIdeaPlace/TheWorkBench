using TheCardPlace.Core;

namespace TheCardPlace.Euchre;

/// <summary>
/// The review sentences: the strings behind the browser game's H, T, L, S, P, O and C buttons,
/// its status line and its turn prompt, ported from <c>ui.js</c> by way of the Swift
/// <c>EuchreReview</c>. Pure functions from state and seat to text; the app reads them out.
///
/// THE ONE EUCHRE-SPECIFIC ACCESSIBILITY DECISION: a sighted euchre player answers "what would my
/// hand be if hearts were trump?" by looking at five cards at once. Read out card by card, the
/// same question is a memory exercise with a colour rule in it. So during the bidding the hand
/// read does the mapping: it says what your trump WOULD be, names the bowers, and counts them. It
/// tells you nothing you could not derive; it just removes a workload that only exists if you
/// cannot see the cards.
/// </summary>
public static class EuchreReview
{
    private const int Seats = EuchreGame.Seats;
    private const int HandSize = EuchreGame.HandSize;

    // MARK: Small pieces

    internal static string SeatName(EuchreState s, int? i)
    {
        if (i is not { } n || n < 0 || n >= s.Players.Count) return "the table";
        return s.Players[n].Name;
    }

    private static readonly string[] CountWords = ["no", "one", "two", "three", "four", "five", "six"];

    internal static string CountWord(int n, string one, string many)
    {
        var w = n >= 0 && n < CountWords.Length ? CountWords[n] : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return w + " " + (n == 1 ? one : many);
    }

    internal static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    internal static int TrickNumber(EuchreState s) => Math.Min(HandSize, s.TrickLog.Count + 1);

    internal static int SideTricks(EuchreState s, int t) =>
        Enumerable.Range(0, Seats).Where(i => EuchreGame.TeamOf(i) == t).Sum(i => s.Players[i].TricksWon);

    internal static bool ValidSeat(EuchreState s, int seat) => seat >= 0 && seat < s.Players.Count;

    /// <summary>Whether this seat is the one the table is waiting on.</summary>
    internal static bool IsMyTurn(EuchreState s, int seat) => s.Phase switch
    {
        EuchrePhase.Bid1 or EuchrePhase.Bid2 or EuchrePhase.Play => s.Turn == seat,
        EuchrePhase.Discard => s.Dealer == seat,
        _ => false,
    };

    /// <summary>The hand, grouped by trump and then by suit, with the bowers named. Every card is read out in full in both groups.</summary>
    public static string GroupedHand(IEnumerable<Card> cards, Suit? trump)
    {
        var sorted = EuchreCards.SortHand(cards, trump);
        var groups = new List<(Suit Key, List<Card> Cards)>();
        foreach (var c in sorted)
        {
            var key = EuchreCards.EffectiveSuit(c, trump);
            if (groups.Count > 0 && groups[^1].Key == key) groups[^1].Cards.Add(c);
            else groups.Add((key, [c]));
        }
        return string.Join(". ", groups.Select(g =>
        {
            var label = trump != null && g.Key == trump ? "Trump" : g.Key.Name();
            return label + ": " + string.Join(", ", g.Cards.Select(c => EuchreCards.Bower(c, trump) switch
            {
                EuchreBower.Right => c.Name + ", the right bower",
                EuchreBower.Left => c.Name + ", the left bower",
                _ => c.Name,
            }));
        }));
    }

    // MARK: H — the hand

    /// <summary>
    /// The hand grouped by trump and suit; during the bidding, what it would be worth with the
    /// suit on offer as trump (round one) or how many trump each available suit would give (round
    /// two).
    /// </summary>
    public static string Hand(EuchreState s, int seat)
    {
        if (!ValidSeat(s, seat)) return "";
        var hand = s.Players[seat].Hand;
        if (hand.Count == 0) return "Your hand is empty.";

        var lead = "";
        /* The dealer's sixth card, called out before anything else: it is the thing they actually
         * have to act on. */
        if (s.Phase == EuchrePhase.Discard && s.Dealer == seat && s.Upcard is { } taken)
            lead = $"You took the {taken.Name}. ";

        if (s.Phase == EuchrePhase.Bid1 && s.Upcard is { } up)
        {
            var t = up.Suit;
            var mine = hand.Where(c => EuchreCards.IsTrump(c, t)).ToList();
            var rest = hand.Where(c => !EuchreCards.IsTrump(c, t)).ToList();
            var msg = $"With {t.LowerName()} as trump you would hold {CountWord(mine.Count, "trump", "trump")}. ";
            if (mine.Count > 0) msg += GroupedHand(mine, t) + ". ";
            msg += rest.Count == 0 ? "Nothing else." : "The rest: " + GroupedHand(rest, t) + ".";
            if (s.Dealer == seat)
                msg += $" You are the dealer, so the {up.Name} would come to you and you would put a card back.";
            else if (EuchreGame.PartnerOf(seat) == s.Dealer)
                msg += $" Your partner is dealing, so the {up.Name} would go to them.";
            else
                msg += $" {SeatName(s, s.Dealer)} is dealing, so the {up.Name} would go to the other side.";
            return msg;
        }

        if (s.Phase == EuchrePhase.Bid2)
        {
            var bySuit = "Your hand: " + GroupedHand(hand, null) + ".";
            List<string> options = [];
            foreach (var su in EuchreCards.Suits)
            {
                if (su == s.DeniedSuit) continue;
                var n = hand.Count(c => EuchreCards.IsTrump(c, su));
                options.Add($"{su.LowerName()} {n}");
            }
            var denied = s.DeniedSuit?.Name() ?? "Nothing";
            return bySuit + $" {denied} cannot be named. Trump you would hold: " + string.Join(", ", options) + ".";
        }

        var @out = lead + $"Your hand, {CountWord(hand.Count, "card", "cards")}. " + GroupedHand(hand, s.Trump) + ".";
        if (s.SittingOut == seat)
            @out += $" These are out of play this hand — {SeatName(s, s.Maker)} is playing alone.";
        return @out;
    }

    // MARK: T — the trick

    /// <summary>"Trump led. Ruth is winning with the Ace of Spades, trump."</summary>
    public static string TrickShort(EuchreState s)
    {
        if (s.Trick.Count == 0) return "Nothing played to this trick yet.";
        var led = EuchreCards.EffectiveSuit(s.Trick[0].Card, s.Trump);
        var wi = EuchreGame.TrickWinnerIndex(s.Trick, s.Trump);
        var w = s.Trick[wi];
        var ledWord = led == s.Trump ? "Trump" : led.Name();
        return $"{ledWord} led. {SeatName(s, w.Player)} is winning with the {EuchreCards.Describe(w.Card, s.Trump)}.";
    }

    public static string Trick(EuchreState s, int seat)
    {
        var head = $"Trick {TrickNumber(s)} of {HandSize}. ";
        if (s.Trick.Count == 0)
            return head + $"Nothing played yet. {SeatName(s, s.Turn)} to lead.";
        var list = string.Join(". ", s.Trick.Select(p => $"{SeatName(s, p.Player)}, {EuchreCards.Describe(p.Card, s.Trump)}"));
        return head + list + ". " + TrickShort(s);
    }

    // MARK: L — the last trick

    public static string LastTrick(EuchreState s, int seat)
    {
        if (s.LastTrick is not { } lt) return "No trick has been completed yet this hand.";
        var list = string.Join(". ", lt.Plays.Select(p => $"{SeatName(s, p.Player)}, {p.Card.Name}"));
        return $"Trick {lt.Number}. " + list + $". {SeatName(s, lt.Winner)} took it.";
    }

    // MARK: S — the score

    /// <summary>Tricks belong to a SIDE in euchre, not to a player, and that is the number that decides the hand.</summary>
    public static string Scores(EuchreState s, int seat)
    {
        if (!ValidSeat(s, seat)) return "";
        int us = EuchreGame.TeamOf(seat), them = 1 - us;
        var target = s.Config.PointsToWin;
        List<string> parts = [];
        parts.Add($"Tricks this hand: you and {SeatName(s, EuchreGame.PartnerOf(seat))} {SideTricks(s, us)}, " +
            $"{EuchreGame.SideWords(s, them)} {SideTricks(s, them)}.");
        parts.Add($"Game {s.GameNumber} to {target}: you {s.Scores[us]}, them {s.Scores[them]}.");
        if (s.GamesWon[0] != 0 || s.GamesWon[1] != 0)
            parts.Add($"Games won: you {s.GamesWon[us]}, them {s.GamesWon[them]}.");
        if (s.Maker is { } maker && s.Phase != EuchrePhase.HandOver && s.Phase != EuchrePhase.GameOver)
        {
            var mt = EuchreGame.TeamOf(maker);
            var need = 3 - SideTricks(s, mt);
            if (mt == us)
            {
                parts.Add(need > 0 ? $"You need {CountWord(need, "more trick", "more tricks")} to make it."
                                   : "You have made it.");
            }
            else
            {
                parts.Add(need > 0
                    ? $"They need {CountWord(need, "more trick", "more tricks")}; " +
                      $"{CountWord(3 - SideTricks(s, us), "more trick", "more tricks")} euchres them."
                    : "They have made it.");
            }
        }
        return string.Join(" ", parts);
    }

    // MARK: P — trump and partners

    /// <summary>
    /// Who has spoken so far in the round now under way. Only passes can appear here: the moment
    /// somebody does anything else the round is over.
    /// </summary>
    internal static string BiddingSoFar(EuchreState s, int round)
    {
        var said = s.BidLog.Where(b => b.Kind == EuchreBidKind.Pass && b.Round == round).ToList();
        if (said.Count == 0) return "Nobody has bid yet this round.";
        var names = said.Select(b => SeatName(s, b.Player));
        return string.Join(", ", names) + (said.Count == 1 ? " has passed." : " have passed.");
    }

    /// <summary>Trump, who made it, who your partner is, who is sitting out.</summary>
    public static string TrumpAndPartner(EuchreState s, int seat)
    {
        if (!ValidSeat(s, seat)) return "";
        var partner = EuchreGame.PartnerOf(seat);
        var @base = $"Your partner is {SeatName(s, partner)}, in seat {partner + 1}. ";

        if (s.Phase == EuchrePhase.Bid1 && s.Upcard is { } up1)
        {
            return @base + $"The upcard is the {up1.Name}, so {up1.Suit.LowerName()} are on offer. " +
                $"{SeatName(s, s.Dealer)} is dealing. " + BiddingSoFar(s, 1);
        }
        if (s.Phase == EuchrePhase.Bid2 && s.Upcard is { } up2)
        {
            return @base + $"The {up2.Name} was turned down, so {s.DeniedSuit?.LowerName() ?? up2.Suit.LowerName()} cannot be named. " +
                $"{SeatName(s, s.Dealer)} is dealing" +
                (s.Config.StickTheDealer ? " and must name a suit if it reaches them" : "") + ". " +
                BiddingSoFar(s, 2);
        }
        if (s.Maker is not { } maker || s.Trump is not { } trump) return @base + "Nobody has made trump yet.";

        var msg = @base + $"{trump.Name()} are trump, made by " +
            (maker == seat ? "you" : SeatName(s, maker)) +
            (EuchreGame.TeamOf(maker) == EuchreGame.TeamOf(seat) ? ", on your side" : ", against you") + ". ";
        msg += $"The right bower is the Jack of {trump.Name()} and the left bower is the Jack of {trump.SameColour().Name()}. ";
        if (s.Alone)
        {
            msg += (maker == seat ? "You are" : SeatName(s, maker) + " is") + " playing alone, so " +
                (s.SittingOut == seat ? "you are" : SeatName(s, s.SittingOut) + " is") + " sitting this hand out. ";
        }
        if (s.Upcard is { } up)
        {
            msg += $"The upcard was the {up.Name}" +
                (s.UpcardStatus == EuchreUpcardStatus.TurnedDown ? ", turned down." : $", taken by {SeatName(s, s.Dealer)}.");
        }
        return msg;
    }

    // MARK: C — cards played

    /// <summary>
    /// WHAT HAS BEEN PLAYED. Not what has not. The browser game used to name the highest trump
    /// still out; it stopped, because working that out is the game, and a button that does it
    /// removes the skill unevenly.
    /// </summary>
    public static string CardsPlayed(EuchreState s, int seat)
    {
        if (s.Upcard is not { } up) return "No hand has been dealt yet, so there is nothing to count.";
        if (s.Trump is not { } trump)
            return $"Trump has not been decided yet, so there is nothing to count. The upcard is the {up.Name}.";
        var down = s.Played.Concat(s.Trick.Select(p => p.Card)).ToList();
        var trumpGone = down.Count(c => EuchreCards.IsTrump(c, trump));

        List<string> parts = [$"Trump played: {trumpGone} of 7."];
        foreach (var su in EuchreCards.Suits)
        {
            if (su == trump) continue;
            var n = down.Count(c => !EuchreCards.IsTrump(c, trump) && c.Suit == su);
            parts.Add($"{su.Name()}: {CountWord(n, "card", "cards")} played.");
        }
        parts.Add($"The upcard was the {up.Name}.");
        return string.Join(" ", parts);
    }

    // MARK: O — play order

    private static readonly string[] PlaceWords = ["", "one place", "two places", "three places"];

    internal static string Places(int k) => k >= 0 && k < PlaceWords.Length ? PlaceWords[k] : $"{k} places";

    /// <summary>The roles a seat may be shown as. One place, so the spoken play order and the players table can never drift apart.</summary>
    public static List<string> RoleTags(EuchreState s, int i, int seat)
    {
        List<string> roles = [];
        if (i == s.Dealer) roles.Add("dealer");
        if (i == s.Maker) roles.Add(s.Alone ? "maker, alone" : "maker");
        if (i == s.SittingOut) roles.Add("sitting out");
        if (i == EuchreGame.PartnerOf(seat)) roles.Add("your partner");
        return roles;
    }

    internal static string RoleSuffix(EuchreState s, int i, int seat)
    {
        var r = RoleTags(s, i, seat);
        return r.Count == 0 ? "" : ", " + string.Join(", ", r);
    }

    /// <summary>Where everyone sits in the running order, and where you are in it.</summary>
    public static string PlayOrder(EuchreState s, int seat)
    {
        if (!ValidSeat(s, seat)) return "";
        List<string> list = [];

        if (s.Phase == EuchrePhase.Bid1 || s.Phase == EuchrePhase.Bid2)
        {
            var round = s.Phase == EuchrePhase.Bid1 ? 1 : 2;
            var decided = new Dictionary<int, string>();
            foreach (var b in s.BidLog)
                if (b.Kind != EuchreBidKind.Pass || b.Round == round)
                    decided[b.Player] = b.Kind == EuchreBidKind.Pass ? "passed" : b.Words;
            var start = ((s.Dealer ?? 0) + 1) % Seats;
            for (var k = 0; k < Seats; k++)
            {
                var i = (start + k) % Seats;
                var st = decided.TryGetValue(i, out var said) ? said : (i == s.Turn ? "deciding now" : "still to decide");
                list.Add($"{k + 1}, {SeatName(s, i)}{RoleSuffix(s, i, seat)}, {st}");
            }
            return "Bidding order, starting to the dealer's left. " + string.Join(". ", list) + ".";
        }

        if (s.Phase != EuchrePhase.Play && s.Phase != EuchrePhase.HandOver && s.Phase != EuchrePhase.GameOver)
        {
            return "Seating order: " + string.Join(", ", Enumerable.Range(0, Seats).Select(i => SeatName(s, i) + RoleSuffix(s, i, seat))) + ".";
        }

        var startSeat = s.Trick.Count > 0 ? s.Trick[0].Player : s.Leader;
        var playedBy = new Dictionary<int, Card>();
        foreach (var t in s.Trick) playedBy[t.Player] = t.Card;

        List<int> order = [];
        var at = startSeat;
        for (var k = 0; k < EuchreGame.ActiveCount(s); k++)
        {
            order.Add(at);
            at = EuchreGame.NextActive(s, at);
        }

        int? youAt = order.IndexOf(seat) is var y0 and >= 0 ? y0 : null;
        int? makerAt = s.Maker is { } mk && order.IndexOf(mk) is var m0 and >= 0 ? m0 : null;
        for (var idx = 0; idx < order.Count; idx++)
        {
            var s2 = order[idx];
            var line = $"{idx + 1}, {SeatName(s, s2)}{RoleSuffix(s, s2, seat)}";
            if (s.Phase == EuchrePhase.Play)
                line += ", " + (playedBy.TryGetValue(s2, out var pc) ? $"played the {pc.Name}" : "to play");
            list.Add(line);
        }

        var msg = "Play order for this trick, starting with the lead. " + string.Join(". ", list) + ".";
        if (s.SittingOut is { } @out)
        {
            msg += " " + (@out == seat ? "You are" : SeatName(s, @out) + " is") +
                " sitting out, so there are only three cards to this trick.";
        }
        if (youAt == 0) msg += " You lead.";
        else if (youAt is { } yl && yl == order.Count - 1) msg += " You play last.";

        if (s.Maker is { } maker && makerAt is { } m && youAt is { } y && maker != seat)
        {
            var delta = m - y;
            msg += delta > 0
                ? $" The maker plays {Places(delta)} after you."
                : $" The maker plays {Places(-delta)} before you.";
        }

        if (s.Phase == EuchrePhase.Play && youAt is { } ya && !playedBy.ContainsKey(seat))
        {
            var after = order.Skip(ya + 1).Select(i => SeatName(s, i)).ToList();
            msg += after.Count == 0
                ? " Nobody plays after you."
                : $" {after.Count}" + (after.Count == 1 ? " player plays" : " players play") +
                  " after you: " + string.Join(", ", after) + ".";
        }
        return msg;
    }

    // MARK: W — who is here

    /// <summary>Who is at the table, which side they are on, and whose move it is.</summary>
    public static string WhoIsHere(EuchreState s, int seat)
    {
        if (!ValidSeat(s, seat)) return "";
        List<string> parts = [$"You are playing against the computer. Your partner is {SeatName(s, EuchreGame.PartnerOf(seat))}."];
        for (var i = 0; i < Seats; i++)
        {
            var p = s.Players[i];
            var who = p.Occupant == Occupant.Human ? (i == seat ? "you" : "a person") : "the computer";
            parts.Add($"Seat {i + 1}, {p.Name}, {who}" +
                (i == EuchreGame.PartnerOf(seat) ? ", your partner" : "") +
                (EuchreGame.TeamOf(i) == EuchreGame.TeamOf(seat) ? "" : ", against you") + ".");
        }
        if (EuchreGame.SeatToAct(s) is { } onMove)
            parts.Add(onMove == seat ? "It is your turn." : $"Waiting for {SeatName(s, onMove)}.");
        else if (s.Phase == EuchrePhase.HandOver)
            parts.Add("The hand is over; the table is waiting for somebody to deal.");
        else if (s.Phase == EuchrePhase.GameOver)
            parts.Add("The game is over.");
        return string.Join(" ", parts);
    }

    // MARK: The hand's result

    /// <summary>One sentence: who won the hand and how.</summary>
    public static string HandResult(EuchreState s, int seat)
    {
        if (!ValidSeat(s, seat) || s.Result is not { } r) return "";
        if (r.ThrownIn)
            return "Everybody passed twice, so the hand was thrown in. Nobody scored.";
        var mySide = EuchreGame.TeamOf(seat);
        var makerSide = r.MakerTeam ?? 0;
        var whoMade = r.Maker == seat ? "You" : SeatName(s, r.Maker);
        var head = whoMade + " made " + (r.Trump?.LowerName() ?? "trump") +
            (r.Alone ? ", alone" : "") + $" and took {CountWord(r.Made, "trick", "tricks")}. ";
        if (r.Euchred)
        {
            return head + (makerSide == mySide
                ? "You were euchred — two points against you."
                : "Euchred — two points to you.");
        }
        return head + Cap(makerSide == mySide
            ? CountWord(r.Deltas[mySide], "point", "points") + " to you."
            : CountWord(r.Deltas[1 - mySide], "point", "points") + " to them.");
    }

    /// <summary>
    /// What was face down, once the hand is over: the upcard, what the dealer put back, and the
    /// three cards nobody ever sees at a real table. Null while a hand is in progress.
    /// </summary>
    public static string? DealReveal(EuchreState s)
    {
        if ((s.Phase != EuchrePhase.HandOver && s.Phase != EuchrePhase.GameOver) || s.Dealt is not { } dealt) return null;
        var up = dealt.Upcard;
        string note;
        if (s.Result?.ThrownIn == true)
        {
            note = $"Nobody took the {up.Name} and nobody named a suit, so the hand was thrown in.";
        }
        else if (s.UpcardStatus == EuchreUpcardStatus.TurnedDown)
        {
            note = $"The {up.Name} was turned down, so {up.Suit.LowerName()} could not be named. " +
                $"{SeatName(s, s.Maker)} named {s.Trump?.LowerName() ?? "trump"} instead.";
        }
        else
        {
            note = $"{SeatName(s, s.Dealer)} took the {up.Name} and put back the " +
                (s.Discard?.Name ?? "card shown") + ".";
        }
        note += $" Upcard: {EuchreCards.Describe(up, s.Trump)}.";
        if (s.Discard is { } d) note += $" Put back: {EuchreCards.Describe(d, s.Trump)}.";
        var kitty = dealt.Kitty.Select(c => EuchreCards.Describe(c, s.Trump)).ToList();
        note += " Kitty: " + (kitty.Count == 0 ? "none" : Prose.List(kitty)) + ".";
        return note;
    }

    // MARK: The status line

    /// <summary>One sentence for the status line.</summary>
    public static string Status(EuchreState s, int seat)
    {
        var mine = IsMyTurn(s, seat);
        switch (s.Phase)
        {
            case EuchrePhase.Idle:
                return "No hand has been dealt yet.";
            case EuchrePhase.Bid1:
                return $"Bidding, round one. The upcard is the {s.Upcard?.Name ?? "upcard"}. " +
                    (mine ? "Your turn: order it up or pass?" : $"Waiting for {SeatName(s, s.Turn)}.");
            case EuchrePhase.Bid2:
                return $"Bidding, round two. {s.DeniedSuit?.Name() ?? "The upcard"} turned down. " +
                    (mine ? "Your turn: name a suit or pass?" : $"Waiting for {SeatName(s, s.Turn)}.");
            case EuchrePhase.Discard:
                return s.Dealer == seat
                    ? "You took the upcard. Put one card back."
                    : $"{SeatName(s, s.Dealer)} took the upcard and is putting a card back.";
            case EuchrePhase.Play:
                return $"{s.Trump?.Name() ?? "Trump"} are trump — trick {TrickNumber(s)} of {HandSize} — " +
                    (s.SittingOut == seat ? "you are sitting out this hand."
                        : mine ? "your turn to play." : $"{SeatName(s, s.Turn)} to play.");
            case EuchrePhase.HandOver:
            {
                var us = ValidSeat(s, seat) ? EuchreGame.TeamOf(seat) : 0;
                return $"Hand {s.HandNumber} complete. Score {s.Scores[us]} to {s.Scores[1 - us]}.";
            }
            default:
            {
                var win = s.GameWinner ?? 0;
                return $"Hand {s.HandNumber} complete. Game over — {EuchreGame.SideWords(s, win)} win, " +
                    $"{s.Scores[win]} to {s.Scores[1 - win]}.";
            }
        }
    }

    /// <summary>
    /// What the game says to a seat when it becomes their move, or when the table is waiting for a
    /// button. Empty when there is nothing to prompt.
    /// </summary>
    public static string TurnPrompt(EuchreState s, int seat)
    {
        if (!ValidSeat(s, seat)) return "";
        switch (s.Phase)
        {
            case EuchrePhase.HandOver:
                return "Choose Deal next hand.";
            case EuchrePhase.GameOver:
                return "Choose Start a new game.";
            case EuchrePhase.Play when s.SittingOut == seat:
                return $"You are sitting out this hand while {SeatName(s, s.Maker)} plays alone.";
        }
        if (!IsMyTurn(s, seat)) return "";
        switch (s.Phase)
        {
            case EuchrePhase.Bid1:
            {
                var up = s.Upcard;
                return $"Your turn to bid. The upcard is the {up?.Name ?? "upcard"}. " +
                    $"Order it up to make {up?.Suit.LowerName() ?? "its suit"} trump, or pass.";
            }
            case EuchrePhase.Bid2:
                return $"Your turn to bid. {s.DeniedSuit?.Name() ?? "The upcard"} were turned down. Name another suit, or pass.";
            case EuchrePhase.Discard:
                return "You took the upcard. Choose one card to put back.";
            case EuchrePhase.Play:
                if (s.Trick.Count == 0) return $"Your lead. Trick {TrickNumber(s)} of {HandSize}.";
                return "Your turn to play. " + TrickShort(s);
            default:
                return "";
        }
    }

    /// <summary>A short hint for the play phase: what to lead or follow.</summary>
    public static string PlayHint(EuchreState s, int seat)
    {
        if (s.Phase != EuchrePhase.Play || s.Trump is not { } trump) return "";
        if (s.Trick.Count == 0) return $"Lead any card from your hand. {trump.Name()} are trump.";
        var led = EuchreCards.EffectiveSuit(s.Trick[0].Card, trump);
        return led == trump
            ? $"Trump was led. Follow with trump if you have any — including the left bower, the Jack of {trump.SameColour().Name()}."
            : $"Follow {led.LowerName()} if you can.";
    }
}
