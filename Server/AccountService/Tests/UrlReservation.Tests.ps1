#Requires -Version 5.1
param([switch]$ReadOnlyNative, [string]$ExistingUrl = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../Deployment/IpHttps.Common.ps1')

if ($ReadOnlyNative) {
    # Queries only: never add/delete an HTTP.sys reservation, firewall rule or certificate.
    $sddl = Get-FpsUrlReservation 'http://+:80/.well-known/acme-challenge/'
    Write-Output ('Native exact URL query: ' + $(if ($null -eq $sddl) { 'absent' } else { 'present' }))
    if ($ExistingUrl) {
        $existing = Get-FpsUrlReservation $ExistingUrl
        if ([string]::IsNullOrEmpty($existing)) { throw 'Expected a known existing URL reservation.' }
        $null = New-Object Security.AccessControl.RawSecurityDescriptor($existing)
        Write-Output 'Native existing URL query: readable security descriptor'
    }
    return
}

# Test real SDDL parsing and orchestration with explicit fake native-query/netsh boundaries.
$results = New-Object 'Collections.Generic.List[string]'
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw ('FAIL: ' + $Message) }
    $results.Add('PASS: ' + $Message)
    Write-Output ('PASS: ' + $Message)
}
$userSid = 'S-1-5-21-101-202-303-500'
$otherSid = 'S-1-5-21-101-202-303-501'
Check (Test-FpsUrlReservationAllowsUser ('D:(A;;GX;;;' + $userSid + ')') $userSid) 'A direct listening grant is reusable'
Check (Test-FpsUrlReservationAllowsUser ('D:(A;;GA;;;' + $userSid + ')') $userSid) 'Generic-all grants are recognized without a GX substring'
Check (-not (Test-FpsUrlReservationAllowsUser ('D:(A;;GW;;;' + $userSid + ')') $userSid)) 'Delegation alone is not listening permission'
Check (-not (Test-FpsUrlReservationAllowsUser ('D:(A;;GX;;;' + $otherSid + ')') $userSid)) 'Another user grant is not reused'
Check (-not (Test-FpsUrlReservationAllowsUser 'D:(A;;GX;;;BA)' $userSid)) 'Elevated Administrator membership is not treated as a limited renewal-task grant'
Check (-not (Test-FpsUrlReservationAllowsUser ('D:(D;;GX;;;' + $userSid + ')(A;;GA;;;' + $userSid + ')') $userSid)) 'A user deny is not bypassed by an allow'
Check (-not (Test-FpsUrlReservationAllowsUser ('D:(D;;GX;;;WD)(A;;GX;;;' + $userSid + ')') $userSid)) 'An Everyone deny is respected'
Check (-not (Test-FpsUrlReservationAllowsUser ('D:(D;;GX;;;BU)(A;;GX;;;' + $userSid + ')') $userSid @('S-1-5-32-545'))) 'A user-group deny is respected'
Check (-not (Test-FpsUrlReservationAllowsUser ('D:(A;IO;GX;;;' + $userSid + ')') $userSid)) 'An inherit-only grant does not authorize this URL'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$script:Grant = 'D:(A;;GX;;;' + $identity.User.Value + ')'
$script:OtherGrant = 'D:(A;;GX;;;' + $otherSid + ')'
function Get-FpsUrlReservation {
    param([string]$Url)
    $script:Queries++
    if ($script:FailQuery) { throw 'Simulated HTTP.sys query failure.' }
    return $script:CurrentSddl
}
function netsh {
    param([Parameter(ValueFromRemainingArguments = $true)][object[]]$Arguments)
    if (($Arguments -join ' ') -notmatch '^http add urlacl ') { throw 'Tests forbid all other netsh operations.' }
    $script:Adds++
    if ($script:Race) { $script:CurrentSddl = $script:OtherGrant; $global:LASTEXITCODE = 183 }
    else { $script:CurrentSddl = $script:Grant; $global:LASTEXITCODE = 0 }
}
function Reset-Fake($Sddl) {
    $script:Queries = 0; $script:Adds = 0; $script:FailQuery = $false; $script:Race = $false
    $script:CurrentSddl = $Sddl
}
function Rejected {
    try { Ensure-FpsAcmeUrlReservation $identity.Name; return $false }
    catch { return $true }
}

Reset-Fake $null
Ensure-FpsAcmeUrlReservation $identity.Name
Check ($script:Adds -eq 1 -and $script:Queries -eq 2) 'An absent reservation is added once and verified by a second native query'
Reset-Fake $script:Grant
Ensure-FpsAcmeUrlReservation $identity.Name
Check ($script:Adds -eq 0) 'Repeated preparation reuses the current user reservation without rewriting it'
Reset-Fake ('D:(A;;GA;;;' + $identity.User.Value + ')')
Ensure-FpsAcmeUrlReservation $identity.Name
Check ($script:Adds -eq 0) 'An existing generic-all user grant needs no system change'
Reset-Fake $script:OtherGrant
Check (Rejected) 'A real permission conflict stops preparation'
Check ($script:Adds -eq 0) 'A different user reservation is never overwritten or deleted'
Reset-Fake $null
$script:FailQuery = $true
Check (Rejected) 'Native query errors are not interpreted as a missing reservation'
Check ($script:Adds -eq 0) 'Failed queries cause no write'
Reset-Fake $null
$script:Race = $true
Check (Rejected) 'A reservation created by another user during setup is preserved'
Check ($script:Adds -eq 1 -and $script:Queries -eq 2) 'A creation race only rechecks permissions and never removes a reservation'

$output = Join-Path $PSScriptRoot ('../test-results/urlacl-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
[IO.File]::WriteAllText((Join-Path $output 'results.txt'), ($results -join [Environment]::NewLine))
Write-Output ('Completed ' + $results.Count + ' URL reservation assertions. Native-query/netsh are faked; no system configuration was changed. Report: ' + $output)
