param([string[]]$Weapons=@('SwordShortCommon','SwordLongCommon','SwordGreatZweihander','SwordGreatCommon','SwordRaktaKatana','SwordGreatExecution'),[ValidateSet('host','client')][string[]]$Roles=@('host','client'),[ValidateRange(1,10)][int]$Swings=3,[int]$Latency=60,[int]$Loss=5)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$cases=[System.Collections.Generic.List[object]]::new()
function Send($Name,$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 10 -Compress)}
foreach($name in @('host','client')){Send $name @{op='disconnect'}|Out-Null}
Send 'host' @{op='host';port=19050;latency=$Latency;loss=$Loss}|Out-Null
Send 'client' @{op='join';address='127.0.0.1';port=19050;latency=$Latency;loss=$Loss}|Out-Null
$deadline=[DateTime]::UtcNow.AddSeconds(20)
do{
    $s=Send 'host' @{op='status'};$c=Send 'client' @{op='status'}
    if($s.connected -and $s.peers -eq 2 -and $c.connected -and $c.peers -eq 2){break}
    if([DateTime]::UtcNow -gt $deadline){throw 'The isolated arena pair did not connect.'}
    Start-Sleep -Milliseconds 150
}while($true)
$report=@{time=[DateTime]::UtcNow.ToString('o');passed=$false;everySwingRequired=$true;swingsPerCase=$Swings;latency=$Latency;loss=$Loss;build=@{game=$s.game;mod=$s.modBuild;core=$s.coreBuild;hostProcess=$s.processId;clientProcess=$c.processId};cases=$cases}
try{
    foreach($role in $Roles){foreach($weapon in $Weapons){
        $two=$weapon -in @('SwordLongCommon','SwordGreatZweihander','SwordGreatCommon','SwordGreatExecution')
        $duration=if($two){.08}else{.05}
        $suffix="-$weapon";if($weapon -eq 'SwordShortCommon'){$suffix=''}
        if($two){$suffix+='-twohand-arc'}
        if($duration -ne .05){$suffix+="-$duration"}
        $path=Join-Path $workspace "artifacts\physical-slice-$role$suffix-report.json"
        $errorText=$null;$evidence=$null
        try{
            & (Join-Path $PSScriptRoot 'Invoke-PhysicalSliceTests.ps1') -Attacker $role -Weapon $weapon -TwoHanded:$two -Arc:$two -ArcAngle 60 -StrokeSeconds $duration -MinimumContactSpeed 8 -Attempts $Swings -UnprotectedOnly
        }catch{$errorText=$_.ToString();Write-Output "FAIL: $role $weapon : $errorText"}
        if(Test-Path -LiteralPath $path){$evidence=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json}
        $cuts=@($evidence.attempts|Where-Object {!$_.protected -and $_.cutSucceeded}).Count
        $trials=@($evidence.attempts|Where-Object {!$_.protected}).Count
        $cases.Add(@{role=$role;weapon=$weapon;twoHanded=$two;passed=($null -eq $errorText -and $cuts -eq $Swings -and $trials -eq $Swings);cuts=$cuts;swings=$trials;error=$errorText;evidence=$evidence})
        Write-Output "NECK CONSISTENCY: $role $weapon : $cuts/$trials live swings cut the neck"
    }}
    $report.passed=@($cases|Where-Object {!$_.passed}).Count -eq 0
}finally{
    $report.totalSwings=($cases|Measure-Object swings -Sum).Sum
    $report.totalCuts=($cases|Measure-Object cuts -Sum).Sum
    $report.completed=[DateTime]::UtcNow.ToString('o')
    $report|ConvertTo-Json -Depth 25|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\neck-reliability-report.json')
}
if(!$report.passed){throw 'Neck consistency failed; every live swing is counted in artifacts/neck-reliability-report.json.'}
