"""Transcribe recordings with Whisper, on this PC, to check what the screen reader said.

    python -I transcribe.py <file.wav|file.mp3> [more files] [--from SECONDS] [--for SECONDS]
                            [--pitch | --pitch-only]

ffmpeg does the decoding and resampling (faster-whisper's own decoder breaks on some PyAV
versions). The VAD filter and condition_on_previous_text=False keep Whisper from looping on the
long silences screen reader recordings have. Whisper sometimes skips a stretch right after a chime;
if a stretch comes back empty, transcribe it again starting a second or two later before deciding
the audio is missing.

--pitch adds the speech's median pitch, which tells voices apart where a transcript can't: Narrator's
Zira (female) is around 180 Hz, David and Mark around 100-130 Hz.
"""
import argparse
import subprocess

import numpy as np

ap = argparse.ArgumentParser()
ap.add_argument("files", nargs="+")
ap.add_argument("--from", dest="start", type=float, default=None)
ap.add_argument("--for", dest="length", type=float, default=None)
ap.add_argument("--model", default="small.en")
ap.add_argument("--pitch", action="store_true", help="also print the median pitch")
ap.add_argument("--pitch-only", action="store_true", help="print only the median pitch")
args = ap.parse_args()


def load(path: str) -> np.ndarray:
    cmd = ["ffmpeg", "-loglevel", "error"]
    if args.start is not None:
        cmd += ["-ss", str(args.start)]
    if args.length is not None:
        cmd += ["-t", str(args.length)]
    cmd += ["-i", path, "-f", "s16le", "-ac", "1", "-ar", "16000", "-"]
    raw = subprocess.run(cmd, capture_output=True, check=True).stdout
    return np.frombuffer(raw, np.int16).astype("float32") / 32768


def median_pitch(x: np.ndarray, sr: int = 16000):
    """Median fundamental frequency of the voiced 40 ms frames, by autocorrelation."""
    fl = int(0.04 * sr)
    lo, hi = int(sr / 400), int(sr / 70)
    f0s = []
    for i in range(0, len(x) - fl, fl):
        fr = x[i:i + fl]
        if np.sqrt((fr ** 2).mean()) < 0.02:
            continue
        fr = fr - fr.mean()
        ac = np.correlate(fr, fr, "full")[fl - 1:]
        k = lo + int(np.argmax(ac[lo:hi]))
        if ac[k] > 0.4 * ac[0]:
            f0s.append(sr / k)
    return (float(np.median(f0s)), len(f0s)) if f0s else (None, 0)


model = None
for p in args.files:
    x = load(p)
    print(f"== {p}")
    if args.pitch or args.pitch_only:
        f0, frames = median_pitch(x)
        print(f"  pitch: {f0:.0f} Hz median over {frames} voiced frames" if f0 else "  pitch: no voiced speech found")
    if args.pitch_only:
        continue
    if model is None:
        from faster_whisper import WhisperModel
        model = WhisperModel(args.model, device="cpu", compute_type="int8")
    # The temperature list is Whisper's standard fallback: a window whose output repeats itself is
    # retried at a higher temperature, instead of looping on a short phrase.
    segs, _ = model.transcribe(x, language="en", beam_size=5, temperature=[0.0, 0.2, 0.4, 0.6, 0.8],
                               vad_filter=True, condition_on_previous_text=False)
    offset = args.start or 0.0
    for s in segs:
        t = s.start + offset
        print(f"  [{int(t // 60)}:{t % 60:04.1f}] {s.text.strip()}")
