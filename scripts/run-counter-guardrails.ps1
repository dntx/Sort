param(
    [ValidateSet("fast-default", "iterative-frontier", "compact", "full-counter-suite")]
    [string]$Profile = "fast-default",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$ListOnly
)

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
        "*StaysWithinBaseline",
        "Default_IterativeDeepeningBaselineRemainsStable",
        "Default_IterativeDeepening_BeatsExactPath"
    )
}

if (-not $profileMethods.ContainsKey($Profile)) {
    throw "Unknown profile: $Profile"
}

$selectors = @($profileMethods[$Profile] | ForEach-Object { "FullyQualifiedName~$_" })
$filter = ($selectors -join "|")

Write-Host "Counter guardrail profile '$Profile'" -ForegroundColor Cyan
Write-Host "Configuration=$Configuration ListOnly=$ListOnly" -ForegroundColor Cyan
Write-Host "Method selectors:" -ForegroundColor Cyan
foreach ($method in $profileMethods[$Profile]) {
    Write-Host "  - $method"
}
Write-Host "Filter: $filter"

if ($ListOnly) {
    Write-Host "ListOnly set; no tests executed." -ForegroundColor Yellow
    exit 0
}

dotnet test .\TopKFinder.Tests\TopKFinder.Tests.csproj --configuration $Configuration --nologo --filter $filter
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
