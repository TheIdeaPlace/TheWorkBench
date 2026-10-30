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


def parse_nvda_log(path: Path, offset: int = 0) -> list[tuple[float, str]]:
    """(seconds since midnight, text) for every 'Speaking' entry in an NVDA log, from byte `offset`
    on. An offset past the end (NVDA restarted, so its log was started afresh) reads it all."""
    data = path.read_bytes()
    if offset > len(data):
        offset = 0
    entries, when = [], None
    for line in data[offset:].decode("utf-8", errors="replace").splitlines():
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


def _seconds(clock: str) -> float:
    hh, mm, rest = clock.split(":")
    return int(hh) * 3600 + int(mm) * 60 + float(rest)


def read_clock(keys: Path) -> dict:
    """From a clip's key log: 'zero' (wall clock at the stopwatch's zero, seconds since midnight),
    'end' (recording end, clip seconds), 'logpos' (NVDA's log length when the take began) and
    'drift' (how far the wall clock moved against the stopwatch during the take, seconds)."""
    info = {"zero": None, "end": None, "logpos": 0, "drift": None}
    clock_end = None
    for line in keys.read_text(encoding="utf-8-sig").splitlines():
        parts = line.split("\t")
        if len(parts) < 2 or not parts[0].isdigit():
            continue
        ms = int(parts[0]) / 1000.0
        if parts[1] == "clock" and len(parts) >= 3:
            info["zero"] = _seconds(parts[2]) - ms
        elif parts[1] == "end":
            info["end"] = ms
        elif parts[1] == "logpos" and len(parts) >= 3 and parts[2].isdigit():
            info["logpos"] = int(parts[2])
        elif parts[1] == "clockend" and len(parts) >= 3:
            clock_end = (_seconds(parts[2]), ms)
    if info["zero"] is not None and clock_end is not None:
        wall = clock_end[0] - info["zero"]
        if wall < -43200:
            wall += 86400
        info["drift"] = wall - clock_end[1]
    return info


def clip_speech(log: Path, keys: Path) -> list[tuple[float, str]]:
    """Utterances during one recording, as (seconds on the clip's stopwatch, text)."""
    info = read_clock(keys)
    zero, end = info["zero"], info["end"]
    if zero is None:
        raise SystemExit(f"{keys} has no 'clock' entry; it was recorded before NVDA support. "
                         "Re-record it to get its speech as text.")
    if info["drift"] is not None and abs(info["drift"]) > 0.1:
        print(f"WARNING: the VM's clock moved {info['drift']:+.2f} s during this take (time sync?), so "
              "NVDA's timestamps may not line up with the keys. Record it again.", file=sys.stderr)
    out = []
    for when, text in parse_nvda_log(log, info["logpos"]):
        t = when - zero
        if t < -43200:
            t += 86400       # the recording ran past midnight
        elif t > 43200:
            t -= 86400       # the clock entry was just after midnight, the utterance just before
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
