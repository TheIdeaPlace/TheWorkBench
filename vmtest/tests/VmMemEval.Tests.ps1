# Pester 3.4 tests for the memory evaluation's rules. They need no VM: everything under test works on
# numbers a run recorded. Run: Invoke-Pester vmtest\tests
$root = Split-Path $PSScriptRoot
Import-Module (Join-Path $root 'eval\VmMemEval.psm1') -Force

# A run with every measure healthy; a test changes what it's about.
function New-Run {
    param([string]$Config = 'MemEval-4', [string]$Workload = 'W7', [string]$HostName = 'PC1', [double]$Duration = 100, [int]$ExitCode = 0,
        $TestsTotal = $null, [string[]]$FailedTests = @(), $Speech = $null, $Latency = $null, $Limits = @(), [int]$Exhaustion = 0, [string[]]$Crashes = @(),
        [double]$LowSeconds = 0, [double]$PagingSeconds = 0, [double]$CommitPercent = 50, $SoakGrowthGB = $null, [int]$PagefileGrewMB = 0, [double]$PeakGB = 3, [switch]$Discarded)
    [pscustomobject]@{
        host = $HostName; workload = $Workload; config = $Config; repeat = 1; durationSeconds = $Duration; exitCode = $ExitCode
        testsTotal = $TestsTotal; failedTests = $FailedTests; speech = $Speech; latency = $Latency; limits = $Limits
        guestEvents = [pscustomobject]@{ exhaustion = $Exhaustion; crashes = $Crashes }
        guest = [pscustomobject]@{ LowAvailableSeconds = $LowSeconds; PagingSeconds = $PagingSeconds; PeakCommitPercent = $CommitPercent; SoakGrowthGB = $SoakGrowthGB; PeakCommittedGB = $PeakGB }
        pagefileGrewMB = $PagefileGrewMB; discarded = [bool]$Discarded; discardReason = ''
    }
}
function Check($checks, [int]$number) { @($checks | Where-Object Number -eq $number)[0] }
function Run-Criteria($Candidate, $Control, [double]$MaxGB = 4) { Test-EvalCriteria -Candidate @($Candidate) -Control @($Control) -MaxGB $MaxGB }

Describe 'the sizes under test' {
    It 'has one Control, and it offers the most memory' {
        $configs = Get-EvalConfigs
        $control = @($configs | Where-Object Role -eq 'Control')
        $control.Count | Should Be 1
        $control[0].MaxGB | Should Be (($configs | Measure-Object MaxGB -Maximum).Maximum)
    }
    It 'names each VM for its maximum, so a result can never be misread' {
        foreach ($c in Get-EvalConfigs) { $c.Name | Should Be "MemEval-$($c.MaxGB)" }
    }
    It 'never starts or floors a VM above its maximum' {
        foreach ($c in Get-EvalConfigs) {
            ($c.StartupGB -le $c.MaxGB) | Should Be $true
            ($c.MinGB -le $c.StartupGB) | Should Be $true
        }
    }
}

Describe 'reading the guest counters' {
    $csv = @'
"(PDH-CSV 4.0) (Pacific Standard Time)(480)","\\MEMEVAL-4\Memory\Available MBytes","\\MEMEVAL-4\Memory\Committed Bytes","\\MEMEVAL-4\Memory\Commit Limit","\\MEMEVAL-4\Memory\Pages Input/sec","\\MEMEVAL-4\Paging File(_Total)\% Usage"
"11/01/2026 17:00:00.123","2048","2147483648","4294967296"," ","1.5"
"11/01/2026 17:00:05.123","1024.5","3221225472","4294967296","12.5","2"
'@
    It 'finds each column whatever the computer is called' {
        $rows = @(ConvertFrom-CounterCsv -Csv $csv)
        $rows.Count | Should Be 2
        $rows[1].AvailableMB | Should Be 1024.5
        $rows[1].Committed | Should Be 3221225472
        $rows[1].CommitLimit | Should Be 4294967296
        $rows[1].PagesInput | Should Be 12.5
        $rows[1].PagefilePct | Should Be 2
    }
    It 'reads the time the same way on any PC' {
        $rows = @(ConvertFrom-CounterCsv -Csv $csv)
        $rows[0].Time.Month | Should Be 11
        $rows[0].Time.Day | Should Be 1
    }
    It 'treats a blank sample as no value, not zero' {
        $null -eq (@(ConvertFrom-CounterCsv -Csv $csv)[0].PagesInput) | Should Be $true
    }
    It 'copes with a log that has no samples' {
        @(ConvertFrom-CounterCsv -Csv '').Count | Should Be 0
    }
}

