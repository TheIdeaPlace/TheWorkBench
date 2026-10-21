"""Render a short sample of each Kokoro voice, so people can choose hosts by ear.

    python -I samples.py <out folder> [--prefix a,b] [--text "..."]

--prefix picks voices by their first letter: a = American English, b = British English
(the others are e Spanish, f French, h Hindi, i Italian, j Japanese, p Portuguese, z Mandarin;
those need their own text). Writes one MP3 per voice plus 00-all-voices.mp3 with every sample in a row.
"""
import argparse
import os
import subprocess
from pathlib import Path

import numpy as np
import soundfile as sf
from kokoro_onnx import Kokoro

ACCENTS = {"a": ("American English", "en-us"), "b": ("British English", "en-gb")}
SEX = {"f": "female", "m": "male"}

ap = argparse.ArgumentParser()
ap.add_argument("out", type=Path)
ap.add_argument("--prefix", default="a,b")
ap.add_argument("--text", default="This app is built screen reader first. "
                "Press control shift P to open the command palette, and F6 to move between panes.")
args = ap.parse_args()

models = Path(os.environ.get("AUDIODEMO_DATA") or Path(os.environ["LOCALAPPDATA"]) / "audio-demo") / "models"
k = Kokoro(str(models / "kokoro-v1.0.onnx"), str(models / "voices-v1.0.bin"))
prefixes = tuple(p.strip() for p in args.prefix.split(","))
args.out.mkdir(parents=True, exist_ok=True)


def mp3(wav: Path, dest: Path):
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-i", str(wav),
                    "-codec:a", "libmp3lame", "-b:a", "96k", str(dest)], check=True)
    wav.unlink()


everything = []
for v in sorted(x for x in k.get_voices() if x.startswith(prefixes)):
    accent, lang = ACCENTS.get(v[0], ("", "en-us"))
    who = v.split("_", 1)[1].capitalize()
    intro = f"This is {who}, an {accent} {SEX.get(v[1], '')} voice. " if accent else f"This is {who}. "
    audio, sr = k.create(intro + args.text, voice=v, speed=1.0, lang=lang)
    wav = args.out / f"{v}.wav"
    sf.write(wav, audio, sr)
    mp3(wav, args.out / f"{v}.mp3")
    everything += [audio, np.zeros(int(0.8 * sr), np.float32)]
    print(v, flush=True)

if everything:
    wav = args.out / "all.wav"
    sf.write(wav, np.concatenate(everything), 24000)
    mp3(wav, args.out / "00-all-voices.mp3")
