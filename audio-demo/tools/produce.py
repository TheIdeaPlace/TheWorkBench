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
    #pause  1.8                                optional: length of a [PAUSE] in seconds, default 1.8
    # any other line starting with '#' is a comment
    #chapter Getting around                    starts an MP3 chapter here (positional, unlike the rest)
    [MICHAEL]
    Welcome to ...
    [PAUSE]                                    a section break
    [DEMO d01-orient]                          insert clips/d01-orient.wav here

Host pacing follows Quill's podcast generator (Community-Access/quill, docs/podcast/tools).

    python -I produce.py <project folder> [--script script.txt] [--out name.mp3] [--transcript]

With #chapter lines, the MP3 gets chapters (podcast apps that support them let listeners jump
between sections) and the chapter list is printed for show notes. --transcript also writes
out/<name>-transcript.txt: chapter headings, each host line from the script, and each demo as
Whisper hears it after trimming. Narrator's lines there are machine transcription: read them
before publishing.
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
# A section break has to be clearly longer than the gaps inside a turn and between speakers, or a
# listener can't hear that the topic changed: 0.9 s was barely longer than ordinary pauses.
SAME_SPEAKER_GAP, SPEAKER_CHANGE_GAP, DEFAULT_PAUSE = 0.18, 0.32, 1.8
CHAPTER_LEAD = 0.3                             # a chapter starts this far before its first sound, in silence
QUIET = 1e-3                                   # about -60 dBFS: the virtual cable gives digital silence
                                               # between utterances, so anything above this is speech
MUTE_LIMIT = 0.25                              # silence leftover speech no further than this past the first key
LEGACY_MISS = 0.3                              # older logs: how far a guessed lag may have cut into an answer
DEMO_LEAD, DEMO_TAIL = 0.45, 0.6               # silence around a demo, inside the chimes
MAX_SILENCE, KEEP_HEAD, KEEP_TAIL = 1.1, 0.30, 0.45   # pause shortening inside clips
CAPTURE_LAG = 0.35                             # ffmpeg starts a moment after the key stopwatch
MAX_CHARS = 360


def data_dir() -> Path:
    return Path(os.environ.get("AUDIODEMO_DATA") or Path(os.environ["LOCALAPPDATA"]) / "audio-demo")


DIRECTIVE = re.compile(r"^#(voice|speed|title|pause|chapter)\s+(.*)$")   # no space after '#': '# voice ...' is a comment
MARKER = re.compile(r"^\[([A-Z][A-Z0-9_]*)(?:\s+([\w.-]+))?\]$")


