# Copy files out of the test VM, and run PowerShell in it, over the same PowerShell Direct session
# vmtest uses. vmtest has 'push' but no 'pull'; this fills that gap without changing vmtest.
#
# Like vmtest, it works on the VM held by the task for the git repo and branch you run it from.

function Get-VmTestModulePath {
    $home_ = if ($env:VMTEST_HOME) { $env:VMTEST_HOME } else { Join-Path $PSScriptRoot '..\..\vmtest' }
    $p = Join-Path $home_ 'VmTest.psm1'
    if (-not (Test-Path $p)) { throw "Can't find vmtest (looked in $home_). Set VMTEST_HOME to the vmtest folder." }
    (Resolve-Path $p).Path
}

function Get-VmTestScriptPath { Join-Path (Split-Path (Get-VmTestModulePath)) 'vmtest.ps1' }

function Invoke-WithGuestSession([scriptblock]$Body) {
    $m = Import-Module (Get-VmTestModulePath) -Force -PassThru -DisableNameChecking
    $session = & $m { Assert-TaskHasVM | Out-Null; New-GuestSession }
    try { & $Body $session } finally { Remove-PSSession $session; Remove-Module $m -ErrorAction SilentlyContinue }
}

function Copy-FromTestVM {
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
    Invoke-WithGuestSession { param($s) Copy-Item $Source -Destination $Destination -FromSession $s -Force }
}

function Invoke-InTestVM {
    param([Parameter(Mandatory)][string]$Script)
    Invoke-WithGuestSession { param($s) Invoke-Command -Session $s -ScriptBlock ([scriptblock]::Create($Script)) }
}

# Runs a vmtest command and returns its output lines. Throws, with all of the output, if vmtest
# failed, or if 'vmtest run' reports that the command it ran exited non-zero ("Exit code N").
function Invoke-VmTest {
    $out = & powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Get-VmTestScriptPath) @args 2>&1
    $code = $LASTEXITCODE
    $text = @($out | ForEach-Object { "$_" })
    $all = $text -join "`n"
    if ($all -match 'vmtest \w+ failed') { throw $all }
    if ($args[0] -eq 'run') {
        $m = [regex]::Match($all, '(?m)^Exit code (-?\d+)')
        if (($m.Success -and [int]$m.Groups[1].Value -ne 0) -or (-not $m.Success -and $code -ne 0)) {
            throw "The command in the VM failed:`n$all"
        }
    }
    $text
}

Export-ModuleMember -Function Copy-FromTestVM, Invoke-InTestVM, Invoke-VmTest
