# Runs in the test VM with admin rights, through 'vmtest run -ScriptFile -Elevated'. Starts Windows' own
# performance log, which keeps recording after this script ends and costs far less than a looping
# PowerShell would, so measuring doesn't disturb speech timing. Binary format, because a CSV log only
# ever lists the processes running when it started.
$ErrorActionPreference = 'Stop'
$dir = 'C:\vmtest\eval'
New-Item $dir -ItemType Directory -Force | Out-Null
Get-ChildItem $dir -File | Remove-Item -Force

# Through cmd, so its complaint about a log that doesn't exist yet can't stop this script.
cmd /c "logman stop vmtest-memeval >nul 2>&1"
cmd /c "logman delete vmtest-memeval >nul 2>&1"

$counterFile = Join-Path $dir 'counters.txt'
@(
    '\Memory\Available MBytes'
    '\Memory\Committed Bytes'
    '\Memory\Commit Limit'
    '\Memory\Pages Input/sec'
    '\Paging File(_Total)\% Usage'
    '\Process(*)\Working Set - Private'
) | Set-Content $counterFile -Encoding ASCII

$out = cmd /c "logman create counter vmtest-memeval -cf `"$counterFile`" -si 5 -f bin -o `"$(Join-Path $dir 'memeval')`" -ow 2>&1"
if ($LASTEXITCODE -ne 0) { "logman create failed: $out"; exit 1 }
$out = cmd /c "logman start vmtest-memeval 2>&1"
if ($LASTEXITCODE -ne 0) { "logman start failed: $out"; exit 1 }

# Stop-Counters.ps1 reports events since this moment, and whether the paging file grew from this size.
$pagefile = @(Get-CimInstance Win32_PageFileUsage | Measure-Object AllocatedBaseSize -Sum)[0].Sum
@{ started = (Get-Date).ToString('o'); pagefileMB = [int]$pagefile } | ConvertTo-Json | Set-Content (Join-Path $dir 'start.json') -Encoding ASCII
'Counters started.'
exit 0
