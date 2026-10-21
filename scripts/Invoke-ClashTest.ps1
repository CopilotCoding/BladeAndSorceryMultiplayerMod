param([int]$Attempts = 6, [ValidateSet('host','client')][string]$Attacker = 'host')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
function Send([string]$Instance, [hashtable]$Command) { & (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Instance -Json ($Command | ConvertTo-Json -Compress -Depth 12) }
function State([string]$Instance) { Send $Instance @{op='status'} }
$defender = if ($Attacker -eq 'host') { 'client' } else { 'host' }
$hostState = State $Attacker
$origin = $hostState.root
$x = [double]$origin[0]; $y = [double]$origin[1]; $z = [double]$origin[2]
foreach ($name in @('host','client')) { Send $name @{op='release'} | Out-Null }
foreach ($name in @('host','client')) { Send $name @{op='clear-loose-items'} | Out-Null }
Send $Attacker @{op='teleport';position=@($x,$y,$z);yaw=0} | Out-Null
Send $defender @{op='teleport';position=@($x,$y,($z+1.0));yaw=180} | Out-Null
Send $Attacker @{op='pose';head=@($x,($y+1.75),$z);headRotation=@(0,0,0);left=@(($x-.3),($y+1.2),($z+.2));right=@(($x+.3),($y+1.6),($z+.45));rightRotation=@(0,-90,0)} | Out-Null
Send $defender @{op='pose';head=@($x,($y+1.75),($z+1.0));headRotation=@(0,180,0);left=@(($x+.3),($y+1.2),($z+1.0));right=@($x,($y+1.15),($z+.65));rightRotation=@(-55,180,0)} | Out-Null
Start-Sleep -Milliseconds 500
foreach ($name in @('host','client')) { Send $name @{op='equip';item='SwordShortCommon'} | Out-Null }
Start-Sleep -Milliseconds 800
$beforeHost = State 'host'; $beforeClient = State 'client'
if ($beforeHost.rightItem -ne 'SwordShortCommon' -or $beforeClient.rightItem -ne 'SwordShortCommon') { throw 'Both fighters must hold swords before the clash trajectory starts.' }
for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
    Send $Attacker @{op='motion';to=@(($x+.3),($y+1.35),($z+.45));duration=.35;rotation=@(0,-90,0)} | Out-Null
    Start-Sleep -Milliseconds 450
    Send $Attacker @{op='motion';to=@(($x+.3),($y+1.65),($z+.45));duration=.45;rotation=@(0,-90,0)} | Out-Null
    Start-Sleep -Milliseconds 550
}
$afterHost = State 'host'; $afterClient = State 'client'
if ($afterHost.rightItem -ne 'SwordShortCommon' -or $afterClient.rightItem -ne 'SwordShortCommon') { throw 'A fighter dropped its sword during the test; inspect the native grip and trajectory.' }
$report = @{
    time=[DateTime]::UtcNow.ToString('o')
    attacker=$Attacker
    hostContacts=($afterHost.weaponContacts - $beforeHost.weaponContacts)
    clientContacts=($afterClient.weaponContacts - $beforeClient.weaponContacts)
    hostPhysicsContacts=($afterHost.physicsWeaponContacts - $beforeHost.physicsWeaponContacts)
    clientPhysicsContacts=($afterClient.physicsWeaponContacts - $beforeClient.physicsWeaponContacts)
    hostTrace=$afterHost.contactsTrace
    clientTrace=$afterClient.contactsTrace
    hostState=$afterHost
    clientState=$afterClient
}
$report | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $workspace "artifacts\clash-test-$Attacker-report.json")
Write-Output "Native cross-player sword contacts: host=$($report.hostContacts), client=$($report.clientContacts); Unity physics contacts: host=$($report.hostPhysicsContacts), client=$($report.clientPhysicsContacts)"
if ($report.hostPhysicsContacts -lt 1 -or $report.clientPhysicsContacts -lt 1) { throw 'Physical sword contact was not observed on both instances; inspect the physical trajectories and report.' }
Write-Output 'PASS: Unity sword-to-sword physics contacts observed independently on host and client'
