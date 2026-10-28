using TheCardPlace.Core;

namespace TheCardPlace.Spades;

/// <summary>
/// The adapter the app's table driver uses. Thin: every member forwards to the static classes.
/// </summary>
public sealed class SpadesEngine : IGameEngine<SpadesState, SpadesAction>
{
    public int Seats(SpadesState state) => SpadesGame.Seats;

    /// <summary>
    /// Who decides for a seat. A number that is not a seat at this table answers Human, so a
    /// driver never runs the computer for a chair that does not exist.
    /// </summary>
    public Occupant OccupantOf(SpadesState state, int seat) =>
        seat >= 0 && seat < state.Players.Count ? state.Players[seat].Occupant : Occupant.Human;

    public int? SeatToAct(SpadesState state) => SpadesGame.SeatToAct(state);

    public ActionResult Apply(SpadesState state, int seat, SpadesAction action, RandomSource rng) =>
        SpadesGame.ApplyAction(state, seat, action, rng);

    public SpadesAction? Decide(SpadesState state, int seat, RandomSource rng) => SpadesAI.Decide(state, seat, rng);

    public EventLog Log(SpadesState state) => state.Log;

    /// <summary>
    /// From the iOS <c>SpadesSession.driveBots()</c>: "The pause is between plays. A bid is one
    /// short sentence and goes straight into the announcement queue." So the table waits the
    /// player's pace before a computer's card, and never before a computer's bid.
    /// </summary>
    public bool PausesBefore(SpadesState state, SpadesAction action) => state.Phase == SpadesPhase.Play;
}
