param([switch]$SkipLaunch,[switch]$KeepRunning)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$checks=[Collections.Generic.List[object]]::new();$passed=$false;$build=$null
function Send($name,$command){& "$PSScriptRoot/Send-TestCommand.ps1" -Name $name -Json ($command|ConvertTo-Json -Depth 12 -Compress)}
function State($name){Send $name @{op='status'}}
function Probe($name,$id){Send $name @{op='compatibility-inspect';entity=$id}}
function Pass($reason){$checks.Add(@{test=$reason;passed=$true});Write-Output "PASS: $reason"}
function Audit-World {
    $a=Send host @{op='world-audit'};$b=Send client @{op='world-audit'}
    $issues=[Collections.Generic.List[string]]::new();$checked=0;$maxPosition=0.0;$maxAngle=0.0
    if(!$a.connected -or !$b.connected -or $a.level -ne $b.level -or $a.seed -ne $b.seed){$issues.Add('Session/map/seed mismatch')}
    $aIds=@($a.items|ForEach-Object id|Sort-Object);$bIds=@($b.items|ForEach-Object id|Sort-Object)
    if(@($a.items|Where-Object {!$_.id}).Count -or @($b.items|Where-Object {!$_.id}).Count -or ($aIds -join ',') -ne ($bIds -join ',')){$issues.Add('Missing, duplicate or unregistered items')}
    foreach($left in $a.items){
        $right=@($b.items|Where-Object id -eq $left.id)[0];if(!$right){continue}
        if($left.catalog -ne $right.catalog -or $left.owner -ne $right.owner -or $left.epoch -ne $right.epoch){$issues.Add("Identity mismatch $($left.id)");continue}
        $source=if($left.owned){$left}else{$right}
        $speed=0.0;$spin=0.0;for($i=0;$i -lt 3;$i++){$speed+=$source.velocity[$i]*$source.velocity[$i];$spin+=$source.angular[$i]*$source.angular[$i]}
        if($source.held -gt 0 -or $source.holstered -or $source.penetrating -or $speed -gt .0009 -or $spin -gt .0025){continue}
        $distance=0.0;$dot=0.0;for($i=0;$i -lt 3;$i++){$distance+=[Math]::Pow($left.position[$i]-$right.position[$i],2)}
        for($i=0;$i -lt 4;$i++){$dot+=$left.rotation[$i]*$right.rotation[$i]}
        $distance=[Math]::Sqrt($distance);$angle=2*[Math]::Acos([Math]::Min(1.0,[Math]::Abs($dot)))*180/[Math]::PI
        $checked++;$maxPosition=[Math]::Max($maxPosition,$distance);$maxAngle=[Math]::Max($maxAngle,$angle)
        if($distance -gt .02 -or $angle -gt 1){$issues.Add("$($left.catalog) $($left.id): $distance m / $angle degrees")}
    }
    $result=@{matching=$issues.Count -eq 0 -and $checked -gt 0;checked=$checked;maxPosition=$maxPosition;maxAngle=$maxAngle;issues=$issues.ToArray();host=$a;client=$b}
    $result|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $workspace 'artifacts/world-prop-audit.json')
    return $result
}
function Wait-Check($condition,$reason,$seconds=40){
    $deadline=[DateTime]::UtcNow.AddSeconds($seconds)
    do{try{if(&$condition){Pass $reason;return}}catch{};Start-Sleep -Milliseconds 200}while([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $reason"
}
try{
    if(!$SkipLaunch){& "$PSScriptRoot/Start-TestPair.ps1"}
    $s=State host;$build=@{mod=$s.modBuild;core=$s.coreBuild}
    foreach($check in (Send host @{op='compatibility-file-checks'}).checks){Pass $check}
    foreach($name in @('host','client')){
        Send $name @{op='disconnect'}|Out-Null
        Send $name @{op='compatibility-register'}|Out-Null
    }
    Wait-Check {(Send host @{op='compatibility-status'}).ready -and (Send client @{op='compatibility-status'}).ready} 'Background content fingerprints and registered adapter signatures complete'
    Send host @{op='host'}|Out-Null
    Wait-Check {(State host).connected} 'Host starts with content admission enabled'
    Send client @{op='compatibility-mismatch';enabled=$true}|Out-Null
    Send client @{op='join'}|Out-Null
    Wait-Check {$c=Send client @{op='compatibility-status'};$c.notice -like '*Mod mismatch*' -and !(State client).connected -and (State host).peers -eq 1} 'Synthetic incompatible content rejected before world membership with named notice'
    Send client @{op='compatibility-mismatch';enabled=$false}|Out-Null
    Send client @{op='join'}|Out-Null
    Wait-Check {(State host).peers -eq 2 -and (State client).scene.ready} 'Matching mod content joins and reaches native world catch-up'
    Wait-Check {(Audit-World).matching} 'All settled arena items match catalog, ownership, position within 2 cm and rotation within 1 degree'
    $before=(Send client @{op='world-audit'}).corrections
    $perturbed=Send client @{op='world-perturb-replicas'}
    if($perturbed.count -lt 1){throw 'No loose replica props available for drift recovery fixture'}
    Wait-Check {(Audit-World).matching -and (Send client @{op='world-audit'}).corrections -gt $before} 'Eight deliberately displaced/rotated loose replicas recover the owner resting poses' 60
    $id=(Send host @{op='compatibility-spawn'}).entity
    Wait-Check {$p=Probe client $id;$p.catalog -eq 'BASMP_Test_ModSword' -and $p.custom -eq 7 -and ($p.marker -join ',') -eq '1,2,3' -and $p.adapter -eq 11} 'Synthetic catalog sword spawns with native custom data, Unity value fields and adapter state'
    Send host @{op='compatibility-set';entity=$id;custom=17;value=23}|Out-Null
    Wait-Check {$p=Probe client $id;$p.custom -eq 17 -and $p.adapter -eq 23 -and $p.echoBlocked} 'Owner native-data and adapter updates apply without a publication feedback loop'
    if(!(Send host @{op='compatibility-event';entity=$id}).sent){throw 'Owner event was refused'}
    Wait-Check {(Probe client $id).events -eq 1} 'Owner adapter event reaches the replica'
    Send host @{op='compatibility-repeat';entity=$id}|Out-Null
    Wait-Check {(Probe client $id).events -eq 2} 'Repeated reliable adapter event is applied once'
    if((Send client @{op='compatibility-event';entity=$id}).sent){throw 'Non-owner API event was accepted'}
    $rejected=(State host).rejected
    Send client @{op='compatibility-forge';entity=$id}|Out-Null
    Wait-Check {(State host).rejected -gt $rejected -and (Probe client $id).events -eq 2} 'Host rejects a forged adapter event from a non-owner'
    $imbue=Send host @{op='compatibility-imbue';entity=$id}
    if($imbue.groups -lt 1){throw 'Expected native sword imbue group'}
    Wait-Check {@((Probe client $id).imbues|Where-Object {$_.spell -eq 'Fire' -and $_.energy -gt 0}).Count -gt 0} 'Native mod-item imbue state reaches the replica'
    & "$PSScriptRoot/Stop-TestInstances.ps1" -Names third
    & "$PSScriptRoot/Start-TestInstance.ps1" -Name third
    Wait-Check {(State third).creature} 'Third independent game process reaches a native player' 180
    Send third @{op='compatibility-register'}|Out-Null
    Wait-Check {(Send third @{op='compatibility-status'}).ready} 'Late joiner registers the same adapter and content'
    # Native imbues drain normally while the third process starts. Refresh just
    # before joining so this check measures catch-up rather than expired energy.
    Send host @{op='compatibility-imbue';entity=$id}|Out-Null
    Send third @{op='join'}|Out-Null
    Wait-Check {$p=Probe third $id;(State third).peers -eq 3 -and $p.custom -eq 17 -and $p.adapter -eq 23 -and $p.events -eq 0 -and @($p.imbues|Where-Object {$_.spell -eq 'Fire' -and $_.energy -gt 0}).Count -gt 0} 'Late join reconstructs current custom/adapter/imbue state without replaying historical events' 90
    $suppressed=(Send client @{op='world-audit'}).suppressedDespawns
    Send client @{op='compatibility-local-despawn';entity=$id}|Out-Null
    Wait-Check {(Probe client $id).custom -eq 17 -and (Send client @{op='world-audit'}).suppressedDespawns -gt $suppressed} 'Replica-local native cleanup cannot remove an item owned by another peer'
    $recovered=(Send client @{op='world-audit'}).recoveredItems
    Send client @{op='compatibility-destroy-replica';entity=$id}|Out-Null
    Wait-Check {(Probe client $id).custom -eq 17 -and (Probe client $id).adapter -eq 23 -and (Send client @{op='world-audit'}).recoveredItems -gt $recovered} 'Destroyed local representation reconstructs its existing identity and custom state'
    Send client @{op='reset-pose'}|Out-Null
    Send client @{op='grab-entity';entity=$id}|Out-Null
    Wait-Check {$owner=(State client).peer;(Probe host $id).owner -eq $owner -and (Probe client $id).owner -eq $owner} 'Native pickup transfers the compatible item to a new owner'
    Send client @{op='compatibility-set';entity=$id;custom=29;value=31}|Out-Null
    Wait-Check {(Probe host $id).custom -eq 29 -and (Probe host $id).adapter -eq 31 -and (Probe third $id).adapter -eq 31} 'New owner custom state reaches both other peers after handoff'
    Send client @{op='release'}|Out-Null
    Send client @{op='compatibility-despawn';entity=$id}|Out-Null
    Wait-Check {@((State host).entities|Where-Object id -eq $id).Count -eq 0 -and @((State third).entities|Where-Object id -eq $id).Count -eq 0} 'Native despawn removes custom item and its retained descriptor on every peer'
    $passed=$true
}catch{$checks.Add(@{test='Run completion';passed=$false;error=$_.ToString()});throw}
finally{
    @{time=[DateTime]::UtcNow.ToString('o');passed=$passed;build=$build;checks=$checks;representativeSyntheticFixture=$true;thirdPartyModsCertified=$false}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $workspace 'artifacts/mod-compatibility-report.json')
    if(!$KeepRunning){& "$PSScriptRoot/Stop-TestInstances.ps1" -Names host,client,third}
}
