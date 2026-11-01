<#
.SYNOPSIS
Run one of the eval scripts on the PC that has the VMs, this one or another.

.DESCRIPTION
With -Computer, copies this folder to C:\vmtest-eval there and runs the script over PowerShell
remoting, with the sign-in 'vmtest host' saved. It's a folder of its own because vmtest replaces
C:\vmtest-tool whenever another PC's copy differs. Without -Computer, it simply runs the script here.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('New-EvalVMs', 'Invoke-EvalRun', 'Set-HostWatch')][string]$Script,
    [string]$Computer,
    [hashtable]$Parameters = @{}
)

$ErrorActionPreference = 'Stop'
if (-not $Computer) {
    & (Join-Path $PSScriptRoot "$Script.ps1") @Parameters
    exit $LASTEXITCODE
}

$signIn = Join-Path $env:LOCALAPPDATA "vmtest\hosts\$Computer.xml"
if (-not (Test-Path $signIn)) { throw "There's no saved sign-in for $Computer. Run 'vmtest host $Computer' once in your own PowerShell window." }
$session = New-PSSession -ComputerName $Computer -Credential (Import-Clixml $signIn)
try {
    $target = 'C:\vmtest-eval'
    Invoke-Command -Session $session -ArgumentList $target -ScriptBlock {
        param($t)
        if (Test-Path $t) { Remove-Item $t -Recurse -Force }
        New-Item $t -ItemType Directory -Force | Out-Null
    }
    Get-ChildItem $PSScriptRoot -Force | ForEach-Object { Copy-Item $_.FullName -Destination $target -ToSession $session -Recurse -Force }
    Invoke-Command -Session $session -ArgumentList $target, $Script, $Parameters -ScriptBlock {
        param($t, $name, $p)
        Set-ExecutionPolicy -Scope Process Bypass -Force
        Get-ChildItem $t -Recurse -File | Unblock-File
        & (Join-Path $t "$name.ps1") @p
    }
} finally { Remove-PSSession $session }
