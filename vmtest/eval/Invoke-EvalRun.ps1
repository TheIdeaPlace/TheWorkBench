<#
.SYNOPSIS
One measured run: take an eval VM from Clean, run one workload with the counters on, record the result.

.DESCRIPTION
Runs on the PC that has the VMs. It takes its VM by name, with VMTEST_POOL set only for each vmtest
call, so 'vmtest begin' from anywhere else never sees the eval VMs. Writes one JSON file per run, under
%LOCALAPPDATA%\vmtest\eval\runs, and always hands the VM back. It won't start while another VM is
running, since the times couldn't be compared and a 12 GB VM could crowd out real work.

The first clean run ever recorded on a VM counts as warm-up and is marked thrown out. So is a run
where another VM started, the PC dropped below its reserve, or a fine workload's counters are
incomplete. A workload that failed always counts. Exits non-zero if the VM couldn't be handed back.

A workload script takes -Vt, -VtOk and -Arguments, and returns exactly one object with an integer
exitCode, plus any of testsTotal, failedTests, speech, latency and limits. Anything else counts as
the workload failing, so a script that forgets to capture a command's output can't turn a failure into
a pass.
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

$running = @(Get-VM | Where-Object { $_.State -eq 'Running' } | ForEach-Object Name)
if ($running.Count) { throw "$($running -join ', ') running on this PC, so a run now couldn't be compared with the others. Try again when it's free." }

$pool = @('ClaudeTesting') + @(Get-EvalConfigs | ForEach-Object Name)
$task = @{ Repo = 'MemEval'; Branch = "$Workload-$Config-$Repeat" }

# vmtest on the eval VM, with its output and exit code returned.
function vt {
    param([Parameter(Mandatory)][string]$Command, [Parameter(ValueFromRemainingArguments)]$Rest)
    Invoke-EvalVmTest -ToolPath $ToolPath -Pool $pool -Arguments (@($Command) + @($Rest) + @('-Repo', $task.Repo, '-Branch', $task.Branch))
}
# The same, throwing if it fails and returning only the output.
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
$beginTried = $false
$workloadStarted = $false
$startError = ''
$handedBack = $true
$argumentText = (@($WorkloadArguments.Keys | Sort-Object | ForEach-Object { "$_=$($WorkloadArguments[$_])" }) -join ',')
$record = [ordered]@{
    schema = (Get-EvalSchema); host = $env:COMPUTERNAME; workload = $Workload; config = $Config; maxGB = $size.MaxGB; repeat = $Repeat
    startedAt = (Get-Date).ToString('o'); settleSeconds = $SettleSeconds
    # Only runs with the same setup are compared. A 3-minute pilot says nothing beside a 10-minute run.
    setup = "settle=$SettleSeconds;$argumentText"
    cleanFrom = ''; memory = $null
    durationSeconds = $null; exitCode = $null; testsTotal = $null; failedTests = @(); speech = $null; latency = $null; limits = @()
    guest = $null; guestEvents = $null; topProcesses = @(); pagefileGrewMB = 0
    hostMinAvailableMB = $null; hostPeakCpuPercent = $null; otherVMsRunning = @(); vmMemoryStatusWarningSeconds = $null; peakDemandMB = $null; peakAssignedMB = $null
    discarded = $false; discardReason = ''; notes = @()
}

