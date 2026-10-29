@echo off
setlocal
set "FPS_UGS_INSTALLER=%~dp0Install-UgsPvpBridge.ps1"
if exist "%~dp0Deployment\Install-UgsPvpBridge.ps1" set "FPS_UGS_INSTALLER=%~dp0Deployment\Install-UgsPvpBridge.ps1"
echo This installs a NEW local UGS PvP worker. Existing backend tasks or TCP 5080 listeners will be refused.
echo No account migration, certificates or firewall changes are performed.
choice /C YN /N /M "Continue with Administrator installation? [Y/N] "
if errorlevel 2 exit /b 0
powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -Command "$scriptFile = $env:FPS_UGS_INSTALLER; $q = [char]34; $installArgs = '-NoProfile -NoExit -ExecutionPolicy RemoteSigned -File ' + $q + $scriptFile + $q + ' -Install'; Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -Verb RunAs -ArgumentList $installArgs -WorkingDirectory ([IO.Path]::GetDirectoryName($scriptFile)) -Wait"
if errorlevel 1 pause
