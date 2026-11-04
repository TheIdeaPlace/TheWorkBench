# W9: the soak: NVDA reading QuickMail for 55 minutes with a recording running, to show leaks and slow
# growth. 'vmtest run' allows an hour at most, so it's under the plan's 60 minutes.
param($Vt, $VtOk, [hashtable]$Arguments = @{})
Import-Module (Join-Path $PSScriptRoot '..\VmMemEval.psm1') -Force
Invoke-EvalSpeechWorkload -Vt $Vt -Template (Join-Path $PSScriptRoot '..\guest\Measure-SpeechTake.ps1') -Settings @{ Reader = 'NVDA'; TourMinutes = 55 } -TimeoutSeconds 3600
