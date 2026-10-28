using TheCardPlace.Core;

namespace TheCardPlace.Hearts;

/// <summary>
/// The adapter the app's table driver uses. Thin: every member forwards to the static classes.
/// </summary>
public sealed class HeartsEngine : IGameEngine<HeartsState, HeartsAction>
{
    public int Seats(HeartsState state) => HeartsGame.Seats;

    /// A seat nobody sits in answers Bot: the driver then asks Decide, which has nothing for it,
    /// rather than waiting for a person who is not there.
    public Occupant OccupantOf(HeartsState state, int seat) =>
        seat >= 0 && seat < state.Players.Count ? state.Players[seat].Occupant : Occupant.Bot;

    public int? SeatToAct(HeartsState state) => HeartsGame.SeatToAct(state);

    public ActionResult Apply(HeartsState state, int seat, HeartsAction action, RandomSource rng) =>
        HeartsGame.ApplyAction(state, seat, action, rng);

    public HeartsAction? Decide(HeartsState state, int seat, RandomSource rng) => HeartsAI.Decide(state, seat, rng);

    public EventLog Log(HeartsState state) => state.Log;

    /// The iOS session waits the pace only in the play phase, before a computer's card. A
    /// computer's pass is bookkeeping and goes through at once.
    public bool PausesBefore(HeartsState state, HeartsAction action) =>
        state.Phase == HeartsPhase.Play && action is HeartsAction.Play;
}
