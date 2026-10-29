@echo off
setlocal
set "FPS_RAINYUN_INSTALLER=%~dp0Install-RainyunBackend.ps1"
if exist "%~dp0Deployment\Install-RainyunBackend.ps1" set "FPS_RAINYUN_INSTALLER=%~dp0Deployment\Install-RainyunBackend.ps1"
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -Command "$scriptFile = $env:FPS_RAINYUN_INSTALLER; $q = [char]34; $installArgs = '-NoProfile -NoExit -ExecutionPolicy RemoteSigned -File ' + $q + $scriptFile + $q; Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Verb RunAs -ArgumentList $installArgs -WorkingDirectory ([IO.Path]::GetDirectoryName($scriptFile)) -Wait"
if errorlevel 1 pause
