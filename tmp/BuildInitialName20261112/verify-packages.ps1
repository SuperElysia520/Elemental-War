$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$releaseRoot = (Resolve-Path -LiteralPath 'E:\Unity资产\FPS\Builds\UgsPvp\InitialName-20261112').Path
$evidenceRoot = (Resolve-Path -LiteralPath 'E:\Unity资产\FPS\tmp\BuildInitialName20261112').Path
$notePath = Join-Path $releaseRoot '雨云替换说明.txt'

# Refresh the small documentation entry after adding the full bridge fallback.
foreach ($item in @(
    @{zip='WindowsServer-20261112.zip'; entry='雨云替换说明.txt'},
    @{zip='BridgeUpdate-20261112.zip'; entry='BridgeUpdate/雨云替换说明.txt'}
)) {
    $archive = [IO.Compression.ZipFile]::Open((Join-Path $releaseRoot $item.zip), [IO.Compression.ZipArchiveMode]::Update)
    try {
        $oldEntry = $archive.GetEntry($item.entry)
        if ($null -ne $oldEntry) { $oldEntry.Delete() }
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $notePath, $item.entry, [IO.Compression.CompressionLevel]::Optimal)
    } finally { $archive.Dispose() }
}

function Get-StreamHash([IO.Stream]$Stream) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hasher.ComputeHash($Stream)).Replace('-','') }
    finally { $hasher.Dispose() }
}

$specs = @(
    @{zip='WindowsClient-20261112.zip'; part='Client'; required=@('Client/FPS.exe','Client/UnityPlayer.dll','Client/FPS_Data/Managed/Assembly-CSharp.dll','Client/MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll')},
    @{zip='WindowsServer-20261112.zip'; part='Server'; required=@('Server/FPS_PVP_Server.exe','Server/UnityPlayer.dll','Server/FPS_PVP_Server_Data/Managed/Assembly-CSharp.dll','Server/MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll')},
    @{zip='BridgeUpdate-20261112.zip'; part='BridgeUpdate'; required=@('BridgeUpdate/AccountService.dll','BridgeUpdate/AccountService.pdb','BridgeUpdate/Update-Bridge.ps1','BridgeUpdate/patch-manifest.json','BridgeUpdate/Test-UgsPvpBridge-v2.ps1')},
    @{zip='BridgeFull-20261112.zip'; part='Bridge'; required=@('Bridge/AccountService.exe','Bridge/AccountService.dll','Bridge/AccountService.runtimeconfig.json','Bridge/coreclr.dll')}
)
$artifacts = @()
foreach ($spec in $specs) {
    $path = Join-Path $releaseRoot $spec.zip
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $seen = New-Object 'System.Collections.Generic.HashSet[string]'
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if (-not $seen.Add($name)) { throw "Duplicate ZIP entry: $name" }
            if ($name -match '(^|/)(State|CareerOutbox_[^/]*|career-results|RecoveryManifests|data)(/|$)' -or
                $name -match '(?i)(^|/)(accounts\.json|ugs-pvp-config\.json|bridge-runtime\.json|[^/]+\.(pfx|p12|key|pem))$' -or
                $name -match '(BurstDebugInformation_DoNotShip|(^|/)\.\.?(/|$)|^/|^[A-Za-z]:)') {
                throw "Unexpected private/state/debug/path entry: $name"
            }
            $sourcePath = Join-Path $releaseRoot ($name.Replace('/', '\'))
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "No source for $name" }
            $source = Get-Item -LiteralPath $sourcePath
            if ($entry.Length -ne $source.Length) { throw "ZIP size mismatch: $name" }
            $stream = $entry.Open()
            try { $entryHash = Get-StreamHash $stream } finally { $stream.Dispose() }
            if ($entryHash -ne (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash) { throw "ZIP hash mismatch: $name" }
        }
        foreach ($required in $spec.required) {
            if (-not $seen.Contains($required)) { throw "Required file missing: $required" }
        }
        $sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $releaseRoot $spec.part) -File -Recurse -Force | Where-Object {
            $_.FullName -notmatch '[\\/][^\\/]*BurstDebugInformation_DoNotShip[\\/]' -and $_.FullName -notmatch '[\\/]Deployment[\\/]RecoveryManifests[\\/]'
        })
        $expectedCount = $sourceFiles.Count
        if ($spec.part -in @('Server','Bridge')) { $expectedCount++ }
        if ($seen.Count -ne $expectedCount) { throw "Missing or extra ZIP files in $($spec.zip): $($seen.Count) vs $expectedCount" }
        $artifacts += [ordered]@{
            file = $spec.zip
            part = $spec.part
            bytes = (Get-Item -LiteralPath $path).Length
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            filesVerified = $seen.Count
            verification = 'Every uncompressed entry matches its source SHA256; required runtime files present; private state/debug files absent.'
        }
    } finally { $archive.Dispose() }
}