def parse(text: str):
    """Return (directives, segments). Segments are ('SAY', speaker, text) | ('PAUSE',) | ('DEMO', name)
    | ('CHAPTER', title).

    Strict on purpose: a mistyped marker or stray text fails the build with its line number rather
    than being dropped, or worse, read aloud by a host.
    """
    meta = {"voices": {}, "speed": DEFAULT_SPEED, "title": "", "pause": DEFAULT_PAUSE}
    segs, cur, buf = [], None, []
    chapter_line = 0

    def flush():
        if buf:
            segs.append(("SAY", cur, " ".join(buf)))
        buf.clear()

    for n, line in enumerate(text.splitlines(), start=1):
        t = line.strip().lstrip("﻿")
        if not t:
            continue
        if t.startswith("#"):
            if re.match(r"^#chapter\s*$", t, re.I):
                raise SystemExit(f"line {n}: '#chapter' needs a title, e.g. #chapter Search")
            d = DIRECTIVE.match(t)
            if d:
                key, value = d.group(1), d.group(2).strip()
                if key == "voice":
                    parts = value.split()
                    if len(parts) != 2:
                        raise SystemExit(f"line {n}: '#voice' takes a host name and a Kokoro voice, e.g. #voice MICHAEL am_michael")
                    meta["voices"][parts[0].upper()] = parts[1]
                elif key in ("speed", "pause"):
                    example = "#speed 1.03" if key == "speed" else "#pause 1.8"
                    try:
                        meta[key] = float(value)
                    except ValueError:
                        raise SystemExit(f"line {n}: '#{key}' needs a number, e.g. {example}")
                    if not 0 < meta[key] <= 10:
                        raise SystemExit(f"line {n}: '#{key} {value}' is out of range, e.g. {example}")
                elif key == "chapter":
                    # Positional: the chapter starts where this line sits. It also ends the current
                    # host turn, so text after it needs a [HOST] marker again.
                    flush()
                    if segs and segs[-1][0] == "CHAPTER":
                        raise SystemExit(f"line {n}: two #chapter lines with nothing between them make an "
                                         "empty chapter. Remove one.")
                    segs.append(("CHAPTER", value)); cur = None
                    chapter_line = n
                else:
                    meta["title"] = value
            continue
        if t.startswith("[") and t.endswith("]"):
            m = MARKER.match(t)
            if not m:
                raise SystemExit(f"line {n}: '{t}' isn't a marker. Use [HOST], [PAUSE] or [DEMO clip-name], in capitals.")
            flush()
            tag = m.group(1)
            if tag == "PAUSE":
                segs.append(("PAUSE",)); cur = None
            elif tag == "DEMO":
                if not m.group(2):
                    raise SystemExit(f"line {n}: [DEMO] needs a clip name, e.g. [DEMO d01-orient]")
                segs.append(("DEMO", m.group(2))); cur = None
            else:
                if tag not in meta["voices"]:
                    raise SystemExit(f"line {n}: [{tag}] has no '#voice {tag} <kokoro voice>' line in the script")
                cur = tag
            continue
        if cur is None:
            raise SystemExit(f"line {n}: text with no host. Put a [HOST] marker before it: {t[:60]}")
        buf.append(t)
    flush()
    if not meta["voices"]:
        raise SystemExit("The script declares no hosts. Add lines like: #voice MICHAEL am_michael")
    if not any(s[0] in ("SAY", "DEMO") for s in segs):
        raise SystemExit("The script has no host turns or demos.")
    chapters = [i for i, s in enumerate(segs) if s[0] == "CHAPTER"]
    last_played = max(i for i, s in enumerate(segs) if s[0] in ("SAY", "DEMO"))
    if chapters and chapters[-1] > last_played:
        raise SystemExit(f"line {chapter_line}: this #chapter has nothing after it to play.")
    if chapters and any(s[0] in ("SAY", "DEMO") for s in segs[:chapters[0]]):
        print("  note: the first #chapter isn't at the top, so the opening isn't in any chapter. "
              "Add a #chapter before the first line.", file=sys.stderr)
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


