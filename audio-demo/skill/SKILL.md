---
name: audio-demo
description: Produce an audio demo (a podcast-style MP3) of a Windows app — two AI hosts talking it through, with real screen reader recordings of the app in use, made in a vmtest VM. Use when the user asks for an audio demo, an audio tour, a podcast episode, or a listenable walkthrough of an app; to add or change sections of an existing demo; to re-record a demo clip; or to hear the available AI voices.
---

# Audio demos with AI hosts and real screen reader recordings

The result is one MP3: two AI hosts (Kokoro, a free on-device neural voice engine) explaining an
app, with clips in between of a real screen reader reading the real app as keys are pressed: Windows
Narrator or NVDA. The recording happens in a vmtest VM, never on the user's PC. The pipeline
follows the one Quill used for its podcast (`docs/podcast/` in Community-Access/quill). The sister
skill **sr-test** uses the same rig and tools to check speech against expectations.

**Never claim to know how something sounds.** You can't hear the result. Everything you say
about speech comes from NVDA's own speech log (exact text), from Whisper transcripts of the
recordings (Narrator), and from audio level measurements. Ask the person to listen, and say
exactly what to listen for.

**Choosing the screen reader.** Use the one the person asks for. With NVDA, each clip also gets
`clips\<name>.speech.txt`: NVDA's log of the exact text it *sent to its voice*. The transcript
uses it instead of Whisper. That's not proof the listener heard all of it, because the next key
cuts speech off. Before a host line quotes a demo, check that the demo wasn't interrupted at that
point: look at the gap to the next key, or run `clip.ps1 -Whisper` and read what was really
audible. Quote only what was heard. NVDA and Narrator word things
differently ("Email accounts list" against "email accounts, work, disconnected"), so a script
written for one screen reader's clips must be checked line by line against the other's.

## Where things are

