param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$ArtifactsDir = ".\\artifacts"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$snapshotsDir = Join-Path $ArtifactsDir "snapshots"
New-Item -ItemType Directory -Force -Path $snapshotsDir | Out-Null

$defaultJson = Join-Path $snapshotsDir "default-counter-snapshot.json"
$defaultCsv = Join-Path $snapshotsDir "default-counter-snapshot.csv"
$compactJson = Join-Path $snapshotsDir "compact-counter-snapshot.json"
$compactCsv = Join-Path $snapshotsDir "compact-counter-snapshot.csv"
$iterativeJson = Join-Path $snapshotsDir "iterative-counter-snapshot.json"
$iterativeCsv = Join-Path $snapshotsDir "iterative-counter-snapshot.csv"

Write-Host "Collecting default snapshot..." -ForegroundColor Cyan
pwsh .\scripts\collect-default-counter-snapshot.ps1 -Configuration $Configuration -OutputJsonPath $defaultJson -OutputCsvPath $defaultCsv
if ($LASTEXITCODE -ne 0) { throw "Default snapshot collection failed." }

Write-Host "Collecting compact snapshot..." -ForegroundColor Cyan
pwsh .\scripts\collect-compact-counter-snapshot.ps1 -Configuration $Configuration -OutputJsonPath $compactJson -OutputCsvPath $compactCsv
if ($LASTEXITCODE -ne 0) { throw "Compact snapshot collection failed." }

Write-Host "Collecting iterative snapshot..." -ForegroundColor Cyan
pwsh .\scripts\collect-iterative-counter-snapshot.ps1 -Configuration $Configuration -OutputJsonPath $iterativeJson -OutputCsvPath $iterativeCsv
if ($LASTEXITCODE -ne 0) { throw "Iterative snapshot collection failed." }

function Read-SnapshotRows {
    param([string]$Path)

    $rows = Get-Content -Path $Path -Raw | ConvertFrom-Json
    if ($rows -is [System.Array]) {
        return $rows
    }

    return @($rows)
}

function Get-NumericDeltaFields {
    param([object]$Row)

    $fields = @()
    foreach ($prop in $Row.PSObject.Properties.Name) {
        if ($prop -like "*Delta") {
            $fields += $prop
        }
    }

    return $fields
}

function Summarize-Snapshot {
    param(
        [string]$Name,
        [object[]]$Rows
    )

    $deltaFields = @()
    if ($Rows.Count -gt 0) {
        $deltaFields = Get-NumericDeltaFields -Row $Rows[0]
    }

    $positiveCount = 0
    $maxPositive = 0
    foreach ($row in $Rows) {
        foreach ($field in $deltaFields) {
            $value = $row.$field
            if ($null -ne $value) {
                $intValue = [int]$value
                if ($intValue -gt 0) {
                    $positiveCount++
                    if ($intValue -gt $maxPositive) {
                        $maxPositive = $intValue
                    }
                }
            }
        }
    }

    return [pscustomobject]@{
        name = $Name
        rows = $Rows.Count
        positiveDeltaCells = $positiveCount
        maxPositiveDelta = $maxPositive
    }
}

$defaultRows = Read-SnapshotRows -Path $defaultJson
$compactRows = Read-SnapshotRows -Path $compactJson
$iterativeRows = Read-SnapshotRows -Path $iterativeJson

$summaryRows = @(
    (Summarize-Snapshot -Name "default" -Rows $defaultRows),
    (Summarize-Snapshot -Name "compact" -Rows $compactRows),
    (Summarize-Snapshot -Name "iterative" -Rows $iterativeRows)
)

$combined = [ordered]@{
    generatedAtUtc = [DateTime]::UtcNow.ToString("o")
    configuration = $Configuration
    artifactsDir = $ArtifactsDir
    snapshots = [ordered]@{
        default = [ordered]@{ json = $defaultJson; csv = $defaultCsv }
        compact = [ordered]@{ json = $compactJson; csv = $compactCsv }
        iterative = [ordered]@{ json = $iterativeJson; csv = $iterativeCsv }
    }
    summary = $summaryRows
}

$combinedJsonPath = Join-Path $ArtifactsDir "counter-snapshot-summary.json"
$combinedMdPath = Join-Path $ArtifactsDir "counter-snapshot-summary.md"

$combined | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8NoBOM -Path $combinedJsonPath

$mdLines = @(
    "# Counter Snapshot Summary",
    "",
    "- generatedAtUtc: $($combined.generatedAtUtc)",
    "- configuration: $Configuration",
    "",
    "| snapshot | rows | positive-delta cells | max positive delta |",
    "| --- | ---: | ---: | ---: |"
)

foreach ($row in $summaryRows) {
    $mdLines += "| $($row.name) | $($row.rows) | $($row.positiveDeltaCells) | $($row.maxPositiveDelta) |"
}

$mdLines += ""
$mdLines += "## Paths"
$mdLines += "- default json: $defaultJson"
$mdLines += "- default csv: $defaultCsv"
$mdLines += "- compact json: $compactJson"
$mdLines += "- compact csv: $compactCsv"
$mdLines += "- iterative json: $iterativeJson"
$mdLines += "- iterative csv: $iterativeCsv"
$mdLines += "- combined json: $combinedJsonPath"
$mdLines += "- combined md: $combinedMdPath"

$mdLines -join "`r`n" | Set-Content -Encoding utf8NoBOM -Path $combinedMdPath

Write-Host "Wrote combined summary JSON: $combinedJsonPath" -ForegroundColor Green
Write-Host "Wrote combined summary MD:   $combinedMdPath" -ForegroundColor Green
$summaryRows | Format-Table name, rows, positiveDeltaCells, maxPositiveDelta -AutoSize
