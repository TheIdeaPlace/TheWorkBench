"""Self-tests for speechlog.py (NVDA log parsing) and srcheck.py (expected speech). No VM, no models.

    python -I test_srcheck.py
"""
from __future__ import annotations

import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import speechlog as sl  # noqa: E402
import srcheck as sc  # noqa: E402


def test_speech_strings_skips_commands():
    seq = "[LangChangeCommand ('en_US'), 'Inbox', 'tree view', CancellableSpeech (still valid), '4 unread']"
    assert sl.speech_strings(seq) == ["Inbox", "tree view", "4 unread"], sl.speech_strings(seq)


def test_speech_strings_quotes_and_escapes():
    seq = "[\"it's here\", 'say \\'hi\\'', BreakCommand(time=100), 'a, b']"
    assert sl.speech_strings(seq) == ["it's here", "say 'hi'", "a, b"], sl.speech_strings(seq)


def test_speech_strings_whitespace_and_empty():
    assert sl.speech_strings("['  two   words ', '', '\\n']") == ["two words"]


def test_parse_log_and_clip_window():
    d = Path(tempfile.mkdtemp())
    log = d / "nvda.log"
    log.write_text(
        "INFO - __main__ (10:00:00.000) - MainThread (1):\nStarting NVDA\n"
        "IO - speech.speech.speak (10:00:01.000) - MainThread (1):\nSpeaking ['before']\n"
        "IO - speech.speech.speak (10:00:05.500) - MainThread (1):\nSpeaking [LangChangeCommand ('en_US'), 'Untitled - Notepad']\n"
        "IO - inputCore.InputManager.executeGesture (10:00:06.000) - MainThread (1):\nInput: kb(desktop):downArrow\n"
        "IO - speech.speech.speak (10:00:07.250) - MainThread (1):\nSpeaking ['Text editor', 'blank']\n"
        "IO - speech.speech.speak (10:00:30.000) - MainThread (1):\nSpeaking ['after']\n",
        encoding="utf-8")
    keys = d / "c.keys.txt"
    keys.write_text("0\tclock\t10:00:04.000\t2026-10-21\n500\tstart\trecording\n1200\tx\tlaunch\t2\n10000\tend\trecording\n",
                    encoding="utf-8")
    got = sl.clip_speech(log, keys)
    assert got == [(1.5, "Untitled - Notepad"), (3.25, "Text editor blank")], got


def test_clip_window_across_midnight():
    d = Path(tempfile.mkdtemp())
    log = d / "nvda.log"
    log.write_text("IO - speech.speech.speak (00:00:01.000) - MainThread (1):\nSpeaking ['new day']\n", encoding="utf-8")
    keys = d / "k.txt"
    keys.write_text("0\tclock\t23:59:59.000\t2026-10-21\n5000\tend\trecording\n", encoding="utf-8")
    assert sl.clip_speech(log, keys) == [(2.0, "new day")]


def _project(steps: str, keys: str, speech: str) -> Path:
    d = Path(tempfile.mkdtemp())
    (d / "steps").mkdir(); (d / "clips").mkdir()
    (d / "steps" / "t.steps").write_text(steps, encoding="utf-8")
    (d / "clips" / "t.keys.txt").write_text(keys, encoding="utf-8")
    (d / "clips" / "t.speech.txt").write_text(speech, encoding="utf-8")
    return d


def _run(d: Path):
    steps = sc.parse_steps(d / "steps" / "t.steps")
    keys = d / "clips" / "t.keys.txt"
    runs, start, end = sc.read_log(keys)
    sc.align(steps, runs, start, keys)
    speech = sl.read_speech(d / "clips" / "t.speech.txt")
    sc.assign(steps, end, speech)
    return steps, sc.check(steps, exact=True)


STEPS = "e Welcome\nk {DOWN}\nw 2\ne Sam Patel\ne! read\ne/ \\d+ of \\d+\nk {TAB}\ne0\nt hi\ne Hi\nk {F6}\ne never\n"
KEYS = ("0\tclock\t10:00:00.000\t2026-10-21\n100\tstart\trecording\n1000\tk\t{DOWN}\t2\n3000\tk\t{TAB}\t7\n"
        "4000\tt\th\t9\n4200\tt\ti\t9\n9000\tend\trecording\n")
SPEECH = "500\tWelcome to the app\n1200\tunread, Sam Patel, 1 of 12\n4500\tHi\n"


def test_expectations_pass_and_fail():
    steps, problems = _run(_project(STEPS, KEYS, SPEECH))
    results = {e.line: e.result for s in steps for e in s.expects}
    assert results[1] == "PASS"            # before the first step
    assert results[4] == "PASS"            # said, case and punctuation ignored
    assert results[5] == "PASS", results   # "read" is not said: "unread" is a different word
    assert results[6] == "PASS"            # regex
    assert results[8] == "PASS"            # silence after Tab
    assert results[10] == "PASS"           # typed text: window starts at its first character
    assert results[12] == "FAIL"           # F6 was never pressed: step not in the log
    assert problems == 1, problems


def test_not_said_is_whole_words():
    # 'read' must not match inside 'unread', or e! read could never pass on an unread message.
    assert sc.norm("unread, Sam") == "unread sam"
    steps, _ = _run(_project("k {DOWN}\ne! read\n", "0\tclock\t10:00:00.000\t2026-10-21\n1000\tk\t{DOWN}\t1\n",
                             "1200\tunread, Sam Patel\n"))
    assert steps[1].expects[0].result == "PASS", steps[1].expects[0]


