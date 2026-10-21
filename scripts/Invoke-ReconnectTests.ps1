param([int]$Latency=60,[int]$Loss=5)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
function Send([string]$Instance,[hashtable]$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Instance -Json ($Command|ConvertTo-Json -Compress -Depth 12)}
function State([string]$Instance){Send $Instance @{op='status'}}
function Wait-For([scriptblock]$Condition,[string]$Reason){
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    while([DateTime]::UtcNow -lt $deadline){if(&$Condition){return};Start-Sleep -Milliseconds 150}
    throw "Timed out: $Reason"
}
$report=@{time=[DateTime]::UtcNow.ToString('o');passed=$false}
try{
    $before=State 'client'
    if(!$before.connected -or $before.peers -ne 2){throw 'Start a connected two-player arena first.'}
    Send 'client' @{op='equip';item='SwordShortCommon'}|Out-Null
    Start-Sleep -Milliseconds 400
    $s=State 'client';$weapon=@($s.entities|Where-Object {$_.owner -eq $s.peer -and $_.catalog -eq 'SwordShortCommon' -and $_.held -gt 0})[0]
    if(!$weapon){throw 'The reconnect fixture failed to equip its native weapon.'}
    $nativeId=$weapon.nativeId
    Send 'client' @{op='release'}|Out-Null
    Send 'client' @{op='disconnect'}|Out-Null
    Send 'host' @{op='disconnect'}|Out-Null
    Wait-For {
        $a=State 'host';$b=State 'client'
        !$a.connected -and !$b.connected -and !$a.gameplayPatches -and !$b.gameplayPatches -and $a.networkControllers -eq 0 -and $b.networkControllers -eq 0
    } 'clean inactive session before reconnecting'
    Send 'host' @{op='host';name='Host';latency=$Latency;loss=$Loss}|Out-Null
    Send 'client' @{op='join';name='Client';latency=$Latency;loss=$Loss}|Out-Null
    Wait-For {
        $a=State 'host';$b=State 'client'
        $source=@($b.entities|Where-Object nativeId -eq $nativeId)
        $a.peers -eq 2 -and $b.peers -eq 2 -and $a.unregisteredItems -eq 0 -and $b.unregisteredItems -eq 0 -and
        $source.Count -eq 1 -and $source[0].owner -eq $b.peer -and $source[0].held -eq 0 -and @($a.entities|Where-Object id -eq $source[0].id).Count -eq 1
    } 'the same dropped native client object rejoins the network registry'
    $a=State 'host';$b=State 'client'
    $idsA=@($a.entities|Where-Object kind -eq 'Item'|ForEach-Object id|Sort-Object)
    $idsB=@($b.entities|Where-Object kind -eq 'Item'|ForEach-Object id|Sort-Object)
    if(($idsA -join ',') -ne ($idsB -join ',')){throw 'World-object identities differ after reconnecting.'}
    $report.nativeItem=$nativeId;$report.host=$a;$report.client=$b;$report.passed=$true
    Write-Output 'PASS: Disconnect/reconnect preserves the dropped native client object and restores matching world identities'
}catch{$report.error=$_.ToString();throw}
finally{$report|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\reconnect-report.json')}
