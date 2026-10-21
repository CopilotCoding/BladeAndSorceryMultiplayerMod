param([switch]$SkipLaunch,[switch]$KeepRunning)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$checks=[Collections.Generic.List[object]]::new();$audits=[Collections.Generic.List[object]]::new();$passed=$false;$build=$null
function Send($n,$c){& "$PSScriptRoot/Send-TestCommand.ps1" -Name $n -Json ($c|ConvertTo-Json -Depth 8 -Compress)}
function State($n){Send $n @{op='status'}}
function Entity($n,$id){@((State $n).entities|Where-Object id -eq $id)[0]}
function Wait-Check($condition,$reason){$end=[DateTime]::UtcNow.AddSeconds(40);do{if(&$condition){$checks.Add(@{test=$reason;passed=$true});Write-Output "PASS: $reason";return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $end);throw "Timed out: $reason"}
function Assert-Snap($n,$id,$before,$label){
 Wait-Check {$e=Entity $n $id;$e.teleportSnaps -gt $before} "$label receives a teleport discontinuity"
 $e=Entity $n $id;$a=$e.teleportAudit;$audits.Add(@{test=$label;entity=$id;receiver=$n;audit=$a})
 if(!$a -or $a.buffers -ne 1 -or $a.rootError -gt .001 -or $a.rotationError -gt .1 -or $a.partError -gt .001 -or $a.sampleError -gt .001){throw "$label did not immediately snap every received body and discard the old interpolation poses: $($a|ConvertTo-Json -Compress)"}
 $checks.Add(@{test="$label snaps immediately without an intermediate interpolated pose";passed=$true});Write-Output "PASS: $label snaps immediately without an intermediate interpolated pose"
}
try{
 if(!$SkipLaunch){& "$PSScriptRoot/Stop-TestInstances.ps1" -Names third;& "$PSScriptRoot/Start-TestPair.ps1" -ReadyTimeoutSeconds 240}
 Send host @{op='host';port=19050}|Out-Null;Send client @{op='join';port=19050}|Out-Null
 Wait-Check {$h=State host;$c=State client;$h.connected -and $c.connected -and $h.scene.ready -and $c.scene.ready -and @($c.entities|Where-Object kind -eq Player).Count -eq 2} 'Two native players are ready for teleport checks'
 $h=State host;$build=@{mod=$h.modBuild;core=$h.coreBuild}
 foreach($n in @('host','client')){
  Send $n @{op='test-caption';text='AUTOMATED TELEPORT TEST: scripted jumps and turns must SNAP on the other peer.'}|Out-Null
  Send $n @{op='invincibility';enabled=$true}|Out-Null
 }
 foreach($n in @('host','client')){
  $other=if($n -eq 'host'){'client'}else{'host'};$s=State $n;$actor=@($s.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $s.peer})[0]
  Send $n @{op='equip';item='SwordShortCommon';side='right'}|Out-Null
  Wait-Check {$a=Entity $n $actor.id;$b=Entity $other $actor.id;$a.rightHeld -and $a.rightHeld -eq $b.rightHeld} "$n held sword reaches the remote native grip"
  $weapon=(Entity $n $actor.id).rightHeld;$before=(Entity $other $actor.id).teleportSnaps;$weaponBefore=(Entity $other $weapon).teleportSnaps
  $destination=if($n -eq 'host'){@(-5,.08,4)}else{@(5,.08,-4)}
  Send $n @{op='teleport';position=$destination;yaw=90;navmesh=$true}|Out-Null
  Assert-Snap $other $actor.id $before "$n long teleport"
  Assert-Snap $other $weapon $weaponBefore "$n carried sword teleport"
  $source=Entity $n $actor.id;$before=(Entity $other $actor.id).teleportSnaps
  Send $n @{op='teleport';position=$source.position;yaw=180}|Out-Null
  Assert-Snap $other $actor.id $before "$n rotation-only teleport"
  $source=Entity $n $actor.id;$before=(Entity $other $actor.id).teleportSnaps
  Send $n @{op='teleport';position=@(($source.position[0]+.2),$source.position[1],$source.position[2]);yaw=180}|Out-Null
  Assert-Snap $other $actor.id $before "$n short teleport"
 }
 $passed=$true
}catch{$checks.Add(@{test='Completion';passed=$false;error=$_.ToString()});throw}
finally{
 foreach($n in @('host','client')){try{Send $n @{op='test-caption';text=''}|Out-Null}catch{}}
 @{time=[DateTime]::UtcNow.ToString('o');passed=$passed;build=$build;checks=$checks;audits=$audits}|ConvertTo-Json -Depth 10|Set-Content "$workspace/artifacts/teleport-report.json"
 if(!$KeepRunning){& "$PSScriptRoot/Stop-TestInstances.ps1" -Names host,client,third}
}
