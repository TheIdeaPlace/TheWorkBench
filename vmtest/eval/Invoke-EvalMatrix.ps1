<#
.SYNOPSIS
Run workloads across sizes on this PC until each has its results, the way the plan tests them.

.DESCRIPTION
For each workload it alternates Control with 4 GB, so neither gets the warmer caches, until each has
enough runs that count. If 4 GB doesn't pass it moves up to 6 GB, then 8 GB, and stops at the first
that passes. It carries on from what's already recorded, so after a dropped connection you just run
it again. -TooSmall also runs the 2 GB VM, which has to fail for the criteria to mean anything.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Workload,
    [string[]]$AllowRunning = @(),
    [switch]$TooSmall,
    [string]$ToolPath,
    [int]$MaxExtraRuns = 3,
    # Stops between runs after this long, so whatever started it can't kill it partway through a run and
    # strand a VM. 0 means no limit. Run it again to carry on.
    [int]$MaxMinutes = 0
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VmMemEval.psm1') -Force
$me = $env:COMPUTERNAME
$runScript = Join-Path $PSScriptRoot 'Invoke-EvalRun.ps1'
$began = Get-Date
function Test-OutOfTime { $MaxMinutes -and ((Get-Date) - $began).TotalMinutes -ge $MaxMinutes }

function Get-Runs { @(Read-EvalRuns | Where-Object { $_.host -eq $me }) }
function Get-Counted([string]$W, [string]$C) { @(Select-CountedRun (Get-Runs) $W $C $me).Count }
function Get-Tried([string]$W, [string]$C) { @(Get-Runs | Where-Object { $_.workload -eq $W -and $_.config -eq $C -and -not (Test-RunNeverHadVM $_) }).Count }

# Make a size's VM if this is the first time it's needed. Only some workloads ever reach 6 or 8 GB.
function Initialize-Size([string]$C) {
    $vm = Get-VM -Name $C -ErrorAction SilentlyContinue
    if ($vm -and "$($vm.Notes)" -match ' Rigged\.') { return }
    Write-Host "Making $C"
    $arguments = if ($ToolPath) { @{ ToolPath = $ToolPath } } else { @{} }
    if (-not $vm -or "$($vm.Notes)" -notmatch 'Sized') { & (Join-Path $PSScriptRoot 'New-EvalVMs.ps1') -Config $C @arguments | Out-Null }
    & (Join-Path $PSScriptRoot 'Install-EvalRig.ps1') -Config $C @arguments | ForEach-Object { Write-Host "  $_" }
}

# Hand back any eval VM a run left locked, say because whatever started it was killed. No other run can
# be going, since each host only runs one of these at a time, and waiting on one would never end.
function Clear-StrandedRuns {
    foreach ($file in Get-ChildItem (Join-Path $env:LOCALAPPDATA 'vmtest') -Filter 'lock-MemEval-*.json' -ErrorAction SilentlyContinue) {
        $lock = Get-Content $file.FullName -Raw | ConvertFrom-Json
        if ($lock.repo -ne 'MemEval') { continue }
        Write-Host "Handing back $($file.BaseName -replace '^lock-', ''), left by $($lock.branch)"
        $tool = if ($ToolPath) { $ToolPath } elseif (Test-Path 'C:\vmtest-tool\vmtest.ps1') { 'C:\vmtest-tool\vmtest.ps1' } else { Join-Path (Split-Path $PSScriptRoot) 'vmtest.ps1' }
        $r = Invoke-EvalVmTest -ToolPath $tool -Pool @(Get-EvalConfigs | ForEach-Object Name) -Arguments @('end', '-Repo', 'MemEval', '-Branch', $lock.branch)
        if ($r.ExitCode -ne 0) { throw "Couldn't hand back $($file.BaseName): $($r.Output -join ' ')" }
    }
}
Clear-StrandedRuns

