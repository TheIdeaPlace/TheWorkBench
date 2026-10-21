"""Read what a screen reader said, as text, from NVDA's log.

NVDA started with --log-level=12 (input/output) logs every utterance:

    IO - speech.speech.speak (15:22:03.123) - MainThread (5604):
    Speaking ['Inbox', 'tree view', LangChangeCommand ('en_US'), '4 unread']

That is exact: the words NVDA sent to the synthesizer, not a guess from audio. This module turns
those entries into (seconds, text) pairs, lined up against a clip's key log: vm\\demo.ps1 writes a
"clock" entry with the wall-clock time of its stopwatch's zero, and an "end" entry when it stops.

    python -I speechlog.py <nvda.log> <clip.keys.txt> <clip.speech.txt>

writes one "<ms>\\t<text>" line per utterance inside the recording, ms on the clip's stopwatch.
Narrator has no such log; for Narrator clips, use transcribe.py (Whisper) instead.
"""
from __future__ import annotations

import ast
import re
import sys
from pathlib import Path

HEADER = re.compile(r"^IO - \S+ \((\d\d):(\d\d):(\d\d)\.(\d{3})\)")


def speech_strings(seq: str) -> list[str]:
    """The spoken strings in a logged speech sequence, in order.

    The sequence is Python's repr of a list that mixes strings with commands such as
    LangChangeCommand ('en_US') or BreakCommand(time=100). Only string literals at the list's own
    level are speech; any inside a command's parentheses are its arguments.
    """
    out, depth, i, n = [], 0, 0, len(seq)
    while i < n:
        c = seq[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif c in "'\"":
            # Find the end of this literal, honouring backslash escapes, then let Python decode it.
            j = i + 1
            while j < n and seq[j] != c:
                j += 2 if seq[j] == "\\" else 1
            literal = seq[i:j + 1]
            if depth == 1:   # inside the outer [ ] only
                try:
                    out.append(ast.literal_eval(literal))
                except (ValueError, SyntaxError):
                    out.append(literal[1:-1])
            i = j
        i += 1
    return [s for s in (" ".join(x.split()) for x in out) if s]


def parse_nvda_log(path: Path) -> list[tuple[float, str]]:
    """(seconds since midnight, text) for every 'Speaking' entry in an NVDA log."""
    entries, when = [], None
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        m = HEADER.match(line)
        if m:
            h, mi, s, ms = (int(g) for g in m.groups())
            when = h * 3600 + mi * 60 + s + ms / 1000
            continue
        if when is not None and line.startswith("Speaking "):
            text = " ".join(speech_strings(line[len("Speaking "):]))
            if text:
                entries.append((when, text))
            when = None
    return entries


def read_clock(keys: Path) -> tuple[float | None, float | None]:
    """(clock at the stopwatch's zero in seconds since midnight, recording end in clip seconds)."""
    zero, end = None, None
    for line in keys.read_text(encoding="utf-8-sig").splitlines():
        parts = line.split("\t")
        if len(parts) >= 3 and parts[1] == "clock":
            hh, mm, rest = parts[2].split(":")
            zero = int(hh) * 3600 + int(mm) * 60 + float(rest) - int(parts[0]) / 1000.0
        elif len(parts) >= 2 and parts[1] == "end" and parts[0].isdigit():
            end = int(parts[0]) / 1000.0
    return zero, end


def clip_speech(log: Path, keys: Path) -> list[tuple[float, str]]:
    """Utterances during one recording, as (seconds on the clip's stopwatch, text)."""
    zero, end = read_clock(keys)
    if zero is None:
        raise SystemExit(f"{keys} has no 'clock' entry; it was recorded before NVDA support. "
                         "Re-record it to get its speech as text.")
    out = []
    for when, text in parse_nvda_log(log):
        t = when - zero
        if t < -43200:
            t += 86400       # the recording ran past midnight
        if t >= 0 and (end is None or t <= end):
            out.append((t, text))
    return out


def write_speech(path: Path, events: list[tuple[float, str]]) -> None:
    path.write_text("".join(f"{int(round(t * 1000))}\t{text}\n" for t, text in events), encoding="utf-8")


def read_speech(path: Path) -> list[tuple[float, str]]:
    out = []
    for line in path.read_text(encoding="utf-8").splitlines():
        ms, _, text = line.partition("\t")
        if ms.isdigit():
            out.append((int(ms) / 1000.0, text))
    return out


if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit(__doc__)
    events = clip_speech(Path(sys.argv[1]), Path(sys.argv[2]))
    write_speech(Path(sys.argv[3]), events)
    for t, text in events:
        print(f"  [{int(t // 60)}:{t % 60:04.1f}] {text}")
    if not events:
        print("  (NVDA logged no speech during this recording. Is NVDA running with --log-level=12?)")
