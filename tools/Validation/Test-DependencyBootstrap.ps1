param([string]$GameDir,[string]$MSBuild)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(-not $GameDir){
    . (Join-Path $root 'tools/BuildProof.ps1')
    Initialize-ApiBuildEnvironment $root
    $GameDir=$env:SHCDE_GAME_DIR
}
if(-not $MSBuild){
    $MSBuild=$env:SHCDE_MSBUILD
    if(-not $MSBuild){$MSBuild=& (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe') -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe'|Select-Object -First 1}
}
if(-not $GameDir -or -not $MSBuild){throw 'Game references and MSBuild required.'}
& $MSBuild (Join-Path $root 'tests/DependencyBootstrap.Tests/DependencyBootstrap.Tests.csproj') /nologo /verbosity:minimal /p:Configuration=Release "/p:GameDir=$GameDir"
if($LASTEXITCODE -ne 0){throw 'Bootstrap contract test compilation failed.'}
& (Join-Path $root 'tests/DependencyBootstrap.Tests/bin/APIShared.DependencyBootstrap.Tests.exe') $GameDir
if($LASTEXITCODE -ne 0){throw 'Bootstrap contract tests failed.'}
