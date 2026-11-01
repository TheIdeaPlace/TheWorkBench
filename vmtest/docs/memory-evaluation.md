# VM memory evaluation

**Status: in progress.** The measuring kit has run on both hosts, for the idle workload only. No real
workload has been measured, so there's no sizing recommendation yet. The figures below are from before
the kit's review, and those runs don't count towards a result; the baselines get run again. How to run
it is in [`../eval/README.md`](../eval/README.md).

## What the VMs have today

Neither host has a flat 8 GB. Every pool VM uses dynamic memory, and the hosts differ:

| Host | Hardware | RAM | Pool VM min / startup / max |
|---|---|---|---|
| SURFACEPRO7 | Arm64 Snapdragon X, 12 logical CPUs | 31.6 GB | 2 / 4 / 8 GB |
| THESTADIUM | x64 i7-10850H, 12 logical CPUs | 31.6 GB | 2 / 6 / 12 GB |

No record says why THESTADIUM's pool is 6 / 12. The Hyper-V Manage defaults give that maximum if a VM
is built with 6 GB. Three VMs at 12 GB could ask for 36 GB on that 31.6 GB PC.

## Idle baseline (W0)

THESTADIUM, a VM resumed from Clean, 2-minute settle, then 10 minutes idle. Three runs at each size,
the first on the 4 GB VM counted as warm-up.

| Maximum | Peak demand | Peak assigned | Peak committed | Lowest available |
|---|---|---|---|---|
| 12 GB | 2.8 to 3.1 GB | 4.0 to 4.1 GB | 2.8 to 3.2 GB | 120 to 321 MB |
| 4 GB | 3.4 to 3.5 GB | 4.0 GB | 3.2 GB | 252 to 355 MB |

What this shows:

- **Offering more doesn't make an idle VM take more.** A 12 GB VM stayed at about 4 GB.
- **Idle isn't small.** Over the last 5 minutes of the runs read closely, demand averaged about
  2.7 GB. A 4 GB maximum leaves little room before any work starts. Whether that matters is what the
  workload runs have to show.
- **Defender was the largest ordinary process at idle** in the pilot runs, at about 145 to 195 MB.

The Surface's short pilot (1-minute settle, 3 minutes idle, one run at each size) looked similar: 2.6
GB peak demand and 4.0 GB peak assigned at both sizes. Its 4 GB VM touched 28 MB available, under 30 s.
That pilot settled for less time than a real run will, and the Surface still needs its full baseline.

## Changes to the criteria

Made from the idle baseline, as the plan allows. The 4 GB VM's idle numbers were in view when these were
chosen, so they aren't independent of it for idle. They were fixed before any real workload ran. The
review also dropped the "ignore the first 2 minutes" part of criterion 8: the counters start after a
run's settling time, so it was hiding the workload's own first 2 minutes.

| Criterion | Was | Now | Why |
|---|---|---|---|
| 7, memory available | Above 10% of the maximum | Above 100 MB | Dynamic memory keeps taking free memory back, so low Available is normal. The old rule scaled with the maximum: an idle 12 GB VM spent up to 620 s under its limit and an idle 4 GB VM 5 s, which is backwards. In the full baseline no idle VM went under 120 MB; the shorter pilots dipped lower only briefly. |
| 8, sustained paging | No more than Control's longest stretch | No more than twice Control's longest stretch | Idle VMs pass 100 pages/s for 230 to 415 s at both sizes, and Control's own runs varied by about 1.6 times. |

## Things the pilot proved

- A VM's memory settings survive `vmtest begin` restoring its Clean.
- `VMTEST_POOL` set in the eval's own process works, locally and over remoting, and the pool's own VMs
  and task checkpoints stay as they were.
- Windows' performance log keeps recording between `vmtest run` calls.

## Not done yet

- **Measuring what the kit itself costs** needs a workload with a repeatable duration; idle has none.
- **Showing the criteria catch a real shortage:** the deliberately too-small 2 GB VM hasn't run yet.
- **Workloads W1 to W9, and running several VMs at once.**
- **Watching real work for a week:** the scheduled task it needs hasn't been allowed yet.
