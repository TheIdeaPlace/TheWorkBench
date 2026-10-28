using TheCardPlace.Core;

namespace TheCardPlace.Euchre;

/// <summary>The adapter the app's table driver uses. Thin: every member forwards to the static classes.</summary>
public sealed class EuchreEngine : IGameEngine<EuchreState, EuchreAction>
{
    public int Seats(EuchreState state) => EuchreGame.Seats;

    /// <summary>A seat outside the table is nobody's to act for, so it answers Bot and the driver finds nothing to decide.</summary>
    public Occupant OccupantOf(EuchreState state, int seat) =>
        seat >= 0 && seat < state.Players.Count ? state.Players[seat].Occupant : Occupant.Bot;

    public int? SeatToAct(EuchreState state) => EuchreGame.SeatToAct(state);

    public ActionResult Apply(EuchreState state, int seat, EuchreAction action, RandomSource rng) =>
        EuchreGame.ApplyAction(state, seat, action, rng);

    public EuchreAction? Decide(EuchreState state, int seat, RandomSource rng) => EuchreAI.Decide(state, seat, rng);

    public EventLog Log(EuchreState state) => state.Log;

    /// <summary>
    /// The iOS session waits the player's pace only in the play phase. A bid is a sentence, and
    /// the announcer already spaces sentences out; the dealer's discard is bookkeeping.
    /// </summary>
    public bool PausesBefore(EuchreState state, EuchreAction action) =>
        state.Phase == EuchrePhase.Play && action is EuchreAction.Play;
}
