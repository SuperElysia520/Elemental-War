# Local orchestration tests only: ACME, Windows tasks, and TLS are replaced with explicit fakes.
# This script never installs a module, requests a certificate, registers a task, or opens a port.
#Requires -Version 5.1
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$serviceDirectory = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $serviceDirectory ('test-results/deployment-' + [Guid]::NewGuid().ToString('N'))
}
$testRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$commonPath = Join-Path $serviceDirectory 'Deployment/IpHttps.Common.ps1'
. $commonPath
$originalSource = Get-Content -Raw -LiteralPath (Join-Path $serviceDirectory 'Deployment/Renew-WindowsIpHttps.ps1')
$fakeBoundary = @'
. '__COMMON__'
function Wait-FpsLocalTls($Configuration, [string]$Thumbprint) {
    if ($script:FailNewTls -and $Thumbprint -eq 'NEW') { throw 'Simulated TLS activation failure.' }
    $actualChain = [IO.File]::ReadAllText((Join-Path $Configuration.stateDirectory 'tls/fullchain.pem'))
    $actualKey = [IO.File]::ReadAllText((Join-Path $Configuration.stateDirectory 'tls/private.key'))
    if ($actualChain -ne ($Thumbprint + '-chain') -or $actualKey -ne ($Thumbprint + '-key')) { throw 'Certificate pair does not match expected activation.' }
}
'@.Replace('__COMMON__', $commonPath.Replace("'", "''"))
$source = $originalSource.Replace(". (Join-Path `$PSScriptRoot 'IpHttps.Common.ps1')", $fakeBoundary)
$renew = [ScriptBlock]::Create($source)
function Import-Module { param($Name, $MinimumVersion, [switch]$Force) if ($Name -ne 'Posh-ACME') { throw 'Unexpected module request.' } }
function Set-PAServer { param($Name) if ($Name -ne 'LE_PROD') { throw 'Unexpected CA request.' } }
function Submit-Renewal { param($MainDomain, $Name) $script:RenewalChecks++ }
function Get-PACertificate { param($MainDomain, $Name) return $script:Certificate }
function Stop-ScheduledTask { param($TaskName) $script:TaskState = 'Ready'; $script:Stops++ }
function Start-ScheduledTask { param($TaskName) $script:TaskState = 'Running'; $script:Starts++ }
function Get-ScheduledTask { param($TaskName) return [pscustomobject]@{ State = $script:TaskState } }

function New-TestState([string]$Name, [string]$CertificateThumbprint) {
    $state = Join-Path $testRoot $Name
    $null = New-Item -ItemType Directory -Path (Join-Path $state 'tls'),(Join-Path $state 'acme'),(Join-Path $state 'data')
    [IO.File]::WriteAllText((Join-Path $state 'tls/fullchain.pem'), 'OLD-chain')
    [IO.File]::WriteAllText((Join-Path $state 'tls/private.key'), 'OLD-key')
    [IO.File]::WriteAllText((Join-Path $state 'data/accounts.json'), 'existing-account-data')
    [IO.File]::WriteAllText((Join-Path $state 'acme/fullchain.cer'), ($CertificateThumbprint + '-chain'))
    [IO.File]::WriteAllText((Join-Path $state 'acme/cert.key'), ($CertificateThumbprint + '-key'))
    $config = @{
        ownerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        stateDirectory = $state; acmeDirectory = (Join-Path $state 'acme'); publicIp = '114.66.59.53'
        taskName = 'FAKE-ACCOUNT-TASK'; orderName = 'fps-account-ip'
        certificateThumbprint = 'OLD'; certificateExpiresUtc = [DateTime]::UtcNow.AddDays(2).ToString('o')
    }
    Write-FpsJson (Join-Path $state 'deployment.json') $config
    $script:Certificate = [pscustomobject]@{
        AllSANs = @('114.66.59.53'); NotAfter = [DateTime]::UtcNow.AddDays(6)
        FullChainFile = (Join-Path $state 'acme/fullchain.cer'); KeyFile = (Join-Path $state 'acme/cert.key')
        Thumbprint = $CertificateThumbprint
    }
    $script:TaskState = 'Running'; $script:Starts = 0; $script:Stops = 0; $script:RenewalChecks = 0; $script:FailNewTls = $false
    return $state
}
function Assert-Test([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw ('FAIL: ' + $Message) }
    $script:Results.Add('PASS: ' + $Message)
}
$script:Results = New-Object 'Collections.Generic.List[string]'

$state = New-TestState 'unchanged' 'OLD'
& $renew -StateDirectory $state
$status = Get-Content -Raw -LiteralPath (Join-Path $state 'renewal-status.json') | ConvertFrom-Json
Assert-Test ($status.status -eq 'unchanged' -and $script:Starts -eq 0 -and $script:Stops -eq 0 -and $script:RenewalChecks -eq 1) 'An unchanged certificate checks renewal once without interrupting the service.'

$state = New-TestState 'renewed' 'NEW'
& $renew -StateDirectory $state
$status = Get-Content -Raw -LiteralPath (Join-Path $state 'renewal-status.json') | ConvertFrom-Json
$config = Read-FpsHttpsConfiguration $state
Assert-Test ($status.status -eq 'renewed' -and $config.certificateThumbprint -eq 'NEW' -and $script:Starts -eq 1 -and $script:Stops -eq 1) 'A renewed certificate activates once and records its fingerprint.'
Assert-Test ([IO.File]::ReadAllText((Join-Path $state 'data/accounts.json')) -eq 'existing-account-data') 'Certificate replacement preserves account data.'

$state = New-TestState 'rollback' 'NEW'
$script:FailNewTls = $true
$failed = $false
try { & $renew -StateDirectory $state } catch { $failed = $_.Exception.Message -match 'Simulated TLS' }
$status = Get-Content -Raw -LiteralPath (Join-Path $state 'renewal-status.json') | ConvertFrom-Json
$config = Read-FpsHttpsConfiguration $state
Assert-Test ($failed -and $status.status -eq 'failed' -and $config.certificateThumbprint -eq 'OLD') 'A failed TLS activation returns failure and retains the old deployment fingerprint.'
Assert-Test ([IO.File]::ReadAllText((Join-Path $state 'tls/fullchain.pem')) -eq 'OLD-chain' -and [IO.File]::ReadAllText((Join-Path $state 'tls/private.key')) -eq 'OLD-key' -and $script:TaskState -eq 'Running' -and $script:Starts -eq 2) 'Failed activation restores the original certificate pair and restarts the service.'
$lockCheck = [IO.File]::Open((Join-Path $state 'renew.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
$lockCheck.Dispose()
Assert-Test $true 'Failure releases the renewal lock for the next scheduled attempt.'

$state = New-TestState 'wrong-san' 'NEW'
$script:Certificate.AllSANs = @('192.0.2.1')
$failed = $false
try { & $renew -StateDirectory $state } catch { $failed = $_.Exception.Message -match 'IP SAN' }
Assert-Test ($failed -and $script:Starts -eq 0 -and $script:Stops -eq 0) 'A certificate for the wrong IP is rejected before stopping the service.'

$report = Join-Path $testRoot 'results.txt'
[IO.File]::WriteAllLines($report, $script:Results)
$script:Results
Write-Output ('Report: ' + $report)
