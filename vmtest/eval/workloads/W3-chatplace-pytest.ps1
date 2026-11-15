# W3: The Chat Place's pytest suite, a frequent Python workload. The plan also named IDT's wx GUI tests
# and the visual probe, which aren't included; those results would need their own workload.
param($Vt, $VtOk, [hashtable]$Arguments = @{})
Import-Module (Join-Path $PSScriptRoot '..\VmMemEval.psm1') -Force
$runtime = & $Vt run -ScriptFile (Join-Path $PSScriptRoot '..\guest\Install-VCRedist.ps1') -Elevated -Timeout 600
if ($runtime.ExitCode -ne 0) { return [pscustomobject]@{ exitCode = -1; problem = "Couldn't install the Visual C++ runtime: $($runtime.Output -join ' ')" } }
ConvertFrom-WorkloadOutput (& $Vt run -ScriptFile (Join-Path $PSScriptRoot '..\guest\Measure-Pytest.ps1') -Timeout 1800).Output
