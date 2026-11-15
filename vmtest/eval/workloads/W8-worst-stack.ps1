# W8: the worst stack: NVDA, QuickMail and a recording, with a full build of QuickMail started alongside.
# The build takes a few minutes of the 8-minute tour, so it overlaps the start of it, not all of it. The
# plan named GreenMail's JVM for the extra load. A build asks for more memory than it would, and needs no
# network rig, so this stands in for it. Runs recorded before the build's result was kept don't show whether
# it succeeded.
param($Vt, $VtOk, [hashtable]$Arguments = @{})
Import-Module (Join-Path $PSScriptRoot '..\VmMemEval.psm1') -Force
Invoke-EvalSpeechWorkload -Vt $Vt -Template (Join-Path $PSScriptRoot '..\guest\Measure-SpeechTake.ps1') -Settings @{ Reader = 'NVDA'; TourMinutes = 8; AlongsideBuild = $true } -TimeoutSeconds 1800
