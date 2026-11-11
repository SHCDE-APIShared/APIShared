@echo off
setlocal EnableExtensions
set "PUBLISHER_NOPAUSE=0"
for %%A in (%*) do if /I "%%~A"=="/nopause" set "PUBLISHER_NOPAUSE=1"
if not exist "%~dp0WorkshopUploader.exe" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0PublisherText.ps1" -Key Launcher.PackageMissing
  set "PUBLISHER_RESULT=1"
  goto :finish
)
"%~dp0WorkshopUploader.exe" --interactive %*
set "PUBLISHER_RESULT=%ERRORLEVEL%"
:finish
if "%PUBLISHER_NOPAUSE%"=="0" pause
exit /b %PUBLISHER_RESULT%
