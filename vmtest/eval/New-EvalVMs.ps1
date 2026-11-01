<#
.SYNOPSIS
Make the eval VMs on this PC, each with its own memory settings and its own Clean.

.DESCRIPTION
They're copies from the pool's first VM's Clean, made by 'vmtest prepare -Pool' with VMTEST_POOL set
only in this process, so 'vmtest begin' from any other session never sees them. Memory settings are
kept in each checkpoint, so each VM is turned off, given its size, and then prepared again, which
replaces the copy's Clean with one taken at that size.

Run this on the PC that has the VMs. It changes nothing about the pool's own VMs.
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
if ($unknown) { throw "No such eval config: $($unknown -join ', ')." }

$env:VMTEST_POOL = (@('ClaudeTesting') + @($all | Where-Object { $Config -contains $_.Name } | ForEach-Object Name)) -join ','
Remove-Item Env:\VMTEST_HOST -ErrorAction SilentlyContinue

function vtOk {
    param([Parameter(ValueFromRemainingArguments)]$Rest)
    $ErrorActionPreference = 'Continue'   # so a line on stderr is output, not a failure
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ToolPath @Rest 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "vmtest $($Rest -join ' ') failed: $($out -join ' ')" }
    $out
}

vtOk prepare -Pool

foreach ($c in $all | Where-Object { $Config -contains $_.Name }) {
    $vm = Get-VM -Name $c.Name
    $memory = Get-VMMemory -VMName $c.Name
    if ($memory.Maximum -eq $c.MaxGB * 1GB -and $memory.Startup -eq $c.StartupGB * 1GB -and $memory.Minimum -eq $c.MinGB * 1GB) {
        "$($c.Name) is already $($c.MinGB) / $($c.StartupGB) / $($c.MaxGB) GB."
        continue
    }
    if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force }
    Set-VMMemory -VMName $c.Name -DynamicMemoryEnabled $true -MinimumBytes ($c.MinGB * 1GB) -StartupBytes ($c.StartupGB * 1GB) -MaximumBytes ($c.MaxGB * 1GB)
    vtOk prepare -VM $c.Name -Force
    $after = Get-VMMemory -VMName $c.Name
    "$($c.Name): min $($after.Minimum / 1GB), startup $($after.Startup / 1GB), max $($after.Maximum / 1GB) GB."
}
