# Runs in the rig VM with admin rights: installs what every workload needs, once, so it can go into
# Clean and no run spends time or network on it. Safe to run again. Nothing it installs is left running.
# Not 'Stop': native tools write ordinary progress to stderr, which that would turn into a failure.
# Every native call has its exit code checked instead.
$ErrorActionPreference = 'Continue'
$files = 'C:\vmtest\files'
$src = 'C:\vmtest\src'
New-Item $src -ItemType Directory -Force | Out-Null
$arm = $env:PROCESSOR_ARCHITECTURE -eq 'ARM64'
"Architecture: $env:PROCESSOR_ARCHITECTURE"

function Install-Winget([string]$Id, [string[]]$Extra = @()) {
    "winget: $Id"
    $out = & winget install --id $Id --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity @Extra 2>&1
    # 0 is installed; -1978335189 is already installed, nothing newer.
    if ($LASTEXITCODE -notin 0, -1978335189) { $out | Select-Object -Last 8; throw "winget couldn't install $Id (exit $LASTEXITCODE)." }
}
function Update-EnvPath { $env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH', 'User') }

Install-Winget 'Microsoft.DotNet.SDK.10'
Update-EnvPath
if ($arm) {
    # QuickMail.Tests is built for win-x64, so on Arm the test host needs the x64 runtimes beside the
    # Arm SDK. winget can't do it: it sees the same package as already installed.
    $installer = Join-Path $env:TEMP 'dotnet-install.ps1'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    foreach ($runtime in 'windowsdesktop', 'dotnet') {
        "x64 runtime: $runtime"
        & $installer -Channel 10.0 -Architecture x64 -Runtime $runtime -InstallDir 'C:\Program Files\dotnet\x64' | Out-Null
    }
}

Install-Winget 'Python.Python.3.12' @('--scope', 'machine')
Update-EnvPath

# Source. Gitignored credentials aren't in an archive, and nothing here signs in to anything, so they
# get the same empty placeholders QuickMail's own CI writes.
foreach ($name in 'quickmail', 'quickcalc', 'aichat') {
    $target = Join-Path $src $name
    if (-not (Test-Path $target)) { Expand-Archive (Join-Path $files "$name-src.zip") $target -Force -ErrorAction Stop }
}
$services = Join-Path $src 'quickmail\QuickMail\Services'
if (-not (Test-Path $services)) { throw "The QuickMail source isn't at $services." }
Set-Content (Join-Path $services 'GoogleOAuthService.Credentials.cs') @'
namespace QuickMail.Services;
public partial class GoogleOAuthService
{
    private const string ClientId     = "";
    private const string ClientSecret = "";
}
'@ -Encoding UTF8
Set-Content (Join-Path $services 'BugReportService.Credentials.cs') @'
namespace QuickMail.Services;
public partial class BugReportService
{
    private const string RelayUrl = "";
    private const string RelayKey = "";
}
'@ -Encoding UTF8

# Packages only, not builds: every run builds from nothing, but none depends on the network.
"Restoring .NET packages"
& dotnet restore (Join-Path $src 'quickmail\QuickMail.Tests\QuickMail.Tests.csproj') 2>&1 | Select-Object -Last 2
if ($LASTEXITCODE) { throw 'dotnet restore failed for QuickMail.Tests.' }
foreach ($p in 'QuickCalc.Core.Tests', 'QuickCalc.UiTests') {
    & dotnet restore (Join-Path $src "quickcalc\quickcalc\tests\$p") 2>&1 | Select-Object -Last 1
    if ($LASTEXITCODE) { throw "dotnet restore failed for $p." }
}

"Python packages for The Chat Place"
$python = (Get-Command python -ErrorAction Stop).Source
$venv = Join-Path $src 'aichat\.venv'
if (-not (Test-Path $venv)) { & $python -m venv $venv }
& (Join-Path $venv 'Scripts\python.exe') -m pip install --quiet -r (Join-Path $src 'aichat\requirements-dev.txt') 2>&1 | Select-Object -Last 2
if ($LASTEXITCODE) { Write-Warning 'pip install failed for The Chat Place; W3 will show it.' }

# ffmpeg isn't in the payload, because remoting takes hours over a 200 MB file. It's where demo.ps1 looks.
if (-not (Test-Path 'C:\vmtest\files\ffmpeg.exe')) {
    Install-Winget 'Gyan.FFmpeg' @('--scope', 'machine')
    $ffmpeg = Get-ChildItem 'C:\Program Files\WinGet\Packages', "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $ffmpeg) { throw "winget installed Gyan.FFmpeg, but ffmpeg.exe isn't where it should be." }
    Copy-Item $ffmpeg.FullName 'C:\vmtest\files\ffmpeg.exe' -ErrorAction Stop
}

# The recording rig: the audio-demo scripts do the signature checks.
& 'C:\vmtest\files\install-vbcable.ps1'
& 'C:\vmtest\files\install-nvda.ps1' -Installer 'C:\vmtest\files\nvda_2026.2.exe'
New-Item 'C:\vmtest\rec' -ItemType Directory -Force | Out-Null

"dotnet: $(& dotnet --version)"
"python: $(& $python --version 2>&1)"
'Rig installed.'
exit 0
