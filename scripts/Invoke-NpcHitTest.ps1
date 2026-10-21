param([ValidateSet('host','client')][string]$Attacker='client',[Parameter(Mandatory=$true)][string]$Victim)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
function Send([string]$Name,[hashtable]$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 12 -Compress)}
function State([string]$Name){Send $Name @{op='status'}}
$s=State $Attacker
$x=[double]$s.root[0];$y=[double]$s.root[1];$z=[double]$s.root[2]
Send 'host' @{op='npc-ai';entity=$Victim;enabled=$false}|Out-Null
Send 'host' @{op='npc-teleport';entity=$Victim;position=@($x,$y,($z+.5));yaw=180}|Out-Null
Send $Attacker @{op='pose';head=@($x,($y+1.75),$z);headRotation=@(0,0,0);left=@(($x-.25),($y+1.3),($z+.25));right=@(($x+.35),($y+2.15),($z+.65));rightRotation=@(0,-90,0)}|Out-Null
Send $Attacker @{op='equip';item='SwordShortCommon'}|Out-Null
Start-Sleep -Milliseconds 700
$before=State 'host'
$old=@($before.entities|Where-Object id -eq $Victim)[0]
if(!$old -or $old.dead){throw 'NPC physical-hit fixture must be alive.'}
for($i=0;$i -lt 3;$i++){
    Send $Attacker @{op='motion';to=@(($x+.35),($y+.95),($z+.65));duration=.16;rotation=@(0,-90,0)}|Out-Null
    Start-Sleep -Milliseconds 260
    $current=@((State 'host').entities|Where-Object id -eq $Victim)[0]
    if($current -and ($current.dead -or $current.health -lt $old.health)){break}
    Send $Attacker @{op='motion';to=@(($x+.35),($y+2.15),($z+.65));duration=.35;rotation=@(0,-90,0)}|Out-Null
    Start-Sleep -Milliseconds 450
}
Send $Attacker @{op='release'}|Out-Null
Send $Attacker @{op='clear-loose-items'}|Out-Null
$deadline=[DateTime]::UtcNow.AddSeconds(5)
do{
    $hostState=State 'host';$clientState=State 'client'
    $a=@($hostState.entities|Where-Object id -eq $Victim)[0]
    $b=@($clientState.entities|Where-Object id -eq $Victim)[0]
    $passed=$a -and $b -and $a.health -lt $old.health -and [Math]::Abs($a.health-$b.health) -lt .01 -and $a.dead -eq $b.dead
    if($passed){break}
    Start-Sleep -Milliseconds 100
}while([DateTime]::UtcNow -lt $deadline)
@{time=[DateTime]::UtcNow.ToString('o');attacker=$Attacker;victim=$Victim;before=$old.health;after=$a.health;remoteHealth=$b.health;passed=$passed;host=$hostState;client=$clientState}|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $workspace "artifacts\npc-physical-hit-$Attacker-report.json")
if(!$passed){throw "$Attacker native sword strike did not produce a synchronized NPC combat outcome."}
Write-Output "PASS: $Attacker physical sword strike damages the native NPC and synchronizes its outcome"
