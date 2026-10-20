# QuickCalc

An accessible calculator for Windows, built first for people using JAWS, NVDA or Narrator, and comfortable for keyboard, mouse and low-vision users.

You type a whole calculation, such as `12 × (3 − 1)` or `80 + 15%`, and press Enter. The answer is spoken right away, shown in the Result box, and kept in a History list you can review and reuse.

## Accessibility

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
- Writing things side by side multiplies them: `2pi`, `3(4 + 1)`.
- Missing closing parentheses at the end are filled in.
- Functions: `sqrt cbrt abs sin cos tan asin acos atan ln log exp round floor ceil`. Parentheses are optional: `sqrt 16`.
- Constants `pi` (or `π`) and `e`. Scientific notation: `1.5e3`.
- Angles are in degrees unless you choose Radians.

Results show up to 15 significant digits, so `0.1 + 0.2` shows 0.3.

## Building

Requires the .NET 10 SDK on Windows.

```
dotnet build QuickCalc.slnx
dotnet run --project src/QuickCalc
dotnet test tests/QuickCalc.Core.Tests
dotnet test tests/QuickCalc.UiTests
```

`QuickCalc.UiTests` launches the app and drives it through UI Automation, the same interface screen readers use, checking names, focus order, keyboard behavior, and the spoken announcements. It sends real keystrokes, so leave the machine alone for the half minute it takes. It won't send keys unless QuickCalc is in front.
