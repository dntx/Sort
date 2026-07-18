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
	@{ n = 9;  m = 3; k = 3; searchedCap = 95;   outcomesCap = 991;    candidateCap = 1286;   duplicateCap = 104 },
	@{ n = 11; m = 3; k = 3; searchedCap = 267;  outcomesCap = 3532;   candidateCap = 5114;   duplicateCap = 276 },
	@{ n = 12; m = 3; k = 3; searchedCap = 486;  outcomesCap = 7303;   candidateCap = 10909;  duplicateCap = 550 },
	@{ n = 12; m = 4; k = 4; searchedCap = 242;  outcomesCap = 9809;   candidateCap = 9776;   duplicateCap = 2232 },
	@{ n = 12; m = 4; k = 3; searchedCap = 63;   outcomesCap = 492;    candidateCap = 544;    duplicateCap = 111 },
	@{ n = 10; m = 3; k = 4; searchedCap = 409;  outcomesCap = 6360;   candidateCap = 7882;   duplicateCap = 495 },
	@{ n = 10; m = 3; k = 5; searchedCap = 323;  outcomesCap = 5521;   candidateCap = 5634;   duplicateCap = 360 },
	@{ n = 12; m = 4; k = 5; searchedCap = 710;  outcomesCap = 32512;  candidateCap = 33855;  duplicateCap = -1 },
	@{ n = 16; m = 4; k = 4; searchedCap = 5650; outcomesCap = 328532; candidateCap = 464319; duplicateCap = -1 },
	@{ n = 20; m = 5; k = 4; searchedCap = 3587; outcomesCap = 304457; candidateCap = 379108; duplicateCap = -1 },
	@{ n = 13; m = 4; k = 3; searchedCap = 97;   outcomesCap = 1346;   candidateCap = 1542;   duplicateCap = 329 },
	@{ n = 8;  m = 4; k = 2; searchedCap = 3;    outcomesCap = 4;      candidateCap = 5;      duplicateCap = 0 },
	@{ n = 9;  m = 4; k = 3; searchedCap = 16;   outcomesCap = 93;     candidateCap = 68;     duplicateCap = 29 },
	@{ n = 8;  m = 3; k = 4; searchedCap = 53;   outcomesCap = 591;    candidateCap = 546;    duplicateCap = 73 },
	@{ n = 8;  m = 2; k = 3; searchedCap = 317;  outcomesCap = -1;     candidateCap = 4232;   duplicateCap = -1 },
	@{ n = 9;  m = 3; k = 4; searchedCap = 173;  outcomesCap = 2759;   candidateCap = 3008;   duplicateCap = 254 },
	@{ n = 10; m = 3; k = 6; searchedCap = 409;  outcomesCap = 6360;   candidateCap = 7882;   duplicateCap = 495 },
	@{ n = 5;  m = 3; k = 2; searchedCap = 4;    outcomesCap = 12;     candidateCap = 4;      duplicateCap = 3 },
	@{ n = 6;  m = 2; k = 2; searchedCap = 21;   outcomesCap = 72;     candidateCap = 85;     duplicateCap = -1 },
	@{ n = 10; m = 2; k = 2; searchedCap = 106;  outcomesCap = 740;    candidateCap = 1115;   duplicateCap = 2 },
	@{ n = 25; m = 5; k = 3; searchedCap = 247;  outcomesCap = 759;    candidateCap = 7261;   duplicateCap = -1 }
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
