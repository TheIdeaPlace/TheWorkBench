# Turn the test VM this task holds into a recording rig for screen reader demos.
#
#   powershell -File setup-rig.ps1 [-Voice 'Microsoft Zira - English (United States)'] [-KeepCharacterEcho]
#
# Run it from the git repo whose vmtest task holds the VM ('vmtest begin' first). It:
#   1. installs VB-Audio Virtual Cable in the VM (a Hyper-V VM has no sound device, so Narrator is silent);
#   2. copies ffmpeg in, to record the cable;
#   3. sets Narrator's voice, turns off character echo (typing is heard as words), and stops Narrator
#      Home from opening; these are Narrator's own settings, written before it first starts;
#   4. starts Narrator while recording, and transcribes the result, to prove sound comes through.
# Safe to run again: each step checks what is already there.
#
# Narrator lists only the OneCore voices: on an English VM, "Microsoft David", "Microsoft Zira" and
# "Microsoft Mark - English (United States)". (Zira Desktop, the SAPI 5 voice, is not offered.)
param(
    [string]$Voice = 'Microsoft Zira - English (United States)',
    [switch]$KeepCharacterEcho,
    [string]$VbCableUrl = 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip'
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
    Invoke-WebRequest $VbCableUrl -OutFile $zip -UseBasicParsing
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
New-Item `$base, "`$base\NoRoam", "`$base\NarratorHome" -Force -ErrorAction SilentlyContinue | Out-Null
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
