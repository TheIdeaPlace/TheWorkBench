# W2: QuickCalc's unit and UI Automation tests, built from nothing: a typical app test, with the app
# running and real key presses.
param($Vt, $VtOk, [hashtable]$Arguments = @{})
Import-Module (Join-Path $PSScriptRoot '..\VmMemEval.psm1') -Force
$root = 'C:\vmtest\src\quickcalc\quickcalc\tests'
Invoke-EvalDotnetWorkload -Vt $Vt -Projects "$root\QuickCalc.Core.Tests", "$root\QuickCalc.UiTests" -Template (Join-Path $PSScriptRoot '..\guest\Measure-DotnetTests.ps1') -TimeoutSeconds 1200
