# The rules of the VM memory evaluation: the configs, what a run's counters mean, and whether a size
# passes. Nothing here touches a VM or a host, so all of it can be tested without one.
# docs\memory-evaluation.md holds the results; README.md here says how to run it.

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

# The numbers in the success criteria, as the pilot left them; docs\memory-evaluation.md says why
# they changed. They're fixed before any candidate runs, and the report prints whatever was used.
function Get-EvalThresholds {
    [pscustomobject]@{
        TaskTimePercent     = 10     # criterion 5
        LatencyMedianPercent = 10    # criterion 6
        LatencyMedianMs     = 100
        LatencyWorstPercent = 20
        LatencyWorstMs      = 200
        # Criterion 7, in MB rather than a share of the maximum: dynamic memory keeps taking free memory
        # back, so an idle 12 GB VM sits lower than an idle 4 GB one. The idle baseline never went under 120.
        AvailableMB         = 100
        SustainedSeconds    = 30     # criteria 7 and 8: anything shorter is a brief dip
        PagesInputPerSec    = 100    # criterion 8
        # Idle VMs pass 100 pages/s for minutes at any size, and Control's own runs varied by 1.6 times,
        # so only clearly more than Control's longest counts.
        PagingControlFactor = 2
        IgnoreFirstSeconds  = 120    # criterion 8: startup always pages
        CommitPercent       = 85     # criterion 9
        SoakWindowMinutes   = 30     # criterion 10
        SoakGrowthPercent   = 5      # criterion 10: of the VM's maximum, over that window
    }
}

function Get-EvalDataRoot { Join-Path $env:LOCALAPPDATA 'vmtest\eval' }

# Runs written in an older format, such as the first pilot's, stay on disk but never count.
function Get-EvalSchema { 2 }

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
    if (-not $rows) { return @() }
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
    if (-not $sorted) { return $null }
    $mid = [math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { $sorted[$mid] } else { ($sorted[$mid - 1] + $sorted[$mid]) / 2.0 }
}

# What the criteria need from one run's counters.
function Get-CounterSummary {
    param([object[]]$Rows, [Parameter(Mandatory)][double]$MaxGB, [double]$SampleSeconds = 5, $Thresholds = (Get-EvalThresholds))
    $Rows = @($Rows)
    $floorMB = $Thresholds.AvailableMB
    $skip = [int][math]::Ceiling($Thresholds.IgnoreFirstSeconds / $SampleSeconds)
    $settled = @($Rows | Select-Object -Skip $skip)
    $commitPct = @($Rows | Where-Object { $_.Committed -and $_.CommitLimit } | ForEach-Object { 100.0 * $_.Committed / $_.CommitLimit })
    $soak = [int][math]::Ceiling($Thresholds.SoakWindowMinutes * 60 / $SampleSeconds)
    $committed = @($Rows | Where-Object { $_.Committed } | ForEach-Object { [double]$_.Committed })
    $tail = @($committed | Select-Object -Last $soak)
    $availableValues = @($Rows | ForEach-Object { $_.AvailableMB } | Where-Object { $null -ne $_ })
    $isLow = { param($v) $v -lt $floorMB }.GetNewClosure()
    $pagesLimit = $Thresholds.PagesInputPerSec
    $isPaging = { param($v) $v -ge $pagesLimit }.GetNewClosure()
    [pscustomobject]@{
        Samples             = $Rows.Count
        MinAvailableMB      = if ($availableValues) { ($availableValues | Measure-Object -Minimum).Minimum } else { $null }
        LowAvailableSeconds = Get-LongestRunSeconds -Values @($Rows | ForEach-Object { $_.AvailableMB }) -Test $isLow -SampleSeconds $SampleSeconds
        PagingSeconds       = Get-LongestRunSeconds -Values @($settled | ForEach-Object { $_.PagesInput }) -Test $isPaging -SampleSeconds $SampleSeconds
        PeakCommitPercent   = if ($commitPct) { [math]::Round(($commitPct | Measure-Object -Maximum).Maximum, 1) } else { $null }
        PeakCommittedGB     = if ($committed) { [math]::Round(($committed | Measure-Object -Maximum).Maximum / 1GB, 2) } else { $null }
        # Over the last stretch only, so a soak's startup doesn't look like a leak. A shorter run has no such stretch.
        SoakGrowthGB        = if ($tail.Count -ge $soak) { [math]::Round((Get-Slope $tail) * ($tail.Count - 1) / 1GB, 3) } else { $null }
    }
}

