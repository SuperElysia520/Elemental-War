#Requires -Version 5.1
#Requires -PSEdition Desktop
param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$StateDirectory,
    [string]$PublicIp = '114.66.59.53',
    [switch]$Production
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IpHttps.Common.ps1')
$null = [Net.IPAddress]::Parse($PublicIp)
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $publish 'AccountService.dll'))) { throw 'PublishDirectory must contain AccountService.dll.' }
$state = [IO.Path]::GetFullPath($StateDirectory).TrimEnd('\')
if ($state.Length -lt 10 -or $state -eq [IO.Path]::GetPathRoot($state).TrimEnd('\')) { throw 'Choose a dedicated state directory, not a drive root.' }
if ($state.Equals($publish, [StringComparison]::OrdinalIgnoreCase) -or $state.StartsWith($publish.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'StateDirectory must be outside the replaceable PublishDirectory.'
}
$marker = Join-Path $state '.fps-account-state'
if ((Test-Path -LiteralPath $state) -and -not (Test-Path -LiteralPath $marker) -and @(Get-ChildItem -Force -LiteralPath $state).Count -gt 0) {
    throw 'StateDirectory is nonempty and is not an initialized FPS account state directory. Choose a new dedicated directory.'
}
if ($Production -and (Test-Path -LiteralPath (Join-Path $state 'deployment.json'))) { throw 'Production is already initialized. Use the renewal script; existing account data was not changed.' }
New-Item -ItemType Directory -Force -Path $state | Out-Null
$windowsIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($sidValue in @($windowsIdentity.User.Value, 'S-1-5-18', 'S-1-5-32-544')) {
    $sid = New-Object Security.Principal.SecurityIdentifier($sidValue)
    $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $state -AclObject $acl
[IO.File]::WriteAllText($marker, 'FPS Account Service state; do not place inside the replaceable application directory.')

$channel = if ($Production) { 'production' } else { 'staging' }
$acmeState = Join-Path $state ('acme-' + $channel)
# Posh-ACME only honors POSHACME_HOME when the directory already exists at import time.
New-Item -ItemType Directory -Force -Path $acmeState | Out-Null
$env:POSHACME_HOME = $acmeState
Import-Module Posh-ACME -MinimumVersion 4.34.0 -Force
$ca = if ($Production) { 'LE_PROD' } else { 'LE_STAGE' }
Set-PAServer $ca
if (-not (Get-PAProfile -Profile 'shortlived')) { throw 'This ACME server does not advertise the required shortlived profile.' }
if (-not (Get-PAAccount)) {
    $terms = (Get-PAServer).meta.termsOfService
    if ([string]::IsNullOrWhiteSpace($terms)) { throw 'Unable to obtain current CA terms of service; account was not created.' }
    Write-Output ('Read the current Let''s Encrypt terms: ' + $terms)
    $consent = Read-Host 'After reviewing the terms, type I AGREE to create this ACME account, or press Enter to stop'
    if ($consent -cne 'I AGREE') { throw 'Terms were not accepted. No ACME account was created.' }
    $null = New-PAAccount -AcceptTOS
}
$randomBytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($randomBytes) } finally { $rng.Dispose() }
$pfxSecret = (New-Object Net.NetworkCredential('', [Convert]::ToBase64String($randomBytes))).SecurePassword
$certificate = New-PACertificate -Domain $PublicIp -Name 'fps-account-ip' -Plugin WebSelfHost `
    -PluginArgs @{ WSHPort = 80; WSHTimeout = 180 } -Profile shortlived -AlwaysNewKey `
    -PfxPassSecure $pfxSecret -UseModernPfxEncryption -UsePfxDerEncoding
Assert-FpsCertificate $certificate $PublicIp
if (-not $Production) {
    Write-Output 'Staging validation succeeded. This certificate is untrusted and has NOT been configured on the account service.'
    return
}
$tls = Join-Path $state 'tls'
New-Item -ItemType Directory -Force -Path $tls,(Join-Path $state 'data') | Out-Null
# PEM keys have no passphrase, are protected by the private directory ACL, and never appear in task arguments/logs.
Copy-Item -LiteralPath $certificate.FullChainFile -Destination (Join-Path $tls 'fullchain.pem')
Copy-Item -LiteralPath $certificate.KeyFile -Destination (Join-Path $tls 'private.key')
$config = [ordered]@{
    ownerSid = $windowsIdentity.User.Value; windowsAccount = $windowsIdentity.Name
    publicIp = $PublicIp; publishDirectory = $publish; stateDirectory = $state; acmeDirectory = $acmeState
    taskName = 'FPS-AccountService'; renewalTaskName = 'FPS-AccountService-Certificate'
    orderName = 'fps-account-ip'; certificateThumbprint = $certificate.Thumbprint
    certificateExpiresUtc = $certificate.NotAfter.ToUniversalTime().ToString('o')
}
Write-FpsJson (Join-Path $state 'deployment.json') $config
Write-Output ('Production certificate prepared for https://' + $PublicIp + '; no service/task has been started.')
Write-Output ('StateDirectory: ' + $state)
