"""Check what a screen reader said against what it should have said.

    python -I srcheck.py <project folder> <name> [--report FILE] [--baseline FILE]

Reads <project>\\steps\\<name>.steps (the keys, with expectations), and the recording's
<project>\\clips\\<name>.keys.txt and, for NVDA, <name>.speech.txt (its own log of every utterance,
written by clip.ps1). Each expectation belongs to the last logged step (k, t, x or n) above it in the
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
Exit code: 1 if anything failed (NVDA) or was flagged (Narrator), else 0.
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

LOGGED = ("k", "t", "x", "n")   # steps the recorder logs ("n" is logged as a "k")


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
            steps.append(Step(n, f"{op} {arg}".strip()))
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
    """Logged step times by steps-file line ({line: first time}), recording start, recording end."""
    first, start, end, unnumbered = {}, 0.0, None, 0
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
            if len(parts) >= 4 and parts[3].isdigit():
                first.setdefault(int(parts[3]), t)
            else:
                unnumbered += 1
    if unnumbered and not first:
        raise SystemExit(f"{path} has no steps-file line numbers; it was recorded before srcheck existed. "
                         "Record it again.")
    return first, start, end


def whisper_speech(wav: Path) -> list[tuple[float, str]]:
    from faster_whisper import WhisperModel
    import numpy as np
    raw = subprocess.run(["ffmpeg", "-loglevel", "error", "-i", str(wav), "-f", "s16le", "-ac", "1",
                          "-ar", "16000", "-"], capture_output=True, check=True).stdout
    x = np.frombuffer(raw, np.int16).astype("float32") / 32768
    model = WhisperModel("small.en", device="cpu", compute_type="int8")
    segs, _ = model.transcribe(x, language="en", beam_size=5, temperature=[0.0, 0.2, 0.4, 0.6, 0.8],
                               vad_filter=True, condition_on_previous_text=False)
    return [(s.start, s.text.strip()) for s in segs if s.text.strip()]


def assign(steps: list[Step], first: dict, start: float, end: float | None, speech) -> None:
    """Give each step that ran its window, from its first logged entry to the next step's (the last
    one to the end of the recording), and the utterances that fall in it."""
    if end is None:   # an older or interrupted log: run to just after the last utterance
        end = max([t for t, _ in speech] + [start]) + 1
    steps[0].start = start
    for s in steps[1:]:
        s.start = first.get(s.line)
    timeline = sorted((s for s in steps if s.start is not None), key=lambda s: s.start)
    for i, s in enumerate(timeline):
        s.end = timeline[i + 1].start if i + 1 < len(timeline) else end
        s.speech = [(t, text) for t, text in speech if s.start <= t < s.end]


def check(steps: list[Step], exact: bool) -> int:
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
            e.result = "PASS" if ok else ("FAIL" if exact else "FLAG")
            problems += 0 if ok else 1
    return problems


def report(steps: list[Step], name: str, exact: bool, problems: int) -> str:
    total = sum(len(s.expects) for s in steps)
    source = ("NVDA's speech log (exact text)" if exact
              else "a Whisper transcript of the recording (approximate: a FLAG means listen, not that it's wrong)")
    out = [f"# Screen reader check: {name}", "",
           f"Speech from {source}.", "",
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


def baseline(steps_file: Path, steps: list[Step]) -> str:
    """The steps file with an 'e' line for each utterance after each logged step that has none."""
    by_line = {s.line: s for s in steps if s.line}
    lines = steps_file.read_text(encoding="utf-8-sig").splitlines()
    out, pending = [], None

    def emit():
        if pending and not pending.expects:
            out.extend(f"e {text}" for _, text in pending.speech)

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
    first, start, end = read_log(clips / f"{args.name}.keys.txt")

    speech_file = clips / f"{args.name}.speech.txt"
    exact = speech_file.exists()
    speech = read_speech(speech_file) if exact else whisper_speech(clips / f"{args.name}.wav")
    assign(steps, first, start, end, speech)

    problems = check(steps, exact)
    text = report(steps, args.name, exact, problems)
    out = args.report or (proj / "out" / f"{args.name}-check.md")
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(text, encoding="utf-8")
    if args.baseline:
        args.baseline.write_text(baseline(steps_file, steps), encoding="utf-8")
    total = sum(len(s.expects) for s in steps)
    word = "failed" if exact else "flagged"
    print(f"{args.name}: {total - problems} of {total} expectations met" + (f", {problems} {word}" if problems else ""))
    for s in steps:
        for e in s.expects:
            if e.result != "PASS":
                print(f"  {e.result} line {e.line} ({s.label}): {e.kind} '{e.text}' - {e.why}")
    print(out)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
