param(
	[ValidateSet("Debug", "Release")]
	[string]$Configuration = "Release",
	[string]$OutputJsonPath = ".\\artifacts\\compact-counter-snapshot.json",
	[string]$OutputCsvPath = ".\\artifacts\\compact-counter-snapshot.csv"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$cases = @(
	@{ n = 9;  m = 3; k = 3; searchedCap = 159; outcomesCap = 5473; duplicateCap = 800;  compactStatesCap = 78;   compactGroupsCap = 1219;   compactStepOptimalCap = 368 },
	@{ n = 11; m = 3; k = 3; searchedCap = 540; outcomesCap = 16220; duplicateCap = 1743; compactStatesCap = 131;  compactGroupsCap = 2847;   compactStepOptimalCap = 647 },
	@{ n = 12; m = 4; k = 4; searchedCap = 471; outcomesCap = 20854; duplicateCap = 5538; compactStatesCap = 46;   compactGroupsCap = 1395;   compactStepOptimalCap = 165 },
	@{ n = 10; m = 3; k = 4; searchedCap = 1088; outcomesCap = 47634; duplicateCap = 5242; compactStatesCap = 324; compactGroupsCap = 11228;  compactStepOptimalCap = 2777 },
	@{ n = 12; m = 4; k = 3; searchedCap = 131; outcomesCap = 6321; duplicateCap = 2566; compactStatesCap = 43;   compactGroupsCap = 811;    compactStepOptimalCap = 199 },
	@{ n = 12; m = 3; k = 3; searchedCap = 538; outcomesCap = 8550; duplicateCap = 622;  compactStatesCap = -1;   compactGroupsCap = -1;     compactStepOptimalCap = -1 },
	@{ n = 8;  m = 4; k = 2; searchedCap = 7; outcomesCap = 30; duplicateCap = 12; compactStatesCap = -1;         compactGroupsCap = -1;     compactStepOptimalCap = -1 },
	@{ n = 10; m = 3; k = 5; searchedCap = 623; outcomesCap = 9835; duplicateCap = 625; compactStatesCap = -1;     compactGroupsCap = -1;     compactStepOptimalCap = -1 },
	@{ n = 13; m = 4; k = 3; searchedCap = 142; outcomesCap = 2385; duplicateCap = 563; compactStatesCap = -1;     compactGroupsCap = -1;     compactStepOptimalCap = -1 },
	@{ n = 12; m = 3; k = 4; searchedCap = -1; outcomesCap = -1; duplicateCap = -1; compactStatesCap = 690;        compactGroupsCap = 40377;  compactStepOptimalCap = 5931 },
	@{ n = 10; m = 2; k = 4; searchedCap = -1; outcomesCap = -1; duplicateCap = -1; compactStatesCap = 4118;       compactGroupsCap = 120336; compactStepOptimalCap = 29291 }
)

dotnet build .\TopKFinder.csproj -c $Configuration --nologo | Out-Null

$assemblyPath = Resolve-Path ".\\bin\\$Configuration\\net8.0-windows\\TopKFinder.dll"
$assembly = [System.Reflection.Assembly]::LoadFrom($assemblyPath)
$builderType = $assembly.GetType("StrategyBuilder", $true)

function New-Builder {
	param(
		[int]$N,
		[int]$M,
		[int]$K
	)

	$ctorArgs = @($N, $M, $K, [System.Threading.CancellationToken]::None, $null, $false)
	return [Activator]::CreateInstance($builderType, $ctorArgs)
}

function Get-CompactCounters {
	param(
		[int]$N,
		[int]$M,
		[int]$K
	)

	$builder = New-Builder -N $N -M $M -K $K
	$plan = $builderType.GetMethod("BuildEdgeCompactStage").Invoke($builder, @())
	$stats = $plan.GetType().GetProperty("SearchStatistics").GetValue($plan)
	$statsType = $stats.GetType()
	$diag = $statsType.GetProperty("Diagnostics").GetValue($stats)
	$diagType = $diag.GetType()

	return [ordered]@{
		searched = [int]$statsType.GetProperty("SearchedStates").GetValue($stats)
		outcomes = [int]$statsType.GetProperty("OutcomesConstructed").GetValue($stats)
		duplicate = [int]$diagType.GetProperty("DuplicateOutcomeSkips").GetValue($diag)
		compactStatesSolved = [int]$statsType.GetProperty("CompactStatesSolved").GetValue($stats)
		compactGroupsEnumerated = [int]$statsType.GetProperty("CompactGroupsEnumerated").GetValue($stats)
		compactStepOptimalGroups = [int]$statsType.GetProperty("CompactStepOptimalGroups").GetValue($stats)
	}
}

$rows = @()
foreach ($case in $cases) {
	$n = [int]$case.n
	$m = [int]$case.m
	$k = [int]$case.k
	Write-Host "Collecting compact counters for ($n,$m,$k)..." -ForegroundColor Cyan

	$current = Get-CompactCounters -N $n -M $m -K $k

	$rows += [pscustomobject]@{
		n = $n
		m = $m
		k = $k
		searched = $current.searched
		searchedCap = [int]$case.searchedCap
		searchedDelta = if ([int]$case.searchedCap -ge 0) { [int]$case.searchedCap - $current.searched } else { $null }
		outcomes = $current.outcomes
		outcomesCap = [int]$case.outcomesCap
		outcomesDelta = if ([int]$case.outcomesCap -ge 0) { [int]$case.outcomesCap - $current.outcomes } else { $null }
		duplicate = $current.duplicate
		duplicateCap = [int]$case.duplicateCap
		duplicateDelta = if ([int]$case.duplicateCap -ge 0) { [int]$case.duplicateCap - $current.duplicate } else { $null }
		compactStatesSolved = $current.compactStatesSolved
		compactStatesCap = [int]$case.compactStatesCap
		compactStatesDelta = if ([int]$case.compactStatesCap -ge 0) { [int]$case.compactStatesCap - $current.compactStatesSolved } else { $null }
		compactGroupsEnumerated = $current.compactGroupsEnumerated
		compactGroupsCap = [int]$case.compactGroupsCap
		compactGroupsDelta = if ([int]$case.compactGroupsCap -ge 0) { [int]$case.compactGroupsCap - $current.compactGroupsEnumerated } else { $null }
		compactStepOptimalGroups = $current.compactStepOptimalGroups
		compactStepOptimalCap = [int]$case.compactStepOptimalCap
		compactStepOptimalDelta = if ([int]$case.compactStepOptimalCap -ge 0) { [int]$case.compactStepOptimalCap - $current.compactStepOptimalGroups } else { $null }
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
$rows |
	Where-Object {
		($_.searchedDelta -ne $null -and $_.searchedDelta -gt 0) -or
		($_.outcomesDelta -ne $null -and $_.outcomesDelta -gt 0) -or
		($_.duplicateDelta -ne $null -and $_.duplicateDelta -gt 0) -or
		($_.compactStatesDelta -ne $null -and $_.compactStatesDelta -gt 0) -or
		($_.compactGroupsDelta -ne $null -and $_.compactGroupsDelta -gt 0) -or
		($_.compactStepOptimalDelta -ne $null -and $_.compactStepOptimalDelta -gt 0)
	} |
	Sort-Object n, m, k |
	Format-Table n, m, k, searchedDelta, outcomesDelta, duplicateDelta, compactStatesDelta, compactGroupsDelta, compactStepOptimalDelta -AutoSize
