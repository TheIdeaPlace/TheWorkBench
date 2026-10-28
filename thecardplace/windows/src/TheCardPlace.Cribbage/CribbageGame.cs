using System.Globalization;
using TheCardPlace.Core;

namespace TheCardPlace.Cribbage;

public static class CribbageGame
{
    public const int Seats = 2;
    public const int Dealt = 6;
    public const int Kept = 4;
    public const int CribSize = 4;

    public static int Other(int i) => 1 - i;

    public static CribbageState CreateGame(CribbageConfig config)
    {
        var cfg = new CribbageConfig
        {
            Names = [.. config.Names],
            Difficulty = config.Difficulty,
            TargetScore = config.TargetScore,
        };
        while (cfg.Names.Count < Seats) cfg.Names.Add(CribbageConfig.DefaultNames[cfg.Names.Count]);
        var players = Enumerable.Range(0, Seats).Select(i => new CribbagePlayer
        {
            Index = i,
            Name = cfg.Names[i],
            Occupant = i == 0 ? Occupant.Human : Occupant.Bot,
        }).ToList();
        return new CribbageState
        {
            Phase = CribbagePhase.Idle,
            Players = players,
            Config = cfg,
            Log = new EventLog(),
            GamesWon = [0, 0],
            GameNumber = 0,
            IsGameOver = false,
            GameWinner = null,
            Dealer = null,
            HandNumber = 0,
            Turn = 0,
            CutForDeal = null,
            Deck = [],
            Crib = [],
            Discarded = [null, null],
            Starter = null,
            Pile = [],
            RunStart = 0,
            Count = 0,
            GoSaid = [false, false],
            LastPlayer = null,
            CountStage = 0,
            CountResults = [],
            Dealt = null,
            Result = null,
            History = [],
        };
    }

    // MARK: - Words

    internal static string NameOf(CribbageState s, int i) => s.Players[i].Name;

    /// <summary>Keeps messages grammatical when a player is named "You".</summary>
    internal static string Vb(CribbageState s, int i, string third, string second) =>
        s.Players[i].Name.ToLowerInvariant() == "you" ? second : third;

    /// <summary>The possessive: "your", "Ruth’s".</summary>
    internal static string Poss(CribbageState s, int i)
    {
        var n = s.Players[i].Name;
        return n.ToLowerInvariant() == "you" ? "your" : n + "’s";
    }

    public static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    internal static string ScoreLine(CribbageState s) =>
        $"{NameOf(s, 0)} {N(s.Players[0].Score)}, {NameOf(s, 1)} {N(s.Players[1].Score)}.";

    internal static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

    // MARK: - Whose move

    /// <summary>
    /// Exactly the phases ApplyAction accepts a NextHand in. Idle belongs to Start: a table that
    /// has never dealt is started deliberately.
    /// </summary>
    public static bool CanDeal(CribbageState state) =>
        state.Phase is CribbagePhase.RoundOver or CribbagePhase.GameOver;

    /// <summary>
    /// Not simply Turn: during the discard it is the lowest seat still to throw, and between hands
    /// it is nobody.
    /// </summary>
    public static int? SeatToAct(CribbageState state) => state.Phase switch
    {
        CribbagePhase.CutForDeal => state.Turn,
        CribbagePhase.Discard => state.SeatsOutstanding is [var first, ..] ? first : null,
        CribbagePhase.Play or CribbagePhase.Count => state.Turn,
        _ => null,
    };

    // MARK: - The play, as questions

    /// <summary>
    /// The cards in this seat's hand that fit under thirty-one, whether or not it is their turn.
    /// </summary>
    public static List<Card> Playable(CribbageState state, int seat)
    {
        if (seat < 0 || seat >= state.Players.Count) return [];
        return state.Players[seat].Hand.Where(c => state.Count + CribbageCards.Value(c) <= 31).ToList();
    }

    public static bool CanPlay(CribbageState state, int seat) => Playable(state, seat).Count > 0;

    public static List<Card> LegalPlays(CribbageState state, int seat)
    {
        if (state.Phase != CribbagePhase.Play || state.Turn != seat) return [];
        return Playable(state, seat);
    }

