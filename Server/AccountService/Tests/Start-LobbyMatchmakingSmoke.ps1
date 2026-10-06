param(
    [Parameter(Mandatory = $true)][string]$ClientExe,
    [Parameter(Mandatory = $true)][string]$ServerExe,
    [Parameter(Mandatory = $true)][string]$ServiceDll,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateRange(10000, 24000)][int]$GamePort = 17777
)
$ErrorActionPreference = 'Stop'
$clientPath = (Resolve-Path -LiteralPath $ClientExe).Path
$serverPath = (Resolve-Path -LiteralPath $ServerExe).Path
$servicePath = (Resolve-Path -LiteralPath $ServiceDll).Path
$runPath = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $runPath -Force | Out-Null
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$httpPort = $listener.LocalEndpoint.Port
$listener.Stop()
$endpoint = 'http://127.0.0.1:' + $httpPort
@{ endpoint = $endpoint; gamePort = $GamePort } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runPath 'run.json') -Encoding utf8
Write-Output ('Running isolated lobby smoke test: ' + $runPath)
$previous = @{}
foreach ($key in @('ASPNETCORE_ENVIRONMENT','FPS_ACCOUNT_CLIENT_URL','FPS_ACCOUNT_SERVER_URL','FPS_PROBE_USERNAME','FPS_PROBE_PASSWORD')) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}
$owned = @()
try {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:FPS_ACCOUNT_CLIENT_URL = $endpoint
    $env:FPS_ACCOUNT_SERVER_URL = $endpoint
    $serviceArgs = @(('"' + $servicePath + '"'), ('--urls=' + $endpoint),
        ('--AccountStorePath="' + (Join-Path $runPath 'accounts.json') + '"'),
        ('--PvpMatchmaking:StartPort=' + $GamePort), '--PvpMatchmaking:ServerCount=2')
    $owned += Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList $serviceArgs -WorkingDirectory (Split-Path $servicePath -Parent) -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        try { $null = Invoke-RestMethod -Uri ($endpoint + '/health'); $ready = $true; break }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'Isolated account/matchmaking service did not start.' }
    foreach ($port in @($GamePort, ($GamePort + 1))) {
        $serverArgs = @('-batchmode','-nographics','-dedicatedPvpServer','-port',$port,'-logFile',
            ('"' + (Join-Path $runPath ('server-' + $port + '.log')) + '"'))
        $owned += Start-Process -FilePath $serverPath -ArgumentList $serverArgs -WorkingDirectory (Split-Path $serverPath -Parent) -WindowStyle Hidden -PassThru
    }
    foreach ($role in @('A','B')) {
        $env:FPS_PROBE_USERNAME = 'party_' + $role + '_' + (Split-Path $runPath -Leaf)
        $env:FPS_PROBE_PASSWORD = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $body = @{username=$env:FPS_PROBE_USERNAME;password=$env:FPS_PROBE_PASSWORD} | ConvertTo-Json -Compress
        $null = Invoke-RestMethod -Method Post -Uri ($endpoint + '/api/auth/register') -ContentType 'application/json' -Body $body
        $body = $null
        $clientArgs = @('-partySmoke',$role,'-partySmokeOutput',('"' + $runPath + '"'),'-partySmokePort',$GamePort,
            '-screen-width','1280','-screen-height','720','-screen-fullscreen','0','-logFile',('"' + (Join-Path $runPath ($role + '-player.log')) + '"'))
        $owned += Start-Process -FilePath $clientPath -ArgumentList $clientArgs -WorkingDirectory (Split-Path $clientPath -Parent) -WindowStyle Hidden -PassThru
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(210)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Test-Path -LiteralPath (Join-Path $runPath 'A-report.json')) -and (Test-Path -LiteralPath (Join-Path $runPath 'B-report.json'))) { break }
        Start-Sleep -Milliseconds 500
    }
    foreach ($role in @('A','B')) {
        $path = Join-Path $runPath ($role + '-report.json')
        if (-not (Test-Path -LiteralPath $path)) { throw ($role + ' has no report; inspect ' + $runPath) }
        $report = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
        Write-Output ($role + ': ' + $report.status + ', team=' + $report.team + ', returned=' + $report.returned)
        if ($report.status -ne 'PASS') { throw ($role + ': ' + $report.error + '; reports: ' + $runPath) }
    }
    Write-Output ('Reports: ' + $runPath)
}
finally {
    foreach ($process in $owned) { if (-not $process.HasExited) { Stop-Process -InputObject $process -Force } }
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
}
