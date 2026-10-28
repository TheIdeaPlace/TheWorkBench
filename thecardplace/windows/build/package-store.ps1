<#
.SYNOPSIS
    Builds the Microsoft Store package: one MSIX per architecture, bundled into
    artifacts\TheCardPlace-<version>.msixbundle for Partner Center.

.DESCRIPTION
    Ported from RSS Quick's build/package-store.ps1, which is live in the Store; its
    docs/STORE-PLAN.md has the reasons. The ones that shape this script:

    - Self-contained, but loose files rather than one executable. The Store updates a package by
      comparing blocks, and a compressed single-file executable changes almost everywhere between
      versions, so every update would download the whole thing.
    - Unsigned. The Store signs what it publishes.
    - makeappx comes from Microsoft's Microsoft.Windows.SDK.BuildTools NuGet package, fetched at
      a pinned version into artifacts\tools, so nothing needs the Windows SDK installed.
    - The logos are made from the iOS app icon, so every platform shows the same one.
    - The version is the one in src\TheCardPlace\TheCardPlace.csproj, the only place it is written.

    PowerShell 5.1-compatible, like publish.ps1.

.PARAMETER Architecture
    x64, arm64, or both (the default).

.PARAMETER Install
    Also register this machine's architecture, so the packaged app can be started from the Start
    menu as "The Card Place (dev)" and tried before anything goes to Microsoft. Needs Developer Mode.
    It is registered from artifacts\store-installed under the identity's Name plus ".Dev", so it
    never replaces a real Store install and a later build does not delete its files. The script
    prints the command that removes it.

.PARAMETER RequireIdentity
    Fail unless build\store\identity.json holds the real values from Partner Center. The release
    workflow passes it, so a tag cannot produce a bundle Partner Center would reject. Without it,
    placeholder values are fine: a package registered with -Install needs no real identity.

.EXAMPLE
    powershell -File build/package-store.ps1 -Architecture x64 -Install
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64', 'both')]
    [string] $Architecture = 'both',

    [switch] $Install,

    [switch] $RequireIdentity
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times slower drawing its bar
Set-StrictMode -Version Latest

# Bumped by hand. Any version carries makeappx; newer only matters if the manifest format moves.
$buildToolsVersion = '10.0.28000.2705'

$repo      = Split-Path -Parent $PSScriptRoot
$project   = Join-Path $repo 'src\TheCardPlace\TheCardPlace.csproj'
$artifacts = Join-Path $repo 'artifacts'
$storeOut  = Join-Path $artifacts 'store'
$template  = Join-Path $PSScriptRoot 'store\AppxManifest.xml'
$iconFile  = Join-Path (Split-Path -Parent $repo) 'ios\TheCardPlace\Assets.xcassets\AppIcon.appiconset\icon-1024.png'
$csproj    = Get-Content $project -Raw
if ($csproj -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') {
    throw "TheCardPlace.csproj should hold a three-part <Version> such as 1.0.0."
}
$version   = $Matches[1]
# MSIX wants four parts, and the Store requires the last to be 0.
$packageVersion = "$version.0"

$identity = Get-Content (Join-Path $PSScriptRoot 'store\identity.json') -Raw | ConvertFrom-Json
$placeholder = "$($identity.Name) $($identity.Publisher)" -match 'FROM-PARTNER-CENTER'
if ($placeholder -and $RequireIdentity) {
    throw 'build\store\identity.json still holds placeholders. Copy Name and Publisher from Partner Center, Product identity.'
}

if ($Architecture -eq 'both') { $targets = @('x64', 'arm64') } else { $targets = @($Architecture) }

Write-Host "The Card Place $version for the Microsoft Store (package version $packageVersion)"
if ($placeholder) { Write-Warning 'identity.json holds placeholders: fine for trying locally, not for Partner Center.' }
Write-Host ''

# --- makeappx --------------------------------------------------------------------------------
$tools = Join-Path $artifacts "tools\sdk-buildtools-$buildToolsVersion"
if (-not (Test-Path $tools)) {
    Write-Host "Fetching Microsoft.Windows.SDK.BuildTools $buildToolsVersion..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $download = "$tools.zip"
    $unpacking = "$tools.partial"
    New-Item -ItemType Directory -Path (Split-Path $tools) -Force | Out-Null
    if (Test-Path $unpacking) { Remove-Item $unpacking -Recurse -Force }
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools/$buildToolsVersion" -OutFile $download -UseBasicParsing
    # Unpacked beside the real name and renamed into place, so an interrupted run leaves nothing
    # a later run would mistake for a finished download.
    Expand-Archive $download $unpacking -Force
    Rename-Item $unpacking (Split-Path -Leaf $tools)
    Remove-Item $download
}
# The one built for this machine, so an ARM64 laptop does not run it under emulation.
if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { $hostArch = 'arm64' } else { $hostArch = 'x64' }
$makeappx = Get-ChildItem (Join-Path $tools 'bin') -Recurse -Filter makeappx.exe |
    Where-Object { $_.Directory.Name -eq $hostArch } | Select-Object -First 1
if (-not $makeappx) { throw "No $hostArch makeappx.exe in $tools." }

