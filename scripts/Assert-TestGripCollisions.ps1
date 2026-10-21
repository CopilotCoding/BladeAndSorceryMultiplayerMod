param([Parameter(Mandatory)][string]$Owner,[Parameter(Mandatory)][string]$Observer,[Parameter(Mandatory)][string]$Entity)
$ErrorActionPreference='Stop'
function Filters($Name){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json (@{op='grip-collisions';entity=$Entity}|ConvertTo-Json -Compress)}
$native=@(Filters $Owner);$replica=@(Filters $Observer)
if(!$native.Count -or $native.Count -ne $replica.Count){throw 'Native and replicated fighter must hold the same equipment for collision-filter comparison'}
foreach($hand in $native){
    $other=@($replica|Where-Object {$_.side -eq $hand.side -and $_.item -eq $hand.item})[0]
    if(!$other){throw "Replica is missing the native $($hand.side) weapon grip"}
    foreach($part in $hand.parts){
        $copy=@($other.parts|Where-Object key -eq $part.key)[0]
        if(!$copy -or !$part.pairs.Count -or $part.pairs.Count -ne $copy.pairs.Count){throw "Missing native collider pairs for $($hand.side) weapon / $($part.type)"}
        if(@(Compare-Object @($part.pairs) @($copy.pairs)).Count){throw "Replica collision filter differs from native $($hand.side) weapon / $($part.type) key $($part.key)"}
    }
}
@{entity=$Entity;hands=$native.Count;matched=$true}
