<#
.SYNOPSIS
Make the eval VMs on this PC, each with its own memory settings and its own Clean.

.DESCRIPTION
They're copies from the pool's first VM's Clean, made by 'vmtest prepare -Pool' with VMTEST_POOL set
only for those calls, so 'vmtest begin' from anywhere else never sees them. Memory settings are kept
in each checkpoint, so each VM is turned off, given its size, and then prepared again, which replaces
the copy's Clean with one taken at that size. A note on the VM records that this finished, so a
failure halfway is put right by running this again.

Run this on the PC that has the VMs. It changes nothing about the pool's own VMs, and it leaves alone
an eval VM a run is using.
#>
[CmdletBinding()]
param(
    [string]$ToolPath,
    [string[]]$Config = @('MemEval-12', 'MemEval-8', 'MemEval-6', 'MemEval-4', 'MemEval-2')
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VmMemEval.psm1') -Force
if (-not $ToolPath) {
    $ToolPath = if (Test-Path 'C:\vmtest-tool\vmtest.ps1') { 'C:\vmtest-tool\vmtest.ps1' } else { Join-Path (Split-Path $PSScriptRoot) 'vmtest.ps1' }
}
$all = Get-EvalConfigs
$unknown = @($Config | Where-Object { @($all | ForEach-Object Name) -notcontains $_ })
if ($unknown.Count) { throw "No such eval config: $($unknown -join ', ')." }
$wanted = @($all | Where-Object { $Config -contains $_.Name })
$pool = @('ClaudeTesting') + @($wanted | ForEach-Object Name)

function vtOk {
    param([Parameter(ValueFromRemainingArguments)]$Rest)
    $r = Invoke-EvalVmTest -ToolPath $ToolPath -Pool $pool -Arguments $Rest
    if ($r.ExitCode -ne 0) { throw "vmtest $($Rest -join ' ') failed: $($r.Output -join ' ')" }
    $r.Output
}

$stateDir = Join-Path $env:LOCALAPPDATA 'vmtest'
foreach ($c in $wanted) {
    if (Test-Path (Join-Path $stateDir "lock-$($c.Name).json")) { throw "A run is using $($c.Name), so it can't be changed now. Try again when it's free." }
}

vtOk prepare -Pool

foreach ($c in $wanted) {
    $sized = "Sized $($c.MinGB)/$($c.StartupGB)/$($c.MaxGB) GB"
    $vm = Get-VM -Name $c.Name
    if ("$($vm.Notes)".Contains($sized)) {
        "$($c.Name) is already $($c.MinGB) / $($c.StartupGB) / $($c.MaxGB) GB, with its Clean taken at that size."
        continue
    }
    if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force }
    Set-VMMemory -VMName $c.Name -DynamicMemoryEnabled $true -MinimumBytes ($c.MinGB * 1GB) -StartupBytes ($c.StartupGB * 1GB) -MaximumBytes ($c.MaxGB * 1GB)
    vtOk prepare -VM $c.Name -Force
    # Only now: 'prepare -Pool' looks for its own note at the start, so this goes on the end.
    Set-VM -Name $c.Name -Notes "$($vm.Notes) $sized."
    $after = Get-VMMemory -VMName $c.Name
    "$($c.Name): min $($after.Minimum / 1GB), startup $($after.Startup / 1GB), max $($after.Maximum / 1GB) GB."
}
