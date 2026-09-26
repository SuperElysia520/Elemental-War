#Requires -Version 5.1
# Tiny fixtures only. Does not read, package, launch, or modify the real Unity builds.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$serviceRoot = Split-Path -Parent $PSScriptRoot
$fixtureRoot = Join-Path $serviceRoot ('test-results/package-fixture-' + [Guid]::NewGuid().ToString('N'))
$delivery = Join-Path $fixtureRoot 'Delivery'
$output = Join-Path $fixtureRoot 'Output'
$packager = Join-Path $serviceRoot 'Deployment/Package-GameDelivery.ps1'
$results = New-Object 'Collections.Generic.List[string]'
function Write-Fixture([string]$Relative, [string]$Content = 'FIXTURE ONLY - not executable') {
    $path = Join-Path $fixtureRoot $Relative
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $Content, (New-Object Text.UTF8Encoding($false)))
}
function Assert-Test([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw ('FAIL: ' + $Message) }
    $script:results.Add('PASS: ' + $Message)
}
foreach ($role in @('Client', 'Server')) {
    $exe = if ($role -eq 'Client') { 'FPS' } else { 'FPS_PVP_Server' }
    foreach ($file in @(($exe + '.exe'), 'UnityPlayer.dll', 'UnityCrashHandler64.exe', 'MonoBleedingEdge/EmbedRuntime/mono.dll', ($exe + '_Data/globalgamemanagers'), ($exe + '_Data/Managed/Assembly-CSharp.dll'))) {
        Write-Fixture ('Delivery/' + $role + '/' + $file)
    }
    foreach ($excluded in @('debug.pdb', 'server_7777.log', 'pvp-server-7777.pid', 'private.key', '.env', 'accounts.json', 'TestHarness.ps1', 'FPS_BurstDebugInformation_DoNotShip/extra.bin', 'test-results/accounts.json', ($exe + '_Data/Smoke/host-report.json'), ($exe + '_Data/Managed/Assembly-CSharp.pdb'))) {
        Write-Fixture ('Delivery/' + $role + '/' + $excluded) 'PRIVATE OR DEBUG FIXTURE - must not ship'
    }
}
foreach ($launcher in @('Start-FPS.cmd', 'Start-FPS.ps1')) { Write-Fixture ('Delivery/Client/' + $launcher) }
foreach ($launcher in @('Start-PvpServer.cmd', 'Start-PvpServer.ps1', 'Stop-PvpServer.cmd', 'Stop-PvpServer.ps1')) { Write-Fixture ('Delivery/Server/' + $launcher) }
Write-Fixture 'Delivery/README-Deployment.md' '# Fixture deployment instructions'
Write-Fixture 'AccountInput/AccountService.dll'
Write-Fixture 'AccountInput/AccountService.pdb'
Write-Fixture 'AccountInput/AccountService.md' '# Fixture account instructions'
$accountZip = Join-Path $fixtureRoot 'FixtureAccount.zip'
[IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $fixtureRoot 'AccountInput'), $accountZip)
Write-Fixture 'Output/operator-notes.txt' 'KEEP THIS EXISTING FILE'
$sourceHash = (Get-FileHash -LiteralPath (Join-Path $delivery 'Client/FPS.exe')).Hash
& $packager -DeliveryDirectory $delivery -OutputDirectory $output -AccountServiceZip $accountZip -ReleaseId 'fixture-1'

foreach ($kind in @('Client', 'Server')) {
    $zipPath = Join-Path $output ('RifleGirl-PVP-' + $kind + '-Windows.zip')
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
        $exe = if ($kind -eq 'Client') { 'FPS' } else { 'FPS_PVP_Server' }
        $launcher = if ($kind -eq 'Client') { 'Start-FPS.cmd' } else { 'Start-PvpServer.cmd' }
        Assert-Test ($entries -contains ($exe + '.exe') -and $entries -contains ($exe + '_Data/globalgamemanagers') -and $entries -contains 'MonoBleedingEdge/EmbedRuntime/mono.dll' -and $entries -contains $launcher -and $entries -contains 'README-Deployment.md') ($kind + ' preserves complete runtime roots, launcher and README.')
        Assert-Test (@($entries | Where-Object { $_ -match 'DoNotShip|\.pdb$|\.log$|\.pid$|private\.key|\.env$|accounts\.json|test-results|Smoke|TestHarness' }).Count -eq 0) ($kind + ' excludes debug, runtime logs, test fixtures and account/private files.')
        $reader = New-Object IO.StreamReader($archive.GetEntry('README-Deployment.md').Open())
        try { $readme = $reader.ReadToEnd() } finally { $reader.Dispose() }
        Assert-Test ($readme -match 'Delivery release: fixture-1') ($kind + ' README identifies the matching delivery batch.')
    }
    finally { $archive.Dispose() }
}
$accountOutput = [IO.Compression.ZipFile]::OpenRead((Join-Path $output 'AccountService-WindowsServer2022.zip'))
try { Assert-Test ($null -ne $accountOutput.GetEntry('AccountService.dll') -and $null -eq $accountOutput.GetEntry('AccountService.pdb')) 'The account delivery copy preserves its runtime and removes PDB symbols.' }
finally { $accountOutput.Dispose() }
$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $output 'package-manifest.json') | ConvertFrom-Json
Assert-Test ($manifest.packages.Count -eq 3 -and $manifest.releaseId -eq 'fixture-1') 'Manifest describes three archives and the selected delivery batch.'
foreach ($package in $manifest.packages) {
    Assert-Test ((Get-FileHash -LiteralPath (Join-Path $output $package.file) -Algorithm SHA256).Hash -eq $package.sha256) ('SHA256 matches ' + $package.file)
}
& $packager -DeliveryDirectory $delivery -OutputDirectory $output -AccountServiceZip $accountZip -ReleaseId 'fixture-2'
$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $output 'package-manifest.json') | ConvertFrom-Json
Assert-Test ($manifest.releaseId -eq 'fixture-2' -and [IO.File]::ReadAllText((Join-Path $output 'operator-notes.txt')) -eq 'KEEP THIS EXISTING FILE' -and (Get-FileHash -LiteralPath (Join-Path $delivery 'Client/FPS.exe')).Hash -eq $sourceHash) 'Repeated packaging replaces only generated outputs and preserves source binaries and unrelated output files.'
$rejected = $false
try { & $packager -DeliveryDirectory $delivery -OutputDirectory (Join-Path $delivery 'Client/ForbiddenOutput') -AccountServiceZip $accountZip -ReleaseId 'fixture-invalid' }
catch { $rejected = $_.Exception.Message -match 'separate from both runtime input' }
Assert-Test ($rejected -and -not (Test-Path -LiteralPath (Join-Path $delivery 'Client/ForbiddenOutput'))) 'An output directory inside a runtime input is rejected before writing.'
Assert-Test (@(Get-ChildItem -LiteralPath $output -Filter '*.pending-*').Count -eq 0) 'Completed runs leave no temporary output archives.'
$report = Join-Path $fixtureRoot 'results.txt'
[IO.File]::WriteAllLines($report, $results)
$results
Write-Output ('Fixture report: ' + $report)
