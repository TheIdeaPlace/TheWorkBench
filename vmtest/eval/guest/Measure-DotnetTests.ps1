# Runs in the VM as the signed-in user: builds and runs test projects from nothing, then prints one
# WORKLOAD line. It exits 0 once the tests ran to the end, whatever they found, because what they
# found goes in the line. A host-level crash leaves no results file, and that's what counts as failing.
#
# The workload puts the list in front of this file when it sends it: $Projects, folders or .csproj files.
$ErrorActionPreference = 'Continue'

# What a .trx file says: how many tests ran, and which failed.
function Read-TrxResult {
    param([Parameter(Mandatory)][string]$Path)
    [xml]$doc = Get-Content $Path -Raw
    $counters = $doc.TestRun.ResultSummary.Counters
    [pscustomobject]@{
        Total  = [int]$counters.total
        Failed = @($doc.TestRun.Results.UnitTestResult | Where-Object { $_ -and $_.outcome -eq 'Failed' } | ForEach-Object { "$($_.testName)" })
    }
}

if ($Projects) {
    $total = 0
    $failed = @()
    $problems = @()
    foreach ($project in $Projects) {
        $results = Join-Path $env:TEMP "memeval-results-$([guid]::NewGuid())"
        & dotnet test $project -c Release --logger 'trx;LogFileName=results.trx' --results-directory $results 2>&1 | Select-Object -Last 6
        $code = $LASTEXITCODE
        $trx = @(Get-ChildItem $results -Recurse -Filter *.trx -ErrorAction SilentlyContinue | Select-Object -First 1)
        if (-not $trx.Count) { $problems += "no results from $project (dotnet test exit $code)"; continue }
        $result = Read-TrxResult $trx[0].FullName
        $total += $result.Total
        $failed += $result.Failed
        "${project}: $($result.Total) tests, $($result.Failed.Count) failed, dotnet test exit $code"
    }
    $line = [ordered]@{ ran = ($problems.Count -eq 0); total = $total; failed = @($failed); problems = @($problems) }
    'WORKLOAD ' + ($line | ConvertTo-Json -Compress -Depth 3)
    exit 0
}
