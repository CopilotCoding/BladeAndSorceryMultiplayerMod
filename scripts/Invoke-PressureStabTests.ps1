param([ValidateSet('host','client')][string]$Attacker='host',[string]$Weapon='SwordShortCommon')
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$defender=if($Attacker -eq 'host'){'client'}else{'host'}
function Send($Name,$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 10 -Compress)}
function State($Name){Send $Name @{op='status'}}
function Own($State){@($State.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $State.peer})[0]}
function Wait-For($Condition,$Reason){$deadline=[DateTime]::UtcNow.AddSeconds(15);do{if(&$Condition){return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline);throw "Timed out: $Reason"}
$report=@{time=[DateTime]::UtcNow.ToString('o');attacker=$Attacker;weapon=$Weapon;passed=$false}
$protection=@{}
try{
    foreach($name in @('host','client')){
        $s=State $name;$protection[$name]=$s.manualInvincibility
        Send $name @{op='invincibility';enabled=$true}|Out-Null
        foreach($side in @('left','right')){Send $name @{op='release';side=$side}|Out-Null}
        Send $name @{op='clear-loose-items'}|Out-Null
        $old=(Own $s).id;Send $name @{op='respawn'}|Out-Null
        Wait-For {$s=State $name;$s.creature -and (Own $s).id -ne $old} "$name fresh native body"
    }
    Send $Attacker @{op='teleport';position=@(0,.08,0);yaw=0}|Out-Null
    Send $defender @{op='teleport';position=@(0,.08,4);yaw=180}|Out-Null
    Send $Attacker @{op='pose';head=@(0,1.83,0);headRotation=@(0,0,0);left=@(-.4,1.2,-.2);right=@(0,1.4,.05);rightRotation=@(0,0,0)}|Out-Null
    Send $Attacker @{op='equip';item=$Weapon}|Out-Null
    Start-Sleep -Milliseconds 700
    $a=State $Attacker;$item=@($a.entities|Where-Object {$_.catalog -eq $Weapon -and $_.held -gt 0 -and $_.owner -eq $a.peer})[0]
    $tip=@($item.damagers|Where-Object {$_.type -eq 'Pierce' -and $_.forward[2] -gt .9}|Sort-Object {$_.position[2]} -Descending)[0]
    if(!$tip){throw "No native forward-facing piercing tip on $Weapon"}
    $id=$item.id;$height=[double]$a.right[1]+1.4-[double]$tip.position[1]
    $distance=[double]$tip.position[2]+.45
    Send $Attacker @{op='pose';right=@(0,$height,.05)}|Out-Null
    Send $defender @{op='teleport';position=@(0,.08,$distance);yaw=180}|Out-Null
    Send $defender @{op='pose';head=@(0,1.83,$distance);headRotation=@(0,180,0);left=@(.3,1.2,$distance);right=@(-.3,1.2,$distance)}|Out-Null
    $victim=(Own (State $defender)).id
    # Wait for actual physical rest on both copies. The previous "slow" thrust
    # fixture could still hit a moving replica and enter ordinary Hit mode.
    $stable=0
    Wait-For {
        $a=State $Attacker;$b=State $defender
        $weapons=@($a.entities+$b.entities|Where-Object id -eq $id)
        $replica=@($a.entities|Where-Object id -eq $victim)[0]
        $moving=@($weapons|Where-Object { [Math]::Sqrt($_.velocity[0]*$_.velocity[0]+$_.velocity[1]*$_.velocity[1]+$_.velocity[2]*$_.velocity[2]) -gt .08 })
        $movingBones=@($replica.parts|Where-Object { [Math]::Sqrt($_.velocity[0]*$_.velocity[0]+$_.velocity[1]*$_.velocity[1]+$_.velocity[2]*$_.velocity[2]) -gt .15 })
        if($weapons.Count -eq 2 -and !$moving.Count -and !$movingBones.Count){$script:stable++}else{$script:stable=0}
        $script:stable -ge 3
    } 'weapon and target replica at physical rest'
    $report.preparation=State $Attacker;$report.target=State $defender
    foreach($name in @('host','client')){Send $name @{op='combat-trace';clear=$true}|Out-Null;Send $name @{op='invincibility';enabled=$false}|Out-Null}
    Send $Attacker @{op='motion';to=@(0,$height,.55);duration=5}|Out-Null
    $deadline=[DateTime]::UtcNow.AddSeconds(8)
    do{
        $a=State $Attacker;$b=State $defender
        $aw=@($a.entities|Where-Object id -eq $id)[0];$bw=@($b.entities|Where-Object id -eq $id)[0]
        $ap=@($aw.penetrations|Where-Object target -eq $victim);$bp=@($bw.penetrations|Where-Object target -eq $victim)
        if($ap.Count -and $bp.Count){$report.modes=@($ap.mode)+@($bp.mode);break}
        Start-Sleep -Milliseconds 100
    }while([DateTime]::UtcNow -lt $deadline)
    $report.attacking=State $Attacker;$report.defending=State $defender
    if(!$report.modes -or @($report.modes|Where-Object {$_ -ne 'Pressure'}).Count){throw 'A settled, slow push did not produce Pressure-mode penetration on both peers'}
    if($report.attacking.rightItem -ne $Weapon){throw 'Pressure entry broke the source grip'}
    Send $Attacker @{op='motion';to=@(0,$height,-.2);duration=.6}|Out-Null
    Wait-For {$a=@((State $Attacker).entities|Where-Object id -eq $id)[0];$b=@((State $defender).entities|Where-Object id -eq $id)[0];!$a.penetrating -and !$b.penetrating} 'native pressure joint withdrawal on both peers'
    $report.passed=$true
    Write-Output "PASS: $Attacker $Weapon settled slow push uses native Pressure-mode penetration and withdrawal on both peers"
}catch{$report.error=$_.ToString();throw}
finally{
    foreach($role in @('source','target')){
        $name=if($role -eq 'source'){$Attacker}else{$defender}
        try{$report[$role+'Trace']=@(Send $name @{op='combat-trace'});if($protection.ContainsKey($name)){Send $name @{op='invincibility';enabled=$protection[$name]}|Out-Null}}catch{}
    }
    $report|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $workspace "artifacts\pressure-$Attacker-$Weapon-report.json")
}
