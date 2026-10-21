"""Transcribe recordings with Whisper, on this PC, to check what the screen reader said.

    python -I transcribe.py <file.wav|file.mp3> [more files] [--from SECONDS] [--for SECONDS]

ffmpeg does the decoding and resampling (faster-whisper's own decoder breaks on some PyAV
versions). The VAD filter and condition_on_previous_text=False keep Whisper from looping on the
long silences screen reader recordings have. Whisper sometimes skips a stretch right after a chime;
if a stretch comes back empty, transcribe it again starting a second or two later before deciding
the audio is missing.
"""
import argparse
import subprocess

import numpy as np
from faster_whisper import WhisperModel

ap = argparse.ArgumentParser()
ap.add_argument("files", nargs="+")
ap.add_argument("--from", dest="start", type=float, default=None)
ap.add_argument("--for", dest="length", type=float, default=None)
ap.add_argument("--model", default="small.en")
args = ap.parse_args()

model = WhisperModel(args.model, device="cpu", compute_type="int8")
for p in args.files:
    cmd = ["ffmpeg", "-loglevel", "error"]
    if args.start is not None:
        cmd += ["-ss", str(args.start)]
    if args.length is not None:
        cmd += ["-t", str(args.length)]
    cmd += ["-i", p, "-f", "s16le", "-ac", "1", "-ar", "16000", "-"]
    raw = subprocess.run(cmd, capture_output=True, check=True).stdout
    x = np.frombuffer(raw, np.int16).astype("float32") / 32768
    segs, _ = model.transcribe(x, language="en", beam_size=5, temperature=0,
                               vad_filter=True, condition_on_previous_text=False)
    print(f"== {p}")
    offset = args.start or 0.0
    for s in segs:
        t = s.start + offset
        print(f"  [{int(t // 60)}:{t % 60:04.1f}] {s.text.strip()}")
