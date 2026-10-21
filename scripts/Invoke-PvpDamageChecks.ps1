param([string]$HostName,[string]$ClientName,[int]$Port=19051)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$checks=[Collections.Generic.List[object]]::new()
function Send([string]$Name,[hashtable]$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Compress -Depth 8)}
function State([string]$Name){Send $Name @{op='status'}}
function Check([bool]$Condition,[string]$Message){if(!$Condition){throw $Message};$checks.Add(@{test=$Message;passed=$true});Write-Output "PASS: $Message"}
function Wait-Check([scriptblock]$Condition,[string]$Message,[int]$Seconds=20){$until=[DateTime]::UtcNow.AddSeconds($Seconds);do{if(& $Condition){Check $true $Message;return};Start-Sleep -Milliseconds 150}while([DateTime]::UtcNow -lt $until);throw "Timed out: $Message"}
$passed=$false;$build=$null
try{
    Send $HostName @{op='host';name='Damage check host';port=$Port}|Out-Null
    Send $ClientName @{op='join';name='Damage check client';port=$Port}|Out-Null
    Wait-Check { $h=State $HostName;$c=State $ClientName;$h.peers -eq 2 -and $c.connected -and $c.scene.ready } 'Matching modded peers connect with combat patches active'
    $h=State $HostName;$build=@{mod=$h.modBuild;core=$h.coreBuild;game=$h.game}
    Send $HostName @{op='equip';item='SwordShortCommon'}|Out-Null
    Send $ClientName @{op='equip';item='SwordShortCommon'}|Out-Null
    foreach($rule in 0,1){
        $label=if($rule -eq 0){'NativePlayer'}else{'NpcStyle'}
        Send $HostName @{op='mod-action';name='PvP weapon damage (host)';index=$rule}|Out-Null
        Wait-Check {(State $ClientName).match.pvpWeaponDamage -eq $label} "$label host selection replicates"
        foreach($attacker in @($HostName,$ClientName)){
            $victim=if($attacker -eq $HostName){$ClientName}else{$HostName}
            $before=State $victim
            $contact=Send $attacker @{op='pvp-probe-hit';victimPeer=$before.peer;amount=30}
            $expected=if($rule -eq 0){$contact.nativeExpected}else{$contact.npcExpected}
            Wait-Check {$s=State $victim;[Math]::Abs($s.localHealth-($before.localHealth-$expected)) -lt .02} "$label constructed weapon damage reaches $victim exactly once"
            $after=State $victim
            Start-Sleep -Milliseconds 250
            Check ([Math]::Abs((State $victim).localHealth-$after.localHealth) -lt .02) "$label mirrored contact causes no second damage"
        }
    }
    Send $ClientName @{op='mod-action';name='PvP weapon damage (host)';index=0}|Out-Null
    Check ((State $HostName).match.pvpWeaponDamage -eq 'NpcStyle' -and (State $ClientName).match.pvpWeaponDamage -eq 'NpcStyle') 'Client cannot override the host damage rule'
    Send $ClientName @{op='disconnect'}|Out-Null
    Send $ClientName @{op='join';name='Damage check client';port=$Port}|Out-Null
    Wait-Check {$s=State $ClientName;$s.connected -and $s.scene.ready -and $s.match.pvpWeaponDamage -eq 'NpcStyle'} 'Late joining receives the selected damage rule'
    Send $HostName @{op='mode';mode='Coop'}|Out-Null
    $before=State $ClientName
    Send $HostName @{op='pvp-probe-hit';victimPeer=$before.peer;amount=30}|Out-Null
    Start-Sleep -Milliseconds 500
    Check ([Math]::Abs((State $ClientName).localHealth-$before.localHealth) -lt .02) 'NPC-style damage preserves co-op friendly-fire protection'
    Send $HostName @{op='mode';mode='FreeForAll'}|Out-Null
    Send $ClientName @{op='invincibility';enabled=$true}|Out-Null
    $before=State $ClientName
    Send $HostName @{op='pvp-probe-hit';victimPeer=$before.peer;amount=30}|Out-Null
    Start-Sleep -Milliseconds 500
    Check ([Math]::Abs((State $ClientName).localHealth-$before.localHealth) -lt .02) 'NPC-style damage preserves invincibility'
    Send $ClientName @{op='invincibility';enabled=$false}|Out-Null
    Send $HostName @{op='mod-action';name='Survive limb loss (host)';index=1}|Out-Null
    Wait-Check {(State $ClientName).match.surviveLimbLoss} 'Limb survival rule is active for cut completion checks'
    foreach($owner in @($HostName,$ClientName)){
        $observer=if($owner -eq $HostName){$ClientName}else{$HostName}
        $s=State $owner;$body=@($s.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $s.peer})[0];$id=$body.id
        foreach($key in 128,1025,8){
            $result=Send $owner @{op='slice';partKey=$key}
            Check $result.sliced "$owner native cut $key is accepted"
            $type=if($key -eq 128){32}elseif($key -eq 1025){256}else{2}
            Wait-Check {
                $local=@(Send $owner @{op='pvp-probe-body'}|Where-Object id -eq $id)[0]
                $remote=@(Send $observer @{op='pvp-probe-body'}|Where-Object id -eq $id)[0]
                $local -and $remote -and !$local.sliceRunning -and !$remote.sliceRunning -and $local.sliced -contains $type -and $remote.sliced -contains $type -and $local.splitRenderers -gt 0 -and $remote.splitRenderers -gt 0 -and $local.missingSplitReveal -eq 0 -and $remote.missingSplitReveal -eq 0
            } "$owner cut $key completes and replicates with Realistic Bleeding"
        }
    }
    foreach($name in @($HostName,$ClientName)){
        $log=Get-Content (Join-Path $workspace "artifacts/runs/$name/unity.log") -Raw
        Check (!$log.Contains('ThunderRoad.Ragdoll.OnSlice (')) "$name has no native cut completion exception"
    }
    $passed=$true
}finally{
    @{time=[DateTime]::UtcNow.ToString('o');passed=$passed;build=$build;checks=$checks;scope='Constructed native damage contacts and explicit native mesh cuts; no sword-swing quality or VR feel claim.'}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $workspace 'artifacts/pvp-damage-report.json')
}
