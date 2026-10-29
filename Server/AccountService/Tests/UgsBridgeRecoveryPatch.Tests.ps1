#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$PatchScript = '',
    [string]$FixtureDirectory = ''
)

# Run in a separate Windows PowerShell 5.1 process. Every task, process,
# listener, HTTP and sleep operation is an in-process mock. Only isolated fake
# App/Patch/State directories are created; no real tasks or services are used.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Run with Windows PowerShell 5.1.' }
$testDirectory = [IO.Path]::GetDirectoryName($MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($PatchScript)) { $PatchScript = Join-Path $testDirectory '..\Deployment\Apply-UgsBridgeRecoveryPatch.ps1' }
if ([string]::IsNullOrWhiteSpace($FixtureDirectory)) { $FixtureDirectory = Join-Path $testDirectory '..\..\..\tmp\UgsPvp' }
$PatchScript = [IO.Path]::GetFullPath($PatchScript)
if (-not [IO.File]::Exists($PatchScript)) { throw 'Patch script is not available.' }
$fixtureRoot = Join-Path ([IO.Path]::GetFullPath($FixtureDirectory)) ('bridge-recovery-fixtures-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$results = New-Object System.Collections.Generic.List[object]

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Get-FileHashText([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($Path)))).Replace('-', '') }
    finally { $sha.Dispose() }
}
function Get-AclText([string]$Path) {
    return (Microsoft.PowerShell.Security\Get-Acl -LiteralPath $Path).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::All)
}
function Write-Manifest([hashtable]$Fixture, [object[]]$Files) {
    [IO.File]::WriteAllText((Join-Path $Fixture.Patch 'patch-manifest.json'), (@{ files = $Files } | ConvertTo-Json -Depth 8), [Text.Encoding]::UTF8)
}
function New-Fixture([string]$Name, [bool]$IncludePdb = $true) {
    $root = Join-Path $fixtureRoot $Name
    $app = Join-Path $root 'App'; $patch = Join-Path $root 'Patch'; $state = Join-Path $root 'State'
    foreach ($directory in @($app, $patch, $state)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
    foreach ($name in @('AccountService.exe', 'AccountService.dll', 'AccountService.pdb', 'AccountService.deps.json', 'other-runtime.dll', 'appsettings.json')) {
        [IO.File]::WriteAllText((Join-Path $app $name), ('old-' + $name + '-' + $Name), [Text.Encoding]::UTF8)
    }
    [IO.File]::WriteAllText((Join-Path $patch 'AccountService.dll'), ('new-dll-' + $Name), [Text.Encoding]::UTF8)
    if ($IncludePdb) { [IO.File]::WriteAllText((Join-Path $patch 'AccountService.pdb'), ('new-pdb-' + $Name), [Text.Encoding]::UTF8) }
    $runtime = @{ runtimeOptions = @{ tfm = 'net10.0'; includedFrameworks = @(
        @{ name = 'Microsoft.NETCore.App'; version = '10.0.8' },
        @{ name = 'Microsoft.AspNetCore.App'; version = '10.0.8' }
    ) } } | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText((Join-Path $app 'AccountService.runtimeconfig.json'), $runtime, [Text.Encoding]::UTF8)
    $stateFile = Join-Path $state 'ugs-pvp-config.json'
    [IO.File]::WriteAllText($stateFile, '{"fixture":"never-read-state"}', [Text.Encoding]::UTF8)
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $stateAcl = Microsoft.PowerShell.Security\Get-Acl -LiteralPath $stateFile
    $stateAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($currentSid, [Security.AccessControl.FileSystemRights]::ReadData, [Security.AccessControl.AccessControlType]::Deny)))
    Microsoft.PowerShell.Security\Set-Acl -LiteralPath $stateFile -AclObject $stateAcl
    $unreadable = $false
    try { [IO.File]::ReadAllText($stateFile) | Out-Null } catch { $unreadable = $true }
    Assert-True $unreadable 'The State fixture must really be unreadable.'
    foreach ($name in @('AccountService.dll', 'AccountService.pdb')) {
        $acl = Microsoft.PowerShell.Security\Get-Acl -LiteralPath (Join-Path $app $name)
        $guestSid = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-546')
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($guestSid, [Security.AccessControl.FileSystemRights]::ReadData, [Security.AccessControl.AccessControlType]::Deny)))
        Microsoft.PowerShell.Security\Set-Acl -LiteralPath (Join-Path $app $name) -AclObject $acl
    }
    $original = @{}
    foreach ($file in @(Microsoft.PowerShell.Management\Get-ChildItem -LiteralPath $app -File)) {
        $original[$file.Name] = @{ Hash = Get-FileHashText $file.FullName; Acl = Get-AclText $file.FullName }
    }
    $fixture = @{
        Root = $root; App = $app; Patch = $patch; State = $state; StateFile = $stateFile; StateAcl = Get-AclText $stateFile
        Original = $original; IncludePdb = $IncludePdb; TaskState = 'Running'; AppProcess = $true; Listening = $true
        WrongTaskApp = $false; MissingTask = $false; ForeignListener = $false; UnknownPath = $false
        StuckTask = $false; StuckProcess = $false; StuckPort = $false; ReusedPid = $false; AutoRestart = $false
        StopFailure = $false; StartFailure = $false; HealthMode = 'Ready'; StopCalls = 0; StartCalls = 0
        ProcessReads = 0; HandleReads = 0; HealthCalls = 0; StateReads = 0; SleepCalls = @()
    }
    $files = @(@{ file = 'AccountService.dll'; sha256 = Get-FileHashText (Join-Path $patch 'AccountService.dll') })
    if ($IncludePdb) { $files += @{ file = 'AccountService.pdb'; sha256 = Get-FileHashText (Join-Path $patch 'AccountService.pdb') } }
    Write-Manifest $fixture $files
    return $fixture
}

