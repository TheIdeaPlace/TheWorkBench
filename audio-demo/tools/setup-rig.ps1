# Turn the test VM this task holds into a recording rig for screen reader demos.
#
#   powershell -File setup-rig.ps1 [-Voice 'Microsoft Zira - English (United States)'] [-KeepCharacterEcho]
#
# Run it from the git repo whose vmtest task holds the VM ('vmtest begin' first). It:
#   1. installs VB-Audio Virtual Cable in the VM (a Hyper-V VM has no sound device, so Narrator is silent);
#   2. copies ffmpeg in, to record the cable;
#   3. sets Narrator's voice, turns off character echo (typing is heard as words), and stops Narrator
#      Home from opening; these are Narrator's own settings, written before it first starts;
#   4. starts Narrator, records it announcing Notepad opening, and prints the transcript and the
#      voice's pitch, to prove the sound comes through and the voice is the one asked for.
# Safe to run again: each step checks what is already there.
#
# Narrator lists only the OneCore voices: on an English VM, "Microsoft David", "Microsoft Zira" and
# "Microsoft Mark - English (United States)". (Zira Desktop, the SAPI 5 voice, is not offered.)
param(
    [string]$Voice = 'Microsoft Zira - English (United States)',
    [switch]$KeepCharacterEcho,
    [string]$VbCableUrl = 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip',
    # Pack45 as downloaded on 2026-10-20 and again on 2026-10-21. If VB-Audio publishes a new pack,
    # check it and update both the URL and this hash.
    [string]$VbCableSha256 = 'b950e39f01af1d04ea623c8f6d8eb9b6ea5c477c637295fabf20631c85116bfb'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VmFiles.psm1') -Force -DisableNameChecking
$data = if ($env:AUDIODEMO_DATA) { $env:AUDIODEMO_DATA } else { Join-Path $env:LOCALAPPDATA 'audio-demo' }
$vmDir = (Resolve-Path (Join-Path $PSScriptRoot '..\vm')).Path

# 1. VB-Cable. Downloaded to its own folder: it is untrusted until the VM checks its signature.
$dl = Join-Path $data 'downloads\vbcable'
New-Item -ItemType Directory -Force $dl | Out-Null
$zip = Join-Path $dl 'vbcable.zip'
if (-not (Test-Path $zip)) {
    Write-Host "Downloading VB-Cable from $VbCableUrl"
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest $VbCableUrl -OutFile "$zip.part" -UseBasicParsing
    Move-Item "$zip.part" $zip -Force
}
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $VbCableSha256) {
    Remove-Item $zip
    throw "The VB-Cable download's SHA-256 is $hash, not the pinned $VbCableSha256. Deleted it; not installing."
}
Invoke-VmTest push $zip | Out-Null
Write-Host 'Installing VB-Audio Virtual Cable in the VM...'
Invoke-VmTest run -ScriptFile (Join-Path $vmDir 'install-vbcable.ps1') -Elevated -Timeout 400 | Select-Object -Last 4

# 2. ffmpeg. A static build is one self-contained exe; Gyan's (winget Gyan.FFmpeg) is one.
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw 'ffmpeg is not on PATH on this PC. Install it: winget install Gyan.FFmpeg' }
$have = Invoke-InTestVM 'Test-Path C:\vmtest\files\ffmpeg.exe'
if (-not $have) { Write-Host 'Copying ffmpeg into the VM...'; Invoke-VmTest push $ffmpeg | Out-Null }

# 3. Narrator's settings, before it starts.
$echo = if ($KeepCharacterEcho) { 1 } else { 0 }
Invoke-InTestVM @"
Get-Process Narrator -ErrorAction SilentlyContinue | Stop-Process -Force
`$base = 'HKCU:\Software\Microsoft\Narrator'
# Only create missing keys: New-Item -Force on an existing registry key replaces it, values and all.
foreach (`$k in `$base, "`$base\NoRoam", "`$base\NarratorHome") { if (-not (Test-Path `$k)) { New-Item `$k | Out-Null } }
Set-ItemProperty "`$base\NoRoam" SpeechVoice '$($Voice -replace "'", "''")'
Set-ItemProperty `$base EchoChars $echo -Type DWord
Set-ItemProperty `$base EchoWords 1 -Type DWord
Set-ItemProperty "`$base\NarratorHome" AutoStart 0 -Type DWord
"@ | Out-Null
Write-Host "Narrator voice: $Voice; character echo $(if ($echo) { 'on' } else { 'off' }), word echo on."

# 4. Start Narrator, then record it announcing a window opening. (Narrator says nothing while it
#    starts up, so recording its start proves nothing.)
try { Invoke-VmTest launch 'C:\Windows\System32\Narrator.exe' | Out-Null } catch { }   # it has no window to wait for
Start-Sleep 8
# Narrator's first start can open a "Narrator updates" dialog; close it without turning Narrator off.
# (Closing the Narrator Home window, by contrast, turns Narrator off.)
try { Invoke-VmTest invoke 'id:UpdatesNotNowButton' -Window 'Narrator updates' | Out-Null; Write-Host 'Closed the Narrator updates dialog.' } catch { }
$running = Invoke-InTestVM '[bool](Get-Process Narrator -ErrorAction SilentlyContinue)'
if (-not $running) { throw 'Narrator did not start in the VM.' }

$tmp = Join-Path $env:TEMP 'audio-demo-rigcheck'
New-Item -ItemType Directory -Force (Join-Path $tmp 'steps') | Out-Null
Set-Content (Join-Path $tmp 'steps\rig-check.steps') -Value @('w 0.5', 'x', 'w 6') -Encoding ASCII
Write-Host 'Recording Narrator announcing Notepad opening...'
& (Join-Path $PSScriptRoot 'clip.ps1') -Project $tmp -Name 'rig-check' -Launch 'C:\Windows\notepad.exe'
Invoke-InTestVM 'Get-Process Notepad -ErrorAction SilentlyContinue | Stop-Process -Force' | Out-Null
Write-Host "If the transcript above is empty, Narrator isn't reaching the cable. Recording: $tmp\clips\rig-check.wav"

# A transcript can't tell one voice from another, so measure the pitch: Zira, a female voice, sits
# around 180 Hz; David and Mark around 100-130 Hz.
$py = Join-Path $data 'venv\Scripts\python.exe'
$ErrorActionPreference = 'Continue'
& $py -I (Join-Path $PSScriptRoot 'transcribe.py') --pitch-only (Join-Path $tmp 'clips\rig-check.wav') 2>&1 |
    Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }
