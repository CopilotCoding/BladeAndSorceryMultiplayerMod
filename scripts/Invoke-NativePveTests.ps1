param([switch]$SkipLaunch,[switch]$KeepRunning,[switch]$ResumeProgression)
$ErrorActionPreference='Stop'
$pveRoot=Split-Path -Parent $PSScriptRoot
$checks=[Collections.Generic.List[object]]::new();$snapshots=@{};$pvePassed=$false;$pveBuild=$null
$resumedFrom=$null
function Send([string]$Name,[hashtable]$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 12 -Compress)}
function State([string]$Name){Send $Name @{op='status'}}
function Pass([string]$Text){$checks.Add(@{test=$Text;passed=$true});Write-Output "PASS: $Text"}
function Wait-Pve([scriptblock]$Condition,[string]$Text,[int]$Seconds=50){
 $until=[DateTime]::UtcNow.AddSeconds($Seconds)
 do{if(&$Condition){Pass $Text;return};Start-Sleep -Milliseconds 200}while([DateTime]::UtcNow -lt $until)
 throw "Timed out: $Text"
}
function Live([string]$Name){@((State $Name).entities|Where-Object {$_.kind -eq 'Creature' -and !$_.dead})}
function Wave([string]$Name){@((State $Name).nativeWaves)[0]}
function Same-Wave([string[]]$Names=@('host','client')){
 $a=Wave $Names[0]
 foreach($n in $Names|Select-Object -Skip 1){$b=Wave $n;foreach($key in @('key','selected','wave','run','running','outcome','round','alive','queued','kills','cleanup')){if($a.$key -ne $b.$key){return $false}}}
 $true
}
function Start-Wave([string]$Id,[string]$Sender='client'){
 Send $Sender @{op='native-wave-select';wave=$Id}|Out-Null
 Wait-Pve {(Wave host).selected -eq $Id -and (Wave client).selected -eq $Id} "Native book selection $Id reaches both players"
 Send $Sender @{op='native-wave-start'}|Out-Null
 Wait-Pve {(Wave host).running -and (Wave host).wave -eq $Id -and @(Live host|Where-Object nativeWaveMember).Count -gt 0 -and (Same-Wave) -and (Live host).Count -eq (Live client).Count} "Native $Id starts once with replicated AI and progress"
}
function Claim-Loot($Item){
 $script:loot=$Item
 Send client @{op='grab-entity';entity=$Item.id;side='right'}|Out-Null
 Wait-Pve {$s=State client;$found=@($s.entities|Where-Object id -eq $Item.id)[0];$found.owner -eq $s.peer} 'Client claims actual native enemy loot with the same object identity'
 $holders=@((State client).localHolders);$slot=-1
 for($index=0;$index -lt $holders.Count;$index++){if(@($holders[$index].items).Count -eq 0){$slot=$index;break}}
 if($slot -lt 0){
  $state=State client;$player=@($state.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $state.peer})[0]
  $previous=@($state.entities|Where-Object {$_.kind -eq 'Item' -and $_.holder -eq $holders[0].name -and $_.holderActor -eq $player.id})[0]
  if(!$previous){throw 'Could not identify the full native holster'}
  Send client @{op='native-store';entity=$previous.id}|Out-Null
  Wait-Pve {@((State client).localHolders[0].items).Count -eq 0} 'Full test holster stores its existing item through native inventory before claiming loot'
  $slot=0
 }
 Send client @{op='holster-entity';entity=$Item.id;index=$slot}|Out-Null
}
try{
 $prior=Join-Path $pveRoot 'artifacts/native-pve-report.json'
 if(Test-Path -LiteralPath $prior){$archive=Join-Path $pveRoot ('artifacts/regression-history/native-pve-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff'));New-Item -ItemType Directory -Path $archive -Force|Out-Null;Copy-Item -LiteralPath $prior -Destination $archive}
 if(!$SkipLaunch -and !$ResumeProgression){& (Join-Path $PSScriptRoot 'Stop-TestInstances.ps1') -Names third;& (Join-Path $PSScriptRoot 'Start-TestPair.ps1')}
 Send host @{op='host';port=19050}|Out-Null;Send client @{op='join';port=19050}|Out-Null
 Wait-Pve {$h=State host;$c=State client;$h.connected -and $c.connected -and $h.scene.ready -and $c.scene.ready -and @($h.entities|Where-Object kind -eq Player).Count -eq 2 -and @($c.entities|Where-Object kind -eq Player).Count -eq 2} 'Two native players join the Arena session'
 $h=State host;$pveBuild=@{game=$h.game;mod=$h.modBuild;core=$h.coreBuild;host=$h.processId;client=(State client).processId}
 if($ResumeProgression){
  $previous=Get-Content "$pveRoot/artifacts/native-pve-report.json" -Raw|ConvertFrom-Json
  $previousStates=Get-Content "$pveRoot/artifacts/native-pve-states.json" -Raw|ConvertFrom-Json
  if($previous.build.mod -ne $h.modBuild -or $previous.build.core -ne $h.coreBuild -or $previous.build.host -ne $h.processId -or $previous.build.client -ne (State client).processId -or !$previousStates.'damage-host' -or !$previousStates.'damage-client'){throw 'Cannot retain native combat evidence from different binaries/processes'}
  $resumedFrom=$previous.time
  foreach($check in $previous.tests){if(!$check.passed){break};$checks.Add($check);if($check.test -eq 'Stationary combat fixture leaves both worlds before the finite native wave'){break}}
  $snapshots['damage-host']=$previousStates.'damage-host';$snapshots['damage-client']=$previousStates.'damage-client'
  Send client @{op='native-wave-stop'}|Out-Null
  Wait-Pve {!(Wave host).running} 'Progression continuation stops the prior native wave'
  foreach($oldNpc in (Live host)){Send host @{op='npc-despawn';entity=$oldNpc.id}|Out-Null}
  Wait-Pve {(Live host).Count -eq 0 -and (Live client).Count -eq 0} 'Progression continuation starts without prior live enemy fixtures'
 }
 foreach($n in @('host','client')){Send $n @{op='invincibility';enabled=$true}|Out-Null;foreach($side in @('right','left')){Send $n @{op='release';side=$side}|Out-Null}}
 if(!$ResumeProgression){
 Start-Wave SpecialWeaponSwordDuel
 $run=(Wave host).run
 Send host @{op='native-wave-start'}|Out-Null;Send client @{op='native-wave-start'}|Out-Null
 if((Wave host).run -ne $run){throw 'Repeated book Start restarted active wave'}
 Pass 'Repeated host/client native Start does not restart or duplicate the active wave'
 Wait-Pve {$h=Live host;$c=Live client;$h.Count -eq 1 -and $c.Count -eq 1 -and $h[0].id -eq $c[0].id -and $h[0].owner -eq 1 -and $h[0].ai.enabled -and !$c[0].ai.enabled -and $h[0].rightHeld -eq $c[0].rightHeld} 'Native wave has one AI simulation and matching enemy equipment'
 $enemy=(Live host)[0]
 foreach($n in @('host','client')){
  Wait-Pve {@(Live host|Where-Object {$_.nativeWaveMember -and $_.rightHeld}).Count -gt 0} "Native duel opponent is equipped for $n damage measurement"
  $enemy=@(Live host|Where-Object {$_.nativeWaveMember -and $_.rightHeld})[0]
  # Test-only durability prevents incidental mock-controller/body contacts
  # killing the AI before measuring its unchanged native damage to the player.
  Send host @{op='npc-damage';entity=$enemy.id;amount=-2000}|Out-Null
  $observer=if($n -eq 'host'){'client'}else{'host'}
  Send $observer @{op='teleport';position=@(-7,.07,-6);yaw=0;navmesh=$true}|Out-Null
  Send $n @{op='teleport';position=@(0,.07,1);yaw=180;navmesh=$true}|Out-Null
  Send host @{op='npc-teleport';entity=$enemy.id;position=@(0,.07,-1.2);yaw=0}|Out-Null
  Send host @{op='npc-ai';entity=$enemy.id;enabled=$true;holdPosition=$true}|Out-Null
  Send $n @{op='engage-npc';entity=$enemy.id;distance=2.2}|Out-Null
  $initial=State $n;$attacks=$enemy.ai.attacks
  Send $n @{op='invincibility';enabled=$false}|Out-Null
  try{
   Wait-Pve {
    $c=State $n;$e=(Live host)[0];$range=$e.ai.meleeRange
    if($range.max -gt $range.min){Send $n @{op='engage-npc';entity=$enemy.id;distance=(($range.min+$range.max)/2)}|Out-Null}
    $c.localHealth -lt $initial.localHealth -and $e.ai.attacks -gt $attacks -and @($c.damageTrace|Where-Object {$_.time -ge $initial.time -and $_.allowed -and $_.damager -match 'Sword|Axe|Mace|Spear|Dagger'}).Count -gt 0
   } "Native AI chooses and lands real blade/weapon damage on $n" 65
  }finally{Send $n @{op='invincibility';enabled=$true}|Out-Null;Send $n @{op='engage-npc';enabled=$false}|Out-Null}
  $victim=@($initial.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $initial.peer})[0]
  Wait-Pve {$a=@((State $n).entities|Where-Object id -eq $victim.id)[0];$b=@((State $observer).entities|Where-Object id -eq $victim.id)[0];$a.health -eq $b.health} "$n native damage converges on both peers"
  $snapshots["damage-$n"]=State $n
 }
 Send client @{op='native-wave-stop'}|Out-Null
 Wait-Pve {!(Wave host).running -and (Same-Wave)} 'Either player stops the native duel wave'
 # This combat fixture disabled movement. It cannot walk out through the
 # native flee exit after Stop, so remove it before the progression fixture.
 foreach($npc in (Live host)){Send host @{op='npc-despawn';entity=$npc.id}|Out-Null}
 Wait-Pve {(Live host).Count -eq 0 -and (Live client).Count -eq 0} 'Stationary combat fixture leaves both worlds before the finite native wave'
 }
 Start-Wave Training1
 $enemy=@(Live host|Where-Object nativeWaveMember)[0]
 # Native kills are a deterministic progression fixture, not a combat claim.
 for($kill=0;$kill -lt 6;$kill++){
  Wait-Pve {@(Live host|Where-Object {$_.nativeWaveMember -and $_.rightHeld}).Count -gt 0} "Finite native wave supplies equipped enemy $($kill+1)" 45
  $npc=@(Live host|Where-Object {$_.nativeWaveMember -and $_.rightHeld})[0]
  if($kill -eq 0){$firstDrop=@((State host).entities|Where-Object id -eq $npc.rightHeld)[0];if(!$firstDrop){throw 'Native loadout registration has not completed'}}
  Send host @{op='npc-ai';entity=$npc.id;enabled=$false}|Out-Null
  Send host @{op='npc-kill';entity=$npc.id}|Out-Null
  Wait-Pve {(Wave host).kills -eq ($kill+1) -and (Wave client).kills -eq ($kill+1)} "Native kill $($kill+1) advances the shared book count"
  if($kill -eq 0){
   Claim-Loot $firstDrop
  }
 }
 Wait-Pve {!(Wave host).running -and (Wave host).outcome -eq 1 -and (Wave host).queued -eq 0 -and (Same-Wave)} 'Finite native wave completes once with matching victory, kills and queue'
 Start-Wave SpecialWeaponSwordDuel host
 Wait-Pve {$h=State host;$c=State client;$a=@($h.entities|Where-Object id -eq $loot.id)[0];$b=@($c.entities|Where-Object id -eq $loot.id)[0];$a -and $b -and $a.owner -eq $c.peer -and @($c.localHolders.items|Where-Object {$_ -eq $loot.catalog}).Count -gt 0} 'Native cleanup keeps claimed holstered enemy loot on both peers'
 Wait-Pve {$e=(Live host)[0];$e.ai.brain -eq 'HumanHard' -and $e.rightHeld -eq (Live client)[0].rightHeld} 'Native Hard enemy choice retains its own brain and equipment'
 & (Join-Path $PSScriptRoot 'Start-TestInstance.ps1') -Name third
 Wait-Pve {(State third).creature} 'Third player reaches its native local body' 180
 Send third @{op='invincibility';enabled=$true}|Out-Null;Send third @{op='join';port=19050}|Out-Null
 Wait-Pve {(State third).connected -and (State third).scene.ready -and (Same-Wave @('host','client','third')) -and (Live third).Count -eq (Live host).Count -and @((State third).entities|Where-Object kind -eq Player).Count -eq 3} 'Late join receives active native wave, queue, enemies and safe player slot' 90
 Send third @{op='native-wave-stop'}|Out-Null
 Wait-Pve {!(Wave host).running -and (Wave host).outcome -eq 2 -and (Same-Wave @('host','client','third'))} 'Late joining player can stop the native wave through its normal book'
 Send third @{op='disconnect'}|Out-Null
 & (Join-Path $PSScriptRoot 'Stop-TestInstances.ps1') -Names third
 # Travel cleanup clears active wave state and retains personal equipment.
 Send client @{op='travel';level='Home';mode='Sandbox'}|Out-Null
 Wait-Pve {$h=State host;$c=State client;$h.level -eq 'Home' -and $c.level -eq 'Home' -and $h.scene.ready -and $c.scene.ready -and (Live host).Count -eq 0 -and (Live client).Count -eq 0 -and @($c.localHolders.items|Where-Object {$_ -eq $loot.catalog}).Count -gt 0} 'Returning together to Home removes wave enemies and preserves claimed loadout' 150
 $pvePassed=$true
}catch{$checks.Add(@{test='Run completion';passed=$false;error=$_.ToString()});throw}
finally{
 foreach($n in @('host','client')){try{Send $n @{op='engage-npc';enabled=$false}|Out-Null;Send $n @{op='invincibility';enabled=$true}|Out-Null;$snapshots[$n]=State $n}catch{}}
 @{time=[DateTime]::UtcNow.ToString('o');passed=$pvePassed;build=$pveBuild;tests=$checks;resumedFrom=$resumedFrom;combatFixture='Native duel AI with test-only extra health against incidental controller/body contacts; native player damage unchanged. Finite wave progression uses explicit native kills.'}|ConvertTo-Json -Depth 12|Set-Content (Join-Path $pveRoot 'artifacts/native-pve-report.json')
 $snapshots|ConvertTo-Json -Depth 20|Set-Content (Join-Path $pveRoot 'artifacts/native-pve-states.json')
 if(!$KeepRunning){& (Join-Path $PSScriptRoot 'Stop-TestInstances.ps1') -Names host,client,third}
}
