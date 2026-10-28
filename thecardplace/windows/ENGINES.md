# The engine contract (Windows)

The C# twin of `../ios/CardGamesKit/ENGINES.md`. Read that file first: everything it says about
what an engine is for, the words it uses, the events it writes and the tests it carries holds here.
This file says only how the shape is spelled in C#.

Each engine is a **faithful port of the Swift engine** in `../ios/CardGamesKit/Sources/<Game>Engine`,
which is itself a faithful port of the browser game in `../<game>/js/`. Same rules, same computer
heuristics and difficulty levels, same sentences, character for character, wherever the sentence
makes sense on Windows. Where Swift and the browser disagree, Swift wins unless it is plainly a bug;
say so in a comment if you find one. The words matter: a screen reader user plays by what these
engines say.

## Where things live

```
src/TheCardPlace.Core/            Card, Suit, Rank, RandomSource, EventLog, GameEvent, ActionResult,
                                  Occupant, Difficulty, Pace, Prose, IGameEngine, Snapshot
src/TheCardPlace.<Game>/          one project per game, references Core only. net10.0, no Windows types
tests/TheCardPlace.<Game>.Tests/  xUnit; runs with `dotnet test tests/TheCardPlace.<Game>.Tests`
```

Game names: `Hearts`, `Spades`, `Euchre`, `Cribbage`, `Sheephead`.

An engine project references `TheCardPlace.Core` and **nothing else**: no WPF, no Windows-only APIs,
no NuGet packages. It targets `net10.0` so a server could one day run it for tables with other people.

## The shape, spelled for Hearts

```csharp
namespace TheCardPlace.Hearts;

public sealed class HeartsConfig            // the game's rule options, defaults matching the web settings
{
    public List<string> Names { get; set; } = [...];   // one per seat; seat 0 is the person at this PC
    public Difficulty Difficulty { get; set; } = Difficulty.Normal;
    public int PointsToWin { get; set; } = 100;
}

public sealed class HeartsPlayer
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public Occupant Occupant { get; set; }   // Human for seat 0, Bot otherwise, unless config says
    public List<Card> Hand { get; set; } = [];
    // …whatever the game tracks per seat
}

public enum HeartsPhase { Idle, Passing, Play, HandOver, GameOver }

public sealed class HeartsState
{
    public HeartsPhase Phase { get; set; }
    public List<HeartsPlayer> Players { get; set; } = [];
    public HeartsConfig Config { get; set; } = new();
    public EventLog Log { get; set; } = new();
    // …the rest: trick, turn, leader, dealer, history, winner
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Start), "start")]
[JsonDerivedType(typeof(Play), "play")]
// …one attribute per case
public abstract record HeartsAction
{
    public sealed record Start : HeartsAction;
    public sealed record Play(Card Card) : HeartsAction;
    public sealed record Pass(List<Card> Cards) : HeartsAction;   // note: records with lists compare by reference;
    public sealed record NextHand : HeartsAction;                 // tests compare actions by Snapshot.Json
    public sealed record NewGame : HeartsAction;
}

public static class HeartsGame
{
    public const int Seats = 4;               // or a function of config (sheephead)
    public static HeartsState CreateGame(HeartsConfig config);
    public static ActionResult ApplyAction(HeartsState state, int seat, HeartsAction action, RandomSource rng);
    public static int? SeatToAct(HeartsState state);
    public static bool CanDeal(HeartsState state);
    public static List<Card> LegalPlays(HeartsState state, int seat);
    public static string? WhyNot(HeartsState state, int seat, Card card);
}

public static class HeartsAI
{
    public static HeartsAction? Decide(HeartsState state, int seat, RandomSource rng);
}

public static class HeartsCards  { Describe(...), SortHand(...), Role(...), the game's own ordering }
public static class HeartsReview { Status(state, seat), Hand, Trick, LastTrick, Scores, …the game's own }
public static class HeartsHelp   { public static IReadOnlyList<(string Heading, string Body)> Rules { get; } }

/// The adapter the app's table driver uses. Thin: every member forwards to the static classes.
public sealed class HeartsEngine : IGameEngine<HeartsState, HeartsAction> { … }
```

Spelling rules, because the Swift is value-typed and C# is not:

- **States are classes with public get/set properties**, mutated in place by `ApplyAction` (Swift's
  `inout`). No private fields holding game state, no constructor-only state: everything must survive
  `Snapshot.Clone(state)` unchanged, so `Snapshot.Json(Snapshot.Clone(s)) == Snapshot.Json(s)` holds.
  Use `List<T>`, `Dictionary<int, T>` (or `Dictionary<string,T>`), nullable value types and nested
  classes. Swift tuples become small records or classes with properties. Use `[JsonIgnore]` on computed
  properties.
- **`ApplyAction` never throws on bad input.** Wrong seat, wrong phase, a card not held: return
  `ActionResult.Refused("…")` and leave the state exactly as it was (check before you mutate, or
  validate fully first). `Faulted` is for a caught internal inconsistency.
- **Swift `RandomSource` passed `inout` becomes a `RandomSource` reference.** Same call order as Swift,
  so seeded games replay the same: `shuffled(with:)` is `Shuffled(rng)`, `nextInt(below:)` is
  `NextInt`, `nextUnit()` is `NextUnit`, `chance` is `Chance`. No `Random`, no `DateTime.Now`.
- **The AI reads only what its seat may see.** Never another seat's hand, the undealt deck, the blind
  or the crib. The hidden-information tests prove it.
- **Actions serialise.** Every action case has a `[JsonDerivedType]` so a test can round-trip it through
  `Snapshot.Json` / `JsonSerializer.Deserialize<HeartsAction>(json, Snapshot.Options)`. That is the
  wire format for playing with other people later.
- **`IGameEngine.PausesBefore`** says whether the app should wait the player's pace before a computer
  action so it can be heard. Port it from what the iOS session waits on: look at
  `../ios/TheCardPlace/Games/<Game>/<Game>Session.swift`, `driveBots()`, for where it calls
  `gate.wait(pace)` (hearts: only in the play phase; other games also pause before bids and calls).
- Naming: PascalCase members, `IsX` for booleans where natural. Keep the Swift names otherwise, so the
  two can be read side by side. Keep the Swift doc comments (as `///` XML or plain `//`); they carry
  the reasons for the rules.
- **No warnings.** `TreatWarningsAsErrors` is on for every project. Nullable is on.

## Tests

Port the Swift test target for the game (`../ios/CardGamesKit/Tests/<Game>EngineTests`) to xUnit,
test for test: rules oracle, invariants, refusals, hidden information, review text, and any others it
has. Refusal tests compare `Snapshot.Json(state)` before and after. Add an action round-trip test
(every action case through JSON and back, compared by `Snapshot.Json`) and a test that
`IGameEngine` driving bots alone plays a whole game to its end for each difficulty.

Keep the whole test project under about twenty seconds. Use `[Theory]` with `[InlineData]` or
`[MemberData]` where Swift loops over cases. xUnit analyzers count as warnings: fix them.
