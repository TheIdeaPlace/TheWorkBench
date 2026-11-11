# W1: QuickMail's installer, first run and uninstaller, the commonest light task. Velopack's own 30 s
# limit isn't measured: the MSI has none, and installing Hyper-V Manage isn't part of this workload.
param($Vt, $VtOk, [hashtable]$Arguments = @{})
Import-Module (Join-Path $PSScriptRoot '..\VmMemEval.psm1') -Force
ConvertFrom-WorkloadOutput (& $Vt run -ScriptFile (Join-Path $PSScriptRoot '..\guest\Measure-Install.ps1') -Timeout 1200).Output
