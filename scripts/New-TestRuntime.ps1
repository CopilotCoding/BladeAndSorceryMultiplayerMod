param([string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Blade & Sorcery')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$runtime = Join-Path $workspace '.runtime\game'
if (!(Test-Path -LiteralPath (Join-Path $GamePath 'BladeAndSorcery.exe'))) { throw 'Game executable missing' }
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
Set-Content -LiteralPath (Join-Path $runtime '.basmp-test-runtime') -Value 'Local-only disposable test runtime. Never distribute.'
# Copy mutable startup/managed files; share large, read-only assets via directory junctions.
Get-ChildItem -LiteralPath $GamePath -File | Copy-Item -Destination $runtime -Force
$data = Join-Path $runtime 'BladeAndSorcery_Data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
$sourceData = Join-Path $GamePath 'BladeAndSorcery_Data'
Get-ChildItem -LiteralPath $sourceData -File | Copy-Item -Destination $data -Force
foreach ($directory in Get-ChildItem -LiteralPath $sourceData -Directory) {
    $target = Join-Path $data $directory.Name
    if ($directory.Name -eq 'Managed') {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Get-ChildItem -LiteralPath $directory.FullName -File | Copy-Item -Destination $target -Force
    } elseif ($directory.Name -ne 'StreamingAssets' -and !(Test-Path -LiteralPath $target)) {
        New-Item -ItemType Junction -Path $target -Target $directory.FullName | Out-Null
    }
}
$mono = Join-Path $runtime 'MonoBleedingEdge'
if (!(Test-Path -LiteralPath $mono)) { New-Item -ItemType Junction -Path $mono -Target (Join-Path $GamePath 'MonoBleedingEdge') | Out-Null }
$streaming = Join-Path $data 'StreamingAssets'
New-Item -ItemType Directory -Path $streaming -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $sourceData 'StreamingAssets') -File | Copy-Item -Destination $streaming -Force
foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $sourceData 'StreamingAssets') -Directory) {
    if ($directory.Name -in 'Mods','Logs') { continue }
    $target = Join-Path $streaming $directory.Name
    if (!(Test-Path -LiteralPath $target)) { New-Item -ItemType Junction -Path $target -Target $directory.FullName | Out-Null }
}
$mods = Join-Path $streaming 'Mods'
New-Item -ItemType Directory -Path $mods -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $workspace 'artifacts\mod\BladeAndSorceryMultiplayer') -Destination $mods -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $streaming 'Logs') -Force | Out-Null
& dotnet (Join-Path $workspace 'tools\RuntimePatcher\bin\Release\net8.0\RuntimePatcher.dll') $runtime
if ($LASTEXITCODE -ne 0) { throw 'Could not isolate runtime saves' }
Write-Output "Test runtime ready: $runtime"
