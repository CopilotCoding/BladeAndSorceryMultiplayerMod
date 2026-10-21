param([int]$Latency = 0, [int]$Loss = 0, [switch]$SkipLaunch)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$results = [System.Collections.Generic.List[object]]::new()
function Send([string]$Instance, [hashtable]$Command) {
    & (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Instance -Json ($Command | ConvertTo-Json -Compress -Depth 15)
}
function Status([string]$Instance) { Send $Instance @{op='status'} }
function Wait-Condition([scriptblock]$Condition, [string]$Reason, [int]$Timeout = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
    while ([DateTime]::UtcNow -lt $deadline) { if (& $Condition) { return }; Start-Sleep -Milliseconds 250 }
    throw "Timed out: $Reason"
}
function Assert([bool]$Condition, [string]$Description) {
    $results.Add(@{test=$Description;passed=$Condition})
    if (!$Condition) { throw "FAIL: $Description" }
    Write-Output "PASS: $Description"
}
try {
    if (!$SkipLaunch) { & (Join-Path $PSScriptRoot 'Start-TestPair.ps1') }
    foreach ($name in @('host','client')) { Send $name @{op='disconnect'} | Out-Null }
    $baseline = Status 'host'
    Assert ($baseline.creature -and !$baseline.connected) 'Native player exists with multiplayer inactive'
    Send 'host' @{op='damage';amount=5} | Out-Null
    $damaged = Status 'host'
    Assert ([Math]::Abs($damaged.localHealth - ($baseline.localHealth - 5)) -lt .01) 'Native damage works with multiplayer inactive'
    $anchor = @($baseline.root)
    Send 'host' @{op='teleport';position=$anchor;yaw=0} | Out-Null
    Send 'client' @{op='teleport';position=@($anchor[0],$anchor[1],($anchor[2]+2));yaw=180} | Out-Null
    Send 'host' @{op='host';name='Host';latency=$Latency;loss=$Loss} | Out-Null
    Send 'client' @{op='join';name='Client';latency=$Latency;loss=$Loss} | Out-Null
    Wait-Condition { (Status 'host').peers -eq 2 -and (Status 'client').peers -eq 2 } 'two-peer handshake'
    Wait-Condition { @((Status 'host').entities | Where-Object kind -eq 'Player').Count -eq 2 -and @((Status 'client').entities | Where-Object kind -eq 'Player').Count -eq 2 } 'physical remote players'
    $h = Status 'host'; $c = Status 'client'
    Assert ($h.level -eq $c.level) 'Both instances use the same map'
    Assert ($h.modBuild -eq $c.modBuild -and $h.coreBuild -eq $c.coreBuild) 'Both instances run the exact same mod and protocol binaries'
    $spawnDistance = [Math]::Sqrt([Math]::Pow($h.root[0]-$c.root[0],2)+[Math]::Pow($h.root[2]-$c.root[2],2))
    Assert ($spawnDistance -gt 1.4) 'Joining Arena places the client at a separate native navigation-mesh spawn'
    Assert (@($h.entities | Where-Object { $_.kind -eq 'Player' -and $_.owner -ne $h.peer -and $_.physics -and $_.bodies -ge 15 }).Count -eq 1) 'Host has a native physical client body'
    Assert (@($c.entities | Where-Object { $_.kind -eq 'Player' -and $_.owner -ne $c.peer -and $_.physics -and $_.bodies -ge 15 }).Count -eq 1) 'Client has a native physical host body'
    foreach($source in @($h,$c)){
        $local=@($source.entities|Where-Object {$_.kind -eq 'Player' -and $_.owner -eq $source.peer})[0]
        $other=if($source.peer -eq $h.peer){$c}else{$h}
        $replica=@($other.entities|Where-Object id -eq $local.id)[0]
        Assert ([Math]::Abs($local.height-$replica.height) -lt .005) "Peer $($source.peer) replica preserves calibrated native body height"
    }
    Wait-Condition { (Status 'host').snapshotsReceived -gt 10 -and (Status 'client').snapshotsReceived -gt 10 } 'continuous bidirectional snapshots'
    Assert $true 'Both players publish and receive physical snapshots'
    foreach ($name in @('host','client')) { Send $name @{op='equip';item='SwordShortCommon';side='right'} | Out-Null }
    Wait-Condition { @((Status 'host').entities | Where-Object { $_.catalog -eq 'SwordShortCommon' -and $_.held -gt 0 }).Count -eq 2 -and @((Status 'client').entities | Where-Object { $_.catalog -eq 'SwordShortCommon' -and $_.held -gt 0 }).Count -eq 2 } 'replicated native sword grips'
    Assert $true 'Both swords exist and are held on both instances'
    $before = Status 'client'
    Send 'client' @{op='damage';amount=7} | Out-Null
    Wait-Condition { $local = Status 'client'; $remote = @((Status 'host').entities | Where-Object { $_.kind -eq 'Player' -and $_.owner -eq $local.peer })[0]; [Math]::Abs($remote.health - $local.localHealth) -lt .01 } 'client damage replication'
    $after = Status 'client'
    Assert ([Math]::Abs($after.localHealth - ($before.localHealth - 7)) -lt .01) 'Client damage applies once and replicates to host'
    $h = Status 'host'; $c = Status 'client'
    $h | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\arena-host-state.json')
    $c | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\arena-client-state.json')
} catch {
    $results.Add(@{test='Run completion';passed=$false;error=$_.ToString()})
    throw
} finally {
    @{time=[DateTime]::UtcNow.ToString('o');latency=$Latency;loss=$Loss;tests=$results} | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\arena-test-report.json')
}
