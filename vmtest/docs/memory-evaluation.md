# VM memory evaluation

**Status: THESTADIUM's results are in, with gaps listed below. The Surface is partly done. Nothing has been changed on the pool's VMs.** How to run the kit is in [`../eval/README.md`](../eval/README.md).

## The answer so far

**8 GB isn't needed for anything measured. 4 GB passed everything measured on both hosts. I recommend a 6 GB maximum with one size for all, rather than 4 GB or tiers.**

| Setting | Today, THESTADIUM | Today, Surface | Recommended |
|---|---|---|---|
| Minimum | 2 GB | 2 GB | 2 GB |
| Startup | 6 GB | 4 GB | 4 GB |
| Maximum | 12 GB | 8 GB | 6 GB |

Why 6 GB when 4 GB passed:

- **4 GB passes with little margin on heavy work.** The QuickMail suite reached 76% commit on THESTADIUM and 82% on the Surface, against an 85% limit. Three suites at once reached 91% on one VM.
- **6 GB costs almost nothing.** Dynamic memory only gives a VM what it asks for. Offered 12 GB, the heaviest run took 5.5 GB, and light work stayed at about 4 GB.
- **JAWS wasn't measured.** A little room is a sensible hedge until it is.
- **It still bounds the worst case.** Three VMs could ask for 18 GB, against 36 GB on THESTADIUM today, on a 31.6 GB PC.

No tiers: the plan only allows them if they let a host run another VM or save 4 GB of host memory. Nothing measured needs more than about 5.5 GB, and light work doesn't use the extra, so a heavy tier would gain nothing.

The Surface's settings shouldn't change until its remaining runs are done.

## What was measured

Each workload ran from Clean, with nothing else running on the VM's host beyond what's noted. A size was compared with a Control VM offered 12 GB (2 GB minimum, 4 GB startup). Three runs each, except the soak. About 150 runs finished in all.

### THESTADIUM (x64)

| Workload | Time at 12 GB | Time at 4 GB | Peak demand, 12 GB / 4 GB | Peak commit at 4 GB |
|---|---|---|---|---|
| W0, idle 10 min | 600 s | 600 s | 3,055 / 3,768 MB | 73% |
| W1, QuickMail install, first run, uninstall | 65 s | 58 s | 2,252 / 2,293 MB | 61% |
| W2, QuickCalc tests | 163 s | 160 s | 2,623 / 2,810 MB | 62% |
| W3, The Chat Place pytest | 330 s | 328 s | 2,772 / 2,648 MB | 64% |
| W4, NVDA tour of QuickMail | 394 s | 399 s | 2,670 / 2,810 MB | 63% |
| W5, Narrator recording | 401 s | 404 s | 2,703 / 2,830 MB | 63% |
| W7, QuickMail suite | 316 s | 325 s | 4,094 / 4,177 MB | 76% |
| W8, NVDA tour with a build | 620 s | 592 s | 4,013 / 4,096 MB | 70% |
| W9, soak, 55 min | 3,445 s | 3,450 s | 4,035 / 3,445 MB | 69% |

At 12 GB the most any VM was assigned was 5,116 MB (W8). In the soak, memory didn't grow at either size.

### Surface (Arm64)

| Workload | Time at 12 GB | Time at 4 GB | Peak demand, 12 GB / 4 GB | Peak commit at 4 GB |
|---|---|---|---|---|
| W1 | 57 s | 56 s | 2,580 / 2,785 MB | 52% |
| W2 | 158 s | 158 s | 2,982 / 2,840 MB | 66% |
| W3 | 347 s | 346 s | 3,196 / 3,082 MB | 61% |
| W4 | 406 s | 396 s | 3,115 / 3,034 MB | 67% |
| W5 | 397 s | 396 s | 3,310 / 3,051 MB | 62% |
| W7 | 343 s | 354 s | 4,631 / 4,464 MB | 82% |
| W8 | 611 s | 587 s | 4,472 / 4,300 MB | 78% |

At 12 GB the most any VM was assigned was 5,594 MB (W7). Runs were thrown out whenever another VM started on the PC. W0 and W9 aren't done: the finished 4 GB soak attempts were thrown out because another session started a VM. One of them showed no memory growth, but it doesn't count.

### The criteria do catch a shortage

The 2 GB VM failed W7 on both hosts: 1,351 s against 316 s with 4 failed tests on THESTADIUM, and a timeout after 2,400 s against 343 s on the Surface. Commit reached 94% and the paging file grew. This was only tried on W7.

### Several VMs at once, THESTADIUM at 4 GB

| Workloads | Lowest free memory on the PC | Peak CPU | Slower than alone | Failures |
|---|---|---|---|---|
| Suite + NVDA | 19,786 MB | 50% | Suite 12%, NVDA 2% | None |
| Suite + NVDA + QuickCalc | 15,471 MB | 89% | Suite 22%, QuickCalc 23%, NVDA none | None |
| Three suites | 15,465 MB | 100% | 39% to 77% | None, but one VM reached 91% commit |

**THESTADIUM can run three VMs on memory.** What limits it is CPU: with three heavy jobs the CPU is full, and they slow down. Each go was run once, and the peak CPU figure may understate guest load, because of how Hyper-V reports it to the host.

The Surface wasn't tested this way. It's the tighter host, because the PC itself uses more: free memory ranged from 5.5 to 15 GB with no eval VM running, and fell to about 1 GB during one run with other sessions busy. So its pool size may be better set at 2 than the 3 `Get-DefaultPoolSize` gives it, but that's a guess until measured.

## Not done

