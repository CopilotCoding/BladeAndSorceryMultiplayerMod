param([string[]]$Names = @('host','client'))
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$testExecutable = [System.IO.Path]::GetFullPath((Join-Path $workspace '.runtime\game\BladeAndSorcery.exe'))
foreach ($instanceName in $Names) {
    if ($instanceName -notmatch '^[a-zA-Z0-9_]+$') { throw 'Invalid test instance name' }
    $recordPath = Join-Path $workspace "artifacts\runs\$instanceName\process.json"
    if (!(Test-Path -LiteralPath $recordPath)) { continue }
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
    $testProcessId = [int]$record.pid
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$testProcessId" -ErrorAction SilentlyContinue
    if ($null -eq $process) { continue }
    if ($process.ExecutablePath -ne $testExecutable -or $process.CommandLine -notlike '*-mp-test-dir*') { throw "Refusing to stop unrelated process $testProcessId" }
    Stop-Process -Id $testProcessId -ErrorAction Stop
    Write-Output "Stopped test instance $instanceName ($testProcessId)"
}
