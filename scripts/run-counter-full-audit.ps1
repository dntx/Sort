param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$ArtifactsDir = ".\\artifacts\\counter-full-audit",
    [string]$MatchedTestsBaselinePath = ".\\docs\\counter-guardrails-full-counter-suite-baseline.txt",
    [switch]$ListOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Read-JsonFile {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return $null
    }

    return Get-Content -Path $Path -Raw | ConvertFrom-Json
}

function Read-TrimmedLines {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return @()
    }

    return @(
        Get-Content -Path $Path |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $Value | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8NoBOM -Path $Path
}

function Write-MarkdownFile {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Lines,
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    @($Lines | ForEach-Object { [string]$_ }) -join "`r`n" | Set-Content -Encoding utf8NoBOM -Path $Path
}

function Compare-MatchedTests {
    param(
        [string[]]$CurrentMatchedTests,
        [string]$BaselinePath
    )

    $baselineExists = -not [string]::IsNullOrWhiteSpace($BaselinePath) -and (Test-Path $BaselinePath)
    $baselineMatchedTests = if ($baselineExists) { @(Read-TrimmedLines -Path $BaselinePath) } else { @() }

    $currentLookup = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($name in $CurrentMatchedTests) {
        [void]$currentLookup.Add($name)
    }

    $baselineLookup = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($name in $baselineMatchedTests) {
        [void]$baselineLookup.Add($name)
    }

    $addedTests = @($CurrentMatchedTests | Sort-Object -Unique | Where-Object { -not $baselineLookup.Contains($_) })
    $removedTests = @($baselineMatchedTests | Sort-Object -Unique | Where-Object { -not $currentLookup.Contains($_) })

    return [ordered]@{
        baselinePath = $BaselinePath
        baselineExists = $baselineExists
        currentCount = @($CurrentMatchedTests | Sort-Object -Unique).Count
        baselineCount = if ($baselineExists) { @($baselineMatchedTests | Sort-Object -Unique).Count } else { $null }
        addedCount = $addedTests.Count
        removedCount = $removedTests.Count
        addedTests = $addedTests
        removedTests = $removedTests
    }
}

New-Item -ItemType Directory -Force -Path $ArtifactsDir | Out-Null

$guardrailSummaryPath = Join-Path $ArtifactsDir "counter-guardrails-summary.json"
$matchedTestsPath = Join-Path $ArtifactsDir "counter-guardrails-matched-tests.txt"
$matchedTestsDiffJsonPath = Join-Path $ArtifactsDir "counter-guardrails-matched-tests-diff.json"
$matchedTestsDiffMdPath = Join-Path $ArtifactsDir "counter-guardrails-matched-tests-diff.md"
$fullAuditSummaryJsonPath = Join-Path $ArtifactsDir "counter-full-audit-summary.json"
$fullAuditSummaryMdPath = Join-Path $ArtifactsDir "counter-full-audit-summary.md"
$snapshotSummaryJsonPath = Join-Path $ArtifactsDir "counter-snapshot-summary.json"
$snapshotSummaryMdPath = Join-Path $ArtifactsDir "counter-snapshot-summary.md"

$guardrailArgs = @(
    '.\\scripts\\run-counter-guardrails.ps1',
    '-Profile', 'full-counter-suite',
    '-Configuration', $Configuration,
    '-MatchedTestsPath', $matchedTestsPath,
    '-SummaryJsonPath', $guardrailSummaryPath
)

if ($ListOnly) {
    $guardrailArgs += '-ListOnly'
}

Write-Host "Running full counter guardrail audit..." -ForegroundColor Cyan
pwsh @guardrailArgs
if ($LASTEXITCODE -ne 0) {
    throw "Counter guardrail audit failed."
}

$guardrailSummary = Read-JsonFile -Path $guardrailSummaryPath
$currentMatchedTests = @(Read-TrimmedLines -Path $matchedTestsPath)
$matchedTestsDiff = Compare-MatchedTests -CurrentMatchedTests $currentMatchedTests -BaselinePath $MatchedTestsBaselinePath
Write-JsonFile -Value $matchedTestsDiff -Path $matchedTestsDiffJsonPath

$matchedDiffMdLines = @(
    '# Counter Guardrails Matched Tests Diff',
    '',
    "- configuration: $Configuration",
    "- baselinePath: $MatchedTestsBaselinePath",
    "- baselineExists: $($matchedTestsDiff.baselineExists)",
    "- currentCount: $($matchedTestsDiff.currentCount)",
    "- baselineCount: $($matchedTestsDiff.baselineCount)",
    "- addedCount: $($matchedTestsDiff.addedCount)",
    "- removedCount: $($matchedTestsDiff.removedCount)"
)

if ($matchedTestsDiff.addedCount -gt 0) {
    $matchedDiffMdLines += ''
    $matchedDiffMdLines += '## Added Tests'
    foreach ($name in $matchedTestsDiff.addedTests) {
        $matchedDiffMdLines += "- $name"
    }
}

if ($matchedTestsDiff.removedCount -gt 0) {
    $matchedDiffMdLines += ''
    $matchedDiffMdLines += '## Removed Tests'
    foreach ($name in $matchedTestsDiff.removedTests) {
        $matchedDiffMdLines += "- $name"
    }
}

