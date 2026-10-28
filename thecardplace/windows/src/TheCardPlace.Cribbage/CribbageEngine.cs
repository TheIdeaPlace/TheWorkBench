using TheCardPlace.Core;

namespace TheCardPlace.Cribbage;

/// <summary>
/// The adapter the app's table driver uses. Thin: every member forwards to the static classes.
/// </summary>
public sealed class CribbageEngine : IGameEngine<CribbageState, CribbageAction>
{
    public int Seats(CribbageState state) => CribbageGame.Seats;

    public Occupant OccupantOf(CribbageState state, int seat) =>
        seat >= 0 && seat < state.Players.Count ? state.Players[seat].Occupant : Occupant.Bot;

    public int? SeatToAct(CribbageState state) => CribbageGame.SeatToAct(state);

    public ActionResult Apply(CribbageState state, int seat, CribbageAction action, RandomSource rng) =>
        CribbageGame.ApplyAction(state, seat, action, rng);

    public CribbageAction? Decide(CribbageState state, int seat, RandomSource rng) => CribbageAI.Decide(state, seat, rng);

    public EventLog Log(CribbageState state) => state.Log;

    /// <summary>
    /// A pause before a play, a go, or a step of the count; the throw to the crib and the cut
    /// happen at once, as they would at a table. (The iOS session waits on its pace gate exactly
    /// when the phase is the play or the count.)
    /// </summary>
    public bool PausesBefore(CribbageState state, CribbageAction action) =>
        state.Phase is CribbagePhase.Play or CribbagePhase.Count;
}
