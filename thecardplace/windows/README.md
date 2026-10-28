# The Card Place for Windows

Hearts, Euchre, Spades, Cribbage and Sheephead as a native Windows app, played against the
computer and entirely offline, built for JAWS and NVDA first and on its way to the Microsoft
Store.

Nothing here wraps the web pages. The rules, the computer players and the words the games say are
ported from the iOS engines in `../ios/CardGamesKit`, which are themselves ports of each browser
game's `game.js` and `ai.js`. Every sentence a player hears is the same, character for character,
on all three platforms. The screens are WPF on top of that.

## Building and running

Needs the .NET 10 SDK. Nothing else: no Windows SDK, no NuGet packages in the app itself.

```
dotnet build TheCardPlace.slnx
dotnet run --project src/TheCardPlace
```

## Tests

```
dotnet test TheCardPlace.slnx --filter "Category!=UI"
```

That runs the 532 tests that open no window. Each engine has the suites its browser and iOS
originals have:

- **a rules oracle** written as literal data;
- **invariants** over hundreds of seeded games on every difficulty;
- **refusals**, each with the state compared before and after;
- **hidden information**, where every other seat's cards are scrambled and the computer's choice
  must not change (each was checked against a deliberately cheating player, and caught it);
- **exact review sentences**.

The session tests (`tests/TheCardPlace.Play.Tests`) play whole games of all five through the
controls the screen draws: Enter on a card, the main button, the bid box. They never call the
engine directly. They also cover the announcer's categories, the pace and Continue, a new game
during a pause, and a remote seat.

`tests/TheCardPlace.UiTests` launches the real app, sends keys and listens for the UI Automation
notifications a screen reader would speak. It checks Tab rings, single Tab stops, arrows in the
hand, refusals, the review and status bar keys, and that each game opens with focus on something
to do. **These take focus and type.** Run them on CI or in the test VM, never on a PC someone is
using:

```
dotnet test tests/TheCardPlace.UiTests -c Release
```

`.github/workflows/cardplace-windows-tests.yml` runs everything on every pull request.

## Layout

```
src/TheCardPlace.Core        Card, Suit, Rank, events, seeded randomness, Pace, Prose, IGameEngine
src/TheCardPlace.<Game>      the five engines (ENGINES.md is the contract); net10.0, no Windows types
src/TheCardPlace.Play        everything about playing that is not drawing: the sessions, the
                             table driver, the announcer, the pace gate, settings, game catalog
src/TheCardPlace             the WPF app: one window, the hub, the table, dialogs, themes
tests/...                    one test project per engine, the sessions, the UI
build/package-store.ps1      the Microsoft Store bundle
```

## Keyboard

The single letters are the browser games' own, so a player who knows one knows the other.

| Key | Does |
| --- | --- |
| Tab, Shift+Tab | Between the parts of the table: status, table, your hand, the choices, the main button, on the table, scores, hands played, log. Each list is one stop. |
| Left, Right, Home, End | Card to card in your hand, by position, not by where the overlapping cards fall on screen |
| 1 to 9, 0 | Straight to that card |
| Enter, Space | Play the card, or choose it for a pass, a throw, a bury |
| N | Continue during a pause; cut, go, next or deal. Never a bid, a pass or a bury. |
| H T L S P O C W … | Hear the hand, trick, last trick, scores and the rest; the Review menu lists each game's |
| R | Repeat the last message |
| G | The game log, newest first |
| ? | The keyboard list |
| Alt+U, B, Y, O, S, P, L | Status line, table, hand, on the table, scores, hands played, log |
| Ctrl+N, Ctrl+W | New game, leave the table (each asks first if a game is under way) |
| Ctrl+Comma | Settings |
| Ctrl+F9, Ctrl+Shift+F9 | Read the status bar again; move into it (Escape comes back) |
| Ctrl+Plus, Minus, 0 | Zoom |
| F1 | How to play |

## Accessibility

The decisions that matter, and where they live:

- **Menus are reached with Alt or F10, never Tab** (`MainWindow.xaml`). Nothing in a menu is ever
  disabled or hidden: an item that cannot act says why.
