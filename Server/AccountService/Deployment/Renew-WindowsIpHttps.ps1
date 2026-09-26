#Requires -Version 5.1
#Requires -PSEdition Desktop
param([Parameter(Mandatory = $true)][string]$StateDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IpHttps.Common.ps1')
$config = Read-FpsHttpsConfiguration $StateDirectory
$lock = [IO.File]::Open((Join-Path $config.stateDirectory 'renew.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
    $env:POSHACME_HOME = $config.acmeDirectory
    Import-Module Posh-ACME -MinimumVersion 4.34.0 -Force
    Set-PAServer LE_PROD
    # Default renewal honors the CA/ARI renewal window. Never force issuance on every scheduled run.
    $null = Submit-Renewal -MainDomain $config.publicIp -Name $config.orderName
    $certificate = Get-PACertificate -MainDomain $config.publicIp -Name $config.orderName
    Assert-FpsCertificate $certificate $config.publicIp
    if ($certificate.Thumbprint -eq $config.certificateThumbprint) {
        Write-FpsJson (Join-Path $config.stateDirectory 'renewal-status.json') @{ status = 'unchanged'; checkedUtc = [DateTime]::UtcNow.ToString('o'); expiresUtc = $config.certificateExpiresUtc }
        return
    }
    $tls = Join-Path $config.stateDirectory 'tls'
    $chain = Join-Path $tls 'fullchain.pem'
    $key = Join-Path $tls 'private.key'
    Copy-Item -LiteralPath $chain -Destination ($chain + '.previous') -Force
    Copy-Item -LiteralPath $key -Destination ($key + '.previous') -Force
    Stop-FpsAccountTask $config.taskName
    $previousThumbprint = $config.certificateThumbprint
    $previousExpiry = $config.certificateExpiresUtc
    try {
        Copy-Item -LiteralPath $certificate.FullChainFile -Destination $chain -Force
        Copy-Item -LiteralPath $certificate.KeyFile -Destination $key -Force
        Start-ScheduledTask -TaskName $config.taskName
        Wait-FpsLocalTls $config $certificate.Thumbprint
        $config.certificateThumbprint = $certificate.Thumbprint
        $config.certificateExpiresUtc = $certificate.NotAfter.ToUniversalTime().ToString('o')
        Write-FpsJson (Join-Path $config.stateDirectory 'deployment.json') $config
    }
    catch {
        $activationError = $_
        Stop-FpsAccountTask $config.taskName
        Copy-Item -LiteralPath ($chain + '.previous') -Destination $chain -Force
        Copy-Item -LiteralPath ($key + '.previous') -Destination $key -Force
        Start-ScheduledTask -TaskName $config.taskName
        Wait-FpsLocalTls $config $previousThumbprint
        $config.certificateThumbprint = $previousThumbprint
        $config.certificateExpiresUtc = $previousExpiry
        throw $activationError
    }
    Write-FpsJson (Join-Path $config.stateDirectory 'renewal-status.json') @{ status = 'renewed'; checkedUtc = [DateTime]::UtcNow.ToString('o'); expiresUtc = $config.certificateExpiresUtc }
}
catch {
    $renewalError = $_
    try { Write-FpsJson (Join-Path $config.stateDirectory 'renewal-status.json') @{ status = 'failed'; checkedUtc = [DateTime]::UtcNow.ToString('o'); error = $renewalError.Exception.Message } }
    catch { Write-Warning ('Could not write renewal failure status: ' + $_.Exception.Message) }
    throw $renewalError
}
finally { $lock.Dispose() }
