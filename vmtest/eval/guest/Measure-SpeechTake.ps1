# Runs in the VM as the signed-in user: installs QuickMail, starts a screen reader, records a tour of
# the app's menus through audio-demo's demo.ps1, and prints one WORKLOAD line. What it measures, from
# the take's own key log, NVDA's speech log and the recording:
#   failed     steps where the screen reader said nothing (NVDA only: Narrator keeps no log)
#   misplaced  steps whose speech began after the next key was due, so it landed in the wrong step
#   audioGaps  1 if the recording is more than half a second shorter than the take, meaning capture fell behind
#   latency    key press to first speech: median, and the worst 5%
#
# The workload puts these in front of this file when it sends it: $Reader (NVDA or Narrator), $TourMinutes,
# and $AlongsideBuild, which builds QuickMail throughout for the worst case.
$ErrorActionPreference = 'Continue'
$StepSeconds = 3.5

# ---- reading a take --------------------------------------------------------------------------------

function ConvertTo-SecondsOfDay([string]$Clock) {
    $p = $Clock.Split(':')
    [int]$p[0] * 3600 + [int]$p[1] * 60 + [double]::Parse($p[2], [Globalization.CultureInfo]::InvariantCulture)
}

# What a take's key log says: the wall clock at its zero, how far NVDA's log had got, and each key's time.
function Read-KeyLog {
    param([string[]]$Lines)
    $zero = $null; $logPos = 0; $keys = @()
    foreach ($line in $Lines) {
        $f = $line.Split("`t")
        if ($f.Count -lt 2 -or $f[0] -notmatch '^\d+$') { continue }
        $ms = [double]$f[0]
        switch ($f[1]) {
            'clock' { $zero = (ConvertTo-SecondsOfDay $f[2]) - $ms / 1000 }
            'logpos' { $logPos = [int64]$f[2] }
            'k' { $keys += $ms / 1000 }
        }
    }
    [pscustomobject]@{ Zero = $zero; LogPosition = $logPos; Keys = @($keys) }
}

# When NVDA said something, in seconds of the day, from the lines of its log.
function Read-SpeechTimes {
    param([string[]]$Lines)
    $when = $null
    foreach ($line in $Lines) {
        if ($line -match '^IO - \S+ \((\d\d:\d\d:\d\d\.\d{3})\)') { $when = ConvertTo-SecondsOfDay $Matches[1]; continue }
        if ($null -ne $when -and $line.StartsWith('Speaking ')) {
            # Quoted strings outside a command's parentheses are the speech; a list of only commands says nothing.
            if (($line.Substring(9) -replace '\w+\s*\([^)]*\)', '') -match "'[^']*\S[^']*'|""[^""]*\S[^""]*""") { $when }
            $when = $null
        }
    }
}

# Line the speech up against the keys.
function Measure-Speech {
    param([double[]]$KeySeconds, [double[]]$SpeechSeconds, [double]$StepSeconds = 3.5)
    $failed = @(); $misplaced = 0; $latencies = @()
    for ($i = 0; $i -lt $KeySeconds.Count; $i++) {
        $key = $KeySeconds[$i]
        $next = if ($i + 1 -lt $KeySeconds.Count) { $KeySeconds[$i + 1] } else { [double]::MaxValue }
        $inStep = @($SpeechSeconds | Where-Object { $_ -ge $key -and $_ -lt $next } | Sort-Object)
        if ($inStep.Count -eq 0) { $failed += "step $($i + 1) silent"; continue }
        $latency = ($inStep[0] - $key) * 1000
        $latencies += $latency
        if ($latency -gt $StepSeconds * 1000) { $misplaced++ }
    }
    $sorted = @($latencies | Sort-Object)
    [pscustomobject]@{
        Failed    = @($failed)
        Misplaced = $misplaced
        MedianMs  = if ($sorted.Count) { [math]::Round($sorted[[math]::Floor($sorted.Count / 2)]) } else { $null }
        WorstMs   = if ($sorted.Count) { [math]::Round($sorted[[math]::Min($sorted.Count - 1, [math]::Floor($sorted.Count * 0.95))]) } else { $null }
    }
}

