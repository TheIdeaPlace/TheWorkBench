using TheCardPlace.Core;

namespace TheCardPlace.Play;

/// <summary>
/// Who decides for a chair. The engine only knows person or computer; the session also knows
/// where the person is. Today every human seat is <see cref="Local"/>. A <see cref="Remote"/> seat
/// is one whose actions arrive from another computer through <see cref="GameSession{TState,TAction}.ReceiveRemote"/>;
/// the table waits for it exactly as it waits for the local player.
/// </summary>
public enum SeatController { Local, Computer, Remote }

/// <summary>
/// What the game screen draws, for any of the five games. The screen knows nothing about the
/// rules: it shows the status, the seats, the hand, the scores and the controls a session offers,
/// and passes key presses back.
/// </summary>
public abstract class GameSession : IDisposable
{
    protected GameSession(GameKind kind, AppSettings settings, Announcer announcer)
    {
        Kind = kind;
        Settings = settings;
        Announcer = announcer;
        Gate.Changed += RaiseChanged;
    }

    public GameKind Kind { get; }
    public AppSettings Settings { get; }
    public Announcer Announcer { get; }
    public PaceGate Gate { get; } = new();
    public Pace Pace => Settings.For(Kind).Pace;

    /// <summary>The log, newest first. Every engine event the player may hear, spoken or not.</summary>
    public IReadOnlyList<string> Log => _log;
    private readonly List<string> _log = [];

    /// <summary>Something the screen should redraw from.</summary>
    public event Action? Changed;

    /// <summary>The card the player's focus should move to, by id. Raised when it becomes their turn.</summary>
    public event Action<string>? FocusCardRequested;

    /// <summary>Focus should move to the phase's choices (a bid, a pick). Raised when it becomes the player's turn to choose.</summary>
    public event Action? FocusChoicesRequested;

    public abstract string Status { get; }
    public abstract IReadOnlyList<HandCard> Hand { get; }
    /// <summary>The most cards the hand holds in this game, so the hand area keeps its size.</summary>
    public abstract int HandCapacity { get; }
    public abstract IReadOnlyList<SeatInfo> Seats { get; }
    /// <summary>A line for the middle of the table: trump, the contract, the count. "" for none.</summary>
    public virtual string TableNote => "";
    public abstract ScoreTable Scores { get; }

    /// <summary>
    /// Lines for the "On the table" list: the last trick, the play so far, cards turned up at the
    /// end of a hand. Text a sighted player sees on the table and would otherwise only be heard once.
    /// </summary>
    public virtual IReadOnlyList<string> TableLines => [];
    public virtual ScoreTable History => ScoreTable.Empty("Hands played");

    /// <summary>
    /// The action that moves the game on right now: Pass, Deal, Next. When there is nothing to
    /// press, a dimmed control that says what the game is waiting for and reads the status.
    /// While the pace gate is paused the screen shows Continue in its place.
    /// </summary>
    public abstract GameControl Primary { get; }
    public virtual IReadOnlyList<GameControl> Secondary => [];
    public abstract IReadOnlyList<ReviewItem> Reviews { get; }

    /// <summary>Enter or Space on a card: play it, or choose it for a pass, a discard, a bury.</summary>
    public abstract void Activate(HandCard card);

    public abstract void NewGame();

    /// <summary>Deals the first hand and lets the computer players go. Called once the screen is up.</summary>
    public abstract void Start();

    /// <summary>True while a game is under way and leaving would lose it.</summary>
    public abstract bool InProgress { get; }

    /// <summary>
    /// The N key, as in the browser games: Continue during a pause, or the primary when it is one
    /// that moves the game on (cut, deal, next, go) and says so with its N key. A bid, a pass or
    /// a bury is never made by N; for anything else N reads the status.
    /// </summary>
    public void Advance()
    {
        if (Gate.IsWaiting)
        {
            Gate.Continue();
            return;
        }
        var p = Primary;
        if (p.Enabled && p.Key == 'n') p.Action();
        else Announcer.Request(Status);
    }

    public GameControl ContinueControl => new("Continue", Gate.Continue, "continue", 'n');

    public virtual void Stop() => Gate.Cancel();

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    // ---- for the games ----

    protected void RaiseChanged() => Changed?.Invoke();

    protected void RequestFocus(string? cardId)
    {
        if (cardId is not null && Settings.MoveFocusOnTurn) FocusCardRequested?.Invoke(cardId);
    }

    protected void RequestFocusChoices()
    {
        if (Settings.MoveFocusOnTurn) FocusChoicesRequested?.Invoke();
    }

    /// <summary>A log line, worded for the ear: the log is a list a screen reader reads.</summary>
    protected void AddLog(string text) => _log.Insert(0, SpokenText.Plain(text));

