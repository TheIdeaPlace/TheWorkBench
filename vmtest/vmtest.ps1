<#
.SYNOPSIS
Test a Windows app inside a Hyper-V test VM (ClaudeTesting, or a copy of it) instead of on this PC.

.DESCRIPTION
A task (a repo and branch) takes a free VM from the pool with 'begin', works in it, and either
'save's it (keep the state for next time) or 'end's it (after merge: back to Clean). Repo and branch
come from the current git folder unless -Repo and -Branch are given. Every command that works inside
a VM uses the one this task holds.

  prepare [-Force] [-VM name]      one time: automatic sign-in, agent, Clean checkpoint
  prepare -Pool [-Force]           make the pool's other VMs from the first VM's Clean
  status                           each VM on every PC vmtest uses: its state, who has it, checkpoints
  where                            which PC and VM this task holds, and where its checkpoints are
  begin [-VM name] [-Wait] [-Force]
                                   take a free VM; carry on, restore this task's checkpoint, or start
                                   from Clean. -Wait waits up to -Timeout seconds when all are busy.
  deploy <path> [-Name n]          copy a build folder or file to C:\vmtest\apps\<name>
  push <file> [-Destination dir]   copy a file into the VM (default C:\vmtest\files)
  launch <exe> [-Arguments "a b"]  start a program in the VM (path inside the VM)
  windows                          list open windows and what has focus
  tree [-Window w] [-Depth n]      UI Automation tree of a window (default: the one in front)
  focused                          the control with keyboard focus
  invoke <control> [-Window w]     press, toggle, select or expand a control (AutomationId or name)
  setvalue <control> <value>       set a text box's value
  focus <control> [-Window w]      move keyboard focus to a control
  keys <keys> [-Window w]          send keys, SendKeys syntax: {TAB} {ENTER} ^s %f +{TAB}
  type <text> [-Window w]          type plain text (text starting with - goes in -Target)
  run <command> [-Timeout s]       run a command line as the signed-in user; default 600 s, at most 3600.
                                   Batch-file rules apply: write % as %%.
  run -ScriptFile <file>           copy a .cmd/.bat/.ps1 from this PC and run it the same way
                                   (anything it leaves running may end with it; use launch for that)
  close [-Window w]                close a window, and say whether it really closed
  shot <out.png> [-FromHost]       picture of the VM's screen
  save                             checkpoint this task, park the VM, release it
  end [-Force -VM name]            after merge: delete this task's checkpoints, its VM back to Clean
  host <computer>                  one time: use the test VMs on another PC as well as this PC's;
                                   checks it, saves your sign-in for it and copies vmtest there
  host <computer> -AddNetwork      use that PC on the network this one is on now, too

-Window is a process id (its main window) or part of a window title (dialogs included).
<control> is an AutomationId or a name; id:<id> matches only an AutomationId (id:-31984 for a
negative one) and name:<name> only a name.
-Elevated runs launch, run and the UI commands with admin rights. Without it, programs run like a
normal user's would; use it for per-machine installs and to drive windows of elevated programs.
'vmtest run' exits with the command's own exit code.

vmtest uses this PC's test VMs and those of every PC set up with 'vmtest host' while this PC is on
a network that PC was set up on. 'begin' takes a free VM on any of them, and the other commands go
to the PC whose VM the task holds, over PowerShell remoting: files named here are copied there, and
pictures are copied back. -HostName <computer> (or 'local') picks the PC. VMTEST_HOSTS lists the PCs
instead (for example local,HOSTNAME), and VMTEST_HOST set to a computer's name means only its VMs.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)]
    [ValidateSet('prepare', 'status', 'begin', 'deploy', 'launch', 'windows', 'tree', 'focused', 'invoke',
        'setvalue', 'focus', 'keys', 'type', 'run', 'push', 'close', 'shot', 'save', 'end', 'host', 'where')]
    [string]$Command,
    [Parameter(Position = 1)][string]$Target,
    [Parameter(Position = 2)][string]$Value,
    [string]$Repo,
    [string]$Branch,
    [string]$Name,
    [string]$Arguments,
    [string]$Window,
    [int]$Depth = 6,
    [ValidateRange(1, 3600)][int]$Timeout = 600,
    [string]$ScriptFile,
    [string]$Destination,
    [string]$VM,
    [switch]$Pool,
    [switch]$Wait,
    [switch]$Elevated,
    [switch]$FromHost,
    [switch]$Force,
    [string]$HostName,
    [switch]$AddNetwork,
    [switch]$Json,
    # Internal: from a 'begin' across several PCs, take only a free VM here.
    [switch]$FreeOnly
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VmTest.psm1') -Force

function Need([string]$what) { if (-not $Target) { throw "'vmtest $Command' needs $what." } }

function Show-Answer($r) {
    if ($r.result) { $r.result }
    if ($r.title) { "Window: $($r.title)" }
    if ($r.pid) { "Process id: $($r.pid)" }
    if ($r.lines) { $r.lines }
    if ($r.focus) { "Focus: $($r.focus)" }
    if ($r.note) { "Note: $($r.note)" }
    if ($r.warning) { "Note: $($r.warning)" }
}

# A request to the agent, from a task that holds the VM.
function Ask([hashtable]$request) {
    Assert-TaskHasVM -Repo $Repo -Branch $Branch
    Show-Answer (Invoke-GuestAgent $request -Elevated:$Elevated)
}

