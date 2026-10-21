param([switch]$SkipLaunch,[switch]$KeepRunning,[switch]$IncludeCoop,[switch]$Resume,[int]$Latency=60,[int]$Loss=5)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$results=[System.Collections.Generic.List[object]]::new()
$completedScenarios=@{};$resumeReport=$null
if($Resume -and !$SkipLaunch){throw 'Resume requires -SkipLaunch and the same retained isolated processes.'}
function Send([string]$Instance,[hashtable]$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Instance -Json ($Command|ConvertTo-Json -Compress -Depth 12)}
function State([string]$Instance){Send $Instance @{op='status'}}
function Wait-For([scriptblock]$Condition,[string]$Reason){
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    while([DateTime]::UtcNow -lt $deadline){if(&$Condition){return};Start-Sleep -Milliseconds 150}
    throw "Timed out: $Reason"
}
function Reset-Fighters {
    $protection=@{}
    foreach($name in @('host','client')){
        $protection[$name]=(State $name).manualInvincibility
        Send $name @{op='invincibility';enabled=$true}|Out-Null
    }
    try{
    foreach($name in @('host','client')){
        Wait-For {(State $name).creature} "$name physical player after combat"
        foreach($side in @('left','right')){Send $name @{op='release';side=$side}|Out-Null}
        Send $name @{op='clear-loose-items'}|Out-Null
    }
    foreach($name in @('host','client')){
        $before=State $name
        $old=@($before.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $before.peer})[0].id
        Send $name @{op='respawn'}|Out-Null
        Wait-For {
            $s=State $name;$body=@($s.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $s.peer})
            $s.creature -and $body.Count -eq 1 -and $body[0].id -ne $old -and !$body[0].dead -and $body[0].sliced.Count -eq 0
        } "$name fresh intact native fighter"
    }
    Wait-For {(State 'host').unregisteredItems -eq 0 -and (State 'client').unregisteredItems -eq 0} 'native equipment discovery after respawn'
    Wait-For {
        $h=State 'host';$c=State 'client'
        [Math]::Abs($h.timeScale-1) -lt .001 -and [Math]::Abs($c.timeScale-1) -lt .001 -and !$h.endGameMenu -and !$c.endGameMenu -and !$h.playerMenuBlocked -and !$c.playerMenuBlocked
    } 'normal arena time and usable menus after multiplayer respawn'
    # Let release events and the replacement poses reach the other native scene
    # before starting another scenario with normal damage enabled.
    Start-Sleep -Milliseconds 400
    }finally{
        foreach($name in @('host','client')){Send $name @{op='invincibility';enabled=$protection[$name]}|Out-Null}
    }
}
function Scenario([string]$Name,[scriptblock]$Action,[switch]$Reset){
    if($completedScenarios.ContainsKey($Name)){
        $results.Add(@{scenario=$Name;passed=$true;reusedFrom=$resumeReport.time})
        Write-Output "SCENARIO RETAINED: $Name (same binaries, processes, latency and loss)";return
    }
    try{if($Reset){Reset-Fighters};&$Action;$results.Add(@{scenario=$Name;passed=$true});Write-Output "SCENARIO PASS: $Name"}
    catch{$results.Add(@{scenario=$Name;passed=$false;error=$_.ToString()});throw}
}
$build=$null
$completed=$false;$runError=$null
try{
    if($Resume){$resumeReport=Get-Content -LiteralPath (Join-Path $workspace 'artifacts\pvp-regression-report.json') -Raw|ConvertFrom-Json}
    $previous=@(Get-ChildItem -LiteralPath (Join-Path $workspace 'artifacts') -Filter '*report.json' -File)
    if($previous.Count){
        $history=Join-Path $workspace ('artifacts\regression-history\'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff'))
        New-Item -ItemType Directory -Path $history -Force|Out-Null
        foreach($file in $previous){Copy-Item -LiteralPath $file.FullName -Destination $history}
    }
    if(!$SkipLaunch){& (Join-Path $PSScriptRoot 'Start-TestPair.ps1')}
    $s=State 'host';$build=@{game=$s.game;mod=$s.modBuild;core=$s.coreBuild;hostProcess=$s.processId;clientProcess=(State 'client').processId}
    if($Resume){
        foreach($key in @('game','mod','core','hostProcess','clientProcess')){if($resumeReport.build.$key -ne $build.$key){throw "Cannot resume regression: $key differs from the retained run"}}
        if($resumeReport.latency -ne $Latency -or $resumeReport.loss -ne $Loss){throw 'Cannot resume with different latency/loss settings'}
        foreach($scenario in $resumeReport.scenarios|Where-Object passed){$completedScenarios[$scenario.scenario]=$true}
    }
    Scenario 'Native VR connection keyboard, validation and edited UDP port' {& (Join-Path $PSScriptRoot 'Invoke-ConnectionEntryTests.ps1')}
    Scenario 'Native session buttons and visible status text' {& (Join-Path $PSScriptRoot 'Invoke-SessionUiTests.ps1')}
    Scenario 'Native combat capture buttons and disconnect saving' {& (Join-Path $PSScriptRoot 'Invoke-CombatCaptureTests.ps1')} -Reset
    Scenario 'Arena session and native replication' {& (Join-Path $PSScriptRoot 'Invoke-ArenaTests.ps1') -SkipLaunch -Latency $Latency -Loss $Loss}
    $s=State 'host';$build=@{game=$s.game;mod=$s.modBuild;core=$s.coreBuild;hostProcess=$s.processId;clientProcess=(State 'client').processId}
    Scenario 'Reconnect preserves native dropped objects' {& (Join-Path $PSScriptRoot 'Invoke-ReconnectTests.ps1') -Latency $Latency -Loss $Loss}
    Scenario 'Clean mock rounds and instant initial replica poses' {& (Join-Path $PSScriptRoot 'Invoke-RoundResetTests.ps1')} -Reset
    Scenario 'Fast native thrusts and two-handed shoulder swings across four weapons' {& (Join-Path $PSScriptRoot 'Invoke-WeaponCombatTests.ps1') -Fast -SkipConnect -Latency $Latency -Loss $Loss} -Reset
    Scenario 'Native body grabs with held weapons and stable release' {& (Join-Path $PSScriptRoot 'Invoke-BodyGrabTests.ps1')} -Reset
    foreach($role in @('host','client')){
        Scenario "$role tracked native body alignment" {& (Join-Path $PSScriptRoot 'Invoke-BodyPoseTests.ps1') -Owner $role} -Reset
        Scenario "$role visible native hand and weapon alignment" {& (Join-Path $PSScriptRoot 'Invoke-GripAlignmentTests.ps1') -Owner $role} -Reset
        Scenario "$role sword clashes" {& (Join-Path $PSScriptRoot 'Invoke-ClashTest.ps1') -Attacker $role -Attempts 3} -Reset
        Scenario "$role two-hand grips" {& (Join-Path $PSScriptRoot 'Invoke-TwoHandTests.ps1') -Owner $role} -Reset
        Scenario "$role contested pickup" {& (Join-Path $PSScriptRoot 'Invoke-ContestedGrabTests.ps1') -Taker $role} -Reset
        Scenario "$role stabbing and withdrawal" {& (Join-Path $PSScriptRoot 'Invoke-StabTests.ps1') -Attacker $role} -Reset
        Scenario "$role slow thrust and withdrawal" {& (Join-Path $PSScriptRoot 'Invoke-StabTests.ps1') -Attacker $role -Slow} -Reset
        Scenario "$role physical slicing and invincibility" {& (Join-Path $PSScriptRoot 'Invoke-PhysicalSliceTests.ps1') -Attacker $role} -Reset
        Scenario "$role native sword damage" {& (Join-Path $PSScriptRoot 'Invoke-PhysicalHitTest.ps1') -Attacker $role} -Reset
        Scenario "$role all native sliceable joint replication" {& (Join-Path $PSScriptRoot 'Invoke-DismembermentTests.ps1') -Owner $role} -Reset
        Scenario "$role targeted physical limb and neck cuts" {& (Join-Path $PSScriptRoot 'Invoke-JointSliceTests.ps1') -Attacker $role} -Reset
    }
    Scenario 'Shared items, handoff and flight' {& (Join-Path $PSScriptRoot 'Invoke-ObjectTests.ps1')} -Reset
    Scenario 'Damage, slices, death and automatic respawn' {& (Join-Path $PSScriptRoot 'Invoke-CombatLifecycleTests.ps1')} -Reset
    Scenario 'Native breakables and shared fragments' {& (Join-Path $PSScriptRoot 'Invoke-BreakableTests.ps1')} -Reset
    Scenario 'Three-process late join, existing slices and departing item owner' {& (Join-Path $PSScriptRoot 'Invoke-LateJoinTests.ps1') -TestOwnerDeparture} -Reset
    if($IncludeCoop){Scenario 'Native co-op AI, combat, waves and teammate protection' {& (Join-Path $PSScriptRoot 'Invoke-CoopTests.ps1') -SkipLaunch -KeepRunning} -Reset}
    Scenario 'Manual respawn, disconnect and native single-player isolation' {& (Join-Path $PSScriptRoot 'Invoke-SessionCleanupTests.ps1')} -Reset
    if($IncludeCoop){Scenario 'Native defeat UI, death clock and dead-player disconnect isolation' {& (Join-Path $PSScriptRoot 'Invoke-DeathFlowTests.ps1')}}
    $completed=$true
}catch{$runError=$_.ToString();throw}
finally{
    @{time=[DateTime]::UtcNow.ToString('o');passed=$completed;error=$runError;build=$build;latency=$Latency;loss=$Loss;resumedFrom=if($resumeReport){$resumeReport.time}else{$null};scenarios=$results}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\pvp-regression-report.json')
    if(!$KeepRunning){& (Join-Path $PSScriptRoot 'Stop-TestInstances.ps1') -Names host,client,third}
}