- Tools: `$env:AUDIODEMO_HOME` (this project's folder: `tools\`, `vm\`, `templates\`). If it isn't
  set, run `install.ps1` from the project folder first.
- Python and models: `$env:AUDIODEMO_DATA` if it's set, otherwise `%LOCALAPPDATA%\audio-demo`
  (`venv\`, `models\`). In PowerShell:
  `$data = if ($env:AUDIODEMO_DATA) { $env:AUDIODEMO_DATA } else { "$env:LOCALAPPDATA\audio-demo" }`
  and `$py = "$data\venv\Scripts\python.exe"`. Run tools as `& $py -I <tool>.py ...`.
- vmtest: the vmtest skill. Read it before the first VM command. The tools find vmtest through
  `VMTEST_HOME`, or next to this project in TheWorkBench. Like vmtest, every tool works on the VM
  held by the repo and branch it runs from, so `cd` into the app's repo in the same command as
  each tool; from any other folder it finds no VM, or takes another one.
- A **project folder** per demo: `script.txt`, `steps\`, `clips\`, `cache\`, `out\`. Keep it
  outside any git repository. A delivery folder (for example a synced test folder) gets the MP3s
  and a copy of the project.
- `references\quickmail.md`: everything specific to QuickMail, and a model of what to write down
  for any other app.

## The flow

### 1. Agree on the content

Start from the user's talking points (they often have a file). Plan one demo clip per point that
can be *heard*. Things that are only visual or need hardware, accounts or a network you can't
reach become host talk, not demos. Default length is 15–25 minutes. Hosts speak at about 160 words
a minute, and demos are usually 25–60 seconds each.

### 2. Set up

1. `vmtest begin` from the app's repo, on the task's branch.
2. `powershell -File "$env:AUDIODEMO_HOME\tools\setup-rig.ps1" [-ScreenReader NVDA]`, from the same
   repo.
   - **Common to both:** it installs VB-Audio Virtual Cable (the VM has no sound device, so without
     it a screen reader says nothing) and copies ffmpeg in.
   - **Narrator (the default):** it sets Narrator's voice and turns off character echo.
   - **NVDA:** it installs the pinned NVDA release silently, and seeds its settings so there's no
     welcome dialog, no usage-statistics question and no update check. Typing is echoed as words,
     and the voice is NVDA's default. NVDA runs with its speech log on.

   It finishes by recording the screen reader announcing Notepad opening. Check that what it said
   is shown, and that the pitch matches the voice: Zira about 180 Hz; David, Mark and NVDA's
   default voice about 95–130 Hz. Run it again with the other `-ScreenReader` to switch.
3. Build the app for the VM's architecture (the pool may be ARM64), `vmtest deploy` it, and seed
   **realistic** demo data: names people would have, ordinary subjects, today's dates. Test
   fixtures are written to break layouts and sound absurd read aloud. Keep a pristine copy of the
   data in the VM and restore it before every take.
4. Write a reset routine for the app: close it, restore the data, relaunch, and wait until its
   startup announcements are over. Startup speech landing in the middle of a clip is the most
   common spoiled take.

### 3. Explore before recording

For each demo, walk the keys with `vmtest keys` and read the focus reports. That tells you where
focus lands, what each control is called, and which dialogs appear. Plan the steps from that.

### 4. Record

Write `steps\<name>.steps` (format in `templates\example.steps`), then:

```powershell
powershell -File "$env:AUDIODEMO_HOME\tools\clip.ps1" -Project <folder> -Name d03-search -App <ProcessName>
```

It records, copies the clip back, and prints what the screen reader said: NVDA's own log, or a
Whisper transcript for Narrator. Read it every time. Re-record until it says what the script will
claim it says. Expectation lines (`e …`, see the sr-test skill) can sit in a demo's steps file. A
demo ignores them, and `srcheck.py` checks them, which is a quick way to confirm a re-take still
says what the script quotes.

- **Waits:** after each key, wait long enough for the screen reader to finish (a message-list row
  takes 6–10 s). A key pressed while it's talking interrupts it, which is realistic for a quick
  arrow run but not when the listener needs to hear the result.
- **Setup:** use `q` / `Q` for setup keys. The clip starts at the first logged step (`k`, `t` or
  `x`), so setup speech is trimmed away. A clip with no logged step at all is used from the start of
  the recording.
- **What can't be scripted:** SendKeys can't press the Windows key, or hold the screen reader key
  (Caps Lock or Insert) as a modifier. So screen reader commands like Caps Lock+Tab can't be part
  of a demo. Show the app's own keys instead.
- **Typing:** type with `t`, which waits 220 ms between characters. Faster typing drops
  characters in apps that filter as you type.
- **Start state:** start every clip from the reset state, unless it deliberately follows on from
  another clip.
- **Type-ahead:** a screen reader may not announce a type-ahead jump. Check, and use arrow keys if
  it doesn't.
- **Pop-ups:** a fresh VM can open Windows' "Get Started" window, which takes focus. Close it
  (`vmtest close -Window 'Get Started'`) before recording. `clip.ps1 -App <process>` brings the app
  to the front before the first key, and stops if it can't. It can't stop a window opening
  mid-take, so read every transcript.
- **Stray processes:** a stray ffmpeg in the VM holds vmtest's output file and makes every
  `vmtest run` fail. Kill it with `Invoke-InTestVM 'Get-Process ffmpeg | Stop-Process -Force'`
  (from `tools\VmFiles.psm1`).

### 5. Write the script

Use `templates\script.txt`. Declare hosts with `#voice NAME kokoro_voice`. Writing rules:

- **Write for the ear.** Use short turns that alternate. Spell keys as spoken. Avoid symbols and
  markdown. Spell addresses the way they should be said: "dot I O", not ".io", which Kokoro reads
  as "eo".
- **Describe only what the transcript of that clip shows,** in the clip's own words. Never describe
  speech you inferred.
- **Verify every claim** about a feature or a shortcut against the code or the user guide before it
  goes in.
- **Be honest on air.** Say that the hosts are AI voices, and that the demos are real recordings.
  Say the keys were pressed by a script. Say what was done afterwards: clicks added, volume
  levelled, long pauses shortened, setup cut. Say the machine is offline if it is. Say why
  something wasn't demonstrated.
- Natural moments help: one host interrupting to connect a feature to an earlier one, or noticing
  something the app said on its own.
- Don't name a specific screen reader product unless the content is about that product. A demo
  recorded with Narrator may say so, because that is what the listener hears.
- **Chapters:** put a `#chapter <title>` line at the start of each section, usually right after a
  `[PAUSE]` and once at the very top. The MP3 gets chapters a listener can jump between in podcast
  apps that support them. That matters in a 20-minute tour. Use titles a listener would look for:
  "Search", "Writing, and two ways to check spelling".
- **[PAUSE] is the section break.** It's 1.8 s, clearly longer than the gaps inside a conversation,
  so the listener hears the topic change. `#pause <seconds>` changes it for a script. Don't use it
  as a short beat inside a section.

### 6. Produce and check

```powershell
& $py -I "$env:AUDIODEMO_HOME\tools\produce.py" <folder> --out <Name>-v1.mp3 --transcript
```

It renders each host line once (cached by text), trims and levels the clips, adds a soft click
for each logged key (kept even through pauses that are shortened) and a chime around each demo, and
normalizes to -16 LUFS. It stops with a line number on anything in the script it doesn't
understand, and warns about a silent clip. With `#chapter` lines, it writes the chapters into the
MP3 and prints the chapter list. `--transcript` writes `out\<name>-transcript.txt`, with chapter
headings, each host line from the script, and each demo. Demos come from NVDA's speech log where
the clip has one (marked "logged", and exact), otherwise as Whisper hears them after trimming
(marked "heard").

**Clip starts are checked automatically.** A clip starts just before its first logged key. If
speech is still sounding there, it's leftover from an earlier announcement, and it gets silenced up
to the next gap, so the demo doesn't open on a stray fragment ("...quarter."). The build log names
every clip it changed. A `WARNING` means speech ran straight through the first key with no gap.
Listen to that clip's start, or re-record it with a longer wait before the first key. The check
relies on the virtual cable recording digital silence between utterances. Anything above about
-60 dBFS counts as speech. A clip recorded another way, such as RDP audio or a real sound card
with background noise, would read as speech throughout, so check those starts by ear.

Clips recorded before key logs had a `start` entry only have a guessed capture lag. For those, a
sound that began at most 0.3 s before the cut is taken to be the answer to the first key, cut into
by a lag that was guessed too small. The clip then starts just before that sound, and its clicks
move to match. In a transcript, a demo's time is where its audio starts, about 1.2 s after the
demo time in the build log, which is before the chime. Then check the result:

- Transcribe the whole MP3 (`transcribe.py`), or use `--transcript`, and read it against the
  script.
- Whisper often skips a stretch that starts right after a chime. If a demo comes back empty,
  transcribe it again starting 1–2 seconds later (`--from`, `--for`), and measure its level before
  you conclude the audio is missing.
- **Check clip starts by level, not by transcript.** In the first QuickMail demo, Narrator's
  "Unread" was clipped to "...read", and Whisper still wrote "Unread", because it expected the word.
  The build's start check and an audio level measurement are what catch this.

### 7. Deliver

- **Version every delivery:** `-v1`, `-v2`, and so on. Never overwrite an earlier version. Keep a
  copy of each version's script and clips.
- When a version adds sections, also make an MP3 of just the new sections (a script excerpt run
  through `produce.py --script`). Give demo start times for both files.
- **Ready to share means three files:**
  1. The MP3, with chapters and a title.
  2. A transcript. Start from `--transcript`, read the demo lines, and correct what Whisper misheard
     against the app's real data (addresses, typed misspellings). Deaf and hard-of-hearing
     listeners rely on it.
  3. Short show notes: what it is, where to get the app, and the chapter list with times.
- Give the person a short listening list: how the host voices sound, the balance between hosts and
  the screen reader, the clicks and chimes, and anything you weren't sure about.
- Anything that looks like a bug in the app goes in a list for the user. Don't fix it during the
  demo work unless they ask.

## Voices

Kokoro v1.0 has 54 voices: 20 American English, 8 British English, and the rest in Spanish,
French, Hindi, Italian, Japanese, Portuguese and Mandarin. `samples.py <folder>` renders a sample
of each English voice. The model is Apache-2.0 licensed: free for commercial use, no attribution
needed for its output. Quill uses `am_liam` and `af_jessica`; the QuickMail demo used `am_michael`
and `af_heart`.