Describe 'summing up a run' {
    function Rows([int]$count, [double]$available = 4000, [double]$pages = 0, [double]$committed = 2GB, [double]$limit = 8GB) {
        1..$count | ForEach-Object { [pscustomobject]@{ AvailableMB = $available; PagesInput = $pages; Committed = $committed; CommitLimit = $limit } }
    }
    It 'measures the longest stretch, not the total' {
        Get-LongestRunSeconds -Values @(1, 1, 0, 1, 1, 1, 0, 1) -Test { param($v) $v -eq 1 } -SampleSeconds 5 | Should Be 15
    }
    It 'skips a missing sample rather than ending the stretch or counting it' {
        Get-LongestRunSeconds -Values @(1, $null, 1) -Test { param($v) $v -eq 1 } -SampleSeconds 5 | Should Be 10
    }
    It 'gives a stretch of nothing as 0' {
        Get-LongestRunSeconds -Values @() -Test { param($v) $true } | Should Be 0
    }
    It 'counts low memory below a fixed amount, whatever the VM''s maximum' {
        foreach ($max in 4, 12) {
            (Get-CounterSummary -Rows (Rows 12 -available 120) -MaxGB $max).LowAvailableSeconds | Should Be 0
            (Get-CounterSummary -Rows (Rows 12 -available 90) -MaxGB $max).LowAvailableSeconds | Should Be 60
        }
    }
    It 'counts paging from the first sample, since the counters start after the settling time' {
        (Get-CounterSummary -Rows (Rows 10 -pages 500) -MaxGB 4).PagingSeconds | Should Be 50
    }
    It 'gives no figure, not a healthy 0, for a measure with no values' {
        $blank = 1..50 | ForEach-Object { [pscustomobject]@{ AvailableMB = $null; PagesInput = $null; Committed = $null; CommitLimit = $null } }
        $s = Get-CounterSummary -Rows $blank -MaxGB 4
        $null -eq $s.LowAvailableSeconds | Should Be $true
        $null -eq $s.PagingSeconds | Should Be $true
        $null -eq $s.PeakCommitPercent | Should Be $true
    }
    It 'takes the sample spacing from the log''s own times' {
        $t = [datetime]'2026-11-01T12:00:00'
        $slow = 0..5 | ForEach-Object { [pscustomobject]@{ Time = $t.AddSeconds($_ * 10); AvailableMB = 50; PagesInput = 0; Committed = 1GB; CommitLimit = 8GB } }
        $s = Get-CounterSummary -Rows $slow -MaxGB 4
        $s.CoveredSeconds | Should Be 50
        $s.LowAvailableSeconds | Should Be 60
    }
    It 'gives peak commit as a percentage of the limit' {
        (Get-CounterSummary -Rows (Rows 3 -committed 6GB -limit 8GB) -MaxGB 4).PeakCommitPercent | Should Be 75
    }
    It 'gives no soak figure for a run shorter than the soak window' {
        $null -eq (Get-CounterSummary -Rows (Rows 20) -MaxGB 4).SoakGrowthGB | Should Be $true
    }
    It 'sees steady growth over the soak window' {
        $grow = 1..360 | ForEach-Object { [pscustomobject]@{ AvailableMB = 4000; PagesInput = 0; Committed = 2GB + $_ * 3MB; CommitLimit = 8GB } }
        (Get-CounterSummary -Rows $grow -MaxGB 4).SoakGrowthGB | Should BeGreaterThan 1
    }
    It 'sees none when memory stays flat' {
        (Get-CounterSummary -Rows (Rows 360) -MaxGB 4).SoakGrowthGB | Should Be 0
    }
    It 'survives a run with no samples at all' {
        (Get-CounterSummary -Rows @() -MaxGB 4).Samples | Should Be 0
    }
}

Describe 'whether counters speak for the whole workload' {
    $good = [pscustomobject]@{ LowAvailableSeconds = 0; PagingSeconds = 0; PeakCommitPercent = 50; CoveredSeconds = 600 }
    It 'accepts a log as long as the workload' { Test-CountersComplete $good 600 | Should Be $true }
    It 'refuses a log that stopped early' { Test-CountersComplete $good 1800 | Should Be $false }
    It 'refuses a log of blanks' {
        Test-CountersComplete ([pscustomobject]@{ LowAvailableSeconds = $null; PagingSeconds = $null; PeakCommitPercent = $null; CoveredSeconds = 600 }) 600 | Should Be $false
    }
    It 'refuses no summary at all' { Test-CountersComplete $null 600 | Should Be $false }
}

