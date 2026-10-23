---
name: sr-test
description: Test what a screen reader actually says in a Windows app — drive the real app in a vmtest VM with NVDA (or Narrator), capture its speech as text, and check it against expected speech per key press, with a report of what passed, what failed and what was said. Use when the user asks for a screen reader test pass, to verify announcements, to check what NVDA or Narrator says for a feature, to build a speech regression test, or to compare an app's speech against what the code intends.
---

# Screen reader speech tests

This drives a Windows app in a vmtest VM, records what a real screen reader says after each key,
and checks it against what it *should* say. It shares its rig and tools with the audio-demo skill
(`$env:AUDIODEMO_HOME`). A test can also be a demo: the same steps file records the audio.

**NVDA gives exact text.** Started by the rig with `--log-level=12`, NVDA logs every utterance it
sends to the synthesizer. `clip.ps1` takes each clip's part of that log as
`clips\<name>.speech.txt`, timed against the key presses. A mismatch is a real FAIL.
**Narrator keeps no log,** so its speech comes from a Whisper transcript of the audio. Treat a
mismatch as a FLAG to listen to, never as a failure.

**You still can't hear it.** The log says *what* NVDA said, not how it sounded, and not whether it
was interrupted before the end. A test that passes says the words were sent. Leave pronunciation,
pacing and interruptions to the person's listening.

## Where things are

- Tools: `$env:AUDIODEMO_HOME\tools`. The Python is `$data\venv\Scripts\python.exe`, where `$data` is
  `$env:AUDIODEMO_DATA`, or `%LOCALAPPDATA%\audio-demo` if that isn't set.
