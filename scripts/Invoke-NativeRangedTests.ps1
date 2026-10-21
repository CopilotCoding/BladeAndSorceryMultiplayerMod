param([switch]$ResumeArcher)
$ErrorActionPreference='Stop'
$rangedRoot=Split-Path -Parent $PSScriptRoot
$checks=[Collections.Generic.List[object]]::new();$states=@{};$rangedPassed=$false;$rangedBuild=$null
$resumedFrom=$null
function Send($n,$c){& "$PSScriptRoot/Send-TestCommand.ps1" -Name $n -Json ($c|ConvertTo-Json -Compress -Depth 10)}
function State($n){Send $n @{op='status'}}
function Pass($s){$checks.Add(@{test=$s;passed=$true});Write-Output "PASS: $s"}
function Wait-Ranged($condition,$reason,$seconds=65){$until=[DateTime]::UtcNow.AddSeconds($seconds);do{if(&$condition){Pass $reason;return};Start-Sleep -Milliseconds 200}while([DateTime]::UtcNow -lt $until);throw "Timed out: $reason"}
function Live(){@((State host).entities|Where-Object {$_.kind -eq 'Creature' -and !$_.dead -and $_.nativeWaveMember})}
function Begin($id){Send client @{op='native-wave-select';wave=$id}|Out-Null;Wait-Ranged {@((State host).nativeWaves)[0].selected -eq $id} "Normal wave book selects $id";Send client @{op='native-wave-start'}|Out-Null;Wait-Ranged {(Live).Count -gt 0 -and @((State client).nativeWaves)[0].running} "Native $id enemies join both worlds"}
function Clear-Fixture(){Send client @{op='native-wave-stop'}|Out-Null;Wait-Ranged {!@((State host).nativeWaves)[0].running} 'Native book stops the ranged wave';foreach($e in @((State host).entities|Where-Object kind -eq Creature)){Send host @{op='npc-despawn';entity=$e.id}|Out-Null}}
try{
 $s=State host;$rangedBuild=@{mod=$s.modBuild;core=$s.coreBuild}
 if($ResumeArcher){
  $previous=Get-Content "$rangedRoot/artifacts/native-ranged-report.json" -Raw|ConvertFrom-Json
  $previousStates=Get-Content "$rangedRoot/artifacts/native-ranged-states.json" -Raw|ConvertFrom-Json
  if($previous.build.mod -ne $s.modBuild -or $previous.build.core -ne $s.coreBuild -or $previousStates.magicHost.processId -ne $s.processId -or $previousStates.magicClient.processId -ne (State client).processId -or $s.level -ne 'Arena' -or @($s.nativeWaves)[0].wave -ne 'Training3'){throw 'Cannot continue ranged checks on different binaries/processes or wave'}
  $resumedFrom=$previous.time
  foreach($check in $previous.checks|Where-Object passed){$checks.Add($check)}
  $states.magicHost=$previousStates.magicHost;$states.magicClient=$previousStates.magicClient
  Clear-Fixture
  Begin Training3
 }
 if(!$ResumeArcher){
 foreach($n in @('host','client')){Send $n @{op='invincibility';enabled=$true}|Out-Null;Send $n @{op='engage-npc';enabled=$false}|Out-Null}
 Send client @{op='travel';level='Arena';mode='Sandbox'}|Out-Null
 Wait-Ranged {$h=State host;$c=State client;$h.level -eq 'Arena' -and $c.level -eq 'Arena' -and $h.scene.ready -and $c.scene.ready} 'Both players load native Arena for ranged checks' 150
 Send host @{op='teleport';position=@(-8,.07,10);yaw=180;navmesh=$true}|Out-Null
 Send client @{op='teleport';position=@(0,.07,2);yaw=180;navmesh=$true}|Out-Null
 $initial=State client;Begin CultistSorceryOnly
 Wait-Ranged {(State client).npcReplication.effectsReceived -gt $initial.npcReplication.effectsReceived} 'Native caster charge/bolt effects reach the client'
 Wait-Ranged {(State client).npcReplication.projectilesReceived -gt $initial.npcReplication.projectilesReceived} 'Native sorcery projectile replicas initialize from the owner launch metadata' 100
 $placed=@{};$start=State client
 Send client @{op='invincibility';enabled=$false}|Out-Null
 try{
  Wait-Ranged {
   foreach($e in (Live)){
    if($placed.ContainsKey($e.id) -or @($e.ai.spells|Where-Object spell).Count -eq 0){continue}
    Send host @{op='npc-teleport';entity=$e.id;position=@(($placed.Count*2-2),.07,-4);yaw=0}|Out-Null
    Send host @{op='npc-ai';entity=$e.id;enabled=$true;holdPosition=$true}|Out-Null;$placed[$e.id]=$true
   }
   $c=State client;$c.npcReplication.magicHits -gt $start.npcReplication.magicHits -and $c.localHealth -lt $start.localHealth
  } 'Native casters actually damage the client through a reliable caster outcome' 100
 }finally{Send client @{op='invincibility';enabled=$true}|Out-Null}
 $states.magicHost=State host;$states.magicClient=State client
 Clear-Fixture
 Begin Training3
 }
 # Progress native training groups using explicit native kill fixtures until
 # its actual ranged loadout appears; do not manufacture an archer or bow.
 $archer=$null
 for($attempt=0;$attempt -lt 8;$attempt++){
  Wait-Ranged {(Live).Count -gt 0} 'Native Archer Combat provides the next enemy'
  # Registration of the creature can precede registration of its native
  # equipped items. Observe one settled snapshot before classifying the loadout.
  Start-Sleep -Milliseconds 2500
  $h=State host;$enemies=@($h.entities|Where-Object {$_.kind -eq 'Creature' -and !$_.dead -and $_.nativeWaveMember})
  $archer=@($enemies|Where-Object {$e=$_;@($h.entities|Where-Object {$_.id -in @($e.leftHeld,$e.rightHeld) -and $_.catalog -like '*Bow*'}).Count -gt 0})[0]
  if($archer){break}
  foreach($e in $enemies){Send host @{op='npc-kill';entity=$e.id}|Out-Null}
 }
 if(!$archer){throw 'Native Archer Combat did not supply its bow loadout'}
 Pass 'Native Archer Combat retains an actual book-selected bow enemy'
 foreach($e in (Live|Where-Object id -ne $archer.id)){Send host @{op='npc-ai';entity=$e.id;enabled=$false}|Out-Null}
 $before=State client
 # Leave the active native ranged brain/coroutine untouched. Place only the
 # test player in front of the real archer, without restarting its AI.
 $dx=-$archer.position[0];$dz=-$archer.position[2];$length=[Math]::Sqrt($dx*$dx+$dz*$dz)
 if($length -lt .1){$dx=0;$dz=1;$length=1}
 $x=$archer.position[0]+$dx/$length*8;$z=$archer.position[2]+$dz/$length*8
 $yaw=[Math]::Atan2(-$dx,-$dz)*180/[Math]::PI
 Send host @{op='teleport';position=@(-8,.07,10);yaw=180;navmesh=$true}|Out-Null
 Send client @{op='teleport';position=@($x,.07,$z);yaw=$yaw;navmesh=$true}|Out-Null
 Send client @{op='invincibility';enabled=$false}|Out-Null
 try{
  Wait-Ranged {(State client).npcReplication.projectilesReceived -gt $before.npcReplication.projectilesReceived} 'Native AI fires an arrow and its owner/caster metadata reaches the client' 100
  Wait-Ranged {$c=State client;$c.npcReplication.magicHits -gt $before.npcReplication.magicHits -and $c.localHealth -lt $before.localHealth} 'Native archer arrow damages the client without relying on a surviving projectile entity' 100
 }finally{Send client @{op='invincibility';enabled=$true}|Out-Null}
 $states.archerHost=State host;$states.archerClient=State client
 $arrows=@($states.archerHost.entities|Where-Object {$_.catalog -eq 'Arrow' -and $_.npcProjectile -and $_.owner -eq $states.archerHost.peer})
 if($arrows.Count -eq 0){throw 'No fired native arrow remained for ownership validation'}
 $arrow=$arrows[0]
 Send client @{op='grab-entity';entity=$arrow.id;side='right'}|Out-Null
 Wait-Ranged {$h=State host;$c=State client;$a=@($h.entities|Where-Object id -eq $arrow.id)[0];$b=@($c.entities|Where-Object id -eq $arrow.id)[0];$a -and $b -and $a.owner -eq $c.peer -and $b.owner -eq $c.peer} 'Looting a fired native arrow transfers its existing identity to the client'
 $clientState=State client;$player=@($clientState.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $clientState.peer})[0]
 Wait-Ranged {$h=State host;$c=State client;$a=@($h.entities|Where-Object id -eq $arrow.id)[0];$b=@($c.entities|Where-Object id -eq $arrow.id)[0];$a.lastHandler -eq $player.id -and $b.lastHandler -eq $player.id} 'NPC launch metadata does not replace the new player damage attribution after arrow ownership changes'
 Send client @{op='release';side='right'}|Out-Null
 Clear-Fixture
 Send client @{op='travel';level='Home';mode='Sandbox'}|Out-Null
 Wait-Ranged {$h=State host;$c=State client;$h.level -eq 'Home' -and $c.level -eq 'Home' -and $h.scene.ready -and $c.scene.ready} 'Session returns Home after native ranged/spell combat' 150
 $rangedPassed=$true
}catch{$checks.Add(@{test='Completion';passed=$false;error=$_.ToString()});throw}
finally{
 foreach($n in @('host','client')){try{Send $n @{op='invincibility';enabled=$true}|Out-Null}catch{}}
 @{time=[DateTime]::UtcNow.ToString('o');passed=$rangedPassed;build=$rangedBuild;checks=$checks;resumedFrom=$resumedFrom}|ConvertTo-Json -Depth 10|Set-Content "$rangedRoot/artifacts/native-ranged-report.json"
 $states|ConvertTo-Json -Depth 20|Set-Content "$rangedRoot/artifacts/native-ranged-states.json"
}
