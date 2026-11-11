# Turn the test VM this task holds into a recording rig for screen reader demos and tests.
#
#   powershell -File setup-rig.ps1 [-ScreenReader Narrator|NVDA] [-Voice '...'] [-KeepCharacterEcho]
#
# Run it from the git repo whose vmtest task holds the VM ('vmtest begin' first). It:
#   1. installs VB-Audio Virtual Cable in the VM (a Hyper-V VM has no sound device, so a screen
#      reader in it is silent);
#   2. copies ffmpeg in, to record the cable;
#   3. sets up the screen reader, before it first starts:
#      - Narrator: its voice, character echo off (typing is heard as words), Narrator Home off;
#      - NVDA: installs the pinned release silently, and seeds its settings so it starts without the
#        welcome and usage-statistics dialogs or update checks, with typing echoed as words. Its
#        voice is NVDA's own default (Windows OneCore, the system voice), as a new user would hear.
#        NVDA runs with --log-level=12, so it logs every utterance as text in C:\vmtest\rec\nvda.log:
#        clip.ps1 reads that, which is what makes exact speech tests possible (srcheck.py).
#   4. starts the screen reader (stopping the other one: two at once would talk over each other),
#      records it announcing Notepad opening, and prints what it said and the voice's pitch, to
#      prove the sound comes through and the voice is the one asked for.
# Safe to run again: each step checks what is already there. Run it again with the other
# -ScreenReader to switch.
#
# Narrator lists only the OneCore voices: on an English VM, "Microsoft David", "Microsoft Zira" and
# "Microsoft Mark - English (United States)". (Zira Desktop, the SAPI 5 voice, is not offered.)
param(
    [ValidateSet('Narrator', 'NVDA')][string]$ScreenReader = 'Narrator',
    [string]$Voice = 'Microsoft Zira - English (United States)',   # Narrator only
    [switch]$KeepCharacterEcho,
    [string]$VbCableUrl = 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip',
    # Pack45 as downloaded on 2026-10-20 and again on 2026-10-21. If VB-Audio publishes a new pack,
    # check it and update both the URL and this hash.
    [string]$VbCableSha256 = 'b950e39f01af1d04ea623c8f6d8eb9b6ea5c477c637295fabf20631c85116bfb',
    # NVDA 2026.2, the stable release on 2026-10-21, signed by NV Access Limited. To move to a newer
    # release, update both, from https://github.com/nvaccess/nvda/releases.
    [string]$NvdaUrl = 'https://github.com/nvaccess/nvda/releases/download/release-2026.2/nvda_2026.2.exe',
    [string]$NvdaSha256 = 'f3f8d29974a88d687b3c4809be192219ec579c5bdabcda5aaf53635288bca824'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VmFiles.psm1') -Force -DisableNameChecking
$data = if ($env:AUDIODEMO_DATA) { $env:AUDIODEMO_DATA } else { Join-Path $env:LOCALAPPDATA 'audio-demo' }
$vmDir = (Resolve-Path (Join-Path $PSScriptRoot '..\vm')).Path

# Download to its own folder, check the pinned hash, and only then use it.
function Get-Pinned([string]$Url, [string]$Sha256, [string]$Folder, [string]$FileName) {
    $dl = Join-Path $data "downloads\$Folder"
    New-Item -ItemType Directory -Force $dl | Out-Null
    $file = Join-Path $dl $FileName
    if (-not (Test-Path $file)) {
        Write-Host "Downloading $FileName from $Url"
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest $Url -OutFile "$file.part" -UseBasicParsing
        Move-Item "$file.part" $file -Force
    }
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $Sha256) {
        Remove-Item $file
        throw "$FileName's SHA-256 is $hash, not the pinned $Sha256. Deleted it; not installing."
    }
    $file
}

# 1. VB-Cable.
$zip = Get-Pinned $VbCableUrl $VbCableSha256 'vbcable' 'vbcable.zip'
Invoke-VmTest push $zip | Out-Null
Write-Host 'Installing VB-Audio Virtual Cable in the VM...'
Invoke-VmTest run -ScriptFile (Join-Path $vmDir 'install-vbcable.ps1') -Elevated -Timeout 400 | Select-Object -Last 4

# 2. ffmpeg. A static build is one self-contained exe; Gyan's (winget Gyan.FFmpeg) is one.
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { throw 'ffmpeg is not on PATH on this PC. Install it: winget install Gyan.FFmpeg' }
$have = Invoke-InTestVM 'Test-Path C:\vmtest\files\ffmpeg.exe'
if (-not $have) { Write-Host 'Copying ffmpeg into the VM...'; Invoke-VmTest push $ffmpeg | Out-Null }

# 3. The screen reader's settings, before it starts. Both are stopped first.
$echo = if ($KeepCharacterEcho) { 1 } else { 0 }
$nvdaExe = $null
Invoke-InTestVM @'
# Stopped, not asked to quit: 'nvda -q' needs a desktop, and this session has none.
Get-Process Narrator, nvda -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
'@ | Out-Null

