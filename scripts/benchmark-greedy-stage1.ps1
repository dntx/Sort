param(
    [int]$WarmupRuns = 1,
    [int]$MeasuredRuns = 5,
    [switch]$AsCsv,
    [string]$CsvPath = "",
    [string]$BaselineCsvPath = "",
    [switch]$BaselineOnly,
    [string]$BaselineOutputPath = ".\\scripts\\benchmark-greedy-stage1-baseline.csv",
    [double]$RegressionTolerancePercent = 0,
    [switch]$EnforceBaseline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($WarmupRuns -lt 0) {
    throw "WarmupRuns must be >= 0."
}
if ($MeasuredRuns -lt 1) {
    throw "MeasuredRuns must be >= 1."
}
if ($RegressionTolerancePercent -lt 0) {
    throw "RegressionTolerancePercent must be >= 0."
}
if ($BaselineOnly -and $EnforceBaseline) {
    throw "BaselineOnly cannot be combined with EnforceBaseline."
}
if ($BaselineOnly -and $AsCsv) {
    throw "BaselineOnly cannot be combined with AsCsv. Use BaselineOutputPath."
}
if ($BaselineOnly -and -not [string]::IsNullOrWhiteSpace($BaselineCsvPath)) {
    throw "BaselineOnly cannot be combined with BaselineCsvPath."
}
if ($BaselineOnly -and [string]::IsNullOrWhiteSpace($BaselineOutputPath)) {
    throw "BaselineOutputPath must be non-empty when BaselineOnly is set."
}

function Get-Median {
    param([double[]]$Values)

    $sorted = $Values | Sort-Object
    $n = $sorted.Count
    if ($n % 2 -eq 1) {
        return [double]$sorted[[int]($n / 2)]
    }

    return ([double]$sorted[$n / 2 - 1] + [double]$sorted[$n / 2]) / 2.0
}

function Invoke-CaseRun {
    param(
        [int]$N,
        [int]$M,
        [int]$K
    )

    $cmdOutput = dotnet run --project .\TopKFinder.csproj -- $N $M $K --mode greedy --stage 1 2>&1 | Out-String

    $stageMatch = [regex]::Match($cmdOutput, 'stage greedy-feasible: steps=(\d+), edges=(\d+) \(([0-9.]+)s\)')
    $stepsMatch = [regex]::Match($cmdOutput, 'worst-case steps =\s*(\d+)')
    $edgesMatch = [regex]::Match($cmdOutput, 'total edges =\s*(\d+)')
    $statesMatch = [regex]::Match($cmdOutput, 'output states =\s*(\d+)')

    if (-not $stepsMatch.Success -or -not $edgesMatch.Success -or -not $statesMatch.Success) {
        throw "Failed to parse run output for ($N,$M,$K). Output:`n$cmdOutput"
    }

    if ($stageMatch.Success) {
        $seconds = [double]$stageMatch.Groups[3].Value
    }
    else {
        # Fall back to no timing when stage line is unavailable.
        $seconds = [double]::NaN
    }

    return [pscustomobject]@{
        Steps = [int]$stepsMatch.Groups[1].Value
        Edges = [int]$edgesMatch.Groups[1].Value
        States = [int]$statesMatch.Groups[1].Value
        Seconds = $seconds
    }
}

function Convert-ToDoubleInvariant {
    param([object]$Value)

    if ($null -eq $Value) {
        throw "Cannot convert null to double."
    }

    return [double]::Parse(
        [string]$Value,
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture)
}

function Convert-ToIntInvariant {
    param([object]$Value)

    if ($null -eq $Value) {
        throw "Cannot convert null to int."
    }

    return [int]::Parse(
        [string]$Value,
        [System.Globalization.NumberStyles]::Integer,
        [System.Globalization.CultureInfo]::InvariantCulture)
}

$cases = @(
    @{ N = 20; M = 3; K = 6 },
    @{ N = 22; M = 3; K = 6 },
    @{ N = 24; M = 3; K = 6 },
    @{ N = 16; M = 5; K = 5 }
)

Write-Host "Benchmarking greedy stage 1 (median across runs)" -ForegroundColor Cyan
Write-Host "WarmupRuns=$WarmupRuns MeasuredRuns=$MeasuredRuns" -ForegroundColor Cyan

$results = New-Object System.Collections.Generic.List[object]

foreach ($case in $cases) {
    $n = [int]$case.N
    $m = [int]$case.M
    $k = [int]$case.K

    for ($i = 0; $i -lt $WarmupRuns; $i++) {
        [void](Invoke-CaseRun -N $n -M $m -K $k)
    }

    $sampleTimes = New-Object System.Collections.Generic.List[double]
    $sampleSteps = New-Object System.Collections.Generic.List[int]
    $sampleEdges = New-Object System.Collections.Generic.List[int]
    $sampleStates = New-Object System.Collections.Generic.List[int]

    for ($i = 0; $i -lt $MeasuredRuns; $i++) {
        $run = Invoke-CaseRun -N $n -M $m -K $k
        $sampleTimes.Add($run.Seconds)
        $sampleSteps.Add($run.Steps)
        $sampleEdges.Add($run.Edges)
        $sampleStates.Add($run.States)
    }

    $stepDistinct = @($sampleSteps | Select-Object -Unique).Count
    $edgeDistinct = @($sampleEdges | Select-Object -Unique).Count
    $stateDistinct = @($sampleStates | Select-Object -Unique).Count
    $isStructurallyStable = ($stepDistinct -eq 1 -and $edgeDistinct -eq 1 -and $stateDistinct -eq 1)

    $medianSeconds = Get-Median -Values $sampleTimes.ToArray()
    $avgSeconds = [math]::Round((($sampleTimes | Measure-Object -Average).Average), 3)

    $results.Add([pscustomobject]@{
        Case = "$n,$m,$k"
        Steps = $sampleSteps[0]
        Edges = $sampleEdges[0]
        States = $sampleStates[0]
        MedianSeconds = [math]::Round($medianSeconds, 3)
        AverageSeconds = $avgSeconds
        StableStructure = $isStructurallyStable
        Samples = ($sampleTimes | ForEach-Object { [math]::Round($_, 3) }) -join ","
    })
}

$results | Format-Table -AutoSize

if ($BaselineOnly) {
    $results | Export-Csv -Path $BaselineOutputPath -NoTypeInformation
    Write-Host "Baseline CSV written to: $BaselineOutputPath" -ForegroundColor Green
    return
}

$resolvedBaselinePath = ""
if ([string]::IsNullOrWhiteSpace($BaselineCsvPath)) {
    $defaultBaselinePath = ".\\scripts\\benchmark-greedy-stage1-baseline.csv"
    if (Test-Path $defaultBaselinePath) {
        $resolvedBaselinePath = $defaultBaselinePath
    }
}
else {
    $resolvedBaselinePath = $BaselineCsvPath
}

if (-not [string]::IsNullOrWhiteSpace($resolvedBaselinePath)) {
    if (-not (Test-Path $resolvedBaselinePath)) {
        throw "Baseline CSV not found: $resolvedBaselinePath"
    }

    Write-Host "" 
    Write-Host "Comparing against baseline: $resolvedBaselinePath" -ForegroundColor Cyan

    $baselineRows = Import-Csv -Path $resolvedBaselinePath
    $baselineByCase = @{}
    foreach ($row in $baselineRows) {
        if (-not [string]::IsNullOrWhiteSpace($row.Case)) {
            $baselineByCase[$row.Case] = $row
        }
    }

    $comparison = New-Object System.Collections.Generic.List[object]
    $regressionCount = 0

    foreach ($result in $results) {
        $caseKey = [string]$result.Case
        if (-not $baselineByCase.ContainsKey($caseKey)) {
            $comparison.Add([pscustomobject]@{
                Case = $caseKey
                BaselineMedianSeconds = [double]::NaN
                CurrentMedianSeconds = $result.MedianSeconds
                DeltaSeconds = [double]::NaN
                DeltaPercent = [double]::NaN
                StructureMatch = $false
                Status = "MISSING_BASELINE_CASE"
            })
            $regressionCount++
            continue
        }

        $baseline = $baselineByCase[$caseKey]
        $baselineMedian = Convert-ToDoubleInvariant $baseline.MedianSeconds
        $baselineSteps = Convert-ToIntInvariant $baseline.Steps
        $baselineEdges = Convert-ToIntInvariant $baseline.Edges
        $baselineStates = Convert-ToIntInvariant $baseline.States

        $deltaSeconds = [double]$result.MedianSeconds - $baselineMedian
        $deltaPercent = if ($baselineMedian -gt 0) { ($deltaSeconds / $baselineMedian) * 100.0 } else { [double]::NaN }

        $structureMatch = (
            ([int]$result.Steps -eq $baselineSteps) -and
            ([int]$result.Edges -eq $baselineEdges) -and
            ([int]$result.States -eq $baselineStates)
        )

        $isRegression = $false
        $status = "PASS"
        if (-not $structureMatch) {
            $isRegression = $true
            $status = "FAIL_STRUCTURE_CHANGED"
        }
        elseif ($deltaSeconds -gt 0 -and ($deltaPercent -gt $RegressionTolerancePercent)) {
            $isRegression = $true
            $status = "FAIL_TIME_REGRESSION"
        }

        if ($isRegression) {
            $regressionCount++
        }

        $comparison.Add([pscustomobject]@{
            Case = $caseKey
            BaselineMedianSeconds = [math]::Round($baselineMedian, 3)
            CurrentMedianSeconds = [double]$result.MedianSeconds
            DeltaSeconds = [math]::Round($deltaSeconds, 3)
            DeltaPercent = if ([double]::IsNaN($deltaPercent)) { [double]::NaN } else { [math]::Round($deltaPercent, 2) }
            StructureMatch = $structureMatch
            Status = $status
        })
    }

    $comparison | Format-Table -AutoSize

    Write-Host "Baseline comparison summary: regressions=$regressionCount, tolerance=${RegressionTolerancePercent}%" -ForegroundColor Cyan
    if ($EnforceBaseline -and $regressionCount -gt 0) {
        throw "Baseline comparison failed with $regressionCount regression case(s)."
    }
}

if ($AsCsv) {
    if ([string]::IsNullOrWhiteSpace($CsvPath)) {
        $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $CsvPath = ".\\benchmark-greedy-stage1-$timestamp.csv"
    }

    $results | Export-Csv -Path $CsvPath -NoTypeInformation
    Write-Host "CSV written to: $CsvPath" -ForegroundColor Green
}
