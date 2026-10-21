# Runs INSIDE the test VM: records the virtual audio cable while sending scripted keys to the app.
#
#   powershell -File demo.ps1 -Name d01-orient -Steps C:\vmtest\files\d01-orient.steps
#               [-App QuickMail] [-Launch 'C:\vmtest\apps\QuickMail\QuickMail.exe'] [-LaunchArgs '--profileDir x']
#
# Steps file (UTF-8), one per line ('#' lines are comments):
#   k <SendKeys>   press keys, logged      e.g.  k ^+p    k {F6}    k %{DOWN}    k {SPACE}
#   t <text>       type text, logged       one character every 220 ms (faster drops characters)
#   q <SendKeys>   press keys, NOT logged  setup before the first logged key; trimmed from the clip
#   Q <text>       type text, NOT logged
#   w <seconds>    wait                    give the screen reader time to finish speaking
#   x              launch -Launch, logged  for recording an app starting up
# SendKeys can't press the Windows key, or hold the screen reader key (Caps Lock, Insert) as a
# modifier, so screen reader commands like Caps Lock+Tab can't be scripted this way.
#
# -App <process name> brings that app's window to the front first, and fails if it can't.
# Writes C:\vmtest\rec\<Name>.wav and <Name>.keys.txt (ms since start, kind, key; the 'start' line is
# when the recording really began). The producer starts each clip at its first logged step, so
# whatever the screen reader says during setup is cut.
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
public static class Fg {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@

# Parse every step before recording, so a typo fails now rather than halfway through a take.
$plan = New-Object Collections.Generic.List[object]
$n = 0
foreach ($raw in (Get-Content $Steps -Encoding UTF8)) {
    $n++
    $line = $raw.Trim()
    if (-not $line -or $line.StartsWith('#')) { continue }
    $op = $line.Substring(0, 1); $arg = $line.Substring(1).Trim()
    if ($op -cnotin 'k', 't', 'q', 'Q', 'w', 'x') { throw "Steps line ${n}: unknown step '$line' (use k, t, q, Q, w or x)." }
    if ($op -ceq 'w') { $s = 0.0; if (-not [double]::TryParse($arg, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$s)) { throw "Steps line ${n}: 'w' needs a number of seconds." } }
    if ($op -cin 'k', 'q', 't', 'Q' -and -not $arg) { throw "Steps line ${n}: '$op' needs keys or text after it." }
    if ($op -ceq 'x' -and -not $Launch) { throw "Steps line ${n}: 'x' needs -Launch." }
    $plan.Add(@($op, $arg))
}

$out = "C:\vmtest\rec\$Name.wav"
$log = "C:\vmtest\rec\$Name.keys.txt"
New-Item -ItemType Directory -Force C:\vmtest\rec | Out-Null
Remove-Item $out, $log -ErrorAction SilentlyContinue

if ($App) {
    $p = Get-Process $App -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if (-not $p) { throw "No window for a process named '$App' is open in the VM." }
    [void][Fg]::SetForegroundWindow($p.MainWindowHandle)
    Start-Sleep -Milliseconds 400
    if ([Fg]::GetForegroundWindow() -ne $p.MainWindowHandle) {
        # A dialog the app owns counts as the app being in front; anything else does not.
        $fgPid = (Get-Process | Where-Object { $_.MainWindowHandle -eq [Fg]::GetForegroundWindow() } | Select-Object -First 1).Id
        if ($fgPid -ne $p.Id) { throw "Couldn't bring '$App' to the front; something else has focus. Close it and record again." }
    }
}

# Every stream redirected, so ffmpeg never inherits (and holds open) the caller's output file.
$psi = New-Object Diagnostics.ProcessStartInfo $Ffmpeg
$psi.Arguments = "-hide_banner -loglevel error -y -f dshow -audio_buffer_size 50 -i audio=`"$Device`" -ac 1 -ar 48000 `"$out`""
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$ff = [Diagnostics.Process]::Start($psi)
$errTask = $ff.StandardError.ReadToEndAsync()   # drained continuously, so ffmpeg can never block on it
$sw = [Diagnostics.Stopwatch]::StartNew()

# Don't send a key until ffmpeg is really recording. The first capture from a just-installed cable
# can take several seconds to open; keys sent before then would be lost from the clip.
while (-not (Test-Path $out)) {
    if ($ff.HasExited) { throw "ffmpeg stopped before recording (exit $($ff.ExitCode)): $($errTask.Result)" }
    if ($sw.Elapsed.TotalSeconds -gt 15) { $ff.Kill(); throw "ffmpeg didn't start recording '$Device' within 15 seconds." }
    Start-Sleep -Milliseconds 100
}
$entries = New-Object Collections.Generic.List[string]
$entries.Add(("{0}`tstart`trecording" -f $sw.ElapsedMilliseconds))
Start-Sleep -Milliseconds 1200

function Send([string]$keys) { [System.Windows.Forms.SendKeys]::SendWait($keys.Replace('{SPACE}', ' ')) }
function TypeText([string]$text, [int]$gapMs, [bool]$logIt) {
    foreach ($ch in $text.ToCharArray()) {
        $s = [string]$ch
        if ('+^%~(){}[]'.Contains($s)) { $s = '{' + $s + '}' }
        if ($logIt) { $entries.Add(("{0}`tt`t{1}" -f $sw.ElapsedMilliseconds, $ch)) }
        [System.Windows.Forms.SendKeys]::SendWait($s)
        Start-Sleep -Milliseconds $gapMs
    }
}

$failure = $null
try {
    foreach ($step in $plan) {
        if ($ff.HasExited) { throw "ffmpeg stopped recording partway through (exit $($ff.ExitCode)): $($errTask.Result)" }
        $op, $arg = $step
        # Case-sensitive: q/Q and t/T are different operations.
        switch -CaseSensitive ($op) {
            'w' { Start-Sleep -Milliseconds ([int]([double]::Parse($arg, [Globalization.CultureInfo]::InvariantCulture) * 1000)) }
            'k' { $entries.Add(("{0}`tk`t{1}" -f $sw.ElapsedMilliseconds, $arg)); Send $arg }
            'q' { Send $arg }
            't' { TypeText $arg 220 $true }
            'Q' { TypeText $arg 120 $false }
            'x' {
                $entries.Add(("{0}`tx`tlaunch" -f $sw.ElapsedMilliseconds))
                if ($LaunchArgs) { Start-Process $Launch -ArgumentList $LaunchArgs } else { Start-Process $Launch }
            }
        }
    }
    Start-Sleep -Milliseconds ([int]($Tail * 1000))
} catch {
    $failure = $_
} finally {
    # 'q' on stdin makes ffmpeg finish the WAV properly; killing it would leave a broken header.
    if (-not $ff.HasExited) {
        try { $ff.StandardInput.Write('q'); $ff.StandardInput.Flush() } catch { }
        if (-not $ff.WaitForExit(10000)) { $ff.Kill() }
    }
    $entries | Set-Content $log -Encoding UTF8
}
if ($failure) { throw $failure }
if ($ff.ExitCode -ne 0) { throw "ffmpeg exited $($ff.ExitCode): $($errTask.Result)" }
"recorded $Name ($([math]::Round($sw.Elapsed.TotalSeconds, 1)) s)"
