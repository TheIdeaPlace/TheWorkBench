namespace TheCardPlace.Euchre.Tests;

/// <summary>
/// Shared helpers: a config with the web defaults, a driver that plays the table with the AI
/// while asserting every move is legal, and a builder for constructed positions.
/// </summary>
internal static class Support
{
    public static readonly string[] Names = ["You", "Ruth", "Dale", "Marta"];

    public static EuchreConfig Config(Difficulty difficulty = Difficulty.Normal, bool stick = false, bool alone = true,
                                      int points = 10, string[]? names = null) =>
        new()
        {
            Names = [.. names ?? Names],
            Difficulty = difficulty,
            PointsToWin = points,
            StickTheDealer = stick,
            AllowAlone = alone,
        };

    public static Card C(string id) => Card.Parse(id);

    public static List<Card> Cards(params string[] ids) => ids.Select(Card.Parse).ToList();

    /// <summary>
    /// Play the table with the AI until <paramref name="stop"/> is true. Every decision is checked
    /// against LegalPlays before it is applied, and every application must be accepted. Returns
    /// the number of moves made.
    /// </summary>
    public static int Drive(EuchreState s, RandomSource rng, Func<EuchreState, bool> stop)
    {
        var moves = 0;
        while (!stop(s))
        {
            if (EuchreGame.SeatToAct(s) is not { } seat)
            {
                Assert.Fail($"nobody to act at phase {s.Phase}");
                return moves;
            }
            if (EuchreAI.Decide(s, seat, rng) is not { } action)
            {
                Assert.Fail($"the computer had no move for seat {seat} at phase {s.Phase}");
                return moves;
            }
            if (action is EuchreAction.Play play && !EuchreGame.LegalPlays(s, seat).Contains(play.Card))
                Assert.Fail($"seat {seat} chose an illegal card {play.Card.Id}");
            var phase = s.Phase;
            var r = EuchreGame.ApplyAction(s, seat, action, rng);
            Assert.True(r.IsOk, $"the computer's move was refused: {r.Reason ?? "no reason"} ({action} at {phase}, seat {seat})");
            moves++;
            Assert.True(moves <= 400, "a hand never finished");
        }
        return moves;
    }

    public static bool HandIsOver(EuchreState s) => s.Phase is EuchrePhase.HandOver or EuchrePhase.GameOver;

    /// <summary>Deal (Start or NextHand as the phase requires) and play one whole hand.</summary>
    public static void PlayHand(EuchreState s, RandomSource rng)
    {
        EuchreAction deal = s.Phase == EuchrePhase.Idle ? new EuchreAction.Start() : new EuchreAction.NextHand();
        var r = EuchreGame.ApplyAction(s, 0, deal, rng);
        Assert.True(r.IsOk, $"could not deal: {r.Reason}");
        Drive(s, rng, HandIsOver);
    }

    /// <summary>A constructed position. Hands are card ids; the trick is (seat, id).</summary>
    public static EuchreState Position(EuchrePhase phase, int dealer = 3, int turn = 0, int? leader = null,
                                       Suit? trump = null, string[][]? hands = null,
                                       string? upcard = null, EuchreUpcardStatus upcardStatus = EuchreUpcardStatus.None,
                                       Suit? deniedSuit = null, int? maker = null, bool alone = false,
                                       int? sittingOut = null, (int Seat, string Id)[]? trick = null,
                                       EuchreConfig? config = null)
    {
        var s = EuchreGame.CreateGame(config ?? Config());
        s.Phase = phase;
        s.Dealer = dealer;
        s.Turn = turn;
        s.Leader = leader ?? turn;
        s.Trump = trump;
        hands ??= [[], [], [], []];
        for (var i = 0; i < EuchreGame.Seats; i++) s.Players[i].Hand = Cards(hands[i]);
        s.Upcard = upcard is null ? null : C(upcard);
        s.UpcardStatus = upcardStatus;
        s.DeniedSuit = deniedSuit;
        s.Maker = maker;
        s.Alone = alone;
        s.SittingOut = sittingOut;
        s.Trick = (trick ?? []).Select(t => new EuchrePlay(t.Seat, C(t.Id))).ToList();
        s.HandNumber = 1;
        s.GameNumber = 1;
        return s;
    }

    /// <summary>
    /// A deep copy, as Swift's <c>var alt = s</c> is. The event log is left out of the copy and
    /// replaced by an empty one: the AI never reads it, and copying a whole game's log thousands
    /// of times is what would make the hidden-information test slow.
    /// </summary>
    public static EuchreState CopyWithoutLog(EuchreState s)
    {
        var log = s.Log;
        s.Log = new EventLog();
        try { return Snapshot.Clone(s); }
        finally { s.Log = log; }
    }

    /// <summary>
    /// The same position with everything seat <paramref name="keeping"/> may not see replaced by a
    /// different plausible arrangement of the same cards: the other seats' hands, the kitty, and
    /// the dealer's discard (unless the seat is the dealer, whose own discard it is entitled to
    /// remember).
    /// </summary>
    public static EuchreState Scramble(EuchreState s, int keeping, RandomSource rng)
    {
        var p = keeping;
        var alt = CopyWithoutLog(s);
        Card? upTaken = s.UpcardStatus == EuchreUpcardStatus.Taken ? s.Upcard : null;
        List<Card> pool = [];
        for (var i = 0; i < EuchreGame.Seats; i++)
        {
            if (i == p) continue;
            foreach (var c in s.Players[i].Hand)
                if (c != upTaken) pool.Add(c);
        }
        pool.AddRange(s.Kitty);
        var discardMoves = p != s.Dealer && s.Discard != null && s.Discard != upTaken;
        if (discardMoves && s.Discard is { } d) pool.Add(d);
        pool = pool.Shuffled(rng);

        var at = 0;
        for (var i = 0; i < EuchreGame.Seats; i++)
        {
            if (i == p) continue;
            List<Card> fresh = [];
            foreach (var c in s.Players[i].Hand)
            {
                if (c == upTaken) fresh.Add(c);
                else { fresh.Add(pool[at]); at++; }
            }
            alt.Players[i].Hand = fresh;
        }
        alt.Kitty = pool.GetRange(at, s.Kitty.Count);
        at += s.Kitty.Count;
        if (discardMoves) { alt.Discard = pool[at]; at++; }
        return alt;
    }
}
