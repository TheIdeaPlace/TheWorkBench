# W5: Narrator reading a tour of QuickMail while it's recorded in real time. Narrator keeps no log, so
# this one measures the recording: capture falling behind is what a shortage would do here.
param($Vt, $VtOk, [hashtable]$Arguments = @{})
Import-Module (Join-Path $PSScriptRoot '..\VmMemEval.psm1') -Force
Invoke-EvalSpeechWorkload -Vt $Vt -Template (Join-Path $PSScriptRoot '..\guest\Measure-SpeechTake.ps1') -Settings @{ Reader = 'Narrator'; TourMinutes = 5 } -TimeoutSeconds 1200