function New-MockProcess([int]$ProcessId, [string]$Path, [bool]$IsOld = $true) {
    $process = [pscustomobject]@{ Id = $ProcessId; ProcessName = 'AccountService'; Path = $Path; IsOld = $IsOld }
    $process | Add-Member -MemberType ScriptProperty -Name Handle -Value { $global:BridgePatchFixture.HandleReads++; return [IntPtr]1 }
    $process | Add-Member -MemberType ScriptProperty -Name HasExited -Value {
        return $this.IsOld -and $global:BridgePatchFixture.StopCalls -gt 0 -and -not $global:BridgePatchFixture.StuckProcess
    }
    $process | Add-Member -MemberType ScriptMethod -Name Dispose -Value { }
    return $process
}
function global:Get-ScheduledTask {
    [CmdletBinding()] param([string]$TaskName, [string]$TaskPath)
    if ($TaskName -cne 'FPS-UgsPvpBridge' -or $TaskPath -cne '\') { throw 'Fixture blocked another task.' }
    $f = $global:BridgePatchFixture
    if ($f.MissingTask) { throw 'FIXTURE_SECRET_TASK_NOT_FOUND' }
    $working = $f.App
    if ($f.WrongTaskApp) { $working = $f.Patch }
    $state = $f.TaskState
    if ($f.StuckTask -and $f.StopCalls -gt 0) { $state = 'Running' }
    return [pscustomobject]@{ TaskName = $TaskName; TaskPath = $TaskPath; State = $state; Actions = @([pscustomobject]@{ WorkingDirectory = $working }) }
}
function global:Stop-ScheduledTask {
    [CmdletBinding()] param([string]$TaskName, [string]$TaskPath)
    if ($TaskName -cne 'FPS-UgsPvpBridge' -or $TaskPath -cne '\') { throw 'Fixture blocked another task stop.' }
    $global:BridgePatchFixture.StopCalls++
    if ($global:BridgePatchFixture.StopFailure) { throw 'FIXTURE_SECRET_STOP_FAILURE' }
    $global:BridgePatchFixture.TaskState = 'Ready'
}
function global:Start-ScheduledTask {
    [CmdletBinding()] param([string]$TaskName, [string]$TaskPath)
    if ($TaskName -cne 'FPS-UgsPvpBridge' -or $TaskPath -cne '\') { throw 'Fixture blocked another task start.' }
    $global:BridgePatchFixture.StartCalls++
    if ($global:BridgePatchFixture.StartFailure) { throw 'FIXTURE_SECRET_START_FAILURE' }
    $global:BridgePatchFixture.TaskState = 'Running'
}
function global:Get-Process {
    [CmdletBinding()] param([int]$Id)
    $f = $global:BridgePatchFixture
    $f.ProcessReads++
    if ($PSBoundParameters.ContainsKey('Id') -and $Id -eq 202) { return New-MockProcess 202 (Join-Path $f.Patch 'AnotherService.exe') }
    if ($PSBoundParameters.ContainsKey('Id') -and $Id -ne 101) { throw 'Fixture blocked an unknown process.' }
    $path = Join-Path $f.App 'AccountService.exe'
    if ($f.UnknownPath) { $path = '' }
    if ($f.AppProcess -and ($f.StopCalls -eq 0 -or $f.StuckProcess -or $f.StartCalls -gt 0)) { return New-MockProcess 101 $path }
    if ($f.StopCalls -gt 0 -and ($f.ReusedPid -or $f.AutoRestart)) { return New-MockProcess 101 $path $false }
}
function global:Get-NetTCPConnection {
    [CmdletBinding()] param([string]$State)
    if ($State -cne 'Listen') { throw 'Fixture blocked another listener operation.' }
    $f = $global:BridgePatchFixture
    if ($f.ForeignListener) { return [pscustomobject]@{ LocalPort = 5080; OwningProcess = 202 } }
    if ($f.Listening -and ($f.StopCalls -eq 0 -or $f.StuckPort -or $f.StartCalls -gt 0)) {
        return [pscustomobject]@{ LocalPort = 5080; OwningProcess = 101 }
    }
}
function global:Invoke-RestMethod {
    [CmdletBinding()] param([uri]$Uri, [int]$TimeoutSec)
    if ($Uri.AbsoluteUri -cne 'http://127.0.0.1:5080/health') { throw 'Fixture blocked a nonlocal HTTP request.' }
    $f = $global:BridgePatchFixture
    $f.HealthCalls++
    if ($f.HealthMode -eq 'Throws') { throw 'FIXTURE_SECRET_HTTP_FAILURE' }
    if ($f.HealthMode -eq 'OldHealth') { return [pscustomobject]@{ status = 'ok'; service = 'fps-account'; apiVersion = 2 } }
    $health = [pscustomobject]@{
        status = 'ok'; service = 'fps-account'; apiVersion = 2
        ugsBridgeEnabled = $true; ugsBridgeReady = $true; ugsLeaseSecondsRemaining = 9.2; ugsEnvironment = 'development'
    }
    switch ($f.HealthMode) {
        'NotReady' { $health.ugsBridgeReady = $false }
        'Expired' { $health.ugsLeaseSecondsRemaining = 0 }
        'Disabled' { $health.ugsBridgeEnabled = $false }
        'StringReady' { $health.ugsBridgeReady = 'true' }
        'StringLease' { $health.ugsLeaseSecondsRemaining = '9' }
        'InfiniteLease' { $health.ugsLeaseSecondsRemaining = [double]::PositiveInfinity }
    }
    return $health
}
function global:Start-Sleep {
    [CmdletBinding()] param([int]$Seconds = 0, [int]$Milliseconds = 0)
    $f = $global:BridgePatchFixture
    $f.SleepCalls += ($Seconds * 1000 + $Milliseconds)
    if ($Seconds -eq 16 -and $f.AutoRestart) { $f.TaskState = 'Running' }
}
function global:Get-Content {
    [CmdletBinding()] param([string[]]$LiteralPath, [string[]]$Path, [switch]$Raw)
    foreach ($target in @($LiteralPath) + @($Path)) {
        if ($target -and [IO.Path]::GetFullPath($target).StartsWith($global:BridgePatchFixture.State, [StringComparison]::OrdinalIgnoreCase)) {
            $global:BridgePatchFixture.StateReads++
            throw 'Fixture blocked a State read.'
        }
    }
    throw 'The patch does not need Get-Content.'
}
# Fail closed if future edits add process-kill or OS-configuration operations.
function global:Stop-Process { throw 'FIXTURE_SECRET_FORBIDDEN_PROCESS_KILL' }
function global:Restart-Service { throw 'FIXTURE_SECRET_FORBIDDEN_SERVICE_ACTION' }
function global:Set-ItemProperty { throw 'FIXTURE_SECRET_FORBIDDEN_OS_WRITE' }

function Invoke-Fixture([hashtable]$Fixture, [string]$App = '', [string]$Patch = '') {
    $global:BridgePatchFixture = $Fixture
    if (-not $App) { $App = $Fixture.App }; if (-not $Patch) { $Patch = $Fixture.Patch }
    $output = @(& $PatchScript -AppDirectory $App -PatchDirectory $Patch *>&1) | Out-String
    Assert-True (-not $output.Contains('FIXTURE_SECRET_')) 'An underlying exception was exposed.'
    $diagnostics = ($output -split '[\r\n]+' | Where-Object { $_ -notlike 'Program backup: *' }) -join "`n"
    Assert-True (-not $diagnostics.Contains($Fixture.Root)) 'Fixture paths were exposed outside the explicit program-backup location.'
    return @{ Failed = $output -match 'Patch failed at stage:|Patch saved, but stage:'; Output = $output }
}
function Assert-StateUntouched([hashtable]$Fixture) {
    Assert-True ($Fixture.StateReads -eq 0) 'State was read.'
    Assert-True ((Get-AclText $Fixture.StateFile) -ceq $Fixture.StateAcl) 'State ACL changed.'
    Assert-True (@(Microsoft.PowerShell.Management\Get-ChildItem -LiteralPath $Fixture.State).Count -eq 1) 'State files changed.'
}
function Assert-AppOriginal([hashtable]$Fixture) {
    foreach ($name in $Fixture.Original.Keys) {
        $file = Join-Path $Fixture.App $name
        Assert-True ((Get-FileHashText $file) -ceq $Fixture.Original[$name].Hash) ('Original bytes changed: ' + $name)
        Assert-True ((Get-AclText $file) -ceq $Fixture.Original[$name].Acl) ('Original ACL changed: ' + $name)
    }
    Assert-StateUntouched $Fixture
}
function Assert-PreflightRejected([hashtable]$Fixture) {
    $outcome = Invoke-Fixture $Fixture
    Assert-True $outcome.Failed 'Unsafe preflight was accepted.'
    Assert-True ($Fixture.StopCalls -eq 0 -and $Fixture.StartCalls -eq 0 -and $Fixture.HealthCalls -eq 0) 'Preflight changed task state.'
    Assert-AppOriginal $Fixture
}
function Run-Case([string]$Name, [scriptblock]$Body) {
    try { & $Body; $results.Add([pscustomobject]@{ Name = $Name; Success = $true; Error = '' }); Write-Host ('PASS ' + $Name) }
    catch { $results.Add([pscustomobject]@{ Name = $Name; Success = $false; Error = $_.Exception.Message }); Write-Host ('FAIL ' + $Name + ': ' + $_.Exception.Message) }
}

Run-Case 'success backs up only programs and preserves existing file ACLs and runtime' {
    $f = New-Fixture 'success'
    $outcome = Invoke-Fixture $f
    Assert-True (-not $outcome.Failed) ('Update failed: ' + $outcome.Output)
    Assert-True ($f.StopCalls -eq 1 -and $f.StartCalls -eq 1 -and $f.HealthCalls -eq 1 -and $f.HandleReads -ge 2) 'Expected verified process handles and one fixed-task stop/start.'
    Assert-True ($f.SleepCalls -contains 16000) 'A full lease-drain interval is required.'
    foreach ($name in $f.Original.Keys) {
        $path = Join-Path $f.App $name
        Assert-True ((Get-AclText $path) -ceq $f.Original[$name].Acl) ('ACL changed: ' + $name)
        if ($name -in @('AccountService.dll', 'AccountService.pdb')) {
            Assert-True ((Get-FileHashText $path) -ceq (Get-FileHashText (Join-Path $f.Patch $name))) ('Program not updated: ' + $name)
        } else { Assert-True ((Get-FileHashText $path) -ceq $f.Original[$name].Hash) ('Unrelated program dependency changed: ' + $name) }
    }
    $backups = @(Microsoft.PowerShell.Management\Get-ChildItem -LiteralPath $f.App -Directory | Where-Object { $_.Name -like 'bridge-recovery-backup-*' })
    Assert-True ($backups.Count -eq 1) 'Exactly one program backup is required.'
    $saved = @(Microsoft.PowerShell.Management\Get-ChildItem -LiteralPath $backups[0].FullName -File)
    Assert-True ($saved.Count -eq 2) 'Only DLL/PDB may be backed up.'
    foreach ($name in @('AccountService.dll', 'AccountService.pdb')) {
        Assert-True ((Get-FileHashText (Join-Path $backups[0].FullName $name)) -ceq $f.Original[$name].Hash) 'Backup did not preserve old program bytes.'
    }
    Assert-True ($outcome.Output -match 'local freshness check only' -and $outcome.Output -match 'Test-UgsPvpBridge-v2.ps1') 'Success must still require cloud lease acceptance.'
    Assert-StateUntouched $f
}
Run-Case 'idle DLL-only update preserves PDB and still drains lease' {
    $f = New-Fixture 'idle-dll-only' $false
    $f.TaskState = 'Ready'; $f.AppProcess = $false; $f.Listening = $false
    $outcome = Invoke-Fixture $f
    Assert-True (-not $outcome.Failed) 'Idle update failed.'
    Assert-True ($f.StopCalls -eq 0 -and $f.StartCalls -eq 1 -and $f.SleepCalls -contains 16000) 'Idle task must start once after draining lease.'
    Assert-True ((Get-FileHashText (Join-Path $f.App 'AccountService.pdb')) -ceq $f.Original['AccountService.pdb'].Hash) 'Omitted PDB changed.'
    Assert-StateUntouched $f
}
Run-Case 'tampered hash is rejected before stopping task' {
    $f = New-Fixture 'bad-hash'
    [IO.File]::WriteAllText((Join-Path $f.Patch 'AccountService.dll'), 'tampered')
    Assert-PreflightRejected $f
}
Run-Case 'unknown manifest file is rejected' {
    $f = New-Fixture 'unknown-file'
    Write-Manifest $f @(@{ file = 'AccountService.exe'; sha256 = Get-FileHashText (Join-Path $f.Patch 'AccountService.dll') })
    Assert-PreflightRejected $f
}
Run-Case 'duplicate manifest file is rejected' {
    $f = New-Fixture 'duplicate-file'
    $entry = @{ file = 'AccountService.dll'; sha256 = Get-FileHashText (Join-Path $f.Patch 'AccountService.dll') }
    Write-Manifest $f @($entry, $entry)
    Assert-PreflightRejected $f
}
Run-Case 'extra manifest properties are rejected' {
    $f = New-Fixture 'extra-manifest'
    $entry = @{ file = 'AccountService.dll'; sha256 = Get-FileHashText (Join-Path $f.Patch 'AccountService.dll'); destination = 'State' }
    Write-Manifest $f @($entry)
    Assert-PreflightRejected $f
}
Run-Case 'wrong runtime is rejected before stopping task' {
    $f = New-Fixture 'wrong-runtime'
    $path = Join-Path $f.App 'AccountService.runtimeconfig.json'
    [IO.File]::WriteAllText($path, '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.9"}]}}')
    $f.Original['AccountService.runtimeconfig.json'].Hash = Get-FileHashText $path
    Assert-PreflightRejected $f
}
Run-Case 'foreign listener is rejected before stopping any task' {
    $f = New-Fixture 'foreign-listener'; $f.ForeignListener = $true
    Assert-PreflightRejected $f
}
Run-Case 'task working directory must match App exactly' {
    $f = New-Fixture 'wrong-task-app'; $f.WrongTaskApp = $true
    Assert-PreflightRejected $f
}
Run-Case 'missing root bridge task is rejected' {
    $f = New-Fixture 'missing-task'; $f.MissingTask = $true
    Assert-PreflightRejected $f
}
Run-Case 'unknown AccountService process path is rejected safely' {
    $f = New-Fixture 'unknown-process-path'; $f.UnknownPath = $true
    Assert-PreflightRejected $f
}
Run-Case 'orphan App process without listener is rejected before stop' {
    $f = New-Fixture 'orphan-no-port'; $f.Listening = $false; $f.TaskState = 'Ready'
    Assert-PreflightRejected $f
}
Run-Case 'stop command failure does not write or restart' {
    $f = New-Fixture 'stop-failure'; $f.StopFailure = $true
    $outcome = Invoke-Fixture $f
    Assert-True ($outcome.Failed -and $f.StartCalls -eq 0) 'Stop failure must stop the update.'
    Assert-AppOriginal $f
}
foreach ($failureMode in @('StuckTask', 'StuckProcess', 'StuckPort', 'ReusedPid')) {
    $capturedMode = $failureMode
    Run-Case ('stop verification refuses ' + $capturedMode) {
        $f = New-Fixture ('stop-' + $capturedMode); $f[$capturedMode] = $true
        if ($capturedMode -eq 'StuckProcess') { $f.Listening = $false }
        $outcome = Invoke-Fixture $f
        Assert-True ($outcome.Failed -and $f.StopCalls -eq 1 -and $f.StartCalls -eq 0) 'Incomplete stop must not write or restart.'
        Assert-AppOriginal $f
    }
}
Run-Case 'task auto restart before lease drain prevents writing' {
    $f = New-Fixture 'auto-restart'; $f.AutoRestart = $true
    $outcome = Invoke-Fixture $f
    Assert-True ($outcome.Failed -and $f.StartCalls -eq 0) 'An unexpected new App process must block the update.'
    Assert-AppOriginal $f
}
Run-Case 'start failure is static and keeps backups' {
    $f = New-Fixture 'start-failure'; $f.StartFailure = $true
    $outcome = Invoke-Fixture $f
    Assert-True ($outcome.Failed -and $f.StartCalls -eq 1 -and $f.HealthCalls -eq 0) 'Start failure should not claim health.'
    Assert-StateUntouched $f
}
foreach ($healthMode in @('OldHealth', 'NotReady', 'Expired', 'Disabled', 'StringReady', 'StringLease', 'InfiniteLease', 'Throws')) {
    $capturedHealth = $healthMode
    Run-Case ('local health must not accept ' + $capturedHealth) {
        $f = New-Fixture ('health-' + $capturedHealth); $f.HealthMode = $capturedHealth
        $outcome = Invoke-Fixture $f
        Assert-True ($outcome.Failed -and $f.StartCalls -eq 1 -and $f.HealthCalls -eq 60) 'Invalid readiness must never be accepted or restarted repeatedly.'
        Assert-True ($outcome.Output -notmatch 'Bridge readiness OK') 'Unready lease was reported healthy.'
        Assert-StateUntouched $f
    }
}
Run-Case 'overlapping directories are rejected' {
    $f = New-Fixture 'overlap'
    $outcome = Invoke-Fixture $f $f.App $f.App
    Assert-True ($outcome.Failed -and $f.StopCalls -eq 0 -and $f.StartCalls -eq 0) 'Overlapping directories were accepted.'
    Assert-AppOriginal $f
}
Run-Case 'relative App directory is rejected' {
    $f = New-Fixture 'relative-path'
    $outcome = Invoke-Fixture $f 'relative\App' $f.Patch
    Assert-True ($outcome.Failed -and $f.StopCalls -eq 0 -and $f.StartCalls -eq 0) 'Relative directory was accepted.'
    Assert-AppOriginal $f
}

$failures = @($results | Where-Object { -not $_.Success })
$reportPath = Join-Path $fixtureRoot 'results.json'
[IO.File]::WriteAllText($reportPath, ($results | ConvertTo-Json -Depth 6), [Text.Encoding]::UTF8)
Write-Host ('Fixture results: ' + $results.Count + ' cases, ' + $failures.Count + ' failed. ' + $reportPath)
if ($failures.Count -gt 0) { exit 1 }
exit 0
