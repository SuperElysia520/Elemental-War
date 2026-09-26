param(
    [Parameter(Mandatory = $true)][string]$ClientExe,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$AccountServiceUrl = 'http://127.0.0.1:5080',
    [ValidateRange(1, 65535)][int]$ServerPort = 7777,
    [ValidateRange(120, 600)][int]$TimeoutSeconds = 300,
    [switch]$InspectOcclusion
)
$ErrorActionPreference = 'Stop'
$clientPath = (Resolve-Path -LiteralPath $ClientExe).Path
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$uri = [Uri]$AccountServiceUrl
if (-not $uri.IsAbsoluteUri -or ($uri.Scheme -ne 'https' -and -not ($uri.Scheme -eq 'http' -and $uri.IsLoopback))) {
    throw 'AccountServiceUrl must use HTTPS or loopback HTTP.'
}
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runPath = Join-Path $outputPath $runId
New-Item -ItemType Directory -Path $runPath | Out-Null
$previousEnvironment = @{}
foreach ($name in @('FPS_PROBE_USERNAME', 'FPS_PROBE_PASSWORD', 'FPS_ACCOUNT_CLIENT_URL')) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$processes = @()
try {
    foreach ($role in @('host', 'join')) {
        $username = 'probe_' + $role + '_' + $runId
        $randomBytes = New-Object byte[] 32
        $random = [Security.Cryptography.RandomNumberGenerator]::Create()
        $random.GetBytes($randomBytes)
        $random.Dispose()
        $password = [Convert]::ToBase64String($randomBytes)
        $body = @{ username = $username; password = $password } | ConvertTo-Json -Compress
        # Real registration; generated passwords exist only in this process and child environments, never arguments or files.
        $null = Invoke-RestMethod -Method Post -Uri ($AccountServiceUrl.TrimEnd('/') + '/api/auth/register') -ContentType 'application/json' -Body $body
        $env:FPS_PROBE_USERNAME = $username
        $env:FPS_PROBE_PASSWORD = $password
        $env:FPS_ACCOUNT_CLIENT_URL = $AccountServiceUrl.TrimEnd('/')
        $logPath = Join-Path $runPath ($role + '-player.log')
        $arguments = @('-pvpMigrationProbe', $role, '-pvpProbeRunId', $runId, '-pvpProbeOutput', ('"' + $runPath + '"'),
            '-pvpProbeServerPort', $ServerPort, '-pvpProbeTimeout', ($TimeoutSeconds - 15),
            '-screen-width', '1280', '-screen-height', '720', '-screen-fullscreen', '0', '-logFile', ('"' + $logPath + '"'))
        if ($InspectOcclusion) { $arguments += @('-pvpProbeOcclusion', '1') }
        $processes += Start-Process -FilePath $clientPath -ArgumentList $arguments -WindowStyle Hidden -PassThru
        $password = $body = $null
    }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (@($processes | Where-Object { -not $_.HasExited }).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    $failed = $false
    foreach ($role in @('host', 'join')) {
        $reportPath = Join-Path $runPath ($role + '-report.json')
        if (-not (Test-Path -LiteralPath $reportPath)) { Write-Output "$role`: FAIL (no report)"; $failed = $true; continue }
        $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
        Write-Output ($role + ': ' + $report.status + ' / ' + $report.stage)
        if ($report.status -ne 'PASS') { $failed = $true }
    }
    Write-Output ('Reports: ' + $runPath)
    if ($failed) { throw 'PVP migration smoke test failed or timed out; inspect the reports and player logs.' }
}
finally {
    foreach ($process in $processes) { if (-not $process.HasExited) { Stop-Process -InputObject $process -Force } }
    foreach ($name in $previousEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
}
