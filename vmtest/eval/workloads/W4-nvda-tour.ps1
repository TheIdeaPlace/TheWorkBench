# W4: NVDA reading a tour of QuickMail, with its speech logged and recorded.
param($Vt, $VtOk, [hashtable]$Arguments = @{})
Import-Module (Join-Path $PSScriptRoot '..\VmMemEval.psm1') -Force
Invoke-EvalSpeechWorkload -Vt $Vt -Template (Join-Path $PSScriptRoot '..\guest\Measure-SpeechTake.ps1') -Settings @{ Reader = 'NVDA'; TourMinutes = 5 } -TimeoutSeconds 1200
