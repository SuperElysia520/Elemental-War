#Requires -Version 5.1
#Requires -PSEdition Desktop
#Requires -RunAsAdministrator
param([Parameter(Mandatory = $true)][string]$StateDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'IpHttps.Common.ps1')
$config = Read-FpsHttpsConfiguration $StateDirectory -AllowAdministrator
foreach ($name in @($config.taskName, $config.renewalTaskName)) {
    if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) { throw ('Task already exists; inspect it before replacing: ' + $name) }
}
$serviceExe = Join-Path $config.publishDirectory 'AccountService.exe'
$runtimeConfig = Get-Content -Raw -LiteralPath (Join-Path $config.publishDirectory 'AccountService.runtimeconfig.json') | ConvertFrom-Json
$selfContained = (Test-Path -LiteralPath $serviceExe) -and ($null -ne $runtimeConfig.runtimeOptions.includedFrameworks)
if (-not $selfContained) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    if (-not ((& $dotnet --list-runtimes) -match '^Microsoft.AspNetCore.App 10\.')) { throw 'Install the Windows x64 .NET 10 ASP.NET Core Runtime first.' }
}
$credential = Get-Credential -UserName $config.windowsAccount -Message 'Windows task identity: use the same account that initialized the certificate. Password is stored by Windows Task Scheduler, not in scripts.'
if ($null -eq $credential) { throw 'Task registration canceled.' }
$accountSid = (New-Object Security.Principal.NTAccount($credential.UserName)).Translate([Security.Principal.SecurityIdentifier]).Value
if ($accountSid -ne $config.ownerSid) { throw 'Task account must match the ACME account owner.' }
$tls = Join-Path $config.stateDirectory 'tls'
$serviceArguments = '--contentRoot "{0}" --environment Production --AccountStorePath "{1}" --Kestrel:Endpoints:Internal:Url http://127.0.0.1:5080 --Kestrel:Endpoints:PublicHttps:Url https://0.0.0.0:443 --Kestrel:Endpoints:PublicHttps:Certificate:Path "{2}" --Kestrel:Endpoints:PublicHttps:Certificate:KeyPath "{3}"' -f `
    $config.publishDirectory,(Join-Path $config.stateDirectory 'data/accounts.json'),(Join-Path $tls 'fullchain.pem'),(Join-Path $tls 'private.key')
if ($selfContained) {
    $serviceAction = New-ScheduledTaskAction -Execute $serviceExe -Argument $serviceArguments -WorkingDirectory $config.publishDirectory
}
else {
    $serviceAction = New-ScheduledTaskAction -Execute $dotnet -Argument ('"' + (Join-Path $config.publishDirectory 'AccountService.dll') + '" ' + $serviceArguments) -WorkingDirectory $config.publishDirectory
}
$principal = New-ScheduledTaskPrincipal -UserId $config.windowsAccount -LogonType Password -RunLevel Limited
$serviceSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -StartWhenAvailable
$serviceTask = New-ScheduledTask -Action $serviceAction -Trigger (New-ScheduledTaskTrigger -AtStartup) -Principal $principal -Settings $serviceSettings
$powerShell = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
$renewalScript = Join-Path $PSScriptRoot 'Renew-WindowsIpHttps.ps1'
$renewalAction = New-ScheduledTaskAction -Execute $powerShell -Argument ('-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "' + $renewalScript + '" -StateDirectory "' + $config.stateDirectory + '"') -WorkingDirectory $PSScriptRoot
$renewalTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) -RepetitionInterval (New-TimeSpan -Hours 6)
$renewalSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 15) -RestartCount 2 -RestartInterval (New-TimeSpan -Minutes 15) -MultipleInstances IgnoreNew -StartWhenAvailable
$renewalTask = New-ScheduledTask -Action $renewalAction -Trigger $renewalTrigger -Principal $principal -Settings $renewalSettings
try {
    $plainPassword = $credential.GetNetworkCredential().Password
    Register-ScheduledTask -TaskName $config.taskName -InputObject $serviceTask -User $credential.UserName -Password $plainPassword | Out-Null
    Register-ScheduledTask -TaskName $config.renewalTaskName -InputObject $renewalTask -User $credential.UserName -Password $plainPassword | Out-Null
}
finally { $plainPassword = $null; $credential = $null }
Start-ScheduledTask -TaskName $config.taskName
Wait-FpsLocalTls $config $config.certificateThumbprint
Write-Output ('Account service is responding with a trusted IP certificate at https://' + $config.publicIp)
Write-Output 'Scheduled startup and 6-hour renewal checks are registered. Confirm connectivity from another computer before releasing clients.'