Describe 'the criteria' {
    $control = 1..3 | ForEach-Object { New-Run -Config 'MemEval-12' }
    It 'passes a healthy run' {
        Test-ChecksPass (Run-Criteria (New-Run) $control) | Should Be $true
    }
    It 'says NotMeasured, not Pass, for what a workload has no measure of' {
        $checks = Run-Criteria (New-Run) $control
        (Check $checks 2).Result | Should Be 'NotMeasured'
        (Check $checks 4).Result | Should Be 'NotMeasured'
        (Check $checks 6).Result | Should Be 'NotMeasured'
        (Check $checks 10).Result | Should Be 'NotMeasured'
    }

    Context 'missing data never passes' {
        It 'doesn''t pass when Control measured speech and this lost it' {
            $withSpeech = New-Run -Config 'MemEval-12' -Speech ([pscustomobject]@{ failed = @(); misplaced = 0; audioGaps = 0 })
            $checks = Run-Criteria (New-Run) $withSpeech
            (Check $checks 2).Result | Should Be 'Incomplete'
            Test-ChecksPass $checks | Should Be $false
        }
        It 'doesn''t pass when only some runs have speech' {
            $s = [pscustomobject]@{ failed = @(); misplaced = 0; audioGaps = 0 }
            (Check (Run-Criteria @((New-Run -Speech $s), (New-Run)) (New-Run -Config 'MemEval-12' -Speech $s)) 2).Result | Should Be 'Incomplete'
        }
        It 'doesn''t pass when Control had latency or limits and this lost them' {
            (Check (Run-Criteria (New-Run) (New-Run -Config 'MemEval-12' -Latency ([pscustomobject]@{ medianMs = 1; worstMs = 1 }))) 6).Result | Should Be 'Incomplete'
            (Check (Run-Criteria (New-Run) (New-Run -Config 'MemEval-12' -Limits @([pscustomobject]@{ name = 'x'; limit = 30; actual = 1 }))) 4).Result | Should Be 'Incomplete'
        }
        It 'doesn''t pass when one run in three has no counters' {
            $lost = New-Run
            $lost.guest = $null
            $checks = Run-Criteria @((New-Run), (New-Run), $lost) $control
            foreach ($n in 7, 8, 9) { (Check $checks $n).Result | Should Be 'Incomplete' }
            Test-ChecksPass $checks | Should Be $false
        }
        It 'doesn''t pass when no events came back' {
            $lost = New-Run
            $lost.guestEvents = $null
            (Check (Run-Criteria $lost $control) 3).Result | Should Be 'Incomplete'
        }
        It 'still fails a bad reading, even with another run''s counters missing' {
            $lost = New-Run
            $lost.guest = $null
            (Check (Run-Criteria @((New-Run -LowSeconds 300), $lost) $control) 7).Result | Should Be 'Fail'
        }
        It 'doesn''t pass when a soak''s counters stopped short of Control''s' {
            (Check (Run-Criteria (New-Run) (New-Run -Config 'MemEval-12' -SoakGrowthGB 0)) 10).Result | Should Be 'Incomplete'
        }
        It 'treats a soak growth of exactly 0 as measured' {
            (Check (Run-Criteria (New-Run -SoakGrowthGB 0) (New-Run -Config 'MemEval-12' -SoakGrowthGB 0)) 10).Result | Should Be 'Pass'
        }
        It 'treats no duration as Incomplete, not a pass' {
            $none = New-Run
            $none.durationSeconds = $null
            (Check (Run-Criteria $none $control) 5).Result | Should Be 'Incomplete'
        }
    }

    Context '1: same results' {
        It 'fails a run of 0 tests, the extreme of the 3,920 of 4,920 run' {
            (Check (Run-Criteria (New-Run -TestsTotal 0) (New-Run -Config 'MemEval-12' -TestsTotal 4920)) 1).Result | Should Be 'Fail'
        }
        It 'fails exit 0 where Control always exited 1' {
            (Check (Run-Criteria (New-Run -ExitCode 0) (New-Run -Config 'MemEval-12' -ExitCode 1)) 1).Result | Should Be 'Fail'
        }
        It 'never lets a died run match a Control that died' {
            (Check (Run-Criteria (New-Run -ExitCode -1) (New-Run -Config 'MemEval-12' -ExitCode -1)) 1).Result | Should Be 'Fail'
        }
        It 'fails a run with no exit code' {
            $none = New-Run
            $none.exitCode = $null
            (Check (Run-Criteria $none $control) 1).Result | Should Be 'Fail'
        }
        It 'fails an exit code Control never had' { (Check (Run-Criteria (New-Run -ExitCode 1) $control) 1).Result | Should Be 'Fail' }
        It 'fails a workload that died outright' { Test-ChecksPass (Run-Criteria (New-Run -ExitCode -1) $control) | Should Be $false }
        It 'passes a failing exit code Control had too' {
            (Check (Run-Criteria (New-Run -ExitCode 1) (New-Run -Config 'MemEval-12' -ExitCode 1)) 1).Result | Should Be 'Pass'
        }
        It 'fails a different test count, like the 3,920 of 4,920 run' {
            (Check (Run-Criteria (New-Run -TestsTotal 3920) (New-Run -Config 'MemEval-12' -TestsTotal 4920)) 1).Result | Should Be 'Fail'
        }
        It 'fails a run that reports no count when Control did' {
            (Check (Run-Criteria (New-Run) (New-Run -Config 'MemEval-12' -TestsTotal 4920)) 1).Result | Should Be 'Fail'
        }
        It 'fails a test that fails only here' {
            (Check (Run-Criteria (New-Run -FailedTests 'A', 'B') (New-Run -Config 'MemEval-12' -FailedTests 'A')) 1).Detail | Should Match 'failing only here: B'
        }
        It 'passes a test that fails in Control too' {
            (Check (Run-Criteria (New-Run -FailedTests 'A') (New-Run -Config 'MemEval-12' -FailedTests 'A')) 1).Result | Should Be 'Pass'
        }
    }

    Context '2: same speech' {
        $speech = [pscustomobject]@{ failed = @(); misplaced = 0; audioGaps = 0 }
        $controlSpeech = New-Run -Config 'MemEval-12' -Speech ([pscustomobject]@{ failed = @('step 3'); misplaced = 0; audioGaps = 0 })
        It 'passes matching speech' { (Check (Run-Criteria (New-Run -Speech $speech) $controlSpeech) 2).Result | Should Be 'Pass' }
        It 'fails a step failing only here' {
            (Check (Run-Criteria (New-Run -Speech ([pscustomobject]@{ failed = @('step 9'); misplaced = 0; audioGaps = 0 })) $controlSpeech) 2).Result | Should Be 'Fail'
        }
        It 'passes a step failing in Control too' {
            (Check (Run-Criteria (New-Run -Speech ([pscustomobject]@{ failed = @('step 3'); misplaced = 0; audioGaps = 0 })) $controlSpeech) 2).Result | Should Be 'Pass'
        }
        It 'fails speech in the wrong step' {
            (Check (Run-Criteria (New-Run -Speech ([pscustomobject]@{ failed = @(); misplaced = 1; audioGaps = 0 })) $controlSpeech) 2).Result | Should Be 'Fail'
        }
        It 'fails an audio gap' {
            (Check (Run-Criteria (New-Run -Speech ([pscustomobject]@{ failed = @(); misplaced = 0; audioGaps = 2 })) $controlSpeech) 2).Result | Should Be 'Fail'
        }
    }

    Context '3: exhaustion and crashes' {
        It 'fails any resource exhaustion' { (Check (Run-Criteria (New-Run -Exhaustion 1) $control) 3).Result | Should Be 'Fail' }
        It 'fails a crash only here' { (Check (Run-Criteria (New-Run -Crashes 'app.exe') $control) 3).Result | Should Be 'Fail' }
        It 'passes a crash Control has too' {
            (Check (Run-Criteria (New-Run -Crashes 'app.exe') (New-Run -Config 'MemEval-12' -Crashes 'app.exe')) 3).Result | Should Be 'Pass'
        }
    }

    Context '4: time limits' {
        It 'fails over a limit' {
            (Check (Run-Criteria (New-Run -Limits @([pscustomobject]@{ name = 'Velopack'; limit = 30; actual = 31 })) $control) 4).Result | Should Be 'Fail'
        }
        It 'passes exactly at a limit' {
            (Check (Run-Criteria (New-Run -Limits @([pscustomobject]@{ name = 'Velopack'; limit = 30; actual = 30 })) $control) 4).Result | Should Be 'Pass'
        }
    }

    Context '5: task time' {
        It 'passes 10% over' { (Check (Run-Criteria (New-Run -Duration 110) $control) 5).Result | Should Be 'Pass' }
        It 'fails more than 10% over' { (Check (Run-Criteria (New-Run -Duration 111) $control) 5).Result | Should Be 'Fail' }
        It 'allows Control''s own spread when that is wider' {
            $spread = (New-Run -Config 'MemEval-12' -Duration 100), (New-Run -Config 'MemEval-12' -Duration 100), (New-Run -Config 'MemEval-12' -Duration 130)
            (Check (Run-Criteria (New-Run -Duration 125) $spread) 5).Result | Should Be 'Pass'
        }
        It 'judges the median, so one slow run doesn''t decide it' {
            $runs = (New-Run -Duration 100), (New-Run -Duration 100), (New-Run -Duration 500)
            (Check (Run-Criteria $runs $control) 5).Result | Should Be 'Pass'
        }
        It 'fails when most runs are slow' {
            $runs = (New-Run -Duration 100), (New-Run -Duration 500), (New-Run -Duration 500)
            (Check (Run-Criteria $runs $control) 5).Result | Should Be 'Fail'
        }
    }

    Context '6: speech latency' {
        $base = New-Run -Config 'MemEval-12' -Latency ([pscustomobject]@{ medianMs = 500; worstMs = 900 })
        function L([double]$median, [double]$worst) { New-Run -Latency ([pscustomobject]@{ medianMs = $median; worstMs = $worst }) }
        It 'passes within the larger of 10% or 100 ms' { (Check (Run-Criteria (L 600 900) $base) 6).Result | Should Be 'Pass' }
        It 'fails beyond it' { (Check (Run-Criteria (L 601 900) $base) 6).Result | Should Be 'Fail' }
        It 'uses the percentage when it is larger' {
            $slow = New-Run -Config 'MemEval-12' -Latency ([pscustomobject]@{ medianMs = 2000; worstMs = 3000 })
            (Check (Run-Criteria (L 2200 3000) $slow) 6).Result | Should Be 'Pass'
            (Check (Run-Criteria (L 2201 3000) $slow) 6).Result | Should Be 'Fail'
        }
        It 'fails a worse worst 5% even with a good median' { (Check (Run-Criteria (L 500 1101) $base) 6).Result | Should Be 'Fail' }
        It 'says NotMeasured when Control has no latency to compare with' { (Check (Run-Criteria (L 500 900) $control) 6).Result | Should Be 'NotMeasured' }
    }

    Context '7 to 9: room to spare' {
        It 'ignores a brief dip' { (Check (Run-Criteria (New-Run -LowSeconds 25) $control) 7).Result | Should Be 'Pass' }
        It 'fails a sustained low' { (Check (Run-Criteria (New-Run -LowSeconds 30) $control) 7).Result | Should Be 'Fail' }
        It 'fails sustained paging' { (Check (Run-Criteria (New-Run -PagingSeconds 30) $control) 8).Result | Should Be 'Fail' }
        It 'passes paging within twice Control''s, which is its own run-to-run noise' {
            (Check (Run-Criteria (New-Run -PagingSeconds 120) (New-Run -Config 'MemEval-12' -PagingSeconds 60)) 8).Result | Should Be 'Pass'
        }
        It 'fails paging beyond that' {
            (Check (Run-Criteria (New-Run -PagingSeconds 125) (New-Run -Config 'MemEval-12' -PagingSeconds 60)) 8).Result | Should Be 'Fail'
        }
        It 'fails high commit' { (Check (Run-Criteria (New-Run -CommitPercent 85) $control) 9).Result | Should Be 'Fail' }
        It 'fails a paging file that grew' { (Check (Run-Criteria (New-Run -PagefileGrewMB 512) $control) 9).Result | Should Be 'Fail' }
    }

    Context '10: soak' {
        It 'passes flat memory' { (Check (Run-Criteria (New-Run -SoakGrowthGB 0.01) (New-Run -Config 'MemEval-12' -SoakGrowthGB 0)) 10).Result | Should Be 'Pass' }
        It 'fails steady growth' { (Check (Run-Criteria (New-Run -SoakGrowthGB 1) (New-Run -Config 'MemEval-12' -SoakGrowthGB 0)) 10).Result | Should Be 'Fail' }
    }

    It 'catches a starved run, which is what the too-small VM has to prove' {
        $starved = New-Run -Config 'MemEval-2' -Duration 400 -ExitCode -1 -Exhaustion 2 -LowSeconds 300 -PagingSeconds 300 -CommitPercent 98
        $failed = @(Run-Criteria $starved $control 2 | Where-Object Result -eq 'Fail' | ForEach-Object Number)
        foreach ($n in 1, 3, 5, 7, 8, 9) { $failed -contains $n | Should Be $true }
    }
}

