#Requires -Version 5.1
param([string]$DeploymentDirectory = (Join-Path $PSScriptRoot '../Deployment'))
$ErrorActionPreference = 'Stop'

# Execute the actual deployment scripts with isolated files and fake ACME commands.
# No real Posh-ACME import, CA call, certificate issuance, task, ACL or listener is used.
# The import fake follows Posh-ACME 4.35.0's documented ConfigRoot selection/cache rule:
# https://github.com/rmbolger/Posh-ACME/blob/v4.35.0/Posh-ACME/Private/Import-PAConfig.ps1
# https://poshac.me/docs/v4/Guides/Using-an-Alternate-Config-Location/
$deployment = (Resolve-Path -LiteralPath $DeploymentDirectory).Path
$serviceRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$fixtureRoot = Join-Path $serviceRoot ('test-results/acme-home-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$scripts = Join-Path $fixtureRoot 'Deployment'
$publish = Join-Path $fixtureRoot 'App'
$state = Join-Path $fixtureRoot 'State'
New-Item -ItemType Directory -Force -Path $scripts,$publish | Out-Null
$checks = New-Object 'Collections.Generic.List[string]'
$savedAcmeHome = [Environment]::GetEnvironmentVariable('POSHACME_HOME')

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw ('FAIL: ' + $Message) }
    $checks.Add($Message)
    Write-Output ('PASS: ' + $Message)
}
function Write-Fixture([string]$Path, [string]$Text) {
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($true)))
}
foreach ($name in @('Initialize-WindowsIpHttps.ps1', 'Renew-WindowsIpHttps.ps1', 'IpHttps.Common.ps1')) {
    $source = [IO.File]::ReadAllText((Join-Path $deployment $name))
    $source = [regex]::Replace($source, '(?m)^#Requires[^\r\n]*\r?\n', '')
    Write-Fixture (Join-Path $scripts $name) $source
}
Write-Fixture (Join-Path $publish 'AccountService.dll') 'not an executable; certificate-only fixture'
$global:AcmeDirectoryTest = @{
    Imports = (New-Object 'Collections.Generic.List[object]')
    Servers = (New-Object 'Collections.Generic.List[object]')
    ConfigRoot = ''; DefaultRoot = (Join-Path $fixtureRoot 'fake-default-profile/Posh-ACME')
    Fallbacks = 0; Renewals = 0; Certificates = 0; AclRequests = 0; TaskCalls = 0
    CurrentServer = ''; Certificate = $null
}