# How many seconds of sound a PCM WAV holds, from its header and size.
function Get-WavSeconds {
    param([string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = New-Object IO.BinaryReader($stream)
        $stream.Position = 28
        $bytesPerSecond = $reader.ReadInt32()
        if ($bytesPerSecond -le 0) { return 0 }
        ($stream.Length - 44) / $bytesPerSecond
    } finally { $stream.Close() }
}

# ---- the take --------------------------------------------------------------------------------------

if (-not $Reader) { return }   # the tests load this for the functions above

$problems = @()
$msi = Get-ChildItem 'C:\vmtest\files' -Filter 'QuickMail-*.msi' | Where-Object { ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') -eq ($_.Name -like '*arm64*') } | Select-Object -First 1
$p = Start-Process msiexec.exe -ArgumentList '/i', "`"$($msi.FullName)`"", '/qn', '/norestart' -PassThru -Wait
"msiexec exit $($p.ExitCode)"
$exe = @("$env:LOCALAPPDATA\QuickMail\current\QuickMail.exe", "$env:LOCALAPPDATA\QuickMail\QuickMail.exe", "$env:ProgramFiles\QuickMail\QuickMail.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) { $exe = (Get-ChildItem $env:LOCALAPPDATA, $env:ProgramFiles -Recurse -Filter QuickMail.exe -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }
if (-not $exe) { 'WORKLOAD ' + (@{ ran = $false; problems = @('QuickMail.exe not found after installing') } | ConvertTo-Json -Compress); exit 0 }
Get-Process QuickMail, Update -ErrorAction SilentlyContinue | Stop-Process -Force

$logFile = 'C:\vmtest\rec\nvda.log'
New-Item 'C:\vmtest\rec' -ItemType Directory -Force | Out-Null
if ($Reader -eq 'NVDA') {
    Start-Process "$env:ProgramFiles\NVDA\nvda.exe" -ArgumentList '--log-level=12', '-f', $logFile
} else {
    $base = 'HKCU:\Software\Microsoft\Narrator'
    foreach ($k in $base, "$base\NoRoam", "$base\NarratorHome") { if (-not (Test-Path $k)) { New-Item $k | Out-Null } }
    Set-ItemProperty "$base\NarratorHome" AutoStart 0 -Type DWord
    Start-Process 'C:\Windows\System32\Narrator.exe'
}
Start-Sleep -Seconds 12

# A tour of what's there on a first run: into each menu and out, and through the window with Tab.
$block = @('%f', '{DOWN}', '{DOWN}', '{ESC}', '{ESC}', '%v', '{DOWN}', '{ESC}', '{ESC}', '%h', '{DOWN}', '{ESC}', '{ESC}', '{TAB}', '{TAB}', '+{TAB}')
$steps = New-Object Collections.Generic.List[string]
$steps.Add('w 1'); $steps.Add('x'); $steps.Add('w 50')    # its startup announcements run up to about 50 s
$count = [math]::Ceiling($TourMinutes * 60 / $StepSeconds)
for ($i = 0; $i -lt $count; $i++) { $steps.Add("k $($block[$i % $block.Count])"); $steps.Add("w $StepSeconds") }
$stepsFile = 'C:\vmtest\files\tour.steps'
$steps | Set-Content $stepsFile -Encoding UTF8

$build = $null
if ($AlongsideBuild) {
    $build = Start-Process dotnet -ArgumentList 'build', 'C:\vmtest\src\quickmail\QuickMail.Tests\QuickMail.Tests.csproj', '-c', 'Release', '--no-restore' -WindowStyle Hidden -PassThru
}

& 'C:\vmtest\files\demo.ps1' -Name 'tour' -Steps $stepsFile -Launch $exe 2>&1 | Select-Object -Last 3
$takeFailed = -not (Test-Path 'C:\vmtest\rec\tour.keys.txt')
if ($build -and -not $build.WaitForExit(600000)) { $problems += 'the build alongside was still going 10 minutes after the take'; Stop-Process -Id $build.Id -Force -ErrorAction SilentlyContinue }
Get-Process QuickMail, Narrator, nvda -ErrorAction SilentlyContinue | Stop-Process -Force

if ($takeFailed) { 'WORKLOAD ' + (@{ ran = $false; problems = @('the take left no key log') } | ConvertTo-Json -Compress); exit 0 }

$keyLines = Get-Content 'C:\vmtest\rec\tour.keys.txt' -Encoding UTF8
$endMs = (@($keyLines | Where-Object { $_ -match "`tend`t" })[0]).Split("`t")[0]
$startMs = (@($keyLines | Where-Object { $_ -match "`tstart`t" })[0]).Split("`t")[0]
$short = (([double]$endMs - [double]$startMs) / 1000) - (Get-WavSeconds 'C:\vmtest\rec\tour.wav')
"recording is $([math]::Round($short, 2)) s shorter than the take"
$speech = [ordered]@{ failed = @(); misplaced = 0; audioGaps = $(if ($short -gt 0.5) { 1 } else { 0 }) }
$latency = $null
if ($Reader -eq 'NVDA') {
    $keys = Read-KeyLog $keyLines
    $bytes = [IO.File]::ReadAllBytes($logFile)
    $text = [Text.Encoding]::UTF8.GetString($bytes, [math]::Min($keys.LogPosition, $bytes.Length), $bytes.Length - [math]::Min($keys.LogPosition, $bytes.Length))
    $spoken = @(Read-SpeechTimes ($text -split "`r?`n") | ForEach-Object { $_ - $keys.Zero })
    $m = Measure-Speech -KeySeconds $keys.Keys -SpeechSeconds $spoken -StepSeconds $StepSeconds
    "$($keys.Keys.Count) keys, $($spoken.Count) utterances, $($m.Failed.Count) silent, median $($m.MedianMs) ms"
    $speech.failed = @($m.Failed)
    $speech.misplaced = $m.Misplaced
    if ($null -ne $m.MedianMs) { $latency = [ordered]@{ medianMs = $m.MedianMs; worstMs = $m.WorstMs } }
}
'WORKLOAD ' + ([ordered]@{ ran = $true; speech = $speech; latency = $latency; problems = @($problems) } | ConvertTo-Json -Compress -Depth 4)
exit 0