try {
    $beganAt = Get-Date
    $beginTried = $true
    vtOk begin -VM $Config | Out-Null
    $record.beginSeconds = [math]::Round(((Get-Date) - $beganAt).TotalSeconds, 1)
    $live = Get-VM -Name $Config
    $memory = Get-VMMemory -VMName $Config
    $record.memory = [ordered]@{ minGB = $memory.Minimum / 1GB; startupGB = $memory.Startup / 1GB; maxGB = $memory.Maximum / 1GB; dynamic = [bool]$memory.DynamicMemoryEnabled }
    $record.cleanFrom = "$($live.Notes)"
    if ($memory.Maximum -ne $size.MaxGB * 1GB -or $memory.Startup -ne $size.StartupGB * 1GB -or $memory.Minimum -ne $size.MinGB * 1GB -or -not $memory.DynamicMemoryEnabled) {
        throw "$Config is $($memory.Minimum / 1GB) / $($memory.Startup / 1GB) / $($memory.Maximum / 1GB) GB after 'begin', not $($size.MinGB) / $($size.StartupGB) / $($size.MaxGB) GB. This run would measure the wrong size; run New-EvalVMs.ps1 again."
    }

    if (-not $NoCounters) {
        # A limit of its own, in case this script is killed before it can write the stop file.
        $sampler = Start-Process powershell.exe -PassThru -WindowStyle Hidden -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            "`"$(Join-Path $PSScriptRoot 'Measure-Host.ps1')`"", '-Folder', "`"$sampleDir`"", '-StopFile', "`"$stopFile`"", '-MaxMinutes', '360'
    }
    if ($SettleSeconds) { Start-Sleep -Seconds $SettleSeconds }
    if (-not $NoCounters) { vtOk run -ScriptFile (Join-Path $PSScriptRoot 'guest\Start-Counters.ps1') -Elevated -Timeout 120 | Out-Null }

    # A workload that dies is a result, not a broken run: running out of memory is what this is looking for.
    $workloadStarted = $true
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $result = $null
    try {
        $returned = @(& $workloadScript.FullName -Vt ${function:vt} -VtOk ${function:vtOk} -Arguments $WorkloadArguments)
        if ($returned.Count -eq 1 -and $null -ne $returned[0].exitCode) { $result = $returned[0] }
        else { $record.notes += "The workload returned $($returned.Count) objects, not one with an exit code, so it counts as failed." }
    } catch { $record.notes += "The workload failed: $($_.Exception.Message)" }
    $watch.Stop()
    $record.durationSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
    $record.exitCode = -1
    if ($result) {
        foreach ($k in 'exitCode', 'testsTotal', 'failedTests', 'speech', 'latency', 'limits') {
            if ($result.PSObject.Properties[$k]) { $record[$k] = $result.$k }
        }
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
    # Even after a failed 'begin', which can leave the VM running and locked. It's harmless if nothing
    # was taken, since 'end' only acts on what this run's own task holds.
    if ($beginTried) {
        $ended = vt end
        if ($ended.ExitCode -ne 0) { $handedBack = $false; $record.notes += "vmtest end failed: $($ended.Output -join ' ')" }
    }
}

# What the PC itself saw during the run, and whether it can be compared with the others.
$samples = @(Get-ChildItem $sampleDir -Filter '*.csv' | Where-Object { $_.Name -ne 'guest-counters.csv' } | ForEach-Object { Import-Csv $_.FullName })
if ($samples.Count) {
    $readings = @($samples | Where-Object { "$($_.HostAvailableMB)" -ne '' })
    if ($readings.Count) {
        $record.hostMinAvailableMB = (@($readings | ForEach-Object { [double]$_.HostAvailableMB }) | Measure-Object -Minimum).Minimum
        $record.hostPeakCpuPercent = (@($readings | ForEach-Object { [double]$_.HostCpuPercent }) | Measure-Object -Maximum).Maximum
    }
    $mine = @($samples | Where-Object { $_.VM -eq $Config })
    if ($mine.Count) {
        $record.peakDemandMB = (@($mine | ForEach-Object { [double]$_.DemandMB }) | Measure-Object -Maximum).Maximum
        $record.peakAssignedMB = (@($mine | ForEach-Object { [double]$_.AssignedMB }) | Measure-Object -Maximum).Maximum
        $record.vmMemoryStatusWarningSeconds = @($mine | Where-Object { $_.Status -match 'Warning|Low' }).Count * 5
    }
    $record.otherVMsRunning = @($samples | Where-Object { $_.VM -and $_.VM -ne $Config } | ForEach-Object VM | Sort-Object -Unique)
}

# Warm-up counts only real measured runs in this format. One that can't be read just doesn't count,
# so it can't lose the run that has just finished.
$earlier = @(Get-ChildItem $runDir -Filter "*-$Config-*.json" -ErrorAction SilentlyContinue | Where-Object {
        try {
            $e = Get-Content $_.FullName -Raw | ConvertFrom-Json
            $e.schema -eq (Get-EvalSchema) -and $e.guest
        } catch { $false }
    })
$reason = Get-RunDiscardReason -Started $workloadStarted -ExitCode ([int]$record.exitCode) -HasCounters (Test-CountersComplete $record.guest ([double]$record.durationSeconds)) `
    -OtherVMs @($record.otherVMsRunning) -HostMinAvailableMB $record.hostMinAvailableMB -HasHostRows ($NoCounters -or $samples.Count -gt 0) -ReserveGB $ReserveGB `
    -FirstOnVM (($earlier.Count -eq 0) -and (-not $KeepWarmup)) -StartError $startError
if ($NoCounters -and $workloadStarted) { $reason = 'Overhead check, with the counters off.' }
$record.discardReason = $reason
$record.discarded = [bool]$reason

$file = Join-Path $runDir "$Workload-$Config-$Repeat-$(Get-Date -Format yyyyMMddHHmmss).json"
[pscustomobject]$record | ConvertTo-Json -Depth 6 | Set-Content $file -Encoding UTF8
[pscustomobject]$record | Select-Object host, workload, config, repeat, durationSeconds, exitCode, peakDemandMB, peakAssignedMB, hostMinAvailableMB, discarded, discardReason
foreach ($note in $record.notes) { "Note: $note" }
"Recorded $file"
if (-not $handedBack) { throw "$Config wasn't handed back. See the note above; it may still be running and locked." }
