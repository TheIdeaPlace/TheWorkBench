# audio-demo

Make an audio demo of a Windows app: two AI hosts talking it through, with real screen reader
recordings of the app in use in between. One MP3, the kind of thing you'd post as a podcast episode.

The first one was a 25-minute tour of QuickMail (October 2026). The hosts were written by Claude and
voiced by [Kokoro](https://huggingface.co/hexgrad/Kokoro-82M), a free neural voice engine that runs
on your own PC. Windows Narrator, with the Microsoft Zira voice, read QuickMail running in a test
VM. The approach follows Quill's podcast pipeline (`docs/podcast/` in
[Community-Access/quill](https://github.com/Community-Access/quill)).

It's built to be driven by Claude Code through the `audio-demo` skill, together with
[vmtest](../vmtest/) for the VM.

## Install

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
```

You need x64 Python 3.12 and ffmpeg on PATH (`winget install Gyan.FFmpeg`). The installer
creates a Python environment and downloads the Kokoro model (about 340 MB) into
`%LOCALAPPDATA%\audio-demo`, along with Whisper's speech model (about 500 MB). It installs the
skill, and sets `AUDIODEMO_HOME` to the folder you run it from. So run it from the checkout you
keep, not from a temporary worktree. Nothing it does opens a window. `-WhatIf` shows what it would
do.

## What's here

| | |
|---|---|
| `skill\SKILL.md` | The Claude Code skill: the whole flow, and what goes wrong |
| `skill\references\quickmail.md` | App-specific notes from the QuickMail demo, a model for other apps |
| `tools\setup-rig.ps1` | Turns a vmtest VM into a recording rig: VB-Audio Virtual Cable, ffmpeg, Narrator's voice |
| `tools\clip.ps1` | Records one demo clip in the VM from a steps file, brings it back, transcribes it |
| `tools\produce.py` | Builds the MP3 from the script, the host voices and the clips, with chapters, and optionally a transcript |
| `tools\test_produce.py` | Self-tests for the clip-start check, script parsing and chapter metadata (no models needed) |
| `tools\transcribe.py` | Whisper transcripts, to check what the screen reader said |
| `tools\samples.py` | A sample of every English voice, to choose hosts by ear |
| `tools\VmFiles.psm1` | Copies files out of the VM, which vmtest's `push` can't do in reverse |
| `vm\demo.ps1` | Runs in the VM: records the audio cable while sending the keys |
| `vm\install-vbcable.ps1` | Runs in the VM: installs the virtual audio cable silently |
| `templates\` | A starting script and an example steps file |

## Why a virtual audio cable

A Hyper-V VM has no sound card, so a screen reader in it has nowhere to speak. VB-Audio Virtual
Cable (donationware, from [vb-audio.com](https://vb-audio.com/Cable/); downloaded at setup, not
kept in this repository) gives it a playback device, and ffmpeg records the other end. The
recordings are exactly what the screen reader produced, with nothing played aloud on anyone's
desk.
