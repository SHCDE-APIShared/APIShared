[CmdletBinding(PositionalBinding=$false)]
param([string]$GameDir,[string]$MSBuild,[switch]$Package,
    [Parameter(ValueFromRemainingArguments)][string[]]$DriverArguments)
$ErrorActionPreference='Stop'
foreach($argument in $DriverArguments){if($argument -ine '/nopause'){throw "Unknown Proton fix build argument: $argument"}}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'ProtonWorkshopFixPackage.ps1')
if($GameDir){$env:SHCDE_GAME_DIR=$GameDir}
if($MSBuild){$env:SHCDE_MSBUILD=$MSBuild}
Initialize-ApiBuildEnvironment $root
if(-not $GameDir){$GameDir=$env:SHCDE_GAME_DIR}
if(-not $MSBuild){$MSBuild=$env:SHCDE_MSBUILD}
if(-not $MSBuild){
    $MSBuild=& (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe') -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe'|Select-Object -First 1
}
if(-not $MSBuild -or -not [IO.File]::Exists($MSBuild)){throw 'MSBuild is required.'}
$initial=Get-ProtonFixInputState $root $GameDir $MSBuild
$proofPath=Join-Path $root '.local/ProtonWorkshopFix/build-proof.json'
if([IO.File]::Exists($proofPath)){[IO.File]::Delete($proofPath)}
foreach($project in @('proton-fix/ProtonWorkshopFix.csproj','tests/ProtonWorkshopFix.Tests/ProtonWorkshopFix.Tests.csproj')) {
    Write-Host ('[{0:yyyy-MM-dd HH:mm:ss.fff}] Building {1}' -f [DateTime]::Now,$project)
    & $MSBuild (Join-Path $root $project) /nologo /verbosity:minimal /t:Rebuild /p:Configuration=Release "/p:GameDir=$GameDir"
    if($LASTEXITCODE -ne 0){throw "Proton fix compilation failed: $project"}
}
& (Join-Path $root 'tests/ProtonWorkshopFix.Tests/bin/APIShared.ProtonWorkshopFix.Tests.exe') $GameDir
if($LASTEXITCODE -ne 0){throw 'Standalone Proton fix contract tests failed.'}
$dll=Join-Path $root '.local/ProtonWorkshopFix/bin/APIShared.ProtonWorkshopFix.dll'
Assert-ProtonFixAssembly $root $dll $GameDir
$final=Get-ProtonFixInputState $root $GameDir $MSBuild
if($initial.Fingerprint -cne $final.Fingerprint){throw 'Proton fix sources changed during its build; rebuild after editing is complete.'}
$commit=(& git -C $root rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0){throw 'Could not record Proton fix source commit.'}
$proof=[ordered]@{SchemaVersion=1;TestMode='full';CreatedUtc=[DateTime]::UtcNow.ToString('o');Commit=$commit;GameDir=$GameDir;MSBuild=$MSBuild;Fingerprint=$final.Fingerprint;Inputs=$final.Inputs;AssemblySHA256=(Get-ApiFileHash $dll)}
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($proofPath))
[IO.File]::WriteAllText($proofPath,(($proof|ConvertTo-Json -Depth 5).Replace("`r`n","`n").Replace("`n","`r`n")+"`r`n"),[Text.UTF8Encoding]::new($false))
& (Join-Path $PSScriptRoot 'Validation/Test-ProtonWorkshopFixPackage.ps1') -Root $root
if($Package){
    $version=[string]([IO.File]::ReadAllText((Join-Path $root 'info.json'))|ConvertFrom-Json).Version
    $run=Join-Path $root ('.local/ProtonWorkshopFix/releases/r-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
    New-ProtonFixAsset $root $run $version
}
Write-Host ('[{0:yyyy-MM-dd HH:mm:ss.fff}] PASS: standalone Proton fix built and tested; nothing installed.' -f [DateTime]::Now)