- A project folder per app under test (outside any repo): `steps\`, `clips\`, `out\`.
- vmtest: read the vmtest skill before the first VM command. Like vmtest, every tool here works on
  the VM held by the repo and branch it runs from, so `cd` into the app's repo in the same command
  as each tool.

## The flow

1. **Set up.** Run `vmtest begin` from the app's repo, then
   `powershell -File "$env:AUDIODEMO_HOME\tools\setup-rig.ps1" -ScreenReader NVDA`. It installs
   NVDA 2026.2 silently, with no welcome dialog, no usage-statistics question and no update check,
   using NVDA's default voice and typing echoed as words. Its last step records NVDA announcing
   Notepad. Check that the transcript shows "Untitled - Notepad".
   - Run it again with `-ScreenReader Narrator` to switch. Only one screen reader runs at a time.
2. **Deploy the app** the way users get it: the release build or installer if the test is about
   what ships, or a fresh build of the branch if it's about a change. Keep a pristine copy of any
   data or settings in the VM, and restore it before every run.
3. **Work out the expected speech from the code,** not from guesses about screen readers. Find:
   - accessible names, roles and descriptions;
   - item text and the `ToString()` or text a list item is built from;
   - the app's own announcements, such as UIA notifications or `AccessibilityHelper.Announce`;
   - the user guide's description.

   Expect each element's *name* and the app's own messages. Don't expect the screen reader's
   wording around them ("list", "1 of 9", "selected"), unless you've seen it in an earlier run.
   That wording is NVDA's, it changes between versions, and it isn't what the app controls.
4. **Write the steps** (`steps\<name>.steps`). Use the audio-demo format, plus expectation lines.
   Each expectation belongs to the last `k`, `t`, `u`, `x` or `n` step above it:

   ```
   k ^1                  press Ctrl+1
   w 3                   give NVDA time to speak
   e Email accounts      expected: said (whole words; case and punctuation ignored)
   e! Work, disconnected not said
   e/ \d+ of \d+         a regular expression (case ignored)
   e0                    nothing at all
   ```

   `n <keys>` holds the NVDA key (Insert) with other keys. For example, `n t` reads the window
   title, `n {TAB}` reads focus, and `n {DOWN}` reads all. In the first runs, a view changing
   inside one window changed its title with nothing spoken about it, so check titles with `n t`
   rather than expecting them to be announced.

   Use `q` and `Q` for setup that isn't under test. Wait long enough after each key for the whole
   announcement, usually 2 to 4 seconds, and 6 to 10 for a long list row. NVDA logs speech when it
   starts it, so the next key doesn't hide what was sent. But if the next key comes too soon, the
   speech that's still on its way lands in the next step's window.
5. **Run it:**
   `powershell -File "$env:AUDIODEMO_HOME\tools\clip.ps1" -Project <folder> -Name <name> -App <process>`.
   It prints every utterance with its time.
6. **Check it:** `& $py -I "$env:AUDIODEMO_HOME\tools\srcheck.py" <folder> <name>`. This writes
   `out\<name>-check.md`: every step, everything said after it, and each expectation as PASS, FAIL
   (NVDA) or FLAG (Narrator). The exit code is 1 if anything didn't pass.
7. **Judge each failure** before reporting it. A failure is one of:
   - **The app is wrong.** A missing or wrong name, a missing announcement, focus in the wrong
     place, or something announced twice. That's a finding.
   - **The expectation is wrong.** The code says otherwise, or the expectation was really about
     NVDA's own wording. Fix the expectation, and say so.
   - **The run is wrong.** A pop-up took focus, a wait was too short, or the app was still
     loading. Re-run it, and don't report it.
8. **Regression tests.** `srcheck.py ... --baseline <file>` writes the steps file with an `e` line
   for every utterance heard after each step that had none. Read it, delete what is NVDA's wording
   rather than the app's, and keep it. The next run against a new build then shows exactly what
   changed.

## Reporting

Give the person:
- the check report;
- a short list of findings, each with its key path, what was said, what should be said and why
  (file:line in the app); and
- a listening list for what text can't show. That covers whether the speech is clear, whether
  something long gets cut off, and how it sounds with their own screen reader and settings.

Don't claim how JAWS or another screen reader would behave from an NVDA run. Name the screen
reader, version and voice the run used.

## What goes wrong

- **Focus.** A fresh VM opens Windows' "Get Started" window, and it takes focus. The rig closes it,
  but it can come back. `clip.ps1 -App` stops if the app isn't in front.
- **Speech from before the step.** Startup announcements can arrive late. Restore the app, launch
  it, and wait until it's quiet before the first logged key.
- **Live data.** Dates, scores and network results change between runs. Expect the stable parts,
  such as names, labels and structure, or use `e/` with a pattern, never a literal score.
- **What can't be scripted.** The Windows key can't be pressed. NVDA commands go through `n`.
  Narrator's own commands, which use Caps Lock or Insert as their modifier, aren't supported.
- **Typing.** `t` sends letters and digits as key presses, and spaces and punctuation as Unicode
  characters. With SendKeys alone, NVDA's word echo ran words together. With Unicode alone, an app
  that detects typing from key presses can mistake each character for caret movement. QuickMail's
  spell check announced half-typed words. On-screen keyboards, some dictation and braille input
  send characters that way too, so test that path with `u <text>`. It types everything as Unicode,
  and is logged like `t`. (`Q` types the same way as `t`, just faster and unlogged.) The hybrid
  applies only while NVDA runs. Under Narrator, `t` and `Q` type through SendKeys as they always
  did. With Unicode spaces, Narrator's word echo broke down and something opened the Widgets panel
  mid-take, so use `u` with NVDA only.
- **Launching.** `x` brings the launched program's window to the front, because Windows won't let
  a background process do that by itself. It waits up to 60 seconds for any process of that name
  to open a window, which covers single-file (PyInstaller) builds, where the real app is a child
  process. A slow first start can still need a longer `w` after `x`.
- **Enter against Space.** If Enter on a button does nothing, check Space before reporting it.
  Toolkits differ. Qt push buttons outside dialogs ignore Enter, and that's a finding in itself.
- **The log is per NVDA session.** Restarting NVDA starts a new `C:\vmtest\rec\nvda.log`. Recordings
  from before the restart keep the `.speech.txt` files they already have.
