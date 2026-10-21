param([switch]$Build,[ValidateRange(30,300)][int]$ReadyTimeoutSeconds=180)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Stop-TestInstances.ps1')
if ($Build) { & (Join-Path $PSScriptRoot 'Build.ps1') }
Copy-Item -LiteralPath (Join-Path $workspace 'artifacts\mod\BladeAndSorceryMultiplayer') -Destination (Join-Path $workspace '.runtime\game\BladeAndSorcery_Data\StreamingAssets\Mods') -Recurse -Force
foreach ($name in @('host','client')) { & (Join-Path $PSScriptRoot 'Start-TestInstance.ps1') -Name $name }
$deadline = [DateTime]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
$ready = @{}
while ([DateTime]::UtcNow -lt $deadline -and $ready.Count -lt 2) {
    foreach ($name in @('host','client')) {
        if ($ready.ContainsKey($name)) { continue }
        $root = Join-Path $workspace "artifacts\runs\$name"
        try {
            $record = Get-Content -LiteralPath (Join-Path $root 'process.json') -Raw | ConvertFrom-Json
            $status = Get-Content -LiteralPath (Join-Path $root 'status.json') -Raw | ConvertFrom-Json
            if ($status.processId -eq $record.pid -and $status.creature) { $ready[$name] = $true; Write-Output "$name physical player ready" }
        } catch { }
    }
    if ($ready.Count -lt 2) { Start-Sleep -Milliseconds 200 }
}
if ($ready.Count -ne 2) { throw 'One or both game instances failed to reach the physical-player ready state. Inspect artifacts/runs/*/unity.log.' }
