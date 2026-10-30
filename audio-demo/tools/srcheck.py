"""Check what a screen reader said against what it should have said.

    python -I srcheck.py <project folder> <name> [--report FILE] [--baseline FILE]

Reads <project>\\steps\\<name>.steps (the keys, with expectations), and the recording's
<project>\\clips\\<name>.keys.txt and, for NVDA, <name>.speech.txt (its own log of every utterance,
written by clip.ps1). Each expectation belongs to the last logged step (k, t, u, x or n) above it in the
steps file, and is checked against everything the screen reader said from that step until the next
logged step (or the end of the recording):

    k {DOWN}
    w 3
    e Sam Patel                 said, as whole words, somewhere in that speech (case and punctuation ignored)
    e! Read                     NOT said (whole words: 'unread' doesn't count as 'read')
    e/ \\d+ of \\d+              said, matching a regular expression (case ignored)
    e0                          nothing said at all

Expectations above the first logged step are checked against the speech before it.

Sources:
  - NVDA: exact text from its log, so a mismatch is a FAIL.
  - Narrator (no log): a Whisper transcript of the audio, timed by segment. Recognition can mishear,
    so a mismatch is a FLAG to listen to, never a FAIL.

The report (Markdown) lists every logged step and what was said after it, with each expectation's
result. --baseline writes a copy of the steps file with an `e` line for every utterance heard, to
review and keep as the expected speech for the next run (a regression test).
Steps are matched to the recording in order, and each one's keys are checked, so expectation lines
and comments can be edited after recording. Changing a key or adding a step means recording again.
Exit code: 1 if anything failed (NVDA) or was flagged (Narrator); 2 if NVDA logged nothing at all
(the run can't be trusted); else 0.
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))   # python -I leaves this folder off sys.path
from speechlog import read_speech  # noqa: E402

LOGGED = ("k", "t", "u", "x", "n")   # steps the recorder logs ("n" as a "k", "u" as a "t")


@dataclass
class Expectation:
    line: int
    kind: str          # 'said', 'not', 'regex', 'silent'
    text: str
    result: str = ""
    why: str = ""


@dataclass
class Step:
    line: int          # 0 for "before the first logged step"
    label: str
    op: str = ""
    arg: str = ""
    expects: list[Expectation] = field(default_factory=list)
    start: float | None = None
    end: float | None = None
    speech: list[tuple[float, str]] = field(default_factory=list)


def norm(s: str) -> str:
    """Lower case, punctuation as spaces, single spaces: what 'said' compares."""
    return " ".join(re.sub(r"[^\w]+", " ", s.lower()).split())


def says(expected: str, said: str) -> bool:
    """True if `expected` was said as whole words: "read" is not in "unread", but is in "read, Sam"."""
    return f" {norm(expected)} " in f" {norm(said)} "


def parse_steps(path: Path) -> list[Step]:
    steps = [Step(0, "(before the first step)")]
    for n, raw in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), start=1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        op, arg = line[0], line[1:].strip()
        if op in LOGGED:
            steps.append(Step(n, f"{op} {arg}".strip(), op, arg))
        elif op == "e":
            if arg == "0":
                exp = Expectation(n, "silent", "")
            elif arg.startswith("!"):
                exp = Expectation(n, "not", arg[1:].strip())
            elif arg.startswith("/"):
                exp = Expectation(n, "regex", arg[1:].strip())
                try:
                    re.compile(exp.text)
                except re.error as err:
                    raise SystemExit(f"{path}:{n}: bad regular expression: {err}")
            else:
                exp = Expectation(n, "said", arg)
            if exp.kind != "silent" and not exp.text:
                raise SystemExit(f"{path}:{n}: '{line}' needs the expected speech after it.")
            steps[-1].expects.append(exp)
    return steps


def read_log(path: Path):
    """The recording's logged steps in order, as [kind, key, first time], with a typed text's
    characters grouped into one; plus the recording start and end."""
    runs, start, end, last_line = [], 0.0, None, None
    for raw in path.read_text(encoding="utf-8-sig").splitlines():
        parts = raw.split("\t")
        if len(parts) < 2 or not parts[0].isdigit():
            continue
        t = int(parts[0]) / 1000.0
        if parts[1] == "start":
            start = t
        elif parts[1] == "end":
            end = t
        elif parts[1] in ("k", "t", "x"):
            line = parts[3] if len(parts) >= 4 else None
            key = parts[2] if len(parts) >= 3 else ""
            if parts[1] == "t" and runs and runs[-1][0] == "t" and line is not None and line == last_line:
                runs[-1][1] += key      # the next character of the same typed text
            else:
                runs.append([parts[1], key, t])
            last_line = line
    return runs, start, end


def align(steps: list[Step], runs: list, start: float, log: Path) -> None:
    """Give each logged step its start time, matching the recording's steps to the steps file in
    order and checking each one's keys. Matching by order, not by line number, lets expectations
    and comments be edited after recording; a changed key, or a step added, is refused."""
    steps[0].start = start
    logged = steps[1:]
    if len(runs) > len(logged):
        raise SystemExit(f"{log} has {len(runs)} steps but the steps file now has {len(logged)}: it was "
                         "changed after recording. Record it again.")
    for i, (kind, key, t) in enumerate(runs):
        st = logged[i]
        want_kind = {"n": "k", "u": "t"}.get(st.op, st.op)
        want_key = {"k": st.arg, "n": f"NVDA+{st.arg}", "t": st.arg, "u": st.arg, "x": "launch"}[st.op]
        if kind != want_kind or key != want_key:
            raise SystemExit(f"Step {i + 1} (line {st.line}, '{st.label}') was recorded as '{kind} {key}': "
                             "the steps file changed after recording. Record it again.")
        st.start = t
    # Steps after the last recorded one weren't run (the take stopped early); they keep start None.


def whisper_speech(wav: Path, offset: float) -> list[tuple[float, str]]:
    """Whisper's transcript of the recording, timed on the clip's stopwatch. Whisper counts from the
    start of the WAV, which began `offset` seconds after the stopwatch (the 'start' entry)."""
    from faster_whisper import WhisperModel
    import numpy as np
    raw = subprocess.run(["ffmpeg", "-loglevel", "error", "-i", str(wav), "-f", "s16le", "-ac", "1",
                          "-ar", "16000", "-"], capture_output=True, check=True).stdout
    x = np.frombuffer(raw, np.int16).astype("float32") / 32768
    model = WhisperModel("small.en", device="cpu", compute_type="int8")
    segs, _ = model.transcribe(x, language="en", beam_size=5, temperature=[0.0, 0.2, 0.4, 0.6, 0.8],
                               vad_filter=True, condition_on_previous_text=False)
    return shift(((s.start, s.text.strip()) for s in segs if s.text.strip()), offset)


def shift(speech, offset: float) -> list[tuple[float, str]]:
    return [(t + offset, text) for t, text in speech]


def assign(steps: list[Step], end: float | None, speech) -> None:
    """Give each step that ran its window, from its start to the next step's (the last one to the
    end of the recording), and the utterances that fall in it."""
    if end is None:   # an older or interrupted log: run to just after the last utterance
        end = max([t for t, _ in speech] + [s.start for s in steps if s.start is not None]) + 1
    timeline = sorted((s for s in steps if s.start is not None), key=lambda s: s.start)
    for i, s in enumerate(timeline):
        s.end = timeline[i + 1].start if i + 1 < len(timeline) else end
        s.speech = [(t, text) for t, text in speech if s.start <= t < s.end]


def check(steps: list[Step], exact: bool, untrusted: str = "") -> int:
    """Mark each expectation PASS, FAIL (exact speech) or FLAG (transcribed, or untrusted)."""
    problems = 0
    for st in steps:
        said = " ".join(text for _, text in st.speech)
        for e in st.expects:
            if e.kind == "said":
                ok = says(e.text, said)
                e.why = "" if ok else "not said"
            elif e.kind == "not":
                ok = not says(e.text, said)
                e.why = "" if ok else "said, but shouldn't be"
            elif e.kind == "regex":
                ok = re.search(e.text, said, re.IGNORECASE) is not None
                e.why = "" if ok else "no speech matched"
            else:
                ok = not st.speech
                e.why = "" if ok else "something was said"
            if st.start is None:
                ok, e.why = False, "this step wasn't run (not in the recording's key log)"
            if untrusted:
                ok, e.why = False, untrusted
            e.result = "PASS" if ok else ("FAIL" if exact and not untrusted else "FLAG")
            problems += 0 if ok else 1
    return problems


def report(steps: list[Step], name: str, exact: bool, problems: int, untrusted: str = "") -> str:
    total = sum(len(s.expects) for s in steps)
    source = ("NVDA's speech log (exact text)" if exact
              else "a Whisper transcript of the recording (approximate: a FLAG means listen, not that it's wrong)")
    out = [f"# Screen reader check: {name}", "",
           f"Speech from {source}. A logged utterance is what NVDA sent to its voice; the next key "
           "can cut it off, so the log shows what was sent, not that all of it was heard.", ""]
    if untrusted:
        out += [f"**Not trustworthy: {untrusted}.** Every expectation is FLAGged.", ""]
    out += [
           f"**{total - problems} of {total} expectations met.**" if total else "No expectations in this steps file.", ""]
    for st in steps:
        if st.line == 0 and not st.speech and not st.expects:
            continue
        where = f"line {st.line}" if st.line else "start"
        out.append(f"## {where}: `{st.label}`")
        out.append("")
        if st.speech:
            for t, text in st.speech:
                out.append(f"- {t:6.2f} s: {text}")
        else:
            out.append("- (nothing said)")
        for e in st.expects:
            what = {"said": "says", "not": "doesn't say", "regex": "matches", "silent": "says nothing"}[e.kind]
            detail = f" — {e.why}" if e.why else ""
            out.append(f"- **{e.result}** (line {e.line}) {what} `{e.text}`{detail}" if e.text
                       else f"- **{e.result}** (line {e.line}) {what}{detail}")
        out.append("")
    return "\n".join(out) + "\n"


def baseline_line(text: str) -> str:
    """An 'e' line that expects exactly `text`. Text that would read as another kind of expectation
    ('0' is silence, a leading '!' or '/' changes the meaning) is written as an escaped regex."""
    if text == "0" or text.startswith(("!", "/")):
        return "e/ " + re.escape(text)
    return f"e {text}"


def baseline(steps_file: Path, steps: list[Step]) -> str:
    """The steps file with an 'e' line for each utterance after each logged step that has none."""
    by_line = {s.line: s for s in steps if s.line}
    lines = steps_file.read_text(encoding="utf-8-sig").splitlines()
    out, pending = [], None

    def emit():
        if pending and not pending.expects:
            out.extend(baseline_line(text) for _, text in pending.speech)

    for n, raw in enumerate(lines, start=1):
        if n in by_line:
            emit()
            pending = by_line[n]
        out.append(raw)
    emit()
    return "\n".join(out) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("project", type=Path)
    ap.add_argument("name")
    ap.add_argument("--report", type=Path, default=None, help="default: <project>\\out\\<name>-check.md")
    ap.add_argument("--baseline", type=Path, default=None, help="write a steps file with the speech heard as expectations")
    args = ap.parse_args()

    proj = args.project.resolve()
    steps_file = proj / "steps" / f"{args.name}.steps"
    clips = proj / "clips"
    steps = parse_steps(steps_file)
    keys_file = clips / f"{args.name}.keys.txt"
    runs, start, end = read_log(keys_file)
    align(steps, runs, start, keys_file)

    speech_file = clips / f"{args.name}.speech.txt"
    exact = speech_file.exists()
    speech = read_speech(speech_file) if exact else whisper_speech(clips / f"{args.name}.wav", start)
    assign(steps, end, speech)

    total = sum(len(s.expects) for s in steps)
    # An NVDA clip with no speech at all almost always means NVDA wasn't logging (restarted without
    # the rig's flags, or speech turned off), not that the app was silent: don't pass e0 and e! on it.
    untrusted = ("NVDA logged nothing during this recording; check it's running with --log-level=12"
                 if exact and total and not speech else "")
    problems = check(steps, exact, untrusted)
    text = report(steps, args.name, exact, problems, untrusted)
    out = args.report or (proj / "out" / f"{args.name}-check.md")
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(text, encoding="utf-8")
    if args.baseline:
        args.baseline.write_text(baseline(steps_file, steps), encoding="utf-8")
    word = "failed" if exact and not untrusted else "flagged"
    if untrusted:
        print(f"WARNING: {untrusted}.")
    print(f"{args.name}: {total - problems} of {total} expectations met" + (f", {problems} {word}" if problems else ""))
    for s in steps:
        for e in s.expects:
            if e.result != "PASS":
                print(f"  {e.result} line {e.line} ({s.label}): {e.kind} '{e.text}' - {e.why}")
    print(out)
    return 2 if untrusted else (1 if problems else 0)


if __name__ == "__main__":
    sys.exit(main())
