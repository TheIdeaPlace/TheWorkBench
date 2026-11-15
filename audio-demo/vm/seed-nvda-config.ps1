# Runs INSIDE the test VM, as the signed-in user, while NVDA is stopped: sets the NVDA settings the
# rig relies on in %APPDATA%\nvda\nvda.ini. It creates the file if NVDA has never run, and
# otherwise changes only these keys, keeping everything NVDA wrote itself.
# Names from source/config/configSpec.py at NVDA release-2026.2.
param([int]$CharEcho = 0)
$ErrorActionPreference = 'Stop'

$want = [ordered]@{
    'general'  = [ordered]@{ showWelcomeDialogAtStartup = 'False'; playStartAndExitSounds = 'False' }
    'update'   = [ordered]@{ autoCheck = 'False'; startupNotification = 'False'; allowUsageStats = 'False'; askedAllowUsageStats = 'True' }
    # speakTypedCharacters / speakTypedWords: 0 off, 1 in edit controls, 2 always
    'keyboard' = [ordered]@{ speakTypedCharacters = "$CharEcho"; speakTypedWords = '1' }
}

$dir = Join-Path $env:APPDATA 'nvda'
New-Item -ItemType Directory -Force $dir | Out-Null
$ini = Join-Path $dir 'nvda.ini'
$lines = New-Object Collections.Generic.List[string]
if (Test-Path $ini) { foreach ($l in (Get-Content $ini -Encoding UTF8)) { $lines.Add($l) } }
if (-not ($lines | Where-Object { $_ -match '^\s*schemaVersion\s*=' })) { $lines.Insert(0, 'schemaVersion = 24') }

foreach ($section in $want.Keys) {
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].Trim() -eq "[$section]") { $start = $i; break } }
    if ($start -lt 0) { $lines.Add("[$section]"); $start = $lines.Count - 1 }
    foreach ($key in $want[$section].Keys) {
        $value = $want[$section][$key]
        $found = $false
        for ($i = $start + 1; $i -lt $lines.Count -and -not $lines[$i].TrimStart().StartsWith('['); $i++) {
            if ($lines[$i] -match "^\s*$([regex]::Escape($key))\s*=") { $lines[$i] = "`t$key = $value"; $found = $true; break }
        }
        if (-not $found) { $lines.Insert($start + 1, "`t$key = $value") }
    }
}
Set-Content $ini -Value $lines -Encoding UTF8
# Report what the file now says, not what was asked for.
foreach ($key in 'showWelcomeDialogAtStartup', 'autoCheck', 'askedAllowUsageStats', 'speakTypedCharacters', 'speakTypedWords') {
    ($lines | Where-Object { $_ -match "^\s*$key\s*=" } | Select-Object -First 1).Trim()
}
