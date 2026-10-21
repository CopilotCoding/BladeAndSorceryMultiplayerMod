param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$results = [System.Collections.Generic.List[object]]::new()
function Send([string]$Instance, [hashtable]$Command) { & (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Instance -Json ($Command | ConvertTo-Json -Compress -Depth 12) }
function State([string]$Instance) { Send $Instance @{op='status'} }
function Distance($A, $B) { [Math]::Sqrt([Math]::Pow($A[0]-$B[0],2)+[Math]::Pow($A[1]-$B[1],2)+[Math]::Pow($A[2]-$B[2],2)) }
function Wait-For([scriptblock]$Condition, [string]$Test, [int]$Timeout = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $Condition) { $results.Add(@{test=$Test;passed=$true}); Write-Output "PASS: $Test"; return }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out: $Test"
}
try {
    foreach ($name in @('host','client')) { Send $name @{op='release'} | Out-Null }
    foreach ($name in @('host','client')) { Send $name @{op='clear-loose-items'} | Out-Null }
    $h = State 'host'; $c = State 'client'
    if ($h.unregisteredItems -ne 0 -or $c.unregisteredItems -ne 0) { throw 'Native objects exist outside the network registry.' }
    $hostIds = @($h.entities | Where-Object kind -eq 'Item' | ForEach-Object id | Sort-Object)
    $clientIds = @($c.entities | Where-Object kind -eq 'Item' | ForEach-Object id | Sort-Object)
    if (($hostIds -join ',') -ne ($clientIds -join ',')) { throw 'Host and client object identities differ.' }
    $results.Add(@{test='All native arena items use matching network identities without duplicate local props';passed=$true})
    Write-Output 'PASS: All native arena items use matching network identities without duplicate local props'
    $x=[double]$h.root[0];$y=[double]$h.root[1];$z=[double]$h.root[2]
    Send 'host' @{op='teleport';position=@($x,$y,$z);yaw=0} | Out-Null
    Send 'host' @{op='pose';head=@($x,($y+1.75),$z);headRotation=@(0,0,0);right=@(($x+.4),($y+1.3),($z+.3));rightRotation=@(0,0,0)} | Out-Null
    Send 'client' @{op='teleport';position=@(($x+.8),$y,$z);yaw=0} | Out-Null
    Send 'client' @{op='pose';head=@(($x+.8),($y+1.75),$z);headRotation=@(0,0,0);left=@(($x+.55),($y+1.3),($z+.2));right=@(($x+.4),($y+1.3),($z+.3));rightRotation=@(0,0,0)} | Out-Null
    Start-Sleep -Milliseconds 500
    Send 'host' @{op='equip';item='SwordShortCommon'} | Out-Null
    Start-Sleep -Milliseconds 400
    $s=State 'host'; $weapon = @($s.entities | Where-Object { $_.catalog -eq 'SwordShortCommon' -and $_.owner -eq $h.peer -and $_.held -gt 0 })[-1]; $id=$weapon.id
    if (!$id) { throw 'The native source grip did not remain established for the handoff fixture.' }
    Wait-For { @((State 'client').entities | Where-Object id -eq $id).Count -eq 1 } 'Source weapon finishes native spawning on the client'
    Send 'host' @{op='release'} | Out-Null
    # Clear the former holder before changing the weapon's self-collision filter.
    # Grabbing it while its old hand is still around the blade can legitimately
    # pierce that arm and turn the later "throw" into a constrained-body test.
    Send 'host' @{op='teleport';position=@(($x-3),$y,$z);yaw=0} | Out-Null
    Send 'host' @{op='pose';head=@(($x-3),($y+1.75),$z);left=@(($x-3.25),($y+1.15),($z+.2));right=@(($x-2.75),($y+1.3),($z+.45))} | Out-Null
    Send 'client' @{op='grab-entity';entity=$id} | Out-Null
    Wait-For {
        $a=@((State 'host').entities|Where-Object id -eq $id)[0];$b=@((State 'client').entities|Where-Object id -eq $id)[0]
        $a.owner -eq $c.peer -and $b.owner -eq $c.peer -and $a.epoch -eq 2 -and $b.epoch -eq 2 -and $a.held -gt 0 -and $b.held -gt 0
    } 'Client pickup transfers object ownership once and establishes both native grips'
    Start-Sleep -Milliseconds 500
    if(@((State 'client').entities|Where-Object id -eq $id)[0].penetrating){throw 'The throw fixture still has a native penetration constraint.'}
    foreach($name in @('host','client')){Send $name @{op='observe-item';entity=$id}|Out-Null}
    Send 'client' @{op='release';throw=$true} | Out-Null
    Send 'client' @{op='item-velocity';entity=$id;velocity=@(0,2,3)} | Out-Null
    Wait-For {
        $a=State 'host';$b=State 'client'
        $framesA=@($a.itemObservation.frames|Where-Object {$_.held -eq 0 -and !$_.holstered -and $_.velocity[2] -gt 1})
        $framesB=@($b.itemObservation.frames|Where-Object {$_.held -eq 0 -and !$_.holstered -and $_.velocity[2] -gt 1})
        foreach($fa in $framesA){foreach($fb in $framesB){if((Distance $fa.position $fb.position) -lt .7){return $true}}}
        return $false
    } 'Dropped client-owned weapon replicates its position and flight velocity'
    Start-Sleep -Seconds 3
    Wait-For {
        $a=@((State 'host').entities|Where-Object id -eq $id)[0];$b=@((State 'client').entities|Where-Object id -eq $id)[0]
        (Distance $a.position $b.position) -lt .12 -and (Distance $a.velocity $b.velocity) -lt .25
    } 'Released object converges after flight and native collisions'
} catch { $results.Add(@{test='Run completion';passed=$false;error=$_.ToString()}); throw }
finally {
    $states=@{};foreach($name in @('host','client')){try{$states[$name]=State $name}catch{}}
    @{time=[DateTime]::UtcNow.ToString('o');tests=$results;states=$states}|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\object-test-report.json')
}