Describe 'a run with no counters' {
    $control = 1..3 | ForEach-Object { New-Run -Config 'MemEval-12' }
    It 'can''t pass on memory it never measured' {
        $empty = New-Run
        $empty.guest = $null
        $checks = Run-Criteria $empty $control
        foreach ($n in 7, 8, 9) { (Check $checks $n).Result | Should Be 'Incomplete' }
        Test-ChecksPass $checks | Should Be $false
    }
    It 'still fails on what it did measure' {
        $empty = New-Run -ExitCode -1
        $empty.guest = $null
        $empty.guestEvents = $null
        (Check (Run-Criteria $empty $control) 1).Result | Should Be 'Fail'
    }
    It 'compares with a Control that has no counters without an error' {
        $bare = New-Run -Config 'MemEval-12'
        $bare.guest = $null
        (Check (Run-Criteria (New-Run -PagingSeconds 60) $bare) 8).Result | Should Be 'Fail'
    }
}

Describe 'whether a run counts' {
    function Why { param([bool]$Started = $true, [int]$ExitCode = 0, [bool]$HasCounters = $true, [string[]]$OtherVMs = @(), $HostMin = 20000, [bool]$FirstOnVM = $false, [bool]$HasHostRows = $true)
        Get-RunDiscardReason -Started $Started -ExitCode $ExitCode -HasCounters $HasCounters -OtherVMs $OtherVMs -HostMinAvailableMB $HostMin -HasHostRows $HasHostRows -FirstOnVM $FirstOnVM -StartError 'boom' }
    It 'counts a clean run' { Why | Should Be '' }
    It 'throws out a run that never started' { Why -Started $false | Should Match 'boom' }
    It 'throws out a fine workload whose counters didn''t come back' { Why -HasCounters $false | Should Match 'counters' }
    It 'keeps a failed workload with no counters, because that may be the shortage itself' { Why -ExitCode -1 -HasCounters $false | Should Be '' }
    It 'throws out a run while another VM was running' { Why -OtherVMs 'ClaudeTesting' | Should Match 'ClaudeTesting' }
    It 'throws out a run when the PC went below its reserve' { Why -HostMin 3000 | Should Match 'reserve' }
    It 'throws out a run with no host rows, since nothing shows another VM wasn''t running' { Why -HasHostRows $false | Should Match 'own numbers' }
    It 'counts a failed first run, because a failure always counts' { Why -ExitCode 1 -FirstOnVM $true | Should Be '' }
    It 'throws out the first run on a VM as warm-up' { Why -FirstOnVM $true | Should Match 'warm-up' }
}

