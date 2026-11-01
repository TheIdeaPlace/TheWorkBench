<#
.SYNOPSIS
One measured run: take an eval VM from Clean, run one workload with the counters on, record the result.

.DESCRIPTION
Runs on the PC that has the VMs. It takes its VM by name with VMTEST_POOL set only in this process,
so 'vmtest begin' from any other session never sees the eval VMs. Writes one JSON file per run, under
%LOCALAPPDATA%\vmtest\eval\runs, and always hands the VM back.

The first run after a VM has started counts as warm-up and is marked thrown out, as is a run where
another VM was running or the PC dropped below its reserve.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Workload,
    [Parameter(Mandatory)][ValidateSet('MemEval-12', 'MemEval-8', 'MemEval-6', 'MemEval-4', 'MemEval-2')][string]$Config,
    [Parameter(Mandatory)][int]$Repeat,
    [string]$ToolPath,
    [ValidateRange(0, 1800)][int]$SettleSeconds = 120,
    [double]$ReserveGB = 4,
    [hashtable]$WorkloadArguments = @{},
    # For a pilot: keep the run even if it would be thrown out as warm-up.
    [switch]$KeepWarmup,
    # For checking what measuring costs: the same run with the counters and sampler off. It's
    # recorded for that comparison and never counts as a result.
    [switch]$NoCounters
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VmMemEval.psm1') -Force
if (-not $ToolPath) {
    $ToolPath = if (Test-Path 'C:\vmtest-tool\vmtest.ps1') { 'C:\vmtest-tool\vmtest.ps1' } else { Join-Path (Split-Path $PSScriptRoot) 'vmtest.ps1' }
}

$size = Get-EvalConfigs | Where-Object Name -eq $Config
$workloadScript = Get-ChildItem (Join-Path $PSScriptRoot 'workloads') -Filter "$Workload-*.ps1" | Select-Object -First 1
if (-not $workloadScript) { throw "There's no workload script for '$Workload' in $PSScriptRoot\workloads." }

$env:VMTEST_POOL = (@('ClaudeTesting') + @(Get-EvalConfigs | ForEach-Object Name)) -join ','
Remove-Item Env:\VMTEST_HOST -ErrorAction SilentlyContinue   # this script is already on the right PC
$task = @{ Repo = 'MemEval'; Branch = "$Workload-$Config-$Repeat" }

# vmtest on the eval VM, with its output returned and a failure thrown, unless the exit code is wanted.
function vt {
    param([Parameter(Mandatory)][string]$Command, [Parameter(ValueFromRemainingArguments)]$Rest)
    $ErrorActionPreference = 'Continue'   # so a line on stderr is output, not a failure
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ToolPath $Command @Rest -Repo $task.Repo -Branch $task.Branch 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Output = @($out); ExitCode = $LASTEXITCODE }
}
function vtOk {
    param([Parameter(Mandatory)][string]$Command, [Parameter(ValueFromRemainingArguments)]$Rest)
    $r = vt $Command @Rest
    if ($r.ExitCode -ne 0) { throw "vmtest $Command failed ($($r.ExitCode)): $($r.Output -join ' ')" }
    $r.Output
}

$root = Get-EvalDataRoot
$runDir = Join-Path $root "runs\$env:COMPUTERNAME"
$sampleDir = Join-Path $root "run-samples\$Workload-$Config-$Repeat-$(Get-Date -Format yyyyMMddHHmmss)"
New-Item $runDir, $sampleDir -ItemType Directory -Force | Out-Null
$stopFile = Join-Path $sampleDir 'stop'
$sampler = $null
$began = $false
$workloadStarted = $false
$startError = ''
$record = [ordered]@{
    schema = (Get-EvalSchema); host = $env:COMPUTERNAME; workload = $Workload; config = $Config; maxGB = $size.MaxGB; repeat = $Repeat
    startedAt = (Get-Date).ToString('o'); settleSeconds = $SettleSeconds
    durationSeconds = $null; exitCode = $null; testsTotal = $null; failedTests = @(); speech = $null; latency = $null; limits = @()
    guest = $null; guestEvents = $null; topProcesses = @(); pagefileGrewMB = 0
    hostMinAvailableMB = $null; hostPeakCpuPercent = $null; otherVMsRunning = @(); vmMemoryStatusWarningSeconds = $null; peakDemandMB = $null; peakAssignedMB = $null
    discarded = $false; discardReason = ''; notes = @()
}

