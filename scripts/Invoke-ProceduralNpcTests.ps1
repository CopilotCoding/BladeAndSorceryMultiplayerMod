param([switch]$SkipLaunch,[switch]$KeepRunning)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$checks=[Collections.Generic.List[object]]::new();$build=$null;$passed=$false
function Send($n,$c){& "$PSScriptRoot/Send-TestCommand.ps1" -Name $n -Json ($c|ConvertTo-Json -Depth 10 -Compress) -TimeoutSeconds 60}
function State($n){Send $n @{op='status'}}
function Inspect($n,$id){Send $n @{op='procedural-inspect';entity=$id}}
function Info($n){Send $n @{op='procedural-info'}}
function Pass($s){$checks.Add(@{test=$s;passed=$true});Write-Output "PASS: $s"}
function Wait-Check($condition,$reason,$seconds=50){$until=[DateTime]::UtcNow.AddSeconds($seconds);do{if(&$condition){Pass $reason;return};Start-Sleep -Milliseconds 150}while([DateTime]::UtcNow -lt $until);throw "Timed out: $reason"}
function Distance($a,$b){$sum=0.0;for($i=0;$i -lt 3;$i++){$sum+=[Math]::Pow($a[$i]-$b[$i],2)};[Math]::Sqrt($sum)}
function Stop-Recorded($id,$reason){@(Send host @{op='procedural-history'}|Where-Object {$_.id -eq $id -and $_.reason -eq $reason}).Count -eq 1}
try{
 if(!$SkipLaunch){& "$PSScriptRoot/Stop-TestInstances.ps1" -Names third;& "$PSScriptRoot/Start-TestPair.ps1" -ReadyTimeoutSeconds 240}
 foreach($n in @('host','client')){Send $n @{op='disconnect'}|Out-Null}
 if((Info host).canHost -or @((Info host).humans).Count){throw 'Offline procedural simulation/roster is enabled'};Pass 'Procedural host gate and human roster are disabled offline'
 Send host @{op='host';port=19050}|Out-Null;Send client @{op='join';port=19050}|Out-Null
 Wait-Check {(Info host).canHost -and !(Info client).canHost -and (Info client).ready -and @((Info host).humans).Count -eq 2 -and @((Info client).humans).Count -eq 2} 'Only the ready host can simulate; both peers identify both humans'
 $h=State host;$build=@{mod=$h.modBuild;core=$h.coreBuild}
 $a=Info host;$b=Info client
 if((@($a.humans.id|Sort-Object) -join ',') -ne (@($b.humans.id|Sort-Object) -join ',') -or @($a.humans|Where-Object IsLocal).Count -ne 1 -or @($a.humans|Where-Object replica).Count -ne 1){throw 'Human identities or local/remote distinction is wrong'};Pass 'Human query returns stable peer/entity identities and local/remote creature references'
 foreach($n in @('host','client')){if(!(Send $n @{op='procedural-role-check'}).denied){throw 'Player/client registration was admitted'}};Pass 'Clients cannot register controllers and humans cannot be registered as procedural NPCs'
 $cancel=Send host @{op='procedural-cancel-spawn'};if(!$cancel.canceled){throw 'Cancelable native spawn did not report cancellation'};Pass 'Canceled native spawn reports one cancellation and suppresses its setup callback'
 $normal=Send host @{op='procedural-normal-spawn';position=@(-7,.08,-6)}
 Wait-Check {@((State client).entities|Where-Object id -eq $normal.id).Count -eq 1} 'Ordinary native NPC still replicates without opting in'
 if((Inspect host $normal.id).procedural -or (Send host @{op='procedural-window';entity=$normal.id;value=$false}).effective -or !(Send host @{op='procedural-window';entity=$normal.id;value=$true}).effective){throw 'Ordinary native attack window behavior changed'};Pass 'Ordinary NPC retains its native true/false damage window'
 $spawn=Send host @{op='procedural-spawn';position=@(-3,.08,1)};$id=$spawn.id
 Wait-Check {@((State client).entities|Where-Object id -eq $id).Count -eq 1 -and (Inspect client $id).procedural -and (Inspect client $id).held -eq (Inspect host $id).held} 'Procedural NPC mode and native held sword replicate through ordinary entity/grab messages'
 $source=Inspect host $id;$replica=Inspect client $id
 if(!$source.canSimulate -or !$replica.replica -or $replica.canSimulate -or !$replica.gripActive -or $replica.enabled -or $replica.animation -or $replica.colliders -lt 10 -or $replica.joints -lt 5 -or $replica.parts -lt 10){throw 'Remote procedural actor is missing physical bodies/grips or acquired a simulation controller'};Pass 'Replica keeps native colliders/joints and has replicated grip targets, without running host AI/animation'
 if(!(Send host @{op='procedural-window';entity=$id;value=$false}).effective){throw 'Procedural fighter still requires a native animation damage window'};Pass 'Only explicitly procedural NPC bypasses the animation-based damage window'
 if(!(Send host @{op='procedural-duplicate';entity=$id}).denied){throw 'Competing registration admitted'};Pass 'Competing controllers cannot register the same creature twice'
 if(@((Info host).humans).Count -ne 2){throw 'Procedural NPC appeared in human roster'};Pass 'Procedural NPCs remain NPCs and never appear as human targets'
 Send host @{op='procedural-move';entity=$id;offset=@(.12,.05,-.1)}|Out-Null
 Send host @{op='procedural-drive';entity=$id}|Out-Null
 Wait-Check {$a=Inspect host $id;$b=Inspect client $id;(Distance $a.target $b.target) -lt .01 -and $b.appliedSpring -eq 1337 -and $b.appliedDamper -eq 37 -and $b.appliedForce -eq 2300} 'Virtual hand target and custom spring/damper/force apply to the remote physical grip'
 $weapon=(Inspect host $id).held
 Wait-Check {$h=State host;$c=State client;$a=@($h.entities|Where-Object id -eq $weapon)[0];$b=@($c.entities|Where-Object id -eq $weapon)[0];$a -and $b -and $b.physics -and (Distance $a.position $b.position) -lt .18} 'Held weapon remains a dynamic native item and follows the replicated virtual controller'
 Send client @{op='invincibility';enabled=$false}|Out-Null
 $victimPeer=(State client).peer;$health=(State client).localHealth
 Send host @{op='procedural-hit';entity=$id;victimPeer=$victimPeer}|Out-Null
 Wait-Check {(State client).localHealth -lt $health} 'Host scripted native contact reaches the remote victim through existing weapon damage networking'
 $after=(State client).localHealth
 Send client @{op='procedural-hit';entity=$id;victimPeer=$victimPeer}|Out-Null;Start-Sleep -Milliseconds 400
 if([Math]::Abs((State client).localHealth-$after) -gt .01){throw 'Replica applied a duplicate procedural weapon contact'};Pass 'The mirrored client weapon cannot independently apply the same contact damage'
 Send client @{op='invincibility';enabled=$true}|Out-Null
 Send host @{op='npc-damage';entity=$id;amount=5}|Out-Null
 Wait-Check {$h=State host;$c=State client;$a=@($h.entities|Where-Object id -eq $id)[0];$b=@($c.entities|Where-Object id -eq $id)[0];[Math]::Abs($a.health-$b.health) -lt .01} 'Procedural NPC native health updates use existing owner replication'
 & "$PSScriptRoot/Start-TestInstance.ps1" -Name third
 Wait-Check {(State third).creature} 'Third isolated player reaches its native body' 180
 Send third @{op='join';port=19050}|Out-Null
 Wait-Check {(Info third).ready -and @((Info host).humans).Count -eq 3 -and @((State third).entities|Where-Object id -eq $id).Count -eq 1} 'Late join identifies all three human players and reconstructs the existing procedural NPC' 100
 Wait-Check {$a=Inspect host $id;$b=Inspect third $id;$b.procedural -and $b.replica -and !$b.canSimulate -and $b.held -eq $weapon -and $b.appliedSpring -eq 1337 -and (Distance $a.target $b.target) -lt .01} 'Late join restores current hand targets, sword identity and physical grip settings'
 $slice=Send host @{op='procedural-slice';entity=$id};if(!$slice.sliced){throw 'Native dismemberment fixture failed'}
 Wait-Check {$a=@((State host).entities|Where-Object id -eq $id)[0];$b=@((State client).entities|Where-Object id -eq $id)[0];$t=@((State third).entities|Where-Object id -eq $id)[0];$a.sliced.Count -gt 0 -and ($a.sliced -join ',') -eq ($b.sliced -join ',') -and ($a.sliced -join ',') -eq ($t.sliced -join ',')} 'Native NPC dismemberment converges through the existing slice pipeline'
 Send host @{op='npc-kill';entity=$id}|Out-Null
 Wait-Check {@((State client).entities|Where-Object {$_.id -eq $id -and $_.dead}).Count -eq 1 -and (Stop-Recorded $id 'Killed') -and !(Inspect host $id).canSimulate} 'Native death stops the external controller exactly once and replicates its corpse'
 Send host @{op='procedural-despawn';entity=$id}|Out-Null
 Wait-Check {@((State host).entities|Where-Object id -eq $id).Count -eq 0 -and @((State client).entities|Where-Object id -eq $id).Count -eq 0 -and @((State third).entities|Where-Object id -eq $id).Count -eq 0} 'Explicit native despawn removes the same NPC identity from every peer'
 $lost=(Send host @{op='procedural-spawn';position=@(-3,.08,1)}).id
 Send host @{op='procedural-lose-target';entity=$lost}|Out-Null
 Wait-Check {(Stop-Recorded $lost 'TargetsLost') -and !(Inspect host $lost).canSimulate -and !(Send host @{op='procedural-window';entity=$lost;value=$false}).effective} 'Lost virtual target fails closed and notifies the controller once'
 Send host @{op='procedural-despawn';entity=$lost}|Out-Null
 $travel=(Send host @{op='procedural-spawn';position=@(-3,.08,1)}).id
 Send client @{op='travel';level='Home';mode='Sandbox'}|Out-Null
 Wait-Check {(Info host).ready -and (Info client).ready -and (Info third).ready -and (State host).level -eq 'Home' -and (Stop-Recorded $travel 'WorldChanged') -and @((State client).entities|Where-Object id -eq $travel).Count -eq 0} 'Map transition invalidates old controller handles and removes their NPCs while the session stays connected' 150
 $ending=(Send host @{op='procedural-spawn';position=@(0,.1,0)}).id
 Send host @{op='disconnect'}|Out-Null
 Wait-Check {!(Info host).canHost -and (Stop-Recorded $ending 'SessionEnded') -and !(State client).connected -and !(State third).connected} 'Session shutdown invalidates the final controller and disconnects replicas cleanly'
 $passed=$true
}catch{$checks.Add(@{test='Completion';passed=$false;error=$_.ToString()});throw}
finally{
 foreach($n in @('host','client')){try{Send $n @{op='invincibility';enabled=$true}|Out-Null}catch{}}
 @{time=[DateTime]::UtcNow.ToString('o');passed=$passed;build=$build;checks=$checks;fixture='Test-only virtual grip targets and dynamic native sword; source body frozen for stable fixture. Contact delivery uses an explicit native CollisionInstance, not an autonomous AI or a real swing. Native kill/slice calls validate lifecycle, not blade-cut reliability.'}|ConvertTo-Json -Depth 8|Set-Content "$workspace/artifacts/procedural-npc-report.json"
 if(!$KeepRunning){& "$PSScriptRoot/Stop-TestInstances.ps1" -Names host,client,third}
}
