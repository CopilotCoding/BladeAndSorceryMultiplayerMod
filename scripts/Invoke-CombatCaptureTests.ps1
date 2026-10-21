param()
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
function Send($Name,$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 8 -Compress)}
$report=@{time=[DateTime]::UtcNow.ToString('o');passed=$false;captures=@()}
try {
    foreach($name in @('host','client')){
        $s=Send $name @{op='status'}
        if($s.peers -ne 2 -or !$s.connected){throw 'A connected Arena pair is required'}
        Send $name @{op='menu';shown=$true;mods=$true}|Out-Null
        $options=Send $name @{op='mod-options'}
        foreach($label in @('Record combat capture','Save combat capture')){
            $option=@($options|Where-Object name -eq $label)
            if($option.Count -ne 1 -or $option[0].interaction -ne 'ButtonList' -or $option[0].values.Count -ne 1 -or $option[0].applyOnLoad -or $option[0].saveValue){throw "Invalid diagnostic action: $label"}
        }
        Send $name @{op='mod-action';name='Record combat capture'}|Out-Null
        $capture=Send $name @{op='combat-capture-status'}
        if(!$capture.active -or $capture.count -ne 0){throw 'Starting capture did not clear the previous contacts'}
        Send $name @{op='menu';shown=$false}|Out-Null
    }
    & (Join-Path $PSScriptRoot 'Invoke-StabTests.ps1') -Weapon SwordGreatZweihander -TwoHanded -ThrustSeconds .4
    foreach($name in @('host','client')){
        Send $name @{op='mod-action';name='Save combat capture'}|Out-Null
        $capture=Send $name @{op='combat-capture-status'}
        if($capture.active -or !$capture.path -or !(Test-Path -LiteralPath $capture.path)){throw 'Saving did not stop capture and produce a report'}
        $saved=Get-Content -LiteralPath $capture.path -Raw|ConvertFrom-Json
        if($saved.mod -ne (Send $name @{op='status'}).modBuild -or $saved.contacts.Count -eq 0){throw 'Saved capture lacks the current build or native contacts'}
        if(@($saved.contacts|Where-Object {$_.catalog -eq 'SwordGreatZweihander' -and $_.held -eq 2}).Count -eq 0){throw 'Capture did not observe both native sword grips'}
        if(@($saved.contacts|Where-Object phase -eq 'hit').Count -eq 0){throw 'Capture did not observe native hit decisions'}
        $report.captures+=@{peer=$name;path=$capture.path;contacts=$saved.contacts.Count}
        Write-Output "PASS: $name native capture buttons record two-handed combat and save current-build contact decisions"
    }
    foreach($name in @('host','client')){
        foreach($side in @('left','right')){Send $name @{op='release';side=$side}|Out-Null}
        Send $name @{op='clear-loose-items'}|Out-Null
    }
    Start-Sleep -Milliseconds 300
    Send host @{op='mod-action';name='Record combat capture'}|Out-Null
    if((Send host @{op='combat-capture-status'}).count -ne 0){throw 'A repeated recording retained old contact data'}
    Send host @{op='disconnect'}|Out-Null
    $capture=Send host @{op='combat-capture-status'}
    if(!(Test-Path -LiteralPath $capture.path)){throw 'Disconnect did not save an active recording'}
    $saved=Get-Content -LiteralPath $capture.path -Raw|ConvertFrom-Json
    if($saved.contacts.Count -ne 0){throw 'Disconnect saved stale contacts from the previous recording'}
    Write-Output 'PASS: disconnect automatically saves the active capture'
    $report.passed=$true
}catch{$report.error=$_.ToString();throw}
finally{
    foreach($name in @('host','client')){try{Send $name @{op='menu';shown=$false}|Out-Null}catch{}}
    $report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\combat-capture-report.json')
}
