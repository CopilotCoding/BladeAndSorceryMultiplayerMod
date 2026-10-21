param()
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$hostName='practice_validation';$peerName='';$job=$null;$passed=$false
$tests=[System.Collections.Generic.List[object]]::new()
function Send($Name,$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 10 -Compress)}
function State($Name){Send $Name @{op='status'}}
function Own($State){@($State.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $State.peer})[0]}
function Wait-For($Condition,$Reason,[int]$Seconds=30){
    $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
    do{if(&$Condition){$tests.Add(@{test=$Reason;passed=$true});Write-Output "PASS: $Reason";return};Start-Sleep -Milliseconds 150}while([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Reason"
}
try{
    # Start-VrPracticeOpponent itself refuses any other isolated game processes.
    & (Join-Path $PSScriptRoot 'Start-TestInstance.ps1') -Name $hostName
    $root=Join-Path $workspace "artifacts\runs\$hostName"
    $record=Get-Content -LiteralPath (Join-Path $root 'process.json') -Raw|ConvertFrom-Json
    Wait-For {
        try{$s=Get-Content -LiteralPath (Join-Path $root 'status.json') -Raw|ConvertFrom-Json;$s.processId -eq $record.pid -and $s.creature}catch{$false}
    } 'Fresh isolated practice host is ready' 90
    Send $hostName @{op='host';name='Practice validation';port=19050}|Out-Null
    Send $hostName @{op='invincibility';enabled=$true}|Out-Null
    Send $hostName @{op='teleport';position=@(0,.08,4);yaw=0}|Out-Null
    $since=[DateTime]::UtcNow
    $helper=Join-Path $PSScriptRoot 'Start-VrPracticeOpponent.ps1'
    $job=Start-Job -ScriptBlock {param($Path,$HostName);& $Path -TestHostInstance $HostName -Port 19050 -DurationSeconds 70} -ArgumentList $helper,$hostName
    Wait-For {
        $candidate=@(Get-ChildItem -LiteralPath (Join-Path $workspace 'artifacts\runs') -Directory -Filter 'practice_*'|Where-Object {$_.Name -ne $hostName -and $_.LastWriteTimeUtc -ge $since}|Sort-Object LastWriteTimeUtc -Descending)[0]
        if($candidate){$script:peerName=$candidate.Name}
        if(!$peerName){return $false}
        try{$s=Get-Content -LiteralPath (Join-Path $candidate.FullName 'status.json') -Raw|ConvertFrom-Json;$s.connected -and (Own $s).rightHeld}catch{$false}
    } 'Practice helper joins and equips its initial native guard' 100
    $initial=State $peerName;$oldBody=(Own $initial).id;$firstSword=(Own $initial).rightHeld
    $build=@{mod=$initial.modBuild;core=$initial.coreBuild}
    Send $peerName @{op='release';side='right'}|Out-Null
    Start-Sleep -Seconds 2
    $dropped=State $peerName
    if($dropped.rightItem -or @($dropped.entities|Where-Object {$_.catalog -eq 'SwordShortCommon' -and $_.owner -eq $dropped.peer}).Count -ne 1){throw 'Dropping the practice sword spawned replacement equipment'}
    $tests.Add(@{test='Dropped practice sword stays dropped without repeated replacements';passed=$true})
    Send $peerName @{op='kill'}|Out-Null
    Wait-For {$s=State $peerName;$body=Own $s;$body -and !$body.dead -and $body.id -ne $oldBody -and $body.rightHeld -and $body.rightHeld -ne $firstSword} 'Native respawn equips exactly one new guard'
    Wait-For {@((State $peerName).entities|Where-Object id -eq $firstSword).Count -eq 0 -and @((State $hostName).entities|Where-Object id -eq $firstSword).Count -eq 0} 'Unclaimed previous-body sword is removed from both peers'
    $armed=State $peerName;$secondSword=(Own $armed).rightHeld;$secondBody=(Own $armed).id
    Send $peerName @{op='release';side='right'}|Out-Null
    $sword=@($armed.entities|Where-Object id -eq $secondSword)[0];$p=$sword.position
    Send $hostName @{op='teleport';position=@($p[0],.08,($p[2]-.5));yaw=0}|Out-Null
    Send $hostName @{op='pose';head=@($p[0],.95,($p[2]-.5));headRotation=@(0,0,0);left=@(($p[0]-.3),.8,($p[2]-.3));right=$p;rightRotation=@(0,0,0)}|Out-Null
    Start-Sleep -Milliseconds 450
    Send $hostName @{op='grab-entity';entity=$secondSword;side='right'}|Out-Null
    $hostPeer=(State $hostName).peer
    Wait-For {@((State $peerName).entities|Where-Object {$_.id -eq $secondSword -and $_.owner -eq $hostPeer -and $_.held -gt 0}).Count -eq 1} 'Host can claim a dropped practice sword through native ownership transfer'
    Send $hostName @{op='release';side='right'}|Out-Null
    Send $peerName @{op='kill'}|Out-Null
    Wait-For {$s=State $peerName;$body=Own $s;$body -and !$body.dead -and $body.id -ne $secondBody -and $body.rightHeld} 'Practice guard is restored on a second respawn'
    $claimed=@((State $hostName).entities|Where-Object id -eq $secondSword)[0]
    if(!$claimed -or $claimed.owner -ne $hostPeer){throw "Practice cleanup deleted another player's claimed sword"}
    $tests.Add(@{test='Respawn cleanup preserves a sword claimed and dropped by another player';passed=$true})
    Send $hostName @{op='teleport';position=@(0,.08,4);yaw=0}|Out-Null
    Wait-For {$job.State -in @('Completed','Failed','Stopped')} 'Practice helper finishes its bounded run' 75
    Receive-Job $job -ErrorAction Stop|Write-Output
    if($job.State -ne 'Completed'){throw "Practice helper ended in state $($job.State)"}
    Wait-For {$s=State $hostName;$s.peers -eq 1 -and $s.networkControllers -eq 0} 'Practice departure removes its remote controllers'
    $after=State $hostName
    if(!@($after.entities|Where-Object id -eq $secondSword).Count){throw 'Helper shutdown deleted the host-owned sword'}
    $tests.Add(@{test='Helper shutdown preserves transferred equipment';passed=$true})
    $passed=$true
}catch{$tests.Add(@{test='Run completion';passed=$false;error=$_.ToString()});throw}
finally{
    if($job){if($job.State -eq 'Running'){Stop-Job $job};Remove-Job $job -Force}
    $names=@($hostName);if($peerName){$names+=$peerName}
    & (Join-Path $PSScriptRoot 'Stop-TestInstances.ps1') -Names $names
    @{time=[DateTime]::UtcNow.ToString('o');passed=$passed;build=$build;tests=$tests}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\practice-validation-report.json')
}
