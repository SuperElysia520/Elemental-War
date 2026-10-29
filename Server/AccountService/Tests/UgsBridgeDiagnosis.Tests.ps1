#Requires -Version 5.1
param([string]$DiagnosisPath = (Join-Path $PSScriptRoot '../Deployment/Test-UgsPvpBridge.ps1'))
$ErrorActionPreference = 'Stop'

# Exercise the real read-only diagnosis with fake HTTP and clock samples only.
# No service, task, listener, credential, Cloud Save item or real network is used.
$diagnosis = (Resolve-Path -LiteralPath $DiagnosisPath).Path
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('FPS-UgsBridgeDiagnosis-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
$checks = New-Object 'Collections.Generic.List[string]'
$fakeSecret = 'fixture-secret-never-print-56'
$fakeToken = 'fixture-token-never-print-78'
$fakeEpoch = '0123456789abcdef0123456789abcdef'
$otherEpoch = 'abcdef0123456789abcdef0123456789'
$baseTime = [DateTimeOffset]::Parse('2026-10-23T08:00:00Z')
$configuration = @{ UgsPvpBridge = @{
    Enabled = $true; ProjectId = '11111111-1111-4111-8111-111111111111'
    EnvironmentId = '22222222-2222-4222-8222-222222222222'; EnvironmentName = 'development'
    ServiceAccountKeyId = 'fixture-key-id-123'; ServiceAccountSecretKey = $fakeSecret
    MailboxId = 'fps_pvp_bridge_v1'; PollSeconds = 1
} }
$configPath = Join-Path $fixtureRoot 'config.json'
[IO.File]::WriteAllText($configPath, ($configuration | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($true)))
$configBefore = [IO.File]::ReadAllBytes($configPath)
$expectedResults = @{
    'normal renewing lease' = 'LeaseHealthy'; 'missing lease' = 'LeaseMissing'
    'token 201 renewing lease' = 'LeaseHealthy'; 'token 201 malformed JSON' = 'TokenResponseInvalid'
    'token 201 missing accessToken' = 'TokenResponseInvalid'; 'token 204 empty response' = 'TokenResponseInvalid'
    'expired lease' = 'LeaseExpired'; 'server clock behind' = 'LeaseExpired'
    'token HTTP 401' = 'TokenExchangeFailed'; 'token HTTP 403' = 'TokenExchangeFailed'; 'token HTTP 0' = 'TokenExchangeFailed'
    'token HTTP 302' = 'TokenExchangeFailed'; 'Cloud Save HTTP 302' = 'CloudReadFailed'
    'Cloud Save HTTP 401' = 'CloudReadFailed'; 'Cloud Save HTTP 403' = 'CloudReadFailed'; 'Cloud Save HTTP 0' = 'CloudReadFailed'
    'malformed JSON' = 'CloudResponseInvalid'; 'string expiry' = 'LeaseInvalid'
    'cloud Date missing' = 'CloudTimeUnavailable'; 'epoch changes' = 'BridgeRestartedDuringCheck'
    'fresh but not renewing' = 'LeaseNotAdvancing'; 'token response missing accessToken' = 'TokenResponseInvalid'
}

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw ('FAIL: ' + $Message) }
    $checks.Add($Message)
    Write-Output ('PASS: ' + $Message)
}
function New-Reply([int]$Status, [string]$Content, $CloudDate, [DateTimeOffset]$LocalUtc) {
    return [pscustomobject]@{ StatusCode = $Status; Content = $Content; CloudDate = $CloudDate; LocalUtcMidpoint = $LocalUtc }
}
function New-LeaseReply([int]$Index, [long]$ExpiresAt, [string]$Epoch = $fakeEpoch, [int]$ClockOffset = 0, [switch]$NoDate) {
    $cloudTime = $baseTime.AddSeconds(7 * $Index)
    $value = @{ version = 1; epoch = $Epoch; expiresAt = $ExpiresAt }
    $content = @{ results = @(@{ key = 'bridge'; value = $value; writeLock = 'fixture-write-lock' }) } | ConvertTo-Json -Depth 6 -Compress
    $date = $cloudTime
    if ($NoDate) { $date = $null }
    return New-Reply 200 $content $date ($cloudTime.AddSeconds($ClockOffset))
}
function New-NormalReplies {
    $rows = @()
    for ($i = 0; $i -lt 3; $i++) { $rows += New-LeaseReply $i ($baseTime.AddSeconds(7 * $i + 15).ToUnixTimeMilliseconds()) }
    return $rows
}
function Invoke-Case([string]$Name, [object[]]$Replies, [scriptblock]$Verify, [int]$TokenStatus = 200, [string]$TokenContent = '') {
    $tokenContentProvided = $PSBoundParameters.ContainsKey('TokenContent')
    & {
        param($Name, $Replies, $Verify, $TokenStatus, $TokenContent, $TokenContentProvided)
        . $diagnosis -ConfigPath $configPath
        $state = @{ Index = 0; Sleeps = 0; Calls = (New-Object 'Collections.Generic.List[object]') }
        if (-not $TokenContentProvided) { $TokenContent = @{ accessToken = $fakeToken } | ConvertTo-Json -Compress }
        function Start-Sleep { param($Seconds, $Milliseconds) $state.Sleeps++ }
        function Invoke-UgsBridgeHttp {
            param([string]$Uri, [string]$Authorization, [string]$Method, [AllowNull()][string]$Body)
            $state.Calls.Add([pscustomobject]@{
                Uri = $Uri; Authorization = $Authorization; Method = $Method; Body = $Body
                Tls12 = [Net.ServicePointManager]::SecurityProtocol -eq [Net.SecurityProtocolType]::Tls12
            })
            if ($Method -eq 'POST') { return New-Reply $TokenStatus $TokenContent $baseTime $baseTime }
            if ($state.Index -ge $Replies.Count) { throw 'FIXTURE_UNEXPECTED_HTTP_REQUEST' }
            $next = $Replies[$state.Index]
            $state.Index++
            return $next
        }
        # Capture every stream so privacy checks include accidental diagnostics.
        $previousTls = [Net.ServicePointManager]::SecurityProtocol
        $output = @(Invoke-UgsBridgeDiagnosis -ConfigPath $configPath *>&1)
        $reports = @($output | Where-Object { $null -ne $_ -and $_.PSObject.Properties.Name -contains 'Result' })
        Check ($reports.Count -eq 1) ($Name + ': returns one fixed report')
        $report = $reports[0]
        Check ($report.Result -ceq $expectedResults[$Name]) ($Name + ': returns the discriminating result category')
        Check ([Net.ServicePointManager]::SecurityProtocol -eq $previousTls -and @($state.Calls | Where-Object { -not $_.Tls12 }).Count -eq 0) ($Name + ': pins TLS only during diagnosis and restores prior protocol')
        $serialized = $output | ConvertTo-Json -Depth 15 -Compress
        $privateValues = @($fakeSecret, $fakeToken, $fakeEpoch, $otherEpoch,
            $configuration.UgsPvpBridge.ServiceAccountKeyId, $configuration.UgsPvpBridge.ProjectId,
            $configuration.UgsPvpBridge.EnvironmentId, 'fixture-write-lock',
            [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($configuration.UgsPvpBridge.ServiceAccountKeyId + ':' + $fakeSecret)))
        $safe = $true
        foreach ($privateValue in $privateValues) { if ($serialized.Contains($privateValue)) { $safe = $false } }
        Check $safe ($Name + ': all streams and JSON redact credentials, IDs and lease contents')
        $tokenUri = 'https://services.api.unity.com/auth/v2/token-exchange?projectId=' + $configuration.UgsPvpBridge.ProjectId + '&environmentId=' + $configuration.UgsPvpBridge.EnvironmentId
        $privateUri = 'https://cloud-save.services.api.unity.com/v1/data/projects/' + $configuration.UgsPvpBridge.ProjectId + '/custom/fps_pvp_bridge_v1/private/items?keys=bridge'
        $validRoutes = $state.Calls.Count -ge 1
        foreach ($call in $state.Calls) {
            if ($call.Method -eq 'POST') {
                $expected = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($configuration.UgsPvpBridge.ServiceAccountKeyId + ':' + $fakeSecret))
                if ($call.Uri -cne $tokenUri -or $call.Authorization -cne $expected -or $call.Body -ne '{}') { $validRoutes = $false }
            } elseif ($call.Method -ne 'GET' -or $call.Uri -cne $privateUri -or $call.Authorization -cne ('Bearer ' + $fakeToken)) { $validRoutes = $false }
        }
        Check $validRoutes ($Name + ': uses only expected official scoped read routes')
        & $Verify $report $state
    } $Name $Replies $Verify $TokenStatus $TokenContent $tokenContentProvided
}

