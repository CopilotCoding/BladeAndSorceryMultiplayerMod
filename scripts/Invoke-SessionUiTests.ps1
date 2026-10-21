param()
$ErrorActionPreference='Stop'
$uiRoot=Split-Path -Parent $PSScriptRoot
$tests=[Collections.Generic.List[object]]::new();$passed=$false;$build=$null
function Send($n,$c){& "$PSScriptRoot/Send-TestCommand.ps1" -Name $n -Json ($c|ConvertTo-Json -Depth 8 -Compress)}
function State($n){Send $n @{op='status'}}
function Pass($s){$tests.Add(@{test=$s;passed=$true});Write-Output "PASS: $s"}
function Wait-Ui($condition,$reason){$until=[DateTime]::UtcNow.AddSeconds(25);do{if(&$condition){Pass $reason;return};Start-Sleep -Milliseconds 150}while([DateTime]::UtcNow -lt $until);throw "Timed out: $reason"}
try{
 $s=State host;$build=@{mod=$s.modBuild;core=$s.coreBuild}
 foreach($n in @('host','client')){Send $n @{op='disconnect'}|Out-Null;Send $n @{op='menu';shown=$true;mods=$true}|Out-Null}
 $options=Send host @{op='mod-options'}
 $tabs=@('Session','Steam','Direct IP','Player & hosting','Play','Diagnostics')
 $page=@($options|Where-Object name -eq 'Multiplayer tab')[0]
 if(($page.values -join '|') -ne ($tabs -join '|')){throw 'Native tabs do not match the six intended pages'}
 if(@($options|Where-Object {$_.name -notin @('Multiplayer tab','Connection status') -and $_.category -notin $tabs}).Count){throw 'An option is missing its proper tab'}
 Pass 'Every multiplayer setting belongs to one of six native menu pages'
 foreach($label in @('Host Direct IP','Join Direct IP','Leave session','Respawn player','Experimental sword wave','Edit host IP / hostname','Paste IP from clipboard','Edit UDP port','Edit player name','Edit server password','Host Steam lobby','Join selected Steam lobby','Join invited Steam lobby')){
  $o=@($options|Where-Object name -eq $label)
  if($o.Count -ne 1 -or $o[0].interaction -ne 'ButtonList' -or $o[0].values.Count -ne 1 -or $o[0].applyOnLoad -or $o[0].saveValue){throw "Unsafe/ambiguous native action: $label"}
 }
 Pass 'Actions are individually labeled native buttons and never execute on menu load'
 for($i=0;$i -lt $tabs.Count;$i++){
  Send host @{op='mod-action';name='Multiplayer tab';index=$i}|Out-Null
  $expectedTab=$tabs[$i]
  Wait-Ui {$o=Send host @{op='mod-options'};@($o|Where-Object {$_.visible -ne ($_.name -in @('Multiplayer tab','Connection status') -or $_.category -eq $expectedTab)}).Count -eq 0} "Only $expectedTab settings appear beside persistent tab/status controls"
 }
 Send host @{op='mod-action';name='Multiplayer tab';index=2}|Out-Null
 Send client @{op='mod-action';name='Multiplayer tab';index=2}|Out-Null
 Send host @{op='mod-action';name='Host Direct IP'}|Out-Null
 Send client @{op='mod-action';name='Join Direct IP'}|Out-Null
 Wait-Ui {(State host).peers -eq 2 -and (State client).peers -eq 2} 'Direct IP tab connects both players with one native button each'
 Wait-Ui {$o=Send host @{op='mod-options'};@($o|Where-Object name -eq 'Connection status')[0].renderedText -like 'Hosting | 2 players*'} 'Persistent native status shows current connection and player count'
 Send host @{op='mod-action';name='Multiplayer tab';index=4}|Out-Null
 Send host @{op='mod-action';name='Friendly fire (host)';index=1}|Out-Null
 Wait-Ui {(State client).match.friendlyFire} 'Play tab shared friendly-fire toggle reaches the client'
 Send host @{op='mod-action';name='Friendly fire (host)';index=0}|Out-Null
 Send host @{op='screenshot'}|Out-Null
 foreach($n in @('host','client')){Send $n @{op='mod-action';name='Multiplayer tab';index=0}|Out-Null}
 Send client @{op='mod-action';name='Leave session'}|Out-Null
 Wait-Ui {!(State client).connected -and (State host).peers -eq 1} 'Session tab Leave disconnects one player cleanly'
 $passed=$true
}catch{$tests.Add(@{test='Completion';passed=$false;error=$_.ToString()});throw}
finally{
 foreach($n in @('host','client')){try{Send $n @{op='menu';shown=$false}|Out-Null}catch{}}
 @{time=[DateTime]::UtcNow.ToString('o');passed=$passed;build=$build;tests=$tests}|ConvertTo-Json -Depth 8|Set-Content "$uiRoot/artifacts/session-ui-report.json"
}