    /// <summary>Why a card cannot be played now, in the words of the rule; null if it can.</summary>
    public static string? WhyNot(CribbageState state, int seat, Card card)
    {
        if (state.Phase != CribbagePhase.Play) return "it is not the play";
        if (state.Turn != seat) return "it is not your turn";
        if (seat < 0 || seat >= state.Players.Count) return "that is not a seat at this table";
        if (!state.Players[seat].Hand.Contains(card)) return "that card is not in your hand";
        var to = state.Count + CribbageCards.Value(card);
        if (to > 31)
            return "it would take the count to " + CribbageCards.NumberWord(to) + ", past thirty-one";
        return null;
    }

    /// <summary>What laying <paramref name="card"/> on the current sequence would score, and why.</summary>
    public static PlayScore PointsForPlay(CribbageState state, Card card) =>
        CribbageScoring.PointsForPlay(state.RunCards, state.Count, card);

    // MARK: - The only way in

    /// <summary>
    /// THE ONLY WAY IN. Validates the seat, the phase and the action, and refuses with a reason in
    /// the words of the rule. Never throws on bad input; a refusal leaves the state exactly as it
    /// was.
    /// </summary>
    public static ActionResult ApplyAction(CribbageState state, int seat, CribbageAction action, RandomSource rng)
    {
        if (state.Players is not { Count: Seats } || state.Discarded is not { Count: Seats } || state.GoSaid is not { Count: Seats })
            return ActionResult.Faulted("the game state is not a two-seat cribbage game");
        if (seat < 0 || seat >= state.Players.Count)
            return ActionResult.Refused("not a seat at this table");

        switch (action)
        {
            case CribbageAction.Start:
                if (state.Phase != CribbagePhase.Idle) return ActionResult.Refused("the game has already started");
                state.Phase = CribbagePhase.CutForDeal;
                state.Turn = 0;
                state.Log.Add(EventKind.Info, "Cut for deal. The lower card deals first and takes the first crib.");
                return ActionResult.Ok;

            case CribbageAction.Cut:
                if (state.Phase != CribbagePhase.CutForDeal) return ActionResult.Refused("there is nothing to cut for");
                return DoCut(state, rng);

            case CribbageAction.Discard d:
            {
                if (state.Phase != CribbagePhase.Discard) return ActionResult.Refused("it is not the discard");
                if (state.HasDiscarded(seat)) return ActionResult.Refused("you have already thrown to the crib");
                var cards = d.Cards;
                if (cards is null || cards.Count != 2) return ActionResult.Refused("choose exactly two cards");
                if (cards[0] == cards[1]) return ActionResult.Refused("choose two different cards");
                foreach (var c in cards)
                    if (!state.Players[seat].Hand.Contains(c)) return ActionResult.Refused($"the {c.Name} is not in your hand");
                return DoDiscard(state, seat, cards);
            }

            case CribbageAction.Play p:
            {
                if (state.Phase != CribbagePhase.Play) return ActionResult.Refused("it is not the play");
                if (state.Turn != seat) return ActionResult.Refused("not your turn");
                if (WhyNot(state, seat, p.Card) is { } why) return ActionResult.Refused(why);
                return DoPlay(state, seat, p.Card);
            }

            case CribbageAction.Go:
                if (state.Phase != CribbagePhase.Play) return ActionResult.Refused("it is not the play");
                if (state.Turn != seat) return ActionResult.Refused("not your turn");
                if (CanPlay(state, seat))
                    return ActionResult.Refused("you have a card you can play, so you must play it");
                return DoGo(state, seat);

            case CribbageAction.Next:
                if (state.Phase != CribbagePhase.Count) return ActionResult.Refused("there is nothing to count");
                if (state.Turn != seat) return ActionResult.Refused("it is not your count");
                return DoNext(state);

            case CribbageAction.NextHand:
                if (!CanDeal(state)) return ActionResult.Refused("the hand is not over");
                return NewHand(state, rng);

            case CribbageAction.NewGame:
                if (state.Phase != CribbagePhase.GameOver) return ActionResult.Refused("the game is not over");
                return DoNewGame(state);

            default:
                // Swift's enum is closed; a C# record hierarchy is not, and a null can arrive over
                // the wire. Refuse rather than throw.
                return ActionResult.Refused("that is not a cribbage action");
        }
    }

    // MARK: - Cutting for deal