function Set-Acl { param($LiteralPath, $AclObject) $global:AcmeDirectoryTest.AclRequests++ }
function Import-Module {
    param([string]$Name, [version]$MinimumVersion, [switch]$Force)
    if ($Name -ne 'Posh-ACME') { throw 'Only the fake Posh-ACME module is available in this test.' }
    $requested = [Environment]::ExpandEnvironmentVariables($env:POSHACME_HOME)
    $exists = Test-Path -LiteralPath $requested -PathType Container
    $global:AcmeDirectoryTest.Imports.Add([pscustomobject]@{ RequestedRoot = $requested; ExistedBeforeImport = $exists; Force = $Force.IsPresent })
    if ($Force -or [string]::IsNullOrWhiteSpace($global:AcmeDirectoryTest.ConfigRoot)) {
        if ($exists) { $global:AcmeDirectoryTest.ConfigRoot = $requested }
        else { $global:AcmeDirectoryTest.ConfigRoot = $global:AcmeDirectoryTest.DefaultRoot; $global:AcmeDirectoryTest.Fallbacks++ }
    }
}
function Set-PAServer {
    param([string]$DirectoryUrl)
    $global:AcmeDirectoryTest.CurrentServer = $DirectoryUrl
    $global:AcmeDirectoryTest.Servers.Add([pscustomobject]@{ Server = $DirectoryUrl; Root = $global:AcmeDirectoryTest.ConfigRoot })
    Write-Fixture (Join-Path $global:AcmeDirectoryTest.ConfigRoot 'fixture-selected-server.txt') $DirectoryUrl
}
function Get-PAProfile { param([string]$Profile) return [pscustomobject]@{ Profile = $Profile } }
function Get-PAAccount { return [pscustomobject]@{ id = 'existing-fixture-acme-account' } }
function New-PAAccount { throw 'Tests must not create a real or replacement CA account.' }
function New-PACertificate {
    param([string]$Domain, [string]$Name, [string]$Plugin, [hashtable]$PluginArgs,
        [string]$Profile, [switch]$AlwaysNewKey, [Security.SecureString]$PfxPassSecure,
        [switch]$UseModernPfxEncryption, [switch]$UsePfxDerEncoding)
    $global:AcmeDirectoryTest.Certificates++
    $chain = Join-Path $global:AcmeDirectoryTest.ConfigRoot 'fixture-certificate/fullchain.pem'
    $key = Join-Path $global:AcmeDirectoryTest.ConfigRoot 'fixture-certificate/private.key'
    Write-Fixture $chain 'FAKE CERTIFICATE: not usable for TLS'
    Write-Fixture $key 'FAKE KEY: no private credential'
    $certificate = [pscustomobject]@{
        AllSANs = @($Domain); FullChainFile = $chain; KeyFile = $key
        Thumbprint = 'fixture-' + $global:AcmeDirectoryTest.CurrentServer
        NotAfter = [DateTime]::UtcNow.AddDays(6)
    }
    $global:AcmeDirectoryTest.Certificate = $certificate
    return $certificate
}
function Submit-Renewal { param([string]$MainDomain, [string]$Name) $global:AcmeDirectoryTest.Renewals++ }
function Get-PACertificate { param([string]$MainDomain, [string]$Name) return $global:AcmeDirectoryTest.Certificate }
function Start-ScheduledTask { throw 'This state-directory test must never start a task.' }
function Stop-ScheduledTask { throw 'This state-directory test must never stop a task.' }
function Read-Host { throw 'This state-directory test must not ask for CA consent or credentials.' }

function Run-Initialization([switch]$Production, [string]$StatePath = $state) {
    & (Join-Path $scripts 'Initialize-WindowsIpHttps.ps1') -PublishDirectory $publish -StateDirectory $StatePath -PublicIp '203.0.113.25' -Production:$Production
}
function Run-Renewal([string]$StatePath = $state) {
    & (Join-Path $scripts 'Renew-WindowsIpHttps.ps1') -StateDirectory $StatePath
}
function Get-RenewalFailure([string]$StatePath) {
    try { Run-Renewal $StatePath; return '' }
    catch { return $_.Exception.Message }
}