    protected void ClearLog() => _log.Clear();

    /// <summary>A dimmed primary for a phase with nothing to press. Pressing it reads the status.</summary>
    protected GameControl Idle(string title, string hint = "") =>
        new(title, () => Announcer.Request(Status), "idle", enabled: false, hint: hint);

    protected GameControl Waiting(string name) => Idle($"Waiting for {name}");

    protected GameControl PlayACard => Idle("Play a card", "Choose a card from your hand");
}

/// <summary>
/// The table driver every game shares. It holds the engine state, sends every action through the
/// engine's seat-checked gate, turns new events into log lines and announcements, and runs the
/// computer players at the chosen pace. The games add only what they draw and what their keys do.
/// </summary>
public abstract class GameSession<TState, TAction> : GameSession
    where TState : class
    where TAction : class
{
    private readonly RandomSource _rng;
    private int _lastEventId;
    private CancellationTokenSource? _run;

    protected GameSession(GameKind kind, AppSettings settings, Announcer announcer, IGameEngine<TState, TAction> engine, RandomSource? rng = null)
        : base(kind, settings, announcer)
    {
        Engine = engine;
        _rng = rng ?? new RandomSource();
        State = CreateState();
        Controllers = Enumerable.Range(0, engine.Seats(State)).Select(i => i == Me ? SeatController.Local : SeatController.Computer).ToArray();
    }

    protected IGameEngine<TState, TAction> Engine { get; }
    public TState State { get; protected set; }

    /// <summary>The chair at this computer. Seat 0 for now; a networked table may seat the player elsewhere.</summary>
    public int Me { get; protected init; }

    /// <summary>Who decides for each chair.</summary>
    public SeatController[] Controllers { get; protected set; }

    /// <summary>The computer players' run, for tests to wait on.</summary>
    public Task BotsTask { get; private set; } = Task.CompletedTask;

    /// <summary>A new state from the current settings.</summary>
    protected abstract TState CreateState();

    protected abstract TAction StartAction { get; }

    /// <summary>A seat's name, for messages the table itself writes.</summary>
    protected virtual string SeatName(int seat) => $"seat {seat + 1}";

    public bool IsMyTurn => Engine.SeatToAct(State) == Me;

    public override void Start()
    {
        _lastEventId = 0;
        Apply(StartAction);
        RunBots();
    }

    public override void NewGame()
    {
        CancelRun();
        OnNewGame();
        // The engine rebuilds from its config, so the config is brought up to date first: rules
        // changed in the hub take effect here. Its log starts again at one.
        State = CreateState();
        _lastEventId = 0;
        AddLog("New game.");
        Apply(StartAction);
        RunBots();
    }

    /// <summary>Clears whatever the game keeps between moves (cards chosen for a pass).</summary>
    protected virtual void OnNewGame() { }

    public override void Stop()
    {
        CancelRun();
        base.Stop();
    }

    private void CancelRun()
    {
        _run?.Cancel();
        _run?.Dispose();
        _run = null;
        Gate.Cancel();
    }

    /// <summary>
    /// The player's action. A refusal is spoken in the rule's words and nothing changes.
    /// On success the new events are spoken and the computer players take their turns.
    /// </summary>
    protected bool Act(TAction action)
    {
        if (!Apply(action)) return false;
        RunBots();
        return true;
    }

    /// <summary>Applies an action for the local seat without starting the computer players.</summary>
    protected bool Apply(TAction action, int? seat = null)
    {
        ActionResult r;
        try
        {
            r = Engine.Apply(State, seat ?? Me, action, _rng);
        }
        catch (Exception ex)
        {
            Announcer.Error("Something went wrong in the game: " + ex.Message);
            return false;
        }
        if (!r.IsOk)
        {
            Announcer.Error(Prose.AsSentence(r.Reason ?? "That was refused"));
            RaiseChanged();
            return false;
        }
        Speak(Drain());
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// An action from a player at another computer. Accepted only for a seat this table says is
    /// remote, and only through the same gate as everyone else's.
    /// </summary>
    public ActionResult ReceiveRemote(int seat, TAction action)
    {
        if (seat < 0 || seat >= Controllers.Length || Controllers[seat] != SeatController.Remote)
            return ActionResult.Refused("that seat is not played from another computer");
        var r = Engine.Apply(State, seat, action, _rng);
        if (r.IsOk)
        {
            Speak(Drain());
            RaiseChanged();
            RunBots();
        }
        return r;
    }

    /// <summary>The events since the last drain, into the log, with the category each is heard under.</summary>
    private List<(string Text, AnnouncementCategory Category)> Drain()
    {
        var fresh = Engine.Log(State).EventsFor(Me, _lastEventId);
        if (fresh.Count == 0) return [];
        _lastEventId = fresh[^1].Id;
        foreach (var e in fresh) AddLog(e.Text);
        return fresh.Select(e => (e.Text, AnnouncementCategories.ForEvent(e, Me))).ToList();
    }

    private void Speak(List<(string, AnnouncementCategory)> parts)
    {
        if (parts.Count > 0) Announcer.SayBatch(parts);
    }

    protected void RunBots()
    {
        _run?.Cancel();
        _run?.Dispose();
        var run = new CancellationTokenSource();
        _run = run;
        BotsTask = DriveBots(run.Token);
    }

    private async Task DriveBots(CancellationToken token)
    {
        var held = new List<(string, AnnouncementCategory)>();
        // A run of plays is gathered into one message when there is no time between them to say
        // each, or when the player asked for that, but never at Wait for me, where every press of
        // Continue must say what it did.
        var batching = Pace == Pace.Immediate || (!Settings.SpeakEveryPlay && Pace != Pace.WaitForMe);
        try
        {
            while (!token.IsCancellationRequested
                   && Engine.SeatToAct(State) is int seat
                   && Controllers[seat] == SeatController.Computer)
            {
                var action = Engine.Decide(State, seat, _rng);
                if (action is null)
                {
                    Announcer.Error($"The computer could not find a move for {SeatName(seat)}. Start a new game with Ctrl+N.");
                    break;
                }
                // Held plays wait for the end of the run: that is what gathering them means.
                if (Engine.PausesBefore(State, action))
                {
                    await Gate.WaitAsync(Pace, token);
                    if (token.IsCancellationRequested) return;
                }
                var r = Engine.Apply(State, seat, action, _rng);
                if (!r.IsOk)
                {
                    Announcer.Error($"The computer's move was refused: {r.Reason ?? "no reason given"}.");
                    break;
                }
                var parts = Drain();
                if (!batching) Speak(parts);
                else
                {
                    held.AddRange(parts);
                    // Gathered plays are spoken a trick at a time at the timed paces, so a hand
                    // the player sits out (a partner alone in euchre) is not silent until it ends.
                    if (Pace != Pace.Immediate && parts.Any(p => p.Category == AnnouncementCategory.Scoring))
                    {
                        Speak(held);
                        held = [];
                    }
                }
                RaiseChanged();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Announcer.Error("Something went wrong in the game: " + ex.Message);
        }
        if (token.IsCancellationRequested) return;
        Speak(held);
        RaiseChanged();
        TurnCameToMe();
    }

    /// <summary>Says it is the player's turn and moves focus to the first card they can play.</summary>
    protected virtual void TurnCameToMe()
    {
        if (!IsMyTurn) return;
        Announcer.Say(Status, AnnouncementCategory.Turn);
        var first = Hand.FirstOrDefault(c => c.Playable) ?? Hand.FirstOrDefault();
        RequestFocus(first?.Id);
    }

    /// <summary>"You" for the player's own chair, the seat's name otherwise, for the On the table lines.</summary>
    private string Who(int seat) => seat == Me ? "You" : SeatName(seat);

    /// <summary>
    /// The cards on the table now, one line each in the order they were played, so the On the
    /// table list can be arrowed card by card: "Ruth led the Queen of Clubs, trump, 3 points",
    /// "You played the Ace of Diamonds, winning so far".
    /// </summary>
    protected IEnumerable<string> TrickLines(IReadOnlyList<(int Seat, Card Card)> plays, Func<Card, string> describe, int? winning)
    {
        for (var i = 0; i < plays.Count; i++)
        {
            var (seat, card) = plays[i];
            var line = $"{Who(seat)} {(i == 0 ? "led" : "played")} the {describe(card)}";
            if (winning == seat && plays.Count > 1) line += ", winning so far";
            yield return line;
        }
    }

    /// <summary>
    /// The last completed trick: who took it first, so the first line is the answer, then one
    /// line per card in the order played.
    /// </summary>
    protected IEnumerable<string> LastTrickLines(IReadOnlyList<(int Seat, Card Card)> plays, int winner, string tail, Func<Card, string> describe)
    {
        if (plays.Count == 0) yield break;
        yield return $"Last trick: {Who(winner)} took it{tail}";
        foreach (var (seat, card) in plays) yield return $"{Who(seat)}, {describe(card)}";
    }

    /// <summary>Position words for a seat seen from the player's chair, in play order.</summary>
    protected string PositionOf(int seat)
    {
        var n = Controllers.Length;
        var rel = ((seat - Me) % n + n) % n;
        if (rel == 0) return "";
        if (n == 2) return "across";
        if (n == 4) return rel switch { 1 => "left", 2 => "across", _ => "right" };
        return rel == 1 ? "left" : rel == n - 1 ? "right" : $"{Prose.Ordinal(rel)} to your left";
    }
}