if ($ScreenReader -eq 'Narrator') {
    Invoke-InTestVM @"
`$base = 'HKCU:\Software\Microsoft\Narrator'
# Only create missing keys: New-Item -Force on an existing registry key replaces it, values and all.
foreach (`$k in `$base, "`$base\NoRoam", "`$base\NarratorHome") { if (-not (Test-Path `$k)) { New-Item `$k | Out-Null } }
Set-ItemProperty "`$base\NoRoam" SpeechVoice '$($Voice -replace "'", "''")'
Set-ItemProperty `$base EchoChars $echo -Type DWord
Set-ItemProperty `$base EchoWords 1 -Type DWord
Set-ItemProperty "`$base\NarratorHome" AutoStart 0 -Type DWord
"@ | Out-Null
    Write-Host "Narrator voice: $Voice; character echo $(if ($echo) { 'on' } else { 'off' }), word echo on."
} else {
    $installer = Get-Pinned $NvdaUrl $NvdaSha256 'nvda' (Split-Path $NvdaUrl -Leaf)
    Invoke-VmTest push $installer | Out-Null
    Write-Host 'Installing NVDA in the VM...'
    $wrapper = Join-Path $env:TEMP 'audio-demo-install-nvda.ps1'
    Set-Content $wrapper -Encoding UTF8 -Value ("& '{0}' -Installer 'C:\vmtest\files\{1}'" -f 'C:\vmtest\files\install-nvda.ps1', (Split-Path $installer -Leaf))
    Invoke-VmTest push (Join-Path $vmDir 'install-nvda.ps1') | Out-Null
    Invoke-VmTest run -ScriptFile $wrapper -Elevated -Timeout 600 | Select-Object -Last 2
    $nvdaExe = (Invoke-InTestVM 'foreach ($p in "$env:ProgramFiles\NVDA\nvda.exe", "${env:ProgramFiles(x86)}\NVDA\nvda.exe") { if (Test-Path $p) { $p; break } }') | Select-Object -First 1
    if (-not $nvdaExe) { throw 'NVDA installed, but nvda.exe is in neither Program Files folder.' }
    # NVDA's settings for the signed-in user (the one vmtest drives), with NVDA stopped (above):
    # created on the first run, updated in place on later ones.
    $seed = Join-Path $env:TEMP 'audio-demo-seed-nvda.ps1'
    Set-Content $seed -Encoding UTF8 -Value ("& 'C:\vmtest\files\seed-nvda-config.ps1' -CharEcho {0}" -f $echo)
    Invoke-VmTest push (Join-Path $vmDir 'seed-nvda-config.ps1') | Out-Null
    $settings = Invoke-VmTest run -ScriptFile $seed -Timeout 60 | Where-Object { $_ -match '=' }
    Write-Host "NVDA: $nvdaExe, default voice, speech logged to C:\vmtest\rec\nvda.log. nvda.ini now says:"
    $settings | ForEach-Object { Write-Host "  $_" }
    Write-Host '  (speakTyped*: 0 off, 1 in edit controls, 2 always)'
}

# 4. Start it, then record it announcing a window opening. (A screen reader says little or nothing
#    while it starts, so recording its start proves nothing.)
if ($ScreenReader -eq 'Narrator') {
    try { Invoke-VmTest launch 'C:\Windows\System32\Narrator.exe' | Out-Null } catch { }   # it has no window to wait for
    Start-Sleep 8
    # Narrator's first start can open a "Narrator updates" dialog; close it without turning Narrator off.
    # (Closing the Narrator Home window, by contrast, turns Narrator off.)
    try { Invoke-VmTest invoke 'id:UpdatesNotNowButton' -Window 'Narrator updates' | Out-Null; Write-Host 'Closed the Narrator updates dialog.' } catch { }
    $running = Invoke-InTestVM '[bool](Get-Process Narrator -ErrorAction SilentlyContinue)'
} else {
    Invoke-InTestVM 'New-Item -ItemType Directory -Force C:\vmtest\rec | Out-Null' | Out-Null
    try { Invoke-VmTest launch $nvdaExe -Arguments '--log-level=12 -f C:\vmtest\rec\nvda.log' | Out-Null } catch { }
    Start-Sleep 10
    $running = Invoke-InTestVM '[bool](Get-Process nvda -ErrorAction SilentlyContinue)'
}
if (-not $running) { throw "$ScreenReader did not start in the VM." }

# A fresh VM opens Windows' "Get Started" window, which takes focus from everything else.
try { Invoke-VmTest close -Window 'Get Started' | Out-Null; Write-Host 'Closed the Get Started window.' } catch { }

$tmp = Join-Path $env:TEMP 'audio-demo-rigcheck'
New-Item -ItemType Directory -Force (Join-Path $tmp 'steps') | Out-Null
Set-Content (Join-Path $tmp 'steps\rig-check.steps') -Value @('w 0.5', 'x', 'w 6') -Encoding ASCII
Write-Host "Recording $ScreenReader announcing Notepad opening..."
& (Join-Path $PSScriptRoot 'clip.ps1') -Project $tmp -Name 'rig-check' -Launch 'C:\Windows\notepad.exe'
Invoke-InTestVM 'Get-Process Notepad -ErrorAction SilentlyContinue | Stop-Process -Force' | Out-Null
Write-Host "If nothing was said above, $ScreenReader isn't reaching the cable (or, for NVDA, its log). Recording: $tmp\clips\rig-check.wav"

# A transcript can't tell one voice from another, so measure the pitch: Zira, a female voice, sits
# around 180 Hz; David and Mark around 100-130 Hz.
$py = Join-Path $data 'venv\Scripts\python.exe'
$ErrorActionPreference = 'Continue'
& $py -I (Join-Path $PSScriptRoot 'transcribe.py') --pitch-only (Join-Path $tmp 'clips\rig-check.wav') 2>&1 |
    Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }
