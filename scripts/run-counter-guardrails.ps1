param(
    [ValidateSet("fast-default", "iterative-frontier", "compact", "full-counter-suite")]
    [string]$Profile = "fast-default",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$filter = switch ($Profile) {
    "fast-default" {
        "FullyQualifiedName~Default_SearchedStateCountStaysWithinBaseline|FullyQualifiedName~Default_OutcomesConstructedStaysWithinBaseline|FullyQualifiedName~Default_CandidateGroupsEnumeratedStaysWithinBaseline|FullyQualifiedName~Default_DuplicateOutcomeSkipsStaysWithinBaseline"
    }
    "iterative-frontier" {
        "FullyQualifiedName~Default_IterativeDeepeningBaselineRemainsStable|FullyQualifiedName~Default_IterativeDeepening_BeatsExactPath"
    }
    "compact" {
        "FullyQualifiedName~Compact_WorkCountersStayWithinBaseline|FullyQualifiedName~Compact_SearchedStateCountStaysWithinBaseline|FullyQualifiedName~Compact_OutcomesConstructedStaysWithinBaseline|FullyQualifiedName~Compact_DuplicateOutcomeSkipsStaysWithinBaseline"
    }
    "full-counter-suite" {
        "FullyQualifiedName~StaysWithinBaseline|FullyQualifiedName~Default_IterativeDeepeningBaselineRemainsStable|FullyQualifiedName~Default_IterativeDeepening_BeatsExactPath"
    }
    default {
        throw "Unknown profile: $Profile"
    }
}

Write-Host "Running counter guardrail profile '$Profile' (Configuration=$Configuration)"
Write-Host "Filter: $filter"

dotnet test .\TopKFinder.Tests\TopKFinder.Tests.csproj --configuration $Configuration --nologo --filter $filter
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
