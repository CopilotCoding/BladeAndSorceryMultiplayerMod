#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$version = (Get-Content -LiteralPath (Join-Path $workspace 'mod/manifest.json') -Raw | ConvertFrom-Json).ModVersion
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw 'Invalid source package version.' }
$packageName = "BladeAndSorceryMultiplayer-v$version-Source"
$relativeFiles = [Collections.Generic.List[string]]::new()
foreach ($relative in @('.gitignore','Directory.Build.props','README.md','TODO.txt',
    'docs/BUILD-SOURCE.md','docs/IMPLEMENTATION.md','docs/UPDATE-0.3.md',
    'mod/manifest.json','mod/README.md','mod/START-HERE.txt','mod/THIRD_PARTY_NOTICES.md',
    'mod/licenses/Harmony.txt','mod/licenses/LiteNetLib.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $workspace $relative) -PathType Leaf)) { throw "Missing source file: $relative" }
    $relativeFiles.Add($relative)
}
foreach ($directory in @('src','tools','tests','scripts')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $workspace $directory) -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($workspace, $file.FullName).Replace('\','/')
        if ($relative -match '/(?:bin|obj)/') { continue }
        $allowed = if ($directory -eq 'scripts') { $file.Extension -eq '.ps1' } else { $file.Extension -in @('.cs','.csproj') }
        if (!$allowed) { continue }
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected linked source: $relative" }
        $relativeFiles.Add($relative)
    }
}
$files = @($relativeFiles | Sort-Object -Unique)
$records = @(foreach ($relative in $files) {
    $source = Join-Path $workspace $relative
    [ordered]@{ path = $relative; bytes = (Get-Item -LiteralPath $source).Length; sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash }
})
$manifest = [ordered]@{
    version = $version
    kind = 'source'
    buildInstructions = 'docs/BUILD-SOURCE.md'
    files = $records
}
$artifacts = Join-Path $workspace 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$temporary = Join-Path $artifacts "$packageName-$([Guid]::NewGuid().ToString('N')).zip"
$destination = Join-Path $artifacts "$packageName.zip"
$archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($relative in $files) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,
            (Join-Path $workspace $relative), "$packageName/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $entry = $archive.CreateEntry("$packageName/SOURCE-MANIFEST.json")
    $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write(($manifest | ConvertTo-Json -Depth 6)) } finally { $writer.Dispose() }
} finally { $archive.Dispose() }

$archive = [IO.Compression.ZipFile]::OpenRead($temporary)
try {
    if ($archive.Entries.Count -ne $files.Count + 1) { throw 'Source ZIP entry count mismatch.' }
    foreach ($record in $records) {
        $entry = $archive.GetEntry("$packageName/$($record.path)")
        if (!$entry -or $entry.Length -ne $record.bytes) { throw "Source ZIP file mismatch: $($record.path)" }
        $stream = $entry.Open()
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $hash = [Convert]::ToHexString($hasher.ComputeHash($stream)) }
        finally { $stream.Dispose(); $hasher.Dispose() }
        if ($hash -ne $record.sha256) { throw "Source ZIP hash mismatch: $($record.path)" }
    }
} finally { $archive.Dispose() }
Move-Item -LiteralPath $temporary -Destination $destination -Force
Write-Output "Source ZIP: $destination"
Write-Output "Verified $($files.Count) source files; no game references, runtime, build output or private settings."
Get-FileHash -LiteralPath $destination -Algorithm SHA256
