param([switch]$SkipLaunch,[switch]$KeepRunning)
$ErrorActionPreference='Stop'
$worldRoot=Split-Path -Parent $PSScriptRoot
$worldChecks=[Collections.Generic.List[object]]::new()
$worldBuild=$null;$worldPassed=$false
function Send([string]$Name,[hashtable]$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 12 -Compress)}
function State([string]$Name){Send $Name @{op='status'}}
function Pass([string]$Text){$worldChecks.Add(@{test=$Text;passed=$true});Write-Output "PASS: $Text"}
function Wait-World([scriptblock]$Condition,[string]$Text,[int]$Seconds=90){
    $worldDeadline=[DateTime]::UtcNow.AddSeconds($Seconds)
    do {if(&$Condition){Pass $Text;return};Start-Sleep -Milliseconds 300}while([DateTime]::UtcNow -lt $worldDeadline)
    throw "Timed out: $Text"
}
function Ready([string]$Name,[string]$Map){$s=State $Name;$s.connected -and $s.level -eq $Map -and $s.scene.ready -and !$s.scene.loading -and @($s.entities|Where-Object kind -eq Player).Count -ge 2}
function Distance($A,$B){[Math]::Sqrt([Math]::Pow($A[0]-$B[0],2)+[Math]::Pow($A[1]-$B[1],2)+[Math]::Pow($A[2]-$B[2],2))}
function Same-World([string[]]$Names){
    $a=State $Names[0];$ids=@($a.entities.id|Sort-Object)
    foreach($name in $Names|Select-Object -Skip 1){$b=State $name;if($a.scene.revision -ne $b.scene.revision -or $a.scene.seed -ne $b.scene.seed -or $a.level -ne $b.level -or @(Compare-Object $ids @($b.entities.id|Sort-Object)).Count -gt 0 -or $b.unregisteredItems -ne 0){return $false}}
    $a.unregisteredItems -eq 0
}
try {
    $worldPreviousReport=Join-Path $worldRoot 'artifacts/shared-world-report.json'
    if(Test-Path -LiteralPath $worldPreviousReport){$worldHistory=Join-Path $worldRoot ('artifacts/regression-history/shared-world-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff'));New-Item -ItemType Directory -Path $worldHistory -Force|Out-Null;Copy-Item -LiteralPath $worldPreviousReport -Destination $worldHistory}
    if(!$SkipLaunch){& (Join-Path $PSScriptRoot 'Stop-TestInstances.ps1') -Names @('third');& (Join-Path $PSScriptRoot 'Start-TestPair.ps1') -ReadyTimeoutSeconds 240}
    else {
        foreach($name in @('host','client')){Send $name @{op='disconnect'}|Out-Null;Send $name @{op='travel';level='Arena';mode='Sandbox'}|Out-Null}
        Wait-World {$h=State host;$c=State client;$h.creature -and $c.creature -and $h.level -eq 'Arena' -and $c.level -eq 'Arena' -and !$h.connected -and !$c.connected} 'Offline fixture resets both native maps before the shared-world checks' 150
    }
    $h=State host;$c=State client
    $worldBuild=@{mod=$h.modBuild;core=$h.coreBuild;host=$h.processId;client=$c.processId}
    Send host @{op='host';port=19050}|Out-Null;Send client @{op='join';port=19050}|Out-Null
    Wait-World {(Ready host Arena) -and (Ready client Arena)} 'Initial players join Arena with separate shared spawn slots'
    $characters=@{host=(State host).character;client=(State client).character}
    if(@((State host).localHolders[0].items).Count -eq 0){Send host @{op='equip';item='SwordLongCommon';side='right'}|Out-Null;Send host @{op='holster-held';index=0}|Out-Null}
    Send host @{op='equip';item='SwordShortCommon';side='right'}|Out-Null
    Send client @{op='equip';item='DaggerCommon';side='right'}|Out-Null
    $holderBaseline=(State host).localHolders|ConvertTo-Json -Depth 5 -Compress
    $inventoryBaseline=@{}
    foreach($name in @('host','client')){$inventoryBaseline[$name]=@((State $name).localInventory|Where-Object state -ne ContentStateHolder|Sort-Object referenceID,state|Select-Object referenceID,type,state,quantity)|ConvertTo-Json -Depth 5 -Compress}
    foreach($case in @(@{sender='client';map='Home'},@{sender='host';map='Canyon'},@{sender='client';map='Market'},@{sender='host';map='Citadel'},@{sender='client';map='Home'})) {
        $previous=(State host).scene.revision
        Send $case.sender @{op='travel';level=$case.map;mode='Sandbox'}|Out-Null
        Wait-World {(Ready host $case.map) -and (Ready client $case.map) -and (Same-World @('host','client'))} "Both players share $($case.map), seed, entities and scene readiness" 150
        $h=State host;$c=State client
        if($h.scene.revision -ne ($previous+1)){throw 'One selection produced multiple world commits'}
        if($h.rightItem -ne 'SwordShortCommon' -or $c.rightItem -ne 'DaggerCommon'){throw 'Travel did not restore individual held loadouts'}
        if(($h.localHolders|ConvertTo-Json -Depth 5 -Compress) -ne $holderBaseline){throw 'Holster contents or slots changed during travel'}
        if($h.character -ne $characters.host -or $c.character -ne $characters.client){throw 'A remote character replaced the local save identity'}
        foreach($name in @('host','client')){if((@((State $name).localInventory|Where-Object state -ne ContentStateHolder|Sort-Object referenceID,state|Select-Object referenceID,type,state,quantity)|ConvertTo-Json -Depth 5 -Compress) -ne $inventoryBaseline[$name]){throw "Inventory or worn equipment changed for $name"}}
        if((Distance $h.root $c.root) -lt 1){throw 'Players overlap at arrival'}
        Pass "$($case.map) preserves distinct hand weapons, holsters, personal character IDs and safe separate arrivals"
    }
    $beforeBook=State host;$bookIds=@($beforeBook.entities.id)
    Send client @{op='book-spawn';item='DaggerCommon'}|Out-Null
    Wait-World {$h=State host;$c=State client;@($h.entities|Where-Object {$_.id -notin $bookIds -and $_.kind -eq 'Item' -and $_.catalog -eq 'DaggerCommon' -and $_.owner -eq $c.peer -and $_.held -eq 0}).Count -eq 1 -and (Same-World @('host','client'))} 'Client native item-book Spawn button replicates an unheld world item'
    $h=State host;$c=State client;$bookItem=@($h.entities|Where-Object {$_.id -notin $bookIds -and $_.kind -eq 'Item' -and $_.catalog -eq 'DaggerCommon' -and $_.owner -eq $c.peer})[0]
    $storedBefore=(@($h.localInventory|Where-Object referenceID -eq 'DaggerCommon'|Measure-Object -Property quantity -Sum).Sum)
    Send host @{op='native-store';entity=$bookItem.id}|Out-Null
    Wait-World {$h=State host;$c=State client;@($h.entities|Where-Object id -eq $bookItem.id).Count -eq 0 -and @($c.entities|Where-Object id -eq $bookItem.id).Count -eq 0 -and (@($h.localInventory|Where-Object referenceID -eq 'DaggerCommon'|Measure-Object -Property quantity -Sum).Sum) -eq ($storedBefore+1)} 'Native storage claims a free remote-owned item and removes it once from both worlds'
    $h=State host;$c=State client
    $sharedWeapon=@($h.entities|Where-Object {$_.kind -eq 'Item' -and $_.catalog -eq 'SwordShortCommon' -and $_.owner -eq $h.peer -and $_.held -gt 0})[0]
    if(!$sharedWeapon){throw 'Shared-world ownership fixture has no source weapon'}
    Send host @{op='release';side='right'}|Out-Null
    Send client @{op='release';side='right'}|Out-Null
    Send client @{op='grab-entity';entity=$sharedWeapon.id;side='right'}|Out-Null
    Wait-World {$h=State host;$c=State client;$a=@($h.entities|Where-Object id -eq $sharedWeapon.id)[0];$b=@($c.entities|Where-Object id -eq $sharedWeapon.id)[0];$a.owner -eq $c.peer -and $b.owner -eq $c.peer -and $a.epoch -eq ($sharedWeapon.epoch+1) -and $a.epoch -eq $b.epoch} 'Home item pickup transfers ownership once and retains the same world object ID'
    Send client @{op='release';side='right'}|Out-Null
    foreach($name in @('host','client')){Send $name @{op='observe-item';entity=$sharedWeapon.id}|Out-Null}
    Send client @{op='item-velocity';entity=$sharedWeapon.id;velocity=@(0,2,2)}|Out-Null
    Wait-World {$h=State host;$c=State client;$a=@($h.entities|Where-Object id -eq $sharedWeapon.id)[0];$b=@($c.entities|Where-Object id -eq $sharedWeapon.id)[0];@($h.itemObservation.frames|Where-Object {$_.velocity[2] -gt .5}).Count -gt 0 -and @($c.itemObservation.frames|Where-Object {$_.velocity[2] -gt .5}).Count -gt 0 -and (Distance $a.position $b.position) -lt .35} 'Shared Home object position and flight velocity converge on both peers'
    Send client @{op='delay-scene-ready';seconds=14}|Out-Null
    Send host @{op='travel';level='Canyon';mode='Sandbox'}|Out-Null
    Wait-World {(Ready host Canyon) -and (Ready client Canyon)} 'Slow participant finishes travel without losing the network session' 150
    Send host @{op='travel';level='Market';mode='Sandbox'}|Out-Null
    Send client @{op='travel';level='Citadel';mode='Sandbox'}|Out-Null
    Wait-World {$h=State host;$c=State client;$h.scene.ready -and $c.scene.ready -and $h.level -eq $c.level -and $h.level -in @('Market','Citadel') -and (Same-World @('host','client'))} 'Racing native selections converge on one destination' 150
    $h=State host;$map=$h.level;$revision=$h.scene.revision
    Send client @{op='travel';level='NoSuchMultiplayerMap';mode='Sandbox'}|Out-Null
    $h=State host;$c=State client
    if($h.scene.revision -ne $revision -or $c.scene.revision -ne $revision -or !$h.scene.ready -or !$c.scene.ready){throw 'Missing-map request disrupted the current session'}
    Pass 'Unavailable native map leaves the existing world intact'
    $missingMap=if($map -eq 'Market'){'Canyon'}else{'Market'}
    Send client @{op='map-assets';level=$missingMap;available=$false}|Out-Null
    try {
        Send host @{op='travel';level=$missingMap;mode='Sandbox'}|Out-Null
        Wait-World {$h=State host;$c=State client;$h.scene.status -like '*assets are unavailable*' -and $h.scene.ready -and $c.scene.ready -and $h.level -eq $map -and $c.level -eq $map -and $h.scene.revision -eq $revision} 'Missing client map assets reject travel before either participant unloads'
    } finally {Send client @{op='map-assets';level=$missingMap;available=$true}|Out-Null}
    Send host @{op='delay-scene-ready';seconds=20}|Out-Null
    Send host @{op='travel';level=$missingMap;mode='Sandbox'}|Out-Null
    Wait-World {$h=State host;$h.scene.loaded -and !$h.scene.ready -and $h.level -eq $missingMap} 'Recovery fixture reaches the native arrival phase before releasing readiness' 150
    Send host @{op='map-load-error'}|Out-Null
    Wait-World {(Ready host $map) -and (Ready client $map) -and (Same-World @('host','client'))} 'Native loading error recovers both participants to their previous shared map' 150
    & (Join-Path $PSScriptRoot 'Start-TestInstance.ps1') -Name third
    Wait-World {$thirdRecord=Get-Content (Join-Path $worldRoot 'artifacts/runs/third/process.json') -Raw|ConvertFrom-Json;$third=State third;$third.processId -eq $thirdRecord.pid -and $third.creature} 'Third isolated player loads for late joining' 120
    Send host @{op='delay-scene-ready';seconds=15}|Out-Null
    Send host @{op='travel';level='Home';mode='Sandbox'}|Out-Null
    Wait-World {$h=State host;$h.scene.expected -eq 'Home' -and !$h.scene.ready} 'Host begins shared Home travel before the third player joins'
    Send third @{op='join';port=19050}|Out-Null
    Wait-World {(Ready host Home) -and (Ready client Home) -and (Ready third Home) -and (Same-World @('host','client','third'))} 'Player joining during travel loads shared Home with matching props, equipment and players' 150
    $h=State host;$c=State client;$t=State third
    if((Distance $h.root $t.root) -lt 1 -or (Distance $c.root $t.root) -lt 1){throw 'Late joiner spawn overlaps an existing player'}
    Pass 'Late joiner receives a separate safe spawn on a non-Arena map'
    $preDropIds=@($h.entities.id)
    Send third @{op='book-spawn';item='DaggerCommon'}|Out-Null
    Wait-World {$h=State host;$t=State third;@($h.entities|Where-Object {$_.id -notin $preDropIds -and $_.kind -eq 'Item' -and $_.catalog -eq 'DaggerCommon' -and $_.owner -eq $t.peer}).Count -eq 1} 'Late joiner creates a shared loose native-book item'
    $h=State host;$t=State third;$departingItem=@($h.entities|Where-Object {$_.id -notin $preDropIds -and $_.kind -eq 'Item' -and $_.catalog -eq 'DaggerCommon' -and $_.owner -eq $t.peer})[0]
    Send third @{op='disconnect'}|Out-Null
    Wait-World {$h=State host;$c=State client;$a=@($h.entities|Where-Object id -eq $departingItem.id)[0];$b=@($c.entities|Where-Object id -eq $departingItem.id)[0];$a -and $b -and $a.owner -eq $h.peer -and $b.owner -eq $h.peer -and $a.epoch -eq ($departingItem.epoch+1)} 'Loose shared loot retains its ID and transfers to the host when its owner leaves'
    Send client @{op='travel';level='Home';mode='Sandbox'}|Out-Null
    Wait-World {(Ready host Home) -and (Ready client Home) -and (Same-World @('host','client'))} 'Session returns to shared Home after late joining and departure' 150
    $worldPassed=$true
} catch {$worldChecks.Add(@{test='Completion';passed=$false;error=$_.ToString()});throw}
finally {
    @{time=[DateTime]::UtcNow.ToString('o');passed=$worldPassed;build=$worldBuild;checks=$worldChecks;nativeSwordContactTestsRun=$false}|ConvertTo-Json -Depth 10|Set-Content (Join-Path $worldRoot 'artifacts/shared-world-report.json')
    if(!$KeepRunning){& (Join-Path $PSScriptRoot 'Stop-TestInstances.ps1') -Names @('host','client','third')}
}
