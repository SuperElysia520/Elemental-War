# Shared helpers for Windows Server 2022. Dot-source only from the deployment scripts.
function Get-FpsUrlReservation([string]$Url) {
    # netsh can return exit code 0 for an empty result. Query HTTP.sys directly instead.
    if (-not ('Fps.AccountDeployment.HttpUrlAcl' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace Fps.AccountDeployment {
    public static class HttpUrlAcl {
        [StructLayout(LayoutKind.Sequential)]
        private struct Version { public ushort Major, Minor; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Query { public int Kind; public IntPtr Prefix; public uint Token; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Record { public IntPtr Prefix, Sddl; }
        [DllImport("httpapi.dll", ExactSpelling = true)]
        private static extern uint HttpInitialize(Version version, uint flags, IntPtr reserved);
        [DllImport("httpapi.dll", ExactSpelling = true)]
        private static extern uint HttpTerminate(uint flags, IntPtr reserved);
        [DllImport("httpapi.dll", ExactSpelling = true)]
        private static extern uint HttpQueryServiceConfiguration(IntPtr service, int configId,
            ref Query input, uint inputLength, IntPtr output, uint outputLength,
            out uint requiredLength, IntPtr overlapped);
        public static string Read(string url) {
            uint error = HttpInitialize(new Version { Major = 1, Minor = 0 }, 2, IntPtr.Zero);
            if (error != 0) throw new Win32Exception((int)error, "Cannot initialize HTTP URL reservation query.");
            IntPtr prefix = IntPtr.Zero, buffer = IntPtr.Zero;
            try {
                prefix = Marshal.StringToHGlobalUni(url);
                Query query = new Query { Kind = 0, Prefix = prefix, Token = 0 };
                uint size;
                error = HttpQueryServiceConfiguration(IntPtr.Zero, 2, ref query,
                    (uint)Marshal.SizeOf(typeof(Query)), IntPtr.Zero, 0, out size, IntPtr.Zero);
                if (error == 2) return null;
                if (error != 122 && error != 234) throw new Win32Exception((int)error, "Cannot query HTTP URL reservation.");
                if (size == 0 || size > 1048576) throw new InvalidOperationException("Invalid HTTP URL reservation size.");
                buffer = Marshal.AllocHGlobal((int)size);
                error = HttpQueryServiceConfiguration(IntPtr.Zero, 2, ref query,
                    (uint)Marshal.SizeOf(typeof(Query)), buffer, size, out size, IntPtr.Zero);
                if (error == 2) return null;
                if (error != 0) throw new Win32Exception((int)error, "Cannot read HTTP URL reservation.");
                Record record = (Record)Marshal.PtrToStructure(buffer, typeof(Record));
                if (record.Prefix == IntPtr.Zero || record.Sddl == IntPtr.Zero ||
                    !String.Equals(Marshal.PtrToStringUni(record.Prefix), url, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("HTTP URL reservation response does not match the requested address.");
                string sddl = Marshal.PtrToStringUni(record.Sddl);
                if (String.IsNullOrWhiteSpace(sddl)) throw new InvalidOperationException("HTTP URL reservation has no readable permissions.");
                return sddl;
            }
            finally {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (prefix != IntPtr.Zero) Marshal.FreeHGlobal(prefix);
                HttpTerminate(2, IntPtr.Zero);
            }
        }
    }
}
'@
    }
    return [Fps.AccountDeployment.HttpUrlAcl]::Read($Url)
}

function Test-FpsUrlReservationAllowsUser([string]$Sddl, [string]$UserSid, [string[]]$GroupSids = @()) {
    $descriptor = New-Object Security.AccessControl.RawSecurityDescriptor($Sddl)
    if ($null -eq $descriptor.DiscretionaryAcl) { return $false }
    $denySids = @($UserSid, 'S-1-1-0', 'S-1-5-11') + $GroupSids
    $allowed = $false
    foreach ($ace in $descriptor.DiscretionaryAcl) {
        # Reuse only a direct user grant: the renewal task has a limited, noninteractive token.
        # In particular, an elevated Administrator-group grant does not prove renewal will work.
        if ($ace -isnot [Security.AccessControl.CommonAce] -or $ace.IsCallback) { return $false }
        if (([int]$ace.AceFlags -band [int][Security.AccessControl.AceFlags]::InheritOnly) -ne 0) { continue }
        if (($ace.AccessMask -band 0x30000000) -eq 0) { continue }
        if ($ace.AceQualifier -eq [Security.AccessControl.AceQualifier]::AccessDenied -and $denySids -contains $ace.SecurityIdentifier.Value) { return $false }
        if ($ace.AceQualifier -eq [Security.AccessControl.AceQualifier]::AccessAllowed -and $ace.SecurityIdentifier.Value -eq $UserSid) { $allowed = $true }
    }
    return $allowed
}

function Ensure-FpsAcmeUrlReservation([string]$WindowsAccount) {
    $url = 'http://+:80/.well-known/acme-challenge/'
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $sid = (New-Object Security.Principal.NTAccount($WindowsAccount)).Translate([Security.Principal.SecurityIdentifier]).Value
    $groups = @($identity.Groups | ForEach-Object { $_.Value })
    $sddl = Get-FpsUrlReservation $url
    if ($null -ne $sddl) {
        if ($sid -ne $identity.User.Value) {
            # We cannot infer another account's group token from the administrator running setup.
            $descriptor = New-Object Security.AccessControl.RawSecurityDescriptor($sddl)
            $groups = @($descriptor.DiscretionaryAcl | Where-Object { $_ -is [Security.AccessControl.CommonAce] -and $_.AceQualifier -eq [Security.AccessControl.AceQualifier]::AccessDenied } | ForEach-Object { $_.SecurityIdentifier.Value })
        }
        if (-not (Test-FpsUrlReservationAllowsUser $sddl $sid $groups)) {
            throw 'The ACME URL reservation exists but does not grant this Windows account direct listening permission. Existing permissions were preserved. Inspect: netsh http show urlacl url=http://+:80/.well-known/acme-challenge/'
        }
        return
    }
    # Only add a missing reservation; never delete or replace another application's permissions.
    & netsh http add urlacl ('url=' + $url) ('user=' + $WindowsAccount) listen=yes | Out-Host
    $addError = $LASTEXITCODE
    $sddl = Get-FpsUrlReservation $url
    if ($null -eq $sddl -or -not (Test-FpsUrlReservationAllowsUser $sddl $sid $groups)) {
        throw ('The ACME URL reservation was not granted to this account (netsh exit ' + $addError + '). Existing permissions were preserved.')
    }
}

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
