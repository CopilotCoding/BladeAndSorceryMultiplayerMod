param([switch]$RequireVerified,[switch]$TestsSkippedByUser,[switch]$RequireSharedWorld,[switch]$RequireModCompatibility,[switch]$RequireTeleport)
$ErrorActionPreference='Stop'
if($RequireVerified -and $TestsSkippedByUser){throw 'Choose verified packaging or explicitly skipped checks, not both.'}
$workspace=Split-Path -Parent $PSScriptRoot
$source=Join-Path $workspace 'artifacts\mod\BladeAndSorceryMultiplayer'
$required=@('manifest.json','README.md','COMPATIBILITY.md','THIRD_PARTY_NOTICES.md','BladeAndSorcery.Multiplayer.dll','BladeAndSorcery.Multiplayer.Core.dll','LiteNetLib.dll','0Harmony.dll','licenses\Harmony.txt','licenses\LiteNetLib.txt')
$allowed=$required+@('BladeAndSorcery.Multiplayer.pdb','BladeAndSorcery.Multiplayer.Core.pdb')
foreach($name in $required){if(!(Test-Path -LiteralPath (Join-Path $source $name))){throw "Missing packaged file: $name"}}
foreach($file in Get-ChildItem -LiteralPath $source -File -Recurse){
    $relative=[IO.Path]::GetRelativePath($source,$file.FullName)
    if($relative -notin $allowed){throw "Unexpected distribution file: $relative"}
}
Copy-Item -LiteralPath (Join-Path $workspace 'mod\README.md') -Destination (Join-Path $source 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $workspace 'mod\COMPATIBILITY.md') -Destination (Join-Path $source 'COMPATIBILITY.md') -Force
Copy-Item -LiteralPath (Join-Path $workspace 'mod\manifest.json') -Destination (Join-Path $source 'manifest.json') -Force
$modMvid=[Reflection.Assembly]::LoadFile((Join-Path $source 'BladeAndSorcery.Multiplayer.dll')).ManifestModule.ModuleVersionId.ToString('N')
$coreMvid=[Reflection.Assembly]::LoadFile((Join-Path $source 'BladeAndSorcery.Multiplayer.Core.dll')).ManifestModule.ModuleVersionId.ToString('N')
$verification=$null
$steamVerification=$null
$sessionVerification=$null
$focusedVerification=@{}
foreach($focusedName in @('shared-world','native-pve','session-ui','native-ranged','mod-compatibility','teleport')){
    $focusedPath=Join-Path $workspace "artifacts\$focusedName-report.json"
    if(!(Test-Path -LiteralPath $focusedPath)){continue}
    $focused=Get-Content -LiteralPath $focusedPath -Raw|ConvertFrom-Json
    if($focused.passed -and $focused.build.mod -eq $modMvid -and $focused.build.core -eq $coreMvid){
        $focusedCount=if($focused.checks){$focused.checks.Count}else{$focused.tests.Count}
        $focusedVerification[$focusedName]=@{time=$focused.time;checks=$focusedCount}
    }
}
if($RequireSharedWorld){foreach($focusedName in @('shared-world','native-pve','session-ui','native-ranged')){if(!$focusedVerification.ContainsKey($focusedName)){throw "Passing $focusedName report must match both packaged binaries."}}}
if($RequireModCompatibility -and !$focusedVerification.ContainsKey('mod-compatibility')){throw 'Passing mod-compatibility report must match both packaged binaries.'}
if($RequireTeleport -and !$focusedVerification.ContainsKey('teleport')){throw 'Passing native teleport report must match both packaged binaries.'}
$steamReportPath=Join-Path $workspace 'artifacts\steam-capability-report.json'
if(Test-Path -LiteralPath $steamReportPath){
    $steam=Get-Content -LiteralPath $steamReportPath -Raw|ConvertFrom-Json
    if($steam.passed -and $steam.mod -eq $modMvid -and $steam.core -eq $coreMvid){$steamVerification=@{time=$steam.time;checks=$steam.checks.Count;singleAccount=$true;multiplayerVerified=$false;relayVerified=$false}}
}
$sessionReportPath=Join-Path $workspace 'artifacts\steam-session-checks.json'
if(Test-Path -LiteralPath $sessionReportPath){
    $sessionChecks=Get-Content -LiteralPath $sessionReportPath -Raw|ConvertFrom-Json
    if($sessionChecks.passed -and $sessionChecks.coreMvid -eq $coreMvid){$sessionVerification=@{time=$sessionChecks.time;checks=$sessionChecks.count;steamBackendVerified=$false}}
}
$practiceVerification=$null
$fastVerification=$null
$neckVerification=$null
if($RequireVerified){
    $report=Get-Content -LiteralPath (Join-Path $workspace 'artifacts\pvp-regression-report.json') -Raw|ConvertFrom-Json
    if(!$report.passed -or $report.build.mod -ne $modMvid -or $report.build.core -ne $coreMvid){throw 'The latest passing regression must match both packaged assemblies. Inspect the report before distributing this build.'}
    $verification=@{time=$report.time;scenarios=$report.scenarios.Count;game=$report.build.game;resumedFrom=$report.resumedFrom;retainedScenarios=@($report.scenarios|Where-Object reusedFrom).Count}
    $practice=Get-Content -LiteralPath (Join-Path $workspace 'artifacts\practice-validation-report.json') -Raw|ConvertFrom-Json
    if(!$practice.passed -or $practice.build.mod -ne $modMvid -or $practice.build.core -ne $coreMvid){throw 'The passing practice report must also match both packaged assemblies.'}
    $practiceVerification=@{time=$practice.time;checks=$practice.tests.Count}
    $fast=Get-Content -LiteralPath (Join-Path $workspace 'artifacts\weapon-combat-fast-report.json') -Raw|ConvertFrom-Json
    if(!$fast.passed -or !$fast.fast -or $fast.neckConsistencyVersion -ne 2 -or $fast.neckSwingsPerCase -lt 3 -or $fast.build.mod -ne $modMvid -or $fast.build.core -ne $coreMvid -or $fast.scenarios.Count -ne 12){throw 'The complete passing fast-weapon matrix with every neck swing required must match both packaged assemblies.'}
    if($fast.latency -ne $report.latency -or $fast.loss -ne $report.loss){throw 'Fast combat verification must use the same reported latency and loss.'}
    $fastVerification=@{time=$fast.completed;checks=$fast.scenarios.Count}
    $neck=Get-Content -LiteralPath (Join-Path $workspace 'artifacts\neck-reliability-report.json') -Raw|ConvertFrom-Json
    $expectedNeckCases=@(foreach($role in @('host','client')){foreach($weapon in @('SwordShortCommon','SwordLongCommon','SwordGreatZweihander','SwordGreatCommon','SwordRaktaKatana','SwordGreatExecution')){"$role/$weapon"}})
    $actualNeckCases=@($neck.cases|ForEach-Object {"$($_.role)/$($_.weapon)"})
    if(!$neck.passed -or !$neck.everySwingRequired -or $neck.swingsPerCase -lt 3 -or $neck.build.mod -ne $modMvid -or $neck.build.core -ne $coreMvid -or
        $neck.cases.Count -ne 12 -or @(Compare-Object ($expectedNeckCases|Sort-Object) ($actualNeckCases|Sort-Object)).Count -gt 0 -or
        @($neck.cases|Where-Object {!$_.passed -or $_.cuts -ne $_.swings -or $_.swings -lt 3}).Count -gt 0){throw 'All live neck swings across six swords in both directions must pass on the packaged binaries.'}
    $neckVerification=@{time=$neck.completed;cases=$neck.cases.Count;swings=$neck.totalSwings;cuts=$neck.totalCuts}
    if($neck.latency -ne $report.latency -or $neck.loss -ne $report.loss){throw 'Neck consistency verification must use the same reported latency and loss.'}
}
$manifest=Get-Content -LiteralPath (Join-Path $source 'manifest.json') -Raw|ConvertFrom-Json
if($manifest.ModVersion -notmatch '^\d+\.\d+\.\d+$'){throw 'Expected a numeric mod version.'}
$guide=Join-Path $workspace 'artifacts\START-HERE.txt'
Copy-Item -LiteralPath (Join-Path $workspace 'mod\START-HERE.txt') -Destination $guide -Force
if(!(Get-Content -LiteralPath $guide -Raw).Contains("v$($manifest.ModVersion)")){throw 'The quick-start guide must match the packaged version.'}
$playerReadme=Join-Path $workspace 'artifacts\README.md'
Copy-Item -LiteralPath (Join-Path $workspace 'mod\README.md') -Destination $playerReadme -Force
if(!(Get-Content -LiteralPath $playerReadme -Raw).Contains("v$($manifest.ModVersion)")){throw 'The player README must match the packaged version.'}
$zip=Join-Path $workspace "artifacts\BladeAndSorceryMultiplayer-v$($manifest.ModVersion)-PCVR.zip"
$buildInfo=Join-Path $workspace 'artifacts\BUILD-INFO.txt'
$focusedEvidence=($focusedVerification.Keys|Sort-Object|ForEach-Object{"$_ : $($focusedVerification[$_].checks) passing checks on these exact binaries; $($focusedVerification[$_].time)."}) -join "`r`n"
$steamEvidence=if($steamVerification){"Steam API capability: $($steamVerification.checks) checks on one account using a private lobby; lobby creation, P2P listener, search request and cleanup. Actual cross-account multiplayer and relay connectivity remain UNVERIFIED."}else{'No matching single-account Steam API capability report for these binaries.'}
$sessionEvidence=if($sessionVerification){"Session router: $($sessionVerification.checks) focused networking checks, including password/replay rejection and real Direct IP loopback. These do not verify the Steam backend or VR combat."}else{'No matching focused session-router report for these binaries.'}
$evidence=if($verification){"PvP: $($verification.scenarios) passing scenarios; $($verification.retainedScenarios) retained from the same binaries/processes/settings. Report completed $(([DateTime]$verification.time).ToUniversalTime().ToString('o')).`r`nSimulated combat latency: $($report.latency) ms; packet loss: $($report.loss)%.`r`nFast weapon matrix: $($fastVerification.checks) passing checks; $(([DateTime]$fastVerification.time).ToUniversalTime().ToString('o')).`r`nNeck consistency: $($neckVerification.cuts)/$($neckVerification.swings) live swings across $($neckVerification.cases) cases.`r`nPractice helper: $($practiceVerification.checks) passing checks; $(([DateTime]$practiceVerification.time).ToUniversalTime().ToString('o'))."}elseif($TestsSkippedByUser){'Broad PvP, practice and expanded sword/neck regression suites remain stopped at user request. Focused checks completed for this update are listed separately below.'}else{'Broad automated verification was not required when creating this archive.'}
@"
Blade & Sorcery Multiplayer v$($manifest.ModVersion) - PCVR DEVELOPMENT BUILD
Mod build identity: $modMvid
Protocol build identity: $coreMvid
Mod DLL SHA256: $((Get-FileHash -LiteralPath (Join-Path $source 'BladeAndSorcery.Multiplayer.dll')).Hash)

