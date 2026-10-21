# Runs INSIDE the test VM: records the virtual audio cable while sending scripted keys to the app.
#
#   powershell -File demo.ps1 -Name d01-orient -Steps C:\vmtest\files\d01-orient.steps
#               [-App QuickMail] [-Launch 'C:\vmtest\apps\QuickMail\QuickMail.exe'] [-LaunchArgs '--profileDir x']
#
# Steps file, one per line ('#' lines are comments):
#   k <SendKeys>   press keys, logged      e.g.  k ^+p    k {F6}    k %{DOWN}    k {SPACE}
#   t <text>       type text, logged       one character every 220 ms (faster drops characters)
#   q <SendKeys>   press keys, NOT logged  setup before the first logged key; trimmed from the clip
#   Q <text>       type text, NOT logged
#   w <seconds>    wait                    give the screen reader time to finish speaking
#   x              launch -Launch          for recording an app starting up
#
# Writes C:\vmtest\rec\<Name>.wav and <Name>.keys.txt (ms since start, kind, key). The producer starts
# each clip at its first logged key, so anything the screen reader says during setup is cut.
param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Steps,
    [string]$App = '',
    [string]$Launch = '',
    [string]$LaunchArgs = '',
    [string]$Device = 'CABLE Output (VB-Audio Virtual Cable)',
    [string]$Ffmpeg = 'C:\vmtest\files\ffmpeg.exe',
    [double]$Tail = 3
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Fg { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
'@

$lines = Get-Content $Steps
$out = "C:\vmtest\rec\$Name.wav"
$log = "C:\vmtest\rec\$Name.keys.txt"
New-Item -ItemType Directory -Force C:\vmtest\rec | Out-Null

if ($App) {
    $p = Get-Process $App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($p) { [void][Fg]::SetForegroundWindow($p.MainWindowHandle) }
    Start-Sleep -Milliseconds 400
}

# Every stream redirected, so ffmpeg never inherits (and holds open) the caller's output file.
$psi = New-Object Diagnostics.ProcessStartInfo $Ffmpeg
$psi.Arguments = "-hide_banner -loglevel error -y -f dshow -audio_buffer_size 50 -i audio=`"$Device`" -ac 1 -ar 48000 `"$out`""
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
Remove-Item $out -ErrorAction SilentlyContinue
$ff = [Diagnostics.Process]::Start($psi)
$sw = [Diagnostics.Stopwatch]::StartNew()
# Don't send a key until ffmpeg is really recording. The first capture from a just-installed cable
# can take several seconds to open; sending keys before then loses the start of the demo.
while (-not (Test-Path $out)) {
    if ($ff.HasExited) { throw "ffmpeg stopped before recording (exit $($ff.ExitCode)): $($ff.StandardError.ReadToEnd())" }
    if ($sw.Elapsed.TotalSeconds -gt 15) { $ff.Kill(); throw "ffmpeg didn't start recording '$Device' within 15 seconds." }
    Start-Sleep -Milliseconds 100
}
$entries = New-Object Collections.Generic.List[string]
# When the WAV appeared: the producer lines key times up against the audio from this.
$entries.Add(("{0}`tstart`trecording" -f $sw.ElapsedMilliseconds))
Start-Sleep -Milliseconds 1200

function Send([string]$keys) { [System.Windows.Forms.SendKeys]::SendWait($keys.Replace('{SPACE}', ' ')) }
function TypeText([string]$text, [int]$gapMs, $entries) {
    foreach ($ch in $text.ToCharArray()) {
        $s = [string]$ch
        if ('+^%~(){}[]'.Contains($s)) { $s = '{' + $s + '}' }
        if ($null -ne $entries) { $entries.Add(("{0}`tt`t{1}" -f $sw.ElapsedMilliseconds, $ch)) }
        [System.Windows.Forms.SendKeys]::SendWait($s)
        Start-Sleep -Milliseconds $gapMs
    }
}

try {
    foreach ($raw in $lines) {
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith('#')) { continue }
        $op = $line.Substring(0, 1); $arg = $line.Substring(1).Trim()
        # Case-sensitive: q/Q and t/T are different operations.
        switch -CaseSensitive ($op) {
            'w' { Start-Sleep -Milliseconds ([int]([double]$arg * 1000)) }
            'k' { $entries.Add(("{0}`tk`t{1}" -f $sw.ElapsedMilliseconds, $arg)); Send $arg }
            'q' { Send $arg }
            't' { TypeText $arg 220 $entries }
            'Q' { TypeText $arg 120 $null }
            'x' {
                if (-not $Launch) { throw "Step 'x' needs -Launch." }
                $entries.Add(("{0}`tx`tlaunch" -f $sw.ElapsedMilliseconds))
                if ($LaunchArgs) { Start-Process $Launch -ArgumentList $LaunchArgs } else { Start-Process $Launch }
            }
            default { throw "Unknown step '$line'" }
        }
    }
    Start-Sleep -Milliseconds ([int]($Tail * 1000))
} finally {
    # 'q' on stdin makes ffmpeg finish the WAV properly; killing it would leave a broken header.
    $ff.StandardInput.Write('q'); $ff.StandardInput.Flush()
    if (-not $ff.WaitForExit(10000)) { $ff.Kill() }
    $entries | Set-Content $log
}
"recorded $Name ($([math]::Round($sw.Elapsed.TotalSeconds, 1)) s)"