    private static ActionResult DoCut(CribbageState state, RandomSource rng)
    {
        // Both players cut the SAME deck, as they would at a table.
        var deck = Card.FullDeck.Shuffled(rng);
        Card a = deck[0], b = deck[1];
        state.CutForDeal = new CribbageCut([a, b], false);
        state.Log.Add(EventKind.Deal, NameOf(state, 0) + Vb(state, 0, " cuts", " cut") + " the " + a.Name + ". " +
                      NameOf(state, 1) + Vb(state, 1, " cuts", " cut") + " the " + b.Name + ".",
                      cards: [a, b]);

        // Compared by RUN ORDER, not by counting value: a ten is lower than a jack.
        if (CribbageCards.Order(a) == CribbageCards.Order(b))
        {
            state.CutForDeal = new CribbageCut([a, b], true);
            state.Log.Add(EventKind.Info, "A tie. Cut again.");
            return ActionResult.Ok;
        }
        var dealer = CribbageCards.Order(a) < CribbageCards.Order(b) ? 0 : 1;
        state.Dealer = dealer;
        state.Log.Add(EventKind.Info, NameOf(state, dealer) + Vb(state, dealer, " has", " have") +
                      " the lower card and " + Vb(state, dealer, "deals", "deal") + " first.", seat: dealer);
        return NewHand(state, rng);
    }

    // MARK: - Dealing

    private static ActionResult NewHand(CribbageState state, RandomSource rng)
    {
        if (state.Dealer is not { } dealer) return ActionResult.Faulted("no dealer has been decided");
        if (state.IsGameOver)
        {
            state.Players[0].Score = 0;
            state.Players[1].Score = 0;
            state.IsGameOver = false;
            state.GameWinner = null;
            state.HandNumber = 0;
            state.GameNumber += 1;
            state.Log.Add(EventKind.Info, "A new game. Both scores back to nothing, and " + NameOf(state, dealer) +
                          Vb(state, dealer, " deals", " deal") + ".");
        }
        if (state.GameNumber == 0) state.GameNumber = 1;

        state.HandNumber += 1;
        state.Phase = CribbagePhase.Discard;
        state.Crib = [];
        state.Discarded = [null, null];
        state.Starter = null;
        state.Pile = [];
        state.RunStart = 0;
        state.Count = 0;
        state.GoSaid = [false, false];
        state.LastPlayer = null;
        state.CountStage = 0;
        state.CountResults = [];
        state.Result = null;

        var deck = Card.FullDeck.Shuffled(rng);
        var at = 0;
        for (var i = 0; i < Seats; i++)
        {
            state.Players[i].Hand = [];
            state.Players[i].Kept = [];
            state.Players[i].Played = [];
        }
        // Alternating, one at a time, the way it is dealt at a table.
        for (var n = 0; n < Dealt; n++)
        {
            for (var k = 0; k < Seats; k++)
            {
                var seat = (dealer + 1 + k) % Seats;
                state.Players[seat].Hand.Add(deck[at]);
                at++;
            }
        }
        state.Deck = deck.Skip(at).ToList();
        state.Dealt = new CribbageDeal { Hands = state.Players.Select(p => p.Hand.ToList()).ToList() };

        // Nobody is "on turn" during the discard; Turn names the seat still being waited for, the
        // non-dealer while both are outstanding.
        state.Turn = Other(dealer);

        state.Log.Add(EventKind.Deal, $"Hand {N(state.HandNumber)}. " + NameOf(state, dealer) +
                      Vb(state, dealer, " deals", " deal") + ", so it is " + Poss(state, dealer) +
                      " crib. Both players throw two cards to it.", seat: dealer);
        for (var q = 0; q < Seats; q++)
        {
            var sorted = CribbageCards.SortHand(state.Players[q].Hand);
            state.Log.Add(EventKind.You, "Your six cards: " + CribbageCards.ListNames(sorted) + ".",
                          seat: q, cards: sorted, audience: q);
        }
        return ActionResult.Ok;
    }

    private static ActionResult DoNewGame(CribbageState state)
    {
        state.Players[0].Score = 0;
        state.Players[1].Score = 0;
        for (var i = 0; i < Seats; i++)
        {
            state.Players[i].Hand = [];
            state.Players[i].Kept = [];
            state.Players[i].Played = [];
        }
        state.IsGameOver = false;
        state.GameWinner = null;
        state.HandNumber = 0;
        state.GameNumber += 1;
        state.Phase = CribbagePhase.Idle;
        state.Turn = 0;
        state.Dealer = null;
        state.CutForDeal = null;
        state.Deck = [];
        state.Crib = [];
        state.Discarded = [null, null];
        state.Starter = null;
        state.Pile = [];
        state.RunStart = 0;
        state.Count = 0;
        state.GoSaid = [false, false];
        state.LastPlayer = null;
        state.CountStage = 0;
        state.CountResults = [];
        state.Dealt = null;
        state.Result = null;
        state.Log.Add(EventKind.Info, "A new game. Both scores back to nothing. Cut for deal to begin.");
        return ActionResult.Ok;
    }