$evidence

$sessionEvidence
$steamEvidence
$focusedEvidence

Target game: PCVR 1.3.2.261012003. Focused shared-world coverage: Home, Arena,
Canyon, Market, Citadel. Native PvE coverage and menu checks are listed above
only when their reports match both binaries. Finite-wave progression uses scripted
native kills. Combat fixture enemies receive test-only extra health against incidental
mock-controller contacts; player damage uses native data and actual weapon contacts.
The broad PvP/sword-cut regression suite remains stopped at user request.
These checks do not establish every map/procedural dungeon, full base-game parity,
cross-account Steam/relay networking, or real Index/Quest-PCVR network combat feel.
Read START-HERE.txt for quick setup or README.md for the full player guide.
The same player guide is also inside the mod folder. Game files are not included.
"@|Set-Content -LiteralPath $buildInfo
Compress-Archive -LiteralPath @($source,$guide,$playerReadme,$buildInfo) -DestinationPath $zip -Force
# Inspect the actual archive, including its top-level installation guide.
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try{
    $entries=@($archive.Entries|Where-Object {!$_.FullName.EndsWith('/')}|ForEach-Object {$_.FullName.Replace('\','/')})
    $expected=@('START-HERE.txt','README.md','BUILD-INFO.txt')+@($allowed|Where-Object {Test-Path -LiteralPath (Join-Path $source $_)}|ForEach-Object {'BladeAndSorceryMultiplayer/'+$_.Replace('\','/')})
    if(@(Compare-Object ($expected|Sort-Object) ($entries|Sort-Object)).Count){throw 'Archive contents differ from the approved mod files and quick-start guide.'}
}finally{$archive.Dispose()}
$hashes=@{}
foreach($name in $required|Where-Object {$_ -like '*.dll'}){$hashes[$name]=(Get-FileHash -LiteralPath (Join-Path $source $name) -Algorithm SHA256).Hash}
@{time=[DateTime]::UtcNow.ToString('o');version=$manifest.ModVersion;modMvid=$modMvid;coreMvid=$coreMvid;testsSkippedByUser=[bool]$TestsSkippedByUser;focusedVerification=$focusedVerification;verification=$verification;steamVerification=$steamVerification;sessionVerification=$sessionVerification;practiceVerification=$practiceVerification;fastVerification=$fastVerification;neckVerification=$neckVerification;archive=$zip;sha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash;assemblies=$hashes}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $workspace 'artifacts\package-info.json')
Write-Output "Created development mod archive: $zip"
