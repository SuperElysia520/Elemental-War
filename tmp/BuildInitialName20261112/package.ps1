param([ValidateSet('Client','Server','Bridge','BridgeUpdate')][string]$Part)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$releaseRoot = (Resolve-Path -LiteralPath 'E:\Unity资产\FPS\Builds\UgsPvp\InitialName-20261112').Path
$sourceRoot = Join-Path $releaseRoot $Part
$archiveName = switch ($Part) { 'Client' { 'WindowsClient-20261112.zip' } 'Server' { 'WindowsServer-20261112.zip' } 'Bridge' { 'BridgeFull-20261112.zip' } 'BridgeUpdate' { 'BridgeUpdate-20261112.zip' } }
$archivePath = Join-Path $releaseRoot $archiveName
$files = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Force | Where-Object { $_.FullName -notmatch '[\\/][^\\/]*BurstDebugInformation_DoNotShip[\\/]' -and $_.FullName -notmatch '[\\/]Deployment[\\/]RecoveryManifests[\\/]' })
$stream = [IO.File]::Open($archivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
try {
    foreach ($file in $files) {
        $entry = $file.FullName.Substring($releaseRoot.Length + 1).Replace('\','/')
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
    if ($Part -in @('Server','Bridge')) { $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $releaseRoot '雨云替换说明.txt'), '雨云替换说明.txt', [IO.Compression.CompressionLevel]::Optimal) }
} finally { $archive.Dispose(); $stream.Dispose() }
Get-Item -LiteralPath $archivePath | Select-Object Name,Length