# --- logos -----------------------------------------------------------------------------------
Add-Type -AssemblyName System.Drawing
function New-Logo([string] $path, [int] $size) {
    $source = [System.Drawing.Image]::FromFile($iconFile)
    try {
        $bitmap = New-Object System.Drawing.Bitmap $size, $size
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($source, 0, 0, $size, $size)
        $graphics.Dispose()
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
    }
    finally { $source.Dispose() }
}

# --- packages --------------------------------------------------------------------------------
if (Test-Path $storeOut) { Remove-Item $storeOut -Recurse -Force }
$packages = Join-Path $storeOut 'packages'
New-Item -ItemType Directory -Path $packages -Force | Out-Null

foreach ($arch in $targets) {
    $rid    = "win-$arch"
    $layout = Join-Path $storeOut "layout-$rid"

    Write-Host "[$rid] publishing..."
    & dotnet publish $project `
        --configuration Release `
        --runtime $rid `
        --self-contained true `
        --output $layout `
        -p:PublishSingleFile=false `
        -p:DebugType=none `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "[$rid] publish failed." }

    $assets = Join-Path $layout 'Assets'
    New-Item -ItemType Directory -Path $assets -Force | Out-Null
    New-Logo (Join-Path $assets 'Square44x44Logo.png') 44
    New-Logo (Join-Path $assets 'Square150x150Logo.png') 150
    New-Logo (Join-Path $assets 'StoreLogo.png') 50

    $manifest = Get-Content $template -Raw
    $manifest = $manifest.Replace('$NAME$', $identity.Name)
    $manifest = $manifest.Replace('$PUBLISHERDISPLAYNAME$', $identity.PublisherDisplayName)
    $manifest = $manifest.Replace('$PUBLISHER$', $identity.Publisher)
    $manifest = $manifest.Replace('$VERSION$', $packageVersion)
    $manifest = $manifest.Replace('$ARCHITECTURE$', $arch)
    # UTF-8 without a byte-order mark; WriteAllText writes that in 5.1 as well.
    [System.IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest)

    $msix = Join-Path $packages "TheCardPlace-$version-$arch.msix"
    & $makeappx.FullName pack /d $layout /p $msix /o
    if ($LASTEXITCODE -ne 0) { throw "[$rid] makeappx pack failed." }
    Write-Host "[$rid] package -> $(Split-Path -Leaf $msix)"
    Write-Host ''
}

# Only a build of both architectures gets the plain name, the one to upload. A one-architecture
# build (as -Install often is) is named for what it holds, so it can never overwrite the upload
# with a bundle that leaves out most of Windows.
if ($Architecture -eq 'both') { $bundleName = "TheCardPlace-$version.msixbundle" }
else { $bundleName = "TheCardPlace-$version-$Architecture-only.msixbundle" }
$bundle = Join-Path $artifacts $bundleName
& $makeappx.FullName bundle /d $packages /p $bundle /bv $packageVersion /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle failed.' }

$sizeMb = [math]::Round((Get-Item $bundle).Length / 1MB, 1)
Write-Host ''
Write-Host "Done: $(Split-Path -Leaf $bundle)  $sizeMb MB"

# --- try it here ------------------------------------------------------------------------------
if ($Install) {
    $layout = Join-Path $storeOut "layout-win-$hostArch"
    if (-not (Test-Path $layout)) { throw "-Install needs the $hostArch package; build it with -Architecture $hostArch or both." }

    # The Appx cmdlets are Windows PowerShell's; pwsh 7 reaches them through a compatibility session.
    if ($PSVersionTable.PSEdition -eq 'Core') { Import-Module Appx -UseWindowsPowerShell -WarningAction SilentlyContinue }

    # Its own identity, so it can never replace a real Store install of The Card Place on this machine,
    # and "(dev)" in its name, so the two are told apart in the Start menu.
    $devName = "$($identity.Name).Dev"
    $installed = Join-Path $artifacts 'store-installed'

    # Its own folder too, so the next build does not delete the files a registered copy runs from.
    if (Get-Process TheCardPlace -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$installed\*" }) {
        throw 'The dev copy of The Card Place is running. Close it, then run this again.'
    }
    Get-AppxPackage -Name $devName | Remove-AppxPackage
    if (Test-Path $installed) { Remove-Item $installed -Recurse -Force }
    Copy-Item $layout $installed -Recurse

    $manifestPath = Join-Path $installed 'AppxManifest.xml'
    $manifest = [System.IO.File]::ReadAllText($manifestPath)
    $manifest = $manifest.Replace("Name=`"$($identity.Name)`"", "Name=`"$devName`"")
    $manifest = $manifest.Replace('<DisplayName>The Card Place</DisplayName>', '<DisplayName>The Card Place (dev)</DisplayName>')
    $manifest = $manifest.Replace('DisplayName="The Card Place"', 'DisplayName="The Card Place (dev)"')
    [System.IO.File]::WriteAllText($manifestPath, $manifest)

    # Registered in place from the unpacked layout, which is what lets an unsigned package run.
    Add-AppxPackage -Register $manifestPath
    Write-Host "Registered from $installed. Start The Card Place (dev) from the Start menu."
    Write-Host "Remove it with: Get-AppxPackage $devName | Remove-AppxPackage"
}
