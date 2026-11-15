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
#   u <text>       type text all as Unicode characters, logged like t (the on-screen keyboard
#                  and braille-input path: no key press per character)
#   w <seconds>    wait                    give the screen reader time to finish speaking
#   x              launch -Launch, logged  for recording an app starting up
#   n <SendKeys>   the NVDA key (Insert) held with these keys, logged, e.g. n {DOWN} (read all),
#                  n {TAB} (read focus), n t (read title). NVDA only; Narrator ignores Insert.
#   e <text>       expect: the screen reader says <text> after the step before (for testing; see
#   e! <text>      expect NOT to hear <text>        srcheck.py). Ignored while recording a demo,
#   e/ <regex>     expect speech matching a regex   so a test can also be a demo.
#   e0             expect silence
# Typed text (t, Q): with NVDA, letters and digits as key presses and spaces and punctuation as
# Unicode characters, which apps and NVDA both handle as typing; with Narrator, all through SendKeys. SendKeys
# can't press the Windows key; the NVDA key is held with the 'n' step instead.
#
# -App <process name> brings that app's window to the front first, and fails if it can't.
# Writes C:\vmtest\rec\<Name>.wav and <Name>.keys.txt: ms since start, kind, key and the steps file's
# line number. The 'start' line is when the recording really began, 'clock' the wall-clock time at
# 0 ms (to line up a screen reader's own log, such as NVDA's), and 'end' when recording stopped. The producer starts each clip at its first logged step, so
# whatever the screen reader says during setup is cut.
param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Steps,
    [string]$App = '',
    [string]$Launch = '',
    [string]$LaunchArgs = '',
    [string]$Device = 'CABLE Output (VB-Audio Virtual Cable)',
    [string]$Ffmpeg = 'C:\vmtest\files\ffmpeg.exe',
    [double]$Tail = 3,
    [string]$SpeechLog = 'C:\vmtest\rec\nvda.log'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Fg {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    // Windows refuses SetForegroundWindow from a background process unless it has just had input.
    // A zero-distance mouse move counts as input and, unlike a key, can't open a menu or reach the
    // app. Skipped when the window is already in front.
    public static bool Bring(IntPtr h) {
        if (GetForegroundWindow() == h) return true;
        Kb.NudgeMouse();
        return SetForegroundWindow(h);
    }
}
public static class Kb {
    [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk; public ushort scan; public uint flags; public uint time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct MI { public int dx; public int dy; public uint data; public uint flags; public uint time; public IntPtr extra; }
    // INPUT for 64-bit PowerShell (as vmtest and PowerShell Direct run): 40 bytes, union at 8.
    [StructLayout(LayoutKind.Explicit, Size = 40)] public struct IN { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KI ki; [FieldOffset(8)] public MI mi; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, IN[] inputs, int size);
    static void Put(IN input) {
        if (SendInput(1, new[] { input }, Marshal.SizeOf(typeof(IN))) != 1)
            throw new InvalidOperationException("SendInput was refused (error " + Marshal.GetLastWin32Error() + "); is this a 32-bit PowerShell?");
    }
    static void Send(ushort vk, ushort scan, uint flags) {
        var a = new IN(); a.type = 1; a.ki.vk = vk; a.ki.scan = scan; a.ki.flags = flags; Put(a);
    }
    public static void NudgeMouse() { var a = new IN(); a.type = 0; a.mi.flags = 1; Put(a); }   // MOUSEEVENTF_MOVE by 0, 0
    // One character as Unicode input (KEYEVENTF_UNICODE). Screen readers see these as typed
    // characters, spaces included: with SendKeys or virtual-key codes, NVDA's word echo ran the
    // words together ("Noonworksforme").
    public static void Char(char c) { Send(0, c, 4); Send(0, c, 4 | 2); }
    // Hold or release Insert, the NVDA key (extended key, so it isn't read as numpad 0).
    public static void Insert(bool up) { Send(0x2D, 0x52, 1u | (up ? 2u : 0u)); }
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
    if ($op -cnotin 'k', 't', 'q', 'Q', 'u', 'w', 'x', 'e', 'n') { throw "Steps line ${n}: unknown step '$line' (use k, t, u, q, Q, w, x, n or e)." }
    if ($op -ceq 'e') {
        if (-not $arg -or $arg -in '!', '/') { throw "Steps line ${n}: '$line' needs the expected speech after it (or e0 for silence)." }
        continue   # expectations are checked afterwards, by srcheck.py
    }
    if ($op -ceq 'w') { $s = 0.0; if (-not [double]::TryParse($arg, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$s)) { throw "Steps line ${n}: 'w' needs a number of seconds." } }
    if ($op -cin 'k', 'q', 't', 'Q', 'u', 'n' -and -not $arg) { throw "Steps line ${n}: '$op' needs keys or text after it." }
    if ($op -ceq 'x' -and -not $Launch) { throw "Steps line ${n}: 'x' needs -Launch." }
    $plan.Add(@($op, $arg, $n))
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
$clock0 = [DateTime]::Now

# Don't send a key until ffmpeg is really recording. The first capture from a just-installed cable
# can take several seconds to open; keys sent before then would be lost from the clip.
while (-not (Test-Path $out)) {
    if ($ff.HasExited) { throw "ffmpeg stopped before recording (exit $($ff.ExitCode)): $($errTask.Result)" }
    if ($sw.Elapsed.TotalSeconds -gt 15) { $ff.Kill(); throw "ffmpeg didn't start recording '$Device' within 15 seconds." }
    Start-Sleep -Milliseconds 100
}
$entries = New-Object Collections.Generic.List[string]
$inv = [Globalization.CultureInfo]::InvariantCulture   # ':' in a format is the culture's time separator otherwise
$entries.Add(("0`tclock`t{0}`t{1}" -f $clock0.ToString('HH:mm:ss.fff', $inv), $clock0.ToString('yyyy-MM-dd', $inv)))
# Where NVDA's log ended when this take began, so only what it appends during the take is read
# (its timestamps carry no date). Absent for Narrator.
if (Test-Path $SpeechLog) { $entries.Add(("0`tlogpos`t{0}" -f (Get-Item $SpeechLog).Length)) }
$entries.Add(("{0}`tstart`trecording" -f $sw.ElapsedMilliseconds))
Start-Sleep -Milliseconds 1200

# How 't' and 'Q' type depends on the screen reader. Under NVDA, letters and digits go as key
# presses and everything else as Unicode characters (see TypeText). Under Narrator it all goes
# through SendKeys, as before: with Unicode spaces, Narrator's word echo broke down and something
# opened the Widgets panel and toggled scan mode mid-take.
$script:hybridTyping = [bool](Get-Process nvda -ErrorAction SilentlyContinue)
function Send([string]$keys) { [System.Windows.Forms.SendKeys]::SendWait($keys.Replace('{SPACE}', ' ')) }
function TypeText([string]$text, [int]$gapMs, [bool]$logIt, [int]$lineNo, [bool]$allUnicode = $false) {
    foreach ($ch in $text.ToCharArray()) {
        if ($logIt) { $entries.Add(("{0}`tt`t{1}`t{2}" -f $sw.ElapsedMilliseconds, $ch, $lineNo)) }
        # Letters and digits as ordinary key presses, so the app sees typing (a key down for each
        # character); everything else (spaces, punctuation) as Unicode characters, so the screen
        # reader sees the word break. With all-SendKeys, NVDA's word echo ran words together
        # ("Noonworksforme"); with all-Unicode, apps that detect typing from key presses (QuickMail's
        # spell check) took each character for caret movement.
        if (-not $allUnicode -and (-not $script:hybridTyping -or [char]::IsLetterOrDigit($ch))) {
            $k = [string]$ch
            if ('+^%~(){}[]'.Contains($k)) { $k = '{' + $k + '}' }   # SendKeys' special characters
            [System.Windows.Forms.SendKeys]::SendWait($k)
        } else { [Kb]::Char($ch) }
        Start-Sleep -Milliseconds $gapMs
    }
}

$failure = $null
try {
    foreach ($step in $plan) {
        if ($ff.HasExited) { throw "ffmpeg stopped recording partway through (exit $($ff.ExitCode)): $($errTask.Result)" }
        $op, $arg, $lineNo = $step
        # Case-sensitive: q/Q and t/T are different operations.
        switch -CaseSensitive ($op) {
            'w' { Start-Sleep -Milliseconds ([int]([double]::Parse($arg, [Globalization.CultureInfo]::InvariantCulture) * 1000)) }
            'k' { $entries.Add(("{0}`tk`t{1}`t{2}" -f $sw.ElapsedMilliseconds, $arg, $lineNo)); Send $arg }
            'q' { Send $arg }
            'n' {
                $entries.Add(("{0}`tk`tNVDA+{1}`t{2}" -f $sw.ElapsedMilliseconds, $arg, $lineNo))
                try { [Kb]::Insert($false); Start-Sleep -Milliseconds 50; Send $arg; Start-Sleep -Milliseconds 50 }
                finally { [Kb]::Insert($true) }
            }
            't' { TypeText $arg 220 $true $lineNo }
            'Q' { TypeText $arg 120 $false 0 }
            'u' { TypeText $arg 220 $true $lineNo $true }
            'x' {
                $entries.Add(("{0}`tx`tlaunch`t{1}" -f $sw.ElapsedMilliseconds, $lineNo))
                $proc = if ($LaunchArgs) { Start-Process $Launch -ArgumentList $LaunchArgs -PassThru } else { Start-Process $Launch -PassThru }
                # Windows won't let a program started from this background process take the
                # foreground by itself, so the screen reader would never hear it open. Bring its
                # first window to the front, as a person launching it would see.
                # The window may belong to a child of the process started: a single-file (PyInstaller)
                # build's bootloader starts the real app as a second process with the same name.
                $exeName = [IO.Path]::GetFileNameWithoutExtension($Launch)
                $since = [DateTime]::Now.AddSeconds(-2)
                $deadline = [DateTime]::Now.AddSeconds(60)
                $window = [IntPtr]::Zero
                while ([DateTime]::Now -lt $deadline -and $window -eq [IntPtr]::Zero) {
                    $win = Get-Process $exeName -ErrorAction SilentlyContinue |
                        Where-Object { $_.MainWindowHandle -ne 0 -and $_.StartTime -ge $since } | Select-Object -First 1
                    if ($win) { $window = $win.MainWindowHandle } else { Start-Sleep -Milliseconds 100 }
                }
                if ($window -ne [IntPtr]::Zero) { [void][Fg]::Bring($window) }
                else {
                    # Say so, rather than leave a silent minute in the clip unexplained: a
                    # single-instance app may have handed off to a copy already running, or the
                    # window's process has another name.
                    Write-Warning "No new '$exeName' window appeared within 60 s of launching $Launch."
                    $entries.Add(("{0}`tnote`tno new {1} window within 60 s" -f $sw.ElapsedMilliseconds, $exeName))
                }
            }
        }
    }
    Start-Sleep -Milliseconds ([int]($Tail * 1000))
} catch {
    $failure = $_
} finally {
    try { [Kb]::Insert($true) } catch { }   # a spare key-up is harmless; a stuck NVDA key is not
    # 'q' on stdin makes ffmpeg finish the WAV properly; killing it would leave a broken header.
    if (-not $ff.HasExited) {
        try { $ff.StandardInput.Write('q'); $ff.StandardInput.Flush() } catch { }
        if (-not $ff.WaitForExit(10000)) { $ff.Kill() }
    }
    $entries.Add(("{0}`tend`trecording" -f $sw.ElapsedMilliseconds))
    # The wall clock again, to catch it being moved mid-take (Hyper-V time sync after a restore):
    # NVDA's timestamps follow the wall clock, the key times follow the stopwatch.
    $entries.Add(("{0}`tclockend`t{1}" -f $sw.ElapsedMilliseconds, [DateTime]::Now.ToString('HH:mm:ss.fff', $inv)))
    $entries | Set-Content $log -Encoding UTF8
}
if ($failure) { throw $failure }
if ($ff.ExitCode -ne 0) { throw "ffmpeg exited $($ff.ExitCode): $($errTask.Result)" }
"recorded $Name ($([math]::Round($sw.Elapsed.TotalSeconds, 1)) s)"
