# Runs in the test VM with admin rights, through 'vmtest run -ScriptFile -Elevated'. Stops the log
# Start-Counters.ps1 started and prints what the host needs as one line starting MEMEVAL, because
# 'vmtest run' hands back only the last 200 lines of output and there's no command to fetch a file.
# The counters go compressed; the processes are already reduced to the busiest moment.
$ErrorActionPreference = 'Stop'
$dir = 'C:\vmtest\eval'
$startFile = Join-Path $dir 'start.json'
if (-not (Test-Path $startFile)) { 'Stop-Counters: the counters were never started in this VM.'; exit 1 }
$start = Get-Content $startFile -Raw | ConvertFrom-Json

cmd /c "logman stop vmtest-memeval >nul 2>&1"
$log = Get-ChildItem $dir -Filter 'memeval*.blg' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $log) { 'Stop-Counters: no counter log was written.'; exit 1 }

$all = Join-Path $dir 'all.csv'
$out = cmd /c "relog `"$($log.FullName)`" -f csv -o `"$all`" -y 2>&1"
if ($LASTEXITCODE -ne 0) { "relog failed: $out"; exit 1 }
$rows = @(Import-Csv $all)
if (-not $rows) { 'Stop-Counters: the counter log has no samples.'; exit 1 }
$columns = @($rows[0].PSObject.Properties.Name)
$available = $columns | Where-Object { $_ -like '*\Memory\Available MBytes' } | Select-Object -First 1

# The moment the guest had least memory free, and which programs held it then.
$lowest = $null; $lowestValue = [double]::MaxValue
foreach ($r in $rows) {
    $v = 0.0
    if ([double]::TryParse("$($r.$available)".Trim(), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$v) -and $v -lt $lowestValue) { $lowestValue = $v; $lowest = $r }
}
$top = @()
if ($lowest) {
    $processes = foreach ($c in $columns | Where-Object { $_ -like '*\Process(*)\Working Set - Private' -and $_ -notlike '*(_Total)*' -and $_ -notlike '*(Idle)*' }) {
        $v = 0.0
        if ([double]::TryParse("$($lowest.$c)".Trim(), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$v) -and $v -gt 0) {
            [pscustomobject]@{ name = ($c -replace '^.*\\Process\(', '' -replace '\)\\Working Set - Private$', ''); mb = [math]::Round($v / 1MB) }
        }
    }
    $top = @($processes | Sort-Object mb -Descending | Select-Object -First 10)
}

# Everything but the per-process columns, compressed.
$memoryOnly = @($columns | Where-Object { $_ -notlike '*\Process(*' })
$csv = ($rows | Select-Object $memoryOnly | ConvertTo-Csv -NoTypeInformation) -join "`n"
$buffer = New-Object System.IO.MemoryStream
$gzip = New-Object System.IO.Compression.GZipStream($buffer, [System.IO.Compression.CompressionMode]::Compress)
$bytes = [System.Text.Encoding]::UTF8.GetBytes($csv)
$gzip.Write($bytes, 0, $bytes.Length)
$gzip.Close()

$since = [datetime]$start.started
$exhaustion = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 2004; StartTime = $since } -ErrorAction SilentlyContinue).Count
$crashes = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1026; StartTime = $since } -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Id -eq 1000) { "$($_.Properties[0].Value)" } else { ("$($_.Message)" -split "`n")[0].Trim() }
    } | Sort-Object -Unique)
$pagefile = @(Get-CimInstance Win32_PageFileUsage | Measure-Object AllocatedBaseSize -Sum)[0].Sum

$report = [ordered]@{
    counters       = [Convert]::ToBase64String($buffer.ToArray())
    topProcesses   = $top
    exhaustion     = $exhaustion
    crashes        = $crashes
    pagefileGrewMB = [int]$pagefile - [int]$start.pagefileMB
}
'MEMEVAL ' + ($report | ConvertTo-Json -Compress -Depth 4)
exit 0
