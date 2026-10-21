#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$GamePath,
    [ValidateSet('Release','Debug')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

function Test-GameInstallation([string]$Path) {
    (Test-Path -LiteralPath (Join-Path $Path 'BladeAndSorcery.exe') -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $Path 'BladeAndSorcery_Data/Managed/ThunderRoad.dll') -PathType Leaf)
}

if ([string]::IsNullOrWhiteSpace($GamePath)) {
    $steamRoots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($setting in @(
        @{ Key='HKCU:\Software\Valve\Steam'; Property='SteamPath' },
        @{ Key='HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'; Property='InstallPath' }
    )) {
        $value = Get-ItemPropertyValue -LiteralPath $setting.Key -Name $setting.Property -ErrorAction SilentlyContinue
        if ($value) { [void]$steamRoots.Add($value) }
    }
    if (${env:ProgramFiles(x86)}) { [void]$steamRoots.Add((Join-Path ${env:ProgramFiles(x86)} 'Steam')) }
    $libraries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($steamRoot in $steamRoots) {
        [void]$libraries.Add($steamRoot)
        $libraryFile = Join-Path $steamRoot 'steamapps/libraryfolders.vdf'
        if (Test-Path -LiteralPath $libraryFile -PathType Leaf) {
            $vdf = Get-Content -LiteralPath $libraryFile -Raw
            foreach ($match in [regex]::Matches($vdf, '"path"\s+"([^\"]+)"')) {
                [void]$libraries.Add($match.Groups[1].Value.Replace('\\','\'))
            }
        }
    }
    foreach ($library in $libraries) {
        $installDirectory = 'Blade & Sorcery'
        $appManifest = Join-Path $library 'steamapps/appmanifest_629730.acf'
        if (Test-Path -LiteralPath $appManifest -PathType Leaf) {
            $match = [regex]::Match((Get-Content -LiteralPath $appManifest -Raw), '"installdir"\s+"([^\"]+)"')
            if ($match.Success) { $installDirectory = $match.Groups[1].Value }
        }
        $candidate = Join-Path $library "steamapps/common/$installDirectory"
        if (Test-GameInstallation $candidate) { $GamePath = $candidate; break }
    }
    if (!$GamePath) { throw 'Blade & Sorcery PCVR was not found in Steam libraries. Run again with -GamePath "D:\SteamLibrary\steamapps\common\Blade & Sorcery".' }
}
$gameRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $GamePath).ProviderPath)
if (!(Test-GameInstallation $gameRoot)) { throw "Expected a PCVR Blade & Sorcery installation at: $gameRoot" }
$gameExe = Join-Path $gameRoot 'BladeAndSorcery.exe'
if (@(Get-Process -Name BladeAndSorcery -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $gameExe }).Count) {
    throw 'Close Blade & Sorcery before building and installing. The script will not stop your game.'
}
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install a .NET SDK supporting net8.0, then open a new PowerShell 7 window.' }
$sdks = @(& dotnet --list-sdks)
if ($LASTEXITCODE -ne 0 -or !@($sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 8 }).Count) {
    throw 'A .NET SDK supporting net8.0 is required (development used SDK 9.0.305).'
}
$sourceManaged = Join-Path $gameRoot 'BladeAndSorcery_Data/Managed'
$localManaged = Join-Path $workspace 'Managed'
if ($sourceManaged.Equals($localManaged,[StringComparison]::OrdinalIgnoreCase)) { throw 'The source workspace must be separate from the installed game Managed folder.' }
if ((Test-Path -LiteralPath $localManaged) -and ((Get-Item -LiteralPath $localManaged).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The workspace Managed folder must be a regular directory, not a link to another location.'
}
Write-Output "Game: $gameRoot"
Write-Output '1/3 Copying installed game references into local Managed...'
New-Item -ItemType Directory -Path $localManaged -Force | Out-Null
Get-ChildItem -LiteralPath $sourceManaged | Copy-Item -Destination $localManaged -Recurse -Force
# Wildcard assembly references must not retain DLLs from an older game version.
foreach ($file in Get-ChildItem -LiteralPath $localManaged -File -Filter '*.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $sourceManaged $file.Name) -PathType Leaf)) {
        Remove-Item -LiteralPath $file.FullName -Force
    }
}
if ((Get-FileHash -LiteralPath (Join-Path $sourceManaged 'ThunderRoad.dll')).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path $localManaged 'ThunderRoad.dll')).Hash) { throw 'Game reference copy verification failed.' }

Write-Output '2/3 Restoring dependencies and building the mod...'
& (Join-Path $PSScriptRoot 'Build.ps1') -Configuration $Configuration -Restore
Write-Output '3/3 Installing the built mod...'
& (Join-Path $PSScriptRoot 'Install-Mod.ps1') -GamePath $gameRoot
Write-Output 'Done. Start Blade & Sorcery through Steam as usual.'