    // MARK: - The discard

    private static ActionResult DoDiscard(CribbageState state, int p, List<Card> chosenIn)
    {
        if (state.Dealer is not { } dealer) return ActionResult.Faulted("no dealer has been decided");
        var chosen = chosenIn.ToList();
        state.Players[p].Hand.RemoveAll(chosen.Contains);
        state.Players[p].Kept = state.Players[p].Hand.ToList();
        state.Discarded[p] = chosen;

        state.Log.Add(EventKind.You, "You threw the " + CribbageCards.ListNames(chosen) + " to " +
                      (dealer == p ? "your own crib" : Poss(state, dealer) + " crib") +
                      ". You keep " + CribbageCards.ListNames(CribbageCards.SortHand(state.Players[p].Kept)) + ".",
                      seat: p, cards: chosen, audience: p);
        // Public that it happened, private what it was.
        state.Log.Add(EventKind.Info, NameOf(state, p) + Vb(state, p, " has thrown", " have thrown") +
                      " two cards to the crib.", seat: p);

        if (state.SeatsOutstanding is [var first, ..])
        {
            state.Turn = first;
            return ActionResult.Ok;
        }

        // Both in. Form the crib, turn the starter, pay his heels.
        var crib = new List<Card>();
        for (var s = 0; s < Seats; s++) crib.AddRange(state.Discarded[s] ?? []);
        state.Crib = crib;

        if (state.Deck.Count == 0) return ActionResult.Faulted("the pack is empty at the cut");
        var starter = state.Deck[0];
        state.Deck.RemoveAt(0);
        state.Starter = starter;
        state.Log.Add(EventKind.Deal, "The starter is the " + CribbageCards.Describe(starter) + ".", cards: [starter]);

        if (state.Dealt is { } dealt)
        {
            dealt.Crib = crib.ToList();
            dealt.Starter = starter;
        }

        if (starter.Rank == Rank.Jack)
        {
            Award(state, dealer, 2, "two for his heels");
            if (state.IsGameOver) return ActionResult.Ok;
        }

        state.Phase = CribbagePhase.Play;
        state.Turn = Other(dealer);
        state.Log.Add(EventKind.Info, NameOf(state, state.Turn) + Vb(state, state.Turn, " leads", " lead") +
                      ". The count starts at nothing.", seat: state.Turn);
        return ActionResult.Ok;
    }

    // MARK: - Scoring, and the only way anybody gains a point

    /// <summary>
    /// <paramref name="phrase"/> is the whole of what the points were for, in the words a cribbage
    /// player would use: "two for his heels", "fifteen for two and a pair for two", "one for the go".
    /// </summary>
    private static void Award(CribbageState state, int p, int points, string phrase)
    {
        if (points == 0) return;
        state.Players[p].Score += points;
        state.Log.Add(EventKind.Score, NameOf(state, p) + Vb(state, p, " scores ", " score ") + phrase + ". " + ScoreLine(state), seat: p);
        CheckWinner(state);
    }

