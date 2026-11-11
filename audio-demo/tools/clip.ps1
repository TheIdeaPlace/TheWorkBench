# Record one demo clip in the test VM and bring it back.
#
#   powershell -File clip.ps1 -Project <folder> -Name d01-orient [-App QuickMail]
#               [-Launch 'C:\vmtest\apps\QuickMail\QuickMail.exe'] [-LaunchArgs '--profileDir C:\vmtest\demo-profile']
#               [-NoTranscribe] [-Whisper]
#
# Reads <Project>\steps\<Name>.steps, runs it in the VM with vm\demo.ps1 while recording the virtual
# audio cable, copies <Name>.wav and <Name>.keys.txt into <Project>\clips, then shows what the
# screen reader said:
#   - NVDA (running in the VM, set up by setup-rig.ps1 -ScreenReader NVDA): its own log of every
#     utterance, exact, saved as <Name>.speech.txt. -Whisper also transcribes the audio.
#   - Narrator: a Whisper transcript of the audio (Narrator keeps no log of what it says).
# Run it from the git repo whose vmtest task holds the VM, as with vmtest itself.
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Name,
    [string]$App = '',
    [string]$Launch = '',
    [string]$LaunchArgs = '',
    [switch]$NoTranscribe,
    [switch]$Whisper
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VmFiles.psm1') -Force -DisableNameChecking

$Project = (Resolve-Path $Project).Path
$steps = Join-Path $Project "steps\$Name.steps"
if (-not (Test-Path $steps)) { throw "No steps file: $steps" }
$runner = Join-Path $PSScriptRoot '..\vm\demo.ps1'

Invoke-VmTest push $steps | Out-Null
Invoke-VmTest push (Resolve-Path $runner).Path | Out-Null

# A one-line wrapper avoids quoting the arguments through vmtest's cmd layer.
$q = { param($v) "'" + ($v -replace "'", "''") + "'" }
$call = "& C:\vmtest\files\demo.ps1 -Name $(& $q $Name) -Steps $(& $q "C:\vmtest\files\$Name.steps")"
if ($App) { $call += " -App $(& $q $App)" }
if ($Launch) { $call += " -Launch $(& $q $Launch)" }
if ($LaunchArgs) { $call += " -LaunchArgs $(& $q $LaunchArgs)" }
$wrapper = Join-Path $env:TEMP "audio-demo-run-$Name.ps1"
Set-Content -Path $wrapper -Value $call -Encoding UTF8
# Throws with the VM's full error output if the take failed, so a half take is never copied back.
Invoke-VmTest run -ScriptFile $wrapper -Timeout 900 | Where-Object { $_ -match '^recorded ' }

$clips = Join-Path $Project 'clips'
New-Item -ItemType Directory -Force $clips | Out-Null
Copy-FromTestVM "C:\vmtest\rec\$Name.wav" (Join-Path $clips "$Name.wav")
Copy-FromTestVM "C:\vmtest\rec\$Name.keys.txt" (Join-Path $clips "$Name.keys.txt")
Invoke-VmTest focused | Select-Object -Last 1

$data = if ($env:AUDIODEMO_DATA) { $env:AUDIODEMO_DATA } else { Join-Path $env:LOCALAPPDATA 'audio-demo' }
$py = Join-Path $data 'venv\Scripts\python.exe'

# NVDA logs what it says; take this clip's part of that log as text.
$nvda = Invoke-InTestVM '[bool](Get-Process nvda -ErrorAction SilentlyContinue) -and (Test-Path C:\vmtest\rec\nvda.log)'
if ($nvda) {
    $log = Join-Path $env:TEMP "audio-demo-nvda-$Name.log"
    # NVDA keeps its log open without letting a plain copy read it, so snapshot it in the VM with a
    # read that shares the file, then bring the snapshot back.
    Invoke-InTestVM @'
$in = [IO.File]::Open('C:\vmtest\rec\nvda.log', 'Open', 'Read', 'ReadWrite, Delete')
try { $out = [IO.File]::Create('C:\vmtest\rec\nvda-snapshot.log'); try { $in.CopyTo($out) } finally { $out.Close() } } finally { $in.Close() }
'@ | Out-Null
    Copy-FromTestVM 'C:\vmtest\rec\nvda-snapshot.log' $log
    # Never leave the previous take's speech in place: srcheck would check it as this take's.
    $speechFile = Join-Path $clips "$Name.speech.txt"
    Remove-Item $speechFile -ErrorAction SilentlyContinue
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    # stderr shown as text: speechlog's errors and warnings (no clock entry, the VM clock moving).
    & $py -I (Join-Path $PSScriptRoot 'speechlog.py') $log (Join-Path $clips "$Name.keys.txt") $speechFile 2>&1 |
        ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { "  $($_.Exception.Message)" } else { $_ } }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    Remove-Item $log -ErrorAction SilentlyContinue
    if ($code) { throw "Couldn't read NVDA's speech log for $Name (exit $code); see above." }
    if ((Get-Item $speechFile).Length -eq 0) {
        Write-Warning "NVDA logged nothing during this take. Is it running with --log-level=12 (setup-rig.ps1 -ScreenReader NVDA starts it so)?"
    }
} else {
    Remove-Item (Join-Path $clips "$Name.speech.txt") -ErrorAction SilentlyContinue   # never keep a stale one
}

if (-not $NoTranscribe -and (-not $nvda -or $Whisper)) {
    # Windows PowerShell turns a native command's stderr into an error under 'Stop'; Whisper writes
    # progress and warnings there. Let it through, and judge success by the exit code instead.
    $ErrorActionPreference = 'Continue'
    & $py -I (Join-Path $PSScriptRoot 'transcribe.py') (Join-Path $clips "$Name.wav") 2>&1 |
        Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }
    if ($LASTEXITCODE) { Write-Warning "Transcription failed (exit $LASTEXITCODE). The clip itself is in $clips." }
}
