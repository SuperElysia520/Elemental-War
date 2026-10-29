#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$AppDirectory = 'C:\FpsUgsPvp\App',
    [string]$PatchDirectory = ''
)

if ([string]::IsNullOrWhiteSpace($PatchDirectory)) { $PatchDirectory = [IO.Path]::GetDirectoryName($MyInvocation.MyCommand.Path) }

# Only the fixed bridge task and its existing DLL/PDB are changed. Configuration,
# credentials, Unity servers, runtime dependencies and operating-system settings
# are never read or changed by this updater.
& {
    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version 2
    $taskName = 'FPS-UgsPvpBridge'
    $stage = 'Preflight'
    $tracked = New-Object System.Collections.Generic.List[object]

    function Get-PlainPath([string]$Path) {
        if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathRooted($Path) -or $Path.StartsWith('\\')) {
            throw 'Use an absolute local path.'
        }
        $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
        if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) { throw 'Use a dedicated subdirectory.' }
        $ancestor = $full
        while ($ancestor) {
            if ((Test-Path -LiteralPath $ancestor) -and
                ((Get-Item -Force -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Reparse points are not supported.'
            }
            $ancestor = [IO.Path]::GetDirectoryName($ancestor)
        }
        return $full
    }

    function Get-Hash([byte[]]$Bytes) {
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($hasher.ComputeHash($Bytes)).Replace('-', '') }
        finally { $hasher.Dispose() }
    }

    function Get-BridgeTask {
        $tasks = @(Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop)
        if ($tasks.Count -ne 1 -or $tasks[0].TaskName -cne $taskName -or $tasks[0].TaskPath -cne '\') {
            throw 'The fixed bridge task was not found.'
        }
        $actions = @($tasks[0].Actions)
        if ($actions.Count -ne 1 -or (Get-PlainPath $actions[0].WorkingDirectory) -ine $app) {
            throw 'The bridge task belongs to another App directory.'
        }
        return $tasks[0]
    }

    function Get-BridgeListeners {
        # Query all listeners so an empty port is not a cmdlet error.
        return @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object { $_.LocalPort -eq 5080 })
    }

    function Get-AppProcesses {
        $matches = @()
        foreach ($candidate in @(Get-Process -ErrorAction Stop | Where-Object { $_.ProcessName -ieq 'AccountService' })) {
            # An unreadable path must never be interpreted as an absent process.
            $candidatePath = $candidate.Path
            if ([string]::IsNullOrWhiteSpace($candidatePath)) { throw 'A program process path cannot be verified.' }
            if ([IO.Path]::GetFullPath($candidatePath) -ieq $executable) { $matches += $candidate }
        }
        return $matches
    }

    function Test-BridgeStopped {
        $task = Get-BridgeTask
        if ($task.State -in @('Running', 'Queued')) { return $false }
        foreach ($oldProcess in $tracked) { if (-not $oldProcess.HasExited) { return $false } }
        if (@(Get-AppProcesses).Count -ne 0 -or @(Get-BridgeListeners).Count -ne 0) { return $false }
        return $true
    }

    try {
        $app = Get-PlainPath $AppDirectory
        $patch = Get-PlainPath $PatchDirectory
        if (-not [IO.Directory]::Exists($app) -or -not [IO.Directory]::Exists($patch) -or
            $app.Equals($patch, [StringComparison]::OrdinalIgnoreCase) -or
            $app.StartsWith($patch + '\', [StringComparison]::OrdinalIgnoreCase) -or
            $patch.StartsWith($app + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Use separate existing App and patch directories.'
        }
        $executable = Get-PlainPath (Join-Path $app 'AccountService.exe')
        $dll = Get-PlainPath (Join-Path $app 'AccountService.dll')
        if (-not [IO.File]::Exists($executable) -or -not [IO.File]::Exists($dll)) { throw 'Existing program was not found.' }
        $runtimePath = Get-PlainPath (Join-Path $app 'AccountService.runtimeconfig.json')
        $runtime = [IO.File]::ReadAllText($runtimePath) | ConvertFrom-Json
        foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')) {
            if (@($runtime.runtimeOptions.includedFrameworks | Where-Object { $_.name -ceq $framework -and $_.version -ceq '10.0.8' }).Count -ne 1) {
                throw 'This patch requires the original self-contained 10.0.8 program.'
            }
        }
        $manifestPath = Get-PlainPath (Join-Path $patch 'patch-manifest.json')
        $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
        if (@($manifest.PSObject.Properties).Count -ne 1 -or -not $manifest.PSObject.Properties['files']) { throw 'Invalid patch manifest.' }
        $files = @($manifest.files)
        if ($files.Count -lt 1 -or $files.Count -gt 2) { throw 'Invalid patch manifest.' }
        $payload = @{}
        foreach ($entry in $files) {
            if (@($entry.PSObject.Properties).Count -ne 2 -or -not $entry.PSObject.Properties['file'] -or
                -not $entry.PSObject.Properties['sha256'] -or $entry.file -isnot [string] -or $entry.sha256 -isnot [string] -or
                $entry.file -cnotin @('AccountService.dll', 'AccountService.pdb') -or
                $entry.sha256 -cnotmatch '\A[a-fA-F0-9]{64}\z' -or $payload.ContainsKey($entry.file)) {
                throw 'Unknown or duplicate patch file.'
            }
            $source = Get-PlainPath (Join-Path $patch $entry.file)
            $null = Get-PlainPath (Join-Path $app $entry.file)
            $bytes = [IO.File]::ReadAllBytes($source)
            if ($bytes.Length -eq 0 -or (Get-Hash $bytes) -ine $entry.sha256) { throw 'Patch integrity check failed.' }
            $payload[$entry.file] = $bytes
        }
        if (-not $payload.ContainsKey('AccountService.dll')) { throw 'The program patch is missing.' }

        $stage = 'VerifyOwner'
        $task = Get-BridgeTask
        $appProcesses = @(Get-AppProcesses)
        if ($appProcesses.Count -gt 1) { throw 'More than one App process exists.' }
        $listeners = @(Get-BridgeListeners)
        foreach ($listener in $listeners) {
            $owner = Get-Process -Id $listener.OwningProcess -ErrorAction Stop
            try {
                if ([string]::IsNullOrWhiteSpace($owner.Path) -or [IO.Path]::GetFullPath($owner.Path) -ine $executable -or
                    @($appProcesses | Where-Object { $_.Id -eq $owner.Id }).Count -ne 1) {
                    throw 'Port 5080 belongs to another application.'
                }
                $null = $owner.Handle
            } finally { $owner.Dispose() }
        }
        if ($appProcesses.Count -gt 0 -and $task.State -notin @('Running', 'Queued')) {
            throw 'An App process exists outside the running bridge task.'
        }
        foreach ($program in $appProcesses) {
            # Pin the actual process handle, rather than treating a reused PID as
            # the old process. The updater never kills a process by PID or name.
            $null = $program.Handle
            $tracked.Add($program)
        }

        $stage = 'StopTask'
        $task = Get-BridgeTask
        if ($task.State -in @('Running', 'Queued')) { Stop-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop }
        $stopped = $false
        for ($attempt = 0; $attempt -lt 80; $attempt++) {
            if (Test-BridgeStopped) { $stopped = $true; break }
            Start-Sleep -Milliseconds 250
        }
        if (-not $stopped) { throw 'The bridge has not completely stopped.' }

        $stage = 'BackupPrograms'
        $backup = Join-Path $app ('bridge-recovery-backup-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        [IO.Directory]::CreateDirectory($backup) | Out-Null
        foreach ($name in @('AccountService.dll', 'AccountService.pdb')) {
            $existing = Get-PlainPath (Join-Path $app $name)
            if ([IO.File]::Exists($existing)) { Copy-Item -LiteralPath $existing -Destination (Join-Path $backup $name) -ErrorAction Stop }
        }
        Write-Output ('Program backup: ' + $backup)
        if (-not (Test-BridgeStopped)) { throw 'The bridge restarted during backup.' }

        $stage = 'WritePrograms'
        foreach ($name in @('AccountService.dll', 'AccountService.pdb')) {
            if ($payload.ContainsKey($name)) {
                $target = Get-PlainPath (Join-Path $app $name)
                # In-place content replacement preserves an existing file's ACL.
                [IO.File]::WriteAllBytes($target, $payload[$name])
                if ((Get-Hash ([IO.File]::ReadAllBytes($target))) -cne (Get-Hash $payload[$name])) { throw 'Program write verification failed.' }
            }
        }

        $stage = 'LeaseDrain'
        # The lease lasts 15 seconds. Wait a full 16 seconds after all old
        # processes have exited, even when this invocation found an idle task.
        Start-Sleep -Seconds 16
        if (-not (Test-BridgeStopped)) { throw 'The bridge restarted while draining the old lease.' }
        $stage = 'StartTask'
        Start-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop
        $stage = 'HealthCheck'
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            try {
                $health = Invoke-RestMethod 'http://127.0.0.1:5080/health' -TimeoutSec 2 -ErrorAction Stop
                if ($health.status -ceq 'ok' -and $health.service -ceq 'fps-account' -and $health.apiVersion -eq 2 -and
                    $health.ugsBridgeEnabled -is [bool] -and $health.ugsBridgeEnabled -and
                    $health.ugsBridgeReady -is [bool] -and $health.ugsBridgeReady -and
                    ($health.ugsLeaseSecondsRemaining -is [int] -or $health.ugsLeaseSecondsRemaining -is [long] -or
                     $health.ugsLeaseSecondsRemaining -is [double] -or $health.ugsLeaseSecondsRemaining -is [decimal]) -and
                    -not [double]::IsNaN([double]$health.ugsLeaseSecondsRemaining) -and
                    -not [double]::IsInfinity([double]$health.ugsLeaseSecondsRemaining) -and
                    $health.ugsLeaseSecondsRemaining -gt 0) {
                    Write-Output 'Patch applied. Bridge readiness OK: this host currently reports an unexpired confirmed bridge lease.'
                    Write-Output 'This is a local freshness check only. Also run the included Test-UgsPvpBridge-v2.ps1 and require LeaseHealthy, LeaseAdvancing true and EpochStable true.'
                    return
                }
            } catch { }
            Start-Sleep -Milliseconds 500
        }
        Write-Output 'Patch saved, but stage: HealthCheck is not ready. Program backups were preserved; run the included read-only Test-UgsPvpBridge-v2.ps1 diagnosis.'
    }
    catch {
        # Exception details may contain process or task arguments. Print only a
        # fixed stage, never task actions, config values or raw HTTP responses.
        Write-Output ('Patch failed at stage: ' + $stage + '. No configuration or credentials were accessed. Program backups, if created, were preserved.')
    }
    finally {
        foreach ($oldProcess in $tracked) { $oldProcess.Dispose() }
    }
}
