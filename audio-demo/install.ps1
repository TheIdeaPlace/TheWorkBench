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
# Needs: Python 3.12 for x64 (the pinned numpy has no wheels for 3.10 or 3.11; x64 Python also runs on
# ARM64 PCs, and is the only kind this has been tested with), and ffmpeg on PATH (winget install Gyan.FFmpeg).
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
    foreach ($v in @('3.12')) {
        try {
            $exe = & py "-$v" -c "import sys, platform; print(sys.executable if platform.machine() in ('AMD64','x86_64') else '')" 2>$null
            if ($LASTEXITCODE -eq 0 -and $exe) { return $exe.Trim() }
        } catch { }
    }
    foreach ($p in "$env:ProgramFiles\Python312\python.exe", "$env:LOCALAPPDATA\Programs\Python\Python312\python.exe") {
        if (Test-Path $p) { return $p }
    }
}
$venv = Join-Path $DataDir 'venv'
$py = Join-Path $venv 'Scripts\python.exe'
if (Test-Path $py) {
    Write-Host "Python environment: $venv (exists)"
} else {
    $base = Find-Python
    if (-not $base) { throw 'No x64 Python 3.12 found. Install one: winget install Python.Python.3.12 --architecture x64' }
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
# SHA-256 of the v1.0 files as downloaded on 2026-10-20.
$modelHashes = @{
    'kokoro-v1.0.onnx' = '7d5df8ecf7d4b1878015a32686053fd0eebe2bc377234608764cc0ef3636a6c5'
    'voices-v1.0.bin'  = 'bca610b8308e8d99f32e6fe4197e7ec01679264efed0cac9140fe9c29f1fbf7d'
}
foreach ($f in $modelHashes.Keys) {
    $dest = Join-Path $models $f
    if (Test-Path $dest) { Write-Host "model: $f (exists)"; continue }
    Act "download $f"
    if (-not $WhatIf) {
        New-Item -ItemType Directory -Force $models | Out-Null
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest "$release/$f" -OutFile "$dest.part" -UseBasicParsing
        $hash = (Get-FileHash "$dest.part" -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $modelHashes[$f]) { Remove-Item "$dest.part"; throw "$f downloaded with SHA-256 $hash, not the pinned $($modelHashes[$f]). Deleted it." }
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

# --- AUDIODEMO_DATA (only when it isn't the default) ---------------------------------------------
$defaultData = Join-Path $env:LOCALAPPDATA 'audio-demo'
if ($DataDir -ne $defaultData -and [Environment]::GetEnvironmentVariable('AUDIODEMO_DATA', 'User') -ne $DataDir) {
    Act "set AUDIODEMO_DATA=$DataDir (so the tools find the environment outside the default place)"
    if (-not $WhatIf) { [Environment]::SetEnvironmentVariable('AUDIODEMO_DATA', $DataDir, 'User') }
}

# --- check, and fetch Whisper's model (about 500 MB, from Hugging Face) so the first recording doesn't
# --- stall on it -------------------------------------------------------------------------------
if (-not $WhatIf) {
    $env:AUDIODEMO_DATA = $DataDir
    $ErrorActionPreference = 'Continue'   # Hugging Face writes progress to stderr; judge by exit code
    & $py -I -c "import os; from pathlib import Path; import kokoro_onnx, faster_whisper; m = Path(os.environ['AUDIODEMO_DATA']) / 'models'; k = kokoro_onnx.Kokoro(str(m / 'kokoro-v1.0.onnx'), str(m / 'voices-v1.0.bin')); print('Kokoro ready:', len(k.get_voices()), 'voices'); faster_whisper.WhisperModel('small.en', device='cpu', compute_type='int8'); print('Whisper ready: small.en')" 2>&1 |
        Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }
    if ($LASTEXITCODE) { throw 'The installed environment failed its check.' }
    $ErrorActionPreference = 'Stop'
}
Write-Host ''
Write-Host 'Done. Hear the voices: python -I tools\samples.py <folder>  (with the venv python above)'