- **Every list is one Tab stop with arrows inside it.** This covers the hand, the seats round the
  table, the scores and the log. An empty list is still a stop, so it can be found and heard to be
  empty (`GameView.xaml.cs`, end of `Refresh`).
- **A card that cannot be played stays in the hand and says why:** "Nine of Hearts, cannot be
  played, you must follow clubs". Enter on it repeats the rule, and nothing changes. Buttons that
  are not ready are dimmed, not disabled, and say what they are waiting for.
- **One announcer, with categories,** after the Idea Place baseline (`announcements.md`) and
  QuickMail's design (`src/TheCardPlace.Play/Announcer.cs`).
  - Settings ▸ Screen Reader Announcements has a master switch and six categories:
    - results of what I do;
    - hints;
    - other players' plays, bids and calls;
    - tricks and scores;
    - deals and messages to me;
    - when it is my turn.
  - Refusals, and anything asked for with a review key, are always spoken.
  - Everything goes into the log and the status bar either way.
- **UI Automation notifications** (`Services.cs`). Answers to a key interrupt; game events queue,
  so they never cut off the name of the card focus just reached. Messages are raised once the
  window is idle, so they follow a focus change. While the app is in the background they are held,
  then spoken when it comes back.
- **Focus follows the turn,** to the first card you can play, or to the bid or pick choices. It
  never moves out of an open menu or dialog. Settings can turn this off; the turn is still
  announced.
- **Choices come before the button that confirms them,** in Tab order and on screen: choose the bid
  amount, then Tab to Bid.
- **Nothing by colour alone.**
  - Suits are printed as symbols.
  - Points and trump are written on the cards.
  - An unplayable card is greyed and crossed.
  - A chosen card rises, with "✓ chosen" under it.
  - Contrast on the felt: white text 7.9:1, red ink on a card 7.1:1, the amber focus ring 5.1:1.
- **High contrast:** the whole theme swaps to the system colours, live, including from one high
  contrast theme to another (`Themes/HighContrast.xaml`).
- **Zoom** to 200%; the hand re-fans to fit and the seats shrink together rather than overlap.

## Playing with other people later

This version is the computer opponents only, but the seams are in place:

- Every engine sends every action through one seat-checked `ApplyAction`, and refuses in the
  rule's words.
- Every action and every state serialises to JSON (`Snapshot`), which tests prove for all five.
- The engines target plain `net10.0` with no Windows types, so a server can run them.
- `GameSession` gives each chair a `SeatController`: `Local`, `Computer` or `Remote`. The table
  waits for a remote seat exactly as it waits for the local player.
  - `ReceiveRemote(seat, action)` takes a move from another computer through the same gate.
  - A test plays a hearts pass that way.
- What remains is a transport (the browser games' Cloudflare room worker is the obvious one), a
  lobby, and choosing which seat is `Me`.

## The Microsoft Store

```
powershell -File build/package-store.ps1
```

This makes `artifacts/TheCardPlace-<version>.msixbundle`, with x64 and ARM64. It follows RSS
Quick's, which is live in the Store; its `docs/STORE-PLAN.md` explains why it works this way:

- unsigned, because the Store signs it;
- loose files, so updates are small;
- logos from the iOS icon;
- `makeappx` fetched from NuGet;
- the version from `TheCardPlace.csproj`.

The app asks for no capabilities beyond `runFullTrust`, because it never touches the network.
Settings live in `%APPDATA%\TheCardPlace`, which Windows redirects into the package.

`build/store/identity.json` holds placeholders until the product is created in Partner Center
(as an "MSIX or PWA app"); copy Name and Publisher from *Product identity*. A
`cardplace-windows-v*` tag builds the bundle on CI as an artifact.

## What is not here yet

- Playing with other people (see above).
- Report a Bug and a crash report form. Crashes are logged to `%APPDATA%\TheCardPlace\TheCardPlace.log`.
- Coming back to a game after closing the app, which the browser games have.
- Difficulty in hearts and spades is offered nowhere, because the originals' computer players
  never read it.
