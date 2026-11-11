# Runs INSIDE the test VM, elevated: installs NVDA silently.
#
# The host (setup-rig.ps1) has already checked the installer's SHA-256 against the pinned value;
# here it must also carry a valid signature from NV Access before it runs elevated.
# --install-silent installs without starting the new copy, and without "Use NVDA during sign-in".
param(
    [Parameter(Mandatory)][string]$Installer,
    [string]$Signer = 'NV Access Limited',
    [string]$Version = '2026.2'    # the pinned release; nvda.ini's schemaVersion assumes it
)
$ErrorActionPreference = 'Stop'

foreach ($p in "$env:ProgramFiles\NVDA\nvda.exe", "${env:ProgramFiles(x86)}\NVDA\nvda.exe") {
    if (Test-Path $p) {
        $have = (Get-Item $p).VersionInfo.ProductVersion
        if ($have -notlike "$Version*") {
            Write-Warning "NVDA $have is installed, not the pinned $Version. Kept as it is; uninstall it ('$(Split-Path $p)\uninstall.exe' /S) to get $Version."
        }
        "NVDA is already installed: $p ($have)"
        return
    }
}
$sig = Get-AuthenticodeSignature $Installer
$cn = if ($sig.SignerCertificate) { $sig.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) }
if ($sig.Status -ne 'Valid' -or $cn -ne $Signer) {
    throw "$Installer is not validly signed by '$Signer' (status $($sig.Status), signer '$cn'); not running it."
}
$p = Start-Process $Installer -ArgumentList '--install-silent' -PassThru
if (-not $p.WaitForExit(300000)) { throw 'The NVDA installer did not finish within 5 minutes.' }
foreach ($exe in "$env:ProgramFiles\NVDA\nvda.exe", "${env:ProgramFiles(x86)}\NVDA\nvda.exe") {
    if (Test-Path $exe) { "Installed NVDA: $exe (installer exit $($p.ExitCode))"; return }
}
throw "The NVDA installer exited $($p.ExitCode), but nvda.exe is not in Program Files."