Describe 'choosing runs' {
    It 'leaves out thrown-out runs, and other workloads, sizes and hosts' {
        $runs = (New-Run), (New-Run -Discarded), (New-Run -Workload 'W1'), (New-Run -Config 'MemEval-6'), (New-Run -HostName 'PC2')
        @(Select-ValidRun $runs 'W7' 'MemEval-4' 'PC1').Count | Should Be 1
    }
    It 'gives none, not an error, when nothing matches' {
        @(Select-ValidRun @((New-Run)) 'W9' 'MemEval-4' 'PC1').Count | Should Be 0
    }
}

Describe 'the smallest passing size' {
    $control = 1..3 | ForEach-Object { New-Run -Config 'MemEval-12' }
    function Three([string]$config, [int]$exit = 0) { 1..3 | ForEach-Object { New-Run -Config $config -ExitCode $exit } }
    It 'is the smallest candidate that passes' {
        $r = Get-WorkloadResult -Runs (@($control) + @(Three 'MemEval-8') + @(Three 'MemEval-6') + @(Three 'MemEval-4' 1)) -Workload 'W7' -HostName 'PC1'
        $r.SmallestGB | Should Be 6
    }
    It 'stops at the first size to pass, without the larger ones having run' {
        (Get-WorkloadResult -Runs (@($control) + @(Three 'MemEval-4')) -Workload 'W7' -HostName 'PC1').SmallestGB | Should Be 4
    }
    It 'doesn''t believe a smaller size that passed after a larger one failed' {
        $r = Get-WorkloadResult -Runs (@($control) + @(Three 'MemEval-8' 1) + @(Three 'MemEval-4')) -Workload 'W7' -HostName 'PC1'
        $null -eq $r.SmallestGB | Should Be $true
        $r.OutOfOrder | Should Be $true
    }
    It 'takes the size above the failure when there is one' {
        $r = Get-WorkloadResult -Runs (@($control) + @(Three 'MemEval-8') + @(Three 'MemEval-6' 1) + @(Three 'MemEval-4')) -Workload 'W7' -HostName 'PC1'
        $r.SmallestGB | Should Be 8
    }
    It 'doesn''t judge a size with too few runs' {
        $r = Get-WorkloadResult -Runs (@($control) + (New-Run -Config 'MemEval-4')) -Workload 'W7' -HostName 'PC1'
        $null -eq $r.SmallestGB | Should Be $true
        $r.Sizes[0].Reason | Should Match '1 of the 3'
    }
    It 'doesn''t judge against too few Control runs' {
        $r = Get-WorkloadResult -Runs (@((New-Run -Config 'MemEval-12')) + @(Three 'MemEval-4')) -Workload 'W7' -HostName 'PC1'
        $null -eq $r.SmallestGB | Should Be $true
        $r.ControlRuns | Should Be 1
    }
    It 'accepts one run each for the workloads the plan runs once' {
        $r = Get-WorkloadResult -Runs @((New-Run -Workload 'W9' -Config 'MemEval-12'), (New-Run -Workload 'W9')) -Workload 'W9' -HostName 'PC1'
        $r.SmallestGB | Should Be 4
    }
    It 'judges nothing when every Control run died, whatever the size did' {
        $dead = 1..3 | ForEach-Object { $x = New-Run -Config 'MemEval-12' -ExitCode -1; $x.guest = $null; $x }
        $r = Get-WorkloadResult -Runs (@($dead) + @(Three 'MemEval-4' -1)) -Workload 'W7' -HostName 'PC1'
        $null -eq $r.SmallestGB | Should Be $true
        $r.ControlProblem | Should Match 'died'
        $null -eq $r.PeakGB | Should Be $true
    }
    It 'judges nothing when a Control run has no counters' {
        $bare = New-Run -Config 'MemEval-12'
        $bare.guest = $null
        (Get-WorkloadResult -Runs (@($control) + $bare + @(Three 'MemEval-4')) -Workload 'W7' -HostName 'PC1').ControlProblem | Should Match 'no counters'
    }
    It 'doesn''t compare runs with different settings' {
        $short = Three 'MemEval-4'
        $short | ForEach-Object { $_ | Add-Member setup 'settle=60;Minutes=3' }
        $r = Get-WorkloadResult -Runs (@($control) + @($short)) -Workload 'W7' -HostName 'PC1'
        $null -eq $r.SmallestGB | Should Be $true
        $r.Sizes[0].Reason | Should Match 'different settings'
    }
    It 'trusts nothing for a workload where the too-small VM passed' {
        $r = Get-WorkloadResult -Runs (@($control) + @(Three 'MemEval-4') + @(Three 'MemEval-2')) -Workload 'W7' -HostName 'PC1'
        $null -eq $r.SmallestGB | Should Be $true
        $r.CriteriaBlind | Should Be $true
    }
    It 'records when the too-small VM failed, which proves the criteria work' {
        $r = Get-WorkloadResult -Runs (@($control) + @(Three 'MemEval-4') + @(Three 'MemEval-2' 1)) -Workload 'W7' -HostName 'PC1'
        $r.ShortageCaught | Should Be $true
        $r.SmallestGB | Should Be 4
    }
    It 'is unknown when no candidate has been run' {
        $null -eq (Get-WorkloadResult -Runs @($control) -Workload 'W7' -HostName 'PC1').SmallestGB | Should Be $true
    }
    It 'never counts the too-small VM as a size to use, even if it passes' {
        $r = Get-WorkloadResult -Runs (@($control) + @(Three 'MemEval-2')) -Workload 'W7' -HostName 'PC1'
        $null -eq $r.SmallestGB | Should Be $true
    }
    It 'judges nothing when Control has no runs' {
        $r = Get-WorkloadResult -Runs @(Three 'MemEval-4') -Workload 'W7' -HostName 'PC1'
        $null -eq $r.SmallestGB | Should Be $true
        $null -eq $r.Sizes[0].Pass | Should Be $true
    }
    It 'judges nothing when Control itself ran out of memory' {
        $bad = 1..3 | ForEach-Object { New-Run -Config 'MemEval-12' -Exhaustion 1 }
        $null -eq (Get-WorkloadResult -Runs (@($bad) + @(Three 'MemEval-4')) -Workload 'W7' -HostName 'PC1').SmallestGB | Should Be $true
    }
    It 'compares only with Control on the same host' {
        $slowOther = 1..3 | ForEach-Object { New-Run -Config 'MemEval-12' -HostName 'PC2' -Duration 1000 }
        $r = Get-WorkloadResult -Runs (@($control) + @($slowOther) + @(1..3 | ForEach-Object { New-Run -Duration 500 })) -Workload 'W7' -HostName 'PC1'
        $null -eq $r.SmallestGB | Should Be $true
    }
}

