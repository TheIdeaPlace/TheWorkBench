# W3: The Chat Place's pytest suite, a frequent Python workload. The plan also named IDT's wx GUI tests
# and the visual probe, which aren't included; those results would need their own workload.
param($Vt, $VtOk, [hashtable]$Arguments = @{})
Import-Module (Join-Path $PSScriptRoot '..\VmMemEval.psm1') -Force
ConvertFrom-WorkloadOutput (& $Vt run -ScriptFile (Join-Path $PSScriptRoot '..\guest\Measure-Pytest.ps1') -Timeout 1800).Output
