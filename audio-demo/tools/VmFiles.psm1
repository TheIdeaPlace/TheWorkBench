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

# With VMTEST_HOST set, the task's VM is on that PC, and PowerShell Direct reaches it only from
# there: so the session goes to the host, and from the host into the VM, as AIChat's visual probe
# (tools/ui_probe_vm.ps1) does. $Body gets the host session, with the VM's session in the host's
# $global:vmfilesGuest. Without this, setup-rig and clip failed with "doesn't hold a test VM",
# because the VM's lock is kept on the host, not here. Only VMTEST_HOST is covered: with
# VMTEST_HOSTS, or a host vmtest picks for itself from the network, these still look on this PC
# (TheWorkBench #251).
function Invoke-WithHostGuestSession([scriptblock]$Body) {
    $m = Import-Module (Get-VmTestModulePath) -Force -PassThru -DisableNameChecking
    try {
        $checkpoint = (& $m { Resolve-Task }).Checkpoint
        $hostSession = & $m { New-HostSession -ComputerName $env:VMTEST_HOST -Patient }
        $tool = & $m { Get-HostToolPath }
        # The host's copy of vmtest made current, as vmtest's own forwarding does before every
        # command: a call made on its own would otherwise import an older one, or none.
        & $m { param($s, $p) Sync-HostTool -Session $s -Path $p | Out-Null } $hostSession $tool
    } catch {
        if ($hostSession) { Remove-PSSession $hostSession }
        throw
    } finally { Remove-Module $m -ErrorAction SilentlyContinue }
    try {
        Invoke-Command -Session $hostSession -ArgumentList $tool, $checkpoint -ErrorAction Stop -ScriptBlock {
            param($tool, $checkpoint)
            $hm = Import-Module (Join-Path $tool 'VmTest.psm1') -Force -PassThru -DisableNameChecking
            # vmtest's own lookup and guest session: its retry while PowerShell Direct isn't
            # ready, and its messages for a VM that's off or an account it refuses.
            $global:vmfilesGuest = & $hm {
                param($checkpoint)
                $name = Find-TaskVM ([pscustomobject]@{ Checkpoint = $checkpoint })
                if (-not $name) { throw "This task doesn't hold a test VM on $env:COMPUTERNAME. Run 'vmtest begin' first." }
                Use-TestVM $name {
                    $vm = Get-TestVM
                    if ($vm.State -ne 'Running') { throw "The test VM '$name' is $($vm.State). Run 'vmtest begin' again." }
                    New-GuestSession
                }
            } $checkpoint
        }
        & $Body $hostSession
    } finally {
        Invoke-Command -Session $hostSession -ScriptBlock {
            if ($global:vmfilesGuest) { Remove-PSSession $global:vmfilesGuest -ErrorAction SilentlyContinue }
        } -ErrorAction SilentlyContinue
        Remove-PSSession $hostSession
    }
}

function Test-RemoteTestVM {
    $m = Import-Module (Get-VmTestModulePath) -Force -PassThru -DisableNameChecking
    try { & $m { Test-ForwardToHost } } finally { Remove-Module $m -ErrorAction SilentlyContinue }
}

function Copy-FromTestVM {
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
    if (-not (Test-RemoteTestVM)) {
        Invoke-WithGuestSession { param($s) Copy-Item $Source -Destination $Destination -FromSession $s -Force }
        return
    }
    # Out of the VM to a folder on the host, then from the host to here.
    Invoke-WithHostGuestSession {
        param($h)
        # Under vmtest's own staging folder, which it sweeps of anything a day old.
        $stage = Invoke-Command -Session $h -ArgumentList $Source -ErrorAction Stop -ScriptBlock {
            param($from)
            $dir = Join-Path $env:TEMP "vmtest-stage\$([guid]::NewGuid())"
            New-Item $dir -ItemType Directory -Force | Out-Null
            try { Copy-Item $from -Destination $dir -FromSession $global:vmfilesGuest -Force -ErrorAction Stop }
            catch { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue; throw }
            Join-Path $dir (Split-Path $from -Leaf)
        }
        try { Copy-Item $stage -Destination $Destination -FromSession $h -Force }
        finally {
            Invoke-Command -Session $h -ArgumentList (Split-Path $stage) -ScriptBlock {
                param($dir) Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Invoke-InTestVM {
    param([Parameter(Mandatory)][string]$Script)
    if (-not (Test-RemoteTestVM)) {
        Invoke-WithGuestSession { param($s) Invoke-Command -Session $s -ScriptBlock ([scriptblock]::Create($Script)) }
        return
    }
    Invoke-WithHostGuestSession {
        param($h)
        Invoke-Command -Session $h -ArgumentList $Script -ScriptBlock {
            param($text) Invoke-Command -Session $global:vmfilesGuest -ScriptBlock ([scriptblock]::Create($text))
        }
    }
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