def test_baseline_adds_heard_speech():
    d = _project("k {DOWN}\nw 1\nk {UP}\ne kept\n", "0\tclock\t10:00:00.000\t2026-10-21\n1000\tk\t{DOWN}\t1\n"
                 "2000\tk\t{UP}\t3\n", "1100\tSam Patel\n1300\tunread\n2100\tkept\n")
    steps, _ = _run(d)
    text = sc.baseline(d / "steps" / "t.steps", steps)
    # The heard speech goes after the step's wait, where a hand-written expectation would sit.
    assert text == "k {DOWN}\nw 1\ne Sam Patel\ne unread\nk {UP}\ne kept\n", repr(text)


def test_editing_expectations_after_recording_keeps_alignment():
    # Recorded with no expectations; then e lines and a comment were added, moving every line number.
    keys = ("0\tclock\t10:00:00.000\t2026-10-21\n100\tstart\trecording\n1000\tk\t{DOWN}\t1\n"
            "3000\tk\t{TAB}\t3\n9000\tend\trecording\n")
    edited = "k {DOWN}\n# added later\ne Sam Patel\nw 2\nk {TAB}\ne Back button\n"
    steps, problems = _run(_project(edited, keys, "1200\tSam Patel\n3100\tBack button\n"))
    assert problems == 0, [(e.line, e.why) for s in steps for e in s.expects]


def test_changed_key_after_recording_is_refused():
    keys = "0\tclock\t10:00:00.000\t2026-10-21\n1000\tk\t{DOWN}\t1\n9000\tend\trecording\n"
    try:
        _run(_project("k {UP}\ne Sam\n", keys, "1200\tSam\n"))
    except SystemExit as e:
        assert "changed after recording" in str(e), e
    else:
        raise AssertionError("no error")


def test_typed_text_and_nvda_key_steps_align():
    keys = ("0\tclock\t10:00:00.000\t2026-10-21\n1000\tt\ta\t1\n1200\tt\t \t1\n1400\tt\tb\t1\n"
            "3000\tk\tNVDA+t\t3\n9000\tend\trecording\n")
    steps, problems = _run(_project("t a b\nw 1\nn t\ne Title\n", keys, "3100\tTitle - App\n"))
    assert problems == 0 and steps[1].start == 1.0 and steps[2].start == 3.0


def test_whisper_times_move_onto_the_stopwatch():
    # Whisper counts from the WAV's start, which is the log's 'start' entry on the stopwatch.
    assert sc.shift([(0.5, "hello")], 2.25) == [(2.75, "hello")]


def test_empty_nvda_log_is_untrusted():
    d = _project("k {DOWN}\ne0\ne! Sam\n", "0\tclock\t10:00:00.000\t2026-10-21\n1000\tk\t{DOWN}\t1\n", "")
    steps = sc.parse_steps(d / "steps" / "t.steps")
    runs, start, end = sc.read_log(d / "clips" / "t.keys.txt")
    sc.align(steps, runs, start, d / "clips" / "t.keys.txt")
    sc.assign(steps, end, [])
    problems = sc.check(steps, exact=True, untrusted="NVDA logged nothing")
    assert problems == 2 and all(e.result == "FLAG" for s in steps for e in s.expects)


def test_baseline_escapes_lines_that_would_mean_something_else():
    assert sc.baseline_line("0") == "e/ 0"
    assert sc.baseline_line("!important") == "e/ !important"
    assert sc.baseline_line("/path") == "e/ /path"
    assert sc.baseline_line("1 of 3") == "e 1 of 3"


def test_log_offset_skips_earlier_sessions_and_drift_is_measured():
    d = Path(tempfile.mkdtemp())
    log = d / "nvda.log"
    old = "IO - speech.speech.speak (10:00:05.000) - MainThread (1):\nSpeaking ['yesterday, same time']\n"
    new = "IO - speech.speech.speak (10:00:05.000) - MainThread (1):\nSpeaking ['today']\n"
    log.write_text(old + new, encoding="utf-8")
    keys = d / "k.txt"
    keys.write_text(f"0\tclock\t10:00:04.000\t2026-10-21\n0\tlogpos\t{len(old.encode())}\n"
                    "8000\tend\trecording\n8000\tclockend\t10:00:12.400\n", encoding="utf-8")
    assert sl.clip_speech(log, keys) == [(1.0, "today")]
    assert abs(sl.read_clock(keys)["drift"] - 0.4) < 1e-6


def test_utterance_just_before_midnight_when_clock_is_just_after():
    d = Path(tempfile.mkdtemp())
    log = d / "nvda.log"
    log.write_text("IO - speech.speech.speak (23:59:59.900) - MainThread (1):\nSpeaking ['late']\n", encoding="utf-8")
    keys = d / "k.txt"
    keys.write_text("500\tclock\t00:00:00.100\t2026-10-22\n5000\tend\trecording\n", encoding="utf-8")
    got = sl.clip_speech(log, keys)
    assert len(got) == 1 and abs(got[0][0] - 0.3) < 1e-6, got


def test_bad_regex_is_reported():
    d = _project("k {DOWN}\ne/ (unclosed\n", "", "")
    try:
        sc.parse_steps(d / "steps" / "t.steps")
    except SystemExit as e:
        assert "bad regular expression" in str(e)
    else:
        raise AssertionError("no error")


if __name__ == "__main__":
    tests = [(n, f) for n, f in sorted(globals().items()) if n.startswith("test_")]
    failed = 0
    for n, f in tests:
        try:
            f(); print(f"ok    {n}")
        except Exception as e:  # noqa: BLE001
            failed += 1; print(f"FAIL  {n}: {e!r}")
    print(f"{len(tests) - failed}/{len(tests)} passed")
    sys.exit(1 if failed else 0)
