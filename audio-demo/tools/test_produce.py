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


def test_measured_short_gap_keeps_answer():
    # From the review: the key cuts the leftover off with only a 30 ms gap before the answer.
    # The answer (1.58 s on) must not be silenced.
    x = track((0.5, 1.55), (1.58, 1.9), (1.96, 3.0))
    start, mute, shift, note = p.check_start(x, 1.47, 1.5, measured=True)
    assert mute is not None and approx(mute, 1.55, 0.015), mute


def test_measured_never_mutes_far_past_key():
    # Leftover that doesn't stop within MUTE_LIMIT of the key: warn rather than guess.
    x = track((0.5, 1.9), (1.95, 3.0))
    start, mute, shift, note = p.check_start(x, 1.47, 1.5, measured=True)
    assert mute is None and note.startswith("WARNING"), (mute, note)


def test_legacy_older_sound_is_leftover_not_answer():
    # Began 0.4 s before the cut: more than LEGACY_MISS, so it's leftover, and nothing moves.
    x = track((1.07, 1.7), (2.0, 3.0))
    start, mute, shift, note = p.check_start(x, 1.47, 1.5, measured=False)
    assert start == 1.47 and shift == 0.0 and mute is not None and approx(mute, 1.7), (start, shift, mute)


def test_sound_at_very_start_no_nan():
    # Sound from 0.03 s, cut at 0.2 s, legacy: no NaN warnings, and 'began' measured correctly.
    import warnings
    with warnings.catch_warnings():
        warnings.simplefilter("error")
        start, mute, shift, note = p.check_start(track((0.03, 1.0)), 0.2, 0.23, measured=False)
    assert approx(start, 0.0) and approx(shift, -0.2), (start, shift)
    assert "0.17 s into" in note, note


def test_measured_no_gap_warns():
    start, mute, shift, note = p.check_start(track((0.5, 4.0)), 1.47, 1.5, measured=True)
    assert mute is None and start == 1.47 and note.startswith("WARNING"), note


def test_legacy_early_answer_moves_back():
    # guessed lag too small: the answer began 0.2 s before the computed cut
    start, mute, shift, note = p.check_start(track((1.27, 2.5)), 1.47, 1.5, measured=False)
    assert mute is None and approx(start, 1.22), start
    assert shift < 0 and approx(shift, start - 1.47), shift
    assert "moved to match" in note, note


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


def expect_exit(script, fragment):
    try:
        p.parse(script)
    except SystemExit as e:
        assert fragment in str(e), e
    else:
        raise AssertionError(f"should have failed with '{fragment}'")


def test_script_errors():
    v = "#voice A af_heart\n"
    expect_exit(v + "#chapter\n[A]\nHi.\n", "needs a title")
    expect_exit(v + "#chapter One\n#chapter Two\n[A]\nHi.\n", "empty chapter")
    expect_exit(v + "[A]\nHi.\n#chapter Late\n", "nothing after it")
    expect_exit(v + "[A]\nHi.\n#chapter Late\n[PAUSE]\n", "nothing after it")
    expect_exit(v + "#pause 0\n[A]\nHi.\n", "out of range")
    expect_exit(v + "#pause -1\n[A]\nHi.\n", "out of range")


def test_timeline_chapters():
    # Two host lines of 1 s, a pause, a chapter, a demo of 2 s, a chapter, a host line.
    meta, segs = p.parse("#voice A af_heart\n#chapter One\n[A]\nFirst.\n[A]\nSecond.\n[PAUSE]\n"
                         "#chapter Two\n[DEMO d1]\n#chapter Three\n[A]\nThird.\n")
    host = {i: np.full(SR, 0.1, np.float32) for i, s in enumerate(segs) if s[0] == "SAY"}
    mix, marks, chapters, events = p.build_timeline(segs, host, lambda nm: np.full(2 * SR, 0.1, np.float32),
                                                    meta["pause"])
    titles = [t for _, t in chapters]
    assert titles == ["One", "Two", "Three"], titles
    assert chapters[0][0] == 0.0
    x = np.abs(mix)
    for start, title in chapters[1:]:
        i = int(start * SR)
        assert x[i:i + int(0.05 * SR)].max() == 0.0, f"chapter {title} doesn't start in silence"
        first_sound = i + int(np.argmax(x[i:] > 0))
        assert (first_sound - i) / SR <= p.CHAPTER_LEAD + 0.41, f"chapter {title} starts too early"
    # The heading for each chapter comes before the event it introduces.
    ev = [t for t, _, _ in events]
    assert all(any(abs(e - c) < 2.0 and c <= e for e in ev) for c, _ in chapters)
    assert len(marks) == 1 and marks[0][0] == "d1"


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
        except (Exception, SystemExit) as e:   # one broken test mustn't stop the rest
            failed += 1
            print(f"FAIL  {n}: {type(e).__name__}: {e}")
    print(f"{len(tests) - failed}/{len(tests)} passed")
    sys.exit(1 if failed else 0)