try {
    Run-Initialization
    $stageHome = Join-Path $state 'acme-staging'
    $productionHome = Join-Path $state 'acme-production'
    Check ($global:AcmeDirectoryTest.Imports.Count -eq 1 -and $global:AcmeDirectoryTest.Imports[0].ExistedBeforeImport) 'Staging creates its ACME state directory before importing Posh-ACME'
    Check ($global:AcmeDirectoryTest.Imports[0].RequestedRoot -eq $stageHome -and $global:AcmeDirectoryTest.Imports[0].Force) 'Staging forces module reload with its explicit dedicated configuration path'
    Check ($global:AcmeDirectoryTest.Servers[0].Server -eq 'LE_STAGE' -and $global:AcmeDirectoryTest.Servers[0].Root -eq $stageHome) 'Staging ACME actions use the staging state instead of the default profile'
    Check (-not (Test-Path -LiteralPath (Join-Path $state 'deployment.json'))) 'A staging run does not activate a production deployment'

    Run-Initialization -Production
    Check ($global:AcmeDirectoryTest.Imports.Count -eq 2 -and $global:AcmeDirectoryTest.Imports[1].ExistedBeforeImport) 'Production creates its separate ACME state directory before module import'
    Check ($global:AcmeDirectoryTest.Imports[1].RequestedRoot -eq $productionHome -and $global:AcmeDirectoryTest.Imports[1].Force) 'Production forces reload so the preceding staging configuration is not cached'
    Check ($global:AcmeDirectoryTest.ConfigRoot -eq $productionHome -and $global:AcmeDirectoryTest.Servers[1].Server -eq 'LE_PROD') 'The same PowerShell session switches from staging to the production ACME server and state'
    Check (([IO.File]::ReadAllText((Join-Path $stageHome 'fixture-selected-server.txt'))) -eq 'LE_STAGE' -and ([IO.File]::ReadAllText((Join-Path $productionHome 'fixture-selected-server.txt'))) -eq 'LE_PROD') 'Staging and production configuration files remain isolated'
    $configuration = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $state 'deployment.json') | ConvertFrom-Json
    Check ($configuration.acmeDirectory -eq $productionHome) 'The activated deployment records the actual production ACME directory'
    Check ($global:AcmeDirectoryTest.Fallbacks -eq 0 -and -not (Test-Path -LiteralPath $global:AcmeDirectoryTest.DefaultRoot)) 'Neither initial setup writes ACME state into the default user profile'

    Write-Fixture (Join-Path $state 'data/accounts.json') '{"accounts":[{"id":"preserved-existing-account"}]}'
    $accountsBefore = [IO.File]::ReadAllText((Join-Path $state 'data/accounts.json'))
    Run-Renewal
    Check ($global:AcmeDirectoryTest.Imports.Count -eq 3 -and $global:AcmeDirectoryTest.Imports[2].RequestedRoot -eq $productionHome -and $global:AcmeDirectoryTest.Imports[2].Force) 'Renewal imports the deployment-recorded production state with a forced reload'
    Check ($global:AcmeDirectoryTest.Renewals -eq 1 -and (Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $state 'renewal-status.json') | ConvertFrom-Json).status -eq 'unchanged') 'A valid existing ACME state performs an ordinary renewal check'
    Check ([IO.File]::ReadAllText((Join-Path $state 'data/accounts.json')) -eq $accountsBefore) 'Initialization and renewal checks preserve existing player account data'

    $missingState = Join-Path $fixtureRoot 'MissingAcmeState'
    $missingHome = Join-Path $missingState 'acme-production'
    $configuration.stateDirectory = $missingState
    $configuration.acmeDirectory = $missingHome
    Write-Fixture (Join-Path $missingState 'deployment.json') ($configuration | ConvertTo-Json -Depth 8)
    $importsBefore = $global:AcmeDirectoryTest.Imports.Count
    $renewalsBefore = $global:AcmeDirectoryTest.Renewals
    Check (-not [string]::IsNullOrWhiteSpace((Get-RenewalFailure $missingState))) 'Renewal rejects a missing configured ACME state directory'
    Check ($global:AcmeDirectoryTest.Imports.Count -eq $importsBefore -and $global:AcmeDirectoryTest.Renewals -eq $renewalsBefore) 'Missing renewal state is rejected before module import or any CA renewal action'
    Check (-not (Test-Path -LiteralPath $missingHome)) 'Renewal does not silently create an empty replacement ACME account directory'

    $fileState = Join-Path $fixtureRoot 'FileInsteadOfAcmeState'
    $fileHome = Join-Path $fileState 'acme-production'
    $configuration.stateDirectory = $fileState
    $configuration.acmeDirectory = $fileHome
    Write-Fixture (Join-Path $fileState 'deployment.json') ($configuration | ConvertTo-Json -Depth 8)
    Write-Fixture $fileHome 'A regular file is not valid ACME state.'
    Check (-not [string]::IsNullOrWhiteSpace((Get-RenewalFailure $fileState)) -and $global:AcmeDirectoryTest.Imports.Count -eq $importsBefore) 'Renewal also rejects a regular file used as the ACME directory'
    Check ($global:AcmeDirectoryTest.Fallbacks -eq 0 -and -not (Test-Path -LiteralPath $global:AcmeDirectoryTest.DefaultRoot)) 'Invalid renewal state never falls back to the default profile'

    Write-Fixture (Join-Path $fixtureRoot 'results.txt') (($checks | ForEach-Object { 'PASS: ' + $_ }) -join [Environment]::NewLine)
    Write-Output ('Completed ' + $checks.Count + ' ACME state-directory assertions. Mocked: module import/cache, CA, certificates, tasks and ACL application. Results: ' + $fixtureRoot)
}
finally {
    if ($null -eq $savedAcmeHome) { Remove-Item Env:POSHACME_HOME -ErrorAction SilentlyContinue }
    else { $env:POSHACME_HOME = $savedAcmeHome }
    Remove-Variable -Name AcmeDirectoryTest -Scope Global -ErrorAction SilentlyContinue
}
