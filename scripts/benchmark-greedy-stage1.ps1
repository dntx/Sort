param(
    [int]$WarmupRuns = 1,
    [int]$MeasuredRuns = 5,
    [switch]$AsCsv,
    [string]$CsvPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($WarmupRuns -lt 0) {
    throw "WarmupRuns must be >= 0."
}
if ($MeasuredRuns -lt 1) {
    throw "MeasuredRuns must be >= 1."
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

if ($AsCsv) {
    if ([string]::IsNullOrWhiteSpace($CsvPath)) {
        $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $CsvPath = ".\\benchmark-greedy-stage1-$timestamp.csv"
    }

    $results | Export-Csv -Path $CsvPath -NoTypeInformation
    Write-Host "CSV written to: $CsvPath" -ForegroundColor Green
}
