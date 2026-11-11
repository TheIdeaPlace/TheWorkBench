# Runs in the VM with admin rights: wxPython can't load without the Visual C++ runtime, and Clean doesn't
# have it, so every test file that imports wx fails to load.
$ErrorActionPreference = 'Continue'
$id = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'Microsoft.VCRedist.2015+.arm64' } else { 'Microsoft.VCRedist.2015+.x64' }
$out = & winget install --id $id --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity 2>&1
if ($LASTEXITCODE -notin 0, -1978335189) { $out | Select-Object -Last 6; "winget couldn't install $id (exit $LASTEXITCODE)."; exit 1 }
"$id is installed."
exit 0
