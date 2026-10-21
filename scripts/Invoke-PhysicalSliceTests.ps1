param([ValidateSet('host','client')][string]$Attacker='host',[ValidateRange(1,20)][int]$Attempts=3,[string]$Weapon='SwordShortCommon',[switch]$TwoHanded,[double]$StrokeSeconds=.05,[switch]$Arc,[double]$MinimumContactSpeed=0,[ValidateRange(15,70)][double]$ArcAngle=45,[switch]$UnprotectedOnly)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$defender=if($Attacker -eq 'host'){'client'}else{'host'}
function Send([string]$Instance,[hashtable]$Command) { & (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Instance -Json ($Command|ConvertTo-Json -Compress -Depth 12) }
function State([string]$Instance) { Send $Instance @{op='status'} }
function OwnBody($State) { @($State.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $State.peer})[0] }
function Wait-Body([string]$Instance,[string]$OldId) {
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    while([DateTime]::UtcNow -lt $deadline) {
        $s=State $Instance;$body=OwnBody $s
        if($body -and $body.id -ne $OldId -and !$body.dead -and $s.localHealth -gt 0 -and $body.sliced.Count -eq 0){return}
        Start-Sleep -Milliseconds 100
    }
    throw "$Instance did not obtain a new intact native body."
}
$report=@{time=[DateTime]::UtcNow.ToString('o');attacker=$Attacker;weapon=$Weapon;arc=[bool]$Arc;duration=$StrokeSeconds;minimumContactSpeed=$MinimumContactSpeed;passed=$false;attempts=@()}
$initial=State $defender
$restoreInvincibility=$initial.manualInvincibility
$restoreAttackerInvincibility=(State $Attacker).manualInvincibility
try {
    if(!$initial.connected -or $initial.peers -ne 2){throw 'Start a connected arena pair first.'}
    foreach($name in @('host','client')) {
        $deadline=[DateTime]::UtcNow.AddSeconds(10)
        while(!(State $name).creature) {
            if([DateTime]::UtcNow -gt $deadline){throw "$name has no native player body."}
            Start-Sleep -Milliseconds 100
        }
    }
    Send $defender @{op='invincibility';enabled=$false}|Out-Null
    $old=(OwnBody $initial).id
    Send $defender @{op='respawn'}|Out-Null
    Wait-Body $defender $old
    $p=(State $Attacker).root;$x=0.0;$y=[double]$p[1];$z=0.0
    $stages=if($UnprotectedOnly){@($false)}else{@($true,$false)}
    foreach($protected in $stages) {
        $stageSuccesses=0
        for($attempt=0;$attempt -lt $Attempts;$attempt++) {
            foreach($name in @('host','client')){Send $name @{op='test-caption';text="SETUP: $Weapon - no live cut"}|Out-Null}
            foreach($name in @('host','client')){Send $name @{op='invincibility';enabled=$true}|Out-Null}
            foreach($name in @('host','client')){
                $deadline=[DateTime]::UtcNow.AddSeconds(10)
                do{
                    $ready=State $name;$readyBody=OwnBody $ready
                    if($ready.creature -and $readyBody -and !$readyBody.dead){break}
                    if([DateTime]::UtcNow -gt $deadline){throw "$name did not finish its native respawn before the next stroke."}
                    Start-Sleep -Milliseconds 100
                }while($true)
                foreach($side in @('left','right')){Send $name @{op='release';side=$side}|Out-Null}
                Send $name @{op='clear-loose-items'}|Out-Null
            }
            # A failed slice can still be a lethal ordinary native hit. Give each
            # unprotected retry an intact fighter, with preparation protected.
            if(!$protected){
                $previous=(OwnBody (State $defender)).id
                Send $defender @{op='respawn'}|Out-Null
                Wait-Body $defender $previous
            }
            Send $Attacker @{op='teleport';position=@($x,$y,$z);yaw=0}|Out-Null
            # Keep long blades clear of the victim while equipment/grips and the
            # wind-up settle. Preparation must not begin with a native impalement.
            Send $defender @{op='teleport';position=@($x,$y,($z+4));yaw=180}|Out-Null
            Send $Attacker @{op='pose';head=@($x,($y+1.75),$z);headRotation=@(0,0,0);left=@(($x-.3),($y+1.2),($z+.2));right=@(($x+.45),($y+1.6),($z+.6));rightRotation=@(0,0,90)}|Out-Null
            Send $defender @{op='pose';head=@($x,($y+1.75),($z+4));headRotation=@(0,180,0);left=@(($x+.3),($y+.8),($z+4));right=@(($x-.3),($y+.8),($z+4))}|Out-Null
            Start-Sleep -Milliseconds 500
            Send $Attacker @{op='equip';item=$Weapon}|Out-Null
            Start-Sleep -Milliseconds 650
            # A one-edged saber must lead with its actual native cutting edge.
            # Keep native angle checks; do not count a back-of-blade stroke as
            # a valid neck-cut fixture for a katana.
            $edgePlan=Send $Attacker @{op='face-cutting-edge';direction=@(-1,0,0)}
            $bladeRotation=$edgePlan.rotation
            Start-Sleep -Milliseconds 350
            $before=State $defender;$body=OwnBody $before;$id=$body.id
            $neck=@($body.parts|Where-Object type -eq 'Neck')[0]
            $neckPoint=@([double]$neck.slicePosition[0],[double]$neck.slicePosition[1],([double]$neck.slicePosition[2]-3))
            $a=State $Attacker
            $heldWeapon=@($a.entities|Where-Object {$_.owner -eq $a.peer -and $_.catalog -eq $Weapon -and $_.held -gt 0})[0]
            $slash=@($heldWeapon.damagers|Where-Object type -eq 'Slash')[0]
            if(!$slash){throw 'The fixture has no native slash damager.'}
            $height=[double]$a.right[1]+$neckPoint[1]-[double]$slash.position[1]+($attempt%3-1)*.015
            $depth=[double]$a.right[2]+$neckPoint[2]-[double]$slash.position[2]
            $expectedNeck=if($null -ne $neck.sliceChild){$neck.sliceChild}else{$neck.typeValue}
            Send $Attacker @{op='pose';right=@(($x+.45),$height,$depth);rightRotation=$bladeRotation}|Out-Null
            Start-Sleep -Milliseconds 350
            if($TwoHanded){
                $s=State $Attacker;$held=@($s.entities|Where-Object {$_.owner -eq $s.peer -and $_.catalog -eq $Weapon -and $_.held -gt 0})[0];$handle=$held.handles[0];$axis=-[double]$handle.axisLength*.4
                $left=@(($handle.position[0]+$handle.up[0]*$axis),($handle.position[1]+$handle.up[1]*$axis),($handle.position[2]+$handle.up[2]*$axis))
                Send $Attacker @{op='pose';left=$left;leftRotation=$bladeRotation}|Out-Null
                Start-Sleep -Milliseconds 200
                Send $Attacker @{op='grab-entity';entity=$held.id;side='left';axis=$axis}|Out-Null
                Start-Sleep -Milliseconds 900
                foreach($name in @($Attacker,$defender)){
                    $s=State $name;$owner=(State $Attacker).peer;$actor=@($s.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $owner})[0]
                    if($actor.leftHeld -ne $held.id -or $actor.rightHeld -ne $held.id){throw 'Two-handed neck-cut fixture did not retain both native grips'}
                }
            }
            $arcPlan=$null
            if($Arc){
                $arcPlan=Send $Attacker @{op='prepare-neck-arc';point=$neckPoint;angle=$ArcAngle}
                Start-Sleep -Milliseconds 900
                $windup=State $Attacker;$windupBlade=@($windup.entities|Where-Object id -eq $heldWeapon.id)[0]
                $windupSlash=@($windupBlade.damagers|Where-Object type -eq 'Slash')[0]
                $verticalCorrection=$neckPoint[1]+($attempt%3-1)*.015-[double]$windupSlash.position[1]
                if([Math]::Abs($verticalCorrection) -gt .15){throw 'Native wind-up blade is too far from its intended neck-height fixture'}
                # Aim using the settled physical blade, including native gravity
                # sag, instead of assuming a commanded wrist is the blade pose.
                Send $Attacker @{op='pose';right=@($windup.right[0],($windup.right[1]+$verticalCorrection),$windup.right[2]);left=@($windup.left[0],($windup.left[1]+$verticalCorrection),$windup.left[2])}|Out-Null
                Start-Sleep -Milliseconds 350
            }
            Send $defender @{op='teleport';position=@($x,$y,($z+1));yaw=180}|Out-Null
            Send $defender @{op='pose';head=@($x,($y+1.75),($z+1));headRotation=@(0,180,0);left=@(($x+.3),($y+.8),($z+1));right=@(($x-.3),($y+.8),($z+1))}|Out-Null
            Start-Sleep -Milliseconds 500
            $armedState=State $Attacker
            $armedBlade=@($armedState.entities|Where-Object id -eq $heldWeapon.id)[0]
            if($armedBlade.penetrating){throw 'The stroke fixture began with an unintended preparation penetration'}
            foreach($name in @('host','client')){Send $name @{op='combat-trace';clear=$true}|Out-Null}
            Send $Attacker @{op='invincibility';enabled=$restoreAttackerInvincibility}|Out-Null
            Send $defender @{op='invincibility';enabled=$protected}|Out-Null
            $caption=if($protected){"INVINCIBILITY CHECK $($attempt+1)/$Attempts - no cut expected"}else{"LIVE NECK CUT $($attempt+1)/$Attempts - $Weapon"}
            foreach($name in @('host','client')){Send $name @{op='test-caption';text=$caption}|Out-Null}
            $scoreState=State 'host'
            $attackerPeer=(State $Attacker).peer;$defenderPeer=(State $defender).peer
            $victimScore=@($scoreState.match.scores|Where-Object peer -eq $defenderPeer)[0]
            $attackerScore=@($scoreState.match.scores|Where-Object peer -eq $attackerPeer)[0]
            $deathsBefore=if($victimScore){[int]$victimScore.deaths}else{0}
            $killsBefore=if($attackerScore){[int]$attackerScore.kills}else{0}
            $contactAt=(State $defender).time
            if($Arc){Send $Attacker @{op='motion';pivot=$arcPlan.pivot;duration=$StrokeSeconds;rotation=$arcPlan.rotation;both=[bool]$TwoHanded}|Out-Null}
            else{Send $Attacker @{op='motion';to=@(($x-.45),$height,$depth);duration=$StrokeSeconds;rotation=$bladeRotation;both=[bool]$TwoHanded}|Out-Null}
            Start-Sleep -Milliseconds ([Math]::Max(250,1000*$StrokeSeconds+150))
            # End live sword contacts before comparing the terminal cut/death.
            foreach($side in @('left','right')){Send $Attacker @{op='release';side=$side}|Out-Null}
            Send $Attacker @{op='clear-loose-items'}|Out-Null
            Start-Sleep -Milliseconds 600
            $after=State $defender;$observed=State $Attacker
            $local=@($after.entities|Where-Object id -eq $id)[0];$remote=@($observed.entities|Where-Object id -eq $id)[0]
            $hits=@($after.damageTrace|Where-Object {$_.time -gt $contactAt -and $_.victim -eq $id -and $_.damager -eq $slash.id})
            $sourceTrace=@(Send $Attacker @{op='combat-trace'}|Where-Object {$_.victim -eq $id -and $_.damager -eq $slash.id})
            $targetTrace=@(Send $defender @{op='combat-trace'}|Where-Object {$_.victim -eq $id -and $_.damager -eq $slash.id})
            # Native TryPerformSlice also checks a contacted part's parent and
            # children: an upper-torso collider can legitimately cut the neck.
            $sourcePeak=($sourceTrace|Where-Object {$_.phase -in @('hit','slice','candidate') -and ($_.partType -in @('Neck','Head') -or @($_.sliceChecks|Where-Object {$_.type -eq 'Neck' -and $_.allowed -and $_.aligned}).Count -gt 0)}|Measure-Object speed -Maximum).Maximum
            $targetPeak=($targetTrace|Where-Object {$_.phase -in @('hit','slice','candidate') -and ($_.partType -in @('Neck','Head') -or @($_.sliceChecks|Where-Object {$_.type -eq 'Neck' -and $_.allowed -and $_.aligned}).Count -gt 0)}|Measure-Object speed -Maximum).Maximum
            $acceptedSpeed=($targetTrace|Where-Object phase -eq 'cut-accepted'|Measure-Object speed -Maximum).Maximum
            $nativeVictimSliceSpeed=($targetTrace|Where-Object {$_.phase -eq 'slice' -and $_.result -and ($_.partType -in @('Neck','Head') -or @($_.sliceChecks|Where-Object type -eq 'Neck').Count -gt 0)}|Measure-Object speed -Maximum).Maximum
            $scoreAfter=State 'host'
            $victimScore=@($scoreAfter.match.scores|Where-Object peer -eq $defenderPeer)[0]
            $attackerScore=@($scoreAfter.match.scores|Where-Object peer -eq $attackerPeer)[0]
            $deathsAfter=if($victimScore){[int]$victimScore.deaths}else{0}
            $killsAfter=if($attackerScore){[int]$attackerScore.kills}else{0}
            $scorePassed=if($protected){$deathsAfter -eq $deathsBefore -and $killsAfter -eq $killsBefore}else{$deathsAfter -eq $deathsBefore+1 -and $killsAfter -eq $killsBefore+1}
            # Keep replica speed as a separate diagnostic. Native source contact
            # speed validates a requested cut; the receiver must accept it through
            # its protection/material/plane checks rather than inventing a hit.
            $speedPassed=$MinimumContactSpeed -le 0 -or ($sourcePeak -ge $MinimumContactSpeed -and ($protected -or $acceptedSpeed -ge $MinimumContactSpeed -or $nativeVictimSliceSpeed -ge $MinimumContactSpeed))
            $cutSucceeded=$expectedNeck -in $local.sliced -and $expectedNeck -in $remote.sliced -and $local.dead -and $remote.dead -and $speedPassed -and $scorePassed
            $report.attempts+=@{protected=$protected;attempt=$attempt;cutSucceeded=[bool]$cutSucceeded;scorePassed=$scorePassed;deathsBefore=$deathsBefore;deathsAfter=$deathsAfter;killsBefore=$killsBefore;killsAfter=$killsAfter;before=$before.localHealth;after=$after.localHealth;victim=$id;hits=$hits;sliceTrace=@($after.sliceTrace|Where-Object {$_.time -gt $contactAt});localSliced=$local.sliced;remoteSliced=$remote.sliced;localDead=$local.dead;remoteDead=$remote.dead;handItem=$observed.rightItem;arc=$arcPlan;preparation=$armedState;sourcePeak=$sourcePeak;targetPeak=$targetPeak;acceptedSourceSpeed=$acceptedSpeed;nativeVictimSliceSpeed=$nativeVictimSliceSpeed;speedPassed=$speedPassed;sourceTrace=$sourceTrace;targetTrace=$targetTrace}
            if($protected) {
                if($after.localHealth -ne $before.localHealth -or $local.dead -or $local.sliced.Count -gt 0){throw 'Native invincibility failed to protect the fighter.'}
                if($hits.Count -gt 0 -and $speedPassed){$stageSuccesses++}
                Write-Output "PROTECTED STROKE $($attempt+1)/$Attempts : no cuts=$($local.sliced.Count -eq 0), source speed=$sourcePeak"
            } elseif($cutSucceeded) {
                if(@($local.sliced|Where-Object {$_ -notin $remote.sliced}).Count -gt 0){throw 'The physical slice did not converge.'}
                $stageSuccesses++
            }
            if(!$protected){
                Write-Output "LIVE STROKE $($attempt+1)/$Attempts : cut=$cutSucceeded, source speed=$sourcePeak, accepted source speed=$acceptedSpeed, victim native cut speed=$nativeVictimSliceSpeed"
                foreach($name in @('host','client')){Send $name @{op='test-caption';text="LIVE RESULT: $stageSuccesses/$($attempt+1) neck cuts - $Weapon"}|Out-Null}
            }
        }
        if($protected){$report.protectedSuccesses=$stageSuccesses}
        else{$report.liveCuts=$stageSuccesses;$report.liveSwings=$Attempts;$report.cutRate=$stageSuccesses/$Attempts}
        if($stageSuccesses -ne $Attempts){throw "Physical slice consistency failed: $stageSuccesses/$Attempts strokes passed (invincibility=$protected). Every stroke counts; no retry success hides a miss."}
        if($protected){$report.invincibilityPassed=$true;Write-Output "PASS: $Attacker all $Attempts native slash contacts preserve invincibility"}
        else{$report.physicalSlicePassed=$true;Write-Output "PASS: $Attacker $Weapon $stageSuccesses/$Attempts live strokes sever the neck and synchronize fatality"}
    }
    $report.passed=$true
}catch{$report.error=$_.ToString();throw}
finally{
    foreach($name in @('host','client')){Send $name @{op='test-caption';text=''}|Out-Null}
    Send $Attacker @{op='invincibility';enabled=$restoreAttackerInvincibility}|Out-Null
    Send $defender @{op='invincibility';enabled=$restoreInvincibility}|Out-Null
    $suffix=if($Weapon -eq 'SwordShortCommon'){''}else{"-$Weapon"}
    if($TwoHanded){$suffix+='-twohand'}
    if($Arc){$suffix+='-arc'}
    if($StrokeSeconds -ne .05){$suffix+="-$StrokeSeconds"}
    $report|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $workspace "artifacts\physical-slice-$Attacker$suffix-report.json")
}
