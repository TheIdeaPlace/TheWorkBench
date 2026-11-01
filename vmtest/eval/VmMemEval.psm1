# The rules of the VM memory evaluation: the configs, what a run's counters mean, and whether a size
# passes. Apart from Invoke-EvalVmTest, nothing here touches a VM or a host, so it can all be tested
# without one. docs\memory-evaluation.md holds the results; README.md here says how to run it.
#
# The rule throughout: missing data is never a pass. A starved VM that loses its counters or its
# speech log is exactly what this is looking for.

$ErrorActionPreference = 'Stop'

# The sizes under test. Control offers more than any host has today's VMs limited to, so nothing is
# held back. MemEval-2 is meant to fail: it proves the criteria notice a real shortage.
function Get-EvalConfigs {
    @(
        [pscustomobject]@{ Name = 'MemEval-12'; MinGB = 2; StartupGB = 4; MaxGB = 12; Role = 'Control' }
        [pscustomobject]@{ Name = 'MemEval-8'; MinGB = 2; StartupGB = 4; MaxGB = 8; Role = 'Candidate' }
        [pscustomobject]@{ Name = 'MemEval-6'; MinGB = 2; StartupGB = 4; MaxGB = 6; Role = 'Candidate' }
        [pscustomobject]@{ Name = 'MemEval-4'; MinGB = 2; StartupGB = 4; MaxGB = 4; Role = 'Candidate' }
        [pscustomobject]@{ Name = 'MemEval-2'; MinGB = 2; StartupGB = 2; MaxGB = 2; Role = 'TooSmall' }
    )
}

# Both hosts have to have an answer before there is one.
function Get-EvalHosts { 'SURFACEPRO7', 'THESTADIUM' }

# The numbers in the success criteria. docs\memory-evaluation.md says how they got here. A change to
# one that feeds a stored summary (AvailableMB, PagesInputPerSec, the soak window) has to bump
# Get-EvalSchema too, or old runs would be judged by a summary worked out under the old number.
function Get-EvalThresholds {
    [pscustomobject]@{
        MinRuns             = 3      # runs needed at a size, Control included, before it can pass
        TaskTimePercent     = 10     # criterion 5
        LatencyMedianPercent = 10    # criterion 6
        LatencyMedianMs     = 100
        LatencyWorstPercent = 20
        LatencyWorstMs      = 200
        # Criterion 7, in MB rather than a share of the maximum: dynamic memory keeps taking free memory
        # back, so an idle 12 GB VM sits lower than an idle 4 GB one.
        AvailableMB         = 100
        SustainedSeconds    = 30     # criteria 7 and 8: anything shorter is a brief dip
        PagesInputPerSec    = 100    # criterion 8
        # Idle VMs pass 100 pages/s for minutes at any size, and Control's own runs varied by 1.6 times,
        # so only clearly more than Control's longest counts.
        PagingControlFactor = 2
        CommitPercent       = 85     # criterion 9
        SoakWindowMinutes   = 30     # criterion 10
        SoakGrowthPercent   = 5      # criterion 10: of the VM's maximum, over that window
        CountersCoverage    = 0.9    # share of a workload the counters have to span for it to count
    }
}

# These two take long enough that the plan runs them once at Control and once at the finalist size.
function Get-EvalMinRuns {
    param([string]$Workload, $Thresholds = (Get-EvalThresholds))
    if ($Workload -in 'W6', 'W9') { 1 } else { $Thresholds.MinRuns }
}

# Which criteria beyond the ones every workload has must be measured for each workload, so a speech
# capture that broke at every size can't pass as simply having no speech to compare.
function Get-EvalRequiredMeasures {
    param([string]$Workload)
    switch ($Workload) {
        'W1' { @(4) }
        'W4' { @(2, 6) }
        'W5' { @(2) }
        'W6' { @(2, 4) }
        'W8' { @(2) }
        'W9' { @(2, 10) }
        default { @() }
    }
}

function Get-EvalDataRoot { Join-Path $env:LOCALAPPDATA 'vmtest\eval' }

# Runs written in an older format, such as the pilots', stay on disk but never count.
function Get-EvalSchema { 4 }

# vmtest as the evaluation needs it: seeing the eval VMs, on this PC. Both settings last for this one
# call, so they can't leak into the window this was run from, where a later 'vmtest begin' would
# otherwise hand a real task an eval VM.
function Invoke-EvalVmTest {
    param([Parameter(Mandatory)][string]$ToolPath, [Parameter(Mandatory)][string[]]$Pool, [object[]]$Arguments = @())
    $savedPool = $env:VMTEST_POOL
    $savedHost = $env:VMTEST_HOST
    $ErrorActionPreference = 'Continue'   # so a line on stderr is output, not a failure
    try {
        $env:VMTEST_POOL = $Pool -join ','
        $env:VMTEST_HOST = $null
        $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ToolPath @Arguments 2>&1 | ForEach-Object { "$_" }
        [pscustomobject]@{ Output = @($out); ExitCode = $LASTEXITCODE }
    } finally {
        $env:VMTEST_POOL = $savedPool
        $env:VMTEST_HOST = $savedHost
    }
}

# ---- reading what a run recorded ----------------------------------------------------------------

function ConvertTo-Number($Text) {
    $value = 0.0
    if ([double]::TryParse("$Text".Trim(), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$value)) { $value } else { $null }
}

