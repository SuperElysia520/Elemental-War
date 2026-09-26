param(
    [Parameter(Mandatory = $true)]
    [string]$DataDirectory,
    [ValidateSet('Production', 'Development')]
    [string]$Environment = 'Production',
    [string]$ListenUrl = 'http://127.0.0.1:5080',
    [string]$TrustedProxyAddress = ''
)

$ErrorActionPreference = 'Stop'
$serviceDll = Join-Path $PSScriptRoot 'AccountService.dll'
if (-not (Test-Path -LiteralPath $serviceDll -PathType Leaf)) {
    throw 'Run the copy of this script in the published AccountService directory.'
}
$accountDataDirectory = [System.IO.Path]::GetFullPath($DataDirectory)
New-Item -ItemType Directory -Force -Path $accountDataDirectory | Out-Null
$env:ASPNETCORE_ENVIRONMENT = $Environment
if (-not [string]::IsNullOrWhiteSpace($TrustedProxyAddress)) {
    $null = [System.Net.IPAddress]::Parse($TrustedProxyAddress)
    $env:TrustedProxyAddresses__0 = $TrustedProxyAddress
}
else {
    Remove-Item Env:TrustedProxyAddresses__0 -ErrorAction SilentlyContinue
}
& dotnet $serviceDll --contentRoot $PSScriptRoot --urls $ListenUrl --AccountStorePath (Join-Path $accountDataDirectory 'accounts.json')
exit $LASTEXITCODE