    /// <summary>
    /// The game stops THE MOMENT somebody reaches the target, not at the end of the hand. A
    /// non-dealer who pegs out during the play wins before the dealer ever counts.
    /// </summary>
    private static bool CheckWinner(CribbageState state)
    {
        if (state.IsGameOver) return true;
        var target = state.Config.TargetScore;
        for (var i = 0; i < Seats; i++)
        {
            if (state.Players[i].Score < target) continue;
            var loser = Other(i);
            var theirs = state.Players[loser].Score;
            CribbageSkunk? skunk = null;
            if (theirs < target / 2 + 1)
                skunk = theirs < target / 4 + 1 ? CribbageSkunk.DoubleSkunk : CribbageSkunk.Skunk;
            state.IsGameOver = true;
            state.GameWinner = i;
            state.GamesWon[i] += 1;
            state.Phase = CribbagePhase.GameOver;
            state.Result = new CribbageHandResult(true, i, [state.Players[0].Score, state.Players[1].Score],
                                                  skunk, state.CountResults.ToList());
            state.Log.Add(EventKind.Game, NameOf(state, i) + Vb(state, i, " wins", " win") + ", " +
                          $"{N(state.Players[i].Score)} to {N(theirs)}" +
                          (skunk is { } sk ? " — a " + sk.Words() + "." : ".") +
                          " Games: " + NameOf(state, 0) + $" {N(state.GamesWon[0])}, " +
                          NameOf(state, 1) + $" {N(state.GamesWon[1])}. Deal to start another.", seat: i);
            RecordHand(state);
            // THE DEAL PASSES HOWEVER THE HAND ENDED. Rotated after the record so the record names
            // the dealer of the hand it describes.
            if (state.Dealer is { } d)
            {
                state.Dealer = Other(d);
                state.Turn = Other(d);
            }
            return true;
        }
        return false;
    }

    // MARK: - The play

    private static bool HandFinished(CribbageState state) =>
        state.Players[0].Hand.Count == 0 && state.Players[1].Hand.Count == 0;

    private static void ResetCount(CribbageState state, int leader, string why)
    {
        state.Count = 0;
        state.RunStart = state.Pile.Count;
        state.GoSaid = [false, false];
        state.Turn = leader;
        if (!HandFinished(state))
        {
            state.Log.Add(EventKind.Info, why + " The count goes back to nothing, and " + NameOf(state, leader) +
                          Vb(state, leader, " leads", " lead") + ".", seat: leader);
        }
    }

    private static ActionResult DoPlay(CribbageState state, int p, Card card)
    {
        var at = state.Players[p].Hand.IndexOf(card);
        if (at < 0) return ActionResult.Refused("that card is not in your hand");
        state.Players[p].Hand.RemoveAt(at);

        // Worked out BEFORE the card joins the pile; PointsForPlay appends the candidate itself, so
        // scoring after the push would count it twice.
        var got = PointsForPlay(state, card);

        state.Players[p].Played.Add(card);
        state.Pile.Add(new CribbagePilePlay(p, card));
        state.Count += CribbageCards.Value(card);
        state.LastPlayer = p;

        state.Log.Add(EventKind.Play, NameOf(state, p) + Vb(state, p, " plays", " play") + " the " + card.Name +
                      $". The count is {N(state.Count)}.", seat: p, cards: [card]);

        var was31 = state.Count == 31;
        if (got.Total > 0) Award(state, p, got.Total, got.Phrase);
        if (state.IsGameOver) return ActionResult.Ok;

        if (HandFinished(state))
        {
            // The last card of the play is worth one, unless it made exactly thirty-one, already
            // paid two.
            if (!was31) Award(state, p, 1, "one for the last card");
            if (state.IsGameOver) return ActionResult.Ok;
            EndPlay(state);
            return ActionResult.Ok;
        }

        if (was31)
        {
            ResetCount(state, Other(p), "Thirty-one.");
            return ActionResult.Ok;
        }

        // The opponent moves next if they can. If they cannot they will have to say go, which is
        // their action to take.
        state.Turn = Other(p);
        return ActionResult.Ok;
    }

    private static ActionResult DoGo(CribbageState state, int p)
    {
        state.GoSaid[p] = true;
        state.Log.Add(EventKind.Info, NameOf(state, p) + Vb(state, p, " says", " say") + " go.", seat: p);

        var opp = Other(p);
        if (CanPlay(state, opp))
        {
            state.Turn = opp;
            return ActionResult.Ok;
        }

        // Neither can play. The last card laid takes one for the go, unless the count is exactly
        // thirty-one, already paid two.
        if (state.LastPlayer is not { } last) return ActionResult.Faulted("a go with nothing played");
        if (state.Count != 31)
        {
            Award(state, last, 1, "one for the go");
            if (state.IsGameOver) return ActionResult.Ok;
        }
        if (HandFinished(state)) { EndPlay(state); return ActionResult.Ok; }
        // Whoever did NOT lay the last card leads the next sequence.
        ResetCount(state, Other(last), "Neither of you can play.");
        return ActionResult.Ok;
    }

