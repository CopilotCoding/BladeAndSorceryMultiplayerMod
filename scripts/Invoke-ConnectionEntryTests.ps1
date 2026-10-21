param()
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
$tests=[System.Collections.Generic.List[object]]::new();$passed=$false;$original=@{}
function Send($Name,$Command){& (Join-Path $PSScriptRoot 'Send-TestCommand.ps1') -Name $Name -Json ($Command|ConvertTo-Json -Depth 9 -Compress)}
function State($Name){Send $Name @{op='status'}}
function Wait-For($Condition,$Reason){$deadline=[DateTime]::UtcNow.AddSeconds(20);do{if(&$Condition){return};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline);throw "Timed out: $Reason"}
function Pass($Reason){$tests.Add(@{test=$Reason;passed=$true});Write-Output "PASS: $Reason"}
function Open-Editor($Name,$Label,$Field){
    Send $Name @{op='menu';shown=$true;mods=$true}|Out-Null
    Send $Name @{op='mod-action';name=$Label}|Out-Null
    Wait-For {$e=(State $Name).connectionEditor;$e.visible -and !$e.loading -and $e.field -eq $Field -and $e.keys.Count -gt 30} "$Name native $Field keyboard"
}
function Key($Name,$Action){
    $key=@((State $Name).connectionEditor.keys|Where-Object action -eq $Action)[0]
    if(!$key){throw "Missing native keyboard action $Action"}
    Send $Name @{op='keyboard-input';key=$key.id}|Out-Null
}
function Type-Keys($Name,[string]$Text){
    Send $Name @{op='keyboard-input';clear=$true}|Out-Null
    foreach($character in $Text.ToCharArray()){
        $key=@((State $Name).connectionEditor.keys|Where-Object {($_.action -eq 'OutputLabel' -and $_.label -ceq [string]$character) -or ($_.action -eq 'Output' -and $_.arg -ceq [string]$character)})[0]
        if(!$key){throw "Native keyboard has no key for $character"}
        Send $Name @{op='keyboard-input';key=$key.id}|Out-Null
    }
    if((State $Name).connectionEditor.text -cne $Text){throw "Native key presses did not produce $Text"}
}
try{
    foreach($name in @('host','client')){
        Wait-For {(State $name).creature} "$name native player ready"
        Send $name @{op='disconnect'}|Out-Null
        $original[$name]=(State $name).connectionSettings
        Send $name @{op='teleport';position=@(0,.08,0);yaw=0}|Out-Null
        Send $name @{op='pose';head=@(0,1.75,0);headRotation=@(35,0,0);left=@(-.3,1.3,.4);leftRotation=@(-55,0,0);right=@(.3,1.3,.4);rightRotation=@(-55,0,0)}|Out-Null
    }
    Open-Editor client 'Edit host IP / hostname' address
    if((State client).connectionEditor.text -ne $original.client.address){throw 'Keyboard did not prefill the saved host address'}
    Type-Keys client '127.0.0.1'
    Send client @{op='screenshot'}|Out-Null
    Key client Confirm
    Wait-For {!((State client).connectionEditor.visible)} 'Confirmed IP editor closes'
    if((State client).connectionSettings.address -ne '127.0.0.1'){throw 'Confirmed native IP entry was not applied'}
    Pass 'Native VR digit/dot keys enter and save a host IP'
    Open-Editor client 'Edit UDP port' port
    if((State client).connectionEditor.text -ne [string]$original.client.port){throw 'Port editor did not prefill the saved port'}
    Type-Keys client '99999';Key client Confirm
    $s=State client
    if(!$s.connectionEditor.visible -or $s.connectionSettings.port -ne $original.client.port){throw 'Invalid port changed settings or closed the editor'}
    Pass 'Invalid UDP port stays editable and leaves the saved port unchanged'
    Key client Cancel
    Wait-For {!((State client).connectionEditor.visible)} 'Cancelled editor closes'
    if((State client).connectionSettings.port -ne $original.client.port){throw 'Cancel changed the saved port'}
    Pass 'Native Cancel keeps previous connection settings'
    foreach($name in @('host','client')){
        Open-Editor $name 'Edit UDP port' port
        Type-Keys $name '19051';Key $name Confirm
        Wait-For {(State $name).connectionSettings.port -eq 19051} "$name changed UDP port"
    }
    Wait-For { @((Send client @{op='mod-options'})|Where-Object name -eq 'Server address')[0].text -eq '127.0.0.1:19051' } 'Native menu shows destination'
    Pass 'Native menu displays the edited host address and UDP port'
    Send host @{op='test-clipboard';text=' 127.0.0.1 ' }|Out-Null
    Send client @{op='mod-action';name='Paste IP from clipboard'}|Out-Null
    $pasted=State client
    if($pasted.connectionSettings.address -ne '127.0.0.1' -or $pasted.connectionSettings.port -ne 19051 -or $pasted.connected){throw 'Clipboard IP did not trim/save while preserving the port and offline state'}
    Pass 'Native clipboard button pastes a desktop IP and preserves the saved UDP port'
    foreach($invalid in @('','https://example.com:19051/path','127.0.0.1:99999')){
        Send host @{op='test-clipboard';text=$invalid}|Out-Null
        Send client @{op='mod-action';name='Paste IP from clipboard'}|Out-Null
        $rejected=State client
        if($rejected.connectionSettings.address -ne '127.0.0.1' -or $rejected.connectionSettings.port -ne 19051){throw 'Invalid clipboard text changed saved connection settings'}
    }
    Pass 'Empty clipboard, URLs and invalid pasted ports preserve the previous destination'
    foreach($endpoint in @(@{text='[::1]:19052';address='::1'},@{text='localhost:19052';address='localhost'},@{text='127.0.0.1:19052';address='127.0.0.1'})){
        Send host @{op='test-clipboard';text=$endpoint.text}|Out-Null
        Send client @{op='mod-action';name='Paste IP from clipboard'}|Out-Null
        $parsed=State client
        if($parsed.connectionSettings.address -ne $endpoint.address -or $parsed.connectionSettings.port -ne 19052){throw 'Pasted address:port was not parsed correctly'}
    }
    Pass 'Clipboard accepts IP:port, hostname:port and bracketed IPv6 endpoints'
    Send host @{op='test-clipboard';text='127.0.0.1:19051'}|Out-Null
    Send client @{op='mod-action';name='Paste IP from clipboard'}|Out-Null
    Open-Editor client 'Edit server password' password
    if(!(State client).connectionEditor.passwordMode){throw 'Password keyboard is not masked'}
    Send client @{op='keyboard-input';text='cancelled-test-password'}|Out-Null
    Key client Cancel
    if((State client).connectionSettings.passwordSet -ne $original.client.passwordSet){throw 'Cancelled password changed settings'}
    Pass 'Password entry uses native masking and Cancel preserves the saved password'
    Open-Editor client 'Edit host IP / hostname' address
    Send client @{op='menu';shown=$false}|Out-Null
    Wait-For {$e=(State client).connectionEditor;!$e.visible -and !$e.loading -and !$e.field} 'Closing menu removes keyboard'
    Pass 'Closing the native menu cleans up its connection keyboard'
    foreach($name in @('host','client')){Send $name @{op='menu';shown=$false}|Out-Null}
    Send host @{op='mod-action';name='Host session'}|Out-Null
    Send client @{op='mod-action';name='Join saved server'}|Out-Null
    Wait-For {(State host).peers -eq 2 -and (State client).peers -eq 2} 'Actual native menu join on changed port'
    Pass 'Host and Join saved server establish a real UDP session using the pasted destination'
    Send host @{op='test-clipboard';text='127.0.0.1:19052'}|Out-Null
    Send client @{op='mod-action';name='Paste IP from clipboard'}|Out-Null
    if((State client).connectionSettings.port -ne 19051 -or !(State client).connected){throw 'Paste changed the destination of a live session'}
    Pass 'Clipboard entry cannot change an active session'
    $passed=$true
}catch{$tests.Add(@{passed=$false;error=$_.ToString()});throw}
finally{
    try{Send host @{op='test-clipboard';restore=$true}|Out-Null}catch{}
    foreach($name in @('host','client')){
        try{
            Send $name @{op='disconnect'}|Out-Null
            if($original.ContainsKey($name)){
                Open-Editor $name 'Edit UDP port' port
                Send $name @{op='keyboard-input';text=[string]$original[$name].port}|Out-Null;Key $name Confirm
                Open-Editor $name 'Edit host IP / hostname' address
                Send $name @{op='keyboard-input';text=$original[$name].address}|Out-Null;Key $name Confirm
            }
            Send $name @{op='menu';shown=$false}|Out-Null
        }catch{$tests.Add(@{test="$name test-settings cleanup";passed=$false;error=$_.ToString()});$passed=$false}
    }
    @{time=[DateTime]::UtcNow.ToString('o');passed=$passed;tests=$tests}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\connection-entry-report.json')
}
