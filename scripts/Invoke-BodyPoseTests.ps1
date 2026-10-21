param([ValidateSet('host','client')][string]$Owner='host')
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$observer=if($Owner -eq 'host'){'client'}else{'host'}
$tests=[System.Collections.Generic.List[object]]::new();$passed=$false
function Send($Name,$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 9 -Compress)}
function State($Name){Send $Name @{op='status'}}
function Own($State){@($State.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $State.peer})[0]}
function Distance($A,$B){[Math]::Sqrt([Math]::Pow($A[0]-$B[0],2)+[Math]::Pow($A[1]-$B[1],2)+[Math]::Pow($A[2]-$B[2],2))}
function Measure-Pose {
    $source=Own (State $Owner);$remote=@((State $observer).entities|Where-Object id -eq $source.id)[0]
    if(!$source -or !$remote -or $source.dead -or $remote.dead){throw 'Body pose check requires both living native fighters'}
    foreach($key in @($source.parts|Where-Object {$_.type -in @('Head','Neck','LeftArm','RightArm','LeftHand','RightHand')}|ForEach-Object key)){
        $a=@($source.parts|Where-Object key -eq $key)[0];$b=@($remote.parts|Where-Object key -eq $key)[0]
        @{part=$a.type;key=$key;error=(Distance $a.position $b.position);local=$a.position;remote=$b.position}
    }
}
try{
    foreach($name in @('host','client')){foreach($side in @('left','right')){Send $name @{op='release';side=$side}|Out-Null};Send $name @{op='clear-loose-items'}|Out-Null}
    Send $Owner @{op='teleport';position=@(0,.08,0);yaw=0}|Out-Null
    Send $observer @{op='teleport';position=@(0,.08,4);yaw=180}|Out-Null
    foreach($height in @(.85,1.3,1.65)){
        Send $Owner @{op='pose';head=@(0,1.8,0);headRotation=@(0,0,0);left=@(-.2,$height,.3);right=@(.2,$height,.3);leftRotation=@(0,0,0);rightRotation=@(0,0,0)}|Out-Null
        # This measures steady alignment after a tracking discontinuity. Record
        # physical convergence explicitly instead of assuming a fixed wall-time
        # delay represents the same number of simulation steps under load.
        $started=[DateTime]::UtcNow;$deadline=$started.AddSeconds(10);$stable=0;$settling=[System.Collections.Generic.List[object]]::new()
        do{
            $probe=@(Measure-Pose);$error=($probe|Measure-Object error -Maximum).Maximum
            $settling.Add(@{seconds=([DateTime]::UtcNow-$started).TotalSeconds;maximumPositionError=$error})
            if($error -lt .03){$stable++}else{$stable=0}
            if($stable -ge 3){break}
            Start-Sleep -Milliseconds 100
        }while([DateTime]::UtcNow -lt $deadline)
        if($stable -lt 3){$tests.Add(@{handHeight=$height;passed=$false;settling=$settling});throw "Native body alignment did not settle within 10 seconds at hand height $height"}
        $samples=@(for($sample=0;$sample -lt 4;$sample++){
            Measure-Pose
            Start-Sleep -Milliseconds 100
        })
        $worst=($samples|Measure-Object error -Maximum).Maximum
        $tests.Add(@{handHeight=$height;passed=$worst -lt .03;maximumPositionError=$worst;settling=$settling;samples=$samples})
        if($worst -ge .03){throw "Unheld native replica bones differ by $worst metres at hand height $height"}
        Write-Output "PASS: $Owner unheld head/arm/hand poses agree within $([Math]::Round($worst,4))m at hand height $height"
    }
    $passed=$true
}finally{
    @{time=[DateTime]::UtcNow.ToString('o');passed=$passed;owner=$Owner;tests=$tests}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $workspace "artifacts\body-pose-$Owner-report.json")
}
