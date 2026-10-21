param([switch]$SkipLaunch,[switch]$KeepRunning)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$results=[System.Collections.Generic.List[object]]::new();$states=@{};$passed=$false;$build=$null
function Send([string]$Name,[hashtable]$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 12 -Compress)}
function State([string]$Name){Send $Name @{op='status'}}
function Wait-For([scriptblock]$Condition,[string]$Reason,[int]$Seconds=15){
    $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
    do{if(&$Condition){$results.Add(@{test=$Reason;passed=$true});Write-Output "PASS: $Reason";return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Reason"
}
try{
    if(!$SkipLaunch){
        & (Join-Path $PSScriptRoot 'Start-TestPair.ps1')
        & (Join-Path $PSScriptRoot 'Invoke-ArenaTests.ps1') -SkipLaunch -Latency 60 -Loss 5
    }
    $h=State 'host';$c=State 'client'
    $build=@{game=$h.game;mod=$h.modBuild;core=$h.coreBuild;hostProcess=$h.processId;clientProcess=$c.processId}
    $old=@($c.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $c.peer})[0].id
    Send 'client' @{op='respawn'}|Out-Null
    Wait-For {
        $s=State 'client';$body=@($s.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $s.peer})[0]
        $s.creature -and $body -and $body.id -ne $old -and !$body.dead -and $s.locomotionEnabled -and !$s.endGameMenu -and !$s.playerMenuBlocked -and [Math]::Abs($s.timeScale-1) -lt .001
    } 'Manual multiplayer respawn keeps native locomotion, menus and normal time usable'
    $beforeHost=State 'host'
    $oldHost=@($beforeHost.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq 1})[0].id
    $oldDeaths=@($beforeHost.match.scores|Where-Object peer -eq 1)[0].deaths
    Send 'host' @{op='kill'}|Out-Null
    Wait-For {
        $s=State 'host';$body=@($s.entities|Where-Object id -eq $oldHost)[0]
        !$s.creature -and $body.dead -and !$s.endGameMenu -and [Math]::Abs($s.timeScale-1) -lt .001
    } 'Host death stays in match simulation without the native defeat coroutine'
    Wait-For {
        $s=State 'host';$body=@($s.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq 1})[0]
        $score=@($s.match.scores|Where-Object peer -eq 1)[0]
        $remoteScore=@((State 'client').match.scores|Where-Object peer -eq 1)[0]
        $s.creature -and $body.id -ne $oldHost -and !$body.dead -and $s.nativeSpawnStarts -eq ($beforeHost.nativeSpawnStarts+1) -and $s.nativeSpawnEnds -eq ($beforeHost.nativeSpawnEnds+1) -and $score.deaths -eq ($oldDeaths+1) -and $remoteScore.deaths -eq $score.deaths -and !$s.endGameMenu -and !$s.playerMenuBlocked -and $s.locomotionEnabled -and [Math]::Abs($s.timeScale-1) -lt .001
    } 'Host automatic respawn runs once and its death is scored once on both peers'
    $c=State 'client';$victim=@($c.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $c.peer})[0].id
    Send 'client' @{op='kill'}|Out-Null
    Wait-For {
        $s=State 'client';$body=@($s.entities|Where-Object id -eq $victim)[0]
        !$s.creature -and $body.dead -and !$s.endGameMenu -and !$s.playerMenuBlocked -and !$s.locomotionEnabled -and [Math]::Abs($s.timeScale-1) -lt .001
    } 'An active-match death suppresses the native defeat menu and death slow motion'
    Wait-For {
        $s=State 'host';$body=@($s.entities|Where-Object id -eq $victim)[0]
        $body.dead -and [Math]::Abs($s.timeScale-1) -lt .001 -and !$s.endGameMenu
    } 'The other fighter receives the death and remains in normal arena simulation'
    Send 'client' @{op='disconnect'}|Out-Null
    Wait-For {
        $s=State 'client'
        !$s.connected -and !$s.gameplayPatches -and $s.networkControllers -eq 0 -and $s.endGameMenu -and $s.playerMenuBlocked -and $s.timeScale -lt .99
    } 'Disconnecting while dead restores the original native single-player defeat flow'
    Wait-For {
        $s=State 'host'
        $s.peers -eq 1 -and $s.networkControllers -eq 0 -and !$s.endGameMenu -and !$s.playerMenuBlocked -and [Math]::Abs($s.timeScale-1) -lt .001
    } 'The departed dead player leaves no controllers or defeat flow on the host'
    Send 'host' @{op='disconnect'}|Out-Null
    $before=State 'host'
    Send 'host' @{op='damage';amount=3}|Out-Null
    Send 'host' @{op='equip';item='SwordShortCommon'}|Out-Null
    Wait-For {
        $s=State 'host'
        !$s.gameplayPatches -and [Math]::Abs($s.localHealth-($before.localHealth-3)) -lt .01 -and $s.rightItem -eq 'SwordShortCommon'
    } 'Native single-player damage and gripping remain available after session cleanup'
    Send 'host' @{op='kill'}|Out-Null
    Wait-For {
        $s=State 'host'
        !$s.connected -and !$s.gameplayPatches -and $s.endGameMenu -and $s.playerMenuBlocked -and $s.timeScale -lt .99
    } 'A later native single-player death still opens its normal defeat menu and slow motion'
    $passed=$true
}catch{$results.Add(@{test='Run completion';passed=$false;error=$_.ToString()});throw}
finally{
    foreach($name in @('host','client')){try{$states[$name]=State $name}catch{}}
    @{time=[DateTime]::UtcNow.ToString('o');passed=$passed;build=$build;tests=$results}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\death-flow-report.json')
    $states|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\death-flow-states.json')
    if(!$KeepRunning){& (Join-Path $PSScriptRoot 'Stop-TestInstances.ps1') -Names host,client}
}
