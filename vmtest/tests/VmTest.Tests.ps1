# Pester 3.4 tests (the version that ships with Windows). Run: Invoke-Pester vmtest\tests
# These need no VM. Hyper-V is replaced by a small fake that keeps VMs and checkpoints in memory, and
# the VM names point at ones that don't exist, so a mistake here can never reach a real test VM.
$root = Split-Path $PSScriptRoot
$env:VMTEST_VM = 'vmtest-pester-no-such-vm'
$env:VMTEST_POOL = '1'
# Run as if outside Claude Code, so locks don't name the session running the tests. The
# 'Claude sessions' tests set these themselves.
Remove-Item Env:\CLAUDE_PID, Env:\CLAUDE_CODE_SESSION_ID -ErrorAction SilentlyContinue
# And as if on this PC, even where VMTEST_HOST is set. 'another PC' sets these itself.
Remove-Item Env:\VMTEST_HOST, Env:\VMTEST_ON_HOST, Env:\VMTEST_REMOTE_HOLDER, Env:\VMTEST_REMOTE_LIVE, Env:\VMTEST_REMOTE_FOLDER,
    Env:\VMTEST_HOST_PATH -ErrorAction SilentlyContinue
$second = "$env:VMTEST_VM-2"
Import-Module (Join-Path $root 'VmTest.psm1') -Force

# ---- the fake Hyper-V --------------------------------------------------------------------------
# $global:Fake.VMs holds each fake VM's state and checkpoints. $global:Fake.Checkpoints, .Calls and
# FakeVM are the first VM's, which is all the one-VM tests look at.
function New-FakeVM([string[]]$Checkpoints = @('Clean'), [string]$State = 'Saved', [string]$CheckpointType = 'Standard', [string]$Notes = '') {
    @{ Checkpoints = [System.Collections.Generic.List[string]]@($Checkpoints); State = $State; CheckpointType = $CheckpointType
        Calls = New-Object System.Collections.Generic.List[string]; Notes = $Notes }
}
function FakeVM([string]$Name = $env:VMTEST_VM) { $global:Fake.VMs[$Name] }
function Use-FakePool {
    param([Parameter(Mandatory)][hashtable]$VMs, [double]$FreeGB = 64)
    $global:Fake = @{ VMs = $VMs; FreeMemory = [int64]($FreeGB * 1GB); Calls = New-Object System.Collections.Generic.List[string] }
    if ($VMs[$env:VMTEST_VM]) { $global:Fake.Checkpoints = $VMs[$env:VMTEST_VM].Checkpoints; $global:Fake.Calls = $VMs[$env:VMTEST_VM].Calls }
    Mock -ModuleName VmTest Get-VM {
        $names = if ($Name) { $Name } else { $global:Fake.VMs.Keys }
        foreach ($n in $names) {
            $v = $global:Fake.VMs[$n]
            if ($v) { [pscustomobject]@{ Name = $n; State = $v.State; CheckpointType = $v.CheckpointType; MemoryStartup = 4GB; Notes = $v.Notes; Id = $n } }
        }
    }
    Mock -ModuleName VmTest Get-VMSnapshot {
        $v = $global:Fake.VMs["$VMName"]
        if ($v) { $v.Checkpoints | Where-Object { -not $Name -or $Name -contains $_ } | ForEach-Object { [pscustomobject]@{ Name = $_; Id = "id-$_" } } }
    }
    Mock -ModuleName VmTest Checkpoint-VM { $v = $global:Fake.VMs["$Name"]; $v.Calls.Add("checkpoint $SnapshotName"); $v.Checkpoints.Add($SnapshotName) }
    Mock -ModuleName VmTest Rename-VMSnapshot {
        $v = $global:Fake.VMs["$VMName"]; $v.Calls.Add("rename $Name -> $NewName")
        $i = $v.Checkpoints.IndexOf($Name); $v.Checkpoints[$i] = $NewName
    }
    Mock -ModuleName VmTest Remove-VMSnapshot { $v = $global:Fake.VMs["$VMName"]; $v.Calls.Add("remove $Name"); [void]$v.Checkpoints.Remove($Name) }
    Mock -ModuleName VmTest Restore-VMSnapshot { $v = $global:Fake.VMs["$VMName"]; $v.Calls.Add("restore $Name"); $v.State = 'Saved' }
    Mock -ModuleName VmTest Stop-VM { $v = $global:Fake.VMs["$Name"]; $v.Calls.Add('turn off'); $v.State = 'Off' }
    Mock -ModuleName VmTest Start-VM { $v = $global:Fake.VMs["$Name"]; $v.Calls.Add('start'); $v.State = 'Running' }
    Mock -ModuleName VmTest Resume-VM { $v = $global:Fake.VMs["$Name"]; $v.Calls.Add('resume'); $v.State = 'Running' }
    Mock -ModuleName VmTest Save-VM { $v = $global:Fake.VMs["$Name"]; $v.Calls.Add('save vm'); $v.State = 'Saved' }
    Mock -ModuleName VmTest Set-VM { $v = $global:Fake.VMs["$Name"]; $v.Calls.Add("set $CheckpointType") }
    Mock -ModuleName VmTest Get-HostFreeMemoryBytes { $global:Fake.FreeMemory }
    # The guest side: signed in, agent installs fine.
    Mock -ModuleName VmTest Wait-GuestSignedIn {}
    Mock -ModuleName VmTest New-GuestSession { 'session' }
    Mock -ModuleName VmTest Remove-PSSession {}
    Mock -ModuleName VmTest Install-GuestAgent {}
}
function Use-FakeHyperV {
    param([string[]]$Checkpoints = @('Clean'), [string]$State = 'Running', [string]$CheckpointType = 'Standard')
    Use-FakePool @{ $env:VMTEST_VM = (New-FakeVM -Checkpoints $Checkpoints -State $State -CheckpointType $CheckpointType) }
}
# Every call made to any fake VM, as "<vm>: <call>".
function Get-AllCalls { foreach ($k in $global:Fake.VMs.Keys) { foreach ($c in $global:Fake.VMs[$k].Calls) { "${k}: $c" } } }

Describe 'scripts' {
    $files = Get-ChildItem $root -Recurse -Include *.ps1, *.psm1
    foreach ($file in $files) {
        It "$($file.Name) parses" {
            $errors = $null
            [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$null, [ref]$errors) | Out-Null
            @($errors).Count | Should Be 0
        }
        It "$($file.Name) is plain ASCII (Windows PowerShell reads files without a BOM as ANSI)" {
            $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
            @($bytes | Where-Object { $_ -gt 127 }).Count | Should Be 0
        }
    }
    It "the agent's C# compiles (Windows PowerShell 5.1, C# 5)" {
        # Each Add-Type here-string, compiled in a fresh PowerShell so the types don't stay loaded here.
        $agent = Get-Content (Join-Path $root 'guest\agent.ps1') -Raw
        $blocks = [regex]::Matches($agent, "Add-Type(?<refs> -ReferencedAssemblies \w+)? -TypeDefinition @'\r?\n(?<code>.*?)\r?\n'@", 'Singleline')
        $blocks.Count | Should Be 2
        foreach ($b in $blocks) {
            $file = Join-Path $TestDrive "block$([guid]::NewGuid()).cs"
            [System.IO.File]::WriteAllText($file, $b.Groups['code'].Value)
            $refs = if ($b.Groups['refs'].Success) { $b.Groups['refs'].Value } else { '' }
            $out = & powershell.exe -NoProfile -NonInteractive -Command "try { Add-Type$refs -TypeDefinition ([IO.File]::ReadAllText('$file')) -ErrorAction Stop; 'compiled' } catch { `$_.Exception.Message }"
            ($out -join ' ') | Should Be 'compiled'
        }
    }
    It 'the agent refuses to run anywhere but a VM' {
        (Get-Content (Join-Path $root 'guest\agent.ps1') -Raw) | Should Match "Model -ne 'Virtual Machine'"
    }
}

Describe 'checkpoint names' {
    It 'start with a readable repo and branch' {
        ConvertTo-TaskCheckpointName 'QuickMail' 'main' | Should Match '^task QuickMail main [0-9a-f]{6}$'
    }
    It 'keep branches apart that read the same' {
        (ConvertTo-TaskCheckpointName 'r' 'feature/a') | Should Not Be (ConvertTo-TaskCheckpointName 'r' 'feature-a')
    }
    It 'keep long branches apart that share their first 100 characters' {
        $long = 'x' * 120
        (ConvertTo-TaskCheckpointName 'r' "$long-one") | Should Not Be (ConvertTo-TaskCheckpointName 'r' "$long-two")
    }
    It 'stay short enough for the temporary names' {
        ("$(ConvertTo-TaskCheckpointName 'r' ('x' * 300)) (new)").Length -le 100 | Should Be $true
    }
    It 'are the same every time' {
        ConvertTo-TaskCheckpointName 'r' 'b' | Should Be (ConvertTo-TaskCheckpointName 'r' 'b')
    }
    It 'refuse names with nothing usable in them' {
        { ConvertTo-TaskCheckpointName 'r' '///' } | Should Throw
    }
}

Describe 'Resolve-Task' {
    It 'uses the repo and branch it is given' {
        $t = Resolve-Task -Repo 'QuickMail' -Branch 'fix/x'
        $t.Repo | Should Be 'QuickMail'
        $t.Branch | Should Be 'fix/x'
        $t.Checkpoint | Should Be (ConvertTo-TaskCheckpointName 'QuickMail' 'fix/x')
        $t.Legacy | Should Be 'task QuickMail fix-x'
    }
    It 'reads them from a git folder' {
        $branch = git -C $root branch --show-current
        if (-not $branch) { Set-TestInconclusive 'detached HEAD (as in CI)'; return }
        $t = Resolve-Task -Path $root
        $t.Repo | Should Be 'TheWorkBench'
        $t.Branch | Should Be $branch
    }
    It 'says what to do outside a git folder' {
        { Resolve-Task -Path $env:SystemRoot } | Should Throw 'give -Repo and -Branch'
    }
}

Describe 'the lock' {
    $env:VMTEST_STATE = Join-Path $TestDrive 'state'
    $a = Resolve-Task -Repo 'A' -Branch 'one'
    $b = Resolve-Task -Repo 'B' -Branch 'two'

    It 'starts free' {
        Clear-VmTestLock
        Get-VmTestLock | Should BeNullOrEmpty
    }
    It 'records who holds it' {
        Set-VmTestLock -Task $a
        (Get-VmTestLock).repo | Should Be 'A'
        (Get-VmTestLock).checkpoint | Should Be $a.Checkpoint
    }
    It 'lets the same task take it again' {
        { Set-VmTestLock -Task $a } | Should Not Throw
    }
    It 'refuses another task' {
        { Set-VmTestLock -Task $b } | Should Throw "'vmtest-pester-no-such-vm' is in use by A / one"
        (Get-VmTestLock).repo | Should Be 'A'
    }
    It 'gives way to -Force' {
        Set-VmTestLock -Task $b -Force
        (Get-VmTestLock).repo | Should Be 'B'
    }
    It 'checks the holder' {
        { Assert-LockedBy -Task $b } | Should Not Throw
        { Assert-LockedBy -Task $a } | Should Throw "A / one doesn't hold a test VM"
    }
    It 'can be released' {
        Clear-VmTestLock
        Get-VmTestLock | Should BeNullOrEmpty
        { Assert-LockedBy -Task $a } | Should Throw "Run 'vmtest begin' first"
    }
    It 'says which file to delete when it is damaged' {
        New-Item -ItemType Directory $env:VMTEST_STATE -Force | Out-Null
        Set-Content (Join-Path $env:VMTEST_STATE 'lock.json') '{ not json'
        { Get-VmTestLock } | Should Throw 'is damaged'
        Clear-VmTestLock
    }
    Remove-Item Env:\VMTEST_STATE
}

