param([ValidateSet('host','client')][string]$Attacker='host',[switch]$Slow,[string]$Weapon='SwordShortCommon',[switch]$TwoHanded,[double]$ThrustSeconds=0,[double]$MinimumContactSpeed=0)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$defender=if($Attacker -eq 'host'){'client'}else{'host'}
function Send([string]$Instance,[hashtable]$Command) { & (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Instance -Json ($Command|ConvertTo-Json -Compress -Depth 12) }
function State([string]$Instance) { Send $Instance @{op='status'} }
$report=@{time=[DateTime]::UtcNow.ToString('o');attacker=$Attacker;weapon=$Weapon;passed=$false}
try {
    foreach($name in @('host','client')){Send $name @{op='release'}|Out-Null;Send $name @{op='clear-loose-items'}|Out-Null}
    $source=State $Attacker;$p=$source.root;$x=[double]$p[0];$y=[double]$p[1];$z=[double]$p[2]
    $victim=State $defender
    $victimId=@($victim.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $victim.peer -and !$_.dead})[0].id
    if(!$victimId){throw 'The stabbing fixture requires a living victim.'}
    $report.victim=$victimId
    Send $Attacker @{op='teleport';position=@($x,$y,$z);yaw=0}|Out-Null
    Send $defender @{op='teleport';position=@($x,$y,($z+4));yaw=180}|Out-Null
    Send $Attacker @{op='pose';head=@($x,($y+1.75),$z);headRotation=@(0,0,0);left=@(($x-.3),($y+1.2),($z+.2));right=@($x,($y+1.4),($z+.05));rightRotation=@(0,0,0)}|Out-Null
    Start-Sleep -Milliseconds 500
    Send $Attacker @{op='equip';item=$Weapon}|Out-Null
    Start-Sleep -Milliseconds 500
    $state=State $Attacker;$heldWeapon=@($state.entities|Where-Object {$_.catalog -eq $Weapon -and $_.held -gt 0 -and $_.owner -eq $source.peer})[0]
    if(!$heldWeapon){throw 'The native stabbing fixture did not establish its source grip.'}
    $tip=@($heldWeapon.damagers|Where-Object {$_.type -eq 'Pierce' -and $_.forward[2] -gt .9}|Sort-Object {$_.position[2]} -Descending)[0]
    if(!$tip){throw "No native forward-facing piercing tip on $Weapon"}
    $id=$heldWeapon.id
    $distance=.6+[double]$tip.position[2]-[double]$state.right[2]+$z
    $handHeight=[double]$state.right[1]+($y+1.4)-[double]$tip.position[1]
    Send $Attacker @{op='pose';right=@($x,$handHeight,($z+.05));rightRotation=@(0,0,0)}|Out-Null
    Send $defender @{op='teleport';position=@($x,$y,$distance);yaw=180}|Out-Null
    Send $defender @{op='pose';head=@($x,($y+1.75),$distance);headRotation=@(0,180,0);left=@(($x+.3),($y+1.2),$distance);right=@(($x-.3),($y+1.2),$distance)}|Out-Null
    Start-Sleep -Milliseconds 650
    if($TwoHanded){
        $s=State $Attacker;$held=@($s.entities|Where-Object id -eq $id)[0];$handle=$held.handles[0];$axis=-[double]$handle.axisLength*.4
        $left=@(($handle.position[0]+$handle.up[0]*$axis),($handle.position[1]+$handle.up[1]*$axis),($handle.position[2]+$handle.up[2]*$axis))
        Send $Attacker @{op='pose';left=$left;leftRotation=@(0,0,0)}|Out-Null
        Start-Sleep -Milliseconds 250
        Send $Attacker @{op='grab-entity';entity=$id;side='left';axis=$axis}|Out-Null
        Start-Sleep -Milliseconds 900
        foreach($name in @($Attacker,$defender)){
            $s=State $name;$body=@($s.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $source.peer})[0]
            if($body.leftHeld -ne $id -or $body.rightHeld -ne $id){throw 'Two-handed thrust preparation did not retain both native grips'}
        }
    }
    $report.preparation=State $Attacker;$report.tip=$tip;$report.targetDistance=$distance
    foreach($name in @('host','client')){Send $name @{op='combat-trace';clear=$true}|Out-Null}
    for($attempt=0;$attempt -lt 3;$attempt++){
        $duration=if($ThrustSeconds -gt 0){$ThrustSeconds}elseif($Slow){1.5}else{.12}
        Send $Attacker @{op='motion';to=@($x,$handHeight,($z+.65));duration=$duration;rotation=@(0,0,0);both=[bool]$TwoHanded}|Out-Null
        $deadline=[DateTime]::UtcNow.AddSeconds($duration+1)
        while([DateTime]::UtcNow -lt $deadline){
            $a=State $Attacker;$b=State $defender
            $aw=@($a.entities|Where-Object id -eq $id)[0];$bw=@($b.entities|Where-Object id -eq $id)[0]
            if(@($aw.penetrations|Where-Object target -eq $victimId).Count -gt 0 -and @($bw.penetrations|Where-Object target -eq $victimId).Count -gt 0){$report.penetratingOnBoth=$true;break}
            Start-Sleep -Milliseconds 80
        }
        if($report.penetratingOnBoth){break}
        Send $Attacker @{op='motion';to=@($x,$handHeight,($z+.05));duration=.3;rotation=@(0,0,0);both=[bool]$TwoHanded}|Out-Null
        Start-Sleep -Milliseconds 400
    }
    $report.attacking=State $Attacker;$report.defending=State $defender
    if(!$report.penetratingOnBoth){throw 'The physical thrust did not produce a persistent native penetration into the intended opponent on both peers.'}
    $sourceTrace=@(Send $Attacker @{op='combat-trace'}|Where-Object {$_.victim -eq $victimId -and $_.damager -eq $tip.id -and $_.phase -ne 'pressure'})
    $targetTrace=@(Send $defender @{op='combat-trace'}|Where-Object {$_.victim -eq $victimId -and $_.damager -eq $tip.id -and $_.phase -ne 'pressure'})
    $report.sourceTrace=$sourceTrace;$report.targetTrace=$targetTrace
    $report.sourcePeak=($sourceTrace|Measure-Object speed -Maximum).Maximum;$report.targetPeak=($targetTrace|Measure-Object speed -Maximum).Maximum
    if($MinimumContactSpeed -gt 0 -and ($report.sourcePeak -lt $MinimumContactSpeed -or $report.targetPeak -lt $MinimumContactSpeed)){throw "The thrust did not reach $MinimumContactSpeed m/s at contact on both peers"}
    if($report.attacking.rightItem -ne $Weapon){throw 'The native source grip broke while establishing the penetration.'}
    Send $Attacker @{op='motion';to=@($x,$handHeight,($z-.1));duration=.4;rotation=@(0,0,0);both=[bool]$TwoHanded}|Out-Null
    $deadline=[DateTime]::UtcNow.AddSeconds(4)
    while([DateTime]::UtcNow -lt $deadline){
        $a=@((State $Attacker).entities|Where-Object id -eq $id)[0];$b=@((State $defender).entities|Where-Object id -eq $id)[0]
        if(!$a.penetrating -and !$b.penetrating){$report.releasedOnBoth=$true;break}
        Start-Sleep -Milliseconds 100
    }
    if(!$report.releasedOnBoth){throw 'Pulling the blade out did not remove the native penetration on both peers.'}
    if((State $Attacker).rightItem -ne $Weapon){throw 'The native source grip broke during blade withdrawal.'}
    $report.passed=$true
    Write-Output "PASS: $Attacker $Weapon physical thrust creates and releases a native penetration on both peers"
}catch{$report.error=$_.ToString();throw}
finally{
    try{$report.finalAttacking=State $Attacker;$report.finalDefending=State $defender}catch{}
    $suffix=if($Slow){'-slow'}else{''}
    if($Weapon -ne 'SwordShortCommon'){$suffix="-$Weapon$suffix"}
    if($TwoHanded){$suffix+='-twohand'}
    if($ThrustSeconds -gt 0){$suffix+="-$ThrustSeconds"}
    $report|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $workspace "artifacts\stab-$Attacker$suffix-report.json")
}