try {
    $beganAt = Get-Date
    vtOk begin -VM $Config | Out-Null
    $began = $true
    $record.beginSeconds = [math]::Round(((Get-Date) - $beganAt).TotalSeconds, 1)
    $live = Get-VM -Name $Config
    if ($live.MemoryMaximum -ne $size.MaxGB * 1GB) {
        throw "$Config has a $($live.MemoryMaximum / 1GB) GB maximum after 'begin', not $($size.MaxGB) GB. Its settings didn't survive the restore, so this run would measure the wrong size."
    }

    if (-not $NoCounters) {
        $sampler = Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            "`"$(Join-Path $PSScriptRoot 'Measure-Host.ps1')`"", '-Folder', "`"$sampleDir`"", '-StopFile', "`"$stopFile`""
    }

    if ($SettleSeconds) { Start-Sleep -Seconds $SettleSeconds }
    if (-not $NoCounters) { vtOk run -ScriptFile (Join-Path $PSScriptRoot 'guest\Start-Counters.ps1') -Elevated -Timeout 120 | Out-Null }

    # A workload that dies is a result, not a broken run: running out of memory is what this is looking for.
    $workloadStarted = $true
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try { $result = & $workloadScript.FullName -Vt ${function:vt} -VtOk ${function:vtOk} -Arguments $WorkloadArguments }
    catch { $result = [pscustomobject]@{ exitCode = -1 }; $record.notes += "The workload failed: $($_.Exception.Message)" }
    $watch.Stop()
    $record.durationSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
    if ($null -eq $result) { $result = [pscustomobject]@{ exitCode = 0 } }
    foreach ($k in 'exitCode', 'testsTotal', 'failedTests', 'speech', 'latency', 'limits') {
        if ($result.PSObject.Properties[$k]) { $record[$k] = $result.$k }
    }

    if (-not $NoCounters) {
        $stopOutput = vtOk run -ScriptFile (Join-Path $PSScriptRoot 'guest\Stop-Counters.ps1') -Elevated -Timeout 300
        $line = @($stopOutput | Where-Object { "$_".StartsWith('MEMEVAL ') })[0]
        if (-not $line) { throw "The VM's counters came back without a MEMEVAL line: $($stopOutput -join ' ')" }
        $report = $line.Substring(8) | ConvertFrom-Json
        $stream = New-Object System.IO.Compression.GZipStream((New-Object System.IO.MemoryStream(, [Convert]::FromBase64String($report.counters))), [System.IO.Compression.CompressionMode]::Decompress)
        $csv = (New-Object System.IO.StreamReader($stream)).ReadToEnd()
        [System.IO.File]::WriteAllText((Join-Path $sampleDir 'guest-counters.csv'), $csv)
        $record.guest = Get-CounterSummary -Rows @(ConvertFrom-CounterCsv -Csv $csv) -MaxGB $size.MaxGB
        $record.guestEvents = [ordered]@{ exhaustion = $report.exhaustion; crashes = @($report.crashes) }
        $record.topProcesses = @($report.topProcesses)
        $record.pagefileGrewMB = $report.pagefileGrewMB
    }
} catch {
    if ($workloadStarted) {
        if ($null -eq $record.exitCode) { $record.exitCode = -1 }
        $record.notes += "Collecting the results failed: $($_.Exception.Message)"
    } else {
        $record.exitCode = -1
        $startError = $_.Exception.Message
    }
} finally {
    if ($sampler) {
        New-Item $stopFile -ItemType File -Force | Out-Null
        if (-not $sampler.WaitForExit(30000)) { Stop-Process -Id $sampler.Id -Force -ErrorAction SilentlyContinue }
    }
    if ($began) {
        $ended = vt end
        if ($ended.ExitCode -ne 0) { $record.notes += "vmtest end failed: $($ended.Output -join ' ')" }
    }
}

# What the PC itself saw during the run, and whether it can be compared with the others.
$samples = @(Get-ChildItem $sampleDir -Filter '*.csv' | Where-Object { $_.Name -ne 'guest-counters.csv' } | ForEach-Object { Import-Csv $_.FullName })
if ($samples) {
    $record.hostMinAvailableMB = (@($samples | ForEach-Object { [double]$_.HostAvailableMB }) | Measure-Object -Minimum).Minimum
    $record.hostPeakCpuPercent = (@($samples | ForEach-Object { [double]$_.HostCpuPercent }) | Measure-Object -Maximum).Maximum
    $mine = @($samples | Where-Object { $_.VM -eq $Config })
    if ($mine) {
        $record.peakDemandMB = (@($mine | ForEach-Object { [double]$_.DemandMB }) | Measure-Object -Maximum).Maximum
        $record.peakAssignedMB = (@($mine | ForEach-Object { [double]$_.AssignedMB }) | Measure-Object -Maximum).Maximum
        $record.vmMemoryStatusWarningSeconds = @($mine | Where-Object { $_.Status -match 'Warning|Low' }).Count * 5
    }
    $record.otherVMsRunning = @($samples | Where-Object { $_.VM -and $_.VM -ne $Config } | ForEach-Object VM | Sort-Object -Unique)
}
# Only this format's runs: an older pilot's doesn't warm anything up for this one.
$earlier = @(Get-ChildItem $runDir -Filter "*-$Config-*.json" -ErrorAction SilentlyContinue | Where-Object { (Get-Content $_.FullName -Raw | ConvertFrom-Json).schema -eq (Get-EvalSchema) })
$record.discardReason = if ($NoCounters) { 'Overhead check, with the counters off.' } else {
    Get-RunDiscardReason -Started $workloadStarted -ExitCode ([int]$record.exitCode) -HasCounters ([bool]$record.guest) -OtherVMs @($record.otherVMsRunning) `
        -HostMinAvailableMB $record.hostMinAvailableMB -ReserveGB $ReserveGB -FirstOnVM ((-not $earlier) -and (-not $KeepWarmup)) -StartError $startError
}
$record.discarded = [bool]$record.discardReason

$file = Join-Path $runDir "$Workload-$Config-$Repeat-$(Get-Date -Format yyyyMMddHHmmss).json"
[pscustomobject]$record | ConvertTo-Json -Depth 6 | Set-Content $file -Encoding UTF8
[pscustomobject]$record | Select-Object host, workload, config, repeat, durationSeconds, exitCode, peakDemandMB, peakAssignedMB, hostMinAvailableMB, discarded, discardReason
"Recorded $file"
