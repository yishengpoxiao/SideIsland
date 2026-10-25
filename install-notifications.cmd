@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-notifications.ps1" %*
set "islandInstallExit=%ERRORLEVEL%"
echo.
if not "%islandInstallExit%"=="0" echo Notification setup did not complete. Read the reason above; no Windows security settings were changed.
pause
exit /b %islandInstallExit%
