param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$results = [System.Collections.Generic.List[object]]::new()
function Send([string]$Instance, [hashtable]$Command) { & (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Instance -Json ($Command | ConvertTo-Json -Compress -Depth 12) }
function State([string]$Instance) { Send $Instance @{op='status'} }
function Wait-For([scriptblock]$Condition, [string]$Test, [int]$Timeout = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $Condition) { $results.Add(@{test=$Test;passed=$true}); Write-Output "PASS: $Test"; return }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out: $Test"
}
try {
    foreach ($owner in @('host','client')) {
        $observer = if ($owner -eq 'host') {'client'} else {'host'}
        $before = State $owner
        if (!$before.connected -or $before.peers -ne 2) { throw 'Start a connected arena pair first.' }
        $id = @($before.entities | Where-Object { $_.kind -eq 'Player' -and $_.owner -eq $before.peer })[0].id
        Send $owner @{op='damage';amount=9} | Out-Null
        Wait-For {
            $local = State $owner; $remote = @((State $observer).entities | Where-Object id -eq $id)[0]
            $null -ne $remote -and [Math]::Abs($local.localHealth - ($before.localHealth - 9)) -lt .01 -and [Math]::Abs($remote.health - $local.localHealth) -lt .01
        } "$owner native damage applies once and replicates"
        $slice = Send $owner @{op='slice'}
        if (!$slice.sliced) { throw "$owner native hand slice failed" }
        Wait-For { @((State $observer).entities | Where-Object id -eq $id)[0].sliced -contains 32 } "$owner native dismemberment replicates"
        $previousScore=@((State 'host').match.scores|Where-Object peer -eq $before.peer)[0]
        $previousDeaths=if($previousScore){[int]$previousScore.deaths}else{0}
        Send $owner @{op='kill'} | Out-Null
        Wait-For { @((State $observer).entities | Where-Object id -eq $id)[0].dead -eq $true } "$owner death replicates"
        Wait-For {
            $h=@((State 'host').match.scores|Where-Object peer -eq $before.peer)[0]
            $c=@((State 'client').match.scores|Where-Object peer -eq $before.peer)[0]
            $h -and $c -and $h.deaths -eq ($previousDeaths+1) -and $c.deaths -eq $h.deaths
        } "$owner death is scored exactly once on both peers"
        Wait-For {
            $local = State $owner; $remote = @((State $observer).entities | Where-Object { $_.kind -eq 'Player' -and $_.owner -eq $before.peer })
            $current = @($local.entities | Where-Object { $_.kind -eq 'Player' -and $_.owner -eq $before.peer })
            $current.Count -eq 1 -and $remote.Count -eq 1 -and $current[0].id -ne $id -and $remote[0].id -eq $current[0].id -and !$remote[0].dead -and $local.localHealth -gt 0 -and $remote[0].sliced.Count -eq 0
        } "$owner respawns with one intact native body on both peers"
    }
} catch {
    $results.Add(@{test='Run completion';passed=$false;error=$_.ToString()}); throw
} finally {
    @{time=[DateTime]::UtcNow.ToString('o');tests=$results} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $workspace 'artifacts\combat-lifecycle-report.json')
}
