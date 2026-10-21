"""Self-tests for produce.py's pure parts: clip-start checking, script parsing, chapter metadata.

    python -I test_produce.py

No models, recordings or VM needed. The audio cases are synthetic: digital silence with bursts of
"speech" (a tone), which is what the virtual cable records.
"""
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import produce as p  # noqa: E402

SR = p.SR


def track(*spans, length=6.0):
    """Silence with a 0.1-amplitude tone over each (start, end) span in seconds."""
    x = np.zeros(int(length * SR), np.float32)
    for a, b in spans:
        t = np.arange(int(a * SR), int(b * SR))
        x[t] = 0.1 * np.sin(2 * np.pi * 220 * t / SR)
    return x


def approx(a, b, tol=0.03):
    return abs(a - b) <= tol


def test_start_in_silence():
    start, mute, shift, note = p.check_start(track((2.0, 3.0)), 1.47, 1.5, measured=True)
    assert (start, mute, shift, note) == (1.47, None, 0.0, ""), (start, mute, shift, note)


def test_measured_leftover_is_silenced():
    # earlier announcement 0.5-1.7 s, key at 1.5 s, answer from 1.9 s
    start, mute, shift, note = p.check_start(track((0.5, 1.7), (1.9, 3.0)), 1.47, 1.5, measured=True)
    assert start == 1.47 and shift == 0.0
    assert mute is not None and approx(mute, 1.7), mute
    assert "leftover" in note


def test_measured_answer_never_silenced():
    # A measured log can't have an answer before the key, but speech right at the cut that
    # stops before the key is still leftover; the answer after the gap must survive.
    x = track((1.40, 1.55), (1.75, 3.0))
    start, mute, shift, note = p.check_start(x, 1.47, 1.5, measured=True)
    assert mute is not None and mute < 1.75, mute


def test_measured_no_gap_warns():
    start, mute, shift, note = p.check_start(track((0.5, 4.0)), 1.47, 1.5, measured=True)
    assert mute is None and start == 1.47 and note.startswith("WARNING"), note


def test_legacy_early_answer_moves_back():
    # guessed lag too small: the answer began 0.2 s before the computed cut
    start, mute, shift, note = p.check_start(track((1.27, 2.5)), 1.47, 1.5, measured=False)
    assert mute is None and approx(start, 1.22), start
    assert shift < 0 and approx(shift, start - 1.47), shift
    assert "re-timed" in note


def test_legacy_long_leftover_is_silenced():
    start, mute, shift, note = p.check_start(track((0.3, 1.7), (2.0, 3.0)), 1.47, 1.5, measured=False)
    assert start == 1.47 and mute is not None and approx(mute, 1.7), (start, mute)


def test_parse_chapters_and_pause():
    meta, segs = p.parse("#voice A af_heart\n#pause 2.5\n#chapter One\n[A]\nHello.\n"
                         "[PAUSE]\n#chapter Two; with = signs\n[A]\nBye.\n")
    assert meta["pause"] == 2.5
    kinds = [s[0] for s in segs]
    assert kinds == ["CHAPTER", "SAY", "PAUSE", "CHAPTER", "SAY"], kinds
    assert segs[3] == ("CHAPTER", "Two; with = signs")


def test_chapter_ends_turn():
    try:
        p.parse("#voice A af_heart\n[A]\nHello.\n#chapter Two\nstray text\n")
    except SystemExit as e:
        assert "no host" in str(e), e
    else:
        raise AssertionError("text after #chapter without a [HOST] marker should fail")


def test_default_pause():
    meta, _ = p.parse("#voice A af_heart\n[A]\nHi.\n")
    assert meta["pause"] == p.DEFAULT_PAUSE == 1.8


def test_only_chapters_is_empty():
    try:
        p.parse("#voice A af_heart\n#chapter Nothing\n")
    except SystemExit as e:
        assert "no host turns" in str(e), e
    else:
        raise AssertionError("a script with only chapters has nothing to play")


def test_ffmetadata():
    text = p.ffmetadata([(0.0, "Intro"), (61.25, "Keys; = and #")], 120.0)
    assert text.startswith(";FFMETADATA1\n")
    assert "START=0\nEND=61250\ntitle=Intro" in text
    assert "START=61250\nEND=120000\ntitle=Keys\\; \\= and \\#" in text


if __name__ == "__main__":
    tests = [(n, f) for n, f in sorted(globals().items()) if n.startswith("test_") and callable(f)]
    failed = 0
    for n, f in tests:
        try:
            f()
            print(f"ok    {n}")
        except AssertionError as e:
            failed += 1
            print(f"FAIL  {n}: {e}")
    print(f"{len(tests) - failed}/{len(tests)} passed")
    sys.exit(1 if failed else 0)
