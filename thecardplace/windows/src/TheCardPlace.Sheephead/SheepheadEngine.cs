namespace TheCardPlace.Sheephead;

/// <summary>
/// The adapter the app's table driver uses. Thin: every member forwards to the static classes.
/// Sheephead has no end to the game: hands go on until the player stops, so the driver deals the
/// next hand itself when <see cref="SheepheadGame.CanDeal"/> says it may.
/// </summary>
public sealed class SheepheadEngine : IGameEngine<SheepheadState, SheepheadAction>
{
    public int Seats(SheepheadState state) => state.Seats;

    public Occupant OccupantOf(SheepheadState state, int seat) => state.Players[seat].Occupant;

    public int? SeatToAct(SheepheadState state) => SheepheadGame.SeatToAct(state);

    public ActionResult Apply(SheepheadState state, int seat, SheepheadAction action, RandomSource rng) =>
        SheepheadGame.ApplyAction(state, seat, action, rng);

    public SheepheadAction? Decide(SheepheadState state, int seat, RandomSource rng) => SheepheadAI.Decide(state, seat, rng);

    public EventLog Log(SheepheadState state) => state.Log;

    /// <summary>
    /// The pause is for plays. A computer deciding to pick, pass or bury is a sentence or two and
    /// does not need the table to wait. (The iOS session waits on the pace gate only in the play
    /// phase.)
    /// </summary>
    public bool PausesBefore(SheepheadState state, SheepheadAction action) => state.Phase == SheepheadPhase.Play;
}
