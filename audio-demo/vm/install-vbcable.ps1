# Runs INSIDE the test VM, elevated: installs VB-Audio Virtual Cable so the screen reader has a sound
# device to speak into (a Hyper-V VM has none) and ffmpeg has one to record from.
#
# The host (setup-rig.ps1) has already checked the zip's SHA-256 against the pinned value. Here:
#   - the setup program must carry a valid signature from VB-Audio's author before it runs elevated;
#   - the driver catalog is attestation-signed by "Microsoft Windows Hardware Compatibility Publisher".
#     Windows asks "Would you like to install this device software?" for that signer, which would
#     stop a silent install, so its certificate is put in TrustedPublisher for the install only and
#     removed straight afterwards. (Left there, it would let any attestation-signed driver from any
#     vendor install silently in this VM and in every checkpoint taken after.)
# No reboot was needed on Windows 11 (x64 or ARM64; the pack includes an ARM64 driver).
param(
    [string]$Zip = 'C:\vmtest\files\vbcable.zip',
    [string]$Dir = 'C:\vmtest\vbcable',
    [string]$SetupSigner = 'BUREL VINCENT'
)
$ErrorActionPreference = 'Stop'

if (Get-PnpDevice -Class AudioEndpoint -ErrorAction SilentlyContinue | Where-Object FriendlyName -like '*VB-Audio*') {
    'VB-Audio Virtual Cable is already installed.'
    return
}
Expand-Archive $Zip $Dir -Force

$setup = Join-Path $Dir 'VBCABLE_Setup_x64.exe'
$setupSig = Get-AuthenticodeSignature $setup
if ($setupSig.Status -ne 'Valid' -or $setupSig.SignerCertificate.Subject -notmatch [regex]::Escape($SetupSigner)) {
    throw "VBCABLE_Setup_x64.exe is not validly signed by '$SetupSigner' (status $($setupSig.Status), signer $($setupSig.SignerCertificate.Subject)); not running it."
}
$cat = Join-Path $Dir 'vbaudio_cable64_win10.cat'
$sig = Get-AuthenticodeSignature $cat
if ($sig.Status -ne 'Valid') { throw "The driver catalog's signature is $($sig.Status); not installing it." }
$thumb = $sig.SignerCertificate.Thumbprint
$cer = Join-Path $Dir 'signer.cer'
[IO.File]::WriteAllBytes($cer, $sig.SignerCertificate.Export('Cert'))
$alreadyTrusted = Test-Path "Cert:\LocalMachine\TrustedPublisher\$thumb"
if (-not $alreadyTrusted) { certutil -addstore -f TrustedPublisher $cer | Out-Null }
try {
    $p = Start-Process $setup -ArgumentList '-i', '-h' -PassThru
    if (-not $p.WaitForExit(180000)) { throw 'VB-Cable setup did not finish within 3 minutes.' }
} finally {
    if (-not $alreadyTrusted) { certutil -delstore TrustedPublisher $thumb | Out-Null }
}
Start-Sleep 5
$endpoints = Get-PnpDevice -Class AudioEndpoint -ErrorAction SilentlyContinue | Where-Object FriendlyName -like '*VB-Audio*'
if (-not $endpoints) { throw "VB-Cable setup exited $($p.ExitCode) but no VB-Audio device appeared." }
$endpoints | ForEach-Object { "$($_.FriendlyName) [$($_.Status)]" }