# The CSV relog writes from the guest's counter log. The first column is the time; the other headers
# start with the computer's name, so columns are found by what they end with. A sample a counter
# has no value for (often the first) comes through as a blank.
function ConvertFrom-CounterCsv {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Csv)
    $rows = @($Csv | ConvertFrom-Csv)
    if ($rows.Count -eq 0) { return @() }
    $columns = @($rows[0].PSObject.Properties.Name)
    $find = { param($suffix) $columns | Where-Object { $_ -like "*\$suffix" } | Select-Object -First 1 }
    $map = @{
        AvailableMB = & $find 'Memory\Available MBytes'
        Committed   = & $find 'Memory\Committed Bytes'
        CommitLimit = & $find 'Memory\Commit Limit'
        PagesInput  = & $find 'Memory\Pages Input/sec'
        PagefilePct = & $find 'Paging File(_Total)\% Usage'
    }
    foreach ($row in $rows) {
        $when = [datetime]::MinValue
        [void][datetime]::TryParse($row.($columns[0]), [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$when)
        $o = [ordered]@{ Time = $when }
        foreach ($k in $map.Keys) { $o[$k] = if ($map[$k]) { ConvertTo-Number $row.($map[$k]) } else { $null } }
        [pscustomobject]$o
    }
}

# How long, in seconds, the longest unbroken stretch of samples matching a test lasted. A sample
# with no value breaks nothing and adds nothing: the counter simply didn't say.
function Get-LongestRunSeconds {
    param([object[]]$Values, [Parameter(Mandatory)][scriptblock]$Test, [double]$SampleSeconds = 5)
    $longest = 0; $current = 0
    foreach ($v in @($Values)) {
        if ($null -eq $v) { continue }
        if (& $Test $v) { $current++; if ($current -gt $longest) { $longest = $current } } else { $current = 0 }
    }
    $longest * $SampleSeconds
}

# How fast a series climbs, in units per sample, by least squares.
function Get-Slope {
    param([double[]]$Values)
    $n = $Values.Count
    if ($n -lt 2) { return 0.0 }
    $meanX = ($n - 1) / 2.0
    $meanY = ($Values | Measure-Object -Average).Average
    $top = 0.0; $bottom = 0.0
    for ($i = 0; $i -lt $n; $i++) { $top += ($i - $meanX) * ($Values[$i] - $meanY); $bottom += ($i - $meanX) * ($i - $meanX) }
    $top / $bottom
}

