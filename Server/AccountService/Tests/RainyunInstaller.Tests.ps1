#Requires -Version 5.1
param([string]$InstallerPath = (Join-Path $PSScriptRoot '../Deployment/Install-RainyunBackend.ps1'))
$ErrorActionPreference = 'Stop'

# File-system integration tests only. The installer runs its actual validation, copy,
# backup and rollback code in isolated fixtures. Task Scheduler, CA, ACL and network
# commands are replaced with fakes; no service, firewall, certificate or ACL is changed.
$installerSource = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $InstallerPath).Path)
$installerSource = [regex]::Replace($installerSource, '(?m)^#Requires[^\r\n]*\r?\n', '')
$serviceRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$fixtureRoot = Join-Path $serviceRoot ('test-results/installer-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
$assertions = New-Object 'Collections.Generic.List[string]'

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw ('FAIL: ' + $Message) }
    $assertions.Add($Message)
    Write-Output ('PASS: ' + $Message)
}
function Write-Fixture([string]$Path, [string]$Text) {
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($true)))
}

function Get-ScheduledTask { param($TaskName, $ErrorAction) return $global:RainyunTestState.Tasks[$TaskName] }
function Start-ScheduledTask { param($TaskName) $global:RainyunTestState.Starts++ }
function Stop-ScheduledTask { param($TaskName, $ErrorAction) $global:RainyunTestState.Stops++ }
function Set-Acl { param($LiteralPath, $AclObject) $global:RainyunTestState.AclRequests++ }
function Get-Module { param([switch]$ListAvailable, $Name) return [pscustomobject]@{ Version = [version]'4.34.0' } }
function Install-Module { throw 'Tests must not install modules.' }
function Get-NetTCPConnection {
    param($State, $ErrorAction)
    # This is the first first-install action after recognition of an incomplete App.
    # Stop before any URL ACL, firewall or certificate action can be attempted.
    $global:RainyunTestState.FirstInstallReached = $true
    throw 'FIXTURE_FIRST_INSTALL_RECOGNIZED'
}
function Invoke-RestMethod {
    param($Uri, $TimeoutSec)
    if ($Uri -ne 'http://127.0.0.1:5080/health') { throw 'Tests must not access any network endpoint.' }
    if ($global:RainyunTestState.AdvanceDatabaseOnHealth) {
        [IO.File]::WriteAllText($global:RainyunTestState.Database, '{"accounts":[{"id":"existing","revision":2}]}')
    }
    return [pscustomobject]@{ status = 'ok'; service = 'fps-account'; apiVersion = $global:RainyunTestState.HealthVersion }
}

$fakeCommon = @'
function Read-FpsHttpsConfiguration([string]$StateDirectory, [switch]$AllowAdministrator) {
    return (Get-Content -Raw -LiteralPath (Join-Path $StateDirectory 'deployment.json') | ConvertFrom-Json)
}
function Stop-FpsAccountTask([string]$TaskName) { $global:RainyunTestState.Stops++ }
function Wait-FpsLocalTls($Configuration, [string]$Thumbprint) { $global:RainyunTestState.TlsChecks++ }
'@
$fakeRenewal = @'
param([string]$StateDirectory)
$global:RainyunTestState.RenewalChecks++
'@