# One run in its own process, so one failing can't take the rest with it.
function Invoke-One([string]$W, [string]$C) {
    # Other work on this PC comes first. A run can't start beside it, so wait quietly for it to finish.
    $waited = 0
    while ($true) {
        $busy = @(Get-VM | Where-Object { $_.State -eq 'Running' -and $AllowRunning -notcontains $_.Name } | ForEach-Object Name)
        if (-not $busy.Count) { break }
        if ($waited -eq 0) { Write-Host "$((Get-Date).ToString('HH:mm:ss')) Waiting for $($busy -join ', ') to finish." }
        if ($waited -ge 240) { throw "$($busy -join ', ') has been running for 4 hours, so this is stopping. Run it again when the PC is free." }
        Start-Sleep -Seconds 60
        $waited++
    }
    $repeat = (Get-Tried $W $C) + 1
    $allow = if ($AllowRunning.Count) { "-AllowRunning $(($AllowRunning | ForEach-Object { "'$_'" }) -join ',')" } else { '' }
    $tool = if ($ToolPath) { "-ToolPath '$ToolPath'" } else { '' }
    $started = Get-Date
    Write-Host "$($started.ToString('HH:mm:ss')) $W at $C, attempt $repeat"
    $ErrorActionPreference = 'Continue'
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& '$runScript' -Workload $W -Config $C -Repeat $repeat $allow $tool" 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $lines = @($out | Where-Object { $_ -match '^(durationSeconds|exitCode|peakDemandMB|peakAssignedMB|discardReason)\s*:\s*\S|^Note:' } | ForEach-Object { ($_ -replace '\s+:\s+', '=').Trim() })
    Write-Host "  $($lines -join '; ')  [$([int]((Get-Date) - $started).TotalMinutes) min]"
    if ($code -ne 0) { Write-Host "  The run ended with exit code $code. Last lines: $(@($out | Select-Object -Last 3) -join ' ')" }
    $code
}

# Alternate between sizes until each has its runs, giving up on a size after a few that didn't count.
function Complete-Sizes([string]$W, [string[]]$Sizes, [int]$Need) {
    foreach ($size in $Sizes) { Initialize-Size $size }
    # The suite's early stops use up attempts without being anyone's fault, so it gets more of them.
    $limit = $Need + 1 + $MaxExtraRuns + $(if (Test-EvalStopsEarly $W) { 4 } else { 0 })
    while ($true) {
        $next = @($Sizes | Where-Object { (Get-Counted $W $_) -lt $Need -and (Get-Tried $W $_) -lt $limit } |
            Sort-Object { Get-Counted $W $_ } | Select-Object -First 1)
        if (-not $next.Count) { break }
        if (Test-OutOfTime) { Write-Host "Stopping after $MaxMinutes minutes. Run this again to carry on."; exit 0 }
        [void](Invoke-One $W $next[0])
    }
}

foreach ($w in $Workload) {
    $need = Get-EvalMinRuns $w
    Write-Host "--- $w, $need runs each"
    Complete-Sizes $w @('MemEval-12', 'MemEval-4') $need
    if ((Get-Counted $w 'MemEval-12') -lt $need) { Write-Host "Control hasn't got its runs for $w, so going no further with it."; continue }
    foreach ($up in 'MemEval-6', 'MemEval-8') {
        $result = Get-WorkloadResult -Runs @(Get-Runs) -Workload $w -HostName $me
        if ($null -ne $result.SmallestGB) { break }
        Complete-Sizes $w @($up) $need
    }
    if ($TooSmall) { Complete-Sizes $w @('MemEval-2') 1 }
    $result = Get-WorkloadResult -Runs @(Get-Runs) -Workload $w -HostName $me
    $smallest = if ($null -ne $result.SmallestGB) { "$($result.SmallestGB) GB" } else { 'none' }
    Write-Host "=== $w on ${me}: smallest passing $smallest. $(@($result.Sizes | ForEach-Object { "$($_.MaxGB) GB $(if ($_.Pass -eq $true) { 'passed' } elseif ($_.Pass -eq $false) { 'failed' } else { "not judged ($($_.Reason))" })" }) -join '; ')"
    foreach ($s in $result.Sizes) {
        foreach ($c in @($s.Checks | Where-Object { $_.Result -eq 'Fail' -or $_.Result -eq 'Incomplete' })) { Write-Host "    $($s.MaxGB) GB, criterion $($c.Number), $($c.Name), $($c.Result). $($c.Detail)" }
    }
}
