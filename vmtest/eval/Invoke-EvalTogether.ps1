<#
.SYNOPSIS
Run several eval VMs at once on this PC, to see how many it can take at the finalist size.

.DESCRIPTION
Each VM runs one workload from Clean, all starting together, each recorded as an ordinary run on its own
MemEval-4a, 4b or 4c. They're never judged against Control. Afterwards this says whether the PC kept its
reserve, whether every workload finished, and how much slower each ran than alone.

The realistic mix is the QuickMail suite, an NVDA tour and QuickCalc's tests. -Workload W7,W7,W7 is the worst
case. Other VMs on this PC have to be off, unless named in -AllowRunning, or it says so and stops. Going
with real work under way, as on the Surface with a typical load, is a matter of when it's run.
#>
[CmdletBinding()]
param(
    [string[]]$Workload = @('W7', 'W4', 'W2'),
    [string[]]$AllowRunning = @(),
    [string]$ToolPath,
    [int]$Go = 1
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VmMemEval.psm1') -Force
if ($Workload.Count -gt 3) { throw 'There are 3 VMs for this, so 3 workloads at most.' }
$names = @(Get-EvalConfigs | Where-Object Role -eq 'Together' | Select-Object -First $Workload.Count | ForEach-Object Name)
foreach ($n in $names) {
    if (-not (Get-VM -Name $n -ErrorAction SilentlyContinue) -or "$((Get-VM -Name $n).Notes)" -notmatch ' Rigged\.') {
        throw "$n isn't ready. Run New-EvalVMs.ps1 -Config $n, then Install-EvalRig.ps1 -Config $n."
    }
}
$busy = @(Get-VM | Where-Object { $_.State -eq 'Running' -and $AllowRunning -notcontains $_.Name } | ForEach-Object Name)
if ($busy.Count) { throw "$($busy -join ', ') running on this PC, so a go now wouldn't measure what it should." }

$runScript = Join-Path $PSScriptRoot 'Invoke-EvalRun.ps1'
$tool = if ($ToolPath) { "-ToolPath '$ToolPath'" } else { '' }
$root = Join-Path (Get-EvalDataRoot) "runs\$env:COMPUTERNAME"
$began = Get-Date
# vmtest allows one VM per Claude session, and these are all one, so they run as if outside any session.
Remove-Item Env:\CLAUDE_PID, Env:\CLAUDE_CODE_SESSION_ID -ErrorAction SilentlyContinue
$children = @(for ($i = 0; $i -lt $names.Count; $i++) {
        # Each is told about the others, since a run otherwise won't start beside another VM. The repeat
        # keeps a go's runs apart, and the wait lets 'begin' take its turn at vmtest's lock.
        $others = @($names | Where-Object { $_ -ne $names[$i] }) + $AllowRunning
        $allow = ($others | ForEach-Object { "'$_'" }) -join ','
        $argument = "& '$runScript' -Workload $($Workload[$i]) -Config $($names[$i]) -Repeat $(100 + $Go) -KeepWarmup -AllowRunning $allow $tool"
        Write-Host "$($names[$i]): $($Workload[$i])"
        Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', "`"$argument`""
    })
foreach ($c in $children) { $c.WaitForExit() }

$runs = @(Get-ChildItem $root -Filter *.json | Where-Object { $_.LastWriteTime -ge $began } | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json } | Where-Object { $names -contains $_.config })
if ($runs.Count -lt $names.Count) { Write-Host "Only $($runs.Count) of $($names.Count) runs left a record." }

# How long each workload took by itself at this size, from the runs already recorded here.
$alone = @{}
foreach ($w in $Workload | Sort-Object -Unique) {
    $times = @(Select-CountedRun @(Read-EvalRuns | Where-Object { $_.host -eq $env:COMPUTERNAME }) $w 'MemEval-4' $env:COMPUTERNAME | ForEach-Object { [double]$_.durationSeconds })
    if ($times.Count) { $alone[$w] = Get-Median $times }
}
$result = Test-TogetherRuns -Runs $runs -AloneSeconds $alone
"$($names.Count) VMs together on $env:COMPUTERNAME ($($Workload -join ', ')): $(if ($result.Pass -and $runs.Count -eq $names.Count) { 'passed' } else { 'did not pass' })."
"The PC's lowest available memory: $($result.LowestHostMB) MB. Peak CPU: $($result.PeakCpu)%."
foreach ($r in $runs) { "$($r.config), $($r.workload): $($r.durationSeconds) s, exit $($r.exitCode), tests $($r.testsTotal), failed $(@($r.failedTests).Count), peak demand $($r.peakDemandMB) MB, commit $($r.guest.PeakCommitPercent)%, paging $([int]$r.guest.PagingSeconds) s" }
foreach ($s in $result.Slowdowns) { "$($s.Workload) took $($s.Ratio) times as long as alone." }
foreach ($p in $result.Problems) { "Problem: $p" }
foreach ($p in $result.Warnings) { "Warning: $p" }
if ($result.CpuBusy) { 'The PC''s CPU was close to full, so slowdown here may be CPU rather than memory.' }