Describe 'the decision' {
    function New-Result([string]$workload, [string]$hostName, $smallest) { [pscustomobject]@{ Workload = $workload; Host = $hostName; SmallestGB = $smallest; ControlRuns = 3; Sizes = @() } }
    function All([double]$light, [double]$heavy, [string[]]$hosts = @('PC1', 'PC2')) {
        foreach ($h in $hosts) {
            foreach ($w in 'W0', 'W1', 'W2', 'W3', 'W4', 'W5', 'W6') { New-Result $w $h $light }
            foreach ($w in 'W7', 'W8') { New-Result $w $h $heavy }
        }
    }
    It 'is one size when the heavy work fits the default' {
        $d = Get-EvalDecision -Hosts PC1, PC2 -Results @(All 4 4)
        $d.DefaultGB | Should Be 4
        $d.Answer | Should Match 'One size: 4 GB'
    }
    It 'takes the largest any light workload needs on any host' {
        $results = @(All 4 4) | ForEach-Object { if ($_.Workload -eq 'W4' -and $_.Host -eq 'PC2') { $_.SmallestGB = 6 }; $_ }
        (Get-EvalDecision -Hosts PC1, PC2 -Results $results).DefaultGB | Should Be 6
    }
    It 'asks Phase 3 about tiers rather than assuming them, when heavy work needs more' {
        $d = Get-EvalDecision -Hosts PC1, PC2 -Results @(All 4 8)
        $d.Answer | Should Match 'Phase 3 decides'
        $d.HeavyGB | Should Be 8
    }
    It 'gives no default while any light workload has no passing size' {
        $results = @(All 4 4) | ForEach-Object { if ($_.Workload -eq 'W4' -and $_.Host -eq 'PC1') { $_.SmallestGB = $null }; $_ }
        $null -eq (Get-EvalDecision -Hosts PC1, PC2 -Results $results).DefaultGB | Should Be $true
    }
    It 'gives no default while a workload hasn''t been run on a host' {
        $results = @(All 4 4) | Where-Object { -not ($_.Workload -eq 'W4' -and $_.Host -eq 'PC1') }
        $null -eq (Get-EvalDecision -Results $results -Hosts 'PC1', 'PC2').DefaultGB | Should Be $true
    }
    It 'says not yet, and names the host, when one host has no results' {
        $d = Get-EvalDecision -Hosts PC1, PC2 -Results @(All 4 4 'PC1')
        $d.Answer | Should Match 'Not yet.*PC2'
        $null -eq $d.DefaultGB | Should Be $true
    }
    It 'says the size isn''t final until the soak has passed there' {
        (Get-EvalDecision -Hosts PC1, PC2 -Results @(All 4 4)).Notes -join ' ' | Should Match 'soak hasn''t passed'
    }
    It 'says when no run has shown the criteria catching a shortage' {
        (Get-EvalDecision -Hosts PC1, PC2 -Results @(All 4 4)).Notes -join ' ' | Should Match 'catching a real shortage'
    }
    It 'notices when the hosts need different sizes' {
        $results = @(All 4 4 'PC1') + @(All 4 8 'PC2')
        (Get-EvalDecision -Hosts PC1, PC2 -Results $results).HostsDiffer | Should Be $true
    }
    It 'doesn''t when they need the same' {
        (Get-EvalDecision -Hosts PC1, PC2 -Results @(All 6 6)).HostsDiffer | Should Be $false
    }
}