    private static void EndPlay(CribbageState state)
    {
        if (state.Dealer is not { } dealer) return;
        state.Phase = CribbagePhase.Count;
        state.CountStage = 0;
        state.CountResults = [];
        state.Turn = Other(dealer);
        state.Log.Add(EventKind.Info, "The play is over. " + NameOf(state, state.Turn) +
                      Vb(state, state.Turn, " counts", " count") + " first.", seat: state.Turn);
    }

    // MARK: - The count

    /// <summary>Stepped, and each seat counts its own hand, which is what happens at a real table.</summary>
    private static ActionResult DoNext(CribbageState state)
    {
        if (state.Dealer is not { } dealer || state.Starter is not { } starter)
            return ActionResult.Faulted("the count has no starter");
        var nonDealer = Other(dealer);

        bool CountHand(int who)
        {
            var r = CribbageScoring.ScoreHand(state.Players[who].Kept, starter, isCrib: false);
            state.CountResults.Add(new CribbageCountResult(who, CribbageCountKind.Hand, r));
            var cards = CribbageCards.SortHand(state.Players[who].Kept);
            state.Log.Add(EventKind.Count, Cap(Poss(state, who)) + " hand: " + CribbageCards.ListNames(cards) +
                          " with the " + starter.Name + ". " + Cap(r.Spoken) + ".", seat: who, cards: cards);
            Award(state, who, r.Total, CribbageCards.NumberWord(r.Total) + " for the hand");
            return state.IsGameOver;
        }

        if (state.CountStage == 0)
        {
            if (CountHand(nonDealer)) return ActionResult.Ok;
            state.CountStage = 1;
            state.Turn = dealer;
            return ActionResult.Ok;
        }
        if (state.CountStage == 1)
        {
            if (CountHand(dealer)) return ActionResult.Ok;
            state.CountStage = 2;
            state.Turn = dealer;
            return ActionResult.Ok;
        }

        // The crib, which nobody has seen until this moment.
        var c = CribbageScoring.ScoreHand(state.Crib, starter, isCrib: true);
        state.CountResults.Add(new CribbageCountResult(dealer, CribbageCountKind.Crib, c));
        var cribCards = CribbageCards.SortHand(state.Crib);
        state.Log.Add(EventKind.Count, Cap(Poss(state, dealer)) + " crib: " + CribbageCards.ListNames(cribCards) +
                      " with the " + starter.Name + ". " + Cap(c.Spoken) + ".", seat: dealer, cards: cribCards);
        Award(state, dealer, c.Total, CribbageCards.NumberWord(c.Total) + " for the crib");
        if (state.IsGameOver) return ActionResult.Ok;

        state.CountStage = 3;
        state.Phase = CribbagePhase.RoundOver;
        state.Result = new CribbageHandResult(false, null, [state.Players[0].Score, state.Players[1].Score],
                                              null, state.CountResults.ToList());
        // Recorded BEFORE the deal passes, so the record names the dealer of the hand it describes.
        RecordHand(state);
        state.Dealer = Other(dealer);
        state.Turn = Other(dealer);
        state.Log.Add(EventKind.Info, $"Hand {N(state.HandNumber)} complete. " + ScoreLine(state) + " " +
                      NameOf(state, state.Turn) + Vb(state, state.Turn, " deals", " deal") + " next.", seat: state.Turn);
        return ActionResult.Ok;
    }

    // MARK: - The permanent record

    private static void RecordHand(CribbageState state)
    {
        // Copies throughout: the Swift record is a value, and a C# record that shared the state's
        // lists would change under it.
        var rec = new CribbageHandRecord
        {
            HandNumber = state.HandNumber,
            GameNumber = state.GameNumber,
            Dealer = state.Dealer ?? 0,
            Starter = state.Starter,
            Dealt = state.Dealt is { } d
                ? new CribbageDeal { Hands = d.Hands.Select(h => h.ToList()).ToList(), Crib = d.Crib.ToList(), Starter = d.Starter }
                : null,
            Discarded = state.Discarded.Select(x => x?.ToList()).ToList(),
            Kept = state.Players.Select(p => p.Kept.ToList()).ToList(),
            Crib = state.Crib.ToList(),
            Pile = state.Pile.ToList(),
            Counts = state.CountResults.ToList(),
            Scores = [state.Players[0].Score, state.Players[1].Score],
            Result = state.Result,
        };
        rec.Problems = AuditHand(rec);
        if (rec.Problems.Count > 0)
        {
            state.Log.Add(EventKind.Info, $"Accounting check failed on hand {N(rec.HandNumber)}: " +
                          string.Join("; ", rec.Problems) + ".");
        }
        state.History.Add(rec);
    }