$metadataChecks = @(
    @{file='Client/FPS_Data/Managed/Assembly-CSharp.dll'; symbols=@('InitialNamePanelView','ConfirmInitialName','get_DisplayName')},
    @{file='Server/FPS_PVP_Server_Data/Managed/Assembly-CSharp.dll'; symbols=@('get_DisplayName','UpdatePvpPlayerProfile')},
    @{file='Bridge/AccountService.dll'; symbols=@('IUgsPersonalCloudStore','get_DisplayName')}
)
foreach ($check in $metadataChecks) {
    $assemblyText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Join-Path $releaseRoot $check.file)))
    foreach ($symbol in $check.symbols) {
        if (-not $assemblyText.Contains($symbol)) { throw "Feature symbol missing from $($check.file): $symbol" }
    }
}
$patchManifest = Get-Content -LiteralPath (Join-Path $releaseRoot 'BridgeUpdate/patch-manifest.json') -Raw | ConvertFrom-Json
foreach ($item in $patchManifest.files) {
    $published = (Get-FileHash -LiteralPath (Join-Path $releaseRoot ('Bridge/' + $item.file)) -Algorithm SHA256).Hash
    $patched = (Get-FileHash -LiteralPath (Join-Path $releaseRoot ('BridgeUpdate/' + $item.file)) -Algorithm SHA256).Hash
    if ($published -ne $patched -or $patched -ne $item.sha256) { throw 'Patch differs from published application or patch manifest.' }
}

$client = Get-Content -LiteralPath (Join-Path $evidenceRoot 'build-client.json') -Raw | ConvertFrom-Json
$server = Get-Content -LiteralPath (Join-Path $evidenceRoot 'build-server.json') -Raw | ConvertFrom-Json
$manifest = [ordered]@{
    release = 'InitialName-20261112'
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    platform = 'Windows x64'
    unityVersion = $client.unityVersion
    unityBackend = 'Mono'
    developmentBuild = $false
    bridgeRuntime = 'Microsoft.NETCore.App 10.0.8 / Microsoft.AspNetCore.App 10.0.8, self-contained win-x64'
    clientBuild = $client
    serverBuild = $server
    localSmoke = (Get-Content -LiteralPath (Join-Path $evidenceRoot 'smoke.json') -Raw | ConvertFrom-Json)
    clientGraphicsSmoke = (Get-Content -LiteralPath (Join-Path $evidenceRoot 'client-graphics-smoke.json') -Raw | ConvertFrom-Json)
    updaterFixtureCases = 29
    updaterFixtureFailures = 0
    featureAssemblySymbolsVerified = $metadataChecks
    onlineUgsOrRainyunValidated = $false
    knownBuildIssues = @('Visual Scripting AOT pre-build ArgumentNullException (typeSet) is logged despite BuildReport Succeeded.', 'DEF-spine.006 has a missing script warning.', 'Third-party shader, deprecated API and existing UIBase method-hiding warnings remain.')
    artifacts = $artifacts
    deploymentInstructions = '雨云替换说明.txt'
}
[IO.File]::WriteAllText((Join-Path $releaseRoot 'manifest.json'), ($manifest | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
$hashLines = $artifacts | ForEach-Object { $_.sha256 + '  ' + $_.file }
[IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), $hashLines, (New-Object Text.UTF8Encoding($false)))
$artifacts | Select-Object file,bytes,filesVerified | Format-Table -AutoSize
