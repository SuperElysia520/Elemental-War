#Requires -Version 5.1
param([string]$RuntimeVersion = '10.0.13', [string]$OutputDirectory = '', [switch]$UseCachedRuntime)
$ErrorActionPreference = 'Stop'
$service = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $service '../..')).Path
if ($RuntimeVersion -notmatch '^10\.0\.\d+$') { throw 'Choose a supported .NET 10 servicing release.' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $service ('publish/RainyunBackend-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath ($output + '.zip'))) { throw 'Choose a new output directory; existing packages are preserved.' }
$project = Join-Path $service 'AccountService.csproj'
$restoreArguments = @('restore', $project, '--runtime', 'win-x64', '--configfile', (Join-Path $service 'NuGet.Config'), "-p:RuntimeFrameworkVersion=$RuntimeVersion", '-p:TargetLatestRuntimePatch=true')
if (-not $UseCachedRuntime) { $restoreArguments += @('--source', 'https://api.nuget.org/v3/index.json') }
& dotnet @restoreArguments
if ($LASTEXITCODE -ne 0) { throw 'Runtime restore failed; no distributable package was produced.' }
& dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore "-p:RuntimeFrameworkVersion=$RuntimeVersion" -p:TargetLatestRuntimePatch=true -o $output
if ($LASTEXITCODE -ne 0) { throw 'Backend publish failed; no distributable ZIP was produced.' }
New-Item -ItemType Directory -Force -Path (Join-Path $output 'Deployment') | Out-Null
foreach ($name in @('Install-RainyunBackend.ps1', 'Install-RainyunBackend.cmd')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $output ('Deployment/' + $name)) }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-RainyunBackend.cmd') -Destination (Join-Path $output 'Install-RainyunBackend.cmd')
$recoveryDirectory = Join-Path $PSScriptRoot 'RecoveryManifests'
if (Test-Path -LiteralPath $recoveryDirectory) {
    New-Item -ItemType Directory -Force -Path (Join-Path $output 'Deployment/RecoveryManifests') | Out-Null
    foreach ($recovery in @(Get-ChildItem -LiteralPath $recoveryDirectory -File -Filter '*.json')) {
        Copy-Item -LiteralPath $recovery.FullName -Destination (Join-Path $output ('Deployment/RecoveryManifests/' + $recovery.Name))
    }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/AI/RainyunBackendDeployment.md') -Destination (Join-Path $output 'README.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/AI/RainyunBackendDeployment.md') -Destination (Join-Path $output 'RainyunBackendDeployment.md')
$files = @(Get-ChildItem -LiteralPath $output -File -Recurse | Where-Object { $_.Extension -ne '.pdb' })
foreach ($pdb in @(Get-ChildItem -LiteralPath $output -File -Recurse -Filter '*.pdb')) {
    if (-not $pdb.FullName.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid generated-file path.' }
    Remove-Item -LiteralPath $pdb.FullName
}
$manifest = @()
foreach ($file in $files) {
    $relative = $file.FullName.Substring($output.Length + 1).Replace('\', '/')
    if ($relative -match '(^|/)(data|tls|acme-production|test-results)(/|$)|accounts\.json|\.(key|pem)$') { throw 'Private state must not be included in a backend package.' }
    $manifest += [ordered]@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[IO.File]::WriteAllText((Join-Path $output 'package-manifest.json'), ($manifest | ConvertTo-Json -Depth 3), (New-Object Text.UTF8Encoding($false)))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($output, ($output + '.zip'))
Write-Output ('Backend directory: ' + $output)
Write-Output ('Backend ZIP: ' + $output + '.zip')
Write-Output ('ZIP SHA256: ' + (Get-FileHash -LiteralPath ($output + '.zip') -Algorithm SHA256).Hash)
