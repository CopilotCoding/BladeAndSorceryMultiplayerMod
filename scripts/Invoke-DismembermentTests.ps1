param([ValidateSet('host','client')][string]$Owner='host')
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$observer=if($Owner -eq 'host'){'client'}else{'host'}
$tests=[System.Collections.Generic.List[object]]::new();$passed=$false
function Send($Name,$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 9 -Compress)}
function State($Name){Send $Name @{op='status'}}
function Own($State){@($State.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $State.peer})[0]}
function Wait-For($Condition,$Reason){$deadline=[DateTime]::UtcNow.AddSeconds(30);do{if(&$Condition){return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline);throw "Timed out: $Reason"}
try{
    foreach($name in @('host','client')){foreach($side in @('left','right')){Send $name @{op='release';side=$side}|Out-Null};Send $name @{op='clear-loose-items'}|Out-Null}
    $parts=@((Own (State $Owner)).parts|Where-Object sliceAllowed)
    if(!$parts.Count){throw 'No native sliceable parts were found'}
    foreach($part in $parts){
        $old=(Own (State $Owner)).id;Send $Owner @{op='respawn'}|Out-Null
        Wait-For {$s=State $Owner;$s.creature -and (Own $s).id -ne $old} 'Fresh native body'
        $body=Own (State $Owner);$id=$body.id
        Wait-For {@((State $observer).entities|Where-Object id -eq $id).Count -eq 1} 'Intact replica'
        $result=Send $Owner @{op='slice';partKey=$part.key}
        if(!$result.sliced){throw "Native TrySlice refused $($part.type), key $($part.key)"}
        $expected=if($null -ne $part.sliceChild){$part.sliceChild}else{$part.typeValue}
        Wait-For {
            $a=@((State $Owner).entities|Where-Object id -eq $id)[0]
            $b=@((State $observer).entities|Where-Object id -eq $id)[0]
            $a -and $b -and $expected -in $a.sliced -and $expected -in $b.sliced -and $a.sliced.Count -eq $b.sliced.Count
        } "Matching native cut on $($part.type), key $($part.key)"
        $tests.Add(@{part=$part.type;key=$part.key;expected=$expected;passed=$true})
        Write-Output "PASS: $Owner native cut replicates $($part.type) key $($part.key) and its redirected parts"
    }
    $passed=$true
}catch{$tests.Add(@{passed=$false;error=$_.ToString()});throw}
finally{
    @{time=[DateTime]::UtcNow.ToString('o');passed=$passed;owner=$Owner;method='Native TrySlice API, not a physical sword trajectory';tests=$tests}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $workspace "artifacts\dismemberment-$Owner-report.json")
}
