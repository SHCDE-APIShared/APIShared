@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\tools\Build-ProtonWorkshopFix.ps1" %*
set "result=%errorlevel%"
if /I "%~1"=="/nopause" exit /b %result%
pause
exit /b %result%
