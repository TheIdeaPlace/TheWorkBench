# W8: the worst stack: NVDA, QuickMail and a recording, with QuickMail building throughout. The plan
# named GreenMail's JVM for the extra load. A full build asks for more memory than it would, and needs no
# network rig, so this stands in for it.
param($Vt, $VtOk, [hashtable]$Arguments = @{})
Import-Module (Join-Path $PSScriptRoot '..\VmMemEval.psm1') -Force
Invoke-EvalSpeechWorkload -Vt $Vt -Template (Join-Path $PSScriptRoot '..\guest\Measure-SpeechTake.ps1') -Settings @{ Reader = 'NVDA'; TourMinutes = 8; AlongsideBuild = $true } -TimeoutSeconds 1800
