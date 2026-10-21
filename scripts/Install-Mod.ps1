param([string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\Blade & Sorcery')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$gameRoot = [IO.Path]::GetFullPath($GamePath)
$gameExe = Join-Path $gameRoot 'BladeAndSorcery.exe'
$gameAssembly = Join-Path $gameRoot 'BladeAndSorcery_Data\Managed\ThunderRoad.dll'
if (!(Test-Path -LiteralPath $gameExe) -or !(Test-Path -LiteralPath $gameAssembly)) { throw 'Expected a PCVR Blade & Sorcery installation.' }
$source = Join-Path $workspace 'artifacts\mod\BladeAndSorceryMultiplayer'
foreach ($required in @('manifest.json','COMPATIBILITY.md','BladeAndSorcery.Multiplayer.dll','BladeAndSorcery.Multiplayer.Core.dll','LiteNetLib.dll','0Harmony.dll','licenses\Harmony.txt','licenses\LiteNetLib.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $source $required))) { throw "Build artifact is incomplete: $required" }
}
if ((Get-FileHash -LiteralPath $gameAssembly).Hash -ne (Get-FileHash -LiteralPath (Join-Path $workspace 'Managed\ThunderRoad.dll')).Hash) { throw 'Installed ThunderRoad.dll differs from the build references. Update the references and rebuild for this game version first.' }
if (@(Get-Process -Name BladeAndSorcery -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $gameExe }).Count -gt 0) { throw 'Exit the installed game before updating its mod files.' }
$modsRoot = [IO.Path]::GetFullPath((Join-Path $gameRoot 'BladeAndSorcery_Data\StreamingAssets\Mods'))
$target = [IO.Path]::GetFullPath((Join-Path $modsRoot 'BladeAndSorceryMultiplayer'))
if (![IO.Path]::GetDirectoryName($target).Equals($modsRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected mod destination.' }
New-Item -ItemType Directory -Path $target -Force | Out-Null
Get-ChildItem -LiteralPath $source | Copy-Item -Destination $target -Recurse -Force
Write-Output "Installed native PCVR mod: $target"
Write-Output 'Load Home or Arena. Open Mods -> Blade & Sorcery Multiplayer; use the Multiplayer tab arrows for Steam or Direct IP. Normal maps and wave books work while connected.'
