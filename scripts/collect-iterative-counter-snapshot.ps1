param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputJsonPath = ".\\artifacts\\iterative-counter-snapshot.json",
    [string]$OutputCsvPath = ".\\artifacts\\iterative-counter-snapshot.csv"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$cases = @(
    @{ n = 14; m = 5; k = 5; maxStep = 5; rootGroupCount = 5; totalEdges = 72; outputStates = 36; expandedOutputStates = 8; searchedCap = 174; outcomesCap = 2768; candidateCap = 7474 },
    @{ n = 16; m = 5; k = 5; maxStep = 6; rootGroupCount = 5; totalEdges = 122; outputStates = 29; expandedOutputStates = 12; searchedCap = 1633; outcomesCap = 66249; candidateCap = 73060 },
    @{ n = 17; m = 5; k = 5; maxStep = 6; rootGroupCount = 5; totalEdges = 135; outputStates = 40; expandedOutputStates = 13; searchedCap = 1309; outcomesCap = 42641; candidateCap = 67024 },
    @{ n = 18; m = 5; k = 5; maxStep = 6; rootGroupCount = 5; totalEdges = 227; outputStates = 66; expandedOutputStates = 14; searchedCap = 1758; outcomesCap = 78787; candidateCap = 88908 },
    @{ n = 12; m = 6; k = 6; maxStep = 3; rootGroupCount = 6; totalEdges = 16; outputStates = 17; expandedOutputStates = 2; searchedCap = 25; outcomesCap = 66; candidateCap = 65 },
    @{ n = 14; m = 6; k = 6; maxStep = 4; rootGroupCount = 6; totalEdges = 92; outputStates = 23; expandedOutputStates = 3; searchedCap = 45; outcomesCap = 404; candidateCap = 2341 }
)

dotnet build .\src\TopKFinder\TopKFinder.csproj -c $Configuration --nologo | Out-Null

$assemblyPath = Resolve-Path ".\\src\\TopKFinder\\bin\\$Configuration\\net8.0-windows\\TopKFinder.dll"
$assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
$builderType = $assembly.GetType("StrategyBuilder", $true)

function New-Builder {
    param(
        [int]$N,
        [int]$M,
        [int]$K
    )

    $ctorArgs = @($N, $M, $K, [System.Threading.CancellationToken]::None, $null, $false)
    $builder = [Activator]::CreateInstance($builderType, $ctorArgs)

    $forceProp = $builderType.GetProperty("ForceIterativeDeepeningForTesting")
    if ($forceProp -ne $null -and $forceProp.CanWrite) {
        $forceProp.SetValue($builder, $true)
    }

    return $builder
}

function Get-IterativeCounters {
    param(
        [int]$N,
        [int]$M,
        [int]$K
    )

    $builder = New-Builder -N $N -M $M -K $K
    $plan = $builderType.GetMethod("ExecuteStepProofStage").Invoke($builder, @())

    $stats = $plan.GetType().GetProperty("SearchStatistics").GetValue($plan)
    $statsType = $stats.GetType()
    $root = $plan.GetType().GetProperty("Root").GetValue($plan)

    return [ordered]@{
        maxStep = [int]$plan.GetType().GetProperty("MaxStep").GetValue($plan)
        rootGroupCount = [int]$root.GetType().GetProperty("Group").GetValue($root).Count
        totalEdges = [int]$plan.GetType().GetProperty("TotalBranchEdges").GetValue($plan)
        outputStates = [int]$statsType.GetProperty("OutputStates").GetValue($stats)
        expandedOutputStates = [int]$statsType.GetProperty("ExpandedOutputStates").GetValue($stats)
        searched = [int]$statsType.GetProperty("SearchedStates").GetValue($stats)
        outcomes = [int]$statsType.GetProperty("OutcomesConstructed").GetValue($stats)
        candidate = [int]$statsType.GetProperty("CandidateGroupsEnumerated").GetValue($stats)
    }
}

$rows = @()
foreach ($case in $cases) {
    $n = [int]$case.n
    $m = [int]$case.m
    $k = [int]$case.k
    Write-Host "Collecting iterative counters for ($n,$m,$k)..." -ForegroundColor Cyan

    $current = Get-IterativeCounters -N $n -M $m -K $k

    if ($current.maxStep -ne [int]$case.maxStep) { throw "maxStep mismatch for ($n,$m,$k): expected $($case.maxStep), got $($current.maxStep)" }
    if ($current.rootGroupCount -ne [int]$case.rootGroupCount) { throw "root group mismatch for ($n,$m,$k): expected $($case.rootGroupCount), got $($current.rootGroupCount)" }
    if ($current.totalEdges -ne [int]$case.totalEdges) { throw "totalEdges mismatch for ($n,$m,$k): expected $($case.totalEdges), got $($current.totalEdges)" }
    if ($current.outputStates -ne [int]$case.outputStates) { throw "outputStates mismatch for ($n,$m,$k): expected $($case.outputStates), got $($current.outputStates)" }
    if ($current.expandedOutputStates -ne [int]$case.expandedOutputStates) { throw "expandedOutputStates mismatch for ($n,$m,$k): expected $($case.expandedOutputStates), got $($current.expandedOutputStates)" }

    $rows += [pscustomobject]@{
        n = $n
        m = $m
        k = $k
        searched = $current.searched
        searchedCap = [int]$case.searchedCap
        searchedDelta = [int]$case.searchedCap - $current.searched
        outcomes = $current.outcomes
        outcomesCap = [int]$case.outcomesCap
        outcomesDelta = [int]$case.outcomesCap - $current.outcomes
        candidate = $current.candidate
        candidateCap = [int]$case.candidateCap
        candidateDelta = [int]$case.candidateCap - $current.candidate
    }
}

$jsonParent = Split-Path -Parent $OutputJsonPath
if (-not [string]::IsNullOrWhiteSpace($jsonParent)) {
    New-Item -ItemType Directory -Force -Path $jsonParent | Out-Null
}
$csvParent = Split-Path -Parent $OutputCsvPath
if (-not [string]::IsNullOrWhiteSpace($csvParent)) {
    New-Item -ItemType Directory -Force -Path $csvParent | Out-Null
}

$rows | Sort-Object n, m, k | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8NoBOM -Path $OutputJsonPath
$rows | Sort-Object n, m, k | Export-Csv -NoTypeInformation -Encoding utf8NoBOM -Path $OutputCsvPath

Write-Host "Wrote snapshot JSON: $OutputJsonPath" -ForegroundColor Green
Write-Host "Wrote snapshot CSV:  $OutputCsvPath" -ForegroundColor Green

Write-Host ""
Write-Host "Ratchet opportunities (delta > 0):" -ForegroundColor Cyan
$rows | Where-Object { $_.searchedDelta -gt 0 -or $_.outcomesDelta -gt 0 -or $_.candidateDelta -gt 0 } | Sort-Object n,m,k | Format-Table n,m,k,searchedDelta,outcomesDelta,candidateDelta -AutoSize

