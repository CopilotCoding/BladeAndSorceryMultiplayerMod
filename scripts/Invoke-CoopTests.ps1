param([switch]$SkipLaunch,[switch]$KeepRunning)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$results=[System.Collections.Generic.List[object]]::new()
$states=@{};$protection=@{};$passed=$false;$build=$null
$engagements=@{}
function Send([string]$Name,[hashtable]$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 12 -Compress)}
function State([string]$Name){Send $Name @{op='status'}}
function Pass([string]$Test){$results.Add(@{test=$Test;passed=$true});Write-Output "PASS: $Test"}
function Wait-For([scriptblock]$Condition,[string]$Reason,[int]$Seconds=25){
    $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
    do{if(&$Condition){Pass $Reason;return};Start-Sleep -Milliseconds 150}while([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Reason"
}
function Npcs([string]$Name){@((State $Name).entities|Where-Object kind -eq 'Creature')}
try{
    $previous=Join-Path $workspace 'artifacts\coop-report.json'
    if(Test-Path -LiteralPath $previous){
        $history=Join-Path $workspace ('artifacts\regression-history\coop-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff'))
        New-Item -ItemType Directory -Path $history -Force|Out-Null
        foreach($file in @('coop-report.json','coop-states.json')){
            $path=Join-Path $workspace "artifacts\$file"
            if(Test-Path -LiteralPath $path){Copy-Item -LiteralPath $path -Destination $history}
        }
    }
    if(!$SkipLaunch){
        & (Join-Path $PSScriptRoot 'Start-TestPair.ps1')
        & (Join-Path $PSScriptRoot 'Invoke-ArenaTests.ps1') -SkipLaunch -Latency 60 -Loss 5
    }
    $s=State 'host';$build=@{game=$s.game;mod=$s.modBuild;core=$s.coreBuild;hostProcess=$s.processId;clientProcess=(State 'client').processId}
    foreach($name in @('host','client')){
        $protection[$name]=(State $name).manualInvincibility
        Send $name @{op='invincibility';enabled=$true}|Out-Null
        foreach($side in @('left','right')){Send $name @{op='release';side=$side}|Out-Null}
    }
    foreach($npc in (Npcs 'host')){Send 'host' @{op='npc-despawn';entity=$npc.id}|Out-Null}
    Wait-For {(Npcs 'host').Count -eq 0 -and (Npcs 'client').Count -eq 0} 'Native NPC fixtures start empty on both peers'
    foreach($name in @('host','client')){Send $name @{op='clear-loose-items'}|Out-Null}
    Send 'host' @{op='teleport';position=@(-5,.07,0);yaw=0}|Out-Null
    Send 'client' @{op='teleport';position=@(5,.07,0);yaw=180}|Out-Null
    foreach($name in @('host','client')){
        $s=State $name;$x=$s.root[0];$y=$s.root[1];$z=$s.root[2]
        $facing=if($name -eq 'host'){0}else{180};$lateral=if($name -eq 'host'){1}else{-1}
        # Do not inherit a previous sword test's raised punching/blocking hands.
        Send $name @{op='pose';head=@($x,($y+1.75),$z);headRotation=@(0,$facing,0);left=@(($x-.4*$lateral),($y+.9),$z);right=@(($x+.4*$lateral),($y+.9),$z);leftRotation=@(0,$facing,0);rightRotation=@(0,$facing,0)}|Out-Null
    }
    Send 'host' @{op='mode';mode='Coop'}|Out-Null
    Send 'host' @{op='wave';repeat=2}|Out-Null
    Wait-For {
        $h=State 'host';$c=State 'client'
        $a=@($h.entities|Where-Object kind -eq 'Creature');$b=@($c.entities|Where-Object kind -eq 'Creature')
        $h.match.wave -eq 1 -and $c.match.wave -eq 1 -and $h.match.pendingWaveSpawns -eq 0 -and $a.Count -eq 2 -and $b.Count -eq 2 -and (@($a.id|Sort-Object)-join ',') -eq (@($b.id|Sort-Object)-join ',') -and @($a|Where-Object {$_.owner -ne 1 -or !$_.ai.enabled -or !$_.ai.active -or $_.ai.faction -ne 3 -or !$_.rightHeld}).Count -eq 0 -and @($b|Where-Object {$_.ai.enabled -or $_.ai.active -or $_.ai.faction -ne 3 -or !$_.rightHeld}).Count -eq 0
    } 'One native two-enemy wave replicates with correct factions, equipment and a single AI owner'
    $h=State 'host'
    $players=@($h.entities|Where-Object kind -eq 'Player')
    Wait-For {
        $enemies=Npcs 'host'
        foreach($player in $players){if(@($enemies|Where-Object {$_.ai.target -eq $player.id}).Count -eq 0){return $false}}
        $true
    } 'Native AI independently acquires both the host and remote player'
    $assigned=@{}
    foreach($player in $players){$assigned[[int]$player.owner]=@(Npcs 'host'|Where-Object {$_.ai.target -eq $player.id})[0].id}
    foreach($name in @('host','client')){
        $observer=if($name -eq 'host'){'client'}else{'host'}
        # Isolate one native encounter after testing simultaneous acquisition.
        # Otherwise ordinary target switching can starve the second damage case.
        foreach($npc in (Npcs 'host')){Send 'host' @{op='npc-ai';entity=$npc.id;enabled=$false}|Out-Null}
        Send $observer @{op='teleport';position=@(-7,.07,-6);yaw=0;navmesh=$true}|Out-Null
        Send $name @{op='teleport';position=@(0,.07,1);yaw=180;navmesh=$true}|Out-Null
        $before=State $name
        $victim=@($before.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $before.peer})[0]
        $enemyId=$assigned[[int]$before.peer]
        $enemy=@(Npcs 'host'|Where-Object id -eq $enemyId)[0]
        $distance=if($enemy.ai.meleeRange.max -gt $enemy.ai.meleeRange.min){($enemy.ai.meleeRange.min+$enemy.ai.meleeRange.max)/2}else{2.1}
        Send 'host' @{op='npc-teleport';entity=$enemyId;position=@($victim.position[0],.07,($victim.position[2]-$distance));yaw=0}|Out-Null
        Send 'host' @{op='npc-ai';entity=$enemyId;enabled=$true;holdPosition=$true}|Out-Null
        Send $name @{op='engage-npc';entity=$enemyId;distance=$distance}|Out-Null
        Wait-For {
            $enemy=@(Npcs 'host'|Where-Object id -eq $enemyId)[0]
            if(!$enemy){return $false}
            if($enemy.ai.meleeRange.max -gt $enemy.ai.meleeRange.min){
                $desired=($enemy.ai.meleeRange.min+$enemy.ai.meleeRange.max)/2
                if([Math]::Abs($desired-$distance) -gt .005){$script:distance=$desired;Send $name @{op='engage-npc';entity=$enemyId;distance=$desired}|Out-Null}
            }
            if($enemy.ai.target -eq $victim.id -and $enemy.ai.attacks -gt 0){return $true}
            $false
        } "Native AI initiates an attack against $name" 30
        Send $name @{op='invincibility';enabled=$false}|Out-Null
        $damageAt=(State $name).time
        try{
            Wait-For {
                $now=State $name;$body=@($now.entities|Where-Object id -eq $victim.id)[0]
                if($body -and $body.health -lt $victim.health -and @($now.damageTrace|Where-Object {$_.time -ge $damageAt -and $_.victim -eq $victim.id -and $_.allowed -and $_.damager -like 'Sword*'}).Count -gt 0){return $true}
                $enemy=@(Npcs 'host'|Where-Object id -eq $enemyId)[0]
                if($enemy -and $enemy.ai.meleeRange.max -gt $enemy.ai.meleeRange.min){
                    $desired=($enemy.ai.meleeRange.min+$enemy.ai.meleeRange.max)/2
                    if([Math]::Abs($desired-$distance) -gt .005){$script:distance=$desired;Send $name @{op='engage-npc';entity=$enemyId;distance=$desired}|Out-Null}
                }
                $false
            } "Native enemy weapon physically damages $name" 40
        }finally{Send $name @{op='invincibility';enabled=$true}|Out-Null;$engagements[$name]=Send $name @{op='engage-npc';enabled=$false}}
        Wait-For {
            $a=@((State $name).entities|Where-Object id -eq $victim.id)[0]
            $b=@((State $observer).entities|Where-Object id -eq $victim.id)[0]
            $a -and $b -and [Math]::Abs($a.health-$b.health) -lt .01 -and $a.dead -eq $b.dead
        } "$name native enemy damage converges on both peers"
    }
    Send 'client' @{op='mode';mode='FreeForAll'}|Out-Null
    Send 'client' @{op='wave'}|Out-Null
    Send 'host' @{op='wave';repeat=2}|Out-Null
    $h=State 'host'
    if($h.match.mode -ne 'Coop' -or $h.match.wave -ne 1 -or (Npcs 'host').Count -ne 2){throw 'A client or repeated request changed an active wave.'}
    Pass 'Clients cannot change the mode or start waves; repeated host requests do not duplicate an active wave'
    foreach($npc in (Npcs 'host')){Send 'host' @{op='npc-ai';entity=$npc.id;enabled=$false}|Out-Null}
    $npc=@(Npcs 'host'|Where-Object {!$_.dead})[0]
    & (Join-Path $PSScriptRoot 'Invoke-NpcHitTest.ps1') -Attacker client -Victim $npc.id
    Pass 'Client physical sword combat reaches the host-owned native NPC'
    foreach($npc in (Npcs 'host')){if(!$npc.dead){Send 'host' @{op='npc-kill';entity=$npc.id}|Out-Null}}
    Wait-For {@(Npcs 'client'|Where-Object {!$_.dead}).Count -eq 0} 'Native NPC deaths replicate to the client'
    Send 'host' @{op='wave';repeat=2}|Out-Null
    Wait-For {
        $h=State 'host';$c=State 'client'
        $h.match.wave -eq 2 -and $c.match.wave -eq 2 -and $h.match.pendingWaveSpawns -eq 0 -and @($h.entities|Where-Object {$_.kind -eq 'Creature' -and !$_.dead}).Count -eq 3 -and @($c.entities|Where-Object {$_.kind -eq 'Creature' -and !$_.dead}).Count -eq 3
    } 'Clearing the first wave allows exactly one three-enemy second wave'
    foreach($npc in (Npcs 'host')){Send 'host' @{op='npc-despawn';entity=$npc.id}|Out-Null}
    Wait-For {(Npcs 'host').Count -eq 0 -and (Npcs 'client').Count -eq 0} 'Native NPC despawn removes every remote enemy replica'
    foreach($name in @('host','client')){
        Send $name @{op='invincibility';enabled=$false}|Out-Null
        Wait-For {(State $name).creature} "$name native fighter remains ready for teammate-contact checks"
    }
    foreach($name in @('host','client')){
        & (Join-Path $PSScriptRoot 'Invoke-PhysicalHitTest.ps1') -Attacker $name -ExpectBlocked
        Pass "$name physical teammate sword contacts preserve synchronized health"
    }
    Send 'host' @{op='mode';mode='FreeForAll'}|Out-Null
    Wait-For {(State 'client').match.mode -eq 'FreeForAll'} 'Returning to PvP replicates the mode change'
    $passed=$true
}catch{$results.Add(@{test='Run completion';passed=$false;error=$_.ToString()});throw}
finally{
    foreach($name in @('host','client')){
        try{Send $name @{op='engage-npc';enabled=$false}|Out-Null;if($protection.ContainsKey($name)){Send $name @{op='invincibility';enabled=$protection[$name]}|Out-Null};$states[$name]=State $name}catch{}
    }
    @{time=[DateTime]::UtcNow.ToString('o');passed=$passed;build=$build;tests=$results;scriptedEngagements=$engagements}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\coop-report.json')
    $states|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\coop-states.json')
    if(!$KeepRunning){& (Join-Path $PSScriptRoot 'Stop-TestInstances.ps1') -Names host,client}
}
