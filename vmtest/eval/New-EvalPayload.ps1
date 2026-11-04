<#
.SYNOPSIS
Gather what the eval VMs need into one folder, the same files for both hosts.

.DESCRIPTION
Source comes from 'git archive', so it has only what's committed: no secrets, and the same on each
host. Installers come from the pinned downloads the audio-demo rig already checked, or from the app's
GitHub release. Run this on a PC with the repos and audio-demo set up. It writes to
%LOCALAPPDATA%\vmtest\eval\payload, outside any repo. Invoke-OnEvalHost.ps1 -SendPayload copies it
to another host.
#>
[CmdletBinding()]
param(
    [string]$GitHubRoot = (Join-Path $env:USERPROFILE 'GitHub'),
    [string]$QuickMailTag = 'v0.8.67',
    [string]$Folder = (Join-Path $env:LOCALAPPDATA 'vmtest\eval\payload')
)

$ErrorActionPreference = 'Stop'
New-Item $Folder -ItemType Directory -Force | Out-Null

function Export-Source([string]$Repo, [string]$Ref, [string]$Path, [string]$Zip) {
    $out = Join-Path $Folder $Zip
    $arguments = @('-C', (Join-Path $GitHubRoot $Repo), 'archive', '--format=zip', '-o', $out, $Ref)
    if ($Path) { $arguments += $Path }
    & git @arguments
    if ($LASTEXITCODE) { throw "git archive of $Repo failed." }
    "$Zip from $Repo at $(& git -C (Join-Path $GitHubRoot $Repo) rev-parse --short $Ref)"
}

Export-Source 'QuickMail' $QuickMailTag '' 'quickmail-src.zip'
Export-Source 'TheWorkBench' 'origin/main' 'quickcalc' 'quickcalc-src.zip'
Export-Source 'AIChat' 'origin/main' '' 'aichat-src.zip'

$downloads = Join-Path $env:LOCALAPPDATA 'audio-demo\downloads'
Copy-Item (Join-Path $downloads 'vbcable\vbcable.zip') $Folder -Force
Copy-Item (Join-Path $downloads 'nvda\nvda_2026.2.exe') $Folder -Force

$version = $QuickMailTag.TrimStart('v')
foreach ($msi in "QuickMail-$version-win.msi", "QuickMail-$version-win-arm64.msi") {
    if (-not (Test-Path (Join-Path $Folder $msi))) {
        & gh release download $QuickMailTag -R (& git -C (Join-Path $GitHubRoot 'QuickMail') remote get-url origin) -p $msi -D $Folder
        if ($LASTEXITCODE) { throw "Couldn't download $msi." }
    }
}
Get-ChildItem $Folder -File | ForEach-Object { "{0,-40} {1,6:N0} MB" -f $_.Name, ($_.Length / 1MB) }
