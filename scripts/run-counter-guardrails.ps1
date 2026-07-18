param(
    [ValidateSet("fast-default", "iterative-frontier", "compact", "full-counter-suite")]
    [string]$Profile = "fast-default",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$ListOnly,
    [string]$SummaryJsonPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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

if (-not $profileMethods.ContainsKey($Profile)) {
    throw "Unknown profile: $Profile"
}

$selectors = @($profileMethods[$Profile] | ForEach-Object { "FullyQualifiedName~$_" })
$filter = ($selectors -join "|")
$minimumMatchedTestsByProfile = @{
    "fast-default" = 60
    "iterative-frontier" = 6
    "compact" = 25
    "full-counter-suite" = 80
}

function Get-MatchedTestCount {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Configuration,
        [Parameter(Mandatory = $true)]
        [string]$Filter
    )

    $listOutput = dotnet test .\TopKFinder.Tests\TopKFinder.Tests.csproj --configuration $Configuration --nologo --list-tests --filter $Filter
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to list tests for filter preflight."
    }

    $matchedTests = @($listOutput | Where-Object { $_ -match '^\s*StrategyRegressionTests\.' })
    return $matchedTests.Count
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

$summary = [ordered]@{
    runner = "run-counter-guardrails"
    timestampUtc = [DateTime]::UtcNow.ToString("o")
    profile = $Profile
    configuration = $Configuration
    listOnly = [bool]$ListOnly
    methods = @($profileMethods[$Profile])
    selectors = @($selectors)
    filter = $filter
    command = "dotnet test .\\TopKFinder.Tests\\TopKFinder.Tests.csproj --configuration $Configuration --nologo --filter $filter"
}

Write-Host "Counter guardrail profile '$Profile'" -ForegroundColor Cyan
Write-Host "Configuration=$Configuration ListOnly=$ListOnly" -ForegroundColor Cyan
Write-Host "Method selectors:" -ForegroundColor Cyan
foreach ($method in $profileMethods[$Profile]) {
    Write-Host "  - $method"
}
Write-Host "Filter: $filter"

$matchedTestCount = Get-MatchedTestCount -Configuration $Configuration -Filter $filter
Write-Host "Matched tests (preflight): $matchedTestCount" -ForegroundColor Cyan
$summary["matchedTestsCount"] = $matchedTestCount

if ($minimumMatchedTestsByProfile.ContainsKey($Profile)) {
    $minimumExpected = [int]$minimumMatchedTestsByProfile[$Profile]
    $summary["minimumExpectedMatchedTests"] = $minimumExpected
    if ($matchedTestCount -lt $minimumExpected) {
        throw "Preflight matched only $matchedTestCount tests for profile '$Profile' (minimum expected $minimumExpected). Check filter selectors."
    }
}

if ($ListOnly) {
    $summary["executed"] = $false
    $summary["exitCode"] = 0
    Write-SummaryJson -Summary $summary -Path $SummaryJsonPath
    Write-Host "ListOnly set; no tests executed." -ForegroundColor Yellow
    exit 0
}

dotnet test .\TopKFinder.Tests\TopKFinder.Tests.csproj --configuration $Configuration --nologo --filter $filter
$exitCode = $LASTEXITCODE
$summary["executed"] = $true
$summary["exitCode"] = $exitCode
Write-SummaryJson -Summary $summary -Path $SummaryJsonPath

if ($exitCode -ne 0) {
    exit $exitCode
}
