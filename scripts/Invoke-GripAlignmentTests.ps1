param([ValidateSet('host','client')][string]$Owner='host',[switch]$MeasureOnly)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$observer=if($Owner -eq 'host'){'client'}else{'host'}
$tests=[System.Collections.Generic.List[object]]::new();$passed=$false
function Send($Name,$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 9 -Compress)}
function State($Name){Send $Name @{op='status'}}
try{
    foreach($name in @('host','client')){foreach($side in @('left','right')){Send $name @{op='release';side=$side}|Out-Null};Send $name @{op='clear-loose-items'}|Out-Null}
    Send $Owner @{op='teleport';position=@(0,.08,0);yaw=0}|Out-Null
    Send $observer @{op='teleport';position=@(0,.08,4);yaw=180}|Out-Null
    $peer=(State $Owner).peer
    foreach($weapon in @('SwordShortCommon','SwordGreatCommon')){
        foreach($side in @('right','left')){
            Send $Owner @{op='pose';head=@(0,1.8,0);headRotation=@(0,0,0);left=@(-.3,1.35,.3);right=@(.3,1.35,.3);leftRotation=@(0,0,0);rightRotation=@(0,0,0)}|Out-Null
            Send $Owner @{op='equip';item=$weapon;side=$side}|Out-Null
            foreach($rotation in @(@(0,0,0),@(-55,0,0),@(0,0,90),@(15,50,-25))){
                $pose=@{op='pose'};$pose[$side+'Rotation']=$rotation;Send $Owner $pose|Out-Null
                Start-Sleep -Milliseconds 1700
                $samples=@(for($i=0;$i -lt 4;$i++){
                    $a=State $Owner;$b=State $observer
                    $local=@($a.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $peer})[0]
                    $remote=@($b.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $peer})[0]
                    $field=$side+'Drive';$held=$side+'Held'
                    if(!$local.$held -or $local.$held -ne $remote.$held){throw "$weapon $side native grip was not retained by both peers"}
                    @{local=$local.$field;remote=$remote.$field}
                    Start-Sleep -Milliseconds 100
                })
                $bad=@($samples|Where-Object {$_.remote.gripSeparation -gt .02 -or $_.remote.gripAngle -gt 8 -or $_.remote.gripSeparation -gt ($_.local.gripSeparation+.02)})
                & (Join-Path $PSScriptRoot 'Assert-TestGripCollisions.ps1') -Owner $Owner -Observer $observer -Entity $local.id|Out-Null
                $tests.Add(@{weapon=$weapon;side=$side;rotation=$rotation;samples=$samples;passed=$bad.Count -eq 0})
                Write-Output "$weapon $side rotation $rotation : local=$([Math]::Round($samples[-1].local.gripSeparation,3))m/$([Math]::Round($samples[-1].local.gripAngle,1))deg remote=$([Math]::Round($samples[-1].remote.gripSeparation,3))m/$([Math]::Round($samples[-1].remote.gripAngle,1))deg"
                if($bad.Count -and !$MeasureOnly){throw 'Remote weapon grip is visibly displaced from the physical hand'}
            }
            Send $Owner @{op='release';side=$side}|Out-Null
            Send $Owner @{op='clear-loose-items'}|Out-Null
        }
    }
    $passed=@($tests|Where-Object {!$_.passed}).Count -eq 0
}finally{
    @{time=[DateTime]::UtcNow.ToString('o');passed=$passed;owner=$Owner;tests=$tests}|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $workspace "artifacts\grip-alignment-$Owner-report.json")
}
