param([ValidateSet('host','client')][string]$Taker='client')
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$giver=if($Taker -eq 'host'){'client'}else{'host'}
function Send([string]$Instance,[hashtable]$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Instance -Json ($Command|ConvertTo-Json -Compress -Depth 12)}
function State([string]$Instance){Send $Instance @{op='status'}}
function Wait-For([scriptblock]$Condition,[string]$Reason){
    $deadline=[DateTime]::UtcNow.AddSeconds(12)
    while([DateTime]::UtcNow -lt $deadline){if(&$Condition){return};Start-Sleep -Milliseconds 100}
    throw "Timed out: $Reason"
}
$report=@{time=[DateTime]::UtcNow.ToString('o');taker=$Taker;passed=$false}
try{
    foreach($name in @('host','client')){Wait-For {(State $name).creature} "$name native living body";Send $name @{op='release'}|Out-Null;Send $name @{op='clear-loose-items'}|Out-Null}
    $g=State $giver;$t=State $Taker
    if(!$g.connected -or $g.peers -ne 2){throw 'Start a connected arena pair first.'}
    $p=$g.root;$x=[double]$p[0];$y=[double]$p[1];$z=[double]$p[2]
    Send $giver @{op='teleport';position=@($x,$y,$z);yaw=0}|Out-Null
    Send $giver @{op='pose';head=@($x,($y+1.75),$z);headRotation=@(0,0,0);left=@(($x-.25),($y+1.2),($z+.2));right=@(($x+.4),($y+1.3),($z+.3));rightRotation=@(0,0,0)}|Out-Null
    Send $Taker @{op='teleport';position=@(($x+.8),$y,$z);yaw=0}|Out-Null
    Send $Taker @{op='pose';head=@(($x+.8),($y+1.75),$z);headRotation=@(0,0,0);left=@(($x+1.1),($y+1.2),($z+.2));right=@(($x+.4),($y+1.3),($z+.3));rightRotation=@(0,0,0)}|Out-Null
    Start-Sleep -Milliseconds 500
    Send $giver @{op='equip';item='SwordShortCommon'}|Out-Null
    Start-Sleep -Milliseconds 500
    $s=State $giver;$oldActor=@($s.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $g.peer})[0].id
    $weapon=@($s.entities|Where-Object {$_.held -gt 0 -and $_.catalog -eq 'SwordShortCommon' -and $_.owner -eq $g.peer})[0]
    if(!$weapon){throw 'Source native grip did not remain established.'}
    $id=$weapon.id
    Wait-For {@((State $Taker).entities|Where-Object id -eq $id).Count -eq 1} 'replicated source weapon'
    # No release command: the native single-hand handle must free its previous holder.
    Send $Taker @{op='grab-entity';entity=$id}|Out-Null
    Wait-For {
        $a=State $giver;$b=State $Taker
        $wa=@($a.entities|Where-Object id -eq $id)[0];$wb=@($b.entities|Where-Object id -eq $id)[0]
        $wa.owner -eq $t.peer -and $wb.owner -eq $t.peer -and $wa.epoch -eq ($weapon.epoch+1) -and $wb.epoch -eq $wa.epoch -and
        $wa.held -eq 1 -and $wb.held -eq 1 -and !$a.rightItem -and $b.rightItem -eq 'SwordShortCommon' -and
        @($a.cachedGrips|Where-Object {$_.entity -eq $oldActor -and $_.other -eq $id}).Count -eq 0 -and
        @($b.cachedGrips|Where-Object {$_.entity -eq $oldActor -and $_.other -eq $id}).Count -eq 0
    } 'native contested pickup, ownership transfer and old-grip removal on both peers'
    $report.giver=State $giver;$report.receiver=State $Taker
    Send $Taker @{op='release'}|Out-Null
    Wait-For {
        $a=State $giver;$b=State $Taker
        @($a.entities|Where-Object id -eq $id)[0].held -eq 0 -and @($b.entities|Where-Object id -eq $id)[0].held -eq 0 -and
        @($a.cachedGrips|Where-Object other -eq $id).Count -eq 0 -and @($b.cachedGrips|Where-Object other -eq $id).Count -eq 0
    } 'new owner releases the taken weapon without a stale grip returning'
    $report.passed=$true
    Write-Output "PASS: $Taker takes the held native weapon, gains ownership once, and releases it cleanly on both peers"
}catch{$report.error=$_.ToString();throw}
finally{$report|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $workspace "artifacts\contested-grab-$Taker-report.json")}
