# W7: QuickMail's whole unit suite, built from nothing, real WebView2 tests included. On Arm it runs
# as x64 under emulation. Probably the heaviest routine work the VMs do.
param($Vt, $VtOk, [hashtable]$Arguments = @{})
Import-Module (Join-Path $PSScriptRoot '..\VmMemEval.psm1') -Force
Invoke-EvalDotnetWorkload -Vt $Vt -Projects 'C:\vmtest\src\quickmail\QuickMail.Tests\QuickMail.Tests.csproj' -Template (Join-Path $PSScriptRoot '..\guest\Measure-DotnetTests.ps1') -TimeoutSeconds 2400
