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
$credential = Import-Clixml $signIn
# Name resolution for a PC on the local network drops now and then, for a minute or so at a time.
$session = $null
foreach ($attempt in 1..6) {
    try { $session = New-PSSession -ComputerName $Computer -Credential $credential -ErrorAction Stop; break }
    catch {
        if ($attempt -eq 6) { throw }
        Write-Host "Couldn't reach $Computer, trying again in 30 s: $($_.Exception.Message)"
        Start-Sleep -Seconds 30
    }
}
try {
    if ($CollectRuns) {
        # By the PC's own name, which is what its folder is called, whatever name reached it here.
        $found = Invoke-Command -Session $session -ScriptBlock {
            $dir = Join-Path $env:LOCALAPPDATA "vmtest\eval\runs\$env:COMPUTERNAME"
            [pscustomobject]@{ Name = $env:COMPUTERNAME; Files = @(Get-ChildItem $dir -Filter *.json -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }) }
        }
        $mine = Join-Path $env:LOCALAPPDATA "vmtest\eval\runs\$($found.Name)"
        New-Item $mine -ItemType Directory -Force | Out-Null
        foreach ($file in @($found.Files)) { Copy-Item $file -Destination $mine -FromSession $session -Force }
        "Copied $(@($found.Files).Count) runs from $($found.Name) to $mine. A run removed there stays here until it's removed here too."
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
