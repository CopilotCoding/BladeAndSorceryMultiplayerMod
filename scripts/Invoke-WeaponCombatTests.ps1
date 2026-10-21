param([int]$Latency=60,[int]$Loss=5,[switch]$SkipConnect,[switch]$Fast,[string[]]$Weapons=@('DaggerCommon','SwordShortCommon','SwordLongCommon','SwordGreatZweihander'),[ValidateSet('host','client')][string[]]$Roles=@('host','client'))
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
function Send([string]$Instance,[hashtable]$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Instance -Json ($Command|ConvertTo-Json -Compress -Depth 12)}
function State([string]$Instance){Send $Instance @{op='status'}}
function Reset-Fighters {
    foreach($name in @('host','client')){Send $name @{op='invincibility';enabled=$true}|Out-Null}
    foreach($name in @('host','client')){
        $readyDeadline=[DateTime]::UtcNow.AddSeconds(15)
        do {
            $ready=State $name;$readyBody=@($ready.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $ready.peer})[0]
            if($ready.creature -and $readyBody -and !$readyBody.dead){break}
            if([DateTime]::UtcNow -gt $readyDeadline){throw "$name physical fighter not ready"}
            Start-Sleep -Milliseconds 150
        }while($true)
        foreach($side in @('left','right')){Send $name @{op='release';side=$side}|Out-Null}
        Send $name @{op='clear-loose-items'}|Out-Null
        $before=State $name;$old=@($before.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $before.peer})[0].id
        Send $name @{op='respawn'}|Out-Null
        $deadline=[DateTime]::UtcNow.AddSeconds(15)
        do {
            $s=State $name;$body=@($s.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $s.peer})[0]
            if($body -and $body.id -ne $old -and !$body.dead -and $body.sliced.Count -eq 0){break}
            if([DateTime]::UtcNow -gt $deadline){throw "$name did not obtain a fresh native fighter"}
            Start-Sleep -Milliseconds 150
        }while($true)
    }
    Start-Sleep -Milliseconds 600
    foreach($name in @('host','client')){Send $name @{op='combat-trace';clear=$true}|Out-Null;Send $name @{op='invincibility';enabled=$false}|Out-Null}
}
$results=[System.Collections.Generic.List[object]]::new()
$initial=@{};foreach($name in @('host','client')){$initial[$name]=(State $name).manualInvincibility}
$thrustDuration=if($Fast){.12}else{.8};$cutDuration=.08
$report=@{time=[DateTime]::UtcNow.ToString('o');passed=$false;fast=[bool]$Fast;neckConsistencyVersion=2;neckSwingsPerCase=3;latency=$Latency;loss=$Loss;scenarios=$results}
try {
    if(!$SkipConnect){
        foreach($name in @('host','client')){Send $name @{op='disconnect'}|Out-Null}
        Send 'host' @{op='host';port=19050;latency=$Latency;loss=$Loss}|Out-Null
        Send 'client' @{op='join';address='127.0.0.1';port=19050;latency=$Latency;loss=$Loss}|Out-Null
        $deadline=[DateTime]::UtcNow.AddSeconds(20)
        while((State 'host').peers -ne 2 -or (State 'client').peers -ne 2){if([DateTime]::UtcNow -gt $deadline){throw 'Arena pair did not connect'};Start-Sleep -Milliseconds 150}
    }
    $s=State 'host';$report.build=@{game=$s.game;mod=$s.modBuild;core=$s.coreBuild;hostProcess=$s.processId;clientProcess=(State 'client').processId}
    foreach($role in $Roles){
        foreach($weapon in $Weapons){
            $two=$weapon -in @('SwordLongCommon','SwordGreatZweihander')
            $label="$role-$weapon-thrust"
            Reset-Fighters
            $errorText=$null
            try {& (Join-Path $PSScriptRoot 'Invoke-StabTests.ps1') -Attacker $role -Weapon $weapon -TwoHanded:$two -ThrustSeconds $thrustDuration -MinimumContactSpeed $(if($Fast){2}else{0})}
            catch {$errorText=$_.ToString();Write-Output "FAIL: $label : $errorText"}
            $traces=@{};foreach($name in @('host','client')){$traces[$name]=@(Send $name @{op='combat-trace'})}
            $results.Add(@{scenario=$label;twoHanded=$two;duration=$thrustDuration;passed=($null -eq $errorText);error=$errorText;traces=$traces})
        }
        foreach($weapon in @($Weapons|Where-Object {$_ -in @('SwordLongCommon','SwordGreatZweihander')})){
            $label="$role-$weapon-neck"
            Reset-Fighters
            $errorText=$null
            try {& (Join-Path $PSScriptRoot 'Invoke-PhysicalSliceTests.ps1') -Attacker $role -Weapon $weapon -TwoHanded -StrokeSeconds $cutDuration -Attempts 3 -Arc:$Fast -ArcAngle $(if($Fast){60}else{45}) -MinimumContactSpeed $(if($Fast){8}else{0})}
            catch {$errorText=$_.ToString();Write-Output "FAIL: $label : $errorText"}
            $traces=@{};foreach($name in @('host','client')){$traces[$name]=@(Send $name @{op='combat-trace'})}
            $results.Add(@{scenario=$label;twoHanded=$true;duration=$cutDuration;passed=($null -eq $errorText);error=$errorText;traces=$traces})
        }
    }
    $report.passed=@($results|Where-Object {!$_.passed}).Count -eq 0
} finally {
    foreach($name in @('host','client')){try{Send $name @{op='invincibility';enabled=$initial[$name]}|Out-Null}catch{}}
    $report.completed=[DateTime]::UtcNow.ToString('o')
    $file=if($Fast){'weapon-combat-fast-report.json'}else{'weapon-combat-report.json'}
    $report|ConvertTo-Json -Depth 25|Set-Content -LiteralPath (Join-Path $workspace "artifacts\$file")
}
if(!$report.passed){throw "Weapon combat matrix failed. Inspect artifacts/$file."}
