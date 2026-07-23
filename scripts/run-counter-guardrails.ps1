param(
    [ValidateSet("fast-default", "iterative-frontier", "compact", "full-counter-suite")]
    [string]$SelectedGuardrail = "fast-default",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$ListOnly,
    [string]$MatchedTestsPath,
    [string]$SummaryJsonPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$testsProject = "TopKFinder.Tests.csproj"
$testsProjectDirectory = ".\tests\TopKFinder.Tests"

$profileMethods = @{
    "fast-default" = @(
        "Default_SearchedStateCountStaysWithinBaseline",
        "Default_OutcomesConstructedStaysWithinBaseline",
        "Default_CandidateGroupsEnumeratedStaysWithinBaseline",
        "Default_DuplicateOutcomeSkipsStaysWithinBaseline"
    )
    "iterative-frontier" = @(
        "Default_IterativeDeepeningBaselineRemainsStable",
        "Default_IterativeDeepening_BeatsExactPath"
    )
    "compact" = @(
        "Compact_WorkCountersStayWithinBaseline",
        "Compact_SearchedStateCountStaysWithinBaseline",
        "Compact_OutcomesConstructedStaysWithinBaseline",
        "Compact_DuplicateOutcomeSkipsStaysWithinBaseline"
    )
    "full-counter-suite" = @(
        "StaysWithinBaseline",
        "Default_IterativeDeepeningBaselineRemainsStable",
        "Default_IterativeDeepening_BeatsExactPath"
    )
}

if (-not $profileMethods.ContainsKey($SelectedGuardrail)) {
    throw "Unknown profile: $SelectedGuardrail"
}

$selectors = @($profileMethods[$SelectedGuardrail] | ForEach-Object { "FullyQualifiedName~$_" })
$filter = ($selectors -join "|")
$minimumMatchedTestsByProfile = @{
    "fast-default" = 60
    "iterative-frontier" = 6
    "compact" = 25
    "full-counter-suite" = 80
}

function Get-MatchedTests {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Configuration,
        [Parameter(Mandatory = $true)]
        [string]$Filter
    )

    Push-Location $testsProjectDirectory
    try {
        $listOutput = dotnet test $testsProject --configuration $Configuration --nologo --list-tests --filter $Filter
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to list tests for filter preflight."
        }
    }
    finally {
        Pop-Location
    }

    $matchedTests = @($listOutput | Where-Object { $_ -match '^\s*StrategyRegressionTests\.' })
    return $matchedTests
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

function Write-MatchedTests {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$MatchedTests,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $MatchedTests | Set-Content -Encoding utf8NoBOM -Path $Path
    Write-Host "Wrote matched test list: $Path"
}

$summary = [ordered]@{
    runner = "run-counter-guardrails"
    timestampUtc = [DateTime]::UtcNow.ToString("o")
    profile = $SelectedGuardrail
    configuration = $Configuration
    listOnly = [bool]$ListOnly
    methods = @($profileMethods[$SelectedGuardrail])
    selectors = @($selectors)
    filter = $filter
    command = "Push-Location $testsProjectDirectory; dotnet test $testsProject --configuration $Configuration --nologo --filter $filter; Pop-Location"
}

Write-Host "Counter guardrail profile '$SelectedGuardrail'" -ForegroundColor Cyan
Write-Host "Configuration=$Configuration ListOnly=$ListOnly" -ForegroundColor Cyan
Write-Host "Method selectors:" -ForegroundColor Cyan
foreach ($method in $profileMethods[$SelectedGuardrail]) {
    Write-Host "  - $method"
}
Write-Host "Filter: $filter"

$matchedTests = @(Get-MatchedTests -Configuration $Configuration -Filter $filter)
$matchedTestCount = $matchedTests.Count
Write-Host "Matched tests (preflight): $matchedTestCount" -ForegroundColor Cyan
$summary["matchedTestsCount"] = $matchedTestCount
Write-MatchedTests -MatchedTests $matchedTests -Path $MatchedTestsPath

if ($minimumMatchedTestsByProfile.ContainsKey($SelectedGuardrail)) {
    $minimumExpected = [int]$minimumMatchedTestsByProfile[$SelectedGuardrail]
    $summary["minimumExpectedMatchedTests"] = $minimumExpected
    if ($matchedTestCount -lt $minimumExpected) {
        throw "Preflight matched only $matchedTestCount tests for profile '$SelectedGuardrail' (minimum expected $minimumExpected). Check filter selectors."
    }
}

if ($ListOnly) {
    $summary["executed"] = $false
    $summary["exitCode"] = 0
    Write-SummaryJson -Summary $summary -Path $SummaryJsonPath
    Write-Host "ListOnly set; no tests executed." -ForegroundColor Yellow
    exit 0
}

Push-Location $testsProjectDirectory
try {
    dotnet test $testsProject --configuration $Configuration --nologo --filter $filter
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

$summary["executed"] = $true
$summary["exitCode"] = $exitCode
Write-SummaryJson -Summary $summary -Path $SummaryJsonPath

if ($exitCode -ne 0) {
    exit $exitCode
}

