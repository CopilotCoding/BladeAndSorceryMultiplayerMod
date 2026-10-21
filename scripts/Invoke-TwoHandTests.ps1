param([ValidateSet('host','client')][string]$Owner='host')
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$observer=if($Owner -eq 'host'){'client'}else{'host'}
$results=[System.Collections.Generic.List[object]]::new()
function Send([string]$Instance,[hashtable]$Command) { & (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Instance -Json ($Command|ConvertTo-Json -Compress -Depth 12) }
function State([string]$Instance) { Send $Instance @{op='status'} }
function Wait-For([scriptblock]$Condition,[string]$Test) {
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    while([DateTime]::UtcNow -lt $deadline) {
        if (& $Condition) {$results.Add(@{test=$Test;passed=$true});Write-Output "PASS: $Test";return}
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out: $Test"
}
function Drives-Match($A,$B) {
    if($null -eq $A -or $null -eq $B){return $false}
    foreach($field in @('positionSpring','positionDamper','positionMax','slerpSpring','xSpring','yzSpring')) {if([Math]::Abs($A.$field-$B.$field) -gt .01){return $false}}
    return $true
}
try {
    Send $Owner @{op='release';side='left'}|Out-Null
    Send $Owner @{op='release';side='right'}|Out-Null
    Send $Owner @{op='clear-loose-items'}|Out-Null
    $before=State $Owner;$peer=$before.peer;$p=$before.root;$x=[double]$p[0];$y=[double]$p[1];$z=[double]$p[2]
    Send $Owner @{op='teleport';position=@($x,$y,$z);yaw=0}|Out-Null
    Send $Owner @{op='pose';head=@($x,($y+1.75),$z);headRotation=@(0,0,0);right=@(($x+.15),($y+1.35),($z+.4));rightRotation=@(0,0,0);left=@(($x+.15),($y+1.35),($z+.2));leftRotation=@(0,0,0)}|Out-Null
    Start-Sleep -Milliseconds 400
    Send $Owner @{op='equip';item='SwordGreatCommon'}|Out-Null
    Start-Sleep -Milliseconds 400
    $s=State $Owner;$item=@($s.entities|Where-Object {$_.catalog -eq 'SwordGreatCommon' -and $_.held -gt 0 -and $_.owner -eq $peer})[0]
    if(!$item){throw 'Native greatsword grip failed.'}
    $id=$item.id;$handle=$item.handles[0];$axis=-[double]$handle.axisLength*.4
    $left=@(($handle.position[0]+$handle.up[0]*$axis),($handle.position[1]+$handle.up[1]*$axis),($handle.position[2]+$handle.up[2]*$axis))
    Send $Owner @{op='pose';left=$left;leftRotation=@(0,0,0)}|Out-Null
    Start-Sleep -Milliseconds 200
    Send $Owner @{op='grab-entity';entity=$id;side='left';axis=$axis}|Out-Null
    Wait-For {
        $local=State $Owner;$remote=State $observer
        $a=@($local.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $peer})[0]
        $b=@($remote.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $peer})[0]
        $a.leftHeld -eq $id -and $a.rightHeld -eq $id -and $b.leftHeld -eq $id -and $b.rightHeld -eq $id -and (Drives-Match $a.leftDrive $b.leftDrive) -and (Drives-Match $a.rightDrive $b.rightDrive)
    } "$Owner two-handed grip establishes both native joints with matching drive settings"
    Send $Owner @{op='release';side='left'}|Out-Null
    Wait-For {
        $local=State $Owner;$remote=State $observer
        $a=@($local.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $peer})[0]
        $b=@($remote.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $peer})[0]
        !$a.leftHeld -and !$b.leftHeld -and $a.rightHeld -eq $id -and $b.rightHeld -eq $id -and (Drives-Match $a.rightDrive $b.rightDrive)
    } "$Owner off-hand release restores the native single-hand grip on both peers"
}catch{$results.Add(@{test='Run completion';passed=$false;error=$_.ToString()});throw}
finally{@{time=[DateTime]::UtcNow.ToString('o');tests=$results}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $workspace "artifacts\two-hand-$Owner-report.json")}