- **W6, JAWS:** not run on either host. Nothing here says JAWS is fine at any size.
- **Surface:** W0, W9 and several VMs at once. A quiet window of a few hours would do it, and [#233](https://github.com/TheIdeaPlace/TheWorkBench/issues/233) would make that easier.
- **A week of watching real work:** the permission check refused the scheduled task, so real-world peaks aren't known.
- **Startup 2 GB against 4 GB:** not compared.
- **The other half of W1 and W3:** Hyper-V Manage's Velopack first start has no release to install, and IDT's wx GUI tests and the visual probe weren't included.
- **W8 used a build instead of GreenMail's JVM.** The build overlaps the first few minutes of the tour, not all of it, and those runs didn't record whether it succeeded.
- **W5 and the sound check:** the check that Narrator's recording has sound was added after W5 ran, so those runs show the capture kept up, not that Narrator spoke.
- **Final speech script:** the sound and clock checks and the midnight handling have unit tests but haven't run in a VM, because other sessions had VMs running on THESTADIUM.
- **Latency is NVDA's log, not sound.** It's the time NVDA sent the speech, so a late synthesizer or audio path wouldn't show. That's for your ears; see below.
- **Audio gaps in W8:** recorded only as yes or no, and Control has one too, so W8 can't show one size losing more audio than another.
- **What the kit costs:** one suite run with the counters off took 315 s against about 316 s with them on. One run is a hint, not a measurement.

## Worth following up, apart from memory

- **QuickMail's suite stopped early on the Surface,** at 162, 1,828 and 4,529 of 5,932 tests, at both 4 GB and 12 GB. The 1,828 and 4,529 runs recorded no failed tests; the 162 run recorded one. It never did this on THESTADIUM in 8 runs. The cause wasn't recorded, and a test host crash can look exactly like this, so nothing here rules out a crash. It also means a passing VM run may not have run every test. The kit now records it as a failure if `dotnet test` fails with no failed test.
- **Failures the same at every size:** QuickCalc fails 23 of 270 tests on both hosts, and The Chat Place 7 of 4,991 on the Surface.

## How the criteria changed

The plan allowed tuning before candidates ran. These went further: most were made with candidate results in view, and several make passing easier, so they deserve a sceptical read. Independent reviews also tightened them. All are in `VmMemEval.psm1`, with tests.

| Criterion | Plan | Now | Why |
|---|---|---|---|
| 7, memory available | Above 10% of maximum | Above 100 MB | Dynamic memory takes free memory back, so an idle 12 GB VM looked worse than an idle 4 GB one |
| 8, paging | No more than Control's longest | No more than twice that, from the first sample | Control's own runs varied by 1.6 times. The 2-minute skip hid the workload's start |
| 2, silent steps | Not in the plan as such | Compared by place in the tour, and by the worst run's count, allowing 10% or 2 over Control's most | Which repeat of a step goes silent varies at Control, whose two soaks had 210 and 234 |
| 2, audio gaps and misplaced speech | None allowed | Per run against Control, allowing 0.5 misplaced | Control has them too when a build runs alongside |
| 5, task time | Median of 3 | The same, and no single run over 1.5 times the allowance | A review showed a median hides one run going badly wrong |
| 6, latency | Median and worst 5% | The same, and no single run over twice the allowance | The same |
| 1, test counts for W7 only | Same count | Only full runs compared, and early stops counted separately, allowing one more than Control | The suite stopped early at 12 GB too |
| 13, Hyper-V's Warning status | Under 60 s | Not used | A 4 GB VM at its limit is in Warning for most of a run even alone |

The soak compares one 4 GB run with the worse of two Control runs, which leans towards passing. A second Control run was added to learn Control's spread, because 4 GB's first comparison failed by a small margin.

## Run records changed by hand

These are in `%LOCALAPPDATA%\vmtest\eval` on each host, marked thrown out with the reason. In each case the run says nothing about memory.

- **W3, both hosts, all 12 first runs:** the VMs had no Visual C++ runtime, so wxPython couldn't load and the tests never ran. W3 was then re-run properly.
- **W4 at 4 GB on THESTADIUM, 3 runs:** NVDA heard nothing at all on that VM, at 61 to 63% commit. Retaking its Clean fixed it, and the same size then spoke normally. The cause was never found. Lost focus was suspected and ruled out. The rig now checks that a Clean produces speech.
- **W4 at 12 GB on the Surface, 2 runs:** one failed reading NVDA's log, a bug in the kit, and one was cut off when the session running it ended, which had its "troubled" mark cleared by hand as well.
- **W0 at 4 GB on THESTADIUM, 1 run:** a short pilot left among the real runs.

## For you to check

The kit can't judge these. Try them at the size you choose:

1. **Listen to a Narrator recording at the smaller size next to one at today's.** You should hear no stutters or cut-off speech.
2. **Use the Surface with JAWS while two VMs run.** Speech should keep up with your typing.
3. **Use a JAWS session in a VM at the smaller size.** It should feel the same as today.

## If you go ahead

- **THESTADIUM's pool:** set 2 / 4 / 6 GB, retake Clean, and remake the copies. Task checkpoints keep their old settings until they end.
- **`README.md:159`** says each VM uses about 4 GB. Measured, 2 to 3 GB for light work and up to 5.5 GB for heavy.
- **`Test-RoomToStart`** counts startup only. With a 6 GB maximum that's a smaller risk, but three VMs can still ask for 18 GB.
- **Hyper-V Manage's defaults** (`New-HyperVRdpVM.ps1:121,511` and `PowerShellHyperVService.cs:82-90`, in another repo) would give a new VM an 8 GB maximum.
- **Remove the eval VMs** when the Surface's runs are done. They're saved or off, using disk only.