def check_start(speech: np.ndarray, first: float, key: float | None, measured: bool):
    """Make sure a clip doesn't open in the middle of a word.

    `first` is where the clip would start (just before the first logged key, at `key`). Returns
    (start, mute_to, click_shift, note): start the clip at `start`, silence the speech in
    [start, mute_to) if mute_to is set, move every key click by `click_shift` seconds (negative is
    earlier; 0 normally), and report `note` in the build log ("" when nothing was wrong).

    - Measured logs (with a 'start' entry): the screen reader can't answer a key before it's
      pressed, so speech already sounding at the cut is leftover from earlier, usually a startup or
      focus announcement. All of it before the key is silenced, then up to the first gap of 20 ms
      or more after the key, and the start, with its key click, stays where it was. Without that,
      the listener heard a stray fragment ("...quarter.") before the demo began. A key cuts the
      screen reader off almost at once, so nothing is silenced more than MUTE_LIMIT after the key:
      if no gap comes by then, the answer may already be playing, and it reports a WARNING instead
      of risking the answer.
    - Older logs use a guessed lag, which was sometimes too small, so the "leftover" can really be
      the answer to the first key. If the sound began at most LEGACY_MISS before the cut, treat it
      as that answer and start just before it, moving the clicks to match. Otherwise it's leftover,
      silenced up to its first 50 ms pause (the key time is a guess, so before and after it can't
      be told apart). The move-back is what fixed "Unread" clipped to "...read" in the first
      QuickMail demo.
    """
    fl = int(0.01 * SR)

    def quiet(i, n=1):
        """True if the n 10 ms frames from sample i are all below QUIET (outside the clip counts as quiet)."""
        for j in range(i, i + n * fl, fl):
            if j < 0 or j >= len(speech):
                continue
            if float(np.sqrt((speech[j:j + fl] ** 2).mean())) >= QUIET:
                return False
        return True

    i = int(first * SR)
    if i >= len(speech) or quiet(i):
        return first, None, 0.0, ""
    b = i
    while b > 0 and not quiet(b - 5 * fl, 5):               # back to the start of this sound
        b -= fl
    began = (i - b) / SR
    if not measured and began <= LEGACY_MISS:
        new = max(0.0, b / SR - 0.05)
        return new, None, new - first, (f"opened {began:.2f} s into the screen reader's first answer: "
                                        f"started {first - new:.2f} s earlier, key clicks moved to match")
    # Leftover speech: find where it stops.
    # - Measured log: before the key nothing can be the answer yet, so all of it is leftover. After
    #   the key the answer can follow within tens of milliseconds, so stop at the first 20 ms gap.
    # - Older log: the key time is a guess, so "before" and "after" can't be told apart. Stop at the
    #   first real pause (50 ms), which bridges the short gaps between words.
    # Either way, give up MUTE_LIMIT past the key (plus LEGACY_MISS for a guessed key).
    k = int((key if key is not None else first) * SR)
    reach = MUTE_LIMIT + (0.0 if measured else LEGACY_MISS)
    limit = min(len(speech), int((k / SR + reach) * SR))
    f = i
    while f < limit:
        if measured:
            if f >= k and quiet(f, 2):
                break
        elif quiet(f, 5):
            break
        f += fl
    if f >= limit:
        return first, None, 0.0, ("WARNING: speech runs through the first key with no gap; the clip may "
                                  "open mid-word. Listen to its start, or re-record with a longer wait first")
    return first, f / SR, 0.0, f"silenced {(f - i) / SR:.2f} s of leftover speech at the start"