try {
    foreach ($scriptPath in @($diagnosis, $PSCommandPath)) {
        $parseTokens = $null; $parseErrors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$parseTokens, [ref]$parseErrors)
        Check (@($parseErrors).Count -eq 0) 'Windows PowerShell 5.1 parses the production or fixture script'
    }
    Invoke-Case 'normal renewing lease' @(New-NormalReplies) {
        param($r, $s)
        Check ($r.TokenHttp -eq 200 -and @($r.Samples).Count -eq 3 -and $s.Sleeps -eq 2) 'normal: three samples without a real wait'
        Check (@($r.Samples | Where-Object { -not $_.Present -or -not $_.SchemaValid -or -not $_.CloudDateAvailable -or $_.RemainingSeconds -le 0 }).Count -eq 0) 'normal: valid leases stay live by cloud time'
        Check ($r.LeaseAdvancing -eq $true -and $r.EpochStable -eq $true) 'normal: renewal advances with stable ownership'
    }
    # Auth v2 documents HTTP 201 Created. A successful status still requires a
    # usable accessToken; accepting all 2xx must not bypass response validation.
    Invoke-Case 'token 201 renewing lease' @(New-NormalReplies) {
        param($r, $s)
        Check ($r.DiagnosticVersion -eq 2 -and $r.TokenHttp -eq 201) '201: v2 report retains the actual successful exchange status'
        Check (@($r.Samples).Count -eq 3 -and $s.Calls.Count -eq 4 -and $s.Sleeps -eq 2) '201: valid accessToken continues through all three lease reads'
        Check ($r.LeaseAdvancing -eq $true -and $r.EpochStable -eq $true) '201: normal heartbeat is classified as healthy'
    } -TokenStatus 201
    Invoke-Case 'token 201 malformed JSON' @() {
        param($r, $s)
        Check ($r.TokenHttp -eq 201 -and $s.Calls.Count -eq 1 -and @($r.Samples).Count -eq 0) '201 malformed JSON: rejected before any lease read'
    } -TokenStatus 201 -TokenContent ('{"unclosed":"' + $fakeSecret)
    Invoke-Case 'token 201 missing accessToken' @() {
        param($r, $s)
        Check ($r.TokenHttp -eq 201 -and $s.Calls.Count -eq 1 -and @($r.Samples).Count -eq 0) '201 missing token: rejected before any lease read'
    } -TokenStatus 201 -TokenContent ('{"unexpected":"' + $fakeSecret + '"}')
    Invoke-Case 'token 204 empty response' @() {
        param($r, $s)
        Check ($r.TokenHttp -eq 204 -and $s.Calls.Count -eq 1 -and @($r.Samples).Count -eq 0) '204: empty successful response cannot authorize a private read'
    } -TokenStatus 204 -TokenContent ''
    $missing = 0..2 | ForEach-Object { New-Reply 200 '{"results":[]}' ($baseTime.AddSeconds(7 * $_)) ($baseTime.AddSeconds(7 * $_)) }
    Invoke-Case 'missing lease' @($missing) {
        param($r, $s)
        Check (@($r.Samples).Count -gt 0 -and @($r.Samples | Where-Object { $_.Present -eq $true }).Count -eq 0) 'missing: absent key is reported without inventing a lease'
        Check ($r.LeaseAdvancing -ne $true) 'missing: no renewal claimed'
    }
    $expired = 0..2 | ForEach-Object { New-LeaseReply $_ ($baseTime.AddSeconds(-20).ToUnixTimeMilliseconds()) }
    Invoke-Case 'expired lease' @($expired) {
        param($r, $s)
        Check (@($r.Samples | Where-Object { $_.SchemaValid -and $_.RemainingSeconds -lt 0 }).Count -gt 0) 'expired: structurally valid lease is expired by cloud time'
        Check ($r.LeaseAdvancing -ne $true) 'expired: unchanged expiry is not renewal'
    }
    $behind = 0..2 | ForEach-Object { New-LeaseReply $_ ($baseTime.AddSeconds(7 * $_ - 45).ToUnixTimeMilliseconds()) -ClockOffset -60 }
    Invoke-Case 'server clock behind' @($behind) {
        param($r, $s)
        Check (@($r.Samples | Where-Object { $_.RemainingSeconds -lt 0 -and $_.ServerUtcMinusCloudSeconds -le -59 }).Count -eq 3) 'clock: cloud expiry catches a lease which looks fresh on the server'
        Check ($r.LeaseAdvancing -eq $true -and $r.EpochStable -eq $true) 'clock: renewal evidence is retained separately from freshness'
        Check ($r.ClockSkewDetected -eq $true) 'clock: skew is flagged without changing system time'
    }
    foreach ($status in @(302, 401, 403, 0)) {
        Invoke-Case ('token HTTP ' + $status) @() {
            param($r, $s)
            Check ($r.TokenHttp -eq $TokenStatus -and $s.Calls.Count -eq 1 -and @($r.Samples).Count -eq 0) 'token failure: no Cloud Save request even if error body contains an accessToken'
        } -TokenStatus $status -TokenContent ('{"accessToken":"' + $fakeToken + '","error":"' + $fakeSecret + '"}')
        $bad = 0..2 | ForEach-Object { New-Reply $status ('{"error":"' + $fakeSecret + '"}') $baseTime $baseTime }
        Invoke-Case ('Cloud Save HTTP ' + $status) @($bad) {
            param($r, $s)
            Check ($r.TokenHttp -eq 200 -and @($r.Samples | Where-Object { $_.Http -ne $status }).Count -eq 0 -and @($r.Samples).Count -gt 0) 'Cloud Save failure: exact transport or HTTP status is retained'
            Check ($r.LeaseAdvancing -ne $true) 'Cloud Save failure: no renewal claimed'
        }
    }
    $badJson = 0..2 | ForEach-Object { New-Reply 200 ('{"unclosed":"' + $fakeSecret) $baseTime $baseTime }
    Invoke-Case 'malformed JSON' @($badJson) {
        param($r, $s)
        Check (@($r.Samples).Count -gt 0 -and @($r.Samples | Where-Object { $_.SchemaValid -eq $true }).Count -eq 0) 'malformed JSON: cannot be mistaken for a valid lease'
    }
    $badShape = '{"results":[{"key":"bridge","value":{"version":1,"epoch":"' + $fakeEpoch + '","expiresAt":"1792742415000"},"writeLock":"fixture-write-lock"}]}'
    $badValues = 0..2 | ForEach-Object { New-Reply 200 $badShape $baseTime $baseTime }
    Invoke-Case 'string expiry' @($badValues) {
        param($r, $s)
        Check (@($r.Samples).Count -gt 0 -and @($r.Samples | Where-Object { $_.SchemaValid -eq $true }).Count -eq 0) 'schema: numeric-looking string does not pass Cloud Code integer rules'
    }
    $dateMissing = 0..2 | ForEach-Object { New-LeaseReply $_ ($baseTime.AddSeconds(7 * $_ + 15).ToUnixTimeMilliseconds()) -NoDate }
    Invoke-Case 'cloud Date missing' @($dateMissing) {
        param($r, $s)
        Check (@($r.Samples | Where-Object { $_.CloudDateAvailable -eq $true -or $null -ne $_.RemainingSeconds -or $null -ne $_.ServerUtcMinusCloudSeconds }).Count -eq 0) 'missing Date: does not substitute the local clock for cloud freshness'
    }
    $changed = @(New-NormalReplies)
    $changed[1] = New-LeaseReply 1 ($baseTime.AddSeconds(22).ToUnixTimeMilliseconds()) $otherEpoch
    Invoke-Case 'epoch changes' $changed {
        param($r, $s)
        Check ($r.EpochStable -eq $false) 'ownership: a changed epoch is detected without exporting it'
    }
    $unchanged = 0..2 | ForEach-Object { New-LeaseReply $_ ($baseTime.AddSeconds(15).ToUnixTimeMilliseconds()) }
    Invoke-Case 'fresh but not renewing' @($unchanged) {
        param($r, $s)
        Check (@($r.Samples | Where-Object { $_.RemainingSeconds -gt 0 }).Count -eq 3 -and $r.LeaseAdvancing -ne $true) 'fresh snapshot: remaining time alone does not prove ongoing renewal'
    }
    Invoke-Case 'token response missing accessToken' @() {
        param($r, $s)
        Check ($r.TokenHttp -eq 200 -and $s.Calls.Count -eq 1 -and $r.LeaseAdvancing -ne $true) 'token JSON: missing token fails before any private read'
    } -TokenContent ('{"unexpected":"' + $fakeSecret + '"}')

    Check ([Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath)) -ceq [Convert]::ToBase64String($configBefore)) 'all cases: source configuration remains byte-for-byte unchanged'
    Write-Output ('PASS: ' + $checks.Count + ' assertions; HTTP, sleeps and credentials were isolated fixtures.')
}
finally {
    # Only the verified task-specific temp directory is removed.
    $resolved = [IO.Path]::GetFullPath($fixtureRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('FPS-UgsBridgeDiagnosis-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
