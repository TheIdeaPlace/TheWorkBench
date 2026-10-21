# Runs INSIDE the test VM, elevated: installs VB-Audio Virtual Cable so the screen reader has a sound
# device to speak into (a Hyper-V VM has none) and ffmpeg has one to record from.
#
# The driver's signing certificate is trusted first, so Windows installs it without the "Would you
# like to install this device software?" prompt that would otherwise stop a silent install.
# No reboot was needed on Windows 11 (x64 or ARM64; the pack includes an ARM64 driver).
param([string]$Zip = 'C:\vmtest\files\vbcable.zip', [string]$Dir = 'C:\vmtest\vbcable')
$ErrorActionPreference = 'Stop'

if (Get-PnpDevice -Class AudioEndpoint -ErrorAction SilentlyContinue | Where-Object FriendlyName -like '*VB-Audio*') {
    'VB-Audio Virtual Cable is already installed.'
    return
}
Expand-Archive $Zip $Dir -Force
$cat = Get-ChildItem $Dir -Filter 'vbaudio_cable64_win10.cat' | Select-Object -First 1
$sig = Get-AuthenticodeSignature $cat.FullName
if ($sig.Status -ne 'Valid') { throw "The VB-Cable driver catalog's signature is $($sig.Status); not installing it." }
$cer = Join-Path $Dir 'signer.cer'
[IO.File]::WriteAllBytes($cer, $sig.SignerCertificate.Export('Cert'))
certutil -addstore -f TrustedPublisher $cer | Out-Null

$p = Start-Process (Join-Path $Dir 'VBCABLE_Setup_x64.exe') -ArgumentList '-i', '-h' -PassThru
if (-not $p.WaitForExit(180000)) { throw 'VB-Cable setup did not finish within 3 minutes.' }
Start-Sleep 5
$endpoints = Get-PnpDevice -Class AudioEndpoint -ErrorAction SilentlyContinue | Where-Object FriendlyName -like '*VB-Audio*'
if (-not $endpoints) { throw "VB-Cable setup exited $($p.ExitCode) but no VB-Audio device appeared." }
$endpoints | ForEach-Object { "$($_.FriendlyName) [$($_.Status)]" }