def demo_clip(clips: Path, name: str, host_rms: float) -> np.ndarray:
    wav = clips / f"{name}.wav"
    if not wav.exists():
        raise SystemExit(f"[DEMO {name}] has no recording: {wav}")
    speech = load_resampled(wav)
    keys, rec_start = read_keys(clips / f"{name}.keys.txt")
    # Older logs have no start entry; for them ~0.35 s was measured between the stopwatch and audio.
    lag = rec_start if rec_start is not None else CAPTURE_LAG
    # Start just before the first logged step: late enough to drop the tail of earlier speech, early
    # enough to keep that step's click. A clip with no logged steps is used from the start.
    first = max(0.0, (keys[0][0] - lag - 0.03) if keys else 0.0)
    if keys:
        first, mute_to, shift, note = check_start(speech, first, keys[0][0] - lag, rec_start is not None)
        lag -= shift          # clicks sit at (key - lag): a negative shift moves them earlier
        if mute_to is not None:
            a, z = int(first * SR), int(mute_to * SR)
            fade = min(z - a, int(0.01 * SR))
            speech[a:a + fade] *= np.linspace(1, 0, fade, dtype=np.float32)
            speech[a + fade:z] = 0.0
        if note:
            print(f"  [DEMO {name}] start: {note}", file=sys.stderr if note.startswith("WARNING") else sys.stdout)
    if float(np.abs(speech).max(initial=0.0)) < 1e-3:
        print(f"  WARNING: clip {name} is silent. Is the screen reader running and speaking into the cable?",
              file=sys.stderr)
    else:
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
    has_click = np.abs(ticks[: n * fl]).reshape(n, fl).max(1) > 0
    # A frame with a click is never "quiet": a key the screen reader says nothing about (a flag
    # toggled, say) still gets heard being pressed.
    quiet = (rms < max(host_rms * 0.06, 2e-4)) & ~has_click
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
    out = (speech[: n * fl] + ticks[: n * fl])[mask].astype(np.float32)
    if len(out) == 0:
        raise SystemExit(f"[DEMO {name}] is empty after trimming. Does its keys log belong to this recording?")
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("project", type=Path, help="project folder (holds script.txt, clips/)")
    ap.add_argument("--script", default="script.txt", help="script file, relative to the project")
    ap.add_argument("--out", default=None, help="MP3 name in out/ (default: <script name>.mp3)")
    ap.add_argument("--transcript", action="store_true",
                    help="also write out/<name>-transcript.txt (needs faster-whisper; adds a minute or two)")
    args = ap.parse_args()

    proj = args.project.resolve()
    script = proj / args.script
    meta, segs = parse(script.read_text(encoding="utf-8"))
    if args.transcript:
        try:
            import faster_whisper  # noqa: F401  (checked now, before a long build, not after it)
        except ImportError:
            raise SystemExit("--transcript needs faster-whisper in this Python environment. Run install.ps1, "
                             "or build without --transcript.")
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
        # Write then rename, so an interrupted build never leaves a truncated line in the cache.
        tmp = f.with_name(f.stem + ".partial.wav")
        sf.write(tmp, a, SR)
        os.replace(tmp, f)
        return a

    host = {i: say(s[1], s[2]) for i, s in enumerate(segs) if s[0] == "SAY"}
    host_rms = float(np.median([active_rms(a) for a in host.values()])) if host else 0.1

    mix, marks, chapters, events = build_timeline(
        segs, host, lambda nm: demo_clip(clips, nm, host_rms), meta["pause"])
    total = len(mix) / SR

    name = args.out or (Path(args.script).stem + ".mp3")
    wav = out_dir / (Path(name).stem + ".wav")
    sf.write(wav, mix, SR)
    mp3 = out_dir / name
    # ID3v2.3 always (ffmpeg's default is 2.4): the widest player support, chapters or not.
    inputs, maps = ["-i", str(wav)], ["-map", "0:a", "-id3v2_version", "3"]
    tags = ["-metadata", f"title={meta['title']}"] if meta["title"] else []
    if chapters:
        meta_file = out_dir / (Path(name).stem + ".chapters.txt")
        meta_file.write_text(ffmetadata(chapters, total), encoding="utf-8")
        inputs += ["-i", str(meta_file)]
        maps += ["-map_chapters", "1"]
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", *inputs, *maps,
                    "-af", "loudnorm=I=-16:TP=-1.5:LRA=11", "-ar", "44100", "-ac", "1",
                    "-codec:a", "libmp3lame", "-b:a", "128k", *tags, str(mp3)], check=True)
    demo_total = sum(d for _, _, d in marks)
    print(f"total {total / 60:.1f} min; hosts {(total - demo_total) / 60:.1f} min; demos {demo_total / 60:.1f} min")
    for nm, start, dur in marks:
        print(f"  {clock(start)}  {nm}  {dur:.1f}s")
    if chapters:
        print("chapters (also in the MP3; for show notes):")
        for start, title in chapters:
            print(f"  {clock(start)}  {title}")
    if args.transcript:
        out_txt = out_dir / (Path(name).stem + "-transcript.txt")
        write_transcript(out_txt, meta, events, chapters, mp3.name)
        print(out_txt)
    print(mp3)
    return 0