Describe 'calling vmtest for the evaluation' {
    $tool = Join-Path $TestDrive 'fake-vmtest.ps1'
    Set-Content $tool @'
"pool=$env:VMTEST_POOL host=[$env:VMTEST_HOST]"
"args=$($args -join ' ')"
exit 7
'@
    It 'passes the pool and clears the host for the call, and returns the output and exit code' {
        $r = Invoke-EvalVmTest -ToolPath $tool -Pool 'A', 'B' -Arguments @('begin', '-VM', 'B')
        $r.ExitCode | Should Be 7
        $r.Output[0] | Should Be 'pool=A,B host=[]'
        $r.Output[1] | Should Be 'args=begin -VM B'
    }
    It 'leaves the caller''s settings as they were, so a later begin there can''t get an eval VM' {
        $savedPool = $env:VMTEST_POOL; $savedHost = $env:VMTEST_HOST
        try {
            $env:VMTEST_POOL = 'mine'; $env:VMTEST_HOST = 'OTHERPC'
            Invoke-EvalVmTest -ToolPath $tool -Pool 'A' | Out-Null
            $env:VMTEST_POOL | Should Be 'mine'
            $env:VMTEST_HOST | Should Be 'OTHERPC'
        } finally { $env:VMTEST_POOL = $savedPool; $env:VMTEST_HOST = $savedHost }
    }
}

