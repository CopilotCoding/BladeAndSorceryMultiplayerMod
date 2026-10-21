param([string]$Name, [string]$Json, [int]$TimeoutSeconds = 20)
$ErrorActionPreference = 'Stop'
if ($Name -notmatch '^[a-zA-Z0-9_]+$') { throw 'Invalid instance name' }
$workspace = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $workspace "artifacts\runs\$Name"
$commands = Join-Path $runRoot 'commands'
if (!(Test-Path -LiteralPath $commands)) { throw 'Test command endpoint not ready' }
$command = $Json | ConvertFrom-Json
$filename = [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfffffff') + '_' + [Guid]::NewGuid().ToString('N') + '.json'
$target = Join-Path $commands $filename
$temporary = $target + '.tmp'
$command | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary
Move-Item -LiteralPath $temporary -Destination $target
$response = Join-Path (Join-Path $runRoot 'responses') $filename
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
while ([DateTime]::UtcNow -lt $deadline) {
    if (Test-Path -LiteralPath $response) {
        $result = Get-Content -LiteralPath $response -Raw | ConvertFrom-Json
        if (!$result.success) { throw ($result.result | Out-String) }
        return $result.result
    }
    Start-Sleep -Milliseconds 100
}
throw "Command timed out: $Json (instance $Name)"
