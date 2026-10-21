param()
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$results=[System.Collections.Generic.List[object]]::new()
function Send([string]$Instance,[hashtable]$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Instance -Json ($Command|ConvertTo-Json -Compress -Depth 12)}
function State([string]$Instance){Send $Instance @{op='status'}}
function Wait-For([scriptblock]$Condition,[string]$Reason){
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    while([DateTime]::UtcNow -lt $deadline){if(&$Condition){$results.Add(@{test=$Reason;passed=$true});Write-Output "PASS: $Reason";return};Start-Sleep -Milliseconds 150}
    throw "Timed out: $Reason"
}
try{
    $h=State 'host'
    if(!$h.connected -or $h.peers -ne 2){throw 'Start a connected arena pair first.'}
    $barrel=@($h.entities|Where-Object {$_.catalog -eq 'Barrel2' -and $_.breakable -and !$_.breakable.broken})[0]
    if(!$barrel){throw 'No intact native arena barrel remains.'}
    $attempt=Send 'client' @{op='break-item';entity=$barrel.id}
    if($attempt.broken){throw 'A non-owner independently broke the shared scene prop.'}
    $results.Add(@{test='A replica cannot independently create a second set of break fragments';passed=$true})
    foreach($role in @('host','client')){
        if($role -eq 'host'){$root=$barrel}
        else{
            Send 'client' @{op='equip';item='Pottery_02'}|Out-Null
            Wait-For {
                $s=State 'client'
                @($s.entities|Where-Object {$_.catalog -eq 'Pottery_02' -and $_.owner -eq $s.peer -and $_.held -gt 0}).Count -eq 1
            } 'Client owns the native held pottery fixture'
            $s=State 'client';$root=@($s.entities|Where-Object {$_.catalog -eq 'Pottery_02' -and $_.owner -eq $s.peer -and $_.held -gt 0})[0]
            Wait-For {@((State 'host').entities|Where-Object id -eq $root.id).Count -eq 1} 'Client breakable has finished spawning on the host'
        }
        $broken=Send $role @{op='break-item';entity=$root.id}
        if(!$broken.broken){throw "$role native Break did not execute."}
        $prefix='break:'+$root.id+':piece:'
        Wait-For {
            $a=State 'host';$b=State 'client'
            $pa=@($a.entities|Where-Object {$_.sceneKey -like ($prefix+'*')})
            $pb=@($b.entities|Where-Object {$_.sceneKey -like ($prefix+'*')})
            $pa.Count -eq $root.breakable.pieces -and $pb.Count -eq $pa.Count -and
            (($pa|ForEach-Object id|Sort-Object) -join ',') -eq (($pb|ForEach-Object id|Sort-Object) -join ',') -and
            @($pa.sceneKey|Select-Object -Unique).Count -eq $pa.Count -and $a.unregisteredItems -eq 0 -and $b.unregisteredItems -eq 0
        } "$role native break yields matching unique fragment identities without orphan props"
        Wait-For {
            $a=State 'host';$b=State 'client'
            foreach($piece in @($a.entities|Where-Object {$_.sceneKey -like ($prefix+'*')})){
                $other=@($b.entities|Where-Object id -eq $piece.id)[0]
                if(!$other){return $false}
                $distance=[Math]::Sqrt([Math]::Pow($piece.position[0]-$other.position[0],2)+[Math]::Pow($piece.position[1]-$other.position[1],2)+[Math]::Pow($piece.position[2]-$other.position[2],2))
                if($distance -gt .2){return $false}
            }
            return $true
        } "$role native fragment positions converge"
        if($role -eq 'host'){
            $s=State 'host';$piece=@($s.entities|Where-Object {$_.sceneKey -like ($prefix+'*')})[0]
            Send 'host' @{op='despawn-item';entity=$piece.id}|Out-Null
            Wait-For {
                $a=State 'host';$b=State 'client'
                @($a.entities|Where-Object id -eq $piece.id).Count -eq 0 -and @($b.entities|Where-Object id -eq $piece.id).Count -eq 0 -and $a.unregisteredItems -eq 0 -and $b.unregisteredItems -eq 0
            } 'Authoritative fragment deletion removes the matched native copy on both peers'
        }
    }
}catch{$results.Add(@{test='Run completion';passed=$false;error=$_.ToString()});throw}
finally{
    $states=@{};foreach($name in @('host','client')){try{$states[$name]=State $name}catch{}}
    @{time=[DateTime]::UtcNow.ToString('o');tests=$results;states=$states}|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\breakable-report.json')
}
