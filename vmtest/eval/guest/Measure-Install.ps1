# Runs in the VM as the signed-in user: QuickMail's installer, first run and uninstaller, the commonest
# light task, and prints one WORKLOAD line. It repeats the first run, so a slow one isn't a fluke.
$ErrorActionPreference = 'Continue'
$problems = @()
$msi = Get-ChildItem 'C:\vmtest\files' -Filter 'QuickMail-*.msi' | Where-Object { ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') -eq ($_.Name -like '*arm64*') } | Select-Object -First 1
$p = Start-Process msiexec.exe -ArgumentList '/i', "`"$($msi.FullName)`"", '/qn', '/norestart' -PassThru -Wait
"install exit $($p.ExitCode)"
if ($p.ExitCode -ne 0) { $problems += "msiexec install exited $($p.ExitCode)" }
$exe = @("$env:LOCALAPPDATA\QuickMail\current\QuickMail.exe", "$env:LOCALAPPDATA\QuickMail\QuickMail.exe", "$env:ProgramFiles\QuickMail\QuickMail.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) { $exe = (Get-ChildItem $env:LOCALAPPDATA, $env:ProgramFiles -Recurse -Filter QuickMail.exe -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }
$limits = @()
if (-not $exe) { $problems += 'QuickMail.exe not found after installing' }
else {
    "found $exe"
    Get-Process QuickMail, Update -ErrorAction SilentlyContinue | Stop-Process -Force
    foreach ($attempt in 1, 2) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        Start-Process $exe | Out-Null
        $seen = $null
        while ($watch.Elapsed.TotalSeconds -lt 180 -and $null -eq $seen) {
            if (@(Get-Process QuickMail -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }).Count) { $seen = [math]::Round($watch.Elapsed.TotalSeconds, 1) } else { Start-Sleep -Milliseconds 200 }
        }
        "first window, attempt ${attempt}: $seen s"
        # A step never reached has no time, which the criteria treat as incomplete, not as fast.
        $limits += [ordered]@{ name = "first window $attempt"; limit = 60; actual = $seen }
        Start-Sleep -Seconds 15
        Get-Process QuickMail -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 5
    }
}
$p = Start-Process msiexec.exe -ArgumentList '/x', "`"$($msi.FullName)`"", '/qn', '/norestart' -PassThru -Wait
"uninstall exit $($p.ExitCode)"
if ($p.ExitCode -ne 0) { $problems += "msiexec uninstall exited $($p.ExitCode)" }
'WORKLOAD ' + ([ordered]@{ ran = ($problems.Count -eq 0); limits = @($limits); problems = @($problems) } | ConvertTo-Json -Compress -Depth 4)
exit 0
