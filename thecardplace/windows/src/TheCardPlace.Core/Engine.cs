using System.Text.Json;
using System.Text.Json.Serialization;

namespace TheCardPlace.Core;

/// <summary>
/// The shape every game's engine has, so one table driver can run any of them. The driver does
/// not know the rules of any game: it asks whose turn it is, lets the computer decide for a bot
/// seat, takes a person's action for a human seat, and passes every action through
/// <see cref="Apply"/>, which is the only way the state changes.
///
/// That one gate is what makes tables with other people possible later. A remote player's action
/// arrives as a <typeparamref name="TAction"/> (which serialises to JSON), goes through the same
/// seat-checked <see cref="Apply"/>, and is refused in the same words as a local one.
/// </summary>
public interface IGameEngine<TState, TAction>
    where TState : class
    where TAction : class
{
    /// <summary>How many chairs this table has.</summary>
    int Seats(TState state);

    /// <summary>Whether a person or the computer decides for this seat.</summary>
    Occupant OccupantOf(TState state, int seat);

    /// <summary>Which seat must act now; null when nobody (between hands, game over).</summary>
    int? SeatToAct(TState state);

    /// <summary>Validates the seat, the phase and the action; refuses with a reason in the words of the rule.</summary>
    ActionResult Apply(TState state, int seat, TAction action, RandomSource rng);

    /// <summary>The computer's move for a seat, reading only what that seat may see.</summary>
    TAction? Decide(TState state, int seat, RandomSource rng);

    /// <summary>The state's event stream.</summary>
    EventLog Log(TState state);

    /// <summary>
    /// Whether the table should pause at the player's chosen pace before this computer action, so
    /// it can be heard. Plays usually pause; bookkeeping such as a computer's pass or discard
    /// usually does not.
    /// </summary>
    bool PausesBefore(TState state, TAction action);
}

/// <summary>
/// Copying and comparing states through JSON. Every engine state serialises: that is how a test
/// proves a refused action changed nothing, how the hidden-information tests make a scrambled
/// copy, and how a table could one day be saved or sent to another player.
/// </summary>
public static class Snapshot
{
    public static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = false,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(Json(value), Options)
        ?? throw new InvalidOperationException("a state cloned to null");
}