def build_timeline(segs, host, clip_for, pause):
    """Lay the script out in time. Returns (mix, marks, chapters, events).

    host: {segment index: rendered host line}; clip_for(name) -> the prepared demo clip.
    marks: (demo name, start s, length s), where start is before the demo's chime.
    chapters: (start s, title). Each starts CHAPTER_LEAD before its first sound, but never before
    the end of the previous sound, so it always lands in silence; the first one, with nothing
    heard before it, starts at 0.
    events: (start s, speaker or "DEMO", text or clip audio) for the transcript, in order.
    """
    timeline, prev, marks, pos, sound_end = [silence(0.5)], None, [], int(0.5 * SR), 0
    chapters, pending, events = [], [], []

    def add(x):
        nonlocal pos
        timeline.append(x)
        pos += len(x)

    def open_chapters(at):
        for title in pending:
            chapters.append((0.0 if sound_end == 0 else max(sound_end / SR, at / SR - CHAPTER_LEAD), title))
        pending.clear()

    for i, s in enumerate(segs):
        if s[0] == "SAY":
            if prev is not None:
                add(silence(SAME_SPEAKER_GAP if prev == s[1] else SPEAKER_CHANGE_GAP))
            open_chapters(pos)
            events.append((pos / SR, s[1], s[2]))
            add(host[i]); prev = s[1]; sound_end = pos
        elif s[0] == "PAUSE":
            add(silence(pause)); prev = None
        elif s[0] == "CHAPTER":
            pending.append(s[1])
        else:
            clip = clip_for(s[1])
            start = pos / SR
            add(silence(0.4))
            open_chapters(pos)
            add(chime(True)); add(silence(DEMO_LEAD))
            events.append((pos / SR, "DEMO", clip))
            add(clip); add(silence(DEMO_TAIL)); add(chime(False)); sound_end = pos; add(silence(0.4))
            marks.append((s[1], start, len(clip) / SR)); prev = None
    add(silence(1.0))
    mix = np.concatenate(timeline)
    mix /= max(1.0, float(np.abs(mix).max()) / 0.95)
    return mix, marks, chapters, events


def clock(t: float) -> str:
    return f"{int(t // 60)}:{int(t % 60):02d}"


def ffmetadata(chapters, total: float) -> str:
    """An ffmpeg metadata file with one [CHAPTER] per entry; each ends where the next starts."""
    def esc(s):   # ffmetadata escapes '=', ';', '#', '\' and newlines with a backslash
        return re.sub(r"([=;#\\\n])", r"\\\1", s)
    lines = [";FFMETADATA1"]
    for k, (start, title) in enumerate(chapters):
        end = chapters[k + 1][0] if k + 1 < len(chapters) else total
        lines += ["[CHAPTER]", "TIMEBASE=1/1000", f"START={int(round(start * 1000))}",
                  f"END={int(round(end * 1000))}", f"title={esc(title)}"]
    return "\n".join(lines) + "\n"


def write_transcript(path: Path, meta, events, chapters, mp3_name: str):
    """Chapter headings, host lines from the script (exact), demos as Whisper hears them."""
    from faster_whisper import WhisperModel   # only needed here; installed by install.ps1
    model = WhisperModel("small.en", device="cpu", compute_type="int8")
    lines = ([meta["title"]] if meta["title"] else []) + [
        "Transcript", f"Times are positions in {mp3_name}; a demo's time is where its audio starts, after the chime.",
        "Demo lines are automatic speech recognition of the screen reader: check them before publishing.", ""]
    ci = 0
    for t, kind, payload in events:
        # A chapter never starts after its first event, so this places each heading just before it.
        while ci < len(chapters) and chapters[ci][0] <= t + 1e-6:
            lines += ["", f"== {chapters[ci][1]} ({clock(chapters[ci][0])}) ==", ""]
            ci += 1
        if kind == "DEMO":
            # Same settings as transcribe.py: VAD and no conditioning keep Whisper from looping on
            # the long silences. The audio is the trimmed clip, as in the MP3 before final levelling.
            pcm = subprocess.run(["ffmpeg", "-loglevel", "error", "-f", "f32le", "-ar", str(SR), "-ac", "1",
                                  "-i", "-", "-f", "f32le", "-ar", "16000", "-ac", "1", "-"],
                                 input=payload.astype(np.float32).tobytes(), capture_output=True, check=True).stdout
            segs, _ = model.transcribe(np.frombuffer(pcm, np.float32), language="en", beam_size=5,
                                       temperature=0, vad_filter=True, condition_on_previous_text=False)
            text = " ".join(s.text.strip() for s in segs) or "(no speech recognized; listen to this demo)"
            lines.append(f"[Demo, {clock(t)}] {text}")
        else:
            lines.append(f"{kind.title()}: {' '.join(payload.split())}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