    /// <summary>
    /// Re-derive the hand from the cards rather than trusting what was recorded. A hand cut short
    /// by somebody winning is checked for what it did contain rather than for what a complete hand
    /// would.
    /// </summary>
    public static List<string> AuditHand(CribbageHandRecord rec)
    {
        var bad = new List<string>();

        // Every card accounted for exactly once.
        var seen = new HashSet<Card>();
        int dup = 0, count = 0;
        void Note(Card c)
        {
            count++;
            if (!seen.Add(c)) dup++;
        }
        var hands = rec.Dealt?.Hands ?? [];
        foreach (var h in hands) h.ForEach(Note);
        if (rec.Starter is { } st) Note(st);
        if (dup > 0) bad.Add($"{N(dup)} card" + (dup == 1 ? " was" : "s were") + " dealt twice");
        if (hands.Count == Seats)
        {
            for (var k = 0; k < hands.Count; k++)
                if (hands[k].Count != Dealt) bad.Add($"seat {N(k + 1)} was dealt {N(hands[k].Count)} cards");
            if (count != Seats * Dealt + (rec.Starter is null ? 0 : 1))
                bad.Add($"the deal accounts for {N(count)} cards");
        }

        // Kept plus discarded equals dealt, per seat, with nothing invented.
        for (var i = 0; i < Seats; i++)
        {
            if (i >= rec.Discarded.Count || rec.Discarded[i] is not { } thrown) continue;
            var keptCards = i < rec.Kept.Count ? rec.Kept[i] : [];
            if (keptCards.Count != Kept) bad.Add($"seat {N(i + 1)} kept {N(keptCards.Count)} cards");
            if (thrown.Count != 2) bad.Add($"seat {N(i + 1)} threw {N(thrown.Count)} cards");
            var dealtTo = i < hands.Count ? hands[i] : [];
            foreach (var c in keptCards.Concat(thrown))
                if (!dealtTo.Contains(c)) bad.Add($"seat {N(i + 1)} held the {c.Id}, which was not dealt to them");
        }

        // The crib is exactly the four thrown cards.
        if (rec.Discarded.Count >= 2 && rec.Discarded[0] is { } a && rec.Discarded[1] is { } b)
        {
            if (!new HashSet<Card>(a.Concat(b)).SetEquals(rec.Crib)) bad.Add("the crib is not what was thrown to it");
            if (rec.Crib.Count != CribSize) bad.Add($"the crib holds {N(rec.Crib.Count)} cards");
        }

        // No card played twice, and only cards that seat kept.
        var playedSeen = new HashSet<Card>();
        foreach (var e in rec.Pile)
        {
            if (!playedSeen.Add(e.Card)) bad.Add($"the {e.Card.Id} was played twice");
            var k = e.Player >= 0 && e.Player < rec.Kept.Count ? rec.Kept[e.Player] : [];
            if (k.Count > 0 && !k.Contains(e.Card))
                bad.Add($"seat {N(e.Player + 1)} played the {e.Card.Id}, which they did not keep");
        }

        // The count never passed thirty-one, re-added from the cards.
        var running = 0;
        foreach (var e in rec.Pile)
        {
            var v = CribbageCards.Value(e.Card);
            if (running + v > 31) running = 0;
            running += v;
            if (running > 31) bad.Add($"the count reached {N(running)}");
        }

        // Every recorded count re-scores to the same number.
        if (rec.Starter is { } starter)
        {
            foreach (var c in rec.Counts)
            {
                var cards = c.Kind == CribbageCountKind.Crib
                    ? rec.Crib
                    : (c.Who >= 0 && c.Who < rec.Kept.Count ? rec.Kept[c.Who] : []);
                if (cards.Count != Kept) continue;
                var again = CribbageScoring.ScoreHand(cards, starter, c.Kind == CribbageCountKind.Crib).Total;
                if (again != c.Result.Total)
                    bad.Add($"the {c.Kind.RawValue()} for seat {N(c.Who + 1)} was recorded as {N(c.Result.Total)} but re-scores to {N(again)}");
            }
        }

        for (var k = 0; k < rec.Scores.Count; k++)
            if (rec.Scores[k] < 0) bad.Add($"seat {N(k + 1)} has a negative score");
        return bad;
    }
}
