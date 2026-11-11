# QuickCalc

An accessible calculator for Windows, built first for people using JAWS, NVDA or Narrator, and comfortable for keyboard, mouse and low-vision users.

You type a whole calculation, such as `12 × (3 − 1)` or `80 + 15%`, and press Enter. The answer is spoken right away, shown in the Result box, and kept in a History list you can review and reuse.

## HTML version

Open [`index.html`](index.html) in a modern browser. No installation, internet connection, build step, or server is needed. The HTML version is separate from the Windows app described below.

- Type a whole expression and press Enter or `=`, or use the keypad. Escape clears it.
- Every keypad button has its own label and Tab stop. Use Tab or Shift+Tab to move between keys, then Space or Enter to press one. Arrow navigation also works when your screen reader passes those keys through. Up and Down in the expression recall history and restore your draft.
- Results and specific errors are announced through a screen reader live region. Inputs and symbol buttons have accessible names, with visible keyboard focus throughout.
- Scientific mode adds powers, roots, trigonometry, and degrees/radians. You can type the functions and constants listed below, including `ans` for the previous answer. Percentages work as described below.
- Select a history entry to reuse it, including its original angle unit. History holds the latest 100 calculations while the page is open; it is not stored or sent anywhere.
- The layout adapts to narrow windows and zoom, follows your system color preference, offers light/dark switching, and supports forced colors and reduced motion.
- Open Keyboard shortcuts (`?` or F1) for help. Copy result uses the clipboard when available and otherwise selects the answer for manual copying.

The browser version uses `index.html`, `styles.css`, `calculator.js`, and `app.js`, with no external fonts, libraries, or network requests. Numbers use JavaScript floating point arithmetic and display up to 15 significant digits.

Optional automated checks require Node.js 22 or later; browser checks also need Chrome or Edge installed. Set `QUICKCALC_BROWSER` if the executable is elsewhere. No npm packages are needed.

```text
npm test
npm run test:browser
```

Browser checks use headless Chrome or Edge to exercise actual keyboard input, accessible names, result/error announcements, history, dialog focus, clipboard fallback, direct file opening, narrow layouts, themes, and forced colors. Screenshots and temporary profiles go in the ignored `.web-test/` directory. These checks do not replace manual testing with a screen reader.

## Windows app accessibility

- **Answers are spoken.** Results, errors, and each keypad press are sent to the screen reader as UI Automation notifications, so you hear "equals 36" without moving focus. A newer announcement replaces one that hasn't been spoken yet.
- **Errors say what's wrong**, for example "Error: Cannot divide by zero". The caret moves to the spot in the expression where the problem is.
- **Every control has a name.** Keypad keys are announced as words ("Divide", "Square root", "Previous answer"), not as symbols.
- **The keypad is one Tab stop.** Arrow keys move between keys, and Space or Enter presses one. Typing while on the keypad goes straight to the expression.
- **History reads naturally**: "12 × 3 = 36". Enter puts a calculation back in the expression box, Ctrl+C copies it, and Delete removes it.
- **Up and Down arrows** in the expression box bring back earlier calculations, as in a command prompt.
- **Zoom** with Ctrl+Plus, Ctrl+Minus and Ctrl+0. Colors come from Windows, so high contrast themes work.
- Standard menus (Alt or F10), access keys for every area, and an F1 help summary.

## Keys

| Key | Action |
| --- | --- |
| Enter or = | Calculate |
| Escape | Clear the expression |
| Up / Down | Earlier and later calculations |
| Ctrl+Shift+C | Copy the result |
| Ctrl+Plus / Ctrl+Minus / Ctrl+0 | Zoom in, zoom out, reset zoom |
| Alt+P / Alt+R / Alt+A / Alt+K / Alt+Y | Expression, Result, Angle unit, Keypad, History |
| F1 | Help |

## What you can type

- `+ - * /` or `× ÷ −`, `^` for powers (`**` works too), parentheses, `%` and `!` (factorial)
- `80 + 15%` is 92 (15% of 80 is added). On its own, `15%` is 0.15.
- Start with an operator to carry on from the last answer: `* 2` doubles it. You can also type `ans`.
- Writing things side by side multiplies them: `2pi`, `3(4 + 1)`. This binds tighter than × and ÷, as in written math, so `1/2pi` is 1 ÷ (2π).
- Missing closing parentheses at the end are filled in.
- Functions: `sqrt cbrt abs sin cos tan asin acos atan ln log exp round floor ceil`. Parentheses are optional: `sqrt 16`.
- Constants `pi` (or `π`) and `e`. Scientific notation: `1.5e3`.
- Angles are in degrees unless you choose Radians.

Results show up to 15 significant digits, so `0.1 + 0.2` shows 0.3. The decimal point is always `.`; the number pad's decimal key types one whatever your region uses.

History shows each calculation the way it was worked out: if you typed `* 2` after an answer of 36, the entry reads `36 × 2 = 72`, so bringing it back later gives the same result.

## Building

Requires the .NET 10 SDK on Windows.

```
dotnet build QuickCalc.slnx
dotnet run --project src/QuickCalc
dotnet test tests/QuickCalc.Core.Tests
dotnet test tests/QuickCalc.UiTests
```

`QuickCalc.UiTests` launches the app and drives it through UI Automation, the same interface screen readers use, checking names, focus order, keyboard behavior, and the spoken announcements. It sends real keystrokes, so it belongs on a machine nobody is using: the `QuickCalc tests` workflow runs it on GitHub's machines for every change under `quickcalc/`, and [vmtest](../vmtest) can run it in a VM. If you do run it locally, leave the machine alone for the half minute it takes. It stops rather than send a key unless QuickCalc is the window in front.

This is an experiment and part of [The WorkBench](../README.md); it may or may not see further development.
