param([ValidateSet('host','client')][string]$Attacker='host',[string[]]$Parts=@('Neck','LeftArm','RightArm','LeftHand','RightHand','LeftLeg','RightLeg','LeftFoot','RightFoot'),[int]$Attempts=3)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$defender=if($Attacker -eq 'host'){'client'}else{'host'}
$tests=[System.Collections.Generic.List[object]]::new();$protection=@{};$passed=$false
function Send($Name,$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 9 -Compress)}
function State($Name){Send $Name @{op='status'}}
function Own($State){@($State.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $State.peer})[0]}
function Wait-For($Condition,$Reason){$deadline=[DateTime]::UtcNow.AddSeconds(30);do{if(&$Condition){return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline);throw "Timed out: $Reason"}
try{
    foreach($name in @('host','client')){$protection[$name]=(State $name).manualInvincibility;Send $name @{op='invincibility';enabled=$true}|Out-Null}
    $previous=(Own (State $Attacker)).id;Send $Attacker @{op='respawn'}|Out-Null
    Wait-For {$s=State $Attacker;$b=Own $s;$s.creature -and $b -and !$b.dead -and $b.sliced.Count -eq 0 -and $b.id -ne $previous} 'Intact native attacker'
    foreach($type in $Parts){
        $succeeded=$false;$attemptsForPart=[System.Collections.Generic.List[object]]::new()
        for($attempt=0;$attempt -lt $Attempts;$attempt++){
            foreach($name in @('host','client')){
                Wait-For {$s=State $name;$b=Own $s;$s.creature -and $b -and !$b.dead} "$name registered living fighter"
                Send $name @{op='invincibility';enabled=$true}|Out-Null
                foreach($side in @('left','right')){Send $name @{op='release';side=$side}|Out-Null}
                Send $name @{op='clear-loose-items'}|Out-Null
            }
            $old=(Own (State $defender)).id;Send $defender @{op='respawn'}|Out-Null
            Wait-For {$s=State $defender;$b=Own $s;$s.creature -and $b -and !$b.dead -and $b.sliced.Count -eq 0 -and $b.id -ne $old} 'Intact native slice target'
            Wait-For {
                $local=Own (State $defender)
                $remote=@((State $Attacker).entities|Where-Object id -eq $local.id)[0]
                $local -and !$local.dead -and $local.id -ne $old -and $remote -and !$remote.dead -and $remote.sliced.Count -eq 0
            } 'Target replica ready'
            $body=Own (State $defender);$id=$body.id
            $part=@($body.parts|Where-Object {$_.type -eq $type -and $_.sliceAllowed}|Sort-Object key)[0]
            if(!$part){$tests.Add(@{part=$type;skipped=$true;reason='No natively sliceable part of this type'});$succeeded=$true;break}
            Send $Attacker @{op='teleport';position=@(0,.08,0);yaw=0}|Out-Null
            Send $defender @{op='teleport';position=@(0,.08,.9);yaw=180}|Out-Null
            $headHeight=if($type -like '*Foot'){.85}elseif($type -like '*Leg'){1.2}elseif($type -like '*Hand'){1.4}else{1.8}
            $defenderHands=if($type -like '*Leg' -or $type -like '*Foot'){1.55}else{.85}
            Send $Attacker @{op='pose';head=@(0,$headHeight,0);headRotation=@(0,0,0);left=@(-.45,1.1,-.2);right=@(.35,1.25,.15);rightRotation=@(0,0,0)}|Out-Null
            Send $defender @{op='pose';head=@(0,1.8,.9);headRotation=@(0,180,0);left=@(.2,$defenderHands,.9);right=@(-.2,$defenderHands,.9);leftRotation=@(0,180,0);rightRotation=@(0,180,0)}|Out-Null
            Start-Sleep -Milliseconds 400
            $targetPart=@((Own (State $defender)).parts|Where-Object key -eq $part.key)[0]
            $cut=$targetPart.slicePosition
            $outside=if([double]$cut[0] -lt -.05){-1}else{1}
            $startX=[double]$cut[0]+.5*$outside;$endX=[double]$cut[0]-.5*$outside;$depth=[double]$cut[2]-.4
            Send $Attacker @{op='pose';right=@($startX,([double]$cut[1]),$depth);rightRotation=@(0,0,90)}|Out-Null
            Send $Attacker @{op='equip';item='SwordShortCommon'}|Out-Null
            Start-Sleep -Milliseconds 650
            $s=State $Attacker
            $weapon=@($s.entities|Where-Object {$_.catalog -eq 'SwordShortCommon' -and $_.held -gt 0 -and $_.owner -eq $s.peer})[0]
            $slash=@($weapon.damagers|Where-Object type -eq Slash)[0]
            if(!$slash){$tests.Add(@{part=$type;passed=$false;preparation=$s});throw 'The native slice fixture has no held blade'}
            $height=[double]$s.right[1]+[double]$cut[1]-[double]$slash.position[1]+($attempt%3-1)*.015
            $stroke=@{start=@($startX,$height,$depth);end=@($endX,$height,$depth);rotation=@(0,0,90);point=$cut}
            Send $Attacker @{op='pose';right=$stroke.start;rightRotation=$stroke.rotation}|Out-Null
            Start-Sleep -Milliseconds 350
            if($type -like '*Hand' -or $type -like '*Foot'){
                $joint=@((Own (State $defender)).parts|Where-Object key -eq $part.key)[0]
                Send $Attacker @{op='aim-joint-slash';entity=$id;part=$part.key;point=$joint.slicePosition;normal=$joint.sliceDirection;stage='orient'}|Out-Null
                Start-Sleep -Milliseconds 450
                for($settle=0;$settle -lt 2;$settle++){
                    $travel=if($type -like '*Leg'){.3}else{.4}
                    $stroke=Send $Attacker @{op='aim-joint-slash';entity=$id;part=$part.key;point=$joint.slicePosition;normal=$joint.sliceDirection;stage='position';distance=$travel}
                    Start-Sleep -Milliseconds 350
                }
            }
            $armed=State $Attacker
            if($armed.rightItem -ne 'SwordShortCommon'){
                $attemptsForPart.Add(@{attempt=$attempt;stroke=$stroke;preparation=$armed})
                $tests.Add(@{part=$type;passed=$false;attempts=$attemptsForPart})
                throw "The native controller could not retain its sword while preparing $type"
            }
            Send $defender @{op='invincibility';enabled=$false}|Out-Null
            $at=(State $defender).time
            Send $Attacker @{op='motion';to=$stroke.end;rotation=$stroke.rotation;duration=.05}|Out-Null
            Start-Sleep -Milliseconds 220
            Send $Attacker @{op='release';side='right'}|Out-Null
            Send $Attacker @{op='clear-loose-items'}|Out-Null
            Start-Sleep -Milliseconds 650
            $a=State $Attacker;$b=State $defender
            $remote=@($a.entities|Where-Object id -eq $id)[0];$local=@($b.entities|Where-Object id -eq $id)[0]
            $expected=if($null -ne $part.sliceChild){$part.sliceChild}else{$part.typeValue}
            $attemptsForPart.Add(@{attempt=$attempt;stroke=$stroke;preparation=$armed;expected=$expected;victim=$id;localSliced=$local.sliced;remoteSliced=$remote.sliced;localDead=$local.dead;remoteDead=$remote.dead;trace=@($b.sliceTrace|Where-Object {$_.time -ge $at});damage=@($b.damageTrace|Where-Object {$_.time -ge $at})})
            if($local -and $remote -and $expected -in $local.sliced -and $expected -in $remote.sliced -and $local.dead -eq $remote.dead){$succeeded=$true;break}
        }
        if(!$succeeded){$tests.Add(@{part=$type;passed=$false;attempts=$attemptsForPart});throw "Native physical $type cut did not replicate the intended joint"}
        if($attemptsForPart.Count){$tests.Add(@{part=$type;passed=$true;attempts=$attemptsForPart});Write-Output "PASS: $Attacker physical $type cut replicates the intended native joint"}
    }
    $passed=$true
}catch{$tests.Add(@{passed=$false;error=$_.ToString()});throw}
finally{
    foreach($name in @('host','client')){try{Send $name @{op='invincibility';enabled=$protection[$name]}|Out-Null}catch{}}
    @{time=[DateTime]::UtcNow.ToString('o');passed=$passed;attacker=$Attacker;tests=$tests}|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $workspace "artifacts\joint-slices-$Attacker-report.json")
}
