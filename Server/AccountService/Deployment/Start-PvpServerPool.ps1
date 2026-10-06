#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][string]$ServerExe,
    [ValidateRange(1, 24535)][int]$StartPort = 7777,
    [ValidateRange(1, 20)][int]$ServerCount = 10,
    [string]$AccountServerUrl = 'http://127.0.0.1:5080',
    [switch]$Stop
)
$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $ServerExe).Path
$directory = Split-Path $executable -Parent
if ($StartPort + $ServerCount - 1 + 41000 -gt 65535) { throw 'Discovery port range exceeds 65535.' }
$uri = [Uri]$AccountServerUrl
if (-not $uri.IsAbsoluteUri -or ($uri.Scheme -ne 'https' -and -not ($uri.Scheme -eq 'http' -and $uri.IsLoopback))) {
    throw 'AccountServerUrl must use HTTPS or loopback HTTP.'
}
$previous = [Environment]::GetEnvironmentVariable('FPS_ACCOUNT_SERVER_URL', 'Process')
$created = @()
try {
    $env:FPS_ACCOUNT_SERVER_URL = $AccountServerUrl.TrimEnd('/')
    for ($port = $StartPort; $port -lt $StartPort + $ServerCount; $port++) {
        $pidPath = Join-Path $directory ('pvp-server-' + $port + '.pid')
        $existing = $null
        if (Test-Path -LiteralPath $pidPath) {
            $recorded = 0
            if (-not [int]::TryParse([IO.File]::ReadAllText($pidPath).Trim(), [ref]$recorded)) { throw ('Invalid process record: ' + $pidPath) }
            $existing = Get-Process -Id $recorded -ErrorAction SilentlyContinue
            if ($null -ne $existing -and $existing.Path -ne $executable) { throw 'Recorded PID belongs to another executable; no action was taken.' }
        }
        if ($Stop) {
            if ($null -ne $existing) { Stop-Process -InputObject $existing; Write-Output ('Stopped recorded server on UDP ' + $port) }
            if (Test-Path -LiteralPath $pidPath) { Remove-Item -LiteralPath $pidPath }
            continue
        }
        if ($null -ne $existing) { Write-Output ('UDP ' + $port + ' is already running under this launcher.'); continue }
        $logPath = Join-Path $directory ('server_' + $port + '.log')
        $arguments = @('-batchmode','-nographics','-port',$port,'-logFile',('"' + $logPath + '"'))
        $process = Start-Process -FilePath $executable -ArgumentList $arguments -WorkingDirectory $directory -WindowStyle Hidden -PassThru
        $created += $process
        [IO.File]::WriteAllText($pidPath, $process.Id.ToString())
        Write-Output ('Started PID ' + $process.Id + '; game UDP ' + $port + '; query UDP ' + ($port + 41000))
    }
}
catch {
    foreach ($process in $created) { if (-not $process.HasExited) { Stop-Process -InputObject $process } }
    throw
}
finally { [Environment]::SetEnvironmentVariable('FPS_ACCOUNT_SERVER_URL', $previous, 'Process') }
