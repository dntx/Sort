[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$registryPath = Join-Path $repoRoot 'tests/test-gates.json'
$testsRoot = Join-Path $repoRoot 'tests'

$registry = Get-Content $registryPath -Raw | ConvertFrom-Json
$registered = @{}
foreach ($gate in $registry.gates) {
    if ($registered.ContainsKey($gate.name)) {
        throw "Duplicate test gate '$($gate.name)' in tests/test-gates.json."
    }

    if ($gate.category -notin @('nightly', 'manual-diagnostic')) {
        throw "Test gate '$($gate.name)' has unsupported category '$($gate.category)'."
    }

    $entrypoint = Join-Path $repoRoot $gate.entrypoint
    if (-not (Test-Path $entrypoint -PathType Leaf)) {
        throw "Test gate '$($gate.name)' references missing entrypoint '$($gate.entrypoint)'."
    }

    if ($gate.category -eq 'nightly') {
        if (-not $gate.workflows -or $gate.workflows.Count -eq 0) {
            throw "Nightly test gate '$($gate.name)' must declare at least one workflow."
        }

        foreach ($workflowPath in $gate.workflows) {
            $workflow = Join-Path $repoRoot $workflowPath
            if (-not (Test-Path $workflow -PathType Leaf)) {
                throw "Test gate '$($gate.name)' references missing workflow '$workflowPath'."
            }
            if ((Get-Content $workflow -Raw) -notmatch [regex]::Escape($gate.name)) {
                throw "Workflow '$workflowPath' does not activate test gate '$($gate.name)'."
            }
        }
    }
    elseif ([string]::IsNullOrWhiteSpace($gate.command)) {
        throw "Manual diagnostic gate '$($gate.name)' must declare an invocation command."
    }

    $registered[$gate.name] = $gate
}

$used = @{}
$pattern = 'GetEnvironmentVariable\(\s*"(?<name>RUN_[A-Z0-9_]+)"\s*\)'
foreach ($file in Get-ChildItem $testsRoot -Filter '*.cs' -Recurse) {
    $content = Get-Content $file.FullName -Raw
    foreach ($match in [regex]::Matches($content, $pattern)) {
        $name = $match.Groups['name'].Value
        if (-not $used.ContainsKey($name)) {
            $used[$name] = @()
        }
        $used[$name] += $file.FullName.Substring($repoRoot.Length + 1).Replace('\', '/')
    }
}

$unregistered = @($used.Keys | Where-Object { -not $registered.ContainsKey($_) } | Sort-Object)
$stale = @($registered.Keys | Where-Object { -not $used.ContainsKey($_) } | Sort-Object)
if ($unregistered.Count -gt 0) {
    throw "Unregistered RUN_* test gates: $($unregistered -join ', '). Add them to tests/test-gates.json."
}
if ($stale.Count -gt 0) {
    throw "Stale test-gate registry entries: $($stale -join ', '). Remove or restore their test usage."
}

Write-Host "Validated $($registered.Count) registered test gates."
