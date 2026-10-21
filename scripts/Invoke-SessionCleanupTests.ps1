param()
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$results=[System.Collections.Generic.List[object]]::new()
function Send([string]$Instance,[hashtable]$Command) { & (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Instance -Json ($Command|ConvertTo-Json -Compress -Depth 12) }
function State([string]$Instance) { Send $Instance @{op='status'} }
function Wait-For([scriptblock]$Condition,[string]$Test,[int]$Timeout=20) {
    $deadline=[DateTime]::UtcNow.AddSeconds($Timeout)
    while([DateTime]::UtcNow -lt $deadline) {
        if (& $Condition) { $results.Add(@{test=$Test;passed=$true}); Write-Output "PASS: $Test"; return }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out: $Test"
}
try {
    foreach($owner in @('host','client')) {
        $before=State $owner; $observer=if($owner -eq 'host'){'client'}else{'host'}
        $old=@($before.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $before.peer})[0].id
        Send $owner @{op='respawn'}|Out-Null
        Wait-For {
            $local=State $owner; $remote=@((State $observer).entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $before.peer})
            $own=@($local.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $before.peer})
            $own.Count -eq 1 -and $own[0].id -ne $old -and $local.localHealth -eq 100 -and $remote.Count -eq 1 -and $remote[0].id -eq $own[0].id -and !$remote[0].dead -and $local.nativeSpawnStarts -eq ($before.nativeSpawnStarts+1) -and $local.nativeSpawnEnds -eq ($before.nativeSpawnEnds+1) -and !$local.endGameMenu -and !$local.playerMenuBlocked -and $local.locomotionEnabled -and [Math]::Abs($local.timeScale-1) -lt .001
        } "$owner manual respawn replaces the living native body and synchronizes it"
    }
    $h=State 'host';$c=State 'client'
    $distance=[Math]::Sqrt([Math]::Pow($h.root[0]-$c.root[0],2)+[Math]::Pow($h.root[2]-$c.root[2],2))
    if($distance -le 1.4){throw 'Respawn placed the players inside each other.'}
    $results.Add(@{test='Arena respawns keep the players separated';passed=$true})
    Start-Sleep -Milliseconds 4500
    # Native locomotion can settle after spawning. Count actual spawner invocations
    # rather than treating any later body movement as a second respawn.
    foreach($name in @('host','client')) {
        $previous=if($name -eq 'host'){$h}else{$c}
        $now=State $name
        if($now.nativeSpawnStarts -ne $previous.nativeSpawnStarts -or $now.nativeSpawnEnds -ne $previous.nativeSpawnEnds) {throw "$name unexpectedly invoked the native spawner again after manual respawn."}
        $results.Add(@{test="$name manual respawn cancels the earlier automatic respawn timer";passed=$true})
        Write-Output "PASS: $name manual respawn cancels the earlier automatic respawn timer"
    }
    Send 'client' @{op='disconnect'}|Out-Null
    Wait-For {
        $local=State 'client';$hostState=State 'host'
        !$local.connected -and !$local.gameplayPatches -and $local.networkControllers -eq 0 -and $hostState.peers -eq 1 -and $hostState.networkControllers -eq 0
    } 'Client disconnect removes remote controllers, replicas, and session patches'
    Send 'host' @{op='disconnect'}|Out-Null
    foreach($name in @('host','client')) {
        Wait-For { $state=State $name; !$state.connected -and !$state.gameplayPatches -and $state.networkControllers -eq 0 } "$name has no multiplayer controllers or gameplay patches after disconnect"
        $before=State $name
        Send $name @{op='damage';amount=3}|Out-Null
        Send $name @{op='equip';item='SwordShortCommon'}|Out-Null
        Wait-For {
            $after=State $name
            [Math]::Abs($after.localHealth-($before.localHealth-3)) -lt .01 -and $after.rightItem -eq 'SwordShortCommon' -and !$after.gameplayPatches
        } "$name native damage and weapon gripping still work in single-player"
    }
} catch {$results.Add(@{test='Run completion';passed=$false;error=$_.ToString()});throw}
finally {@{time=[DateTime]::UtcNow.ToString('o');tests=$results}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\session-cleanup-report.json')}