if ($matchedTestsDiff.addedCount -eq 0 -and $matchedTestsDiff.removedCount -eq 0) {
    $matchedDiffMdLines += ''
    $matchedDiffMdLines += 'No matched-test drift detected.'
}

Write-MarkdownFile -Lines $matchedDiffMdLines -Path $matchedTestsDiffMdPath

$snapshotSummary = $null
if (-not $ListOnly) {
    Write-Host "Collecting snapshot bundle..." -ForegroundColor Cyan
    pwsh .\scripts\collect-all-counter-snapshots.ps1 -Configuration $Configuration -ArtifactsDir $ArtifactsDir
    if ($LASTEXITCODE -ne 0) {
        throw "Snapshot collection failed."
    }

    $snapshotSummary = Read-JsonFile -Path $snapshotSummaryJsonPath
}

$fullAuditSummary = [ordered]@{
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    configuration = $Configuration
    listOnly = [bool]$ListOnly
    artifactsDir = $ArtifactsDir
    paths = [ordered]@{
        guardrailSummaryJson = $guardrailSummaryPath
        matchedTests = $matchedTestsPath
        matchedTestsDiffJson = $matchedTestsDiffJsonPath
        matchedTestsDiffMd = $matchedTestsDiffMdPath
        snapshotSummaryJson = if (-not $ListOnly) { $snapshotSummaryJsonPath } else { $null }
        snapshotSummaryMd = if (-not $ListOnly) { $snapshotSummaryMdPath } else { $null }
        fullAuditSummaryJson = $fullAuditSummaryJsonPath
        fullAuditSummaryMd = $fullAuditSummaryMdPath
    }
    guardrails = $guardrailSummary
    matchedTestsDiff = $matchedTestsDiff
    snapshotSummary = $snapshotSummary
}

Write-JsonFile -Value $fullAuditSummary -Path $fullAuditSummaryJsonPath

$summaryMdLines = @(
    '# Counter Full Audit Summary',
    '',
    "- generatedAtUtc: $($fullAuditSummary.generatedAtUtc)",
    "- configuration: $Configuration",
    "- listOnly: $ListOnly",
    '',
    '## Guardrails',
    '',
    "- profile: $($guardrailSummary.profile)",
    "- matchedTestsCount: $($guardrailSummary.matchedTestsCount)",
    "- minimumExpectedMatchedTests: $($guardrailSummary.minimumExpectedMatchedTests)",
    "- executed: $($guardrailSummary.executed)",
    "- exitCode: $($guardrailSummary.exitCode)",
    '',
    '## Matched Tests Diff',
    '',
    "- baselineExists: $($matchedTestsDiff.baselineExists)",
    "- currentCount: $($matchedTestsDiff.currentCount)",
    "- baselineCount: $($matchedTestsDiff.baselineCount)",
    "- addedCount: $($matchedTestsDiff.addedCount)",
    "- removedCount: $($matchedTestsDiff.removedCount)"
)

if ($matchedTestsDiff.addedCount -gt 0) {
    $summaryMdLines += ''
    $summaryMdLines += '### Added Tests'
    foreach ($name in $matchedTestsDiff.addedTests) {
        $summaryMdLines += "- $name"
    }
}

if ($matchedTestsDiff.removedCount -gt 0) {
    $summaryMdLines += ''
    $summaryMdLines += '### Removed Tests'
    foreach ($name in $matchedTestsDiff.removedTests) {
        $summaryMdLines += "- $name"
    }
}

if (-not $ListOnly -and $null -ne $snapshotSummary) {
    $summaryMdLines += ''
    $summaryMdLines += '## Snapshot Summary'
    $summaryMdLines += ''
    $summaryMdLines += '| snapshot | rows | positive-delta cells | max positive delta |'
    $summaryMdLines += '| --- | ---: | ---: | ---: |'
    foreach ($row in $snapshotSummary.summary) {
        $summaryMdLines += "| $($row.name) | $($row.rows) | $($row.positiveDeltaCells) | $($row.maxPositiveDelta) |"
    }
}
else {
    $summaryMdLines += ''
    $summaryMdLines += '## Snapshot Summary'
    $summaryMdLines += ''
    $summaryMdLines += 'Snapshot collection skipped because `listOnly=true`.'
}

$summaryMdLines += ''
$summaryMdLines += '## Paths'
$summaryMdLines += "- guardrail summary json: $guardrailSummaryPath"
$summaryMdLines += "- matched tests: $matchedTestsPath"
$summaryMdLines += "- matched tests diff json: $matchedTestsDiffJsonPath"
$summaryMdLines += "- matched tests diff md: $matchedTestsDiffMdPath"
if (-not $ListOnly) {
    $summaryMdLines += "- snapshot summary json: $snapshotSummaryJsonPath"
    $summaryMdLines += "- snapshot summary md: $snapshotSummaryMdPath"
}
$summaryMdLines += "- full audit summary json: $fullAuditSummaryJsonPath"
$summaryMdLines += "- full audit summary md: $fullAuditSummaryMdPath"

Write-MarkdownFile -Lines $summaryMdLines -Path $fullAuditSummaryMdPath

Write-Host "Wrote full audit summary JSON: $fullAuditSummaryJsonPath" -ForegroundColor Green
Write-Host "Wrote full audit summary MD:   $fullAuditSummaryMdPath" -ForegroundColor Green
