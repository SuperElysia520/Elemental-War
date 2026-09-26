# Shared helpers for Windows Server 2022. Dot-source only from the deployment scripts.
function Read-FpsHttpsConfiguration([string]$StateDirectory, [switch]$AllowAdministrator) {
    $root = [IO.Path]::GetFullPath($StateDirectory)
    $configuration = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'deployment.json') | ConvertFrom-Json
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $isAdministrator = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($configuration.ownerSid -ne $identity.User.Value -and -not ($AllowAdministrator -and $isAdministrator)) {
        throw 'Run this command as the same Windows account that initialized the ACME certificate.'
    }
    return $configuration
}

function Assert-FpsCertificate($Certificate, [string]$PublicIp) {
    if ($null -eq $Certificate -or $Certificate.AllSANs -notcontains $PublicIp) { throw 'Issued certificate does not contain the requested IP SAN.' }
    if ($Certificate.NotAfter.ToUniversalTime() -lt [DateTime]::UtcNow.AddHours(24)) { throw 'Certificate expires in less than 24 hours.' }
    if (-not (Test-Path -LiteralPath $Certificate.FullChainFile) -or -not (Test-Path -LiteralPath $Certificate.KeyFile)) { throw 'Certificate chain or private key file is missing.' }
}

function Write-FpsJson([string]$Path, $Value) {
    $pending = $Path + '.pending'
    [IO.File]::WriteAllText($pending, ($Value | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($pending, $Path, [Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($pending, $Path) }
}

function Test-FpsLocalTls([string]$PublicIp, [string]$ExpectedThumbprint) {
    # Connect to loopback while validating the public-IP certificate with the normal Windows trust store.
    # No certificate-validation callback or trust bypass is installed.
    $tcp = New-Object Net.Sockets.TcpClient
    try {
        $connect = $tcp.ConnectAsync('127.0.0.1', 443)
        if (-not $connect.Wait(5000)) { throw 'Local HTTPS TCP connection timed out.' }
        $connect.GetAwaiter().GetResult()
        $ssl = New-Object Net.Security.SslStream($tcp.GetStream(), $false)
        try {
            $ssl.ReadTimeout = 5000
            $ssl.WriteTimeout = 5000
            $ssl.AuthenticateAsClient($PublicIp)
            $remote = New-Object Security.Cryptography.X509Certificates.X509Certificate2($ssl.RemoteCertificate)
            if ($remote.Thumbprint -ne $ExpectedThumbprint) { throw 'HTTPS listener has not loaded the new certificate.' }
            $request = [Text.Encoding]::ASCII.GetBytes("GET /health HTTP/1.1`r`nHost: $PublicIp`r`nConnection: close`r`n`r`n")
            $ssl.Write($request, 0, $request.Length)
            $reader = New-Object IO.StreamReader($ssl)
            if ($reader.ReadLine() -notmatch '^HTTP/1\.[01] 200 ') { throw 'HTTPS health check did not return HTTP 200.' }
        }
        finally { if ($null -ne $ssl) { $ssl.Dispose() } }
    }
    finally { $tcp.Dispose() }
}

function Wait-FpsLocalTls($Configuration, [string]$Thumbprint) {
    $lastFailure = ''
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try { Test-FpsLocalTls $Configuration.publicIp $Thumbprint; return }
        catch { $lastFailure = $_.Exception.Message; Start-Sleep -Milliseconds 500 }
    }
    throw ('HTTPS startup validation failed: ' + $lastFailure)
}

function Stop-FpsAccountTask([string]$TaskName) {
    Stop-ScheduledTask -TaskName $TaskName
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if ((Get-ScheduledTask -TaskName $TaskName).State -ne 'Running') { return }
        Start-Sleep -Milliseconds 500
    }
    throw 'Account service did not stop; certificate files were not replaced.'
}