Describe 'the report' {
    It 'reports results, failures and thrown-out runs' {
        $runs = @(1..3 | ForEach-Object { New-Run -Config 'MemEval-12' }) + (New-Run -ExitCode 1) + (New-Run -Discarded)
        $text = (Format-EvalReport -Runs $runs -Hosts PC1 -MinRuns 1) -join "`n"
        $text | Should Match 'criterion 1, Same results'
        $text | Should Match 'Runs thrown out'
        $text | Should Match 'TaskTimePercent: 10'
    }
    It 'says None when nothing failed' {
        $runs = @(1..3 | ForEach-Object { New-Run -Config 'MemEval-12' }) + (New-Run)
        (Format-EvalReport -Runs $runs -Hosts PC1 -MinRuns 1) -join "`n" | Should Match 'None\.'
    }
    It 'uses plain sentences, not a table a screen reader reads as bars' {
        $runs = @(1..3 | ForEach-Object { New-Run -Config 'MemEval-12' }) + (New-Run)
        (Format-EvalReport -Runs $runs -Hosts PC1) -join "`n" | Should Not Match '\|'
    }
    It 'reports the Control run count, even for exactly one run' {
        (Format-EvalReport -Runs @((New-Run -Config 'MemEval-12')) -Hosts PC1) -join "`n" | Should Match 'PC1, W7: 1 Control runs'
    }
    It 'reads back the JSON a run writes' {
        $dir = Join-Path $TestDrive 'runs\PC1'
        New-Item $dir -ItemType Directory -Force | Out-Null
        New-Run | Add-Member -PassThru schema (Get-EvalSchema) | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $dir 'a.json') -Encoding UTF8
        @(Read-EvalRuns -Root $TestDrive)[0].workload | Should Be 'W7'
    }
    It 'judges a run the same after a trip through JSON' {
        $trip = { param($r) $r | ConvertTo-Json -Depth 6 | ConvertFrom-Json }
        $control = & $trip (New-Run -Config 'MemEval-12' -TestsTotal 4920)
        (Check (Run-Criteria (& $trip (New-Run -TestsTotal 0)) $control) 1).Result | Should Be 'Fail'
        Test-ChecksPass (Run-Criteria (& $trip (New-Run -TestsTotal 4920)) $control) | Should Be $true
    }
    It 'leaves out runs written in an older format' {
        $dir = Join-Path $TestDrive 'old\runs\PC1'
        New-Item $dir -ItemType Directory -Force | Out-Null
        New-Run | Add-Member -PassThru schema 1 | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $dir 'a.json') -Encoding UTF8
        @(Read-EvalRuns -Root (Join-Path $TestDrive 'old')).Count | Should Be 0
    }
    It 'reads nothing, without an error, before any run' {
        @(Read-EvalRuns -Root (Join-Path $TestDrive 'nothing')).Count | Should Be 0
    }
}
