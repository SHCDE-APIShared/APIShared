@echo off
setlocal EnableExtensions
set "PUBLISHER_NOPAUSE=0"
for %%A in (%*) do if /I "%%~A"=="/nopause" set "PUBLISHER_NOPAUSE=1"
if not exist "%~dp0WorkshopUploader.exe" (
  echo ERROR: The APIShared Workshop Publisher package is incomplete.
  set "PUBLISHER_RESULT=1"
  goto :finish
)
"%~dp0WorkshopUploader.exe" --interactive %*
set "PUBLISHER_RESULT=%ERRORLEVEL%"
:finish
if "%PUBLISHER_NOPAUSE%"=="0" pause
exit /b %PUBLISHER_RESULT%
