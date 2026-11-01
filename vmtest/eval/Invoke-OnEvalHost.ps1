<#
.SYNOPSIS
Run one of the eval scripts on the PC that has the VMs, this one or another, or fetch that PC's runs.

.DESCRIPTION
With -Computer, copies this folder to a new folder under C:\vmtest-eval there and runs the script over
PowerShell remoting, with the sign-in 'vmtest host' saved. Each call gets its own folder, so one call
can't pull a script out from under another. It's separate from C:\vmtest-tool because vmtest replaces
that whenever another PC's copy differs. Without -Computer, it runs the script here.

-CollectRuns copies that PC's run records to %LOCALAPPDATA%\vmtest\eval\runs here, so the report can
see both hosts.
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Run')][ValidateSet('New-EvalVMs', 'Invoke-EvalRun', 'Set-HostWatch')][string]$Script,
    [Parameter(ParameterSetName = 'Run')][hashtable]$Parameters = @{},
    [Parameter(ParameterSetName = 'Run')][Parameter(Mandatory, ParameterSetName = 'Collect')][string]$Computer,
    [Parameter(Mandatory, ParameterSetName = 'Collect')][switch]$CollectRuns
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
    if ($CollectRuns) {
        $mine = Join-Path $env:LOCALAPPDATA "vmtest\eval\runs\$Computer"
        New-Item $mine -ItemType Directory -Force | Out-Null
        $theirs = Invoke-Command -Session $session -ArgumentList $Computer -ScriptBlock { param($c) Join-Path $env:LOCALAPPDATA "vmtest\eval\runs\$c" }
        Copy-Item (Join-Path $theirs '*') -Destination $mine -FromSession $session -Force
        "Copied $Computer's runs to $mine."
        return
    }
    $target = Invoke-Command -Session $session -ScriptBlock {
        # A dropped connection can leave a folder behind; clear any a week old, which no run lasts.
        Get-ChildItem 'C:\vmtest-eval' -Directory -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-7) } |
            ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
        $t = "C:\vmtest-eval\$([guid]::NewGuid())"
        New-Item $t -ItemType Directory -Force | Out-Null
        $t
    }
    Get-ChildItem $PSScriptRoot -Force | ForEach-Object { Copy-Item $_.FullName -Destination $target -ToSession $session -Recurse -Force }
    try {
        Invoke-Command -Session $session -ArgumentList $target, $Script, $Parameters -ScriptBlock {
            param($t, $name, $p)
            Set-ExecutionPolicy -Scope Process Bypass -Force
            Get-ChildItem $t -Recurse -File | Unblock-File
            & (Join-Path $t "$name.ps1") @p
        }
    } finally {
        try { Invoke-Command -Session $session -ArgumentList $target -ScriptBlock { param($t) Remove-Item $t -Recurse -Force } } catch { }
    }
} finally { Remove-PSSession $session }