# ---- the success criteria -----------------------------------------------------------------------

function New-Check([int]$Number, [string]$Name, [string]$Result, [string]$Detail) {
    [pscustomobject]@{ Number = $Number; Name = $Name; Result = $Result; Detail = $Detail }
}

# Whether a finished run can be compared with others, and if not, why. A workload that failed always
# counts, even with no counters from a VM too starved to report them: that failure is what this is
# looking for. A workload that was fine but has no counters says nothing about memory.
function Get-RunDiscardReason {
    param([bool]$Started, [int]$ExitCode, [bool]$HasCounters, [string[]]$OtherVMs = @(), $HostMinAvailableMB, [double]$ReserveGB = 4, [bool]$FirstOnVM, [string]$StartError)
    if (-not $Started) { return "The run couldn't start: $StartError" }
    if ($OtherVMs) { return "Other VMs were running: $($OtherVMs -join ', ')." }
    if ($null -ne $HostMinAvailableMB -and $HostMinAvailableMB -lt $ReserveGB * 1024) { return "The PC dropped to $HostMinAvailableMB MB available, under its $ReserveGB GB reserve." }
    if ($ExitCode -eq 0 -and -not $HasCounters) { return 'The workload finished but the counters didn''t come back, so it says nothing about memory.' }
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

# Whether one workload passes at one size on one host, against Control's runs on that host. Returns
# one line per criterion: Pass, Fail, or NotMeasured where the workload doesn't have that measure.
function Test-EvalCriteria {
    param([Parameter(Mandatory)][object[]]$Candidate, [Parameter(Mandatory)][object[]]$Control, [Parameter(Mandatory)][double]$MaxGB, $Thresholds = (Get-EvalThresholds))
    $t = $Thresholds

    # 1: the same results.
    $problems = @()
    $controlCodes = @($Control | ForEach-Object { [int]$_.exitCode } | Sort-Object -Unique)
    $badCodes = @($Candidate | Where-Object { $controlCodes -notcontains [int]$_.exitCode } | ForEach-Object { $_.exitCode })
    if ($badCodes) { $problems += "exit codes $($badCodes -join ', ') where Control had $($controlCodes -join ', ')" }
    $controlCounts = @(Get-RunList $Control 'testsTotal' | Sort-Object -Unique)
    if ($controlCounts) {
        $badCounts = @(Get-RunList $Candidate 'testsTotal' | Where-Object { $controlCounts -notcontains $_ })
        if ($badCounts) { $problems += "test counts $($badCounts -join ', ') where Control had $($controlCounts -join ', ')" }
        if (@(Get-RunList $Candidate 'testsTotal').Count -lt $Candidate.Count) { $problems += 'a run reported no test count' }
    }
    $controlFailed = @(Get-RunList $Control 'failedTests')
    $newFailed = @(Get-RunList $Candidate 'failedTests' | Where-Object { $controlFailed -notcontains $_ } | Sort-Object -Unique)
    if ($newFailed) { $problems += "tests failing only here: $($newFailed -join ', ')" }
    New-Check 1 'Same results' $(if ($problems) { 'Fail' } else { 'Pass' }) ($problems -join '; ')

    # 2: the same speech and clean audio. A run with no speech measure has nothing to compare.
    $withSpeech = @($Candidate | Where-Object { $_.speech })
    if (-not $withSpeech) { New-Check 2 'Same speech' 'NotMeasured' '' }
    else {
        $problems = @()
        $controlFails = @($Control | Where-Object { $_.speech } | ForEach-Object { $_.speech.failed } | Where-Object { $_ } | ForEach-Object { $_ })
        $newFails = @($withSpeech | ForEach-Object { $_.speech.failed } | Where-Object { $_ } | ForEach-Object { $_ } | Where-Object { $controlFails -notcontains $_ } | Sort-Object -Unique)
        if ($newFails) { $problems += "steps failing only here: $($newFails -join ', ')" }
        $misplaced = (@($withSpeech | ForEach-Object { [int]$_.speech.misplaced }) | Measure-Object -Sum).Sum
        if ($misplaced) { $problems += "$misplaced speech in the wrong step" }
        $gaps = (@($withSpeech | ForEach-Object { [int]$_.speech.audioGaps }) | Measure-Object -Sum).Sum
        if ($gaps) { $problems += "$gaps audio gaps" }
        New-Check 2 'Same speech' $(if ($problems) { 'Fail' } else { 'Pass' }) ($problems -join '; ')
    }

    # 3: no resource exhaustion and no new crashes.
    $problems = @()
    $exhausted = (@($Candidate | ForEach-Object { [int]$_.guestEvents.exhaustion }) | Measure-Object -Sum).Sum
    if ($exhausted) { $problems += "$exhausted resource exhaustion events" }
    $controlCrashes = @($Control | ForEach-Object { $_.guestEvents.crashes } | Where-Object { $_ } | ForEach-Object { $_ })
    $newCrashes = @($Candidate | ForEach-Object { $_.guestEvents.crashes } | Where-Object { $_ } | ForEach-Object { $_ } | Where-Object { $controlCrashes -notcontains $_ } | Sort-Object -Unique)
    if ($newCrashes) { $problems += "crashes only here: $($newCrashes -join ', ')" }
    New-Check 3 'No exhaustion or new crashes' $(if ($problems) { 'Fail' } else { 'Pass' }) ($problems -join '; ')

    # 4: hard time limits.
    $limits = @($Candidate | ForEach-Object { $_.limits } | Where-Object { $_ } | ForEach-Object { $_ })
    if (-not $limits) { New-Check 4 'Time limits met' 'NotMeasured' '' }
    else {
        $over = @($limits | Where-Object { [double]$_.actual -gt [double]$_.limit } | ForEach-Object { "$($_.name) took $($_.actual) s, limit $($_.limit) s" })
        New-Check 4 'Time limits met' $(if ($over) { 'Fail' } else { 'Pass' }) ($over -join '; ')
    }

    # 5: task time, against Control's median, or the slowest Control run if Control itself varies more.
    $controlTimes = @(Get-RunList $Control 'durationSeconds' | ForEach-Object { [double]$_ })
    $times = @(Get-RunList $Candidate 'durationSeconds' | ForEach-Object { [double]$_ })
    $controlMedian = Get-Median $controlTimes
    $allowed = [math]::Max($controlMedian * (1 + $t.TaskTimePercent / 100.0), ($controlTimes | Measure-Object -Maximum).Maximum)
    $median = Get-Median $times
    New-Check 5 'Task time' $(if ($median -gt $allowed) { 'Fail' } else { 'Pass' }) ("median {0:N1} s, allowed {1:N1} s, Control {2:N1} s" -f $median, $allowed, $controlMedian)

    # 6: key press to first speech.
    $withLatency = @($Candidate | Where-Object { $_.latency })
    $controlLatency = @($Control | Where-Object { $_.latency })
    if (-not $withLatency -or -not $controlLatency) { New-Check 6 'Speech latency' 'NotMeasured' '' }
    else {
        $med = Get-Median @($withLatency | ForEach-Object { [double]$_.latency.medianMs })
        $worst = Get-Median @($withLatency | ForEach-Object { [double]$_.latency.worstMs })
        $controlMed = Get-Median @($controlLatency | ForEach-Object { [double]$_.latency.medianMs })
        $controlWorst = Get-Median @($controlLatency | ForEach-Object { [double]$_.latency.worstMs })
        $allowMed = $controlMed + [math]::Max($controlMed * $t.LatencyMedianPercent / 100.0, $t.LatencyMedianMs)
        $allowWorst = $controlWorst + [math]::Max($controlWorst * $t.LatencyWorstPercent / 100.0, $t.LatencyWorstMs)
        New-Check 6 'Speech latency' $(if ($med -gt $allowMed -or $worst -gt $allowWorst) { 'Fail' } else { 'Pass' }) ("median {0:N0} ms (allowed {1:N0}), worst 5% {2:N0} ms (allowed {3:N0})" -f $med, $allowMed, $worst, $allowWorst)
    }

    # 7 to 9 need the guest's counters. A run without them can't pass on these, however it went.
    $measured = @($Candidate | Where-Object { $_.guest })
    if (-not $measured) {
        New-Check 7 'Memory available' 'NotMeasured' 'no counters came back'
        New-Check 8 'No sustained paging' 'NotMeasured' 'no counters came back'
        New-Check 9 'Commit and paging file' 'NotMeasured' 'no counters came back'
    } else {

    # 7: room left in the guest.
    $low = (@($measured | ForEach-Object { [double]$_.guest.LowAvailableSeconds }) | Measure-Object -Maximum).Maximum
    New-Check 7 'Memory available' $(if ($low -ge $t.SustainedSeconds) { 'Fail' } else { 'Pass' }) "under $($t.AvailableMB) MB for $low s at longest"

    # 8: no sustained paging, unless Control pages about as long, in which case it isn't about size.
    $paging = (@($measured | ForEach-Object { [double]$_.guest.PagingSeconds }) | Measure-Object -Maximum).Maximum
    $controlPaging = [double](@($Control | Where-Object { $_.guest } | ForEach-Object { [double]$_.guest.PagingSeconds }) | Measure-Object -Maximum).Maximum
    $pagingFail = $paging -ge $t.SustainedSeconds -and $paging -gt $controlPaging * $t.PagingControlFactor
    New-Check 8 'No sustained paging' $(if ($pagingFail) { 'Fail' } else { 'Pass' }) "$paging s at longest, Control $controlPaging s"

    # 9: commit and the paging file.
    $commit = (@($measured | ForEach-Object { [double]$_.guest.PeakCommitPercent }) | Measure-Object -Maximum).Maximum
    $grew = @($Candidate | Where-Object { $_.pagefileGrewMB -gt 0 }).Count
    $problems = @()
    if ($commit -ge $t.CommitPercent) { $problems += "commit reached $commit%" }
    if ($grew) { $problems += "paging file grew in $grew run(s)" }
    New-Check 9 'Commit and paging file' $(if ($problems) { 'Fail' } else { 'Pass' }) $(if ($problems) { $problems -join '; ' } else { "commit peaked at $commit%" })
    }

    # 10: soak only.
    $growth = @($measured | ForEach-Object { $_.guest.SoakGrowthGB } | Where-Object { $null -ne $_ })
    if (-not $growth) { New-Check 10 'No steady growth' 'NotMeasured' '' }
    else {
        $worstGrowth = ($growth | Measure-Object -Maximum).Maximum
        $limit = $MaxGB * $t.SoakGrowthPercent / 100.0
        New-Check 10 'No steady growth' $(if ($worstGrowth -gt $limit) { 'Fail' } else { 'Pass' }) "$worstGrowth GB over the last $($t.SoakWindowMinutes) min, allowed $limit GB"
    }
}

function Test-ChecksPass { param([object[]]$Checks) -not @($Checks | Where-Object { $_.Result -eq 'Fail' }) }

# Whether Control's runs are good enough to compare against at all. A size can't pass or fail
# against a Control that failed: exit codes and tests still get compared, but memory-related
# trouble in Control means the workload or the VM needs looking at first.
function Test-ControlUsable {
    param([object[]]$Control)
    if (@($Control).Count -lt 1) { return $false }
    -not @($Control | Where-Object { [int]$_.guestEvents.exhaustion -gt 0 })
}

# The smallest size that passes, testing downward as the plan says: each workload has a result per
# size it was run at, and a size no runs exist for is simply unknown.
function Get-WorkloadResult {
    param([Parameter(Mandatory)][object[]]$Runs, [Parameter(Mandatory)][string]$Workload, [Parameter(Mandatory)][string]$HostName, $Thresholds = (Get-EvalThresholds))
    $configs = Get-EvalConfigs
    $control = Select-ValidRun $Runs $Workload 'MemEval-12' $HostName
    $sizes = foreach ($c in $configs | Where-Object { $_.Role -ne 'Control' }) {
        $sizeRuns = @(Select-ValidRun $Runs $Workload $c.Name $HostName)
        if (-not $sizeRuns) { continue }
        if (-not (Test-ControlUsable $control)) {
            [pscustomobject]@{ Config = $c.Name; MaxGB = $c.MaxGB; Role = $c.Role; Runs = $sizeRuns.Count; Pass = $null; Checks = @() }
            continue
        }
        $checks = @(Test-EvalCriteria -Candidate $sizeRuns -Control $control -MaxGB $c.MaxGB -Thresholds $Thresholds)
        [pscustomobject]@{ Config = $c.Name; MaxGB = $c.MaxGB; Role = $c.Role; Runs = $sizeRuns.Count; Pass = (Test-ChecksPass $checks); Checks = $checks }
    }
    $passing = @($sizes | Where-Object { $_.Pass -and $_.Role -eq 'Candidate' } | Sort-Object MaxGB)
    [pscustomobject]@{
        Workload    = $Workload
        Host        = $HostName
        ControlRuns = $control.Count
        PeakGB      = if ($control) { (@($control | ForEach-Object { [double]$_.guest.PeakCommittedGB }) | Measure-Object -Maximum).Maximum } else { $null }
        Sizes       = @($sizes)
        SmallestGB  = if ($passing) { $passing[0].MaxGB } else { $null }
    }
}

# The plan's decision rules, as far as the workload results alone can take them. Tiers also need
# Phase 3's numbers, so that part comes back as a question to answer, not an answer.
function Get-EvalDecision {
    param([Parameter(Mandatory)][object[]]$Results, [string[]]$Hosts, [string[]]$LightWorkloads = @('W0', 'W1', 'W2', 'W3', 'W4', 'W5', 'W6'), [string[]]$HeavyWorkloads = @('W7', 'W8'))
    if (-not $Hosts) { $Hosts = @($Results | ForEach-Object { $_.Host } | Sort-Object -Unique) }
    $need = {
        param([string[]]$workloads, [string[]]$on)
        $wanted = @($workloads | ForEach-Object { $w = $_; $on | ForEach-Object { "$w|$_" } })
        $largest = $null
        foreach ($key in $wanted) {
            $r = @($Results | Where-Object { "$($_.Workload)|$($_.Host)" -eq $key })[0]
            # One unknown makes the answer unknown: the size has to fit every workload on every host.
            if (-not $r -or $null -eq $r.SmallestGB) { return $null }
            if ($null -eq $largest -or $r.SmallestGB -gt $largest) { $largest = $r.SmallestGB }
        }
        $largest
    }
    $default = & $need $LightWorkloads $Hosts
    $heavy = & $need $HeavyWorkloads $Hosts
    $perHost = @{}
    foreach ($h in $Hosts) { $perHost[$h] = & $need ($LightWorkloads + $HeavyWorkloads) @($h) }
    $differ = @($perHost.Values | Where-Object { $null -ne $_ } | Sort-Object -Unique).Count -gt 1
    $answer =
        if ($null -eq $default) { 'Not yet: some light workloads have no passing size on some host.' }
        elseif ($null -eq $heavy) { "Default maximum $default GB. W7 and W8 have no passing size yet." }
        elseif ($heavy -le $default) { "One size: $default GB maximum. W7 and W8 fit, so no tiers." }
        else { "Default $default GB; W7 or W8 need $heavy GB. Phase 3 decides: a heavy tier only if it lets a host run one more VM or lowers host peak memory by at least 4 GB. Otherwise one size at $heavy GB." }
    [pscustomobject]@{ DefaultGB = $default; HeavyGB = $heavy; PerHost = $perHost; HostsDiffer = $differ; Answer = $answer }
}

# ---- the report ---------------------------------------------------------------------------------

function Read-EvalRuns {
    param([string]$Root = (Get-EvalDataRoot))
    $dir = Join-Path $Root 'runs'
    if (-not (Test-Path $dir)) { return @() }
    foreach ($f in Get-ChildItem $dir -Recurse -Filter *.json) {
        $run = Get-Content $f.FullName -Raw | ConvertFrom-Json
        if ($run.schema -eq (Get-EvalSchema)) { $run }
    }
}

function Format-EvalReport {
    param([Parameter(Mandatory)][object[]]$Runs, $Thresholds = (Get-EvalThresholds))
    $hosts = @($Runs | ForEach-Object { $_.host } | Sort-Object -Unique)
    $workloads = @($Runs | ForEach-Object { $_.workload } | Sort-Object -Unique)
    $discarded = @($Runs | Where-Object { $_.discarded })
    $results = @(foreach ($h in $hosts) { foreach ($w in $workloads) { Get-WorkloadResult -Runs $Runs -Workload $w -HostName $h -Thresholds $Thresholds } })
    "# VM memory evaluation results"
    ""
    "$($Runs.Count) runs recorded, $($discarded.Count) thrown out."
    ""
    "**Decision so far:** $((Get-EvalDecision -Results $results -Hosts $hosts).Answer)"
    ""
    "## Smallest passing size"
    ""
    "| Host | Workload | Control runs | Peak committed at Control | Smallest passing maximum |"
    "|---|---|---|---|---|"
    foreach ($r in $results) {
        $peak = if ($null -ne $r.PeakGB) { "$($r.PeakGB) GB" } else { 'none' }
        $smallest = if ($null -ne $r.SmallestGB) { "$($r.SmallestGB) GB" } else { 'none yet' }
        "| $($r.Host) | $($r.Workload) | $($r.ControlRuns) | $peak | $smallest |"
    }
    ""
    "## Failures"
    ""
    $failures = @(foreach ($r in $results) { foreach ($s in $r.Sizes) { foreach ($c in $s.Checks | Where-Object { $_.Result -eq 'Fail' }) { "- $($r.Host), $($r.Workload), $($s.MaxGB) GB maximum: criterion $($c.Number), $($c.Name). $($c.Detail)" } } })
    if ($failures) { $failures } else { 'None.' }
    if ($discarded) {
        ""
        "## Runs thrown out"
        ""
        foreach ($d in $discarded) { "- $($d.host), $($d.workload), $($d.config), run $($d.repeat): $($d.discardReason)" }
    }
    ""
    "## Thresholds used"
    ""
    foreach ($p in $Thresholds.PSObject.Properties) { "- $($p.Name): $($p.Value)" }
}

Export-ModuleMember -Function Get-EvalConfigs, Get-EvalThresholds, Get-EvalDataRoot, Get-EvalSchema, ConvertFrom-CounterCsv, Get-LongestRunSeconds, Get-Slope, Get-Median,
    Get-CounterSummary, Get-RunDiscardReason, Select-ValidRun, Test-EvalCriteria, Test-ChecksPass, Test-ControlUsable, Get-WorkloadResult, Get-EvalDecision, Read-EvalRuns, Format-EvalReport
