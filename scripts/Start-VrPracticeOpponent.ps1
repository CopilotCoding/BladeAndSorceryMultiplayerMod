param(
    [ValidateRange(10,1800)][int]$DurationSeconds=180,
    [ValidateRange(1024,65535)][int]$Port=9050,
    [ValidateRange(0,200)][int]$Latency=0,
    [ValidateRange(0,900)][int]$WaitForHostSeconds=0,
    [ValidatePattern('^[a-zA-Z0-9_]*$')][string]$TestHostInstance='',
    [string]$GamePath='C:\Program Files (x86)\Steam\steamapps\common\Blade & Sorcery'
)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$runtime=Join-Path $workspace '.runtime\game'
$testExe=[IO.Path]::GetFullPath((Join-Path $runtime 'BladeAndSorcery.exe'))
if(!(Test-Path -LiteralPath (Join-Path $runtime '.basmp-test-runtime'))){throw 'Create the isolated runtime with New-TestRuntime.ps1 first.'}
$allowedHost=0
if($TestHostInstance){
    $record=Get-Content -LiteralPath (Join-Path $workspace "artifacts\runs\$TestHostInstance\process.json") -Raw|ConvertFrom-Json
    $allowedHost=[int]$record.pid
}
$busy=@(Get-CimInstance Win32_Process -Filter "Name='BladeAndSorcery.exe'"|Where-Object {$_.ExecutablePath -eq $testExe -and $_.ProcessId -ne $allowedHost})
if($busy.Count){throw 'Finish or stop other isolated game tests before starting the practice opponent.'}
if($allowedHost){
    $hostProcess=Get-CimInstance Win32_Process -Filter "ProcessId=$allowedHost"
    if(!$hostProcess -or $hostProcess.ExecutablePath -ne $testExe -or $hostProcess.CommandLine -notlike '*-mp-test-dir*'){throw 'The requested automated host is not a verified test process.'}
}
$sourceMod=if($TestHostInstance){Join-Path $workspace 'artifacts\mod\BladeAndSorceryMultiplayer'}else{Join-Path $GamePath 'BladeAndSorcery_Data\StreamingAssets\Mods\BladeAndSorceryMultiplayer'}
if(!(Test-Path -LiteralPath (Join-Path $sourceMod 'BladeAndSorcery.Multiplayer.dll'))){throw 'The host mod is not installed. Install the verified build before running practice.'}
Copy-Item -LiteralPath $sourceMod -Destination (Join-Path $runtime 'BladeAndSorcery_Data\StreamingAssets\Mods') -Recurse -Force
$instance='practice_'+[DateTime]::UtcNow.ToString('yyyyMMddHHmmssffff')
$started=$false;$bodyId='';$guards=0;$ready=$false
$practiceWeapons=[System.Collections.Generic.Dictionary[string,string]]::new()
function Send([hashtable]$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $instance -Json ($Command|ConvertTo-Json -Depth 12 -Compress)}
function State {Send @{op='status'}}
try{
    Write-Output 'The normal PCVR game must already be hosting in Arena. This peer holds a stationary sword guard; it does not run attack trajectories.'
    & (Join-Path $PSScriptRoot 'Start-TestInstance.ps1') -Name $instance
    $started=$true
    $root=Join-Path $workspace "artifacts\runs\$instance"
    $record=Get-Content -LiteralPath (Join-Path $root 'process.json') -Raw|ConvertFrom-Json
    $deadline=[DateTime]::UtcNow.AddSeconds(90)
    do{
        try{$s=Get-Content -LiteralPath (Join-Path $root 'status.json') -Raw|ConvertFrom-Json}catch{$s=$null}
        if($s -and $s.processId -eq $record.pid -and $s.creature){break}
        if([DateTime]::UtcNow -gt $deadline){throw 'The practice game did not reach its physical-player ready state.'}
        Start-Sleep -Milliseconds 250
    }while($true)
    if($WaitForHostSeconds -gt 0 -and !$TestHostInstance){
        Write-Output "Practice client loaded. Waiting up to $WaitForHostSeconds seconds for Host session on UDP $Port."
        $hostDeadline=[DateTime]::UtcNow.AddSeconds($WaitForHostSeconds)
        $hostExe=[IO.Path]::GetFullPath((Join-Path $GamePath 'BladeAndSorcery.exe'))
        do{
            $hostPids=@(Get-Process -Name BladeAndSorcery -ErrorAction SilentlyContinue|Where-Object {$_.Path -eq $hostExe}|ForEach-Object Id)
            $listening=@(Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue|Where-Object {$_.OwningProcess -in $hostPids}).Count -gt 0
            if($listening){break}
            if([DateTime]::UtcNow -gt $hostDeadline){throw 'Timed out waiting for the normal game to host.'}
            Start-Sleep -Milliseconds 500
        }while($true)
    }
    Send @{op='join';name='Practice opponent';address='127.0.0.1';port=$Port;latency=$Latency}|Out-Null
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do{
        $s=State
        if($s.connected -and $s.peers -ge 2 -and @($s.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq 1}).Count -eq 1){break}
        if([DateTime]::UtcNow -gt $deadline){throw 'Could not join the Arena host. Check that the normal game is hosting on this port with the same mod build.'}
        Start-Sleep -Milliseconds 200
    }while($true)
    $until=[DateTime]::UtcNow.AddSeconds($DurationSeconds)
    do{
        $s=State
        if(!$s.connected){throw 'The practice peer disconnected.'}
        $body=@($s.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $s.peer})[0]
        # Replace equipment only for a new living body. A dropped sword, a
        # severed hand or a broken grip must never spawn another sword per poll.
        foreach($id in @($practiceWeapons.Keys)){
            $old=@($s.entities|Where-Object id -eq $id)[0]
            if(!$old){$practiceWeapons.Remove($id)|Out-Null;continue}
            if($practiceWeapons[$id] -ne $body.id -and $old.owner -eq $s.peer -and $old.held -eq 0 -and !$old.penetrating){Send @{op='despawn-item';entity=$id}|Out-Null;$practiceWeapons.Remove($id)|Out-Null}
        }
        if($s.creature -and $body -and !$body.dead -and $body.id -ne $bodyId){
            $yaw=[double]$body.rotation[1];$radians=$yaw*[Math]::PI/180
            $forwardX=[Math]::Sin($radians);$forwardZ=[Math]::Cos($radians)
            $rightX=[Math]::Cos($radians);$rightZ=-[Math]::Sin($radians)
            $x=[double]$s.root[0];$y=[double]$s.root[1];$z=[double]$s.root[2]
            Send @{
                op='pose';head=@($x,($y+1.75),$z);headRotation=@(0,$yaw,0)
                left=@(($x-.3*$rightX+.15*$forwardX),($y+1.2),($z-.3*$rightZ+.15*$forwardZ))
                right=@(($x+.35*$forwardX),($y+1.15),($z+.35*$forwardZ))
                rightRotation=@(-55,$yaw,0)
            }|Out-Null
            Send @{op='equip';item='SwordShortCommon'}|Out-Null
            $armed=State
            $armedBody=@($armed.entities|Where-Object id -eq $body.id)[0]
            if($armedBody.rightHeld){$practiceWeapons[[string]$armedBody.rightHeld]=[string]$body.id}
            $bodyId=$body.id;$guards++
            if(!$ready){$ready=$true;Write-Output "Practice opponent ready for $DurationSeconds seconds. Native guard and body collisions are active. Ctrl+C stops only this test peer."}
        }
        Start-Sleep -Milliseconds 400
    }while([DateTime]::UtcNow -lt $until)
}finally{
    if($started){
        try{
            $s=State
            @{time=[DateTime]::UtcNow.ToString('o');ready=$ready;guards=$guards;mod=$s.modBuild;core=$s.coreBuild;state=$s}|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\vr-practice-report.json')
            foreach($id in @($practiceWeapons.Keys)){
                $item=@($s.entities|Where-Object id -eq $id)[0]
                if($item -and $item.owner -eq $s.peer){try{Send @{op='despawn-item';entity=$id}|Out-Null}catch{Write-Warning "Could not clean up practice weapon $id; continuing peer shutdown."}}
            }
            Send @{op='disconnect'}|Out-Null
            Send @{op='quit'}|Out-Null
            Start-Sleep -Milliseconds 500
        }catch{}
        & (Join-Path $PSScriptRoot 'Stop-TestInstances.ps1') -Names $instance
    }
}
