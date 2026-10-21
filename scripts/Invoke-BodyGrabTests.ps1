param()
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$tests=[System.Collections.Generic.List[object]]::new();$samples=[System.Collections.Generic.List[object]]::new();$protection=@{};$passed=$false;$failureState=$null
function Send($Name,$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 9 -Compress)}
function State($Name){Send $Name @{op='status'}}
function Own($State){@($State.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $State.peer})[0]}
function Distance($A,$B){[Math]::Sqrt([Math]::Pow($A[0]-$B[0],2)+[Math]::Pow($A[1]-$B[1],2)+[Math]::Pow($A[2]-$B[2],2))}
function Wait-For($Condition,$Reason){$deadline=[DateTime]::UtcNow.AddSeconds(12);do{if(&$Condition){return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline);throw "Timed out: $Reason"}
try{
    foreach($name in @('host','client')){
        Wait-For {$s=State $name;$body=Own $s;$s.creature -and $body -and !$body.dead} "$name living native player before body-grab setup"
        $s=State $name;$protection[$name]=$s.manualInvincibility
        Send $name @{op='invincibility';enabled=$true}|Out-Null
        foreach($side in @('left','right')){Send $name @{op='release';side=$side}|Out-Null}
        Send $name @{op='clear-loose-items'}|Out-Null
        $old=(Own $s).id;Send $name @{op='respawn'}|Out-Null
        Wait-For {$s=State $name;$s.creature -and (Own $s).id -ne $old} "$name intact body"
    }
    foreach($grabber in @('host','client')){
        $victim=if($grabber -eq 'host'){'client'}else{'host'}
        foreach($name in @('host','client')){foreach($side in @('left','right')){Send $name @{op='release';side=$side}|Out-Null};Send $name @{op='clear-loose-items'}|Out-Null}
        Send $grabber @{op='teleport';position=@(0,.08,0);yaw=0}|Out-Null
        Send $victim @{op='teleport';position=@(0,.08,.6);yaw=180}|Out-Null
        Send $grabber @{op='pose';head=@(0,1.83,0);headRotation=@(0,0,0);left=@(-.45,1.15,-.2);right=@(.45,1.15,-.2);leftRotation=@(0,0,0);rightRotation=@(0,0,0)}|Out-Null
        Send $victim @{op='pose';head=@(0,1.83,.6);headRotation=@(0,180,0);left=@(.3,1.3,.2);right=@(-.3,1.3,.2);leftRotation=@(0,180,0);rightRotation=@(-55,180,0)}|Out-Null
        Start-Sleep -Milliseconds 450
        Send $victim @{op='equip';item='SwordShortCommon';side='right'}|Out-Null
        Wait-For {$s=State $grabber;$body=@($s.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -ne $s.peer})[0];$body.rightHeld -and (State $victim).rightItem -eq 'SwordShortCommon'} 'Native sword held on both peers before grappling'
        foreach($type in @('LeftArm','RightArm','LeftHand','RightHand')){
            $before=State $grabber;$opponent=@($before.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -ne $before.peer})[0]
            $part=@($opponent.parts|Where-Object {$_.type -eq $type -and $_.handles -gt 0})[0]
            if(!$part){$tests.Add(@{grabber=$grabber;part=$type;skipped=$true;reason='The native player prefab has no handle on this part'});continue}
            if((State $victim).rightItem -ne 'SwordShortCommon'){throw 'The victim lost its sword before the body-grab fixture began'}
            Send $grabber @{op='motion';side='left';to=@(-.45,1.15,-.2);duration=.3}|Out-Null
            Start-Sleep -Milliseconds 450
            # Latch the button in empty space before positioning the hand. Its
            # press event otherwise performs an independent native auto-grab
            # (possibly stealing the nearby sword) before grab-body selects the arm.
            Send $grabber @{op='grip';side='left';pressed=$true}|Out-Null
            Send $grabber @{op='pose';left=$part.position;snap=$false}|Out-Null
            Start-Sleep -Milliseconds 350
            $nativeGrab=Send $grabber @{op='grab-body';entity=$opponent.id;part=$part.key;side='left'}
            if(!$nativeGrab.held -or !$nativeGrab.gripPressed){throw 'Native relative body grip did not start with its controller button held'}
            if($nativeGrab.shoulderDistance -gt $nativeGrab.armReach){throw "The native grip fixture exceeds arm reach: $($nativeGrab.shoulderDistance) > $($nativeGrab.armReach)"}
            try{Wait-For {
                $a=State $grabber;$b=State $victim
                @($a.cachedGrips|Where-Object {$_.other -eq $opponent.id -and $_.side -eq 1}).Count -gt 0 -and @($b.cachedGrips|Where-Object {$_.other -eq $opponent.id -and $_.side -eq 1}).Count -gt 0 -and @((Own $b).parts|Where-Object {$_.key -eq $part.key -and $_.isGrabbed}).Count -gt 0
            } "$grabber $type native grip on both peers"}
            catch{
                $tests.Add(@{test='Native grip start diagnostics';grabber=$grabber;part=$type;nativeGrab=$nativeGrab;source=(State $grabber);target=(State $victim)})
                throw
            }
            $filters=& (Join-Path $PSScriptRoot 'Assert-TestGripCollisions.ps1') -Owner $victim -Observer $grabber -Entity $opponent.id
            Send $grabber @{op='motion';side='left';to=@(($part.position[0]+.12),($part.position[1]+.2),($part.position[2]-.1));duration=.4}|Out-Null
            for($i=0;$i -lt 12;$i++){
                foreach($name in @('host','client')){
                    $s=State $name;$local=Own $s
                    if(!$s.creature -or $local.dead -or [Math]::Abs($s.root[1]) -gt .8 -or (Distance $local.position $s.root) -gt .8){throw "$grabber $type grab displaced $name's native root"}
                    foreach($body in @($s.entities|Where-Object kind -eq 'Player')){
                        foreach($p in $body.parts){
                            $offset=Distance $p.position $body.position;$speed=Distance $p.velocity @(0,0,0)
                            if($offset -gt 3 -or $speed -gt 35){$failureState=$s;throw "$grabber $type grab displaced $name entity $($body.id) part $($p.type) key $($p.key): offset=$offset m, speed=$speed m/s"}
                        }
                    }
                    $armed=@($s.entities|Where-Object id -eq $opponent.id)[0]
                    $weapon=@($s.entities|Where-Object id -eq $armed.rightHeld)[0]
                    if(!$weapon -or (Distance $weapon.position $armed.rightHand) -gt 1.2){$failureState=@{source=State $grabber;target=State $victim;nativeGrab=$nativeGrab};throw "$grabber $type grip lost or launched the victim's held sword on $name"}
                    $samples.Add(@{grabber=$grabber;part=$type;peer=$name;root=$s.root;head=$s.head;body=$local.position;velocity=$local.velocity})
                }
                Start-Sleep -Milliseconds 100
            }
            Send $grabber @{op='release';side='left'}|Out-Null
            Send $grabber @{op='grip';side='left';pressed=$false}|Out-Null
            Wait-For {
                $a=State $grabber;$b=State $victim
                @($a.cachedGrips|Where-Object {$_.other -eq $opponent.id}).Count -eq 0 -and @($b.cachedGrips|Where-Object {$_.other -eq $opponent.id}).Count -eq 0 -and @((Own $b).parts|Where-Object isGrabbed).Count -eq 0
            } "$grabber $type clean release"
            foreach($name in @('host','client')){if(!(State $name).locomotionEnabled){throw "$name lost native locomotion after the grip"}}
            $tests.Add(@{grabber=$grabber;part=$type;nativeGrab=$nativeGrab;collisionFilters=$filters;passed=$true});Write-Output "PASS: $grabber grabs, moves and releases $type without runaway bodies or lost locomotion"
        }
    }
    $passed=$true
}catch{$tests.Add(@{passed=$false;error=$_.ToString()});throw}
finally{
    $states=@{}
    foreach($name in @('host','client')){try{Send $name @{op='release';side='left'}|Out-Null;Send $name @{op='grip';side='left';pressed=$false}|Out-Null;if($protection.ContainsKey($name)){Send $name @{op='invincibility';enabled=$protection[$name]}|Out-Null};$states[$name]=State $name}catch{}}
    @{time=[DateTime]::UtcNow.ToString('o');passed=$passed;tests=$tests;samples=$samples;states=$states;failureState=$failureState}|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\body-grab-report.json')
}
