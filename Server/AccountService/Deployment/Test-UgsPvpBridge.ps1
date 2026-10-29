#Requires -Version 5.1
[CmdletBinding()]
param([string]$ConfigPath = 'C:\FpsUgsPvp\State\ugs-pvp-config.json')

# Read-only cloud diagnosis. Credentials stay in memory and are sent only to
# the fixed official HTTPS destinations. No task, config, ACL or lease writes.
function Invoke-UgsBridgeHttp {
    param([string]$Uri, [string]$Authorization, [ValidateSet('GET','POST')][string]$Method, [string]$Body)
    $before = [DateTimeOffset]::UtcNow
    $response = $null
    try {
        $target = [Uri]$Uri
        if ($target.Scheme -cne 'https' -or $target.UserInfo -or $target.Port -ne 443 -or
            ($Method -eq 'POST' -and ($target.Host -cne 'services.api.unity.com' -or $target.AbsolutePath -cne '/auth/v2/token-exchange')) -or
            ($Method -eq 'GET' -and ($target.Host -cne 'cloud-save.services.api.unity.com' -or $target.AbsolutePath -notmatch '^/v1/data/projects/[a-fA-F0-9-]{36}/custom/fps_pvp_bridge_v1/private/items$'))) {
            return [pscustomobject]@{ StatusCode = 0; Content = ''; CloudDate = $null; LocalUtcMidpoint = $before }
        }
        $parameters = @{
            Uri = $Uri; Method = $Method; Headers = @{ Authorization = $Authorization }
            UseBasicParsing = $true; MaximumRedirection = 0; TimeoutSec = 10
            ErrorAction = 'Stop'; Verbose = $false; Debug = $false
        }
        if ($Method -eq 'POST') { $parameters.Body = $Body; $parameters.ContentType = 'application/json' }
        $response = Invoke-WebRequest @parameters
        $after = [DateTimeOffset]::UtcNow
        $date = [DateTimeOffset]::MinValue
        $hasDate = [DateTimeOffset]::TryParse([string]$response.Headers['Date'], [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$date)
        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode; Content = [string]$response.Content
            CloudDate = $(if ($hasDate) { $date } else { $null })
            LocalUtcMidpoint = $before.AddTicks([long](($after - $before).Ticks / 2))
        }
    } catch {
        # Do not print exception messages, ErrorDetails, response bodies or URLs.
        $status = 0
        if ($_.Exception.Response -is [Net.HttpWebResponse]) {
            $status = [int]$_.Exception.Response.StatusCode
            $_.Exception.Response.Dispose()
        }
        return [pscustomobject]@{ StatusCode = $status; Content = ''; CloudDate = $null; LocalUtcMidpoint = [DateTimeOffset]::UtcNow }
    } finally { $response = $null; $Authorization = $null; $parameters = $null }
}

function Test-UgsBridgeInteger {
    param($Value)
    if ($Value -isnot [int] -and $Value -isnot [long] -and $Value -isnot [double] -and $Value -isnot [decimal]) { return $false }
    $number = [double]$Value
    return -not [double]::IsNaN($number) -and -not [double]::IsInfinity($number) -and
        $number -ge 0 -and $number -le 9007199254740991 -and [Math]::Truncate($number) -eq $number
}

function Read-UgsBridgeLease {
    param($Response)
    $sample = [pscustomobject]@{
        Http = [int]$Response.StatusCode; ResponseValid = $false; Present = $false; SchemaValid = $false
        CloudDateAvailable = $Response.CloudDate -is [DateTimeOffset]
        RemainingSeconds = $null; ServerUtcMinusCloudSeconds = $null
    }
    $result = [pscustomobject]@{ Sample = $sample; Epoch = $null; ExpiresAt = $null }
    if ($sample.CloudDateAvailable) {
        $sample.ServerUtcMinusCloudSeconds = [Math]::Round(($Response.LocalUtcMidpoint - $Response.CloudDate).TotalSeconds, 1)
    }
    if ($sample.Http -ne 200) { return $result }
    try { $data = ConvertFrom-Json -InputObject $Response.Content -ErrorAction Stop }
    catch { return $result }
    if ($null -eq $data -or $data.results -isnot [Array]) { return $result }
    $rows = @($data.results)
    if ($rows.Count -eq 0) { $sample.ResponseValid = $true; return $result }
    $sample.Present = $true
    if ($rows.Count -ne 1 -or $rows[0].key -cne 'bridge' -or $null -eq $rows[0].value) { return $result }
    $sample.ResponseValid = $true
    $value = $rows[0].value
    $names = @($value.PSObject.Properties.Name)
    if ($names.Count -ne 3 -or $names -cnotcontains 'version' -or $names -cnotcontains 'epoch' -or
        $names -cnotcontains 'expiresAt' -or -not (Test-UgsBridgeInteger $value.version) -or $value.version -ne 1 -or
        $value.epoch -isnot [string] -or $value.epoch -cnotmatch '^[a-fA-F0-9]{32}$' -or
        -not (Test-UgsBridgeInteger $value.expiresAt)) { return $result }
    $sample.SchemaValid = $true
    $result.Epoch = $value.epoch
    $result.ExpiresAt = [long]$value.expiresAt
    if ($sample.CloudDateAvailable) {
        $sample.RemainingSeconds = [Math]::Round(($result.ExpiresAt - $Response.CloudDate.ToUnixTimeMilliseconds()) / 1000.0, 1)
    }
    return $result
}

