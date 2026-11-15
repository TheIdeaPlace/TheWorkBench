<#
.SYNOPSIS
Start or stop watching real work on this PC: a scheduled task runs Measure-Host.ps1 for as long as
the evaluation needs it.

.DESCRIPTION
Run this on the PC that has the VMs. It only reads, every 15 seconds rather than a run's 5, because it
stays on for days, including on battery. The rows go in %LOCALAPPDATA%\vmtest\eval\samples. -Stop
removes the scheduled task and keeps the rows.
#>
[CmdletBinding()]
param([switch]$Stop, [ValidateRange(1, 3600)][int]$IntervalSeconds = 15)

$ErrorActionPreference = 'Stop'
$name = 'vmtest-memeval-watch'
$home_ = Join-Path $env:LOCALAPPDATA 'vmtest\eval'
$script = Join-Path $home_ 'Measure-Host.ps1'

$existing = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
if ($Stop) {
    if ($existing) {
        Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $name -Confirm:$false
    }
    "Stopped watching. The rows are in $home_\samples."
    return
}

# A copy of its own, because the folder a call from another PC runs from is removed when it ends.
New-Item $home_ -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Measure-Host.ps1') $script -Force
if ($existing) { Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName $name -Confirm:$false }

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`" -IntervalSeconds $IntervalSeconds"
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew
$user = "$env:USERDOMAIN\$env:USERNAME"
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Principal $principal | Out-Null
Start-ScheduledTask -TaskName $name
Start-Sleep -Seconds 2
if ((Get-ScheduledTask -TaskName $name).State -ne 'Running') { throw "The scheduled task $name didn't start. It needs you to be signed in on this PC." }
"Watching every $IntervalSeconds s. The rows go in $home_\samples. It restarts when you sign in, and '$($MyInvocation.MyCommand.Name) -Stop' ends it."
