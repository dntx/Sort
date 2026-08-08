param(
	[ValidateSet("Debug", "Release")]
	[string]$Configuration = "Release",
	[string]$OutputJsonPath = ".\artifacts\compact-counter-snapshot.json",
	[string]$OutputCsvPath = ".\artifacts\compact-counter-snapshot.csv"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$env:COUNTER_SNAPSHOT_KIND = "compact"
$env:COUNTER_SNAPSHOT_JSON_PATH = $OutputJsonPath
$env:COUNTER_SNAPSHOT_CSV_PATH = $OutputCsvPath

Write-Host "Collecting compact counters..." -ForegroundColor Cyan
dotnet test .\tests\TopKFinder.Tests\TopKFinder.Tests.csproj -c $Configuration --filter "FullyQualifiedName~ExportCompactCounterSnapshot" --nologo
if ($LASTEXITCODE -ne 0) {
	exit $LASTEXITCODE
}

Write-Host "Wrote snapshot JSON: $OutputJsonPath" -ForegroundColor Green
Write-Host "Wrote snapshot CSV:  $OutputCsvPath" -ForegroundColor Green