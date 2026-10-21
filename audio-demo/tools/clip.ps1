# Record one demo clip in the test VM and bring it back.
#
#   powershell -File clip.ps1 -Project <folder> -Name d01-orient [-App QuickMail]
#               [-Launch 'C:\vmtest\apps\QuickMail\QuickMail.exe'] [-LaunchArgs '--profileDir C:\vmtest\demo-profile']
#               [-NoTranscribe]
#
# Reads <Project>\steps\<Name>.steps, runs it in the VM with vm\demo.ps1 while recording the virtual
# audio cable, copies <Name>.wav and <Name>.keys.txt into <Project>\clips, then prints a Whisper
# transcript so you can check what the screen reader said. Run it from the git repo whose vmtest
# task holds the VM, as with vmtest itself.
param(
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Name,
    [string]$App = '',
    [string]$Launch = '',
    [string]$LaunchArgs = '',
    [switch]$NoTranscribe
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
Invoke-VmTest run -ScriptFile $wrapper -Timeout 900 | Select-Object -Last 2

$clips = Join-Path $Project 'clips'
New-Item -ItemType Directory -Force $clips | Out-Null
Copy-FromTestVM "C:\vmtest\rec\$Name.wav" (Join-Path $clips "$Name.wav")
Copy-FromTestVM "C:\vmtest\rec\$Name.keys.txt" (Join-Path $clips "$Name.keys.txt")
Invoke-VmTest focused | Select-Object -Last 1

if (-not $NoTranscribe) {
    $data = if ($env:AUDIODEMO_DATA) { $env:AUDIODEMO_DATA } else { Join-Path $env:LOCALAPPDATA 'audio-demo' }
    & (Join-Path $data 'venv\Scripts\python.exe') -I (Join-Path $PSScriptRoot 'transcribe.py') (Join-Path $clips "$Name.wav") 2>$null
}
