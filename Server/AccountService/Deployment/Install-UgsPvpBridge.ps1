#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$PublishDirectory = '',
    [string]$InstallDirectory = 'C:\FpsUgsPvp\App',
    [string]$StateDirectory = 'C:\FpsUgsPvp\State',
    [string]$ExistingConfiguration = '',
    [string]$ProjectId = '',
    [string]$EnvironmentId = '',
    [string]$EnvironmentName = 'development',
    [string]$ServiceAccountKeyId = '',
    [string]$ServerAddress = '',
    [ValidateRange(1, 24535)][int]$StartPort = 7777,
    [ValidateRange(1, 20)][int]$ServerCount = 10,
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$taskName = 'FPS-UgsPvpBridge'
$trustedSids = @('S-1-5-18', 'S-1-5-19', 'S-1-5-32-544')

function Get-LocalPath([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or -not [IO.Path]::IsPathRooted($Value) -or
        $Value.StartsWith('\\') -or $Value -match '["\r\n]') { throw 'Use an absolute local path without quotes or newlines.' }
    $full = [IO.Path]::GetFullPath($Value).TrimEnd('\')
    if ($full.Length -lt 6 -or $full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) { throw 'Use a dedicated subdirectory.' }
    $ancestor = $full
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -Force -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Reparse points are not supported for deployment paths.'
            }
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $full
}
function Test-Overlap([string]$Left, [string]$Right) {
    return $Left.Equals($Right, [StringComparison]::OrdinalIgnoreCase) -or
        $Left.StartsWith($Right + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $Right.StartsWith($Left + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Assert-EmptyDestination([string]$Path) {
    if ((Test-Path -LiteralPath $Path) -and
        (-not (Test-Path -LiteralPath $Path -PathType Container) -or @(Get-ChildItem -Force -LiteralPath $Path).Count -ne 0)) {
        throw 'A destination is nonempty. Existing programs, State, accounts and certificates were preserved. Use new dedicated directories.'
    }
}
function Set-PrivateDirectory([string]$Path, [bool]$WorkerWrites = $false) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sidText in $trustedSids) {
        $rights = if ($sidText -eq 'S-1-5-19') { if ($WorkerWrites) { 'Modify' } else { 'ReadAndExecute' } } else { 'FullControl' }
        $sid = New-Object Security.Principal.SecurityIdentifier($sidText)
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, $rights, 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Read-RestrictedConfiguration([string]$Path) {
    $configAcl = Get-Acl -LiteralPath $Path
    foreach ($entry in $configAcl.Access) {
        if ($entry.AccessControlType -ne 'Allow') { continue }
        $entrySid = $entry.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($entrySid -notin $trustedSids) { throw 'Existing configuration is readable by an untrusted identity; restrict its ACL before reuse.' }
    }
    try { return ([IO.File]::ReadAllText($Path) | ConvertFrom-Json) }
    catch { throw 'Existing configuration is not valid JSON. Its contents were not printed.' }
}
function Quote-Literal([string]$Value) { return "'" + $Value.Replace("'", "''") + "'" }
function Write-PrivateText([string]$Path, [string]$Value) {
    [IO.File]::WriteAllText($Path, $Value, (New-Object Text.UTF8Encoding($true)))
}

if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'AccountService.dll')) { $PSScriptRoot } else { Split-Path -Parent $PSScriptRoot }
}
$source = Get-LocalPath $PublishDirectory
$app = Get-LocalPath $InstallDirectory
$state = Get-LocalPath $StateDirectory
foreach ($pair in @(@($source, $app), @($source, $state), @($app, $state))) {
    if (Test-Overlap $pair[0] $pair[1]) { throw 'Upload, program and private State directories must be separate and must not contain each other.' }
}
foreach ($required in @('AccountService.dll', 'AccountService.deps.json', 'AccountService.runtimeconfig.json', 'appsettings.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required) -PathType Leaf)) { throw ('Incomplete published program: ' + $required) }
}
$publishedFiles = @(Get-ChildItem -Force -Recurse -LiteralPath $source)
foreach ($entry in $publishedFiles) {
    if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The upload contains a reparse point.' }
    $relative = $entry.FullName.Substring($source.Length + 1)
    if ($relative -match '(^|[\\/])(data|state|tls|acme-production|acme-staging|\.git)([\\/]|$)|accounts\.json$|\.(pem|key)$|ugs-pvp-config\.json$') {
        throw 'The upload contains private state or account data. Publish clean program files only.'
    }
}
Assert-EmptyDestination $app
Assert-EmptyDestination $state
$conflictingTasks = @(Get-ScheduledTask -ErrorAction Stop | Where-Object {
    $_.TaskName -in @($taskName, 'FPS-AccountService', 'FPS-AccountService-Certificate')
})
if ($conflictingTasks.Count -ne 0) { throw 'An existing FPS backend task conflicts with this first-install workflow. Inspect it manually; no task or data was changed.' }
$listeners = @(Get-NetTCPConnection -State Listen -LocalPort 5080 -ErrorAction SilentlyContinue)
if ($listeners.Count -ne 0) { throw 'TCP 5080 is already in use. No process was stopped or replaced.' }
try { $runtime = [IO.File]::ReadAllText((Join-Path $source 'AccountService.runtimeconfig.json')) | ConvertFrom-Json }
catch { throw 'Invalid runtime configuration in the published program.' }
$runtimeProperties = @($runtime.runtimeOptions.PSObject.Properties.Name)
$selfContained = (Test-Path -LiteralPath (Join-Path $source 'AccountService.exe')) -and 'includedFrameworks' -in $runtimeProperties
$dotnetPath = ''
if (-not $selfContained) {
    $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    if (-not ((& $dotnetPath --list-runtimes) -match '^Microsoft.AspNetCore.App 10\.')) { throw 'Install the Windows x64 .NET 10 ASP.NET Core Runtime first.' }
}

$reuse = $null
if (-not [string]::IsNullOrWhiteSpace($ExistingConfiguration)) {
    $existing = Get-LocalPath $ExistingConfiguration
    if (-not (Test-Path -LiteralPath $existing -PathType Leaf)) { throw 'Existing configuration does not exist.' }
    $reuse = Read-RestrictedConfiguration $existing
    try {
        $ProjectId = [string]$reuse.UgsPvpBridge.ProjectId
        $EnvironmentId = [string]$reuse.UgsPvpBridge.EnvironmentId
        $EnvironmentName = [string]$reuse.UgsPvpBridge.EnvironmentName
        $ServiceAccountKeyId = [string]$reuse.UgsPvpBridge.ServiceAccountKeyId
        $ServerAddress = [string]$reuse.PvpMatchmaking.ServerAddress
        $StartPort = [int]$reuse.PvpMatchmaking.StartPort
        $ServerCount = [int]$reuse.PvpMatchmaking.ServerCount
    }
    catch { throw 'Existing configuration is incomplete. Its contents were not printed.' }
}
if ($Install) {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run installation in an Administrator PowerShell window.' }
    foreach ($field in @('ProjectId', 'EnvironmentId', 'ServiceAccountKeyId', 'ServerAddress')) {
        if ([string]::IsNullOrWhiteSpace((Get-Variable -Name $field -ValueOnly))) {
            Set-Variable -Name $field -Value (Read-Host ('Enter ' + $field))
        }
    }
}
$parsedGuid = [Guid]::Empty
if (-not [Guid]::TryParse($ProjectId, [ref]$parsedGuid) -or $parsedGuid -eq [Guid]::Empty) { throw 'ProjectId must be a nonempty Unity project UUID.' }
if (-not [Guid]::TryParse($EnvironmentId, [ref]$parsedGuid) -or $parsedGuid -eq [Guid]::Empty) { throw 'EnvironmentId must be the environment UUID, not its name.' }
if ($EnvironmentName -notmatch '^[a-z0-9][a-z0-9-]{0,39}$') { throw 'EnvironmentName is invalid.' }
if ($ServiceAccountKeyId -notmatch '^[A-Za-z0-9_-]{8,128}$') { throw 'ServiceAccountKeyId is invalid.' }
if ([Uri]::CheckHostName($ServerAddress) -eq [UriHostNameType]::Unknown -or $ServerAddress -match '[\s:/\\]') { throw 'ServerAddress must be the public game-server IPv4 address or DNS hostname, without scheme or port.' }
if ($StartPort -lt 1 -or $StartPort -gt 24535 -or $ServerCount -lt 1 -or $ServerCount -gt 20 -or $StartPort + $ServerCount - 1 + 41000 -gt 65535) { throw 'Invalid dedicated-server or discovery port range.' }
if (-not $Install) {
    Write-Output 'Preflight passed. No files, tasks, ACLs, processes or firewall rules were changed. Use -Install for an explicit first installation.'
    return
}

$secret = $null
$secure = $null
$secretPointer = [IntPtr]::Zero
try {
    if ($null -ne $reuse) { $secret = [string]$reuse.UgsPvpBridge.ServiceAccountSecretKey }
    else {
        $secure = Read-Host 'Enter the UGS service-account secret key (hidden; do not paste it into chat)' -AsSecureString
        $secretPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        $secret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($secretPointer)
    }
    if ($null -eq $secret -or $secret -cnotmatch '\A[\x21-\x39\x3B-\x7E]{8,1024}\z') {
        throw 'Service-account Secret Key is invalid. Copy only the Secret Key value, not Key ID or Key ID:Secret. It must contain 8-1024 visible ASCII characters without spaces, line breaks or colons. Re-enter it at the hidden prompt.'
    }
    # Destinations were verified empty above. Never copy, move, delete or migrate legacy State.
    Set-PrivateDirectory $app
    Set-PrivateDirectory $state
    $worker = Join-Path $state 'worker'
    Set-PrivateDirectory $worker $true
    foreach ($entry in @($publishedFiles | Where-Object { -not $_.PSIsContainer })) {
        $relative = $entry.FullName.Substring($source.Length + 1)
        $target = Join-Path $app $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath $entry.FullName -Destination $target
    }
    $configPath = Join-Path $state 'ugs-pvp-config.json'
    $configuration = [ordered]@{
        UgsPvpBridge = [ordered]@{
            Enabled = $true; ProjectId = $ProjectId; EnvironmentId = $EnvironmentId; EnvironmentName = $EnvironmentName
            ServiceAccountKeyId = $ServiceAccountKeyId; ServiceAccountSecretKey = $secret
            MailboxId = 'fps_pvp_bridge_v1'; PollSeconds = 1
        }
        PvpMatchmaking = [ordered]@{ ServerAddress = $ServerAddress; StartPort = $StartPort; ServerCount = $ServerCount }
    }
    Write-PrivateText $configPath ($configuration | ConvertTo-Json -Depth 5)
    $runnerPath = Join-Path $state 'Start-UgsPvpBridge.ps1'
    $runner = @(
        '#Requires -Version 5.1', '$ErrorActionPreference = ''Stop''',
        ('$env:FPS_UGS_PVP_CONFIG = ' + (Quote-Literal $configPath)),
        '$env:ASPNETCORE_ENVIRONMENT = ''Production''',
        '$env:FPS_ACCOUNT_BACKEND = ''UnityGamingServices''',
        ('Set-Location -LiteralPath ' + (Quote-Literal $app))
    )
    $programArguments = ' --contentRoot ' + (Quote-Literal $app) + ' --urls ''http://127.0.0.1:5080'' --AccountStorePath ' + (Quote-Literal (Join-Path $worker 'bridge-runtime.json'))
    if ($selfContained) { $runner += '& ' + (Quote-Literal (Join-Path $app 'AccountService.exe')) + $programArguments }
    else { $runner += '& ' + (Quote-Literal $dotnetPath) + ' ' + (Quote-Literal (Join-Path $app 'AccountService.dll')) + $programArguments }
    $runner += 'exit $LASTEXITCODE'
    Write-PrivateText $runnerPath ($runner -join "`r`n")
    $poolPath = Join-Path $app 'Start-PvpServerPool.ps1'
    if (-not (Test-Path -LiteralPath $poolPath)) { $poolPath = Join-Path $app 'Deployment\Start-PvpServerPool.ps1' }
    $poolLauncher = @(
        '#Requires -Version 5.1', 'param([Parameter(Mandatory=$true)][string]$ServerExe, [switch]$Stop)',
        '$ErrorActionPreference = ''Stop''',
        '$previousBackend = $env:FPS_ACCOUNT_BACKEND', '$previousUrl = $env:FPS_ACCOUNT_SERVER_URL', 'try {',
        '  $env:FPS_ACCOUNT_BACKEND = ''UnityGamingServices''',
        '  $env:FPS_ACCOUNT_SERVER_URL = ''http://127.0.0.1:5080''',
        ('  & ' + (Quote-Literal $poolPath) + ' -ServerExe $ServerExe -StartPort ' + $StartPort + ' -ServerCount ' + $ServerCount + ' -AccountServerUrl ''http://127.0.0.1:5080'' -Stop:$Stop'),
        '} finally { $env:FPS_ACCOUNT_BACKEND = $previousBackend; $env:FPS_ACCOUNT_SERVER_URL = $previousUrl }'
    )
    Write-PrivateText (Join-Path $state 'Start-UgsPvpServers.ps1') ($poolLauncher -join "`r`n")
    $powershellPath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $taskAction = New-ScheduledTaskAction -Execute $powershellPath -Argument ('-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "' + $runnerPath + '"') -WorkingDirectory $app
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId 'S-1-5-19' -LogonType ServiceAccount -RunLevel Limited
    $taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -StartWhenAvailable
    $task = New-ScheduledTask -Action $taskAction -Trigger (New-ScheduledTaskTrigger -AtStartup) -Principal $taskPrincipal -Settings $taskSettings
    Register-ScheduledTask -TaskName $taskName -InputObject $task -ErrorAction Stop | Out-Null
    Start-ScheduledTask -TaskName $taskName
    Write-Output ('Installed startup task ' + $taskName + '. Check http://127.0.0.1:5080/health on this server.')
    Write-Output ('Dedicated-server launcher: ' + (Join-Path $state 'Start-UgsPvpServers.ps1'))
    Write-Output 'No firewall rules, certificate tasks, existing dedicated servers or legacy account data were changed.'
}
finally {
    if ($secretPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretPointer) }
    if ($null -ne $secure) { $secure.Dispose() }
    $secret = $null; $secure = $null; $reuse = $null; $configuration = $null
}
