#Requires -Version 5.1
param(
    [string]$DeliveryDirectory,
    [string]$OutputDirectory,
    [string]$AccountServiceZip,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$ReleaseId = ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if ([string]::IsNullOrWhiteSpace($DeliveryDirectory)) { $DeliveryDirectory = Join-Path $projectRoot 'Builds/RifleGirlMigration' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $DeliveryDirectory 'Packages' }
if ([string]::IsNullOrWhiteSpace($AccountServiceZip)) { $AccountServiceZip = Join-Path $projectRoot 'Server/AccountService/publish/AccountService-WindowsServer2022.zip' }
$deliveryRoot = [IO.Path]::GetFullPath($DeliveryDirectory).TrimEnd('\', '/')
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
$outputPrefix = $outputRoot + [IO.Path]::DirectorySeparatorChar
if ($outputRoot -eq [IO.Path]::GetPathRoot($outputRoot).TrimEnd('\', '/')) { throw 'OutputDirectory must be a dedicated directory, not a drive root.' }

function Assert-NoReparseAncestors([string]$Path) {
    $candidate = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($candidate)) {
        if (Test-Path -LiteralPath $candidate) {
            if (((Get-Item -Force -LiteralPath $candidate).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ('Junction/symlink paths are not accepted for delivery packaging: ' + $candidate)
            }
        }
        $parent = [IO.Directory]::GetParent($candidate)
        $candidate = if ($null -ne $parent) { $parent.FullName } else { $null }
    }
}
function Assert-OutputPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw ('Output escaped the specified directory: ' + $full) }
    Assert-NoReparseAncestors $full
    return $full
}
function Commit-OutputFile([string]$Pending, [string]$Destination) {
    $pendingPath = Assert-OutputPath $Pending
    $destinationPath = Assert-OutputPath $Destination
    if (Test-Path -LiteralPath $destinationPath) { [IO.File]::Replace($pendingPath, $destinationPath, [Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($pendingPath, $destinationPath) }
}
function Is-ExcludedEntry([string]$RelativePath) {
    $normalized = $RelativePath.Replace('\', '/')
    if ($normalized -match '(^|/)[^/]*DoNotShip[^/]*(/|$)') { return $true }
    if ($normalized -match '(^|/)(test-results|Smoke|local-playtest|account-data|acme-production|acme-staging|\.git|\.vs|logs|tmp)(/|$)') { return $true }
    if ($normalized -match '\.(pdb|mdb|dbg|log|pid|pfx|key)$' -or $normalized -match '(^|/)[^/]+\.dSYM(/|$)') { return $true }
    if ($normalized -match '(^|/)(accounts\.json(\..*)?|deployment\.json|renewal-status\.json|renew\.lock|host-report\.json|join-report\.json|\.env([^/]*)?|credentials\.json|secrets\.json|passwords?\.txt)$') { return $true }
    return $false
}
function Get-RuntimeFiles([string]$Directory, [string]$ExecutableName, [string[]]$Launchers) {
    $root = (Resolve-Path -LiteralPath $Directory).Path.TrimEnd('\', '/')
    Assert-NoReparseAncestors $root
    $dataDirectoryName = [IO.Path]::GetFileNameWithoutExtension($ExecutableName) + '_Data'
    foreach ($required in @($ExecutableName, $dataDirectoryName, 'UnityPlayer.dll', 'MonoBleedingEdge') + $Launchers) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $required))) { throw ('Incomplete game build or missing launcher: ' + (Join-Path $root $required)) }
    }
    $directories = New-Object 'Collections.Generic.Stack[string]'
    $directories.Push($root)
    $files = New-Object 'Collections.Generic.List[object]'
    while ($directories.Count -gt 0) {
        $directory = $directories.Pop()
        foreach ($entry in Get-ChildItem -Force -LiteralPath $directory) {
            $relative = $entry.FullName.Substring($root.Length + 1).Replace('\', '/')
            if (Is-ExcludedEntry $relative) { continue }
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Build contains a junction/symlink: ' + $relative) }
            if ($directory -eq $root) {
                # Only Unity runtime roots and the reviewed launchers are shipped; arbitrary local documents/test tools are omitted.
                $allowed = if ($entry.PSIsContainer) { $entry.Name -eq $dataDirectoryName -or $entry.Name -eq 'MonoBleedingEdge' }
                    else { $entry.Name -eq $ExecutableName -or $entry.Name -eq 'UnityCrashHandler64.exe' -or $entry.Extension -eq '.dll' -or $Launchers -contains $entry.Name }
                if (-not $allowed) { continue }
            }
            if ($entry.PSIsContainer) { $directories.Push($entry.FullName) }
            else { $files.Add([pscustomobject]@{ source = $entry.FullName; entry = $relative; bytes = $entry.Length }) }
        }
    }
    return $files | Sort-Object entry
}
function New-GameArchive([string]$Name, [object[]]$Files, [string]$Readme) {
    $destination = Assert-OutputPath (Join-Path $outputRoot $Name)
    $pending = Assert-OutputPath ($destination + '.pending-' + [Guid]::NewGuid().ToString('N'))
    try {
        $stream = [IO.File]::Open($pending, 'CreateNew', 'ReadWrite', 'None')
        try {
            $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
            try {
                foreach ($file in $Files) {
                    $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.source, $file.entry, [IO.Compression.CompressionLevel]::Optimal)
                }
                $readmeEntry = $archive.CreateEntry('README-Deployment.md', [IO.Compression.CompressionLevel]::Optimal)
                $writer = New-Object IO.StreamWriter($readmeEntry.Open(), (New-Object Text.UTF8Encoding($false)))
                try { $writer.Write($Readme) } finally { $writer.Dispose() }
            }
            finally { $archive.Dispose() }
        }
        finally { $stream.Dispose() }
        Commit-OutputFile $pending $destination
        return [pscustomobject]@{ file = $Name; bytes = (Get-Item -LiteralPath $destination).Length; sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash; entries = @($Files | ForEach-Object { $_.entry }) + @('README-Deployment.md') }
    }
    finally { if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath (Assert-OutputPath $pending) } }
}

Assert-NoReparseAncestors $deliveryRoot
Assert-NoReparseAncestors $outputRoot
foreach ($role in @('Client', 'Server')) {
    $sourceRoot = Join-Path $deliveryRoot $role
    if ($outputRoot.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or $outputRoot.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputDirectory must be separate from both runtime input directories.'
    }
}
$clientFiles = @(Get-RuntimeFiles (Join-Path $deliveryRoot 'Client') 'FPS.exe' @('Start-FPS.cmd', 'Start-FPS.ps1'))
$serverFiles = @(Get-RuntimeFiles (Join-Path $deliveryRoot 'Server') 'FPS_PVP_Server.exe' @('Start-PvpServer.cmd', 'Start-PvpServer.ps1', 'Stop-PvpServer.cmd', 'Stop-PvpServer.ps1'))
$readmePath = Join-Path $deliveryRoot 'README-Deployment.md'
if (-not (Test-Path -LiteralPath $readmePath)) { throw 'Run Prepare-GameDelivery.ps1 after building to create the delivery instructions and launchers.' }
$readme = 'Delivery release: ' + $ReleaseId + "`r`nClient and dedicated server must come from this same release. Keep the account data outside application directories.`r`n`r`n" + [IO.File]::ReadAllText($readmePath)
$accountZipPath = (Resolve-Path -LiteralPath $AccountServiceZip).Path
Assert-NoReparseAncestors $accountZipPath
$accountArchive = [IO.Compression.ZipFile]::OpenRead($accountZipPath)
$accountEntryNames = New-Object 'Collections.Generic.List[string]'
try {
    foreach ($entry in $accountArchive.Entries) {
        if ($entry.FullName -match '(^|[\\/])\.\.([\\/]|$)|^[\\/]|^[A-Za-z]:' -or $entry.FullName -match '^(data|test-results)[\\/]') { throw ('Unsafe account archive entry: ' + $entry.FullName) }
        # The existing framework-dependent account publish includes a PDB; omit its symbols when creating the delivery copy.
        if ($entry.FullName -match '\.(pdb|mdb|dbg)$' -or $entry.FullName -match '(^|[\\/])[^/\\]*DoNotShip[^/\\]*([\\/]|$)') { continue }
        if (Is-ExcludedEntry $entry.FullName) { throw ('Account service archive contains private/runtime/test data: ' + $entry.FullName) }
        $accountEntryNames.Add($entry.FullName)
    }
}
finally { $accountArchive.Dispose() }
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$packages = @()
$packages += New-GameArchive 'RifleGirl-PVP-Client-Windows.zip' $clientFiles $readme
$packages += New-GameArchive 'RifleGirl-PVP-Server-Windows.zip' $serverFiles $readme
$accountDestination = Assert-OutputPath (Join-Path $outputRoot 'AccountService-WindowsServer2022.zip')
$accountPending = Assert-OutputPath ($accountDestination + '.pending-' + [Guid]::NewGuid().ToString('N'))
try {
    $sourceArchive = [IO.Compression.ZipFile]::OpenRead($accountZipPath)
    try {
        $destinationStream = [IO.File]::Open($accountPending, 'CreateNew', 'ReadWrite', 'None')
        try {
            $destinationArchive = New-Object IO.Compression.ZipArchive($destinationStream, [IO.Compression.ZipArchiveMode]::Create, $false)
            try {
                foreach ($entryName in $accountEntryNames) {
                    $sourceEntry = $sourceArchive.GetEntry($entryName)
                    $destinationEntry = $destinationArchive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
                    $sourceStream = $sourceEntry.Open()
                    try {
                        $entryStream = $destinationEntry.Open()
                        try { $sourceStream.CopyTo($entryStream) } finally { $entryStream.Dispose() }
                    }
                    finally { $sourceStream.Dispose() }
                }
            }
            finally { $destinationArchive.Dispose() }
        }
        finally { $destinationStream.Dispose() }
    }
    finally { $sourceArchive.Dispose() }
    Commit-OutputFile $accountPending $accountDestination
}
finally { if (Test-Path -LiteralPath $accountPending) { Remove-Item -LiteralPath (Assert-OutputPath $accountPending) } }
$packages += [pscustomobject]@{ file = 'AccountService-WindowsServer2022.zip'; bytes = (Get-Item -LiteralPath $accountDestination).Length; sha256 = (Get-FileHash -LiteralPath $accountDestination -Algorithm SHA256).Hash; entries = @($accountEntryNames) }
$manifest = [ordered]@{ releaseId = $ReleaseId; createdUtc = [DateTime]::UtcNow.ToString('o'); packages = $packages }
foreach ($output in @(
    @{ name = 'package-manifest.json'; content = ($manifest | ConvertTo-Json -Depth 8) },
    @{ name = 'SHA256SUMS.txt'; content = (($packages | ForEach-Object { $_.sha256 + '  ' + $_.file }) -join "`r`n") },
    @{ name = 'README-Deployment.md'; content = $readme }
)) {
    $destination = Assert-OutputPath (Join-Path $outputRoot $output.name)
    $pending = Assert-OutputPath ($destination + '.pending-' + [Guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($pending, $output.content + "`r`n", (New-Object Text.UTF8Encoding($false)))
        Commit-OutputFile $pending $destination
    }
    finally { if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath (Assert-OutputPath $pending) } }
}
$packages | ForEach-Object { Write-Output ($_.file + '  SHA256=' + $_.sha256 + '  bytes=' + $_.bytes) }
Write-Output ('Delivery release ' + $ReleaseId + ' is in ' + $outputRoot)