Describe 'saving a checkpoint' {
    It 'makes the new one before dropping the old one' {
        Use-FakeHyperV -Checkpoints 'Clean', 'task r b'
        InModuleScope VmTest { Save-TestCheckpoint 'task r b' }
        $global:Fake.Checkpoints -contains 'task r b' | Should Be $true
        $global:Fake.Checkpoints.Count | Should Be 2
        ($global:Fake.Calls -join '; ') | Should Be 'checkpoint task r b (new); rename task r b -> task r b (old); rename task r b (new) -> task r b; remove task r b (old)'
    }
    It 'works the first time, with no old one' {
        Use-FakeHyperV -Checkpoints 'Clean'
        InModuleScope VmTest { Save-TestCheckpoint 'task r b' }
        @($global:Fake.Checkpoints) -join ',' | Should Be 'Clean,task r b'
    }
    It 'recovers a save that stopped after making the new one' {
        Use-FakeHyperV -Checkpoints 'Clean', 'task r b (new)'
        InModuleScope VmTest { Repair-TaskCheckpoint 'task r b' }
        @($global:Fake.Checkpoints) -join ',' | Should Be 'Clean,task r b'
    }
    It 'recovers a save that stopped after moving the old one aside' {
        Use-FakeHyperV -Checkpoints 'Clean', 'task r b (old)', 'task r b (new)'
        InModuleScope VmTest { Repair-TaskCheckpoint 'task r b' }
        @($global:Fake.Checkpoints) -join ',' | Should Be 'Clean,task r b'
        $global:Fake.Calls[0] | Should Be 'rename task r b (new) -> task r b'
    }
    It 'never deletes a leftover copy while the real one is missing' {
        Use-FakeHyperV -Checkpoints 'Clean', 'task r b (old)'
        InModuleScope VmTest { Repair-TaskCheckpoint 'task r b' }
        @($global:Fake.Checkpoints) -join ',' | Should Be 'Clean,task r b'
    }
    It "renames a first-version checkpoint to today's name" {
        Use-FakeHyperV -Checkpoints 'Clean', 'task QuickMail main'
        $t = Resolve-Task -Repo 'QuickMail' -Branch 'main'
        $global:PesterTask = $t
        InModuleScope VmTest { Repair-TaskCheckpoint $global:PesterTask.Checkpoint $global:PesterTask.Legacy }
        $global:Fake.Checkpoints -contains $t.Checkpoint | Should Be $true
        $global:Fake.Checkpoints -contains 'task QuickMail main' | Should Be $false
    }
}

