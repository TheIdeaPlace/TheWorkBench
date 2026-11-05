# W1: QuickMail's installer, first run and uninstaller, the commonest light task. Velopack's own 30 s
# limit isn't measured: the MSI has none, and Hyper-V Manage has no release to install.
param($Vt, $VtOk, [hashtable]$Arguments = @{})
Import-Module (Join-Path $PSScriptRoot '..\VmMemEval.psm1') -Force
ConvertFrom-WorkloadOutput (& $Vt run -ScriptFile (Join-Path $PSScriptRoot '..\guest\Measure-Install.ps1') -Timeout 1200).Output
