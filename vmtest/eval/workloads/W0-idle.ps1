# W0: the floor. A VM resumed from Clean with nothing running, for 10 minutes. What it uses here is
# what every other workload starts from.
param($Vt, $VtOk, [hashtable]$Arguments = @{})
$minutes = if ($Arguments.Minutes) { $Arguments.Minutes } else { 10 }
Start-Sleep -Seconds ($minutes * 60)
[pscustomobject]@{ exitCode = 0 }