function Get-Median {
    param([double[]]$Values)
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $mid = [math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { $sorted[$mid] } else { ($sorted[$mid - 1] + $sorted[$mid]) / 2.0 }
}

# What the criteria need from one run's counters. A measure with no values comes back null, not 0, so
# a log of blanks can't read as a healthy VM. The spacing comes from the log's own times when it has
# them, since a starved guest may sample less often than it was asked to.
function Get-CounterSummary {
    param([object[]]$Rows, [Parameter(Mandatory)][double]$MaxGB, [double]$SampleSeconds = 5, $Thresholds = (Get-EvalThresholds))
    $Rows = @($Rows)
    $timed = @($Rows | Where-Object { $_.Time -and $_.Time -ne [datetime]::MinValue })
    $covered = 0.0
    if ($timed.Count -ge 2) {
        $covered = ($timed[-1].Time - $timed[0].Time).TotalSeconds
        if ($covered -gt 0) { $SampleSeconds = $covered / ($timed.Count - 1) }
    }
    $floorMB = $Thresholds.AvailableMB
    $pagesLimit = $Thresholds.PagesInputPerSec
    $isLow = { param($v) $v -lt $floorMB }.GetNewClosure()
    $isPaging = { param($v) $v -ge $pagesLimit }.GetNewClosure()
    $available = @($Rows | ForEach-Object { $_.AvailableMB } | Where-Object { $null -ne $_ })
    $pages = @($Rows | ForEach-Object { $_.PagesInput } | Where-Object { $null -ne $_ })
    $committed = @($Rows | Where-Object { $null -ne $_.Committed } | ForEach-Object { [double]$_.Committed })
    $commitPct = @($Rows | Where-Object { $_.Committed -and $_.CommitLimit } | ForEach-Object { 100.0 * $_.Committed / $_.CommitLimit })
    $soak = [int][math]::Ceiling($Thresholds.SoakWindowMinutes * 60 / $SampleSeconds)
    $tail = @($committed | Select-Object -Last $soak)
    [pscustomobject]@{
        Samples             = $Rows.Count
        AvailableSamples    = $available.Count
        PagingSamples       = $pages.Count
        CommitSamples       = $commitPct.Count
        CoveredSeconds      = [math]::Round($covered, 1)
        MinAvailableMB      = if ($available.Count) { ($available | Measure-Object -Minimum).Minimum } else { $null }
        LowAvailableSeconds = if ($available.Count) { Get-LongestRunSeconds -Values @($Rows | ForEach-Object { $_.AvailableMB }) -Test $isLow -SampleSeconds $SampleSeconds } else { $null }
        # The counters start after the run's settling time, so nothing here is startup to be ignored.
        PagingSeconds       = if ($pages.Count) { Get-LongestRunSeconds -Values @($Rows | ForEach-Object { $_.PagesInput }) -Test $isPaging -SampleSeconds $SampleSeconds } else { $null }
        PeakCommitPercent   = if ($commitPct.Count) { [math]::Round(($commitPct | Measure-Object -Maximum).Maximum, 1) } else { $null }
        PeakCommittedGB     = if ($committed.Count) { [math]::Round(($committed | Measure-Object -Maximum).Maximum / 1GB, 2) } else { $null }
        # Over the last stretch only, so a soak's startup doesn't look like a leak. A shorter run has no such stretch.
        SoakGrowthGB        = if ($soak -gt 1 -and $tail.Count -ge $soak) { [math]::Round((Get-Slope $tail) * ($tail.Count - 1) / 1GB, 3) } else { $null }
    }
}

# Whether a run's counters can speak for its whole workload: every measure has values, and the log
# lasted about as long as the workload did. One that stopped early says nothing about the rest.
function Test-CountersComplete {
    param($Summary, [double]$DurationSeconds, $Thresholds = (Get-EvalThresholds))
    if (-not $Summary) { return $false }
    if ($null -eq $Summary.LowAvailableSeconds -or $null -eq $Summary.PagingSeconds -or $null -eq $Summary.PeakCommitPercent) { return $false }
    if ($Summary.CoveredSeconds -lt $DurationSeconds * $Thresholds.CountersCoverage) { return $false }
    # Every 5 s, as the log is set up, with the first Pages Input sample always blank. A log that spans
    # the workload on a handful of readings says nothing about the time between them.
    $expected = [math]::Floor($DurationSeconds * $Thresholds.CountersCoverage / 5) - 1
    $Summary.AvailableSamples -ge $expected -and $Summary.PagingSamples -ge $expected -and $Summary.CommitSamples -ge $expected
}

# ---- the success criteria -----------------------------------------------------------------------

# Pass, Fail, NotMeasured (this workload has no such measure, in Control or here), or Incomplete
# (it should have been measured and wasn't, in some runs at least). Only the first and third can pass.
function New-Check([int]$Number, [string]$Name, [string]$Result, [string]$Detail) {
    [pscustomobject]@{ Number = $Number; Name = $Name; Result = $Result; Detail = $Detail }
}

# Whether a finished run can be compared with others, and if not, why. A workload that failed counts,
# even with no counters from a VM too starved to report them, unless another VM or a full PC could
# explain it. A workload that was fine but has no full counters says nothing about memory by itself,
# though Get-WorkloadResult holds a size back for having such runs.
function Get-RunDiscardReason {
    param([bool]$Started, [int]$ExitCode, [bool]$HasCounters, [string[]]$OtherVMs = @(), $HostMinAvailableMB, [bool]$HasHostRows = $true, [double]$ReserveGB = 4, [bool]$FirstOnVM, [string]$StartError)
    if (-not $Started) { return "The run couldn't start: $StartError" }
    if ($OtherVMs.Count) { return "Other VMs were running: $($OtherVMs -join ', ')." }
    if ($null -ne $HostMinAvailableMB -and $HostMinAvailableMB -lt $ReserveGB * 1024) { return "The PC dropped to $HostMinAvailableMB MB available, under its $ReserveGB GB reserve." }
    if ($ExitCode -ne 0) { return '' }
    if (-not $HasHostRows) { return "The PC's own numbers don't cover the run, so nothing shows it was free of other load." }
    if (-not $HasCounters) { return "The workload finished but its counters are missing or stopped early, so it says nothing about memory." }
    if ($FirstOnVM) { return 'First run on this VM, kept as warm-up.' }
    ''
}

# Runs that count: not thrown out, and of this workload, size and host.
function Select-ValidRun {
    param([object[]]$Runs, [string]$Workload, [string]$Config, [string]$HostName)
    @($Runs | Where-Object {
            -not $_.discarded -and
            (-not $Workload -or $_.workload -eq $Workload) -and
            (-not $Config -or $_.config -eq $Config) -and
            (-not $HostName -or $_.host -eq $HostName)
        })
}

function Get-RunList($Runs, [string]$Property) { @($Runs | ForEach-Object { $_.$Property } | Where-Object { $null -ne $_ } | ForEach-Object { $_ }) }

# For a measure that only some workloads have: NotMeasured if neither side has it, Incomplete if
# Control has it and any run here lost it, or if only some runs here have it. Otherwise '' to go on.
function Get-CoverageResult($Candidate, $Control, [scriptblock]$Has) {
    $here = @($Candidate | Where-Object $Has).Count
    $there = @($Control | Where-Object $Has).Count
    if ($here -eq 0 -and $there -eq 0) { return 'NotMeasured' }
    if ($here -lt @($Candidate).Count -and ($there -gt 0 -or $here -gt 0)) { return 'Incomplete' }
    ''
}

# Whether one workload passes at one size on one host, against Control's runs on that host.
function Test-EvalCriteria {
    param([Parameter(Mandatory)][object[]]$Candidate, [Parameter(Mandatory)][object[]]$Control, [Parameter(Mandatory)][double]$MaxGB, $Thresholds = (Get-EvalThresholds), [int[]]$Required = @())
    $checks = @(Get-EvalChecks -Candidate $Candidate -Control $Control -MaxGB $MaxGB -Thresholds $Thresholds)
    foreach ($c in $checks) {
        if ($c.Result -eq 'NotMeasured' -and $Required -contains $c.Number) {
            $c.Result = 'Incomplete'
            $c.Detail = 'this workload has to measure this, and neither it nor Control did'
        }
    }
    $checks
}

function Get-EvalChecks {
    param([object[]]$Candidate, [object[]]$Control, [double]$MaxGB, $Thresholds)
    $t = $Thresholds
    $total = @($Candidate).Count

    # 1: the same results. -1 is the kit's own "the workload threw", which no Control can excuse.
    $problems = @()
    $controlCodes = @($Control | Where-Object { $null -ne $_.exitCode -and [int]$_.exitCode -ne -1 } | ForEach-Object { [int]$_.exitCode } | Sort-Object -Unique)
    $noCode = @($Candidate | Where-Object { $null -eq $_.exitCode }).Count
    if ($noCode) { $problems += "$noCode run(s) recorded no exit code" }
    $badCodes = @($Candidate | Where-Object { $null -ne $_.exitCode -and $controlCodes -notcontains [int]$_.exitCode } | ForEach-Object { [int]$_.exitCode })
    if ($badCodes.Count) { $problems += "exit codes $($badCodes -join ', ') where Control had $($controlCodes -join ', ')" }
    $controlCounts = @(Get-RunList $Control 'testsTotal' | Sort-Object -Unique)
    if ($controlCounts.Count) {
        $counts = @(Get-RunList $Candidate 'testsTotal')
        $badCounts = @($counts | Where-Object { $controlCounts -notcontains $_ })
        if ($badCounts.Count) { $problems += "test counts $($badCounts -join ', ') where Control had $($controlCounts -join ', ')" }
        if ($counts.Count -lt $total) { $problems += "$($total - $counts.Count) run(s) reported no test count" }
    }
    # By how often, so a test flaky in one Control run doesn't excuse failing in every run here.
    $controlFailed = @(Get-RunList $Control 'failedTests')
    $hereFailed = @(Get-RunList $Candidate 'failedTests')
    $controlTotal = [math]::Max(1, @($Control).Count)
    $moreOften = @($hereFailed | Sort-Object -Unique | Where-Object {
            $name = $_
            @($hereFailed | Where-Object { $_ -eq $name }).Count / $total -gt @($controlFailed | Where-Object { $_ -eq $name }).Count / $controlTotal
        })
    $newFailed = @($moreOften | Where-Object { $controlFailed -notcontains $_ })
    $flakier = @($moreOften | Where-Object { $controlFailed -contains $_ })
    if ($newFailed.Count) { $problems += "tests failing only here: $($newFailed -join ', ')" }
    if ($flakier.Count) { $problems += "tests failing more often than in Control: $($flakier -join ', ')" }
    New-Check 1 'Same results' $(if ($problems.Count) { 'Fail' } else { 'Pass' }) ($problems -join '; ')

    # 2: the same speech and clean audio.
    $hasSpeech = { $_.speech -and $null -ne $_.speech.misplaced -and $null -ne $_.speech.audioGaps }
    $coverage = Get-CoverageResult $Candidate $Control $hasSpeech
    if ($coverage) { New-Check 2 'Same speech' $coverage $(if ($coverage -eq 'Incomplete') { "speech came back from $(@($Candidate | Where-Object $hasSpeech).Count) of $total runs" }) }
    else {
        $withSpeech = @($Candidate | Where-Object $hasSpeech)
        $problems = @()
        $controlFails = @($Control | Where-Object $hasSpeech | ForEach-Object { $_.speech.failed } | Where-Object { $_ } | ForEach-Object { $_ })
        $newFails = @($withSpeech | ForEach-Object { $_.speech.failed } | Where-Object { $_ } | ForEach-Object { $_ } | Where-Object { $controlFails -notcontains $_ } | Sort-Object -Unique)
        if ($newFails.Count) { $problems += "steps failing only here: $($newFails -join ', ')" }
        $misplaced = (@($withSpeech | ForEach-Object { [int]$_.speech.misplaced }) | Measure-Object -Sum).Sum
        if ($misplaced) { $problems += "$misplaced speech in the wrong step" }
        $gaps = (@($withSpeech | ForEach-Object { [int]$_.speech.audioGaps }) | Measure-Object -Sum).Sum
        if ($gaps) { $problems += "$gaps audio gaps" }
        New-Check 2 'Same speech' $(if ($problems.Count) { 'Fail' } else { 'Pass' }) ($problems -join '; ')
    }

    # 3: no resource exhaustion and no new crashes. The events can only be read from a VM that answered.
    $problems = @()
    $withEvents = @($Candidate | Where-Object { $_.guestEvents })
    $exhausted = (@($withEvents | ForEach-Object { [int]$_.guestEvents.exhaustion }) | Measure-Object -Sum).Sum
    if ($exhausted) { $problems += "$exhausted resource exhaustion events" }
    $controlCrashes = @($Control | Where-Object { $_.guestEvents } | ForEach-Object { $_.guestEvents.crashes } | Where-Object { $_ } | ForEach-Object { $_ })
    $newCrashes = @($withEvents | ForEach-Object { $_.guestEvents.crashes } | Where-Object { $_ } | ForEach-Object { $_ } | Where-Object { $controlCrashes -notcontains $_ } | Sort-Object -Unique)
    if ($newCrashes.Count) { $problems += "crashes only here: $($newCrashes -join ', ')" }
    $eventsResult = if ($problems.Count) { 'Fail' } elseif ($withEvents.Count -lt $total) { 'Incomplete' } else { 'Pass' }
    if ($withEvents.Count -lt $total) { $problems += "events came back from $($withEvents.Count) of $total runs" }
    New-Check 3 'No exhaustion or new crashes' $eventsResult ($problems -join '; ')

    # 4: hard time limits.
    $coverage = Get-CoverageResult $Candidate $Control { @($_.limits).Count -gt 0 -and $null -ne @($_.limits)[0] }
    if ($coverage) { New-Check 4 'Time limits met' $coverage '' }
    else {
        $limits = @($Candidate | ForEach-Object { $_.limits } | Where-Object { $_ } | ForEach-Object { $_ })
        $over = @($limits | Where-Object { $null -ne $_.actual -and [double]$_.actual -gt [double]$_.limit } | ForEach-Object { "$($_.name) took $($_.actual) s, limit $($_.limit) s" })
        # A step never reached has no time, and on a starved VM that may be the whole story.
        $names = @($Control | ForEach-Object { $_.limits } | Where-Object { $_ } | ForEach-Object { $_.name } | Sort-Object -Unique)
        $unreached = @($limits | Where-Object { $null -eq $_.actual } | ForEach-Object { "$($_.name) has no time" })
        foreach ($run in $Candidate) {
            foreach ($n in $names) { if (-not @(@($run.limits) | Where-Object { $_ -and $_.name -eq $n }).Count) { $unreached += "a run has no $n" } }
        }
        $result = if ($over.Count) { 'Fail' } elseif ($unreached.Count) { 'Incomplete' } else { 'Pass' }
        New-Check 4 'Time limits met' $result ((@($over) + @($unreached | Sort-Object -Unique)) -join '; ')
    }

    # 5: task time, against Control's median, or the slowest Control run if Control itself varies more.
    $controlTimes = @(Get-RunList $Control 'durationSeconds' | ForEach-Object { [double]$_ })
    $times = @(Get-RunList $Candidate 'durationSeconds' | ForEach-Object { [double]$_ })
    if ($controlTimes.Count -eq 0 -or $times.Count -lt $total) {
        New-Check 5 'Task time' 'Incomplete' "times from $($times.Count) of $total runs, and $($controlTimes.Count) from Control"
    } else {
        $controlMedian = Get-Median $controlTimes
        $allowed = [math]::Max($controlMedian * (1 + $t.TaskTimePercent / 100.0), ($controlTimes | Measure-Object -Maximum).Maximum)
        $median = Get-Median $times
        New-Check 5 'Task time' $(if ($median -gt $allowed) { 'Fail' } else { 'Pass' }) ("median {0:N1} s, allowed {1:N1} s, Control {2:N1} s" -f $median, $allowed, $controlMedian)
    }

    # 6: key press to first speech. With no Control figures there's nothing to compare it with.
    $hasLatency = { $_.latency -and $null -ne $_.latency.medianMs -and $null -ne $_.latency.worstMs }
    $withLatency = @($Candidate | Where-Object $hasLatency)
    $controlLatency = @($Control | Where-Object $hasLatency)
    if ($controlLatency.Count -eq 0) { New-Check 6 'Speech latency' 'NotMeasured' '' }
    elseif ($withLatency.Count -lt $total) { New-Check 6 'Speech latency' 'Incomplete' "latency came back from $($withLatency.Count) of $total runs" }
    else {
        $med = Get-Median @($withLatency | ForEach-Object { [double]$_.latency.medianMs })
        $worst = Get-Median @($withLatency | ForEach-Object { [double]$_.latency.worstMs })
        $controlMed = Get-Median @($controlLatency | ForEach-Object { [double]$_.latency.medianMs })
        $controlWorst = Get-Median @($controlLatency | ForEach-Object { [double]$_.latency.worstMs })
        $allowMed = $controlMed + [math]::Max($controlMed * $t.LatencyMedianPercent / 100.0, $t.LatencyMedianMs)
        $allowWorst = $controlWorst + [math]::Max($controlWorst * $t.LatencyWorstPercent / 100.0, $t.LatencyWorstMs)
        New-Check 6 'Speech latency' $(if ($med -gt $allowMed -or $worst -gt $allowWorst) { 'Fail' } else { 'Pass' }) ("median {0:N0} ms (allowed {1:N0}), worst 5% {2:N0} ms (allowed {3:N0})" -f $med, $allowMed, $worst, $allowWorst)
    }

    # 7 to 9 need the guest's counters from every run. A bad measure from the runs that have them still
    # fails; otherwise a run without them leaves the criterion Incomplete, however it went.
    $missing = {
        param([string]$field)
        @($Candidate | Where-Object { -not $_.guest -or $null -eq $_.guest.$field }).Count
    }
    $worstOf = {
        param([string]$field)
        $values = @($Candidate | Where-Object { $_.guest -and $null -ne $_.guest.$field } | ForEach-Object { [double]$_.guest.$field })
        if ($values.Count) { ($values | Measure-Object -Maximum).Maximum } else { $null }
    }
    # 7: room left in the guest.
    $low = & $worstOf 'LowAvailableSeconds'
    $gone = & $missing 'LowAvailableSeconds'
    $result = if ($null -ne $low -and $low -ge $t.SustainedSeconds) { 'Fail' } elseif ($gone) { 'Incomplete' } else { 'Pass' }
    New-Check 7 'Memory available' $result "under $($t.AvailableMB) MB for $low s at longest, measured in $($total - $gone) of $total runs"

    # 8: no sustained paging, unless Control pages about as long, in which case it isn't about size.
    $paging = & $worstOf 'PagingSeconds'
    $gone = & $missing 'PagingSeconds'
    $controlPaging = [double](@($Control | Where-Object { $_.guest -and $null -ne $_.guest.PagingSeconds } | ForEach-Object { [double]$_.guest.PagingSeconds }) | Measure-Object -Maximum).Maximum
    $result = if ($null -ne $paging -and $paging -ge $t.SustainedSeconds -and $paging -gt $controlPaging * $t.PagingControlFactor) { 'Fail' } elseif ($gone) { 'Incomplete' } else { 'Pass' }
    New-Check 8 'No sustained paging' $result "$paging s at longest, Control $controlPaging s, measured in $($total - $gone) of $total runs"

    # 9: commit and the paging file.
    $commit = & $worstOf 'PeakCommitPercent'
    $gone = & $missing 'PeakCommitPercent'
    $grew = @($Candidate | Where-Object { $_.pagefileGrewMB -gt 0 }).Count
    $problems = @()
    if ($null -ne $commit -and $commit -ge $t.CommitPercent) { $problems += "commit reached $commit%" }
    if ($grew) { $problems += "paging file grew in $grew run(s)" }
    $result = if ($problems.Count) { 'Fail' } elseif ($gone) { 'Incomplete' } else { 'Pass' }
    New-Check 9 'Commit and paging file' $result $(if ($problems.Count) { $problems -join '; ' } else { "commit peaked at $commit%, measured in $($total - $gone) of $total runs" })

    # 10: soak only. A run is a soak if Control's was long enough to have a figure.
    $coverage = Get-CoverageResult $Candidate $Control { $_.guest -and $null -ne $_.guest.SoakGrowthGB }
    if ($coverage) { New-Check 10 'No steady growth' $coverage '' }
    else {
        $growth = @($Candidate | Where-Object { $_.guest -and $null -ne $_.guest.SoakGrowthGB } | ForEach-Object { [double]$_.guest.SoakGrowthGB })
        $worstGrowth = ($growth | Measure-Object -Maximum).Maximum
        $limit = $MaxGB * $t.SoakGrowthPercent / 100.0
        New-Check 10 'No steady growth' $(if ($worstGrowth -gt $limit) { 'Fail' } else { 'Pass' }) "$worstGrowth GB over the last $($t.SoakWindowMinutes) min, allowed $limit GB"
    }
}

# A size passes only if nothing failed, nothing is incomplete, and the criteria every workload has
# were actually measured.
function Test-ChecksPass {
    param([object[]]$Checks)
    if (@($Checks | Where-Object { $_.Result -eq 'Fail' -or $_.Result -eq 'Incomplete' }).Count) { return $false }
    foreach ($n in 1, 3, 5, 7, 8, 9) {
        if (-not @($Checks | Where-Object { $_.Number -eq $n -and $_.Result -eq 'Pass' }).Count) { return $false }
    }
    $true
}

# Why these Control runs can't be compared against, or '' if they can.
function Get-ControlProblem {
    param([object[]]$Control, [int]$MinRuns = 1)
    $count = @($Control).Count
    if ($count -lt $MinRuns) { return "Control has $count of the $MinRuns runs it needs." }
    if (@($Control | Where-Object { $_.guestEvents -and [int]$_.guestEvents.exhaustion -gt 0 }).Count) { return 'Control itself ran out of memory.' }
    if (@($Control | Where-Object { $null -eq $_.exitCode -or [int]$_.exitCode -eq -1 }).Count) { return 'A Control run died, so the workload or the kit needs looking at first.' }
    # A workload's exit code says it ran to the end; what its tests found goes in failedTests. So one
    # that doesn't exit 0 with memory to spare is broken at every size, and would excuse a starved VM.
    if (@($Control | Where-Object { [int]$_.exitCode -ne 0 }).Count) { return 'A Control run exited non-zero, so the workload needs looking at first.' }
    if (@($Control | Where-Object { -not $_.guest }).Count) { return 'A Control run has no counters.' }
    $counts = @($Control | ForEach-Object { $_.testsTotal } | Where-Object { $null -ne $_ } | Sort-Object -Unique)
    if ($counts.Count -gt 1) { return "Control's runs disagree on how many tests ran: $($counts -join ', ')." }
    if ($counts.Count -and $counts[0] -eq 0) { return 'Control ran no tests.' }
    ''
}

# Which sizes passed, for one workload on one host, and the smallest that can be trusted: passing is
# only believed above the largest size that failed, because memory doesn't help by being smaller. A
# size with no runs is fine, since the plan starts at 4 GB and only goes up if that fails. Trouble at
# a size that couldn't be judged still counts against the sizes below it.
function Get-WorkloadResult {
    param([Parameter(Mandatory)][object[]]$Runs, [Parameter(Mandatory)][string]$Workload, [Parameter(Mandatory)][string]$HostName, $Thresholds = (Get-EvalThresholds), [int]$MinRuns = 0)
    if (-not $MinRuns) { $MinRuns = Get-EvalMinRuns $Workload $Thresholds }
    $control = @(Select-ValidRun $Runs $Workload 'MemEval-12' $HostName)
    $controlProblem = Get-ControlProblem $control $MinRuns
    $troubledAt = {
        param([string]$config)
        @($Runs | Where-Object { $_.troubled -and $_.workload -eq $Workload -and $_.config -eq $config -and $_.host -eq $HostName }).Count
    }
    $controlTroubled = & $troubledAt 'MemEval-12'
    $sizes = @(foreach ($c in Get-EvalConfigs | Where-Object { $_.Role -ne 'Control' }) {
            $sizeRuns = @(Select-ValidRun $Runs $Workload $c.Name $HostName)
            $troubled = & $troubledAt $c.Name
            if ($sizeRuns.Count -eq 0 -and $troubled -eq 0) { continue }
            $setups = @(@($control) + @($sizeRuns) | ForEach-Object { "$($_.setup)" } | Sort-Object -Unique)
            $reason =
                # Retrying until enough runs come back clean mustn't read as passing.
                if ($troubled -gt $controlTroubled) { "$troubled of its runs failed to start or lost their counters, against $controlTroubled at Control, which may be the shortage itself." }
                elseif ($controlProblem) { $controlProblem }
                elseif ($sizeRuns.Count -lt $MinRuns) { "It has $($sizeRuns.Count) of the $MinRuns runs it needs." }
                elseif ($setups.Count -gt 1) { 'Its runs and Control''s used different settings, so they can''t be compared.' }
                else { '' }
            if ($reason) {
                $badRuns = @($sizeRuns | Where-Object { [int]$_.exitCode -ne 0 -or ($_.guestEvents -and [int]$_.guestEvents.exhaustion -gt 0) }).Count
                [pscustomobject]@{ Config = $c.Name; MaxGB = $c.MaxGB; Role = $c.Role; Runs = $sizeRuns.Count; Pass = $null; Reason = $reason; Checks = @(); Trouble = ($badRuns -gt 0 -or $troubled -gt $controlTroubled) }
            } else {
                $checks = @(Test-EvalCriteria -Candidate $sizeRuns -Control $control -MaxGB $c.MaxGB -Thresholds $Thresholds -Required (Get-EvalRequiredMeasures $Workload))
                $pass = Test-ChecksPass $checks
                [pscustomobject]@{ Config = $c.Name; MaxGB = $c.MaxGB; Role = $c.Role; Runs = $sizeRuns.Count; Pass = $pass; Reason = ''; Checks = $checks; Trouble = (-not $pass) }
            }
        })
    $candidates = @($sizes | Where-Object { $_.Role -eq 'Candidate' })
    $failedSizes = @($candidates | Where-Object { $_.Trouble } | ForEach-Object { $_.MaxGB })
    $largestFailed = if ($failedSizes.Count) { ($failedSizes | Measure-Object -Maximum).Maximum } else { 0 }
    $passing = @($candidates | Where-Object { $_.Pass -eq $true -and $_.MaxGB -gt $largestFailed } | Sort-Object MaxGB)
    $outOfOrder = @($candidates | Where-Object { $_.Pass -eq $true -and $_.MaxGB -lt $largestFailed }).Count -gt 0
    # A size meant to be too small that passes means the criteria can't see a shortage in this
    # workload, so its other passes prove nothing.
    $tooSmall = @($sizes | Where-Object { $_.Role -eq 'TooSmall' })
    # Unjudged clean runs at that size count too: too few to pass, but still no sign of a shortage.
    $blind = @($tooSmall | Where-Object { $_.Pass -eq $true -or ($null -eq $_.Pass -and -not $_.Trouble -and -not $controlProblem) }).Count -gt 0
    $peaks = @($control | Where-Object { $_.guest -and $null -ne $_.guest.PeakCommittedGB } | ForEach-Object { [double]$_.guest.PeakCommittedGB })
    [pscustomobject]@{
        Workload        = $Workload
        Host            = $HostName
        ControlRuns     = $control.Count
        ControlProblem  = $controlProblem
        PeakGB          = if ($peaks.Count) { ($peaks | Measure-Object -Maximum).Maximum } else { $null }
        Sizes           = $sizes
        OutOfOrder      = $outOfOrder
        CriteriaBlind   = $blind
        ShortageCaught  = @($tooSmall | Where-Object { $_.Pass -eq $false }).Count -gt 0
        SmallestGB      = if ($passing.Count -and -not $blind) { $passing[0].MaxGB } else { $null }
    }
}

# The plan's decision rules, as far as the workload results alone can take them. Tiers also need
# Phase 3's numbers, so that part comes back as a question to answer, not an answer. W9 only runs at
# the finalist size, so it can't choose a size, but the size isn't settled until it has passed there.
function Get-EvalDecision {
    param([Parameter(Mandatory)][object[]]$Results, [string[]]$Hosts = (Get-EvalHosts), [string[]]$LightWorkloads = @('W0', 'W1', 'W2', 'W3', 'W4', 'W5', 'W6'), [string[]]$HeavyWorkloads = @('W7', 'W8'), [string]$SoakWorkload = 'W9')
    $need = {
        param([string[]]$workloads, [string[]]$on)
        $largest = $null
        foreach ($h in $on) {
            foreach ($w in $workloads) {
                $r = @($Results | Where-Object { $_.Workload -eq $w -and $_.Host -eq $h })[0]
                # One unknown makes the answer unknown: the size has to fit every workload on every host.
                if (-not $r -or $null -eq $r.SmallestGB) { return $null }
                if ($null -eq $largest -or $r.SmallestGB -gt $largest) { $largest = $r.SmallestGB }
            }
        }
        $largest
    }
    $default = & $need $LightWorkloads $Hosts
    $heavy = & $need $HeavyWorkloads $Hosts
    $perHost = @{}
    foreach ($h in $Hosts) { $perHost[$h] = & $need ($LightWorkloads + $HeavyWorkloads) @($h) }
    $known = @($perHost.Values | Where-Object { $null -ne $_ } | Sort-Object -Unique)
    $noRuns = @($Hosts | Where-Object { $h = $_; -not @($Results | Where-Object { $_.Host -eq $h -and $_.ControlRuns }).Count })
    $answer =
        if ($noRuns.Count) { "Not yet: no results from $($noRuns -join ' or ')." }
        elseif ($null -eq $default) { 'Not yet: some light workloads have no passing size on some host.' }
        elseif ($null -eq $heavy) { "Default maximum $default GB so far. W7 and W8 have no passing size yet." }
        elseif ($heavy -le $default) { "One size: $default GB maximum. W7 and W8 fit, so no tiers." }
        else { "Default $default GB; W7 or W8 need $heavy GB. Phase 3 decides: a heavy tier only if it lets a host run one more VM or lowers host peak memory by at least 4 GB. Otherwise one size at $heavy GB." }
    $notes = @()
    $chosen = if ($null -ne $default) { if ($null -ne $heavy -and $heavy -gt $default) { $heavy } else { $default } }
    if ($null -ne $chosen) {
        $soakShort = @($Hosts | Where-Object {
                $h = $_
                $s = @($Results | Where-Object { $_.Workload -eq $SoakWorkload -and $_.Host -eq $h })[0]
                -not ($s -and $null -ne $s.SmallestGB -and $s.SmallestGB -le $chosen)
            })
        if ($soakShort.Count) { $notes += "The soak hasn't passed at $chosen GB or below on $($soakShort -join ' or ') yet, so this isn't final." }
    }
    $caught = @($Results | Where-Object { $_.ShortageCaught })
    if ($caught.Count) { $notes += "The too-small VM failed as it should for $(($caught | ForEach-Object { "$($_.Workload) on $($_.Host)" }) -join ', '), and that proof only covers those." }
    else { $notes += 'No run has yet shown these criteria catching a real shortage.' }
    if ($known.Count -gt 1) { $notes += "The hosts need different sizes: $(($Hosts | ForEach-Object { "$_ $($perHost[$_]) GB" }) -join ', ')." }
    $blind = @($Results | Where-Object { $_.CriteriaBlind })
    if ($blind.Count) { $notes += "The too-small VM passed for $(($blind | ForEach-Object { "$($_.Workload) on $($_.Host)" }) -join ', '), so those results prove nothing." }
    [pscustomobject]@{ DefaultGB = $default; HeavyGB = $heavy; PerHost = $perHost; HostsDiffer = $known.Count -gt 1; Answer = $answer; Notes = $notes }
}

# ---- the report ---------------------------------------------------------------------------------

function Read-EvalRuns {
    param([string[]]$Root = (Get-EvalDataRoot))
    foreach ($r in $Root) {
        $dir = Join-Path $r 'runs'
        if (-not (Test-Path $dir)) { continue }
        foreach ($f in Get-ChildItem $dir -Recurse -Filter *.json) {
            # One unreadable file, perhaps collected mid-write, shouldn't stop the report. It says so.
            try { $run = Get-Content $f.FullName -Raw | ConvertFrom-Json }
            catch { Write-Warning "Couldn't read $($f.FullName), so it isn't counted: $($_.Exception.Message)"; continue }
            if ($run.schema -eq (Get-EvalSchema)) { $run }
        }
    }
}

# Plain sentences rather than a table, because this prints to a console a screen reader reads.
function Format-EvalReport {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Runs, [string[]]$Hosts = (Get-EvalHosts), $Thresholds = (Get-EvalThresholds), [int]$MinRuns = 0)
    if (@($Runs).Count -eq 0) { return 'No runs recorded yet.' }
    $workloads = @($Runs | ForEach-Object { $_.workload } | Sort-Object -Unique)
    $discarded = @($Runs | Where-Object { $_.discarded })
    $results = @(foreach ($h in $Hosts) { foreach ($w in $workloads) { Get-WorkloadResult -Runs $Runs -Workload $w -HostName $h -Thresholds $Thresholds -MinRuns $MinRuns } })
    $decision = Get-EvalDecision -Results $results -Hosts $Hosts
    "VM memory evaluation results"
    ""
    "$(@($Runs).Count) runs recorded, $($discarded.Count) thrown out, for hosts $($Hosts -join ' and ')."
    ""
    "Decision so far: $($decision.Answer)"
    foreach ($n in $decision.Notes) { "Note: $n" }
    ""
    "Smallest passing size:"
    foreach ($r in $results) {
        $peak = if ($null -ne $r.PeakGB) { "peak committed $($r.PeakGB) GB" } else { 'no peak recorded' }
        $sizes = @($r.Sizes | ForEach-Object {
                $state = if ($_.Pass -eq $true) { 'passed' } elseif ($_.Pass -eq $false) { 'failed' } else { "not judged: $($_.Reason)" }
                "$($_.MaxGB) GB $state ($($_.Runs) runs)"
            })
        $smallest = if ($null -ne $r.SmallestGB) { "smallest passing $($r.SmallestGB) GB" } else { 'no passing size yet' }
        "- $($r.Host), $($r.Workload): $($r.ControlRuns) Control runs, $peak, $smallest. $($sizes -join '; ')"
        if ($r.ControlProblem) { "  Control: $($r.ControlProblem)" }
        if ($r.OutOfOrder) { "  Warning: a smaller size passed after a larger one failed, so the smaller pass isn't believed." }
    }
    ""
    "Criteria not passed:"
    $failures = @(foreach ($r in $results) { foreach ($s in $r.Sizes) { foreach ($c in @($s.Checks | Where-Object { $_.Result -eq 'Fail' -or $_.Result -eq 'Incomplete' })) { "- $($r.Host), $($r.Workload), $($s.MaxGB) GB maximum: criterion $($c.Number), $($c.Name), $($c.Result). $($c.Detail)" } } })
    if ($failures.Count) { $failures } else { 'None.' }
    ""
    "Not measured, because neither these runs nor Control have them:"
    $unmeasured = @(foreach ($r in $results) { foreach ($s in $r.Sizes) { $n = @($s.Checks | Where-Object { $_.Result -eq 'NotMeasured' } | ForEach-Object { $_.Name }); if ($n.Count) { "- $($r.Host), $($r.Workload), $($s.MaxGB) GB maximum: $($n -join ', ')." } } })
    if ($unmeasured.Count) { $unmeasured } else { 'None.' }
    if ($discarded.Count) {
        ""
        "Runs thrown out:"
        foreach ($d in $discarded) { "- $($d.host), $($d.workload), $($d.config), run $($d.repeat): $($d.discardReason)" }
    }
    ""
    "Thresholds used:"
    foreach ($p in $Thresholds.PSObject.Properties) { "- $($p.Name): $($p.Value)" }
}

Export-ModuleMember -Function Get-EvalConfigs, Get-EvalHosts, Get-EvalThresholds, Get-EvalMinRuns, Get-EvalRequiredMeasures, Get-EvalDataRoot, Get-EvalSchema, Invoke-EvalVmTest, ConvertFrom-CounterCsv,
    Get-LongestRunSeconds, Get-Slope, Get-Median, Get-CounterSummary, Test-CountersComplete, Get-RunDiscardReason, Select-ValidRun, Test-EvalCriteria, Test-ChecksPass,
    Get-ControlProblem, Get-WorkloadResult, Get-EvalDecision, Read-EvalRuns, Format-EvalReport