# A failure, as the user (or the PC that sent the command) should see it. From a command spread
# across PCs, it goes in the output, for the PC that asked to show; there 'every VM is busy' exits 75.
function Show-Failure($err) {
    $message = "vmtest $Command failed: $($err.Exception.Message)"
    if ($env:VMTEST_ROUTED) {
        $message
        if ($err.Exception.Data['vmtestSameTask']) { exit 76 }
        if ($err.Exception.Data['vmtestBusy']) { exit 75 }
    } else { Write-Host $message -ForegroundColor Red }
    exit 1
}

# With several PCs' test VMs to use, the command goes to the right one (begin: any with a free VM).
# It runs here, as below, when that's this PC.
if ($Command -ne 'host') {
    try {
        $hosts = @(Get-VmTestHosts)
        if ($hosts.Count) {
            $routed = Invoke-VmTestRouted -Command $Command -Parameters $PSBoundParameters -Hosts $hosts
            if (-not $routed.Here) {
                $routed.Output
                exit $routed.ExitCode
            }
        } else {
            Assert-HostNameHere $HostName
        }
    } catch { Show-Failure $_ }
}

# With VMTEST_HOST set, the test VMs are on another PC: the command runs there, with the same
# parameters, and comes back with the same output and exit code.
if ($Command -ne 'host' -and (Test-ForwardToHost)) {
    try {
        $forwarded = Invoke-VmTestOnHost -Command $Command -Parameters $PSBoundParameters
        $forwarded.Output
        exit $forwarded.ExitCode
    } catch {
        Write-Host "vmtest $Command failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
}

$exitCode = 0
try {
    switch ($Command) {
        'prepare' { Invoke-VmTestPrepare -Repo $Repo -Branch $Branch -VM $VM -Pool:$Pool -Force:$Force }
        'status' { Get-VmTestStatus }
        'where' {
            $w = Get-VmTestWhere -Repo $Repo -Branch $Branch
            if ($Json) { $w | ConvertTo-Json -Compress -Depth 4 }
            elseif ($w.taskError) { throw $w.taskError }
            else {
                $label = if ($env:VMTEST_ON_HOST) { $env:COMPUTERNAME } else { 'this PC' }
                if (-not $w.holds) { "$($w.repo) / $($w.branch) doesn't hold a test VM on $label." }
                Format-Where -Where $w -Label $label
            }
        }
        'begin' { Invoke-VmTestBegin -Repo $Repo -Branch $Branch -VM $VM -Force:$Force -FreeOnly:$FreeOnly -WaitSeconds $(if ($Wait) { $Timeout } else { 0 }) }
        'deploy' { Need 'a folder or file to copy'; Assert-TaskHasVM -Repo $Repo -Branch $Branch; Invoke-VmTestDeploy -Path $Target -Name $Name }
        'push' {
            Need 'a file to copy'
            Assert-TaskHasVM -Repo $Repo -Branch $Branch
            $pushArgs = @{ Path = $Target }
            if ($Destination) { $pushArgs.Destination = $Destination }
            Invoke-VmTestPush @pushArgs
        }
        'launch' { Need 'the program path inside the VM'; Ask @{ op = 'launch'; path = $Target; args = $Arguments } }
        'windows' { Ask @{ op = 'windows' } }
        'tree' { Ask @{ op = 'tree'; window = $Window; depth = $Depth } }
        'focused' { Ask @{ op = 'focused' } }
        'invoke' { Need 'a control (AutomationId or name)'; Ask @{ op = 'invoke'; window = $Window; target = $Target } }
        'setvalue' { Need 'a control (AutomationId or name)'; Ask @{ op = 'setvalue'; window = $Window; target = $Target; value = $Value } }
        'focus' { Need 'a control (AutomationId or name)'; Ask @{ op = 'focus'; window = $Window; target = $Target } }
        'keys' { Need 'keys to send'; Ask @{ op = 'keys'; window = $Window; keys = $Target } }
        'type' { Need 'text to type'; Ask @{ op = 'type'; window = $Window; text = $Target } }
        'close' { Ask @{ op = 'close'; window = $Window } }
        'run' {
            Assert-TaskHasVM -Repo $Repo -Branch $Branch
            $r = Invoke-VmTestRun -Command $Target -ScriptFile $ScriptFile -Timeout $Timeout -Elevated:$Elevated
            Show-Answer $r
            if ($r.result -match '^Exit code (-?\d+)$') { $exitCode = [int]$Matches[1] }
        }
        'shot' {
            Need 'a file to save the picture to'
            # A picture from Hyper-V only looks; one from inside the VM needs the task to hold it.
            if ($FromHost) { Select-VMToLookAt -Repo $Repo -Branch $Branch -VM $VM } else { Assert-TaskHasVM -Repo $Repo -Branch $Branch }
            Invoke-VmTestShot -Out $Target -FromHost:$FromHost
        }
        'save' { Invoke-VmTestSave -Repo $Repo -Branch $Branch }
        'end' { Invoke-VmTestEnd -Repo $Repo -Branch $Branch -VM $VM -Force:$Force }
        'host' {
            Need "the name of the PC that has the test VMs, such as 'vmtest host HOSTNAME'"
            if ($AddNetwork) { Add-VmTestHostNetwork -ComputerName $Target; break }
            # Typed at the console, so it works the same with a screen reader as anything else here.
            try {
                $user = Read-Host "Your account on $Target (for example $Target\you, or MicrosoftAccount\you@example.com)"
                $password = Read-Host "The password for $user" -AsSecureString
            } catch { throw "It asks for your account on $Target, so run it without -NonInteractive." }
            if (-not $user) { throw 'No account was given, so nothing was saved.' }
            Install-VmTestHost -ComputerName $Target -Credential (New-Object System.Management.Automation.PSCredential($user, $password))
        }
    }
} catch { Show-Failure $_ }
exit $exitCode
