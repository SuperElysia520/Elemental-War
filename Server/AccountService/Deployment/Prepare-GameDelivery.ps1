#Requires -Version 5.1
param(
    [string]$PublicIp = '114.66.59.53',
    [ValidateRange(1, 24535)][int]$GamePort = 7777
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$delivery = Join-Path $projectRoot 'Builds/RifleGirlMigration'
$clientDirectory = Join-Path $delivery 'Client'
$serverDirectory = Join-Path $delivery 'Server'
$ip = [Net.IPAddress]::Parse($PublicIp)
if ($ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or [Net.IPAddress]::IsLoopback($ip)) {
    throw 'Delivery requires the actual public IPv4 address; loopback test endpoints must not be distributed.'
}
$accountUrl = 'https://' + $ip.ToString()
foreach ($executable in @((Join-Path $clientDirectory 'FPS.exe'), (Join-Path $serverDirectory 'FPS_PVP_Server.exe'))) {
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw ('Build the matching new client and server first: ' + $executable) }
}
$encoding = New-Object Text.UTF8Encoding($false)
function Write-DeliveryFile([string]$Directory, [string]$Name, [string]$Text) {
    [IO.File]::WriteAllText((Join-Path $Directory $Name), ($Text.Replace("`r`n", "`n").Replace("`n", "`r`n") + "`r`n"), $encoding)
}
$clientScript = @'
#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$accountUrl = '__ACCOUNT_URL__'
$client = Join-Path $PSScriptRoot 'FPS.exe'
if (-not (Test-Path -LiteralPath $client)) { throw 'Keep this launcher beside FPS.exe and its complete data directory.' }
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $health = Invoke-RestMethod -Uri ($accountUrl + '/health') -Method Get -TimeoutSec 15
    if ($health.status -ne 'ok') { throw 'Unexpected health response.' }
}
catch {
    throw ('The account service is not ready at ' + $accountUrl + '. Ask the server operator to complete HTTPS deployment. No HTTP fallback or certificate bypass is enabled. Details: ' + $_.Exception.Message)
}
$previousUrl = [Environment]::GetEnvironmentVariable('FPS_ACCOUNT_CLIENT_URL', 'Process')
try {
    # The child game inherits this HTTPS address; no machine/user environment setting is changed.
    $env:FPS_ACCOUNT_CLIENT_URL = $accountUrl
    Start-Process -FilePath $client -WorkingDirectory $PSScriptRoot | Out-Null
}
finally { [Environment]::SetEnvironmentVariable('FPS_ACCOUNT_CLIENT_URL', $previousUrl, 'Process') }
'@
$serverScript = @'
#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$port = __GAME_PORT__
$server = Join-Path $PSScriptRoot 'FPS_PVP_Server.exe'
$pidFile = Join-Path $PSScriptRoot ('pvp-server-' + $port + '.pid')
if (-not (Test-Path -LiteralPath $server)) { throw 'Keep this launcher beside the complete new dedicated-server build.' }
if (Test-Path -LiteralPath $pidFile) {
    $runningId = 0
    if ([int]::TryParse([IO.File]::ReadAllText($pidFile).Trim(), [ref]$runningId)) {
        $running = Get-Process -Id $runningId -ErrorAction SilentlyContinue
        if ($null -ne $running -and $running.Path -eq $server) {
            Write-Output ('This server is already running (PID ' + $runningId + ').')
            return
        }
    }
}
$tcp = New-Object Net.Sockets.TcpClient
try {
    $connect = $tcp.ConnectAsync('127.0.0.1', 5080)
    if (-not $connect.Wait(5000)) { throw 'Connection timed out.' }
    $connect.GetAwaiter().GetResult()
}
catch { throw 'Start the account service on this Windows server first. Its private endpoint must listen on 127.0.0.1:5080.' }
finally { $tcp.Dispose() }
$previousUrl = [Environment]::GetEnvironmentVariable('FPS_ACCOUNT_SERVER_URL', 'Process')
try {
    $env:FPS_ACCOUNT_SERVER_URL = 'http://127.0.0.1:5080'
    $logPath = Join-Path $PSScriptRoot ('server_' + $port + '.log')
    $arguments = @('-batchmode', '-nographics', '-port', $port, '-logFile', ('"' + $logPath + '"'))
    $serverProcess = Start-Process -FilePath $server -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru
    [IO.File]::WriteAllText($pidFile, $serverProcess.Id.ToString())
    Start-Sleep -Seconds 2
    if ($serverProcess.HasExited) { throw ('The dedicated server exited during startup. Inspect ' + $logPath) }
    Write-Output ('Started PID ' + $serverProcess.Id + '. Game UDP ' + $port + '; discovery UDP ' + ($port + 41000) + '.')
    Write-Output ('Log: ' + $logPath)
    Write-Output 'Confirm successful listeners and no startup errors in that log before testing external clients.'
}
finally { [Environment]::SetEnvironmentVariable('FPS_ACCOUNT_SERVER_URL', $previousUrl, 'Process') }
'@
$stopScript = @'
#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$server = Join-Path $PSScriptRoot 'FPS_PVP_Server.exe'
$pidFile = Join-Path $PSScriptRoot 'pvp-server-__GAME_PORT__.pid'
if (-not (Test-Path -LiteralPath $pidFile)) { Write-Output 'No process started by this launcher is recorded.'; return }
$runningId = 0
if (-not [int]::TryParse([IO.File]::ReadAllText($pidFile).Trim(), [ref]$runningId)) { throw 'Invalid PID record; no process was stopped.' }
$running = Get-Process -Id $runningId -ErrorAction SilentlyContinue
if ($null -ne $running) {
    if ($running.Path -ne $server) { throw 'The recorded PID belongs to a different executable; no process was stopped.' }
    Stop-Process -InputObject $running
    Write-Output ('Stopped PVP game server PID ' + $runningId + '. Active matches were disconnected; cloud accounts were not changed.')
}
Remove-Item -LiteralPath $pidFile
'@
$wrapper = @'
@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0__SCRIPT__"
if errorlevel 1 pause
endlocal
'@
Write-DeliveryFile $clientDirectory 'Start-FPS.ps1' ($clientScript.Replace('__ACCOUNT_URL__', $accountUrl))
Write-DeliveryFile $clientDirectory 'Start-FPS.cmd' ($wrapper.Replace('__SCRIPT__', 'Start-FPS.ps1'))
Write-DeliveryFile $serverDirectory 'Start-PvpServer.ps1' ($serverScript.Replace('__GAME_PORT__', $GamePort.ToString()))
Write-DeliveryFile $serverDirectory 'Start-PvpServer.cmd' ($wrapper.Replace('__SCRIPT__', 'Start-PvpServer.ps1'))
Write-DeliveryFile $serverDirectory 'Stop-PvpServer.ps1' ($stopScript.Replace('__GAME_PORT__', $GamePort.ToString()))
Write-DeliveryFile $serverDirectory 'Stop-PvpServer.cmd' ($wrapper.Replace('__SCRIPT__', 'Stop-PvpServer.ps1'))
$readme = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'GameDeliveryREADME.zh-CN.md'))
$readme = $readme.Replace('{{PUBLIC_IP}}', $ip.ToString()).Replace('{{GAME_PORT}}', $GamePort.ToString()).Replace('{{DISCOVERY_PORT}}', ($GamePort + 41000).ToString())
Write-DeliveryFile $delivery 'README-Deployment.md' $readme
Write-Output ('Prepared launchers and deployment instructions in ' + $delivery)
Write-Output 'No game/account process was started and no firewall, machine environment, or scheduled task was changed.'
