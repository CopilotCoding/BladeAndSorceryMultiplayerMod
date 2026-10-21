param([ValidatePattern('^[a-zA-Z0-9_]+$')][string]$Name = 'host', [string[]]$ExtraArguments = @())
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$runtime = Join-Path $workspace '.runtime\game'
if (!(Test-Path -LiteralPath (Join-Path $runtime '.basmp-test-runtime'))) { throw 'Create the isolated test runtime first' }
$runRoot = Join-Path $workspace "artifacts\runs\$Name"
$saves = Join-Path $runRoot 'saves'
New-Item -ItemType Directory -Path $saves -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $saves 'logs') -Force | Out-Null
$info = [System.Diagnostics.ProcessStartInfo]::new()
$info.FileName = Join-Path $runtime 'BladeAndSorcery.exe'
$info.WorkingDirectory = $runtime
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
$info.Environment['BASMP_TEST_SAVE_ROOT'] = $saves
# Keep Steam entitlement checks, but identify this developer launch so Steam does not
# restart the installed executable and discard the isolated runtime directory.
$info.Environment['SteamAppId'] = '629730'
$info.Environment['SteamGameId'] = '629730'
Set-Content -LiteralPath (Join-Path $runtime 'steam_appid.txt') -Value '629730'
foreach ($argument in @('-vrmode','mockhmd','-skipintro','-skipmodswarning','-skipsandboxspoilwarning','-level','Arena','-gamemode','Sandbox','-playerheight','175','-mod','BladeAndSorceryMultiplayer','-screen-fullscreen','0','-screen-width','800','-screen-height','600','-logFile',(Join-Path $runRoot 'unity.log'),'-mp-test-dir',$runRoot) + $ExtraArguments) { $info.ArgumentList.Add($argument) }
$process = [System.Diagnostics.Process]::Start($info)
@{ pid = $process.Id; directory = $runRoot; started = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'process.json')
Write-Output "Started $Name process $($process.Id); results: $runRoot"
