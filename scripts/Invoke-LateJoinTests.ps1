param([switch]$TestOwnerDeparture)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$results=[System.Collections.Generic.List[object]]::new()
function Send([string]$Instance,[hashtable]$Command) { & (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Instance -Json ($Command|ConvertTo-Json -Compress -Depth 12) }
function State([string]$Instance) { Send $Instance @{op='status'} }
function Wait-For([scriptblock]$Condition,[string]$Test,[int]$Timeout=25) {
    $deadline=[DateTime]::UtcNow.AddSeconds($Timeout)
    while([DateTime]::UtcNow -lt $deadline) {
        if (& $Condition) {$results.Add(@{test=$Test;passed=$true});Write-Output "PASS: $Test";return}
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out: $Test"
}
$started=$false
try {
    $hostState=State 'host';$clientState=State 'client'
    if($hostState.peers -ne 2 -or $clientState.peers -ne 2){throw 'Start a connected two-player Arena session first.'}
    $owner=$clientState.peer
    if(!$clientState.rightItem){Send 'client' @{op='equip';item='SwordShortCommon'}|Out-Null}
    $moved=@($clientState.root[0],$clientState.root[1],($clientState.root[2]+3))
    Send 'client' @{op='teleport';position=$moved;yaw=0}|Out-Null
    Start-Sleep -Milliseconds 700
    $slice=Send 'client' @{op='slice'}
    if(!$slice.sliced){throw 'The native late-join slice fixture failed.'}
    $neckSlice=Send 'client' @{op='slice';part=2}
    if(!$neckSlice.sliced){throw 'The native late-join neck-redirect fixture failed.'}
    Wait-For {
        $s=State 'client';$b=@($s.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $owner})[0]
        $b.sliced -contains 32 -and $b.sliced -contains 2 -and $b.sliced -contains 1
    } 'Native slice fixture includes hand, head and auxiliary neck state'
    $clientState=State 'client';$source=@($clientState.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $owner})[0]
    if(!$source.rightHeld){throw 'The moving player must retain a native weapon for the late-join test.'}
    & (Join-Path $PSScriptRoot 'Start-TestInstance.ps1') -Name third
    $started=$true
    $thirdRoot=Join-Path $workspace 'artifacts\runs\third'
    Wait-For {
        try {
            $record=Get-Content -LiteralPath (Join-Path $thirdRoot 'process.json') -Raw|ConvertFrom-Json
            $status=Get-Content -LiteralPath (Join-Path $thirdRoot 'status.json') -Raw|ConvertFrom-Json
            $status.processId -eq $record.pid -and $status.creature
        }catch{$false}
    } 'Third native game process reaches the physical-player ready state' 90
    Send 'third' @{op='join';name='LateJoin';latency=60;loss=5}|Out-Null
    Wait-For {
        (State 'host').peers -eq 3 -and (State 'client').peers -eq 3 -and (State 'third').peers -eq 3
    } 'Three actual game processes join the same session'
    Wait-For {
        foreach($name in @('host','client','third')) {
            $s=State $name
            if(@($s.entities|Where-Object kind -eq 'Player').Count -ne 3 -or $s.networkControllers -ne 4){return $false}
        }
        return $true
    } 'Every peer has two native remote fighters and four network hand controllers'
    Wait-For {
        $newcomer=State 'third';$existing=State 'client'
        $a=@($existing.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $owner})[0]
        $b=@($newcomer.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $owner})[0]
        $b.rightHeld -eq $a.rightHeld -and $b.sliced -contains 32 -and (($a.sliced|Sort-Object) -join ',') -eq (($b.sliced|Sort-Object) -join ',') -and [Math]::Abs($b.health-$a.health) -lt .01 -and [Math]::Abs($b.position[2]-$a.position[2]) -lt .15
    } 'Late joining reconstructs moved-player pose, current equipment, health and native dismemberment'
    Wait-For {
        $a=State 'host';$b=State 'third'
        $idsA=@($a.entities|Where-Object kind -eq 'Item'|ForEach-Object id|Sort-Object)
        $idsB=@($b.entities|Where-Object kind -eq 'Item'|ForEach-Object id|Sort-Object)
        $a.unregisteredItems -eq 0 -and $b.unregisteredItems -eq 0 -and ($idsA -join ',') -eq ($idsB -join ',')
    } 'The late joiner receives matching world-object identities without duplicate props'
    if($TestOwnerDeparture){
        $s=State 'host'
        $fragments=@($s.entities|Where-Object {$_.owner -eq $owner -and $_.sceneKey -like 'break:*'}|ForEach-Object id)
        if($fragments.Count -eq 0){throw 'Run native breakable tests before the owner-departure scenario.'}
        Send 'client' @{op='disconnect'}|Out-Null
        Wait-For {
            foreach($name in @('host','third')){
                $s=State $name
                if($s.peers -ne 2 -or $s.unregisteredItems -ne 0){return $false}
                foreach($id in $fragments){
                    $piece=@($s.entities|Where-Object id -eq $id)[0]
                    if(!$piece -or $piece.owner -ne 1 -or $piece.epoch -ne 2){return $false}
                }
            }
            return $true
        } 'Departing owner hands shared fragments to the host without changing their identities'
        Send 'client' @{op='join';name='ClientReturn';latency=60;loss=5}|Out-Null
        Wait-For {
            $a=State 'host';$b=State 'client'
            if($a.peers -ne 3 -or $b.peers -ne 3 -or $b.peer -eq $owner -or $b.unregisteredItems -ne 0){return $false}
            foreach($id in $fragments){
                $piece=@($b.entities|Where-Object id -eq $id)[0]
                $canonical=@($a.entities|Where-Object id -eq $id)[0]
                # A piece still in the returning native hand can legitimately be
                # reclaimed. Its identity, owner and epoch must agree with the host.
                if(!$piece -or !$canonical -or $piece.owner -ne $canonical.owner -or $piece.epoch -ne $canonical.epoch){return $false}
            }
            $idsA=@($a.entities|Where-Object kind -eq 'Item'|ForEach-Object id|Sort-Object)
            $idsB=@($b.entities|Where-Object kind -eq 'Item'|ForEach-Object id|Sort-Object)
            ($idsA -join ',') -eq ($idsB -join ',')
        } 'Returning player reuses the broken world without duplicate fragment sets'
    }
    $states=@{}
    foreach($name in @('host','client','third')){$states[$name]=State $name}
    $states|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\late-join-states.json')
}catch{$results.Add(@{test='Run completion';passed=$false;error=$_.ToString()});throw}
finally {
    if($started){
        try{
            $diagnostics=@{};foreach($name in @('host','client','third')){$diagnostics[$name]=State $name}
            $diagnostics|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\late-join-states.json')
        }catch{}
    }
    @{time=[DateTime]::UtcNow.ToString('o');tests=$results}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\late-join-report.json')
    if($started){
        try{Send 'third' @{op='quit'}|Out-Null}catch{}
        Start-Sleep -Milliseconds 500
        & (Join-Path $PSScriptRoot 'Stop-TestInstances.ps1') -Names third
    }
}
