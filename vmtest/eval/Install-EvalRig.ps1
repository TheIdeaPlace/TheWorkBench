<#
.SYNOPSIS
Install the workloads' tools into each eval VM's Clean.

.DESCRIPTION
Run this on the PC that has the VMs, after New-EvalVMs.ps1 has made them and the payload is in
%LOCALAPPDATA%\vmtest\eval\payload. Each VM gets the same payload, so runs need no time or network for
setup, and Clean holds no running program. Each VM installs it itself because Hyper-V can't export a
VM whose disk sits on vmtest's read-only base, so one rigged VM can't be copied. A note on the VM records
that this finished, so a failure halfway is put right by running this again.
#>
[CmdletBinding()]
param([string]$ToolPath, [string[]]$Config = @('MemEval-12', 'MemEval-8', 'MemEval-6', 'MemEval-4', 'MemEval-2'))

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VmMemEval.psm1') -Force
if (-not $ToolPath) {
    $ToolPath = if (Test-Path 'C:\vmtest-tool\vmtest.ps1') { 'C:\vmtest-tool\vmtest.ps1' } else { Join-Path (Split-Path $PSScriptRoot) 'vmtest.ps1' }
}
$pool = @(Get-EvalConfigs | ForEach-Object Name)
$payload = Join-Path (Get-EvalDataRoot) 'payload'
if (-not (Test-Path $payload)) { throw "There's no payload at $payload. Run New-EvalPayload.ps1, or Invoke-OnEvalHost.ps1 -PayloadTo." }
$vmScripts = @((Join-Path $PSScriptRoot 'audio-demo-vm'), (Join-Path $PSScriptRoot '..\..\audio-demo\vm')) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $vmScripts) { throw "Can't find the audio-demo VM scripts." }

foreach ($name in $Config) {
    function vtOk {
        param([Parameter(ValueFromRemainingArguments)]$Rest)
        $r = Invoke-EvalVmTest -ToolPath $ToolPath -Pool $pool -Arguments ($Rest + @('-Repo', 'MemEval', '-Branch', "rig-$name"))
        if ($r.ExitCode -ne 0) { throw "vmtest $($Rest -join ' ') failed: $($r.Output -join ' ')" }
        $r.Output
    }
    if ("$((Get-VM -Name $name).Notes)" -match ' Rigged\.') { "$name already has the rig."; continue }
    $began = Get-Date
    vtOk begin -VM $name | Out-Null
    foreach ($f in Get-ChildItem $payload -File | Where-Object { $_.Name -ne 'ffmpeg.exe' }) { vtOk push $f.FullName | Out-Null }
    foreach ($s in 'install-vbcable.ps1', 'install-nvda.ps1', 'seed-nvda-config.ps1', 'demo.ps1') { vtOk push (Join-Path $vmScripts $s) | Out-Null }
    vtOk run -ScriptFile (Join-Path $PSScriptRoot 'guest\Install-Rig.ps1') -Elevated -Timeout 3500 | Select-Object -Last 3
    # NVDA's settings belong to the signed-in user, not the admin session the rig ran in.
    $seed = Join-Path $env:TEMP "memeval-seed-$PID.ps1"
    Set-Content $seed "& 'C:\vmtest\files\seed-nvda-config.ps1' -CharEcho 0" -Encoding ASCII
    try { vtOk run -ScriptFile $seed -Timeout 120 | Out-Null } finally { Remove-Item $seed -ErrorAction SilentlyContinue }
    # Replaces Clean, reboots so nothing from the install is left running, and frees the VM.
    vtOk prepare -VM $name -Force | Out-Null
    # Only now, so a failure above is put right by running this again.
    Set-VM -Name $name -Notes "$((Get-VM -Name $name).Notes) Rigged."
    "$name has the rig in its Clean, after $([int]((Get-Date) - $began).TotalMinutes) min."
}
