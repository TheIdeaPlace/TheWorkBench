"""Build an audio demo: AI hosts (Kokoro) + recorded screen reader clips -> one MP3.

A project folder holds:
    script.txt      the episode, in the marker format below
    clips/          <name>.wav + <name>.keys.txt per demo, recorded by clip.ps1
    cache/          rendered host lines (created; reused while a line is unchanged)
    out/            the MP3 and a WAV master (created)

Script format (one marker per line, text on the lines after it):
    #title  QuickMail: a guided audio tour     directives start with '#'
    #voice  MICHAEL am_michael                 one per host; the name is the marker
    #voice  MAYA af_heart
    #speed  1.03                               optional, default 1.03
    # any other line starting with '#' is a comment
    [MICHAEL]
    Welcome to ...
    [PAUSE]                                    a 0.9 s beat
    [DEMO d01-orient]                          insert clips/d01-orient.wav here

Host pacing follows Quill's podcast generator (Community-Access/quill, docs/podcast/tools).

    python -I produce.py <project folder> [--script script.txt] [--out name.mp3]
"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import subprocess
import sys
from pathlib import Path

import numpy as np
import soundfile as sf

SR = 24000
DEFAULT_SPEED = 1.03
SAME_SPEAKER_GAP, SPEAKER_CHANGE_GAP, PAUSE_SECONDS = 0.18, 0.32, 0.9
DEMO_LEAD, DEMO_TAIL = 0.45, 0.6               # silence around a demo, inside the chimes
MAX_SILENCE, KEEP_HEAD, KEEP_TAIL = 1.1, 0.30, 0.45   # pause shortening inside clips
CAPTURE_LAG = 0.35                             # ffmpeg starts a moment after the key stopwatch
MAX_CHARS = 360


def data_dir() -> Path:
    return Path(os.environ.get("AUDIODEMO_DATA") or Path(os.environ["LOCALAPPDATA"]) / "audio-demo")


def parse(text: str):
    """Return (directives, segments). Segments are ('SAY', speaker, text) | ('PAUSE',) | ('DEMO', name)."""
    meta = {"voices": {}, "speed": DEFAULT_SPEED, "title": ""}
    segs, cur, buf = [], None, []

    def flush():
        if cur and buf:
            segs.append(("SAY", cur, " ".join(buf)))
        buf.clear()

    for line in text.splitlines():
        t = line.strip().lstrip("﻿")
        if not t:
            continue
        if t.startswith("#"):
            parts = t[1:].split(None, 2)
            if parts and parts[0] == "voice" and len(parts) == 3:
                meta["voices"][parts[1].upper()] = parts[2].strip()
            elif parts and parts[0] == "speed" and len(parts) >= 2:
                meta["speed"] = float(parts[1])
            elif parts and parts[0] == "title" and len(parts) >= 2:
                meta["title"] = t[len("#title"):].strip()
            continue
        m = re.match(r"^\[([A-Z][A-Z0-9_]*)(?:\s+([\w.-]+))?\]$", t)
        if not m:
            buf.append(t)
            continue
        flush()
        tag = m.group(1)
        if tag == "PAUSE":
            segs.append(("PAUSE",)); cur = None
        elif tag == "DEMO":
            if not m.group(2):
                raise SystemExit("[DEMO] needs a clip name, e.g. [DEMO d01-orient]")
            segs.append(("DEMO", m.group(2))); cur = None
        else:
            if tag not in meta["voices"]:
                raise SystemExit(f"[{tag}] has no '#voice {tag} <kokoro voice>' line in the script")
            cur = tag
    flush()
    if not meta["voices"]:
        raise SystemExit("The script declares no hosts. Add lines like: #voice MICHAEL am_michael")
    return meta, segs


def chunks(text: str):
    text = " ".join(text.split())
    if len(text) <= MAX_CHARS:
        return [text]
    out, cur = [], ""
    for s in re.split(r"(?<=[.!?])\s+", text):
        cand = f"{cur} {s}".strip()
        if len(cand) > MAX_CHARS and cur:
            out.append(cur); cur = s
        else:
            cur = cand
    return out + ([cur] if cur else [])


def silence(sec):
    return np.zeros(int(sec * SR), np.float32)


def load_resampled(path: Path) -> np.ndarray:
    raw = subprocess.run(["ffmpeg", "-loglevel", "error", "-i", str(path), "-af", "highpass=f=70",
                          "-f", "f32le", "-ac", "1", "-ar", str(SR), "-"],
                         capture_output=True, check=True).stdout
    return np.frombuffer(raw, np.float32).copy()


def active_rms(x: np.ndarray) -> float:
    fl = int(0.05 * SR)
    if len(x) < fl:
        return 1e-3
    frames = x[: len(x) // fl * fl].reshape(-1, fl)
    r = np.sqrt((frames ** 2).mean(1))
    voiced = r[r > max(1e-4, r.max() * 0.08)]
    return float(np.sqrt((voiced ** 2).mean())) if len(voiced) else 1e-3


def click() -> np.ndarray:
    rng = np.random.default_rng(7)
    n = int(0.012 * SR)
    burst = rng.standard_normal(n).astype(np.float32) * np.exp(-np.linspace(0, 6, n)).astype(np.float32)
    burst = np.convolve(burst, np.ones(3) / 3, "same").astype(np.float32)
    return burst / np.abs(burst).max()


def chime(rising: bool) -> np.ndarray:
    t = np.arange(int(0.16 * SR)) / SR
    env = np.sin(np.pi * t / t[-1]) ** 2
    lo, hi = (660, 990) if rising else (990, 660)
    a = (np.sin(2 * np.pi * lo * t) + 0.3 * np.sin(4 * np.pi * lo * t)) * env
    b = (np.sin(2 * np.pi * hi * t) + 0.3 * np.sin(4 * np.pi * hi * t)) * env
    return (np.concatenate([a, silence(0.03), b]) * 0.12).astype(np.float32)


def read_keys(path: Path):
    """Key presses as (seconds, kind), and the recording start if the log has one."""
    keys, start = [], None
    if path.exists():
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            parts = line.split("\t")
            if len(parts) >= 2 and parts[0].isdigit():
                if parts[1] == "start":
                    start = int(parts[0]) / 1000.0
                else:
                    keys.append((int(parts[0]) / 1000.0, parts[1]))
    return keys, start


def demo_clip(clips: Path, name: str, host_rms: float) -> np.ndarray:
    wav = clips / f"{name}.wav"
    if not wav.exists():
        raise SystemExit(f"[DEMO {name}] has no recording: {wav}")
    speech = load_resampled(wav)
    keys, rec_start = read_keys(clips / f"{name}.keys.txt")
    # Older logs have no start entry; for them ~0.35 s was measured between the stopwatch and audio.
    lag = rec_start if rec_start is not None else CAPTURE_LAG
    first = max(0.0, (keys[0][0] - lag + 0.05) if keys else 0.0)
    speech *= host_rms / active_rms(speech)

    ticks = np.zeros_like(speech)
    c = click() * host_rms * 1.6
    for t, kind in keys:
        if kind == "x":
            continue
        i = int((t - lag) * SR)
        if 0 <= i < len(ticks) - len(c):
            ticks[i:i + len(c)] += c * (0.6 if kind == "t" else 1.0)
    start = int(first * SR)
    speech, ticks = speech[start:], ticks[start:]
    fade = min(len(speech), int(0.03 * SR))
    speech[:fade] *= np.linspace(0, 1, fade, dtype=np.float32)

    # Shorten long pauses in the speech; cut the click track identically so clicks stay in step.
    fl = int(0.02 * SR)
    n = len(speech) // fl
    rms = np.sqrt((speech[: n * fl].reshape(n, fl) ** 2).mean(1))
    quiet = rms < max(host_rms * 0.06, 2e-4)
    keep = np.ones(n, bool)
    i = 0
    while i < n:
        if quiet[i]:
            j = i
            while j < n and quiet[j]:
                j += 1
            if (j - i) * fl / SR > MAX_SILENCE:
                head, tail = int(KEEP_HEAD * SR / fl), int(KEEP_TAIL * SR / fl)
                keep[i + head:(j if j == n else j - tail)] = False
            i = j
        else:
            i += 1
    mask = np.repeat(keep, fl)
    return (speech[: n * fl] + ticks[: n * fl])[mask].astype(np.float32)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("project", type=Path, help="project folder (holds script.txt, clips/)")
    ap.add_argument("--script", default="script.txt", help="script file, relative to the project")
    ap.add_argument("--out", default=None, help="MP3 name in out/ (default: <script name>.mp3)")
    args = ap.parse_args()

    proj = args.project.resolve()
    script = proj / args.script
    meta, segs = parse(script.read_text(encoding="utf-8"))
    out_dir, cache, clips = proj / "out", proj / "cache", proj / "clips"
    out_dir.mkdir(exist_ok=True); cache.mkdir(exist_ok=True)

    models = data_dir() / "models"
    from kokoro_onnx import Kokoro
    kokoro = Kokoro(str(models / "kokoro-v1.0.onnx"), str(models / "voices-v1.0.bin"))
    known = set(kokoro.get_voices())
    for spk, v in meta["voices"].items():
        if v not in known:
            raise SystemExit(f"#voice {spk} {v}: no such Kokoro voice. Run samples.py to hear the choices.")

    speed = meta["speed"]

    def say(spk, text):
        voice = meta["voices"][spk]
        key = hashlib.sha1(f"{voice}|{speed}|{text}".encode()).hexdigest()[:16]
        f = cache / f"{key}.wav"
        if f.exists():
            a, _ = sf.read(f, dtype="float32"); return a
        lang = "en-gb" if voice.startswith("b") else "en-us"
        parts = []
        for ch in chunks(text):
            a, sr = kokoro.create(ch, voice=voice, speed=speed, lang=lang)
            parts += [a.astype(np.float32), silence(0.12)]
        a = np.concatenate(parts[:-1])
        sf.write(f, a, SR); return a

    host = {i: say(s[1], s[2]) for i, s in enumerate(segs) if s[0] == "SAY"}
    host_rms = float(np.median([active_rms(a) for a in host.values()])) if host else 0.1

    timeline, prev, marks = [silence(0.5)], None, []
    for i, s in enumerate(segs):
        if s[0] == "SAY":
            if prev is not None:
                timeline.append(silence(SAME_SPEAKER_GAP if prev == s[1] else SPEAKER_CHANGE_GAP))
            timeline.append(host[i]); prev = s[1]
        elif s[0] == "PAUSE":
            timeline.append(silence(PAUSE_SECONDS)); prev = None
        else:
            clip = demo_clip(clips, s[1], host_rms)
            start = sum(len(x) for x in timeline) / SR
            timeline += [silence(0.4), chime(True), silence(DEMO_LEAD), clip,
                         silence(DEMO_TAIL), chime(False), silence(0.4)]
            marks.append((s[1], start, len(clip) / SR)); prev = None
    timeline.append(silence(1.0))
    mix = np.concatenate(timeline)
    mix /= max(1.0, float(np.abs(mix).max()) / 0.95)

    name = args.out or (Path(args.script).stem + ".mp3")
    wav = out_dir / (Path(name).stem + ".wav")
    sf.write(wav, mix, SR)
    mp3 = out_dir / name
    tags = ["-metadata", f"title={meta['title']}"] if meta["title"] else []
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(wav),
                    "-af", "loudnorm=I=-16:TP=-1.5:LRA=11", "-ar", "44100", "-ac", "1",
                    "-codec:a", "libmp3lame", "-b:a", "128k", *tags, str(mp3)], check=True)
    total = len(mix) / SR
    demo_total = sum(d for _, _, d in marks)
    print(f"total {total / 60:.1f} min; hosts {(total - demo_total) / 60:.1f} min; demos {demo_total / 60:.1f} min")
    for nm, start, dur in marks:
        print(f"  {int(start // 60)}:{int(start % 60):02d}  {nm}  {dur:.1f}s")
    print(mp3)
    return 0


if __name__ == "__main__":
    sys.exit(main())
