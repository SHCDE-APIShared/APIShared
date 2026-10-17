[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BranchBuild.Common.ps1')
$actual = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('APIShared Branch Build ' + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $fixture 'repo'
$game = Join-Path $fixture 'game'
[void][IO.Directory]::CreateDirectory($repo)
$previousGame = $env:SHCDE_GAME_DIR
$env:SHCDE_GAME_DIR = $game
$cases = 0
function Assert-Fixture([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message }; $script:cases++ }
function Git-Fixture([string[]]$Arguments) { Invoke-BranchGit $repo $Arguments | Out-Null }
function Run-Fixture([string]$Driver, [string[]]$Arguments, [int]$Expected) {
    $output = @(& (Join-Path $repo $Driver) @Arguments 2>&1)
    Assert-Fixture ($LASTEXITCODE -eq $Expected) ("Wrong fixture result: " + ($output -join "`n"))
    return ($output -join "`n")
}
try {
    Git-Fixture @('init','-b','main')
    Git-Fixture @('config','user.name','APIShared branch fixture')
    Git-Fixture @('config','user.email','fixture@example.invalid')
    Write-BranchText (Join-Path $repo '.gitignore') ".release-output/`r`nBepInEx/`r`n"
    Write-BranchText (Join-Path $repo 'info.json') '{"GUID":"APIShared_Serp","Version":"1.0.0"}'
    Write-BranchText (Join-Path $repo 'source.txt') 'API_MAIN_COMMIT'
    Write-BranchText (Join-Path $repo 'build.bat') @'
@echo off
setlocal EnableExtensions
set "NO_INSTALL=0"
for %%A in (%*) do if /I "%%~A"=="/noinstall" set "NO_INSTALL=1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build.ps1" -NoInstall %NO_INSTALL%
exit /b %ERRORLEVEL%
'@
    Write-BranchText (Join-Path $repo 'tools/Build.ps1') @'
param([int]$NoInstall)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$game = $env:SHCDE_GAME_DIR
$source = [IO.File]::ReadAllText((Join-Path $root 'source.txt'))
Write-Host $source
$package = Join-Path $root 'BepInEx/plugins/APIShared_Serp'
[void][IO.Directory]::CreateDirectory($package)
[IO.File]::WriteAllText((Join-Path $package 'APIShared.dll'),$source)
if ($NoInstall) { exit 0 }
$destination = Join-Path $game 'BepInEx\plugins\APIShared_Serp'
$toolTarget = Join-Path $game 'BepInEx/tools/APISharedWorkshopPublisher'
[void][IO.Directory]::CreateDirectory($destination)
[void][IO.Directory]::CreateDirectory($toolTarget)
[IO.File]::WriteAllText((Join-Path $destination 'fixture.txt'),$source)
[IO.File]::WriteAllText((Join-Path $toolTarget 'fixture.txt'),$source)
exit 0
'@
    Write-BranchText (Join-Path $repo 'tools/WorkshopUploader/build.bat') "@echo off`r`ntype `"%~dp0..\..\source.txt`"`r`nexit /b %ERRORLEVEL%`r`n"
    Git-Fixture @('add','.')
    Git-Fixture @('commit','-m','API main fixture')
    Git-Fixture @('checkout','-b','develop')
    Write-BranchText (Join-Path $repo 'source.txt') 'API_DEVELOP_COMMIT'
    Git-Fixture @('add','.')
    Git-Fixture @('commit','-m','API develop fixture')
    Write-BranchText (Join-Path $repo 'source.txt') 'DIRTY_MUST_NOT_BUILD'
    foreach ($name in @('BranchBuild.Common.ps1','Invoke-BranchBuild.ps1')) {
        Write-BranchText (Join-Path $repo ('tools/BranchBuild/'+$name)) ([IO.File]::ReadAllText((Join-Path $PSScriptRoot $name)))
    }
    foreach ($relative in @('build.bat','tools/WorkshopUploader/build.bat')) {
        $entry = [IO.File]::ReadAllText((Join-Path $actual $relative))
        $end = $entry.IndexOf(":shcde_branch_selected`r`nendlocal`r`n")
        if ($end -lt 0) { throw "Missing production dispatcher: $relative" }
        Write-BranchText (Join-Path $repo $relative) ($entry.Substring(0,$end + ":shcde_branch_selected`r`nendlocal`r`n".Length) + "exit /b 99`r`n")
    }
    $output = Run-Fixture 'build.bat' @('/branch:main','/nopause') 0
    Assert-Fixture ($output -match 'API_MAIN_COMMIT' -and $output -notmatch 'DIRTY_MUST_NOT_BUILD') 'Standalone main source failed'
    $plugin = Join-Path $game 'BepInEx/plugins/APIShared_Serp/fixture.txt'
    $publisher = Join-Path $game 'BepInEx/tools/APISharedWorkshopPublisher/fixture.txt'
    Assert-Fixture ([IO.File]::ReadAllText($plugin) -ceq 'API_MAIN_COMMIT') 'Standalone main installation failed'
    $pluginHash = Get-BranchHash $plugin; $publisherHash = Get-BranchHash $publisher
    $output = Run-Fixture 'build.bat' @('/branch:develop','/nopause','/noinstall') 0
    Assert-Fixture ($output -match 'API_DEVELOP_COMMIT' -and $output -match 'APIShared_Serp_dev') 'Standalone develop selection failed'
    $output = Run-Fixture 'build.bat' @('/branch:develop','/nopause') 0
    Assert-Fixture ([IO.File]::ReadAllText((Join-Path $game 'BepInEx/plugins/APIShared_Serp_dev/fixture.txt')) -ceq 'API_DEVELOP_COMMIT') 'Development installation failed'
    Assert-Fixture ((Get-BranchHash $plugin) -ceq $pluginHash -and (Get-BranchHash $publisher) -ceq $publisherHash) 'Development/no-install build changed main'
    $output = Run-Fixture 'tools/WorkshopUploader/build.bat' @('/branch:develop','/nopause') 0
    Assert-Fixture ($output -match 'API_DEVELOP_COMMIT' -and $output -notmatch 'DIRTY_MUST_NOT_BUILD') 'Standalone publisher selected working sources'
    Assert-Fixture (((Invoke-BranchGit $repo @('branch','--show-current')) -join '') -ceq 'develop') 'Standalone working branch moved'
    Write-Host "PASS: $cases standalone APIShared/publisher, source isolation, no-install and development installation cases."
    Write-Host "Fixture evidence retained: $fixture"
} finally { $env:SHCDE_GAME_DIR = $previousGame }