function Invoke-UgsBridgeDiagnosis {
    param([string]$ConfigPath = 'C:\FpsUgsPvp\State\ugs-pvp-config.json')
    $ErrorActionPreference = 'Stop'
    $VerbosePreference = 'SilentlyContinue'
    $DebugPreference = 'SilentlyContinue'
    $ProgressPreference = 'SilentlyContinue'
    $previousTls = [Net.ServicePointManager]::SecurityProtocol
    $config = $null; $settings = $null; $basic = $null; $token = $null; $auth = $null
    $report = [pscustomobject]@{
        DiagnosticVersion = 2; ReadOnly = $true; Result = 'ConfigurationUnreadable'
        EnvironmentName = ''; TokenHttp = 0; Samples = @()
        LeaseAdvancing = $false; EpochStable = $false; ClockSkewDetected = $false
    }
    try {
        try { $config = Get-Content -LiteralPath $ConfigPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop }
        catch { return $report }
        $settings = $config.UgsPvpBridge
        $project = [Guid]::Empty; $environment = [Guid]::Empty
        if ($null -eq $settings -or $settings.Enabled -isnot [bool] -or -not $settings.Enabled -or
            -not [Guid]::TryParse([string]$settings.ProjectId, [ref]$project) -or $project -eq [Guid]::Empty -or
            -not [Guid]::TryParse([string]$settings.EnvironmentId, [ref]$environment) -or $environment -eq [Guid]::Empty -or
            $settings.MailboxId -cne 'fps_pvp_bridge_v1' -or
            [string]$settings.EnvironmentName -cnotmatch '^[A-Za-z0-9_-]{1,64}$' -or
            [string]$settings.ServiceAccountKeyId -cnotmatch '^[\x21-\x39\x3B-\x7E]{8,1024}$' -or
            [string]$settings.ServiceAccountSecretKey -cnotmatch '^[\x21-\x39\x3B-\x7E]{8,1024}$') {
            $report.Result = 'ConfigurationInvalid'; return $report
        }
        $report.EnvironmentName = [string]$settings.EnvironmentName
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($settings.ServiceAccountKeyId + ':' + $settings.ServiceAccountSecretKey))
        $auth = Invoke-UgsBridgeHttp -Uri ('https://services.api.unity.com/auth/v2/token-exchange?projectId=' + $project.ToString() + '&environmentId=' + $environment.ToString()) -Authorization ('Basic ' + $basic) -Method POST -Body '{}'
        $report.TokenHttp = [int]$auth.StatusCode
        # Token Exchange v2 returns 201 Created. Match HttpClient's 2xx success
        # rule, then still require a valid JSON response with a nonempty token.
        if ($auth.StatusCode -lt 200 -or $auth.StatusCode -ge 300) { $report.Result = 'TokenExchangeFailed'; return $report }
        try { $token = (ConvertFrom-Json -InputObject $auth.Content -ErrorAction Stop).accessToken }
        catch { $report.Result = 'TokenResponseInvalid'; return $report }
        if ($token -isnot [string] -or [string]::IsNullOrWhiteSpace($token) -or $token.Contains("`r") -or $token.Contains("`n")) {
            $report.Result = 'TokenResponseInvalid'; return $report
        }
        $uri = 'https://cloud-save.services.api.unity.com/v1/data/projects/' + $project.ToString() + '/custom/fps_pvp_bridge_v1/private/items?keys=bridge'
        $leases = @()
        for ($index = 0; $index -lt 3; $index++) {
            if ($index -gt 0) { Start-Sleep -Seconds 7 }
            $response = Invoke-UgsBridgeHttp -Uri $uri -Authorization ('Bearer ' + $token) -Method GET
            $lease = Read-UgsBridgeLease $response
            $leases += $lease
            $report.Samples += $lease.Sample
            if ($response.StatusCode -ne 200) { $report.Result = 'CloudReadFailed'; return $report }
        }
        $valid = @($leases | Where-Object { $_.Sample.SchemaValid })
        if ($valid.Count -eq 3) {
            $report.EpochStable = $valid[0].Epoch -ceq $valid[1].Epoch -and $valid[1].Epoch -ceq $valid[2].Epoch
            $report.LeaseAdvancing = $valid[2].ExpiresAt -gt $valid[0].ExpiresAt
        }
        $report.ClockSkewDetected = @($report.Samples | Where-Object {
            $_.CloudDateAvailable -and [Math]::Abs($_.ServerUtcMinusCloudSeconds) -gt 5
        }).Count -gt 0
        $last = $report.Samples[-1]
        if (-not $last.ResponseValid) { $report.Result = 'CloudResponseInvalid' }
        elseif (-not $last.Present) { $report.Result = 'LeaseMissing' }
        elseif (-not $last.SchemaValid) { $report.Result = 'LeaseInvalid' }
        elseif (-not $last.CloudDateAvailable) { $report.Result = 'CloudTimeUnavailable' }
        elseif ($last.RemainingSeconds -le 0) { $report.Result = 'LeaseExpired' }
        elseif ($valid.Count -ne 3) { $report.Result = 'LeaseUnstable' }
        elseif (-not $report.EpochStable) { $report.Result = 'BridgeRestartedDuringCheck' }
        elseif (-not $report.LeaseAdvancing) { $report.Result = 'LeaseNotAdvancing' }
        else { $report.Result = 'LeaseHealthy' }
        return $report
    } catch {
        $report.Result = 'DiagnosticFailed'
        return $report
    } finally {
        [Net.ServicePointManager]::SecurityProtocol = $previousTls
        $config = $null; $settings = $null; $basic = $null; $token = $null; $auth = $null; $response = $null; $lease = $null; $leases = $null
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-UgsBridgeDiagnosis -ConfigPath $ConfigPath | ConvertTo-Json -Depth 5
}