Describe 'begin' {
    $env:VMTEST_STATE = Join-Path $TestDrive 'begin'

    It "starts a new task from Clean" {
        Use-FakeHyperV -Checkpoints 'Clean' -State 'Saved'
        Clear-VmTestLock
        Invoke-VmTestBegin -Repo 'r' -Branch 'b' | Should Match 'started from Clean'
        $global:Fake.Calls -contains 'restore Clean' | Should Be $true
        (Get-VmTestLock).repo | Should Be 'r'
    }
    It "restores the task's own checkpoint when it has one" {
        $t = Resolve-Task -Repo 'r' -Branch 'b'
        Use-FakeHyperV -Checkpoints 'Clean', $t.Checkpoint -State 'Saved'
        Clear-VmTestLock
        Invoke-VmTestBegin -Repo 'r' -Branch 'b' | Should Match "this task's checkpoint"
        $global:Fake.Calls -contains "restore $($t.Checkpoint)" | Should Be $true
    }
    foreach ($state in 'Running', 'Saved', 'Paused', 'Off') {
        It "never restores over the task's own VM when it is $state" {
            Use-FakeHyperV -Checkpoints 'Clean' -State $state
            Set-VmTestLock -Task (Resolve-Task -Repo 'r' -Branch 'b') -Force
            Invoke-VmTestBegin -Repo 'r' -Branch 'b' | Out-Null
            @($global:Fake.Calls | Where-Object { $_ -like 'restore*' }).Count | Should Be 0
            (FakeVM).State | Should Be 'Running'
        }
    }
    It 'turns on Standard checkpoints so open programs survive' {
        Use-FakeHyperV -Checkpoints 'Clean' -CheckpointType 'Production'
        Clear-VmTestLock
        Invoke-VmTestBegin -Repo 'r' -Branch 'b' | Out-Null
        $global:Fake.Calls -contains 'set Standard' | Should Be $true
    }
    It 'refuses while another task has the VM, and leaves it alone' {
        Use-FakeHyperV -Checkpoints 'Clean'
        Set-VmTestLock -Task (Resolve-Task -Repo 'Other' -Branch 'work') -Force
        { Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' } | Should Throw 'is in use by Other / work'
        $global:Fake.Calls.Count | Should Be 0
        (Get-VmTestLock).repo | Should Be 'Other'
    }
    It 'says how to build the VM' {
        Mock -ModuleName VmTest Get-VM { $null }
        Clear-VmTestLock
        { Invoke-VmTestBegin -Repo 'r' -Branch 'b' } | Should Throw 'Build one with Hyper-V Manage'
    }
    It 'says how to make Clean, and takes no lock' {
        Use-FakeHyperV -Checkpoints @()
        Clear-VmTestLock
        { Invoke-VmTestBegin -Repo 'r' -Branch 'b' } | Should Throw "Run 'vmtest prepare'"
        Get-VmTestLock | Should BeNullOrEmpty
    }
    Clear-VmTestLock
    Remove-Item Env:\VMTEST_STATE
}

Describe 'save and end' {
    $env:VMTEST_STATE = Join-Path $TestDrive 'saveend'
    $mine = Resolve-Task -Repo 'Mine' -Branch 'x'

    It "save refuses when another task has the VM" {
        Use-FakeHyperV
        Set-VmTestLock -Task (Resolve-Task -Repo 'Other' -Branch 'work') -Force
        { Invoke-VmTestSave -Repo 'Mine' -Branch 'x' } | Should Throw "Mine / x doesn't hold a test VM"
        $global:Fake.Calls.Count | Should Be 0
    }
    It 'save keeps the state, parks the VM and frees it' {
        Use-FakeHyperV
        Set-VmTestLock -Task $mine -Force
        Invoke-VmTestSave -Repo 'Mine' -Branch 'x' | Out-Null
        $global:Fake.Checkpoints -contains $mine.Checkpoint | Should Be $true
        (FakeVM).State | Should Be 'Saved'
        Get-VmTestLock | Should BeNullOrEmpty
    }
    It "end from another task only drops its own checkpoint" {
        Use-FakeHyperV -Checkpoints 'Clean', $mine.Checkpoint
        Set-VmTestLock -Task (Resolve-Task -Repo 'Other' -Branch 'work') -Force
        Invoke-VmTestEnd -Repo 'Mine' -Branch 'x' | Should Match 'held no test VM, so none was reset'
        $global:Fake.Checkpoints -contains $mine.Checkpoint | Should Be $false
        @($global:Fake.Calls | Where-Object { $_ -like 'restore*' }).Count | Should Be 0
        (Get-VmTestLock).repo | Should Be 'Other'
    }
    It 'end by the holder drops its checkpoint, goes back to Clean and frees the VM' {
        Use-FakeHyperV -Checkpoints 'Clean', $mine.Checkpoint
        Set-VmTestLock -Task $mine -Force
        Invoke-VmTestEnd -Repo 'Mine' -Branch 'x' | Should Match "back to 'Clean' and freed it"
        $global:Fake.Checkpoints -contains $mine.Checkpoint | Should Be $false
        $global:Fake.Calls -contains 'restore Clean' | Should Be $true
        Get-VmTestLock | Should BeNullOrEmpty
    }
    Clear-VmTestLock
    Remove-Item Env:\VMTEST_STATE
}

Describe 'commands inside the VM' {
    $env:VMTEST_STATE = Join-Path $TestDrive 'inside'
    It 'need the task to hold the VM' {
        Use-FakeHyperV
        Clear-VmTestLock
        { Assert-TaskHasVM -Repo 'r' -Branch 'b' } | Should Throw "Run 'vmtest begin' first"
    }
    It 'need the VM to be running' {
        Use-FakeHyperV -State 'Saved'
        Set-VmTestLock -Task (Resolve-Task -Repo 'r' -Branch 'b') -Force
        { Assert-TaskHasVM -Repo 'r' -Branch 'b' } | Should Throw 'is Saved'
    }
    It 'run wants a command line or a script, not both and not neither' {
        { Invoke-VmTestRun } | Should Throw 'needs a command line'
        { Invoke-VmTestRun -Command 'x' -ScriptFile 'y.ps1' } | Should Throw 'not both'
    }
    It 'run keeps -Timeout inside what the agent can wait for' {
        { Invoke-VmTestRun -Command 'x' -Timeout 5400 } | Should Throw 'between 1 and 3600'
    }
    It 'run only takes .cmd, .bat or .ps1 files' {
        $f = Join-Path $TestDrive 'x.txt'; Set-Content $f 'x'
        { Invoke-VmTestRun -ScriptFile $f } | Should Throw '.cmd, .bat or .ps1'
    }
    Clear-VmTestLock
    Remove-Item Env:\VMTEST_STATE
}

Describe 'prepare' {
    $env:VMTEST_STATE = Join-Path $TestDrive 'prepare'
    It 'refuses while another task has the VM, because it reboots it' {
        Use-FakeHyperV
        Set-VmTestLock -Task (Resolve-Task -Repo 'Other' -Branch 'work') -Force
        { Invoke-VmTestPrepare -Repo 'r' -Branch 'b' } | Should Throw 'Prepare reboots it'
        $global:Fake.Calls.Count | Should Be 0
    }
    It 'refuses to replace Clean without -Force' {
        Use-FakeHyperV
        Clear-VmTestLock
        { Invoke-VmTestPrepare } | Should Throw 'already has a Clean checkpoint'
    }
    Clear-VmTestLock
    Remove-Item Env:\VMTEST_STATE
}

Describe 'the pool' {
    $env:VMTEST_STATE = Join-Path $TestDrive 'pool'
    $first = $env:VMTEST_VM
    $mine = Resolve-Task -Repo 'Mine' -Branch 'x'
    $other = Resolve-Task -Repo 'Other' -Branch 'work'
    $third = Resolve-Task -Repo 'Third' -Branch 'y'
    function Reset-Locks { foreach ($n in $first, $second) { Clear-VmTestLock -VMName $n } }
    # Make a task's lock on a VM look as if it was taken this many minutes ago.
    function Set-HeldSince([string]$VMName, $Task, [int]$MinutesAgo) {
        Set-VmTestLock -Task $Task -VMName $VMName -Force
        $p = Join-Path $env:VMTEST_STATE $(if ($VMName -eq $first) { 'lock.json' } else { "lock-$VMName.json" })
        $l = Get-Content $p -Raw | ConvertFrom-Json
        $l.since = (Get-Date).AddMinutes(-$MinutesAgo).ToString('s')
        $l | ConvertTo-Json | Set-Content $p
    }

    Context 'naming' {
        It 'is the first VM and numbered copies for a count' {
            $env:VMTEST_POOL = '3'
            (Get-PoolNames) -join ',' | Should Be "$first,$first-2,$first-3"
            $env:VMTEST_POOL = '1'
        }
        It 'takes a list of names, the first one first' {
            $env:VMTEST_POOL = 'A, B;C'
            (Get-PoolNames) -join ',' | Should Be 'A,B,C'
            $env:VMTEST_POOL = '1'
        }
        It 'never has fewer than one' {
            $env:VMTEST_POOL = '0'
            (Get-PoolNames) -join ',' | Should Be $first
            $env:VMTEST_POOL = '1'
        }
        foreach ($case in @(@(16, 8, 1), @(24, 8, 2), @(32, 12, 3), @(64, 32, 3), @(64, 4, 1))) {
            It "defaults to $($case[2]) on a PC with $($case[0]) GB and $($case[1]) processors" {
                $global:PesterCase = $case
                Mock -ModuleName VmTest Get-CimInstance { [pscustomobject]@{ TotalPhysicalMemory = $global:PesterCase[0] * 1GB; NumberOfLogicalProcessors = $global:PesterCase[1] } }
                InModuleScope VmTest { Get-DefaultPoolSize } | Should Be $case[2]
            }
        }
        It "keeps the first VM's lock in lock.json, so a task that held it before the pool still holds it" {
            New-Item -ItemType Directory $env:VMTEST_STATE -Force | Out-Null
            @{ repo = 'Mine'; branch = 'x'; checkpoint = $mine.Checkpoint; since = (Get-Date).ToString('s') } | ConvertTo-Json |
                Set-Content (Join-Path $env:VMTEST_STATE 'lock.json')
            $env:VMTEST_POOL = '2'
            Find-TaskVM $mine | Should Be $first
            Clear-VmTestLock -VMName $first
        }
        It 'gives Windows in a copy a name it accepts' {
            InModuleScope VmTest { ConvertTo-ComputerName 'ClaudeTesting-2' } | Should Be 'ClaudeTesting-2'
            (InModuleScope VmTest { ConvertTo-ComputerName 'A very long VM name 2' }).Length -le 15 | Should Be $true
        }
    }

    $env:VMTEST_POOL = '2'
    function Use-TwoVMs([string[]]$FirstCheckpoints = @('Clean'), [string[]]$SecondCheckpoints = @('Clean'), [double]$FreeGB = 64) {
        Use-FakePool -FreeGB $FreeGB @{ $first = (New-FakeVM -Checkpoints $FirstCheckpoints -State 'Running'); $second = (New-FakeVM -Checkpoints $SecondCheckpoints) }
    }

    It 'gives a second task the free VM while the first is in use' {
        Use-TwoVMs; Reset-Locks
        Set-HeldSince $first $other 5
        Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Should Match "'$second' is ready for Mine / x, started from Clean"
        Find-TaskVM $mine | Should Be $second
        (Get-VmTestLock -VMName $first).repo | Should Be 'Other'
        @(Get-AllCalls | Where-Object { $_ -like "${first}:*" }).Count | Should Be 0
    }
    It "prefers the free VM that has this task's checkpoint" {
        Use-TwoVMs -SecondCheckpoints 'Clean', $mine.Checkpoint; Reset-Locks
        Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Should Match "'$second' .* this task's checkpoint"
        (FakeVM $second).Calls -contains "restore $($mine.Checkpoint)" | Should Be $true
    }
    It "starts from Clean elsewhere when its checkpoint's VM is busy, says so, and save drops the older copy" {
        Use-TwoVMs -FirstCheckpoints 'Clean', $mine.Checkpoint; Reset-Locks
        Set-HeldSince $first $other 5
        Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Should Match "checkpoint is on '$first', which is busy, so this started from Clean"
        Invoke-VmTestSave -Repo 'Mine' -Branch 'x' | Out-Null
        (FakeVM $second).Checkpoints -contains $mine.Checkpoint | Should Be $true
        (FakeVM $first).Checkpoints -contains $mine.Checkpoint | Should Be $false
        (Get-VmTestLock -VMName $first).repo | Should Be 'Other'
    }
    It 'says who holds every VM when all are busy, as an error -Wait waits out, and touches nothing' {
        Use-TwoVMs; Reset-Locks
        Set-HeldSince $first $other 5
        Set-HeldSince $second $third 10
        $err = $null
        try { Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' } catch { $err = $_ }
        $err.Exception.Message | Should Match "'$first' is in use by Other / work"
        $err.Exception.Message | Should Match "'$second' is in use by Third / y .* last used 10 minutes ago"
        $err.Exception.Message | Should Match "If '$second' is still idle at"
        $err.Exception.Data['vmtestBusy'] | Should Be $true
        @(Get-AllCalls).Count | Should Be 0
    }
    It 'saves an idle task for it and takes its VM when none is free' {
        Use-TwoVMs; Reset-Locks
        Set-HeldSince $first $other 5
        Set-HeldSince $second $third 200
        Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Should Match 'Third / y had held it unused for 3 hours 20 minutes, so its state was saved first'
        (FakeVM $second).Checkpoints -contains $third.Checkpoint | Should Be $true
        Find-TaskVM $mine | Should Be $second
        Find-TaskVM $third | Should BeNullOrEmpty
        (Get-VmTestLock -VMName $first).repo | Should Be 'Other'
    }
    It 'counts a recent command, not just when the VM was taken' {
        Reset-Locks
        Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running'); $second = (New-FakeVM -State 'Running') }
        Set-HeldSince $first $other 300
        Set-HeldSince $second $third 300
        Assert-TaskHasVM -Repo 'Other' -Branch 'work'
        Assert-TaskHasVM -Repo 'Third' -Branch 'y'
        { Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' } | Should Throw 'last used under a minute ago'
    }
    It 'ignores a note of use from a task that no longer holds the VM' {
        Use-TwoVMs; Reset-Locks
        Set-HeldSince $second $third 300
        InModuleScope VmTest { Set-TaskActivity -Task (Resolve-Task -Repo 'Gone' -Branch 'z') -VMName "$env:VMTEST_VM-2" }
        InModuleScope VmTest { Test-LockStale (Get-VmTestLock "$env:VMTEST_VM-2") } | Should Be $true
    }
    It 'never takes an idle VM when VMTEST_STALE_MINUTES is 0' {
        Use-TwoVMs; Reset-Locks
        Set-HeldSince $first $other 500
        Set-HeldSince $second $third 500
        $env:VMTEST_STALE_MINUTES = '0'
        try { { Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' } | Should Throw 'is in use by' }
        finally { Remove-Item Env:\VMTEST_STALE_MINUTES }
    }
    It "takes an idle VM that has this task's checkpoint before a free one without it" {
        Use-TwoVMs -FirstCheckpoints 'Clean', $mine.Checkpoint; Reset-Locks
        Set-HeldSince $first $other 300
        Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Should Match "'$first' .* this task's checkpoint"
        (FakeVM $first).Checkpoints -contains $other.Checkpoint | Should Be $true
    }
    It "won't start a VM without the memory for it, and says so" {
        Use-TwoVMs -FreeGB 5; Reset-Locks
        Set-HeldSince $first $other 5
        $err = $null
        try { Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' } catch { $err = $_ }
        $err.Exception.Message | Should Match "'$second' is free, but this PC has 5.0 GB of memory free and starting one needs 8.0 GB"
        $err.Exception.Data['vmtestBusy'] | Should Be $true
        @(Get-AllCalls).Count | Should Be 0
    }
    It 'with -Force takes the longest-idle VM and says what was lost' {
        Use-TwoVMs; Reset-Locks
        Set-HeldSince $first $other 5
        Set-HeldSince $second $third 30
        Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' -Force | Should Match 'taken from Third / y with -Force'
        Find-TaskVM $mine | Should Be $second
    }
    It 'gives the VM asked for with -VM, and refuses one that is not in the pool' {
        Use-TwoVMs; Reset-Locks
        Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' -VM $second | Should Match "'$second' is ready"
        Reset-Locks
        { Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' -VM 'nope' } | Should Throw "'nope' isn't one of the test VMs"
    }
    It 'skips a VM that has no Clean yet (still being made)' {
        Use-TwoVMs -SecondCheckpoints @(); Reset-Locks
        Set-HeldSince $first $other 5
        { Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' } | Should Throw "'$first' is in use by Other / work"
    }
    It 'carries on with the VM this task holds, whichever it is' {
        Use-TwoVMs; Reset-Locks
        Set-HeldSince $second $mine 5
        Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Should Match "'$second' .* where this task left it"
        @(Get-AllCalls | Where-Object { $_ -like '*restore*' }).Count | Should Be 0
    }
    It 'waits with -WaitSeconds until a VM is free' {
        Use-TwoVMs; Reset-Locks
        Set-HeldSince $first $other 5
        Set-HeldSince $second $third 5
        Mock -ModuleName VmTest Start-Sleep { Clear-VmTestLock -VMName "$env:VMTEST_VM-2" }
        Mock -ModuleName VmTest Write-Host {}
        Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' -WaitSeconds 60 | Should Match "'$second' is ready"
        Assert-MockCalled -ModuleName VmTest Start-Sleep -Times 1 -Exactly
    }
    It 'commands work on the VM the task holds' {
        Reset-Locks
        Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running'); $second = (New-FakeVM -State 'Running') }
        Set-HeldSince $second $mine 5
        Assert-TaskHasVM -Repo 'Mine' -Branch 'x'
        InModuleScope VmTest { Get-VMName } | Should Be $second
    }
    It 'end drops the checkpoint from every VM but resets only the one this task holds' {
        Use-TwoVMs -FirstCheckpoints 'Clean', $mine.Checkpoint -SecondCheckpoints 'Clean', $mine.Checkpoint; Reset-Locks
        Set-HeldSince $first $other 5
        Set-HeldSince $second $mine 5
        Invoke-VmTestEnd -Repo 'Mine' -Branch 'x' | Should Match "Put '$second' back to 'Clean'"
        (FakeVM $first).Checkpoints -contains $mine.Checkpoint | Should Be $false
        (FakeVM $second).Checkpoints -contains $mine.Checkpoint | Should Be $false
        @((FakeVM $first).Calls | Where-Object { $_ -like 'restore*' }).Count | Should Be 0
        (Get-VmTestLock -VMName $first).repo | Should Be 'Other'
        Get-VmTestLock -VMName $second | Should BeNullOrEmpty
    }
    It 'status lists each VM, who holds it, and the ones not made yet' {
        $env:VMTEST_POOL = '3'
        Use-TwoVMs; Reset-Locks
        Set-HeldSince $second $third 300
        $s = (Get-VmTestStatus) -join "`n"
        $s | Should Match "'$first': Running, free"
        $s | Should Match "'$second': Saved, in use by Third / y .* idle long enough"
        $s | Should Match "'$first-3': not made yet"
        $env:VMTEST_POOL = '2'
    }
    It "says when -VM starts from Clean while this task's checkpoint is on another VM" {
        Use-TwoVMs -FirstCheckpoints 'Clean', $mine.Checkpoint; Reset-Locks
        Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' -VM $second | Should Match "checkpoint is on '$first'"
    }
    It "never takes a lock from before the pool for idle, until its task's first command notes a use" {
        Use-TwoVMs; Reset-Locks
        New-Item -ItemType Directory $env:VMTEST_STATE -Force | Out-Null
        @{ repo = 'Other'; branch = 'work'; checkpoint = $other.Checkpoint; since = (Get-Date).AddHours(-5).ToString('s') } | ConvertTo-Json |
            Set-Content (Join-Path $env:VMTEST_STATE 'lock.json')
        InModuleScope VmTest { Test-LockStale (Get-VmTestLock $env:VMTEST_VM) } | Should Be $false
    }
    It 'end -VM refuses a VM outside the pool, and -Force names the task that lost its state' {
        Use-TwoVMs; Reset-Locks
        { Invoke-VmTestEnd -Repo 'Mine' -Branch 'x' -VM 'SomeoneElsesVM' -Force } | Should Throw "isn't one of the pool's test VMs"
        Set-HeldSince $second $other 5
        Invoke-VmTestEnd -Repo 'Mine' -Branch 'x' -VM $second -Force | Should Match 'in use by Other / work, whose unsaved state is lost'
    }
    Context 'Claude sessions' {
        # Fake Claude processes: process id -> start time. Process 1, 2 and 4 are open; 3 has closed.
        $global:FakeProcs = @{ '1' = '2026-10-22T09:00:00'; '2' = '2026-10-22T10:00:00'; '4' = '2026-10-22T11:00:00' }
        Mock -ModuleName VmTest Get-ProcessStartTime { $global:FakeProcs["$Id"] }
        function Use-Session([int]$Id) { $env:CLAUDE_PID = "$Id"; $env:CLAUDE_CODE_SESSION_ID = "session-$Id" }
        function Set-HeldBy([int]$Id, [string]$VMName, $Task, [int]$MinutesAgo) {
            $global:FakeProcs['3'] = '2026-10-22T08:00:00'   # open while it takes the lock, closed after
            Use-Session $Id
            Set-HeldSince $VMName $Task $MinutesAgo
            $global:FakeProcs.Remove('3')
        }
        try {

        It 'a lock records the session that took it' {
            Use-TwoVMs; Reset-Locks
            Use-Session 1
            Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Out-Null
            $l = Get-VmTestLock -VMName $first
            $l.pid | Should Be 1
            $l.started | Should Be '2026-10-22T09:00:00'
            $l.session | Should Be 'session-1'
        }
        It "never takes an open session's VM after two hours idle, and says when it would" {
            Use-TwoVMs; Reset-Locks
            Set-HeldBy 1 $first $other 300
            Set-HeldBy 2 $second $third 200
            Use-Session 4
            $err = $null
            try { Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' } catch { $err = $_ }
            $err.Exception.Data['vmtestBusy'] | Should Be $true
            $err.Exception.Message | Should Match "If '$first' is still idle at"
            @(Get-AllCalls).Count | Should Be 0
        }
        It "takes an open session's VM once it has been idle for VMTEST_OPEN_STALE_MINUTES" {
            Use-TwoVMs; Reset-Locks
            Set-HeldBy 1 $first $other 5
            Set-HeldBy 2 $second $third 500
            Use-Session 4
            Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Should Match 'Third / y had held it unused for 8 hours 20 minutes'
        }
        It 'never takes an open session''s VM when VMTEST_OPEN_STALE_MINUTES is 0' {
            Use-TwoVMs; Reset-Locks
            Set-HeldBy 1 $first $other 5000
            Set-HeldBy 2 $second $third 5000
            $env:VMTEST_OPEN_STALE_MINUTES = '0'
            Use-Session 4
            try { { Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' } | Should Throw 'is in use by' }
            finally { Remove-Item Env:\VMTEST_OPEN_STALE_MINUTES }
        }
        It "takes a closed session's VM after two hours idle" {
            Use-TwoVMs; Reset-Locks
            Set-HeldBy 1 $first $other 5
            Set-HeldBy 3 $second $third 130
            Use-Session 2
            Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Should Match 'Third / y had held it unused for 2 hours 10 minutes'
        }
        It 'keeps another open session on the same repo and branch out, and touches nothing' {
            Reset-Locks
            Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running'); $second = (New-FakeVM -Checkpoints 'Clean', $mine.Checkpoint) }
            Set-HeldBy 1 $first $mine 5
            Use-Session 2
            $err = $null
            try { Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' } catch { $err = $_ }
            $err.Exception.Message | Should Match 'another Claude session that is still open \(session session-1'
            $err.Exception.Data['vmtestBusy'] | Should Be $true
            { Assert-TaskHasVM -Repo 'Mine' -Branch 'x' } | Should Throw 'another Claude session that is still open'
            { Invoke-VmTestSave -Repo 'Mine' -Branch 'x' } | Should Throw 'another Claude session that is still open'
            { Invoke-VmTestEnd -Repo 'Mine' -Branch 'x' } | Should Throw 'another Claude session that is still open'
            @(Get-AllCalls).Count | Should Be 0
            (FakeVM $second).Checkpoints -contains $mine.Checkpoint | Should Be $true
            (Get-VmTestLock -VMName $first).pid | Should Be 1
        }
        It 'the session holding the VM carries on as before' {
            Reset-Locks
            Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running'); $second = (New-FakeVM) }
            Set-HeldBy 1 $first $mine 5
            Use-Session 1
            { Assert-TaskHasVM -Repo 'Mine' -Branch 'x' } | Should Not Throw
            Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Should Not Match 'took it over'
        }
        It 'takes over its own task from a session that has closed, without restoring over it' {
            Reset-Locks
            Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running'); $second = (New-FakeVM) }
            Set-HeldBy 3 $first $mine 5
            Use-Session 2
            Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Should Match "'$first' .* took it over from a session that has closed"
            @(Get-AllCalls | Where-Object { $_ -like '*restore*' }).Count | Should Be 0
            (Get-VmTestLock -VMName $first).pid | Should Be 2
            { Assert-TaskHasVM -Repo 'Mine' -Branch 'x' } | Should Not Throw
        }
        It 'takes over its own task from an open session once that has left it idle' {
            Reset-Locks
            Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running'); $second = (New-FakeVM) }
            Set-HeldBy 1 $first $mine 500
            Use-Session 2
            Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Should Match 'took it over from an open session that left it unused for 8 hours 20 minutes'
        }
        It 'lets anyone on the task use a lock that does not say which session holds it' {
            Reset-Locks
            Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running'); $second = (New-FakeVM) }
            Remove-Item Env:\CLAUDE_PID -ErrorAction SilentlyContinue
            Set-HeldSince $first $mine 5
            Use-Session 2
            { Assert-TaskHasVM -Repo 'Mine' -Branch 'x' } | Should Not Throw
        }
        It 'tells a task whose VM was taken while idle what happened, until it begins again' {
            Use-TwoVMs; Reset-Locks
            Set-HeldBy 1 $first $other 5
            Set-HeldBy 3 $second $third 200
            Use-Session 2
            Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Out-Null
            Use-Session 1
            $err = $null
            try { Assert-TaskHasVM -Repo 'Third' -Branch 'y' } catch { $err = $_ }
            $err.Exception.Message | Should Match "Third / y doesn't hold a test VM. '$second' sat unused, so at .* it was saved for this task and given to Mine / x. Nothing was lost."
        }
        It 'tells a task its unsaved state went when -Force took its VM' {
            Use-TwoVMs; Reset-Locks
            Set-HeldBy 1 $first $other 5
            Set-HeldBy 3 $second $third 30
            Use-Session 2
            Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' -Force | Should Match 'taken from Third / y'
            $err = $null
            try { Assert-TaskHasVM -Repo 'Third' -Branch 'y' } catch { $err = $_ }
            $err.Exception.Message | Should Match "Mine / x took '$second' with -Force, so this task's unsaved state there is lost"
        }
        It 'clears the note once the task begins again' {
            Use-TwoVMs; Reset-Locks
            Set-HeldBy 1 $first $other 5
            Set-HeldBy 3 $second $third 200
            Use-Session 2
            Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Out-Null
            Invoke-VmTestSave -Repo 'Mine' -Branch 'x' | Out-Null
            Invoke-VmTestBegin -Repo 'Third' -Branch 'y' | Out-Null
            Invoke-VmTestSave -Repo 'Third' -Branch 'y' | Out-Null
            { Assert-TaskHasVM -Repo 'Third' -Branch 'y' } | Should Throw "Third / y doesn't hold a test VM. Run 'vmtest begin' first"
        }
        It 'tells a session asking about the wrong task which VM it does hold' {
            Reset-Locks
            Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running'); $second = (New-FakeVM) }
            Set-HeldBy 2 $first $mine 5
            Use-Session 2
            $err = $null
            try { Assert-TaskHasVM -Repo 'TheWorkBench' -Branch 'main' } catch { $err = $_ }
            $err.Exception.Message | Should Match "This session holds '$first' for Mine / x. vmtest takes the task from the folder it runs in"
            $err.Exception.Message | Should Match "Don't run 'vmtest begin' here"
            $err.Exception.Message | Should Not Match 'Run .vmtest begin. first'
            Use-Session 1
            $err = $null
            try { Assert-TaskHasVM -Repo 'TheWorkBench' -Branch 'main' } catch { $err = $_ }
            $err.Exception.Message | Should Not Match 'This session holds'
        }
        It 'status says whether the holding session is open' {
            Use-TwoVMs; Reset-Locks
            Set-HeldBy 1 $first $other 5
            Set-HeldBy 3 $second $third 5
            $s = (Get-VmTestStatus) -join "`n"
            $s | Should Match "'$first': Running, in use by Other / work since .* ago, by a Claude session that is open"
            $s | Should Match "'$second': Saved, in use by Third / y since .* ago, by a Claude session that has closed"
        }
        It 'end -Force tells the task it evicted' {
            Use-TwoVMs; Reset-Locks
            Set-HeldBy 1 $second $other 5
            Use-Session 2
            Invoke-VmTestEnd -Repo 'Mine' -Branch 'x' -VM $second -Force | Out-Null
            Use-Session 1
            $err = $null
            try { Assert-TaskHasVM -Repo 'Other' -Branch 'work' } catch { $err = $_ }
            $err.Exception.Message | Should Match "Mine / x took '$second' with -Force"
        }
        It "won't give a session a second VM when it runs begin from the wrong folder" {
            Use-TwoVMs; Reset-Locks
            Set-HeldBy 2 $first $mine 5
            Use-Session 2
            { Invoke-VmTestBegin -Repo 'TheWorkBench' -Branch 'main' } | Should Throw "This session already holds '$first' for Mine / x, and this is TheWorkBench / main"
            @(Get-AllCalls).Count | Should Be 0
            Get-VmTestLock -VMName $second | Should BeNullOrEmpty
        }
        It 'a lock from before sessions is claimed by the session that carries on with it' {
            Reset-Locks
            Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running'); $second = (New-FakeVM) }
            New-Item -ItemType Directory $env:VMTEST_STATE -Force | Out-Null
            @{ repo = 'Mine'; branch = 'x'; checkpoint = $mine.Checkpoint; since = (Get-Date).AddMinutes(-5).ToString('s'); v = 2 } | ConvertTo-Json |
                Set-Content (Join-Path $env:VMTEST_STATE 'lock.json')
            Use-Session 2
            Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Should Not Match 'took it over'
            (Get-VmTestLock -VMName $first).pid | Should Be 2
            Use-Session 1
            { Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' } | Should Throw 'another Claude session that is still open'
        }
        It 'counts a session as closed when its process id now belongs to a newer process' {
            Reset-Locks
            Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running'); $second = (New-FakeVM) }
            Set-HeldBy 1 $first $mine 5
            $global:FakeProcs['1'] = '2026-10-22T12:00:00'
            try { InModuleScope VmTest { Test-HolderOpen (Get-VmTestLock $env:VMTEST_VM) } | Should Be $false }
            finally { $global:FakeProcs['1'] = '2026-10-22T09:00:00' }
        }
        It "counts a session as open when its process exists but its start time can't be read" {
            Reset-Locks
            Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running'); $second = (New-FakeVM) }
            Set-HeldBy 1 $first $mine 5
            $global:FakeProcs['1'] = 'unknown'
            try { InModuleScope VmTest { Test-HolderOpen (Get-VmTestLock $env:VMTEST_VM) } | Should Be $true }
            finally { $global:FakeProcs['1'] = '2026-10-22T09:00:00' }
        }
        It 'begin -Force takes over its own task from an open session, and says it was forced' {
            Reset-Locks
            Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running'); $second = (New-FakeVM) }
            Set-HeldBy 1 $first $mine 5
            Use-Session 2
            Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' -Force | Should Match 'took it over from an open session, with -Force'
            @(Get-AllCalls | Where-Object { $_ -like '*restore*' }).Count | Should Be 0
        }
        It "end -Force resets another open session's VM on this task only with -VM, and tells that session" {
            Reset-Locks
            Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running'); $second = (New-FakeVM) }
            Set-HeldBy 1 $first $mine 5
            Use-Session 2
            { Invoke-VmTestEnd -Repo 'Mine' -Branch 'x' -Force } | Should Throw 'another Claude session that is still open'
            Invoke-VmTestEnd -Repo 'Mine' -Branch 'x' -Force -VM $first | Should Match "Put '$first' back to 'Clean'"
            Use-Session 1
            $err = $null
            try { Assert-TaskHasVM -Repo 'Mine' -Branch 'x' } catch { $err = $_ }
            $err.Exception.Message | Should Match "another session on this task ran 'vmtest end -Force' and put '$first' back to Clean"
        }
        It 'save works from another session once the holder has closed' {
            Reset-Locks
            Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running'); $second = (New-FakeVM) }
            Set-HeldBy 3 $first $mine 5
            Use-Session 2
            Invoke-VmTestSave -Repo 'Mine' -Branch 'x' | Should Match 'and released it'
            Get-VmTestLock -VMName $first | Should BeNullOrEmpty
        }
        It "lets the user's own terminal (outside Claude Code) use a VM an open session holds" {
            Reset-Locks
            Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running'); $second = (New-FakeVM) }
            Set-HeldBy 1 $first $mine 5
            Remove-Item Env:\CLAUDE_PID, Env:\CLAUDE_CODE_SESSION_ID
            { Assert-TaskHasVM -Repo 'Mine' -Branch 'x' } | Should Not Throw
            Invoke-VmTestSave -Repo 'Mine' -Branch 'x' | Should Match 'and released it'
        }
        } finally {
            Remove-Item Env:\CLAUDE_PID, Env:\CLAUDE_CODE_SESSION_ID -ErrorAction SilentlyContinue
            Remove-Variable -Scope Global FakeProcs -ErrorAction SilentlyContinue
        }
    }
    It 'shows the time a VM was taken in a form that reads aloud well' {
        $t = (Get-Date).Date.AddHours(15).AddMinutes(4).ToString('s')
        $global:PesterTime = $t
        InModuleScope VmTest { Format-Since $global:PesterTime } | Should Be (Get-Date $t).ToString('h:mm tt')
    }
    Reset-Locks
    $env:VMTEST_POOL = '1'
    Remove-Item Env:\VMTEST_STATE
}

Describe 'settings' {
    It 'says what is wrong with a setting that is not a number' {
        $env:VMTEST_STALE_MINUTES = 'two hours'
        try { { Get-VmTestConfig } | Should Throw "VMTEST_STALE_MINUTES is 'two hours'" } finally { Remove-Item Env:\VMTEST_STALE_MINUTES }
    }
    It 'refuses pool names that could reach outside its folders' {
        $env:VMTEST_POOL = 'ClaudeTesting,..\x'
        try { { Get-PoolNames } | Should Throw "isn't a usable VM name" } finally { $env:VMTEST_POOL = '1' }
    }
    It 'counts names that differ only in case once' {
        $env:VMTEST_POOL = 'A,a,B'
        try { (Get-PoolNames) -join ',' | Should Be 'A,B' } finally { $env:VMTEST_POOL = '1' }
    }
}

Describe 'making the pool' {
    $env:VMTEST_STATE = Join-Path $TestDrive 'making'
    It 'has nothing to make for a pool of one' {
        Use-FakeHyperV
        Invoke-VmTestPrepare -Pool | Should Match 'nothing to make'
    }
    It "leaves alone a VM with a pool name that vmtest didn't make" {
        $env:VMTEST_POOL = '2'
        Use-FakePool @{ $env:VMTEST_VM = (New-FakeVM); $second = (New-FakeVM -Notes 'Somebody else') }
        Mock -ModuleName VmTest Remove-VM { throw 'must not be called' }
        Mock -ModuleName VmTest Export-VMSnapshot { throw 'must not be called' }
        (Invoke-VmTestPrepare -Pool -Force) -join ' ' | Should Match "wasn't made by vmtest, so it was left alone"
        { InModuleScope VmTest { Remove-PoolClone "$env:VMTEST_VM-2" } } | Should Throw "wasn't made by vmtest"
        $env:VMTEST_POOL = '1'
    }
    It "doesn't remake a copy a task is using" {
        $env:VMTEST_POOL = '2'
        Use-FakePool @{ $env:VMTEST_VM = (New-FakeVM); $second = (New-FakeVM -Notes 'vmtest pool VM, made from x') }
        Set-VmTestLock -Task (Resolve-Task -Repo 'Other' -Branch 'work') -VMName $second -Force
        Mock -ModuleName VmTest Export-VMSnapshot { throw 'must not be called' }
        (Invoke-VmTestPrepare -Pool -Force) -join ' ' | Should Match "in use by Other / work, so it wasn't remade"
        Clear-VmTestLock -VMName $second
        $env:VMTEST_POOL = '1'
    }
    It "doesn't remake a copy that has a task's saved state, and names it" {
        $env:VMTEST_POOL = '2'
        Use-FakePool @{ $env:VMTEST_VM = (New-FakeVM); $second = (New-FakeVM -Checkpoints 'Clean', 'task AIChat x 123abc' -Notes 'vmtest pool VM, made from x') }
        Mock -ModuleName VmTest Export-VMSnapshot { throw 'must not be called' }
        Mock -ModuleName VmTest Remove-VM { throw 'must not be called' }
        (Invoke-VmTestPrepare -Pool -Force) -join ' ' | Should Match "has saved state for other tasks \(task AIChat x 123abc\), so it wasn't remade"
        $env:VMTEST_POOL = '1'
    }
    It "with -Force, skips a copy already made from the current Clean" {
        $env:VMTEST_POOL = '2'
        Use-FakePool @{ $env:VMTEST_VM = (New-FakeVM); $second = (New-FakeVM -Notes 'vmtest pool VM, made from P''s Clean id-Clean on 1.') }
        Mock -ModuleName VmTest Export-VMSnapshot { throw 'must not be called' }
        (Invoke-VmTestPrepare -Pool -Force) -join ' ' | Should Match 'already made from the current Clean'
        $env:VMTEST_POOL = '1'
    }
    It 'never deletes a copy a task holds' {
        $env:VMTEST_POOL = '2'
        Use-FakePool @{ $env:VMTEST_VM = (New-FakeVM); $second = (New-FakeVM -Notes 'vmtest pool VM') }
        Set-VmTestLock -Task (Resolve-Task -Repo 'Other' -Branch 'work') -VMName $second -Force
        Mock -ModuleName VmTest Remove-VM { throw 'must not be called' }
        { InModuleScope VmTest { Remove-PoolClone "$env:VMTEST_VM-2" } } | Should Throw 'in use by Other / work'
        Clear-VmTestLock -VMName $second
        $env:VMTEST_POOL = '1'
    }
    It 'uses an earlier copy of the same Clean again, but never a half-made one' {
        $r = Join-Path $TestDrive 'bases'
        $half = Join-Path $r 'base-1'; $done = Join-Path $r 'base-2'; $older = Join-Path $r 'base-3'
        New-Item -ItemType Directory $half, $done, $older -Force | Out-Null
        foreach ($b in $half, $done, $older) { Set-Content (Join-Path $b 'Clean.vhdx') 'x' }
        Set-Content (Join-Path $done 'clean-id.txt') 'id-now'
        Set-Content (Join-Path $older 'clean-id.txt') 'id-before'
        $global:PesterRoot = $r
        InModuleScope VmTest { Find-PoolBase -Root $global:PesterRoot -CleanId 'id-now' } | Should Be $done
        InModuleScope VmTest { Find-PoolBase -Root $global:PesterRoot -CleanId 'id-other' } | Should BeNullOrEmpty
    }
    Context 'cleaning up copies of Clean' {
        $vhdRoot = Join-Path $TestDrive 'vhd'
        $used = Join-Path $vhdRoot 'base-1'; $unused = Join-Path $vhdRoot 'base-2'
        New-Item -ItemType Directory $used, $unused -Force | Out-Null
        Set-Content (Join-Path $used 'p.vhdx') 'x'; Set-Content (Join-Path $unused 'p.vhdx') 'x'
        Mock -ModuleName VmTest Get-VM { [pscustomobject]@{ Name = 'clone' } }
        Mock -ModuleName VmTest Get-VMHardDiskDrive { [pscustomobject]@{ Path = 'C:\x\clone.vhdx' } }
        It "keeps everything when a disk's parent can't be read" {
            Mock -ModuleName VmTest Get-VHD { throw 'no access' }
            $global:PesterRoot = $vhdRoot; InModuleScope VmTest { Remove-UnusedPoolBases $global:PesterRoot }
            Test-Path $unused | Should Be $true
            Test-Path $used | Should Be $true
        }
        It 'deletes only the copy no disk is made from' {
            $global:PesterUsed = Join-Path $used 'p.vhdx'
            Mock -ModuleName VmTest Get-VHD { if ($Path -eq 'C:\x\clone.vhdx') { [pscustomobject]@{ ParentPath = $global:PesterUsed } } else { [pscustomobject]@{ ParentPath = '' } } }
            $global:PesterRoot = $vhdRoot; InModuleScope VmTest { Remove-UnusedPoolBases $global:PesterRoot }
            Test-Path $unused | Should Be $false
            Test-Path $used | Should Be $true
        }
    }
    Remove-Item Env:\VMTEST_STATE
}

Describe 'reading who is signed in' {
    $header = ' USERNAME              SESSIONNAME        ID  STATE   IDLE TIME  LOGON TIME'
    It 'sees the console' {
        $s = InModuleScope VmTest { Get-SignInState -QuserLines @(' USERNAME  SESSIONNAME  ID  STATE', '>vmuser                console             1  Active      none   10/5/2026 7:25 AM') -UserName vmuser }
        $s.State | Should Be 'console'
    }
    It 'sees a disconnected session and its id' {
        $s = InModuleScope VmTest { Get-SignInState -QuserLines @('vmuser                                    2  Disc        5  10/5/2026 9:00 AM') -UserName vmuser }
        $s.State | Should Be 'disconnected'
        $s.Id | Should Be 2
    }
    It 'sees Remote Desktop' {
        $s = InModuleScope VmTest { Get-SignInState -QuserLines @('>vmuser                rdp-tcp#0           2  Active      none   10/5/2026 9:00 AM') -UserName vmuser }
        $s.State | Should Be 'remote'
    }
    It 'sees nobody' {
        (InModuleScope VmTest { Get-SignInState -QuserLines @('No User exists for *') -UserName vmuser }).State | Should Be 'none'
    }
}

Describe 'another PC (VMTEST_HOST)' {
    # No real host: a remote session is faked by running its script blocks here, and copies to and
    # from it are plain copies. The host's vmtest.ps1 itself is replaced by a recorder.
    $env:VMTEST_STATE = Join-Path $TestDrive 'remote'
    $env:VMTEST_HOST_PATH = Join-Path $TestDrive 'host-tool'
    $first = $env:VMTEST_VM
    $mine = Resolve-Task -Repo 'Mine' -Branch 'x'
    $work = Join-Path $TestDrive 'work'
    New-Item -ItemType Directory (Join-Path $work 'build\sub') -Force | Out-Null
    Set-Content (Join-Path $work 'build\app.exe') 'app'
    Set-Content (Join-Path $work 'build\sub\data.txt') 'data'
    New-Item -ItemType File (Join-Path $work 'build\.hidden') -Force | Out-Null
    (Get-Item (Join-Path $work 'build\.hidden') -Force).Attributes = 'Hidden'
    Set-Content (Join-Path $work 'notes.txt') 'notes'
    Set-Content (Join-Path $work 'setup.ps1') 'exit 3'

    function Use-FakeHost([string[]]$Out = @('done'), [int]$Code = 0) {
        $global:HostRuns = New-Object System.Collections.Generic.List[object]
        $global:HostAnswer = @{ out = $Out; code = $Code }
        # A real credential: a plain string would make New-PSSession ask for a password.
        Mock -ModuleName VmTest Get-HostCredential { New-Object System.Management.Automation.PSCredential('user', (New-Object System.Security.SecureString)) }
        Mock -ModuleName VmTest New-PSSession { 'host-session' }
        Mock -ModuleName VmTest Remove-PSSession {}
        Mock -ModuleName VmTest Invoke-OnHost { $a = @($ArgumentList); & $ScriptBlock @a }
        Mock -ModuleName VmTest Copy-ToHost { Copy-Item -Path $Path -Destination $Destination -Recurse:$Recurse -Force }
        Mock -ModuleName VmTest Copy-FromHost { Copy-Item -Path $Path -Destination $Destination -Force }
        Mock -ModuleName VmTest Invoke-HostVmTest {
            # What the host would see: its parameters, its environment, and the staged files.
            $staged = @{}
            foreach ($k in 'Target', 'ScriptFile') {
                $v = $Parameters[$k]
                if ($v -and (Test-Path $v)) { $staged[$k] = @(Get-ChildItem $v -Recurse -Force -File | ForEach-Object { $_.Name }) }
            }
            $global:HostRuns.Add([pscustomobject]@{ Parameters = $Parameters; Environment = $Environment; Staged = $staged })
            if ($Parameters.Command -eq 'shot') {
                Set-Content $Parameters.Target 'png'
                return @{ out = @("Saved the VM's screen (1920x1080) to $($Parameters.Target)"); code = 0 }
            }
            $global:HostAnswer
        }
    }

    It 'is off unless VMTEST_HOST is set, and never forwards from the host itself' {
        Test-ForwardToHost | Should Be $false
        $env:VMTEST_HOST = 'HOSTNAME'
        try {
            Test-ForwardToHost | Should Be $true
            $env:VMTEST_ON_HOST = '1'
            Test-ForwardToHost | Should Be $false
        } finally { Remove-Item Env:\VMTEST_HOST, Env:\VMTEST_ON_HOST -ErrorAction SilentlyContinue }
    }
    It 'refuses a host name that could reach outside its folder' {
        { InModuleScope VmTest { Get-HostCredentialPath '..\x' } } | Should Throw "isn't a usable computer name"
    }
    It 'says how to save a sign-in when there is none' {
        $before = $env:LOCALAPPDATA
        $env:LOCALAPPDATA = Join-Path $TestDrive 'appdata'
        try { { InModuleScope VmTest { Get-HostCredential 'HOSTNAME' } } | Should Throw "To save one, run this once in your own PowerShell window: powershell" }
        finally { $env:LOCALAPPDATA = $before }
    }
    Context 'what goes to the host' {
        Push-Location $work
        try {
            $plan = { param($c, $p) $global:PlanArgs = @($c, $p); InModuleScope VmTest { Get-ForwardPlan -Command $global:PlanArgs[0] -Parameters $global:PlanArgs[1] -Stage 'C:\stage\1' } }
            It 'deploy copies the folder to the staging folder and names that' {
                $r = & $plan 'deploy' @{ Target = 'build'; Name = 'App' }
                $r.Parameters.Target | Should Be 'C:\stage\1\build'
                $r.Parameters.Name | Should Be 'App'
                $r.Uploads[0].Local | Should Be (Join-Path $work 'build')
                $r.Uploads[0].Folder | Should Be $true
            }
            It 'deploy of one file copies that file' {
                $r = & $plan 'deploy' @{ Target = 'notes.txt' }
                $r.Parameters.Target | Should Be 'C:\stage\1\notes.txt'
                $r.Uploads[0].Folder | Should Be $false
            }
            It 'push copies the file and keeps -Destination, a folder in the VM' {
                $r = & $plan 'push' @{ Target = '.\notes.txt'; Destination = 'C:\inside' }
                $r.Parameters.Target | Should Be 'C:\stage\1\notes.txt'
                $r.Parameters.Destination | Should Be 'C:\inside'
                @($r.Uploads).Count | Should Be 1
            }
            It 'push refuses a folder, as it does here' {
                { & $plan 'push' @{ Target = 'build' } } | Should Throw "isn't a file"
            }
            It 'run -ScriptFile copies the script; a command line goes as it is' {
                $r = & $plan 'run' @{ ScriptFile = 'setup.ps1'; Timeout = 90; Elevated = [switch]$true }
                $r.Parameters.ScriptFile | Should Be 'C:\stage\1\setup.ps1'
                $r.Parameters.Timeout | Should Be 90
                $r.Parameters.Elevated | Should Be $true
                $r.Parameters.Elevated.GetType().Name | Should Be 'Boolean'
                $r = & $plan 'run' @{ Target = 'type C:\x\notes.txt' }
                $r.Parameters.Target | Should Be 'type C:\x\notes.txt'
                @($r.Uploads).Count | Should Be 0
            }
            It 'shot is saved on the host and copied back to the path given here' {
                $r = & $plan 'shot' @{ Target = 'pics\out.png'; FromHost = [switch]$true; VM = 'B' }
                $r.Parameters.Target | Should Be 'C:\stage\1\out.png'
                $r.Download.Local | Should Be (Join-Path $work 'pics\out.png')
                $r.Parameters.FromHost | Should Be $true
                $r.Parameters.VM | Should Be 'B'
            }
            It 'other commands go unchanged, with the command named' {
                $r = & $plan 'keys' @{ Target = '{TAB}'; Window = 'Notepad' }
                $r.Parameters.Command | Should Be 'keys'
                $r.Parameters.Target | Should Be '{TAB}'
                $r.Parameters.Window | Should Be 'Notepad'
                @($r.Uploads).Count | Should Be 0
                $r.Download | Should BeNullOrEmpty
            }
        } finally { Pop-Location }
    }
    Context 'running a command there' {
        Push-Location $work
        $env:VMTEST_HOST = 'HOSTNAME'
        try {
            It 'copies vmtest to the host, then only when it differs' {
                Use-FakeHost
                Invoke-VmTestOnHost -Command 'status' -Parameters @{} | Out-Null
                Test-Path (Join-Path $env:VMTEST_HOST_PATH 'vmtest.ps1') | Should Be $true
                Test-Path (Join-Path $env:VMTEST_HOST_PATH 'guest\agent.ps1') | Should Be $true
                Assert-MockCalled -ModuleName VmTest Copy-ToHost -Times 3 -Exactly -Scope It
                Invoke-VmTestOnHost -Command 'status' -Parameters @{} | Out-Null
                Assert-MockCalled -ModuleName VmTest Copy-ToHost -Times 3 -Exactly -Scope It
                Add-Content (Join-Path $env:VMTEST_HOST_PATH 'guest\agent.ps1') '# changed there'
                Invoke-VmTestOnHost -Command 'status' -Parameters @{} | Out-Null
                Assert-MockCalled -ModuleName VmTest Copy-ToHost -Times 6 -Exactly -Scope It
                Get-Content (Join-Path $env:VMTEST_HOST_PATH 'guest\agent.ps1') -Raw | Should Not Match 'changed there'
            }
            It 'passes the output and exit code back, and names the task from the folder here' {
                Use-FakeHost -Out @('line one', 'Exit code 5') -Code 5
                $branch = git -C $root branch --show-current
                if (-not $branch) { Set-TestInconclusive 'detached HEAD (as in CI)'; return }
                Push-Location $root
                try { $r = Invoke-VmTestOnHost -Command 'run' -Parameters @{ Command = 'run'; Target = 'exit 5' } } finally { Pop-Location }
                $r.Output -join '|' | Should Be 'line one|Exit code 5'
                $r.ExitCode | Should Be 5
                $p = $global:HostRuns[0].Parameters
                $p.Repo | Should Be 'TheWorkBench'
                $p.Branch | Should Be $branch
                $p.Target | Should Be 'exit 5'
            }
            It 'keeps -Repo and -Branch when they are given' {
                Use-FakeHost
                Invoke-VmTestOnHost -Command 'begin' -Parameters @{ Repo = 'Mine'; Branch = 'x' } | Out-Null
                $global:HostRuns[0].Parameters.Repo | Should Be 'Mine'
                $global:HostRuns[0].Parameters.Branch | Should Be 'x'
            }
            It 'deploy stages every file, hidden ones too, and removes the staging folder after' {
                Use-FakeHost
                Invoke-VmTestOnHost -Command 'deploy' -Parameters @{ Target = 'build' } | Out-Null
                $run = $global:HostRuns[0]
                ($run.Staged.Target | Sort-Object) -join ',' | Should Be '.hidden,app.exe,data.txt'
                Split-Path $run.Parameters.Target -Leaf | Should Be 'build'
                Test-Path (Split-Path $run.Parameters.Target) | Should Be $false
            }
            It 'push and run -ScriptFile stage their file' {
                Use-FakeHost
                Invoke-VmTestOnHost -Command 'push' -Parameters @{ Target = 'notes.txt' } | Out-Null
                $global:HostRuns[0].Staged.Target | Should Be 'notes.txt'
                Invoke-VmTestOnHost -Command 'run' -Parameters @{ ScriptFile = 'setup.ps1' } | Out-Null
                $global:HostRuns[1].Staged.ScriptFile | Should Be 'setup.ps1'
                Test-Path $global:HostRuns[1].Parameters.ScriptFile | Should Be $false
            }
            It 'shot copies the picture back and names the path here' {
                Use-FakeHost
                $out = Join-Path $work 'shot.png'
                $r = Invoke-VmTestOnHost -Command 'shot' -Parameters @{ Target = 'shot.png' }
                Get-Content $out | Should Be 'png'
                $r.Output | Should Be "Saved the VM's screen (1920x1080) to $out"
            }
            It 'shot that fails copies nothing back' {
                Use-FakeHost -Out @() -Code 1
                Mock -ModuleName VmTest Invoke-HostVmTest { @{ out = @(); code = 1 } }
                (Invoke-VmTestOnHost -Command 'shot' -Parameters @{ Target = 'none.png' }).ExitCode | Should Be 1
                Test-Path (Join-Path $work 'none.png') | Should Be $false
            }
            It 'tells the host it is the host, and who is asking' {
                Use-FakeHost
                $global:FakeProcs = @{ '41' = '2026-10-22T09:00:00' }
                Mock -ModuleName VmTest Get-ProcessStartTime { $global:FakeProcs["$Id"] }
                $env:CLAUDE_PID = '41'; $env:CLAUDE_CODE_SESSION_ID = 'session-41'
                try { Invoke-VmTestOnHost -Command 'windows' -Parameters @{} | Out-Null }
                finally { Remove-Item Env:\CLAUDE_PID, Env:\CLAUDE_CODE_SESSION_ID }
                $e = $global:HostRuns[0].Environment
                $e.VMTEST_ON_HOST | Should Be '1'
                $e.VMTEST_REMOTE_FOLDER | Should Be $work
                $holder = $e.VMTEST_REMOTE_HOLDER | ConvertFrom-Json
                $holder.pid | Should Be 41
                $holder.started | Should Be '2026-10-22T09:00:00'
                $holder.session | Should Be 'session-41'
                $holder.computer | Should Be $env:COMPUTERNAME
                ($e.VMTEST_REMOTE_LIVE | ConvertFrom-Json).computer | Should Be $env:COMPUTERNAME
            }
            It 'outside Claude Code it sends no holder' {
                Use-FakeHost
                Invoke-VmTestOnHost -Command 'windows' -Parameters @{} | Out-Null
                $global:HostRuns[0].Environment.ContainsKey('VMTEST_REMOTE_HOLDER') | Should Be $false
            }
            It "carries the reason it couldn't name the task, outside a git folder" {
                Use-FakeHost
                Invoke-VmTestOnHost -Command 'status' -Parameters @{} | Out-Null
                $global:HostRuns[0].Parameters.ContainsKey('Repo') | Should Be $false
                $global:HostRuns[0].Environment.VMTEST_REMOTE_TASK_ERROR | Should Match 'Not in a git repository'
            }
            It 'lists every process with its start time, and ? where that can not be read' {
                Mock -ModuleName VmTest Get-Process {
                    [pscustomobject]@{ Id = 11; ProcessName = 'node'; StartTime = [datetime]'2026-10-22T09:00:00' }
                    [pscustomobject]@{ Id = 12; ProcessName = 'claude'; StartTime = [datetime]'2026-10-22T10:00:00' }
                    $denied = [pscustomobject]@{ Id = 13; ProcessName = 'svchost' }
                    $denied | Add-Member -MemberType ScriptProperty -Name StartTime -Value { throw 'Access is denied' }
                    $denied
                }
                $live = InModuleScope VmTest { Get-LiveSessions } | ConvertFrom-Json
                $live.computer | Should Be $env:COMPUTERNAME
                $live.pids | Should Be '11@2026-10-22T09:00:00 12@2026-10-22T10:00:00 13@?'
            }
            It 'sends no list when it is too long for an environment variable, so those sessions count as open' {
                Mock -ModuleName VmTest Get-Process { foreach ($i in 1..2000) { [pscustomobject]@{ Id = 100000 + $i; StartTime = [datetime]'2026-10-22T09:00:00' } } }
                InModuleScope VmTest { Get-LiveSessions } | Should BeNullOrEmpty
                Use-FakeHost
                Invoke-VmTestOnHost -Command 'windows' -Parameters @{} | Out-Null
                $global:HostRuns[0].Environment.ContainsKey('VMTEST_REMOTE_LIVE') | Should Be $false
            }
            It 'deletes the staging folder when a copy to the host fails' {
                Use-FakeHost
                Mock -ModuleName VmTest Sync-HostTool { $false }
                Mock -ModuleName VmTest Copy-ToHost { throw 'connection dropped' }
                $stages = Join-Path $env:TEMP 'vmtest-stage'
                $before = @(Get-ChildItem $stages -Directory -ErrorAction SilentlyContinue).Count
                { Invoke-VmTestOnHost -Command 'push' -Parameters @{ Target = 'notes.txt' } } | Should Throw 'connection dropped'
                (@(Get-ChildItem $stages -Directory -ErrorAction SilentlyContinue).Count -le $before) | Should Be $true
                $global:HostRuns.Count | Should Be 0
            }
            It "deletes the staging folder when the host's vmtest fails" {
                Use-FakeHost
                Mock -ModuleName VmTest Sync-HostTool { $false }
                Mock -ModuleName VmTest Invoke-HostVmTest { throw 'the host went away' }
                $stages = Join-Path $env:TEMP 'vmtest-stage'
                $before = @(Get-ChildItem $stages -Directory -ErrorAction SilentlyContinue).Count
                { Invoke-VmTestOnHost -Command 'deploy' -Parameters @{ Target = 'build' } } | Should Throw 'the host went away'
                (@(Get-ChildItem $stages -Directory -ErrorAction SilentlyContinue).Count -le $before) | Should Be $true
            }
            It 'clears staging folders a day old that a dropped connection left' {
                Use-FakeHost
                Mock -ModuleName VmTest Sync-HostTool { $false }
                $old = New-Item -ItemType Directory (Join-Path $env:TEMP "vmtest-stage\pester-old-$([guid]::NewGuid())") -Force
                $old.LastWriteTime = (Get-Date).AddDays(-2)
                $new = New-Item -ItemType Directory (Join-Path $env:TEMP "vmtest-stage\pester-new-$([guid]::NewGuid())") -Force
                try {
                    Invoke-VmTestOnHost -Command 'push' -Parameters @{ Target = 'notes.txt' } | Out-Null
                    Test-Path $old.FullName | Should Be $false
                    Test-Path $new.FullName | Should Be $true
                } finally { Remove-Item $old.FullName, $new.FullName -Recurse -Force -ErrorAction SilentlyContinue }
            }
        } finally {
            Remove-Item Env:\VMTEST_HOST
            Pop-Location
        }
    }
    Context 'sessions on other PCs' {
        $global:FakeProcs = @{ '1' = '2026-10-22T09:00:00' }
        Mock -ModuleName VmTest Get-ProcessStartTime { $global:FakeProcs["$Id"] }
        function Use-Remote([int]$Id, [string]$Computer = 'PC-A', [string]$Started = '2026-10-22T09:00:00') {
            $env:VMTEST_ON_HOST = '1'
            $env:VMTEST_REMOTE_HOLDER = @{ pid = $Id; started = $Started; session = "session-$Id"; computer = $Computer } | ConvertTo-Json -Compress
        }
        function Set-Live([string]$Computer, [hashtable]$Procs) {
            $env:VMTEST_REMOTE_LIVE = [pscustomobject]@{ computer = $Computer; pids = (@($Procs.Keys | ForEach-Object { "$_@$($Procs[$_])" }) -join ' ') } | ConvertTo-Json -Compress
        }
        function Clear-Remote { Remove-Item Env:\VMTEST_ON_HOST, Env:\VMTEST_REMOTE_HOLDER, Env:\VMTEST_REMOTE_LIVE, Env:\VMTEST_REMOTE_TASK_ERROR, Env:\CLAUDE_PID, Env:\CLAUDE_CODE_SESSION_ID -ErrorAction SilentlyContinue }
        function Open-State { InModuleScope VmTest { Test-HolderOpen (Get-VmTestLock $env:VMTEST_VM) } }
        try {
            It 'a lock taken from another PC names that PC' {
                Clear-Remote; Clear-VmTestLock
                Use-Remote 7
                Set-VmTestLock -Task $mine -Force
                $l = Get-VmTestLock
                $l.pid | Should Be 7
                $l.computer | Should Be 'PC-A'
                $l.session | Should Be 'session-7'
            }
            It 'a lock taken here has no computer in it, as before' {
                Clear-Remote; Clear-VmTestLock
                $env:CLAUDE_PID = '1'; $env:CLAUDE_CODE_SESSION_ID = 'session-1'
                Set-VmTestLock -Task $mine -Force
                $p = InModuleScope VmTest { Get-LockPath }
                (Get-Content $p -Raw | ConvertFrom-Json).PSObject.Properties.Name -join ',' | Should Be 'repo,branch,checkpoint,since,v,pid,started,session'
                Open-State | Should Be $true
            }
            It "a session on the asking PC is open while that PC lists it, and closed once it doesn't" {
                Clear-Remote; Clear-VmTestLock
                Use-Remote 7; Set-VmTestLock -Task $mine -Force
                Set-Live 'PC-A' @{ '7' = '2026-10-22T09:00:00' }
                Open-State | Should Be $true
                Set-Live 'PC-A' @{ '8' = '2026-10-22T09:00:00' }
                Open-State | Should Be $false
                Set-Live 'PC-A' @{ '7' = '2026-10-22T12:00:00' }
                Open-State | Should Be $false
                Set-Live 'PC-A' @{ '7' = '?' }
                Open-State | Should Be $true
            }
            It 'a damaged or partial list from the asking PC counts its sessions as open' {
                Clear-Remote; Clear-VmTestLock
                Use-Remote 7; Set-VmTestLock -Task $mine -Force
                foreach ($bad in @('{ not json', '{"pids":"8@2026-10-22T09:00:00"}', '{"computer":"PC-A"}',
                        '{"computer":"PC-A","processes":[{"pid":8,"started":"2026-10-22T09:00:00"}]}')) {
                    $env:VMTEST_REMOTE_LIVE = $bad
                    Open-State | Should Be $true
                }
            }
            It 'a damaged or partial holder stops the command instead of acting as the user' {
                Clear-Remote; Clear-VmTestLock
                Use-FakeHyperV
                Use-Remote 7; Set-VmTestLock -Task $mine -Force
                foreach ($bad in @('{ not json', '{"pid":7,"started":"2026-10-22T09:00:00"}', '{"started":"2026-10-22T09:00:00","computer":"PC-A"}',
                        '{"pid":7,"computer":"PC-A"}', '{"pid":"seven","started":"x","computer":"PC-A"}')) {
                    $env:VMTEST_REMOTE_HOLDER = $bad
                    { Assert-TaskHasVM -Repo 'Mine' -Branch 'x' } | Should Throw "VMTEST_REMOTE_HOLDER doesn't say which Claude session"
                    { Set-VmTestLock -Task $mine -Force } | Should Throw "VMTEST_REMOTE_HOLDER doesn't say"
                }
                (Get-VmTestLock).pid | Should Be 7
            }
            It "on the host, a task is never taken from the host's own folder" {
                Clear-Remote
                $env:VMTEST_ON_HOST = '1'
                { Resolve-Task -Path $root } | Should Throw "didn't name a task"
                $env:VMTEST_REMOTE_TASK_ERROR = 'Not in a git repository; give -Repo and -Branch.'
                { Resolve-Task -Path $root } | Should Throw 'Not in a git repository'
                (Resolve-Task -Repo 'Mine' -Branch 'x').Checkpoint | Should Be $mine.Checkpoint
                Remove-Item Env:\VMTEST_REMOTE_TASK_ERROR
            }
            It "a session on a PC that isn't asking counts as open, and status says why" {
                Clear-Remote; Clear-VmTestLock
                Use-FakeHyperV
                Use-Remote 7; Set-VmTestLock -Task $mine -Force
                Set-Live 'PC-B' @{ '7' = '2026-10-22T09:00:00' }
                Open-State | Should Be $true
                Remove-Item Env:\VMTEST_REMOTE_LIVE
                Open-State | Should Be $true
                Clear-Remote
                Open-State | Should Be $true
                (Get-VmTestStatus) -join "`n" | Should Match "by a Claude session on PC-A, which can't be checked from here, so it counts as open\."
            }
            It 'a session on another PC with the same process id is another session' {
                Clear-Remote; Clear-VmTestLock
                Use-FakeHyperV
                Use-Remote 1; Set-VmTestLock -Task $mine -Force
                Clear-Remote
                $env:CLAUDE_PID = '1'; $env:CLAUDE_CODE_SESSION_ID = 'session-1'
                InModuleScope VmTest { Test-SameHolder (Get-VmTestLock $env:VMTEST_VM) } | Should Be $false
                { Assert-TaskHasVM -Repo 'Mine' -Branch 'x' } | Should Throw 'another Claude session that is still open (session session-1 on PC-A'
                Use-Remote 1 -Computer 'PC-B'
                InModuleScope VmTest { Test-SameHolder (Get-VmTestLock $env:VMTEST_VM) } | Should Be $false
                Use-Remote 1
                InModuleScope VmTest { Test-SameHolder (Get-VmTestLock $env:VMTEST_VM) } | Should Be $true
                { Assert-TaskHasVM -Repo 'Mine' -Branch 'x' } | Should Not Throw
            }
            It "another session on the same PC can take over once that PC says the holder closed" {
                Clear-Remote; Clear-VmTestLock
                Use-FakePool -FreeGB 64 @{ $first = (New-FakeVM -State 'Running') }
                Use-Remote 7; Set-VmTestLock -Task $mine -Force
                Use-Remote 9
                Set-Live 'PC-A' @{ '7' = '2026-10-22T09:00:00'; '9' = '2026-10-22T09:00:00' }
                { Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' } | Should Throw 'still open (session session-7 on PC-A,'
                Set-Live 'PC-A' @{ '9' = '2026-10-22T09:00:00' }
                Invoke-VmTestBegin -Repo 'Mine' -Branch 'x' | Should Match 'took it over from a session that has closed'
                (Get-VmTestLock).pid | Should Be 9
                (Get-VmTestLock).computer | Should Be 'PC-A'
            }
            It 'status on the host names the host' {
                Clear-Remote; Clear-VmTestLock
                Use-FakeHyperV
                $env:VMTEST_ON_HOST = '1'
                (Get-VmTestStatus)[0] | Should Match "^Test VMs on ${env:COMPUTERNAME}: 1 in the pool\. $env:COMPUTERNAME has "
                Clear-Remote
                (Get-VmTestStatus)[0] | Should Match '^Test VMs: 1 in the pool\. This PC has '
            }
        } finally {
            Clear-Remote; Clear-VmTestLock
            Remove-Variable -Scope Global FakeProcs -ErrorAction SilentlyContinue
        }
    }
    Context 'the host end' {
        It "runs the host's vmtest.ps1 with the parameters and environment, and returns its exit code" {
            Mock -ModuleName VmTest Invoke-OnHost { $a = @($ArgumentList); & $ScriptBlock @a }
            $tool = Join-Path $TestDrive 'stub-host'
            New-Item -ItemType Directory $tool -Force | Out-Null
            Set-Content (Join-Path $tool 'vmtest.ps1') 'param([string]$Command, [string]$Target, [switch]$Elevated) "ran $Command [$Target] $Elevated on=$env:VMTEST_ON_HOST"; exit 7'
            try {
                $global:StubTool = $tool
                $r = InModuleScope VmTest {
                    Invoke-HostVmTest -Session 'host-session' -ToolPath $global:StubTool -Parameters @{ Command = 'run'; Target = 'echo "a b"'; Elevated = $true } -Environment @{ VMTEST_ON_HOST = '1' }
                }
                $r.code | Should Be 7
                $r.out | Should Be 'ran run [echo "a b"] True on=1'
            } finally { Remove-Item Env:\VMTEST_ON_HOST -ErrorAction SilentlyContinue }
        }
    }
    Context "'vmtest host'" {
        $before = $env:LOCALAPPDATA
        $env:LOCALAPPDATA = Join-Path $TestDrive 'appdata-host'
        $saved = Join-Path $env:LOCALAPPDATA 'vmtest\hosts\HOSTNAME.xml'
        $cred = New-Object System.Management.Automation.PSCredential('HOSTNAME\you', (ConvertTo-SecureString 'not-a-real-password' -AsPlainText -Force))
        Mock -ModuleName VmTest Test-WSMan {}
        Mock -ModuleName VmTest New-HostSession { 'host-session' }
        Mock -ModuleName VmTest Remove-PSSession {}
        Mock -ModuleName VmTest Sync-HostTool { $true }
        try {
            It "says what the host needs when it doesn't answer, and saves nothing" {
                Mock -ModuleName VmTest Test-WSMan { throw 'The client cannot connect.' }
                { Install-VmTestHost -ComputerName 'HOSTNAME' -Credential $cred } | Should Throw "HOSTNAME doesn't answer PowerShell remoting: The client cannot connect. On HOSTNAME, run 'Enable-PSRemoting -Force'"
                Test-Path $saved | Should Be $false
            }
            # Pester 3.4 keeps a mock an It made for the Its after it, so each one sets Test-WSMan again.
            It "saves nothing when the account can't manage Hyper-V there" {
                Mock -ModuleName VmTest Test-WSMan {}
                Mock -ModuleName VmTest Invoke-OnHost { @{ ok = $false; why = "That account can't manage Hyper-V there: denied. Add it to the Hyper-V Administrators group on that PC, then try again." } }
                { Install-VmTestHost -ComputerName 'HOSTNAME' -Credential $cred } | Should Throw 'Connected to HOSTNAME as HOSTNAME\you, but That account can'
                Test-Path $saved | Should Be $false
                Assert-MockCalled -ModuleName VmTest Sync-HostTool -Times 0 -Exactly -Scope It
            }
            It 'saves the sign-in once the checks pass, and says how to turn it on and off' {
                Mock -ModuleName VmTest Test-WSMan {}
                Mock -ModuleName VmTest Invoke-OnHost { @{ ok = $true; vms = @('ClaudeTesting'); me = 'HOSTNAME\you'; console = 'HOSTNAME\you' } }
                $out = (Install-VmTestHost -ComputerName 'HOSTNAME' -Credential $cred) -join "`n"
                (Import-Clixml $saved).UserName | Should Be 'HOSTNAME\you'
                $out | Should Match 'Its VMs: ClaudeTesting\.'
                $out | Should Match 'setx VMTEST_HOST HOSTNAME'
                $out | Should Match ([regex]::Escape("[Environment]::SetEnvironmentVariable('VMTEST_HOST', `$null, 'User')"))
                $out | Should Not Match 'Note:'
            }
            It "warns when the account isn't the one signed in at the host's screen" {
                Mock -ModuleName VmTest Test-WSMan {}
                Mock -ModuleName VmTest Invoke-OnHost { @{ ok = $true; vms = @(); me = 'HOSTNAME\you'; console = 'HOSTNAME\someone' } }
                (Install-VmTestHost -ComputerName 'HOSTNAME' -Credential $cred) -join "`n" | Should Match 'Note: this signs in to HOSTNAME as HOSTNAME\\you, but HOSTNAME\\someone is signed in'
            }
            It 'gives the whole command line to save a sign-in' {
                Remove-Item $saved -Force
                { InModuleScope VmTest { Get-HostCredential 'HOSTNAME' } } | Should Throw ('powershell -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $root 'vmtest.ps1') + '" host HOSTNAME')
            }
        } finally { $env:LOCALAPPDATA = $before }
    }
    Context 'vmtest.ps1 with VMTEST_HOST set' {
        # vmtest.ps1 itself, with a stand-in module, in its own PowerShell, as Claude runs it.
        $stub = Join-Path $TestDrive 'stub-client'
        New-Item -ItemType Directory $stub -Force | Out-Null
        Copy-Item (Join-Path $root 'vmtest.ps1') $stub
        Set-Content (Join-Path $stub 'VmTest.psm1') @'
function Test-ForwardToHost { $true }
function Invoke-VmTestOnHost {
    param($Command, $Parameters)
    if ($env:VMTEST_STUB_THROW) { throw 'the host went away' }
    [pscustomobject]@{ Output = @("forwarded $Command [$($Parameters.Target)]"); ExitCode = 7 }
}
'@
        function Invoke-Client { $out = & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $stub 'vmtest.ps1') @args 2>&1; [pscustomobject]@{ Out = ($out -join "`n"); Code = $LASTEXITCODE } }
        It 'prints what the host printed and exits with its exit code' {
            $r = Invoke-Client run 'exit 7'
            $r.Out | Should Be 'forwarded run [exit 7]'
            $r.Code | Should Be 7
        }
        It 'says what failed and exits 1 when forwarding fails' {
            $env:VMTEST_STUB_THROW = '1'
            try { $r = Invoke-Client windows } finally { Remove-Item Env:\VMTEST_STUB_THROW }
            $r.Out | Should Be 'vmtest windows failed: the host went away'
            $r.Code | Should Be 1
        }
        It "never forwards 'host'" {
            $r = Invoke-Client host
            $r.Out | Should Match "'vmtest host' needs the name of the PC"
            $r.Code | Should Be 1
        }
    }
    Remove-Item Env:\VMTEST_STATE, Env:\VMTEST_HOST_PATH
    Remove-Variable -Scope Global HostRuns, HostAnswer, PlanArgs, StubTool -ErrorAction SilentlyContinue
}

Remove-Item Env:\VMTEST_VM
Remove-Item Env:\VMTEST_POOL
