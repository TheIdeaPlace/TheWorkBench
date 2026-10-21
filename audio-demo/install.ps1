# audio-demo installer.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 [-Force] [-WhatIf]
#
# Sets up everything that runs on this PC (nothing here opens a window):
#   - a Python environment with Kokoro (the host voices) and faster-whisper (transcripts for checking),
#     in %LOCALAPPDATA%\audio-demo\venv, outside OneDrive and outside this repository;
#   - the Kokoro v1.0 model and its 54 voices, in %LOCALAPPDATA%\audio-demo\models (about 340 MB);
#   - the Claude Code skill, in %USERPROFILE%\.claude\skills\audio-demo (left alone if one is already
#     there, since you may have personalised it; -Force replaces it);
#   - AUDIODEMO_HOME, pointing at this folder, so the skill can find the tools.
# The VM side (sound device, ffmpeg, Narrator settings) is set up per task by tools\setup-rig.ps1.
#
# Needs: Python 3.10-3.12 for x64 (the only kind this has been tested with; it also runs on ARM64 PCs), and ffmpeg on PATH (winget install Gyan.FFmpeg).
param(
    [string]$DataDir = $(if ($env:AUDIODEMO_DATA) { $env:AUDIODEMO_DATA } else { Join-Path $env:LOCALAPPDATA 'audio-demo' }),
    [string]$SkillsDir = (Join-Path $env:USERPROFILE '.claude\skills'),
    [switch]$Force,
    [switch]$WhatIf
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
function Act([string]$m) { if ($WhatIf) { Write-Host "would: $m" } else { Write-Host $m } }

Write-Host ''
Write-Host 'audio-demo installer'
Write-Host "  tools:  $here"
Write-Host "  data:   $DataDir"
Write-Host "  skill:  $SkillsDir\audio-demo"
Write-Host ''

# --- ffmpeg -----------------------------------------------------------------------------------
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if ($ffmpeg) { Write-Host "ffmpeg: $ffmpeg" }
else { Write-Warning 'ffmpeg is not on PATH. Install it (winget install Gyan.FFmpeg), open a new terminal, and run this again.' }

# --- Python -----------------------------------------------------------------------------------
function Find-Python {
    foreach ($v in '3.12', '3.11', '3.10') {
        try {
            $exe = & py "-$v" -c "import sys, platform; print(sys.executable if platform.machine() in ('AMD64','x86_64') else '')" 2>$null
            if ($LASTEXITCODE -eq 0 -and $exe) { return $exe.Trim() }
        } catch { }
    }
    foreach ($p in "$env:ProgramFiles\Python312\python.exe", "$env:ProgramFiles\Python311\python.exe", "$env:LOCALAPPDATA\Programs\Python\Python312\python.exe") {
        if (Test-Path $p) { return $p }
    }
}
$venv = Join-Path $DataDir 'venv'
$py = Join-Path $venv 'Scripts\python.exe'
if (Test-Path $py) {
    Write-Host "Python environment: $venv (exists)"
} else {
    $base = Find-Python
    if (-not $base) { throw 'No x64 Python 3.10-3.12 found. Install one: winget install Python.Python.3.12 --architecture x64' }
    Act "create Python environment with $base"
    if (-not $WhatIf) { New-Item -ItemType Directory -Force $DataDir | Out-Null; & $base -m venv $venv; if ($LASTEXITCODE) { throw 'venv creation failed' } }
}
Act 'install Python packages (requirements.txt)'
if (-not $WhatIf) {
    & $py -m pip install --quiet --disable-pip-version-check -r (Join-Path $here 'requirements.txt')
    if ($LASTEXITCODE) { throw 'pip install failed' }
}

# --- Kokoro model ------------------------------------------------------------------------------
$models = Join-Path $DataDir 'models'
$release = 'https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0'
foreach ($f in 'kokoro-v1.0.onnx', 'voices-v1.0.bin') {
    $dest = Join-Path $models $f
    if (Test-Path $dest) { Write-Host "model: $f (exists)"; continue }
    Act "download $f"
    if (-not $WhatIf) {
        New-Item -ItemType Directory -Force $models | Out-Null
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest "$release/$f" -OutFile "$dest.part" -UseBasicParsing
        Move-Item "$dest.part" $dest -Force
    }
}

# --- skill -------------------------------------------------------------------------------------
$skillDest = Join-Path $SkillsDir 'audio-demo'
if ((Test-Path (Join-Path $skillDest 'SKILL.md')) -and -not $Force) {
    Write-Host 'skill: already installed - left alone (-Force replaces it)'
} else {
    Act 'install skill -> skills\audio-demo\SKILL.md'
    if (-not $WhatIf) {
        New-Item -ItemType Directory -Force $skillDest | Out-Null
        Copy-Item (Join-Path $here 'skill\*') $skillDest -Recurse -Force
    }
}

# --- AUDIODEMO_HOME ----------------------------------------------------------------------------
if ([Environment]::GetEnvironmentVariable('AUDIODEMO_HOME', 'User') -ne $here) {
    Act "set AUDIODEMO_HOME=$here (new terminals and Claude Code sessions see it)"
    if (-not $WhatIf) { [Environment]::SetEnvironmentVariable('AUDIODEMO_HOME', $here, 'User') }
}

# --- check -------------------------------------------------------------------------------------
if (-not $WhatIf) {
    $env:AUDIODEMO_DATA = $DataDir
    & $py -I -c "import os, kokoro_onnx, faster_whisper; k = kokoro_onnx.Kokoro(os.path.join(r'$models', 'kokoro-v1.0.onnx'), os.path.join(r'$models', 'voices-v1.0.bin')); print('Kokoro ready:', len(k.get_voices()), 'voices')"
    if ($LASTEXITCODE) { throw 'The installed environment failed its check.' }
}
Write-Host ''
Write-Host 'Done. Hear the voices: python -I tools\samples.py <folder>  (with the venv python above)'
