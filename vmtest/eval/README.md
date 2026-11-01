# VM memory evaluation kit

Measures how much memory the test VMs actually need, on each host, so the sizes come from evidence.
It adds its own VMs and never changes the pool's. Results go in
[`../docs/memory-evaluation.md`](../docs/memory-evaluation.md); raw data stays on each host in
`%LOCALAPPDATA%\vmtest\eval`.

## What's here

| File | What it does |
|---|---|
| `VmMemEval.psm1` | The sizes, the success criteria and the decision rules. Touches nothing, so it's fully tested (`..\tests\VmMemEval.Tests.ps1`). |
| `New-EvalVMs.ps1` | Makes the `MemEval-*` VMs, each with its own memory settings and its own Clean. |
| `Invoke-EvalRun.ps1` | One measured run: take a VM from Clean, run a workload with the counters on, record it, hand the VM back. |
| `Invoke-OnEvalHost.ps1` | Runs either of those here or on another PC. |
| `Measure-Host.ps1` | Logs every running VM's memory and the PC's load to a CSV. |
| `Set-HostWatch.ps1` | Keeps `Measure-Host.ps1` running for days, to watch real work. |
| `guest\*.ps1` | Start and stop the VM's own performance log. |
| `workloads\*.ps1` | One script per workload. |

## Using it

```powershell
# Make the VMs, on this PC or another (a few minutes each).
.\Invoke-OnEvalHost.ps1 -Script New-EvalVMs -Computer THESTADIUM -Parameters @{ Config = 'MemEval-12', 'MemEval-4' }

# One measured run.
.\Invoke-OnEvalHost.ps1 -Script Invoke-EvalRun -Computer THESTADIUM -Parameters @{ Workload = 'W0'; Config = 'MemEval-4'; Repeat = 1 }

# Watch real work on this PC, then stop.
.\Set-HostWatch.ps1
.\Set-HostWatch.ps1 -Stop

# The report, from the runs recorded on this PC.
Import-Module .\VmMemEval.psm1
Format-EvalReport -Runs @(Read-EvalRuns)
```

## How the VMs stay out of the way

- `VMTEST_POOL` lists the eval VMs only for the eval's own vmtest calls, so `vmtest begin` from
  anywhere else can't be handed one.
- Memory settings are stored in each checkpoint, so each eval VM's Clean is taken at its size.
  A run checks the maximum after `begin` and stops if it's wrong.
- To remove an eval VM, use Hyper-V Manage or `Remove-VM`, and delete its folder under the host's
  `vmtest` disk folder.

## What counts

A run is thrown out, and says why in its record, if it couldn't start, another VM was running, the
PC's own numbers weren't recorded or it dropped below its reserve, the workload was fine but its
counters are missing or stopped early, or it was the first clean run on that VM. A workload that
**failed** counts, even with no counters, because that may be the memory shortage this is looking for.

Missing data never passes. A size needs 3 runs and a healthy Control with the same settings (W6 and W9
need 1), every criterion it should have measured measured, and no larger size that failed. Runs from an
older format (`Get-EvalSchema`) stay on disk and are ignored.

The report needs both hosts' runs, so fetch them first:

```powershell
.\Invoke-OnEvalHost.ps1 -Computer THESTADIUM -CollectRuns
```
