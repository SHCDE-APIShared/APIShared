@echo off
setlocal EnableExtensions
rem SHCDE_TEST_MODES: full quick none
rem Source selection and installation are independent choices.
set "API_WORKSPACE=0"
set "API_BRANCH_ARG=0"
for %%A in (%*) do (
  if /I "%%~A"=="/workspace" set "API_WORKSPACE=1"
  if /I "%%~A"=="/source:working" set "API_WORKSPACE=1"
  for /f "tokens=1 delims=:" %%B in ("%%~A") do if /I "%%B"=="/branch" set "API_BRANCH_ARG=1"
)
if "%API_WORKSPACE%"=="1" (
  if "%API_BRANCH_ARG%"=="1" (
    echo ERROR: /workspace cannot select a committed branch.
    goto :workspace_invalid
  )
  echo Build source: current working tree; /noinstall independently controls installation.
  goto :shcde_branch_selected
)
rem SHCDE_BRANCH_DISPATCH: select committed sources; the selected child owns its build.
if defined SHCDE_BRANCH_SOURCE_ROOT goto :shcde_branch_selected
if defined SHCDE_BRANCH_PINNED goto :shcde_branch_selected
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\BranchBuild\Invoke-BranchBuild.ps1" -Repository "%~dp0." -Driver "build.bat" -Api %*
set "SHCDE_BRANCH_RESULT=%ERRORLEVEL%"
set "SHCDE_BRANCH_PAUSE=1"
for %%A in (%*) do if /I "%%~A"=="/nopause" set "SHCDE_BRANCH_PAUSE=0"
if "%SHCDE_BRANCH_PAUSE%"=="1" pause
exit /b %SHCDE_BRANCH_RESULT%
:shcde_branch_selected
endlocal
@echo off
setlocal EnableExtensions
set "BUILD_DRIVER_NOPAUSE=0"
for %%A in (%*) do if /I "%%~A"=="/nopause" set "BUILD_DRIVER_NOPAUSE=1"
set "BUILD_DRIVER_ORIGINAL_DIR=%CD%"
echo [%date% %time%] START APIShared
cd /d "%~dp0"
if not "%ERRORLEVEL%"=="0" goto :build_driver_directory_failed
rem The outer driver owns the pause, including failures before compilation.
call :build_driver_main %* /nopause
set "BUILD_DRIVER_RESULT=%ERRORLEVEL%"
cd /d "%BUILD_DRIVER_ORIGINAL_DIR%"
echo [%date% %time%] END: exit code %BUILD_DRIVER_RESULT%
if "%BUILD_DRIVER_NOPAUSE%"=="0" pause
exit /b %BUILD_DRIVER_RESULT%

:build_driver_directory_failed
echo ERROR: Cannot enter the build directory "%~dp0".
if "%BUILD_DRIVER_NOPAUSE%"=="0" pause
exit /b 1

:build_driver_main
setlocal EnableExtensions
set "NO_PAUSE=0"
set "NO_INSTALL=0"
set "WORKSPACE_BUILD="
for %%A in (%*) do (
  if /I "%%~A"=="/workspace" set "WORKSPACE_BUILD=-WorkspaceBuild"
  if /I "%%~A"=="/source:working" set "WORKSPACE_BUILD=-WorkspaceBuild"
)
for %%A in (%*) do if /I "%%~A"=="/nopause" set "NO_PAUSE=1"
for %%A in (%*) do if /I "%%~A"=="/noinstall" set "NO_INSTALL=1"
echo [%date% %time%] PowerShell checks / build step
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build.ps1" -NoInstall %NO_INSTALL% %WORKSPACE_BUILD% %*
set "RESULT=%ERRORLEVEL%"
exit /b %RESULT%

:workspace_invalid
exit /b 1
