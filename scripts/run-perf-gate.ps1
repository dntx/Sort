param(
    [int]$WarmupRuns = 1,
    [int]$MeasuredRuns = 5,
    [double]$RegressionTolerancePercent = 5,
    [string]$BaselineCsvPath = ".\\scripts\\benchmark-greedy-stage1-baseline.csv",
    [switch]$EnforceBaseline,
    [switch]$ListOnly
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

Write-Host "Running perf baseline gate"
Write-Host "WarmupRuns=$WarmupRuns MeasuredRuns=$MeasuredRuns RegressionTolerancePercent=$RegressionTolerancePercent"
Write-Host "BaselineCsvPath=$BaselineCsvPath EnforceBaseline=$EnforceBaseline ListOnly=$ListOnly"
Write-Host "Resolved command: pwsh $($args -join ' ')"

if ($ListOnly) {
    Write-Host "ListOnly set; benchmark script not executed." -ForegroundColor Yellow
    exit 0
}

pwsh @args
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
