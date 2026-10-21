param([string]$Configuration = 'Release', [switch]$Restore)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $workspace '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $workspace '.tools\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:MSBuildEnableWorkloadResolver = 'false'
$restoreArguments = @('--no-restore')
if ($Restore) { $restoreArguments = @() }
& dotnet build (Join-Path $workspace 'src\BladeAndSorcery.Multiplayer\BladeAndSorcery.Multiplayer.csproj') -c $Configuration --nologo @restoreArguments
if ($LASTEXITCODE -ne 0) { throw 'Mod build failed' }
& dotnet build (Join-Path $workspace 'tools\RuntimePatcher\RuntimePatcher.csproj') -c $Configuration --nologo @restoreArguments
if ($LASTEXITCODE -ne 0) { throw 'Runtime patcher build failed' }
$destination = Join-Path $workspace 'artifacts\mod\BladeAndSorceryMultiplayer'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $workspace 'mod\manifest.json') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $workspace 'mod\README.md') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $workspace 'mod\COMPATIBILITY.md') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $workspace 'mod\THIRD_PARTY_NOTICES.md') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $workspace 'mod\licenses') -Destination $destination -Recurse -Force
$allowedAssemblies = @('BladeAndSorcery.Multiplayer.dll','BladeAndSorcery.Multiplayer.pdb','BladeAndSorcery.Multiplayer.Core.dll','BladeAndSorcery.Multiplayer.Core.pdb','LiteNetLib.dll','0Harmony.dll')
$output = Join-Path $workspace "src\BladeAndSorcery.Multiplayer\bin\$Configuration\netstandard2.1"
foreach ($assembly in $allowedAssemblies) {
    $source = Join-Path $output $assembly
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $destination -Force }
}
# The netstandard Harmony package is reference-only; Unity Mono needs its runtime build.
Copy-Item -LiteralPath (Join-Path $env:NUGET_PACKAGES 'lib.harmony\2.4.1\lib\net472\0Harmony.dll') -Destination $destination -Force
$allowedFiles = $allowedAssemblies + @('manifest.json','README.md','COMPATIBILITY.md','THIRD_PARTY_NOTICES.md','licenses\Harmony.txt','licenses\LiteNetLib.txt')
foreach ($file in Get-ChildItem -LiteralPath $destination -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($destination,$file.FullName)
    if ($relative -notin $allowedFiles) {
        throw "Unexpected packaged file: $relative. Remove it from the build artifact before distribution."
    }
}
Write-Output "Packaged mod: $destination"
