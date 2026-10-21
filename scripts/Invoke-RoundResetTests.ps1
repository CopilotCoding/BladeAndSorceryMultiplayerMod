param()
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$tests=[System.Collections.Generic.List[object]]::new();$passed=$false
function Send($Name,$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 9 -Compress)}
function State($Name){Send $Name @{op='status'}}
function Own($State){@($State.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $State.peer})[0]}
function Wait-For($Condition,$Reason){$deadline=[DateTime]::UtcNow.AddSeconds(20);do{if(&$Condition){$tests.Add(@{test=$Reason;passed=$true});Write-Output "PASS: $Reason";return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline);throw "Timed out: $Reason"}
try{
    foreach($name in @('host','client')){foreach($side in @('left','right')){Send $name @{op='release';side=$side}|Out-Null};Send $name @{op='clear-loose-items'}|Out-Null}
    foreach($owner in @('host','client')){
        $observer=if($owner -eq 'host'){'client'}else{'host'}
        $s=State $owner;$p=$s.root;$x=[double]$p[0];$y=[double]$p[1];$z=[double]$p[2]
        Send $owner @{op='pose';head=@($x,($y+1.1),$z);left=@(($x-.3),($y+.7),($z+.25));right=@(($x+.3),($y+1.6),($z+.25))}|Out-Null
        Send $owner @{op='motion';to=@(($x-.4),($y+.5),($z+.4));duration=10}|Out-Null
        Send $owner @{op='pose';right=@(($x+.3),($y+1.3),($z+.25))}|Out-Null
        if((State $owner).automation.motionActive){throw 'A new pose did not cancel the earlier scripted motion'}
        $tests.Add(@{test="$owner pose changes cancel previous trajectories immediately";passed=$true})
        Send $owner @{op='motion';to=@(($x-.4),($y+.5),($z+.4));duration=10}|Out-Null
        $before=State $owner;$old=(Own $before).id
        Send $owner @{op='respawn'}|Out-Null
        Wait-For {
            $s=State $owner;$b=Own $s;$a=$s.automation
            $s.creature -and $b -and !$b.dead -and $b.id -ne $old -and $a.resets -eq ($before.automation.resets+1) -and !$a.motionActive -and
                [Math]::Abs($a.head[1]-1.75) -lt .001 -and [Math]::Abs($a.left[0]+.3) -lt .001 -and [Math]::Abs($a.right[0]-.3) -lt .001 -and
                [Math]::Abs($a.left[1]-1.25) -lt .001 -and [Math]::Abs($a.right[1]-1.25) -lt .001 -and !$s.bodyGrips.leftPressed -and !$s.bodyGrips.rightPressed
        } "$owner new native body restores neutral tracking and cancels old motion/input"
        $id=(Own (State $owner)).id
        Wait-For {
            $s=State $observer;$b=@($s.entities|Where-Object id -eq $id)[0]
            $b -and $b.initialPoseSnapped -and @($s.entities|Where-Object id -eq $old).Count -eq 0
        } "$owner fresh replica snaps its initial snapshot and removes the old body"
        Send $observer @{op='snap-replicas'}|Out-Null
    }
    $passed=$true
}catch{$tests.Add(@{passed=$false;error=$_.ToString()});throw}
finally{@{time=[DateTime]::UtcNow.ToString('o');passed=$passed;tests=$tests}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\round-reset-report.json')}
