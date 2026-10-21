param([ValidateSet('host','client')][string]$Attacker = 'host', [int]$Attempts = 3, [switch]$ExpectBlocked)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
function Send([string]$Instance, [hashtable]$Command) { & (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Instance -Json ($Command | ConvertTo-Json -Compress -Depth 12) }
function State([string]$Instance) { Send $Instance @{op='status'} }
$defender = if ($Attacker -eq 'host') {'client'} else {'host'}
foreach($name in @('host','client')) {
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    while(!(State $name).creature) {
        if([DateTime]::UtcNow -gt $deadline){throw "$name has no native player body."}
        Start-Sleep -Milliseconds 100
    }
}
$origin = (State $Attacker).root
$x = [double]$origin[0]; $y = [double]$origin[1]; $z = [double]$origin[2]
foreach ($name in @('host','client')) { Send $name @{op='release'} | Out-Null }
foreach ($name in @('host','client')) { Send $name @{op='clear-loose-items'} | Out-Null }
Send $Attacker @{op='teleport';position=@($x,$y,$z);yaw=0} | Out-Null
Send $defender @{op='teleport';position=@($x,$y,($z+.5));yaw=180} | Out-Null
Send $Attacker @{op='pose';head=@($x,($y+1.75),$z);headRotation=@(0,0,0);left=@(($x-.25),($y+1.3),($z+.25));right=@(($x+.35),($y+2.15),($z+.65));rightRotation=@(0,-90,0)} | Out-Null
Send $defender @{op='pose';head=@($x,($y+1.75),($z+.5));headRotation=@(0,180,0);left=@(($x+.25),($y+1.2),($z+.5));right=@(($x-.35),($y+1.2),($z+.5));rightRotation=@(0,180,0)} | Out-Null
Send $Attacker @{op='equip';item='SwordShortCommon'} | Out-Null
Start-Sleep -Milliseconds 700
$before = State $defender
$victimId = @($before.entities | Where-Object { $_.kind -eq 'Player' -and $_.owner -eq $before.peer })[0].id
for ($i = 0; $i -lt $Attempts; $i++) {
    Send $Attacker @{op='motion';to=@(($x+.35),($y+.95),($z+.65));duration=.16;rotation=@(0,-90,0)} | Out-Null
    Start-Sleep -Milliseconds 260
    $current=State $defender
    $body=@($current.entities|Where-Object id -eq $victimId)[0]
    if ($body -and ($body.dead -or $body.health -lt $before.localHealth)) { break }
    if ($ExpectBlocked -and @($current.damageTrace|Where-Object {$_.time -ge $before.time -and $_.victim -eq $victimId -and !$_.allowed -and $_.damager -like 'Sword*'}).Count -gt 0) { break }
    Send $Attacker @{op='motion';to=@(($x+.35),($y+2.15),($z+.65));duration=.35;rotation=@(0,-90,0)} | Out-Null
    Start-Sleep -Milliseconds 450
}
# End the stroke's physical contacts before comparing terminal outcomes. Reading
# two live scenes sequentially can otherwise compare an early hit with a later one.
Send $Attacker @{op='release'} | Out-Null
Send $Attacker @{op='clear-loose-items'} | Out-Null
$deadline=[DateTime]::UtcNow.AddSeconds(3)
do {
    $after = State $defender; $observed = State $Attacker
    $ownBody = @($after.entities | Where-Object id -eq $victimId)[0]
    $remote = @($observed.entities | Where-Object id -eq $victimId)[0]
    $converged=$ownBody -and $remote -and $null -ne $ownBody.health -and $null -ne $remote.health -and [Math]::Abs($remote.health-$ownBody.health) -lt .01 -and $remote.dead -eq $ownBody.dead
    if($converged){break}
    Start-Sleep -Milliseconds 100
}while([DateTime]::UtcNow -lt $deadline)
$report = @{time=[DateTime]::UtcNow.ToString('o');attacker=$Attacker;expectBlocked=[bool]$ExpectBlocked;before=$before.localHealth;after=$ownBody.health;remoteHealth=$remote.health;victim=$after;attacking=$observed;passed=$false}
$report.passed=$converged -and $ownBody.health -lt $before.localHealth
if($ExpectBlocked) {
    $report.passed=$converged -and [Math]::Abs($ownBody.health-$before.localHealth) -lt .01 -and !$ownBody.dead -and @($after.damageTrace|Where-Object {$_.time -ge $before.time -and $_.victim -eq $victimId -and !$_.allowed -and $_.damager -like 'Sword*'}).Count -gt 0
}
$prefix=if($ExpectBlocked){'friendly-fire'}else{'physical-hit'}
$report | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $workspace "artifacts\$prefix-$Attacker-report.json")
if($ExpectBlocked) {
    if(!$report.passed){throw 'Native teammate contacts were not observed with unchanged synchronized health.'}
    Write-Output 'PASS: Native sword contacts cannot damage the other co-op player'
    return
}
Write-Output "Native physical hit: defender health $($report.before) -> $($report.after); attacker observes $($report.remoteHealth)"
if ($ownBody.health -ge $before.localHealth) { throw 'The native sword trajectory did not damage the owning player.' }
if (!$report.passed) { throw 'The native combat outcome did not converge.' }
Write-Output 'PASS: A native physical weapon strike damages the owning fighter and replicates its outcome'