function New-UpdateFixture([string]$Name, [string]$InstallFolder = 'App') {
    $case = Join-Path $fixtureRoot $Name
    $upload = Join-Path $case 'Upload'
    $install = Join-Path $case $InstallFolder
    $state = Join-Path $case 'State'
    New-Item -ItemType Directory -Force -Path $upload,$install,$state | Out-Null
    Write-Fixture (Join-Path $upload 'Deployment/Install-RainyunBackend.ps1') $installerSource
    Write-Fixture (Join-Path $upload 'Deployment/IpHttps.Common.ps1') $fakeCommon
    Write-Fixture (Join-Path $upload 'Deployment/Renew-WindowsIpHttps.ps1') $fakeRenewal
    foreach ($name in @('Prepare-WindowsIpHttpsHost.ps1', 'Initialize-WindowsIpHttps.ps1', 'Register-WindowsIpHttpsTasks.ps1')) {
        Write-Fixture (Join-Path $upload ('Deployment/' + $name)) "throw 'An update must preserve the existing certificate and tasks.'"
    }
    Write-Fixture (Join-Path $upload 'AccountService.exe') 'new-self-contained-host'
    Write-Fixture (Join-Path $upload 'AccountService.dll') 'new-account-service'
    Write-Fixture (Join-Path $upload 'coreclr.dll') 'fixture-runtime-not-executed'
    Write-Fixture (Join-Path $upload 'appsettings.json') '{}'
    Write-Fixture (Join-Path $upload 'AccountService.runtimeconfig.json') '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.13"}]}}'
    Write-Fixture (Join-Path $install 'AccountService.dll') 'original-account-service'
    Write-Fixture (Join-Path $install 'legacy-only.txt') 'existing-extra-program-file'
    Write-Fixture (Join-Path $install 'Deployment/Renew-WindowsIpHttps.ps1') $fakeRenewal
    Write-Fixture (Join-Path $state 'data/accounts.json') '{"accounts":[{"id":"existing","revision":1}]}'
    Write-Fixture (Join-Path $state 'tls/private.key') 'fixture-private-state-not-a-real-key'
    Write-Fixture (Join-Path $state 'acme-production/account.json') '{"fixture":"existing-acme-account"}'
    $configuration = [ordered]@{
        ownerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        publishDirectory = $install; stateDirectory = $state; publicIp = '203.0.113.25'
        taskName = 'FPS-AccountService'; renewalTaskName = 'FPS-AccountService-Certificate'
        certificateThumbprint = 'fixture-thumbprint'
    }
    Write-Fixture (Join-Path $state 'deployment.json') ($configuration | ConvertTo-Json)
    $global:RainyunTestState = @{
        Starts = 0; Stops = 0; RenewalChecks = 0; AclRequests = 0; TlsChecks = 0; HealthVersion = 2
        AdvanceDatabaseOnHealth = $false; Database = (Join-Path $state 'data/accounts.json')
        Tasks = @{
            'FPS-AccountService' = [pscustomobject]@{ Actions = @([pscustomobject]@{ WorkingDirectory = $install; Execute = 'dotnet.exe'; Arguments = 'old task arguments' }) }
            'FPS-AccountService-Certificate' = [pscustomobject]@{ Actions = @([pscustomobject]@{ WorkingDirectory = (Join-Path $install 'Deployment'); Arguments = ('-StateDirectory "{0}"' -f $state) }) }
        }
    }
    $fixture = [pscustomobject]@{ Root = $case; Upload = $upload; Install = $install; State = $state }
    Update-Manifest $fixture
    return $fixture
}
function Update-Manifest($Fixture) {
    $manifest = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $Fixture.Upload -File -Recurse | Where-Object { $_.Name -ne 'package-manifest.json' })) {
        $manifest += [ordered]@{ path = $file.FullName.Substring($Fixture.Upload.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    }
    Write-Fixture (Join-Path $Fixture.Upload 'package-manifest.json') ($manifest | ConvertTo-Json -Depth 3)
}
function New-RetryFixture([string]$Name) {
    $fixture = New-UpdateFixture $Name
    Remove-Item -LiteralPath (Join-Path $fixture.State 'deployment.json')
    Remove-Item -LiteralPath (Join-Path $fixture.Install 'legacy-only.txt')
    Write-Fixture (Join-Path $fixture.State '.fps-account-state') 'fixture initialized state; preserve every existing state file'
    $global:RainyunTestState.Tasks = @{}
    $global:RainyunTestState.FirstInstallReached = $false
    $currentManifest = Get-Content -Raw -LiteralPath (Join-Path $fixture.Upload 'package-manifest.json') | ConvertFrom-Json
    foreach ($file in $currentManifest) {
        $target = Join-Path $fixture.Install $file.path
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $fixture.Upload $file.path) -Destination $target -Force
    }
    Write-Fixture (Join-Path $fixture.Install 'Deployment/Install-RainyunBackend.ps1') 'fixture previous officially published installer'
    Write-Fixture (Join-Path $fixture.Install 'AccountService.dll') 'fixture previous officially published backend'
    $previousManifest = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $fixture.Install -File -Recurse)) {
        $previousManifest += [ordered]@{ path = $file.FullName.Substring($fixture.Install.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    }
    Write-Fixture (Join-Path $fixture.Upload 'Deployment/RecoveryManifests/fixture-previous.json') ($previousManifest | ConvertTo-Json -Depth 3)
    Update-Manifest $fixture
    return $fixture
}
function Run-Installer($Fixture) {
    & (Join-Path $Fixture.Upload 'Deployment/Install-RainyunBackend.ps1') -PackageDirectory $Fixture.Upload -StateDirectory $Fixture.State -InstallDirectory $Fixture.Install -PublicIp '203.0.113.25'
}
function Expect-Rejection($Fixture) {
    $rejected = $false
    try { Run-Installer $Fixture }
    catch { $rejected = $true }
    return $rejected
}
function Installer-FailureMessage($Fixture) {
    try { Run-Installer $Fixture; return '' }
    catch { return $_.Exception.Message }
}

try {
    $fixture = New-UpdateFixture 'successful-update'
    $databaseBefore = [IO.File]::ReadAllText((Join-Path $fixture.State 'data/accounts.json'))
    $privateBefore = [IO.File]::ReadAllText((Join-Path $fixture.State 'tls/private.key'))
    $acmeBefore = [IO.File]::ReadAllText((Join-Path $fixture.State 'acme-production/account.json'))
    Run-Installer $fixture
    Check ([IO.File]::ReadAllText((Join-Path $fixture.Install 'AccountService.dll')) -match 'new-account-service') 'An existing installation receives the new program files'
    Check ([IO.File]::ReadAllText((Join-Path $fixture.State 'data/accounts.json')) -eq $databaseBefore) 'Successful update preserves the existing account database byte for byte'
    Check ([IO.File]::ReadAllText((Join-Path $fixture.State 'tls/private.key')) -eq $privateBefore -and [IO.File]::ReadAllText((Join-Path $fixture.State 'acme-production/account.json')) -eq $acmeBefore) 'Successful update preserves certificate and ACME identity files'
    Check ($global:RainyunTestState.RenewalChecks -eq 1 -and $global:RainyunTestState.Stops -eq 1 -and $global:RainyunTestState.Starts -eq 1) 'Update checks normal renewal, stops once and restarts the existing task'
    Check ($global:RainyunTestState.Tasks['FPS-AccountService'].Actions[0].Execute -eq 'dotnet.exe') 'An existing framework-dependent scheduled task is preserved during update'
    $backups = @(Get-ChildItem -LiteralPath (Join-Path $fixture.Root 'FpsAccountBackups') -Directory)
    Check ($backups.Count -eq 1 -and [IO.File]::ReadAllText((Join-Path $backups[0].FullName 'App/AccountService.dll')) -match 'original-account-service') 'The update backup contains the old application before replacement'

    $fixture = New-UpdateFixture 'failed-version-check'
    $global:RainyunTestState.HealthVersion = 1
    $global:RainyunTestState.AdvanceDatabaseOnHealth = $true
    Check (Expect-Rejection $fixture) 'A failed new-program health check reports installation failure'
    Check ([IO.File]::ReadAllText((Join-Path $fixture.Install 'AccountService.dll')) -match 'original-account-service') 'Failed validation restores the old application files'
    Check (-not (Test-Path -LiteralPath (Join-Path $fixture.Install 'AccountService.exe'))) 'Rollback removes files newly added by the failed update'
    Check ([IO.File]::ReadAllText((Join-Path $fixture.State 'data/accounts.json')) -match '"revision":2') 'Rollback preserves data written after restart instead of overwriting it with an old backup'
    Check ($global:RainyunTestState.Starts -eq 2) 'Failed validation restarts the original task after restoring its program'

    $fixture = New-UpdateFixture 'corrupt-package'
    Write-Fixture (Join-Path $fixture.Upload 'AccountService.dll') 'corrupt-upload'
    Check (Expect-Rejection $fixture) 'A checksum mismatch rejects the package before installation'
    Check ($global:RainyunTestState.Stops -eq 0 -and [IO.File]::ReadAllText((Join-Path $fixture.Install 'AccountService.dll')) -match 'original-account-service') 'Corrupt uploads never stop or replace the existing service'

    $fixture = New-UpdateFixture 'incomplete-manifest'
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $fixture.Upload 'package-manifest.json') | ConvertFrom-Json
    $manifest = @($manifest | Where-Object { $_.path -ne 'coreclr.dll' })
    Write-Fixture (Join-Path $fixture.Upload 'package-manifest.json') ($manifest | ConvertTo-Json -Depth 3)
    Check (Expect-Rejection $fixture) 'A manifest missing a required runtime file is rejected'
    Check ($global:RainyunTestState.Stops -eq 0) 'An incomplete manifest cannot interrupt the existing service'

    $fixture = New-UpdateFixture 'manifest-traversal'
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $fixture.Upload 'package-manifest.json') | ConvertFrom-Json
    $manifest = @($manifest)
    $manifest += [pscustomobject]@{ path = '../outside.txt'; sha256 = 'invalid' }
    Write-Fixture (Join-Path $fixture.Upload 'package-manifest.json') ($manifest | ConvertTo-Json -Depth 3)
    Check (Expect-Rejection $fixture) 'A manifest path cannot escape the uploaded package directory'
    Check ($global:RainyunTestState.Stops -eq 0) 'Path validation rejects the package before stopping any existing service'

    $fixture = New-UpdateFixture 'private-state-in-package'
    Write-Fixture (Join-Path $fixture.Upload 'data/accounts.json') '{"fixture":"must-not-be-deployed"}'
    Update-Manifest $fixture
    Check (Expect-Rejection $fixture) 'A package containing a user database is rejected'
    Check ($global:RainyunTestState.Stops -eq 0) 'Private state in an upload never triggers an existing-service restart'

    $fixture = New-UpdateFixture 'renewal-lock-in-use'
    $heldLock = [IO.File]::Open((Join-Path $fixture.State 'renew.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    try { Check (Expect-Rejection $fixture) 'An active certificate renewal lock prevents the program update' }
    finally { $heldLock.Dispose() }
    Check ($global:RainyunTestState.Stops -eq 0 -and $global:RainyunTestState.Starts -eq 0) 'A failed lock acquisition leaves the running task untouched'

    $fixture = New-UpdateFixture 'backup-under-program' 'FpsAccountBackups'
    Check (Expect-Rejection $fixture) 'A custom directory layout cannot create the program backup inside the program itself'
    Check ($global:RainyunTestState.Stops -eq 0) 'An unsafe backup location is rejected before stopping the existing service'

    $fixture = New-RetryFixture 'known-incomplete-first-install'
    $databaseBefore = [IO.File]::ReadAllText((Join-Path $fixture.State 'data/accounts.json'))
    Check ((Installer-FailureMessage $fixture) -eq 'FIXTURE_FIRST_INSTALL_RECOGNIZED') 'A verified previous official package can resume an incomplete first installation'
    Check ($global:RainyunTestState.FirstInstallReached -and $global:RainyunTestState.Stops -eq 0 -and $global:RainyunTestState.Starts -eq 0) 'Incomplete-install recognition reaches preflight without changing any scheduled task'
    Check ([IO.File]::ReadAllText((Join-Path $fixture.State 'data/accounts.json')) -eq $databaseBefore) 'Incomplete-install recognition never overwrites existing account data'

    $fixture = New-RetryFixture 'same-package-first-install-retry'
    foreach ($path in @('AccountService.dll', 'Deployment/Install-RainyunBackend.ps1')) {
        Copy-Item -LiteralPath (Join-Path $fixture.Upload $path) -Destination (Join-Path $fixture.Install $path) -Force
    }
    Check ((Installer-FailureMessage $fixture) -eq 'FIXTURE_FIRST_INSTALL_RECOGNIZED') 'An incomplete first installation matching the current package remains retryable'

    $fixture = New-RetryFixture 'mixed-package-first-install'
    Copy-Item -LiteralPath (Join-Path $fixture.Upload 'AccountService.dll') -Destination (Join-Path $fixture.Install 'AccountService.dll') -Force
    Check ((Installer-FailureMessage $fixture) -ne 'FIXTURE_FIRST_INSTALL_RECOGNIZED' -and -not $global:RainyunTestState.FirstInstallReached) 'Files mixed between current and previous packages are rejected before first-install actions'

    $fixture = New-RetryFixture 'unknown-first-install-script'
    Write-Fixture (Join-Path $fixture.Install 'Deployment/Install-RainyunBackend.ps1') 'unknown or manually modified program'
    Check ((Installer-FailureMessage $fixture) -ne 'FIXTURE_FIRST_INSTALL_RECOGNIZED' -and -not $global:RainyunTestState.FirstInstallReached) 'An unknown script is never treated as a recoverable installer-owned deployment'

    $fixture = New-RetryFixture 'unlisted-recovery-manifest'
    $currentManifest = Get-Content -Raw -LiteralPath (Join-Path $fixture.Upload 'package-manifest.json') | ConvertFrom-Json
    $currentManifest = @($currentManifest | Where-Object { $_.path -ne 'Deployment/RecoveryManifests/fixture-previous.json' })
    Write-Fixture (Join-Path $fixture.Upload 'package-manifest.json') ($currentManifest | ConvertTo-Json -Depth 3)
    Check ((Installer-FailureMessage $fixture) -ne 'FIXTURE_FIRST_INSTALL_RECOGNIZED' -and -not $global:RainyunTestState.FirstInstallReached) 'A recovery manifest outside the new package checksum list cannot authorize recovery'

    $fixture = New-RetryFixture 'changed-recovery-manifest'
    Write-Fixture (Join-Path $fixture.Upload 'Deployment/RecoveryManifests/fixture-previous.json') '[]'
    Check ((Installer-FailureMessage $fixture) -ne 'FIXTURE_FIRST_INSTALL_RECOGNIZED' -and -not $global:RainyunTestState.FirstInstallReached) 'Changing a checksummed recovery manifest causes package rejection before recovery'

    Write-Fixture (Join-Path $fixtureRoot 'results.txt') (($assertions | ForEach-Object { 'PASS: ' + $_ }) -join [Environment]::NewLine)
    Write-Output ('Completed ' + $assertions.Count + ' installer file-system assertions. Mocked boundaries: tasks, TLS/network, certificate issuance, ACL application. Results: ' + $fixtureRoot)
}
finally { Remove-Variable -Name RainyunTestState -Scope Global -ErrorAction SilentlyContinue }
