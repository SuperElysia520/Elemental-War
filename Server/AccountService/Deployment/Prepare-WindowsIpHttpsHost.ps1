#Requires -Version 5.1
#Requires -RunAsAdministrator
param([string]$WindowsAccount = ([Security.Principal.WindowsIdentity]::GetCurrent().Name))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IpHttps.Common.ps1')
$identity = New-Object Security.Principal.NTAccount($WindowsAccount)
$null = $identity.Translate([Security.Principal.SecurityIdentifier])
Ensure-FpsAcmeUrlReservation $WindowsAccount
Write-Output 'This prepares TCP 80 HTTP-01 validation and TCP 443 HTTPS. Port 5080 remains loopback-only.'
foreach ($port in @(80, 443)) {
    $name = 'FPS-Account-TCP-' + $port
    if (-not (Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -Name $name -DisplayName $name -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port | Out-Null
    }
}
Write-Output ('HTTP challenge URL reserved for ' + $WindowsAccount + '. Also allow TCP 80/443 in the Rainyun firewall/security group.')
