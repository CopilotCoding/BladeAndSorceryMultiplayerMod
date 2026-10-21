param()
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$runtime=Join-Path $workspace '.runtime\game'
$testExe=[IO.Path]::GetFullPath((Join-Path $runtime 'BladeAndSorcery.exe'))
if(!(Test-Path -LiteralPath (Join-Path $runtime '.basmp-test-runtime'))){throw 'The isolated runtime is missing.'}
if(@(Get-CimInstance Win32_Process -Filter "Name='BladeAndSorcery.exe'"|Where-Object ExecutablePath -eq $testExe).Count){throw 'An isolated game is already in use. Leave that session before checking Steam.'}
$instance='steam_capability_'+[DateTime]::UtcNow.ToString('yyyyMMddHHmmssffff')
$report=[ordered]@{time=[DateTime]::UtcNow.ToString('o');instance=$instance;singleAccount=$true;multiplayerVerified=$false;relayVerified=$false;combatTestsRun=$false;passed=$false;checks=@()}
$started=$false
function Send([hashtable]$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $instance -Json ($Command|ConvertTo-Json -Depth 6 -Compress)}
try{
    Copy-Item -LiteralPath (Join-Path $workspace 'artifacts\mod\BladeAndSorceryMultiplayer') -Destination (Join-Path $runtime 'BladeAndSorcery_Data\StreamingAssets\Mods') -Recurse -Force
    & (Join-Path $PSScriptRoot 'Start-TestInstance.ps1') -Name $instance
    $started=$true
    $statusPath=Join-Path $workspace "artifacts\runs\$instance\status.json"
    $deadline=[DateTime]::UtcNow.AddSeconds(90)
    do{
        try{$state=Get-Content -LiteralPath $statusPath -Raw|ConvertFrom-Json}catch{$state=$null}
        if($state.creature){break}
        if([DateTime]::UtcNow -gt $deadline){throw 'The isolated player did not become ready.'}
        Start-Sleep -Milliseconds 500
    }while($true)
    $report.mod=$state.modBuild;$report.core=$state.coreBuild
    $steam=Send @{op='steam-status'}
    if(!$steam.available){throw $steam.status}
    $report.checks+=@{name='game-owned Steam integration available';passed=$true}
    $options=@(Send @{op='mod-options'})
    $report.nativeMenuOptions=$options
    Send @{op='steam-host';visibility=2}|Out-Null
    $deadline=[DateTime]::UtcNow.AddSeconds(35)
    do{
        $steam=Send @{op='steam-status'}
        if($steam.connected -and $steam.hosting -and $steam.lobby -ne '0'){break}
        if(!$steam.busy -or [DateTime]::UtcNow -gt $deadline){throw ('Private Steam lobby/listener failed: '+$steam.status)}
        Start-Sleep -Milliseconds 500
    }while($true)
    $report.privateLobby=$steam.lobby;$report.checks+=@{name='private lobby creation and Steam P2P listener';passed=$true}
    Send @{op='steam-refresh'}|Out-Null
    Start-Sleep -Seconds 3
    $report.search=Send @{op='steam-status'}
    if($report.search.status -notmatch 'Steam lobbies found'){throw ('Steam search did not complete: '+$report.search.status)}
    $report.checks+=@{name='Steam mod lobby search request completes';passed=$true}
    $left=Send @{op='steam-leave'}
    if($left.lobby -ne '0' -or $left.connected){throw 'Steam lobby/session did not leave cleanly.'}
    $report.checks+=@{name='Steam lobby and network session leave';passed=$true}
    $log=Get-Content -LiteralPath (Join-Path $workspace "artifacts\runs\$instance\unity.log") -Raw
    if($log -match 'ERROR ThunderRoad.ModOption.Apply|HarmonyException|Session stopped after an integration error|Connection failed:'){throw 'Native initialization or session integration errors were logged; inspect the run log.'}
    $report.checks+=@{name='no native menu initialization or session integration errors before shutdown';passed=$true}
    $report.passed=$true
}catch{
    $report.error=$_.Exception.Message
    Write-Warning $report.error
}finally{
    if($started){
        try{Send @{op='steam-leave'}|Out-Null}catch{}
        try{Send @{op='quit'}|Out-Null}catch{}
        Start-Sleep -Milliseconds 500
        & (Join-Path $PSScriptRoot 'Stop-TestInstances.ps1') -Names $instance
    }
    $report|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\steam-capability-report.json')
}
[pscustomobject]$report|Select-Object passed,multiplayerVerified,relayVerified,error|ConvertTo-Json
