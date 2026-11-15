# Runs in the VM as the signed-in user: The Chat Place's pytest suite, and prints one WORKLOAD line. It
# exits 0 once the tests ran to the end, whatever they found.
$ErrorActionPreference = 'Continue'
$report = Join-Path $env:TEMP 'memeval-pytest.xml'
Push-Location 'C:\vmtest\src\aichat'
& '.venv\Scripts\python.exe' -m pytest -q tests "--junitxml=$report" 2>&1 | Select-Object -Last 4
$code = $LASTEXITCODE
Pop-Location
if (-not (Test-Path $report)) {
    'WORKLOAD ' + (@{ ran = $false; problems = @("pytest left no report (exit $code)") } | ConvertTo-Json -Compress)
    exit 0
}
[xml]$doc = Get-Content $report -Raw
$suite = if ($doc.testsuites) { @($doc.testsuites.testsuite)[0] } else { $doc.testsuite }
$failed = @($suite.testcase | Where-Object { $_ -and ($_.failure -or $_.error) } | ForEach-Object { "$($_.classname).$($_.name)" })
# A test file that wouldn't load is reported as an error, and why is the first thing anyone needs to know.
$first = @($suite.testcase | Where-Object { $_.error } | Select-Object -First 1)
$why = if ($first.Count) { @("first error: " + ("$($first[0].error.message)" -replace '\s+', ' ' -replace '(.{300}).*', '$1')) } else { @() }
'WORKLOAD ' + ([ordered]@{ ran = $true; total = [int]$suite.tests; failed = @($failed); problems = $why } | ConvertTo-Json -Compress -Depth 3)
exit 0
