param(
	[ValidateSet("Debug", "Release")]
	[string]$Configuration = "Release",
	[string]$OutputJsonPath = ".\\artifacts\\default-counter-snapshot.json",
	[string]$OutputCsvPath = ".\\artifacts\\default-counter-snapshot.csv"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Mirrors default-path counter-cap rows in StrategyRegressionTests.
$cases = @(
	@{ n = 9;  m = 3; k = 3; searchedCap = 95;   outcomesCap = 845;    candidateCap = 1236;   duplicateCap = 87 },
	@{ n = 11; m = 3; k = 3; searchedCap = 267;  outcomesCap = 3373;   candidateCap = 5053;   duplicateCap = 250 },
	@{ n = 12; m = 3; k = 3; searchedCap = 486;  outcomesCap = 7118;   candidateCap = 10862;  duplicateCap = 532 },
	@{ n = 12; m = 4; k = 4; searchedCap = 210;  outcomesCap = 3052;   candidateCap = 4636;   duplicateCap = 844 },
	@{ n = 12; m = 4; k = 3; searchedCap = 48;   outcomesCap = 207;    candidateCap = 463;    duplicateCap = 73 },
	@{ n = 10; m = 3; k = 4; searchedCap = 409;  outcomesCap = 6360;   candidateCap = 7882;   duplicateCap = 495 },
	@{ n = 10; m = 3; k = 5; searchedCap = 323;  outcomesCap = 5269;   candidateCap = 5593;   duplicateCap = 352 },
	@{ n = 12; m = 4; k = 5; searchedCap = 710;  outcomesCap = 22593;  candidateCap = 28811;  duplicateCap = -1 },
	@{ n = 16; m = 4; k = 4; searchedCap = 5650; outcomesCap = 328532; candidateCap = 464319; duplicateCap = -1 },
	@{ n = 20; m = 5; k = 4; searchedCap = 3272; outcomesCap = 149648; candidateCap = 266517; duplicateCap = -1 },
	@{ n = 13; m = 4; k = 3; searchedCap = 92;   outcomesCap = 506;    candidateCap = 871;    duplicateCap = 133 },
	@{ n = 8;  m = 4; k = 2; searchedCap = 3;    outcomesCap = 4;      candidateCap = 5;      duplicateCap = 0 },
	@{ n = 9;  m = 4; k = 3; searchedCap = 13;   outcomesCap = 36;     candidateCap = 44;     duplicateCap = 18 },
	@{ n = 8;  m = 3; k = 4; searchedCap = 53;   outcomesCap = 457;    candidateCap = 484;    duplicateCap = 58 },
	@{ n = 8;  m = 2; k = 3; searchedCap = 317;  outcomesCap = -1;     candidateCap = 4232;   duplicateCap = -1 },
	@{ n = 9;  m = 3; k = 4; searchedCap = 173;  outcomesCap = 2533;   candidateCap = 2952;   duplicateCap = 245 },
	@{ n = 10; m = 3; k = 6; searchedCap = 409;  outcomesCap = 6360;   candidateCap = 7882;   duplicateCap = 495 },
	@{ n = 5;  m = 3; k = 2; searchedCap = 4;    outcomesCap = 8;      candidateCap = 4;      duplicateCap = 2 },
	@{ n = 6;  m = 2; k = 2; searchedCap = 21;   outcomesCap = 72;     candidateCap = 85;     duplicateCap = -1 },
	@{ n = 10; m = 2; k = 2; searchedCap = 106;  outcomesCap = 740;    candidateCap = 1115;   duplicateCap = 2 },
	@{ n = 25; m = 5; k = 3; searchedCap = 173;  outcomesCap = 469;    candidateCap = 7254;   duplicateCap = -1 }
)

function Parse-IntMatch {
	param(
		[string]$Text,
		[string]$Pattern
	)

	$match = [regex]::Match($Text, $Pattern)
	if (-not $match.Success) {
		throw "Pattern not found: $Pattern"
	}

	return [int]$match.Groups[1].Value
}

function Parse-OutcomesAndDuplicate {
	param([string]$Text)

	$match = [regex]::Match($Text, 'outcomes constructed = (\d+) \(duplicate skips (\d+),')
	if (-not $match.Success) {
		throw "Could not parse outcomes/duplicate counters."
	}

	return @([int]$match.Groups[1].Value, [int]$match.Groups[2].Value)
}

$results = @()
foreach ($case in $cases) {
	$n = [int]$case.n
	$m = [int]$case.m
	$k = [int]$case.k

	Write-Host "Collecting counters for ($n,$m,$k)..." -ForegroundColor Cyan
	$output = dotnet run --project .\TopKFinder.csproj --configuration $Configuration -- $n $m $k --mode exact --stage 1
	if ($LASTEXITCODE -ne 0) {
		throw "dotnet run failed for ($n,$m,$k)."
	}

	$searched = Parse-IntMatch -Text $output -Pattern 'searched states = (\d+)'
	$candidate = Parse-IntMatch -Text $output -Pattern 'candidate groups enumerated = (\d+)'
	$pair = Parse-OutcomesAndDuplicate -Text $output
	$outcomes = $pair[0]
	$duplicate = $pair[1]

	$searchedCap = [int]$case.searchedCap
	$outcomesCap = [int]$case.outcomesCap
	$candidateCap = [int]$case.candidateCap
	$duplicateCap = [int]$case.duplicateCap

	$results += [pscustomobject]@{
		n = $n
		m = $m
		k = $k
		searched = $searched
		searchedCap = $searchedCap
		searchedDelta = $searchedCap - $searched
		outcomes = $outcomes
		outcomesCap = $outcomesCap
		outcomesDelta = if ($outcomesCap -ge 0) { $outcomesCap - $outcomes } else { $null }
		candidate = $candidate
		candidateCap = $candidateCap
		candidateDelta = $candidateCap - $candidate
		duplicate = $duplicate
		duplicateCap = $duplicateCap
		duplicateDelta = if ($duplicateCap -ge 0) { $duplicateCap - $duplicate } else { $null }
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

$results | Sort-Object n, m, k | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8NoBOM -Path $OutputJsonPath
$results | Sort-Object n, m, k | Export-Csv -NoTypeInformation -Encoding utf8NoBOM -Path $OutputCsvPath

Write-Host "Wrote snapshot JSON: $OutputJsonPath" -ForegroundColor Green
Write-Host "Wrote snapshot CSV:  $OutputCsvPath" -ForegroundColor Green

Write-Host ""
Write-Host "Ratchet opportunities (delta > 0):" -ForegroundColor Cyan
$results |
	Where-Object {
		$_.searchedDelta -gt 0 -or
		($_.outcomesDelta -ne $null -and $_.outcomesDelta -gt 0) -or
		$_.candidateDelta -gt 0 -or
		($_.duplicateDelta -ne $null -and $_.duplicateDelta -gt 0)
	} |
	Sort-Object n, m, k |
	Format-Table n, m, k, searchedDelta, outcomesDelta, candidateDelta, duplicateDelta -AutoSize
