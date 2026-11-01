<#
.SYNOPSIS
Log every VM's memory and this PC's load to a CSV, one file a day, until told to stop.

.DESCRIPTION
Reads only. Each row is one VM at one moment, with the PC's own numbers repeated on every row, and
the task holding the VM from vmtest's lock file. When no VM is running there is one row for the PC
alone, with an empty VM name, so what the PC itself uses is on record too.
-StopFile ends it when that file appears; a run uses that. Left out, it runs until it's stopped,
which is how the scheduled task for watching real work runs it.
#>
[CmdletBinding()]
param(
    [string]$Folder = (Join-Path $env:LOCALAPPDATA 'vmtest\eval\samples'),
    [ValidateRange(1, 3600)][int]$IntervalSeconds = 5,
    [string]$StopFile,
    [string]$StateDir = (Join-Path $env:LOCALAPPDATA 'vmtest'),
    # 0 means no limit. A run sets one, in case it's killed before it can write the stop file.
    [int]$MaxMinutes = 0
)

$ErrorActionPreference = 'Stop'
New-Item $Folder -ItemType Directory -Force | Out-Null

# vmtest keeps the first VM's lock in lock.json and the others' in lock-<name>.json.
function Get-Holder([string]$VMName) {
    foreach ($name in @("lock-$($VMName -replace '[^A-Za-z0-9._-]', '_').json", $(if ($VMName -eq 'ClaudeTesting') { 'lock.json' }))) {
        if (-not $name) { continue }
        $path = Join-Path $StateDir $name
        if (Test-Path $path) {
            try { $lock = Get-Content $path -Raw | ConvertFrom-Json; return "$($lock.repo) / $($lock.branch)" } catch { return '' }
        }
    }
    ''
}

# One CSV row: the PC's numbers, then one VM's.
function New-Row($Pc, $VM, $DemandMB, $AssignedMB, $StartupMB, $Status, $Holder) {
    $row = [ordered]@{}
    foreach ($k in $Pc.Keys) { $row[$k] = $Pc[$k] }
    $row.VM = $VM; $row.DemandMB = $DemandMB; $row.AssignedMB = $AssignedMB; $row.StartupMB = $StartupMB; $row.Status = $Status; $row.Holder = $Holder
    [pscustomobject]$row
}

$counters = '\Memory\Available MBytes', '\Memory\Pages Input/sec', '\Processor(_Total)\% Processor Time', '\PhysicalDisk(_Total)\Current Disk Queue Length'

$began = Get-Date
# A failed reading stays blank, because a 0 would look like a PC that had run out of memory.
function ConvertTo-Reading($Value, [int]$Digits) { if ($null -eq $Value) { '' } else { [math]::Round([double]$Value, $Digits) } }

while ($true) {
    if ($StopFile -and (Test-Path $StopFile)) { break }
    if ($MaxMinutes -and ((Get-Date) - $began).TotalMinutes -ge $MaxMinutes) { break }
    $started = Get-Date
    try {
        $values = @{}
        foreach ($s in (Get-Counter -Counter $counters -ErrorAction Continue 2>$null).CounterSamples) {
            if ($s.Path -like '*\available mbytes') { $values.AvailableMB = $s.CookedValue }
            elseif ($s.Path -like '*\pages input/sec') { $values.PagesInput = $s.CookedValue }
            elseif ($s.Path -like '*% processor time') { $values.CpuPercent = $s.CookedValue }
            elseif ($s.Path -like '*current disk queue length') { $values.DiskQueue = $s.CookedValue }
        }
        $pc = [ordered]@{
            Time           = $started.ToString('s')
            HostAvailableMB = ConvertTo-Reading $values.AvailableMB 0
            HostPagesInput = ConvertTo-Reading $values.PagesInput 1
            HostCpuPercent = ConvertTo-Reading $values.CpuPercent 1
            HostDiskQueue  = ConvertTo-Reading $values.DiskQueue 1
        }
        if ($null -eq $values.AvailableMB) { Add-Content (Join-Path $Folder "$env:COMPUTERNAME-errors.log") "$((Get-Date).ToString('s')) The PC's counters gave no reading." }
        $running = @(Get-VM | Where-Object { $_.State -eq 'Running' })
        $rows = if ($running) {
            foreach ($vm in $running) {
                New-Row $pc $vm.Name ([math]::Round($vm.MemoryDemand / 1MB)) ([math]::Round($vm.MemoryAssigned / 1MB)) ([math]::Round($vm.MemoryStartup / 1MB)) "$($vm.MemoryStatus)" (Get-Holder $vm.Name)
            }
        } else {
            New-Row $pc '' '' '' '' '' ''
        }
        $file = Join-Path $Folder "$env:COMPUTERNAME-$($started.ToString('yyyy-MM-dd')).csv"
        $rows | Export-Csv $file -Append -NoTypeInformation
    } catch {
        # A bad sample shouldn't end a week of watching. Say so where it can be found and carry on.
        Add-Content (Join-Path $Folder "$env:COMPUTERNAME-errors.log") "$((Get-Date).ToString('s')) $($_.Exception.Message)"
    }
    $wait = $IntervalSeconds - ((Get-Date) - $started).TotalSeconds
    if ($wait -gt 0) { Start-Sleep -Milliseconds ([int]($wait * 1000)) }
}
