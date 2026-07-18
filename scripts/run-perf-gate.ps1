param(
    [int]$WarmupRuns = 1,
    [int]$MeasuredRuns = 5,
    [double]$RegressionTolerancePercent = 5,
    [string]$BaselineCsvPath = ".\\scripts\\benchmark-greedy-stage1-baseline.csv",
    [switch]$EnforceBaseline,
    [switch]$CollectBenchmarkRows,
    [string]$BenchmarkRowsCsvPath,
    [switch]$ListOnly,
    [string]$SummaryJsonPath
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
if ([string]::IsNullOrWhiteSpace($BaselineCsvPath)) {
    throw "BaselineCsvPath must be non-empty."
}
if ($CollectBenchmarkRows -and [string]::IsNullOrWhiteSpace($BenchmarkRowsCsvPath)) {
    throw "BenchmarkRowsCsvPath must be non-empty when CollectBenchmarkRows is set."
}

$args = @(
    '.\\scripts\\benchmark-greedy-stage1.ps1',
    '-WarmupRuns', $WarmupRuns,
    '-MeasuredRuns', $MeasuredRuns,
    '-BaselineCsvPath', $BaselineCsvPath,
    '-RegressionTolerancePercent', $RegressionTolerancePercent
)

if ($EnforceBaseline) {
    $args += '-EnforceBaseline'
}
if ($CollectBenchmarkRows) {
    $args += @('-AsCsv', '-CsvPath', $BenchmarkRowsCsvPath)
}

function Write-SummaryJson {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Summary,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $Summary | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8NoBOM -Path $Path
    Write-Host "Wrote summary JSON: $Path"
}

$resolvedCommand = "pwsh $($args -join ' ')"
$summary = [ordered]@{
    runner = "run-perf-gate"
    timestampUtc = [DateTime]::UtcNow.ToString("o")
    warmupRuns = $WarmupRuns
    measuredRuns = $MeasuredRuns
    regressionTolerancePercent = $RegressionTolerancePercent
    baselineCsvPath = $BaselineCsvPath
    enforceBaseline = [bool]$EnforceBaseline
    collectBenchmarkRows = [bool]$CollectBenchmarkRows
    benchmarkRowsCsvPath = if ($CollectBenchmarkRows) { $BenchmarkRowsCsvPath } else { $null }
    listOnly = [bool]$ListOnly
    command = $resolvedCommand
}

Write-Host "Running perf baseline gate"
Write-Host "WarmupRuns=$WarmupRuns MeasuredRuns=$MeasuredRuns RegressionTolerancePercent=$RegressionTolerancePercent"
Write-Host "BaselineCsvPath=$BaselineCsvPath EnforceBaseline=$EnforceBaseline CollectBenchmarkRows=$CollectBenchmarkRows ListOnly=$ListOnly"
Write-Host "Resolved command: $resolvedCommand"

if ($ListOnly) {
    $summary["executed"] = $false
    $summary["exitCode"] = 0
    Write-SummaryJson -Summary $summary -Path $SummaryJsonPath
    Write-Host "ListOnly set; benchmark script not executed." -ForegroundColor Yellow
    exit 0
}

pwsh @args
$exitCode = $LASTEXITCODE
$summary["executed"] = $true
$summary["exitCode"] = $exitCode
Write-SummaryJson -Summary $summary -Path $SummaryJsonPath

if ($exitCode -ne 0) {
    exit $exitCode
}
