#Requires -Version 5.1
#Requires -PSEdition Desktop
#Requires -RunAsAdministrator
param(
    [string]$PublicIp = '',
    [string]$InstallDirectory = '',
    [string]$StateDirectory = '',
    [string]$PackageDirectory = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IpHttps.Common.ps1')

function Get-CheckedPath([string]$Root, [string]$Relative) {
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $result = [IO.Path]::GetFullPath((Join-Path $base $Relative))
    if (-not $result.StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package path.' }
    return $result
}
function Protect-BackupDirectory([string]$Path) {
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sidValue in @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18', 'S-1-5-32-544')) {
        $sid = New-Object Security.Principal.SecurityIdentifier($sidValue)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Copy-PackageFiles($Files, [string]$Source, [string]$Destination) {
    foreach ($file in $Files) {
        $target = Get-CheckedPath $Destination $file.path
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath (Get-CheckedPath $Source $file.path) -Destination $target -Force
    }
}
function Test-InstalledPackageManifest($InstalledFiles, $CandidateManifest, [string]$InstallRoot) {
    $candidate = @($CandidateManifest)
    if ($candidate.Count -lt 10 -or @($candidate | Group-Object path | Where-Object { $_.Count -ne 1 }).Count -gt 0) { return $false }
    foreach ($existing in $InstalledFiles) {
        $relative = $existing.FullName.Substring($InstallRoot.Length + 1).Replace('\', '/')
        $match = @($candidate | Where-Object { $_.path -eq $relative })
        if ($match.Count -ne 1 -or (Get-FileHash -LiteralPath $existing.FullName -Algorithm SHA256).Hash -ne $match[0].sha256) { return $false }
    }
    return $true
}
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path.TrimEnd('\')
$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $package 'package-manifest.json') | ConvertFrom-Json
$manifest = @($manifest)
if ($manifest.Count -lt 10 -or @($manifest | Group-Object path | Where-Object { $_.Count -ne 1 }).Count -gt 0) { throw 'Invalid or incomplete package manifest.' }
foreach ($critical in @('AccountService.exe', 'AccountService.dll', 'AccountService.runtimeconfig.json', 'coreclr.dll', 'appsettings.json', 'Deployment/Install-RainyunBackend.ps1', 'Deployment/IpHttps.Common.ps1', 'Deployment/Prepare-WindowsIpHttpsHost.ps1', 'Deployment/Initialize-WindowsIpHttps.ps1', 'Deployment/Register-WindowsIpHttpsTasks.ps1', 'Deployment/Renew-WindowsIpHttps.ps1')) {
    if (@($manifest | Where-Object { $_.path -eq $critical }).Count -ne 1) { throw ('Required package file is missing: ' + $critical) }
}
foreach ($file in $manifest) {
    $source = Get-CheckedPath $package $file.path
    if ($file.path -match '(^|/|\\)(data|tls|acme-production)(/|\\|$)|accounts\.json|\.(pem|key)$') { throw 'Package contains private state paths.' }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.sha256) { throw ('Package checksum failed: ' + $file.path) }
}
$runtime = Get-Content -Raw -LiteralPath (Join-Path $package 'AccountService.runtimeconfig.json') | ConvertFrom-Json
if (-not (Test-Path -LiteralPath (Join-Path $package 'AccountService.exe')) -or $null -eq $runtime.runtimeOptions.includedFrameworks) { throw 'Use the complete self-contained Rainyun backend package.' }

$serviceTask = Get-ScheduledTask -TaskName 'FPS-AccountService' -ErrorAction SilentlyContinue
$renewalTask = Get-ScheduledTask -TaskName 'FPS-AccountService-Certificate' -ErrorAction SilentlyContinue
if (($null -eq $serviceTask) -ne ($null -eq $renewalTask)) { throw 'Only one FPS account task exists. Inspect the previous installation before continuing.' }
if ($null -ne $serviceTask) {
    if ([string]::IsNullOrWhiteSpace($StateDirectory)) {
        $stateMatch = [regex]::Match($renewalTask.Actions[0].Arguments, '-StateDirectory\s+"([^"]+)"')
        if (-not $stateMatch.Success) { throw 'Cannot locate existing StateDirectory. Pass -StateDirectory explicitly.' }
        $StateDirectory = $stateMatch.Groups[1].Value
    }
}
if ([string]::IsNullOrWhiteSpace($StateDirectory)) { $StateDirectory = Join-Path $env:LOCALAPPDATA 'FpsAccountState' }
$state = [IO.Path]::GetFullPath($StateDirectory).TrimEnd('\')
$configPath = Join-Path $state 'deployment.json'
$config = $null
if (Test-Path -LiteralPath $configPath) {
    $config = Read-FpsHttpsConfiguration $state -AllowAdministrator
    if (-not [string]::IsNullOrWhiteSpace($InstallDirectory) -and [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\') -ne $config.publishDirectory.TrimEnd('\')) { throw 'Existing deployment uses a different program directory. Do not relocate it during update.' }
    $InstallDirectory = $config.publishDirectory
    if ([string]::IsNullOrWhiteSpace($PublicIp)) { $PublicIp = $config.publicIp }
    if ($PublicIp -ne $config.publicIp) { throw 'The IP differs from the existing certificate. IP changes require separate certificate migration.' }
    if ($null -ne $serviceTask -and $serviceTask.Actions[0].WorkingDirectory.TrimEnd('\') -ne $InstallDirectory.TrimEnd('\')) { throw 'Existing task does not match deployment.json. No files were changed.' }
}
elseif ($null -ne $serviceTask) { throw 'Existing service has no matching deployment.json. No files were changed.' }
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) { $InstallDirectory = 'C:\FpsAccount\App' }
$install = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
foreach ($pair in @(@($install, $package), @($install, $state), @($package, $state))) {
    if ($pair[0].Length -lt 10 -or $pair[1].Length -lt 10 -or $pair[0] -eq $pair[1] -or $pair[0].StartsWith($pair[1] + '\', [StringComparison]::OrdinalIgnoreCase) -or $pair[1].StartsWith($pair[0] + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Program, upload package and private state must use separate dedicated directories.' }
}
if ([string]::IsNullOrWhiteSpace($PublicIp)) { $PublicIp = Read-Host '请输入这台雨云服务器的公网 IPv4 地址' }
$ip = $null
if (-not [Net.IPAddress]::TryParse($PublicIp, [ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or [Net.IPAddress]::IsLoopback($ip) -or $ip.Equals([Net.IPAddress]::Any)) { throw '请输入有效的服务器公网 IPv4 地址。' }
if ($null -eq $config) {
    if ((Test-Path -LiteralPath $state) -and -not (Test-Path -LiteralPath (Join-Path $state '.fps-account-state')) -and @(Get-ChildItem -Force -LiteralPath $state).Count -gt 0) { throw 'Private state directory is nonempty and unrecognized. Existing files were preserved.' }
    if ((Test-Path -LiteralPath $install) -and @(Get-ChildItem -Force -LiteralPath $install).Count -gt 0) {
        # A canceled first install can resume only from one complete, trusted package identity.
        # Recovery manifests are read only when their own hash was verified in the new package above.
        $installedFiles = @(Get-ChildItem -LiteralPath $install -File -Recurse)
        $recognized = Test-InstalledPackageManifest $installedFiles $manifest $install
        if (-not $recognized) {
            foreach ($recovery in @($manifest | Where-Object { $_.path -match '^Deployment/RecoveryManifests/[^/\\]+\.json$' })) {
                $previousManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Get-CheckedPath $package $recovery.path) | ConvertFrom-Json
                if (Test-InstalledPackageManifest $installedFiles $previousManifest $install) { $recognized = $true; break }
            }
        }
        if (-not $recognized) { throw 'Program directory contains unrecognized or mixed-version files. Existing files were preserved.' }
    }
    $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in @(80, 443) })
    if ($listeners.Count -gt 0) { throw 'TCP 80 or 443 is already in use. Stop or reconfigure the owning application first; no listener was removed.' }
}
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
if ($null -ne $config -and $config.ownerSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { throw '请使用最初申请证书的 Windows 账号登录并以管理员身份运行；证书身份不能在更新时更换。' }
if (-not (Get-Module -ListAvailable -Name Posh-ACME | Where-Object { $_.Version -ge [version]'4.34.0' })) {
    Write-Host '首次安装证书工具；PowerShell Gallery 若询问信任来源，请核对后继续。'
    Install-Module -Name Posh-ACME -MinimumVersion 4.34.0 -Scope CurrentUser
}
if ($null -ne $config -and $null -ne $serviceTask) {
    # Normal renewal checks happen before taking the same renewal lock for the program update.
    & (Join-Path $install 'Deployment/Renew-WindowsIpHttps.ps1') -StateDirectory $state
    $config = Read-FpsHttpsConfiguration $state -AllowAdministrator
}

$backup = Join-Path (Split-Path -Parent $state) ('FpsAccountBackups/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
foreach ($root in @($install, $package, $state)) {
    if ($backup -eq $root -or $backup.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or $root.StartsWith($backup + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Backup directory must be separate from program, upload and state directories.' }
}
$hadProgram = Test-Path -LiteralPath $install
$database = Join-Path $state 'data/accounts.json'
$newFiles = @($manifest | Where-Object { -not (Test-Path -LiteralPath (Get-CheckedPath $install $_.path)) })
$updateLock = $null
$serviceStopped = $false
$programChanged = $false
$newTasksRequested = $false
try {
    if ($null -ne $config) { $updateLock = [IO.File]::Open((Join-Path $state 'renew.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    Protect-BackupDirectory $backup
    if ($hadProgram) { Copy-Item -LiteralPath $install -Destination (Join-Path $backup 'App') -Recurse }
    if (Test-Path -LiteralPath $database) { Copy-Item -LiteralPath $database -Destination (Join-Path $backup 'accounts.json') }
    if ($null -ne $serviceTask) {
        Write-Host '正在备份并更新原账号服务；账号、证书和任务身份均保留。'
        $serviceStopped = $true
        Stop-FpsAccountTask 'FPS-AccountService'
        if (Test-Path -LiteralPath $database) { Copy-Item -LiteralPath $database -Destination (Join-Path $backup 'accounts.json') -Force }
    }
    $programChanged = $true
    Copy-PackageFiles $manifest $package $install
    if ($null -eq $config) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        & (Join-Path $install 'Deployment/Prepare-WindowsIpHttpsHost.ps1') -WindowsAccount $identity.Name
        Write-Host '请确保雨云控制台已放行 TCP 80 和 443。接下来先测试证书，再申请正式证书。'
        & (Join-Path $install 'Deployment/Initialize-WindowsIpHttps.ps1') -PublishDirectory $install -StateDirectory $state -PublicIp $PublicIp
        & (Join-Path $install 'Deployment/Initialize-WindowsIpHttps.ps1') -PublishDirectory $install -StateDirectory $state -PublicIp $PublicIp -Production
        $config = Read-FpsHttpsConfiguration $state -AllowAdministrator
    }
    if ($null -eq $serviceTask) {
        $newTasksRequested = $true
        & (Join-Path $install 'Deployment/Register-WindowsIpHttpsTasks.ps1') -StateDirectory $state
    }
    else {
        Start-ScheduledTask -TaskName 'FPS-AccountService'
        Wait-FpsLocalTls $config $config.certificateThumbprint
    }
    $health = Invoke-RestMethod -Uri 'http://127.0.0.1:5080/health' -TimeoutSec 10
    if ($health.status -ne 'ok' -or $health.service -ne 'fps-account' -or $health.apiVersion -lt 2) { throw 'Updated backend version check failed.' }
    Write-Host ('安装完成。Unity 账号地址填写：https://' + $PublicIp) -ForegroundColor Green
    Write-Host ('数据目录：' + $state)
    Write-Host ('更新前备份：' + $backup)
    Write-Host '还需用另一台电脑打开该地址的 /health，并实际注册、登录确认公网可用。'
}
catch {
    $installFailure = $_
    if ($null -ne $serviceTask -and $serviceStopped) {
        Write-Warning '更新失败，正在恢复更新前程序；账号数据未覆盖。'
        try {
            Stop-FpsAccountTask 'FPS-AccountService'
            foreach ($file in @($newFiles | Where-Object { $programChanged })) {
                $target = Get-CheckedPath $install $file.path
                if (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force }
            }
            foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $backup 'App') -File -Recurse | Where-Object { $programChanged })) {
                $relative = $file.FullName.Substring((Join-Path $backup 'App').TrimEnd('\').Length + 1)
                $target = Get-CheckedPath $install $relative
                New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
                Copy-Item -LiteralPath $file.FullName -Destination $target -Force
            }
            Start-ScheduledTask -TaskName 'FPS-AccountService'
        }
        catch { Write-Warning ('程序自动恢复未完成，请保留备份目录并排查：' + $backup) }
    }
    elseif ($newTasksRequested) {
        # These names were absent before this attempt. Remove only tasks pointing into this installation.
        try {
            foreach ($name in @('FPS-AccountService', 'FPS-AccountService-Certificate')) {
                $created = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
                if ($null -ne $created) {
                    $working = $created.Actions[0].WorkingDirectory.TrimEnd('\')
                    if ($working -eq $install -or $working.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase)) {
                        Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
                        Unregister-ScheduledTask -TaskName $name -Confirm:$false
                    }
                }
            }
        }
        catch { Write-Warning '首次安装任务未能自动清理，请保留错误信息并检查 FPS-AccountService 计划任务。' }
    }
    throw $installFailure
}
finally { if ($null -ne $updateLock) { $updateLock.Dispose() } }
