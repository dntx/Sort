param(
    [string]$BaseRef = "origin/main",
    [string]$HeadRef = "HEAD",
    [string]$ChangedFilePath = "docs/counter-guardrails-full-counter-suite-baseline.txt",
    [string]$PrBodyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$changedFiles = @(git diff --name-only "$BaseRef...$HeadRef")
if ($LASTEXITCODE -ne 0) {
    throw "Failed to diff changed files for counter baseline drift review."
}

$normalizedChangedFiles = @($changedFiles | ForEach-Object { $_.Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$baselineChanged = $normalizedChangedFiles -contains $ChangedFilePath

Write-Host "Counter baseline drift review" -ForegroundColor Cyan
Write-Host "BaseRef=$BaseRef HeadRef=$HeadRef ChangedFilePath=$ChangedFilePath" -ForegroundColor Cyan
Write-Host "Baseline changed: $baselineChanged" -ForegroundColor Cyan

if (-not $baselineChanged) {
    Write-Host "Baseline file not changed; no PR-body explanation required." -ForegroundColor Green
    exit 0
}

if ([string]::IsNullOrWhiteSpace($PrBodyPath) -or -not (Test-Path $PrBodyPath)) {
    throw "Baseline file changed, but PR body text was not provided."
}

$prBody = Get-Content -Path $PrBodyPath -Raw
$match = [regex]::Match($prBody, '(?im)^Counter baseline drift:\s*(.+)$')
if (-not $match.Success) {
    throw "PR body must include 'Counter baseline drift: <explanation>' when docs/counter-guardrails-full-counter-suite-baseline.txt changes."
}

$explanation = $match.Groups[1].Value.Trim()
$disallowedValues = @('none', 'n/a', 'na', '<none>', 'no', 'same', 'unchanged', 'tbd', 'todo')
if ([string]::IsNullOrWhiteSpace($explanation) -or ($disallowedValues -contains $explanation.ToLowerInvariant())) {
    throw "Counter baseline drift explanation is missing or not meaningful."
}

Write-Host "Counter baseline drift explanation: $explanation" -ForegroundColor Green
