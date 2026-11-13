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
    Write-BranchText (Join-Path $repo '.gitignore') ".release-output/`r`nBepInEx/`r`n.local/`r`n"
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
if ($env:API_BRANCH_FIXTURE_FAIL -eq '1') { exit 7 }
$package = Join-Path $root 'BepInEx/plugins/APIShared_Serp'
[void][IO.Directory]::CreateDirectory($package)
[IO.File]::WriteAllText((Join-Path $package 'APIShared.dll'),$source)
$mode = 'full'
if (Get-Command Get-ApiBuildTestMode -ErrorAction SilentlyContinue) { $mode = Get-ApiBuildTestMode 'build.bat' $DriverArguments }
$evidence = Join-Path $root '.local'
[void][IO.Directory]::CreateDirectory($evidence)
[IO.File]::WriteAllText((Join-Path $evidence 'build-proof.json'),(@{TestMode=$mode} | ConvertTo-Json))
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
    # Boundary double: lifecycle tests do not rebuild the real SDK. The real proof contract
    # has its own suite; this fixture rejects changed output AND reduced evidence. Keeping
    # the selected mode here catches accidental SDK promotion after a successful reduced build.
    Write-BranchText (Join-Path $repo 'tools/BuildProof.ps1') @'
function Assert-ApiBuildProof([string]$Root) {
    $proof=[IO.File]::ReadAllText((Join-Path $Root '.local/build-proof.json'))|ConvertFrom-Json
    if ($proof.TestMode -cne 'full') { throw 'Fixture SDK requires full checks.' }
    $text=[IO.File]::ReadAllText((Join-Path $Root 'source.txt'))
    $package=Join-Path $Root 'BepInEx/plugins/APIShared_Serp/APIShared.dll'
    if (-not [IO.File]::Exists($package) -or [IO.File]::ReadAllText($package) -cne $text) { throw 'Fixture SDK output changed.' }
    return [pscustomobject]@{TestMode='full';GameDir=$env:SHCDE_GAME_DIR;ExtenderDir='';MSBuild=''}
}
'@
    Git-Fixture @('add','.')
    Git-Fixture @('commit','-m','API main fixture')
    Git-Fixture @('checkout','-b','develop')
    Write-BranchText (Join-Path $repo 'source.txt') 'API_DEVELOP_COMMIT'
    Git-Fixture @('add','.')
    Git-Fixture @('commit','-m','API develop fixture')
    Write-BranchText (Join-Path $repo 'source.txt') 'DIRTY_MUST_NOT_BUILD'
    foreach ($name in @('BranchBuild.Common.ps1','Invoke-BranchBuild.ps1','BranchRun.ps1','Release-BranchSource.ps1')) {
        Write-BranchText (Join-Path $repo ('tools/BranchBuild/'+$name)) ([IO.File]::ReadAllText((Join-Path $PSScriptRoot $name)))
    }
    Write-BranchText (Join-Path $repo 'tools/BuildChecks.ps1') ([IO.File]::ReadAllText((Join-Path $actual 'tools/BuildChecks.ps1')))
    foreach ($relative in @('build.bat','tools/WorkshopUploader/build.bat')) {
        $entry = [IO.File]::ReadAllText((Join-Path $actual $relative))
        # Execute the whole maintained entrypoint. Only its build boundary is a
        # fixture; labels, comments and dispatcher section layout may change.
        Write-BranchText (Join-Path $repo $relative) $entry
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
    $output = Run-Fixture 'build.bat' @('/branch:develop','/nopause','/tests:quick','/noinstall') 1
    Assert-Fixture ($output -match 'does not support /tests:quick' -and $output -notmatch '\r?\nAPI_DEVELOP_COMMIT\r?\n') 'Old selected driver silently ignored quick mode'
    # The branch fixture now supports mode forwarding. Only fixture sources are committed.
    Write-BranchText (Join-Path $repo 'source.txt') 'API_DEVELOP_COMMIT'
    Write-BranchText (Join-Path $repo 'build.bat') ([IO.File]::ReadAllText((Join-Path $actual 'build.bat')))
    $fixtureBuild = [IO.File]::ReadAllText((Join-Path $repo 'tools/Build.ps1'))
    $fixtureBuild = $fixtureBuild.Replace('param([int]$NoInstall)', 'param([int]$NoInstall, [switch]$WorkspaceBuild, [Parameter(ValueFromRemainingArguments)][string[]]$DriverArguments)')
    $fixtureBuild = $fixtureBuild.Replace("$" + "ErrorActionPreference = 'Stop'", ". (Join-Path $" + "PSScriptRoot 'BuildChecks.ps1')`r`nWrite-Host ('TEST_MODE=' + (Get-ApiBuildTestMode 'build.bat' $" + "DriverArguments))`r`n$" + "ErrorActionPreference = 'Stop'")
    Write-BranchText (Join-Path $repo 'tools/Build.ps1') $fixtureBuild
    Git-Fixture @('add','.')
    Git-Fixture @('commit','-m','Mode-aware build fixture')
    foreach ($mode in @('full','quick','none')) {
        foreach ($keep in @($false,$true)) {
            # Explicit install selection prevents full-SDK cache reuse: exercise each child build.
            $arguments = @('/branch:develop','/nopause','/noinstall','/install:dev',"/tests:$mode")
            if ($keep) { $arguments += '/keepsource' }
            $output = Run-Fixture 'build.bat' $arguments 0
            Assert-Fixture ($output -match "TEST_MODE=$mode") 'Committed branch lost test mode'
            $last = @(Get-ChildItem -LiteralPath (Join-Path $repo '.release-output/branch-builds/develop') -Filter '*.lifecycle.json' -File | Sort-Object Name)[-1]
            $built = [IO.File]::ReadAllText($last.FullName)|ConvertFrom-Json
            $expectedPhase = if ($mode -eq 'full' -and -not $keep) { 'sdk-ready' } else { 'retained' }
            Assert-Fixture ($built.Phase -ceq $expectedPhase) "Wrong successful lifecycle for tests:$mode keepsource:$keep"
            $builtPackage = Join-Path $built.Root 'BepInEx/plugins/APIShared_Serp/APIShared.dll'
            Assert-Fixture ([IO.File]::ReadAllText($builtPackage) -ceq 'API_DEVELOP_COMMIT') 'Successful output was removed'
            $proof = [IO.File]::ReadAllText((Join-Path $built.Root '.local/build-proof.json'))|ConvertFrom-Json
            Assert-Fixture ($proof.TestMode -ceq $mode) 'Fixture evidence lost selected mode'
            Assert-Fixture ((Get-BranchHash $plugin) -ceq $pluginHash -and (Get-BranchHash $publisher) -ceq $publisherHash) 'Reduced/no-install build changed main installation'
        }
    }
    foreach ($mode in @('quick','none')) {
        $output = Run-Fixture 'build.bat' @('/branch:develop','/nopause',"/tests:$mode") 0
        Assert-Fixture ($output -match "TEST_MODE=$mode") 'Installing branch lost reduced mode'
        Assert-Fixture ([IO.File]::ReadAllText((Join-Path $game 'BepInEx/plugins/APIShared_Serp_dev/fixture.txt')) -ceq 'API_DEVELOP_COMMIT') 'Reduced development installation failed'
        Assert-Fixture ((Get-BranchHash $plugin) -ceq $pluginHash -and (Get-BranchHash $publisher) -ceq $publisherHash) 'Reduced development build changed main installation'
    }

    # Exercise production lifecycle helpers with real temporary Git worktrees.
    . (Join-Path $PSScriptRoot 'BranchRun.ps1')
    $run = New-ApiBranchRun $repo 'main' 'build.bat' $false
    Assert-Fixture ([IO.File]::Exists($run.Root+'.lifecycle.json')) 'Ownership must precede checkout'
    $blocked = $false
    try { $other = [IO.File]::Open($run.Root+'.lease','Open','ReadWrite','None'); $other.Dispose() } catch [IO.IOException] { $blocked=$true }
    Assert-Fixture $blocked 'Active lifetime lease did not exclude another writer'
    $null = New-BranchSource $repo 'main' $run.Root
    $current = New-ApiBranchRun $repo 'main' 'build.bat' $false
    Set-ApiBranchRunPhase $run 'sdk-ready'
    Remove-SupersededApiBranchRuns $current
    Assert-Fixture ([IO.Directory]::Exists($run.Root)) 'Active SDK was removed'
    $run.Lease.Dispose()
    Remove-SupersededApiBranchRuns $current
    Assert-Fixture ([IO.Directory]::Exists($run.Root)) 'SDK without consumer-release attestation was removed'
    $run.State.ConsumerSafeToRetire=$true; Set-ApiBranchRunPhase $run 'sdk-ready'
    Remove-SupersededApiBranchRuns $current
    Assert-Fixture ([IO.Directory]::Exists($run.Root)) 'Live owner without an open lease was removed'
    $run.State.OwnerPid=2147483647; Set-ApiBranchRunPhase $run 'sdk-ready'
    Remove-SupersededApiBranchRuns $current
    Assert-Fixture (-not [IO.Directory]::Exists($run.Root)) 'Superseded reproducible SDK was not removed'
    Assert-Fixture (([IO.File]::ReadAllText($run.Root+'.lifecycle.json') | ConvertFrom-Json).Phase -eq 'cleaned') 'Cleanup result missing'
    $kept=New-ApiBranchRun $repo 'main' 'build.bat' $true
    $null=New-BranchSource $repo 'main' $kept.Root; Set-ApiBranchRunPhase $kept 'sdk-ready';$kept.Lease.Dispose()
    Remove-SupersededApiBranchRuns $current
    Assert-Fixture ([IO.Directory]::Exists($kept.Root)) '/keepsource was ignored'
    $unique=New-ApiBranchRun $repo 'main' 'build.bat' $false
    $null=New-BranchSource $repo 'main' $unique.Root
    Write-BranchText (Join-Path $unique.Root 'source.txt') 'UNIQUE_USER_CHANGE'
    $unique.State.OwnerPid=2147483647; $unique.State.ConsumerSafeToRetire=$true; Set-ApiBranchRunPhase $unique 'sdk-ready'; $unique.Lease.Dispose()
    Remove-SupersededApiBranchRuns $current
    Assert-Fixture ([IO.File]::ReadAllText((Join-Path $unique.Root 'source.txt')) -eq 'UNIQUE_USER_CHANGE') 'Unique source lost'
    $ignored=New-ApiBranchRun $repo 'main' 'build.bat' $false
    $null=New-BranchSource $repo 'main' $ignored.Root
    Write-BranchText (Join-Path $ignored.Root 'BepInEx/user-note.txt') 'DATA'
    # An unrecognized ignored root must be retained, not inferred disposable.
    Write-BranchText (Join-Path $ignored.Root '.release-output/private.txt') 'PRIVATE'
    $ignored.State.OwnerPid=2147483647; $ignored.State.ConsumerSafeToRetire=$true; Set-ApiBranchRunPhase $ignored 'sdk-ready';$ignored.Lease.Dispose()
    Remove-SupersededApiBranchRuns $current
    Assert-Fixture ([IO.File]::Exists((Join-Path $ignored.Root '.release-output/private.txt'))) 'Unknown ignored data lost'
    $prepared=New-ApiBranchRun $repo 'main' 'missing/build.bat' $false
    $null=New-BranchSource $repo 'main' $prepared.Root
    Export-ApiBranchRunEvidence $prepared; Remove-ApiBranchRun $prepared; $prepared.Lease.Dispose()
    Assert-Fixture (-not [IO.Directory]::Exists($prepared.Root)) 'Preparation rollback retained an unnecessary full checkout'
    $nested=New-ApiBranchRun $repo 'main' 'build.bat' $false
    $null=New-BranchSource $repo 'main' $nested.Root
    Write-BranchText (Join-Path $nested.Root 'nested/.git') 'foreign repository marker'
    $rejected=$false;try { Remove-ApiBranchRun $nested } catch { $rejected=$true }
    Assert-Fixture ($rejected -and [IO.File]::Exists((Join-Path $nested.Root 'nested/.git'))) 'Nested repository lost'
    $nested.Lease.Dispose()
    # Execute the actual dispatcher exception path, not only helper rollback.
    $output=@(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Invoke-BranchBuild.ps1') -Repository $repo -Driver 'missing/build.bat' -Api /branch:main /nopause 2>&1)
    Assert-Fixture ($LASTEXITCODE -eq 1) 'Missing selected driver unexpectedly succeeded'
    $last=@(Get-ChildItem -LiteralPath (Split-Path -Parent $run.Root) -Filter '*.lifecycle.json' -File | Sort-Object Name)[-1]
    $failedPreparation=[IO.File]::ReadAllText($last.FullName)|ConvertFrom-Json
    Assert-Fixture ($failedPreparation.Phase -eq 'cleaned' -and -not [IO.Directory]::Exists($failedPreparation.Root)) 'Dispatcher preparation exception orphaned its checkout'
    $staged=New-ApiBranchRun $repo 'main' 'build.bat' $false
    $null=New-BranchSource $repo 'main' $staged.Root
    Write-BranchText (Join-Path $staged.Root 'source.txt') 'STAGED_UNIQUE'
    $null=Invoke-BranchGit $staged.Root @('add','source.txt')
    Write-BranchText (Join-Path $staged.Root 'source.txt') 'API_MAIN_COMMIT'
    $rejected=$false;try { Export-ApiBranchRunEvidence $staged } catch { $rejected=$true }
    Assert-Fixture $rejected 'Staged source was discarded because worktree bytes matched'
    $staged.Lease.Dispose()
    $escaped=[pscustomobject]@{Root=(Join-Path $current.Root '../escape');Branch='main';State=$current.State}
    $rejected=$false;try { Assert-ApiBranchRunPath $escaped } catch { $rejected=$true }
    Assert-Fixture $rejected 'Escaping run path accepted'
    $releaseScript=Join-Path $PSScriptRoot 'Release-BranchSource.ps1'
    $release=New-ApiBranchRun $repo 'main' 'build.bat' $false
    $null=New-BranchSource $repo 'main' $release.Root
    $preview=@(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $releaseScript -Root $release.Root 2>&1)
    Assert-Fixture ($LASTEXITCODE -eq 0 -and [IO.Directory]::Exists($release.Root)) 'Release preview mutated the source'
    $release.Lease.Dispose()
    $active=@(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $releaseScript -Root $release.Root -Release 2>&1)
    Assert-Fixture ($LASTEXITCODE -eq 1 -and [IO.Directory]::Exists($release.Root)) 'Active owner released through entrypoint'
    $release.State.OwnerPid=2147483647;Set-ApiBranchRunPhase $release 'sdk-ready'
    $whatif=@(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $releaseScript -Root $release.Root -Release -WhatIf 2>&1)
    Assert-Fixture ($LASTEXITCODE -eq 0 -and [IO.Directory]::Exists($release.Root) -and -not [IO.Directory]::Exists($release.Root+'.evidence')) 'WhatIf mutated release data'
    $released=@(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $releaseScript -Root $release.Root -Release 2>&1)
    Assert-Fixture ($LASTEXITCODE -eq 0 -and -not [IO.Directory]::Exists($release.Root)) ('Explicit SDK release failed: '+($released -join "`n"))
    $uniqueReleased=@(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $releaseScript -Root $unique.Root -Release 2>&1)
    Assert-Fixture ($LASTEXITCODE -eq 1 -and [IO.File]::ReadAllText((Join-Path $unique.Root 'source.txt')) -eq 'UNIQUE_USER_CHANGE') 'Explicit release discarded unique source'
    # Same commit, controller and verified full proof reuse one SDK; changed bytes reject it.
    $cache=New-ApiBranchRun $repo 'develop' 'build.bat' $false
    $null=New-BranchSource $repo 'develop' $cache.Root
    $text=[IO.File]::ReadAllText((Join-Path $cache.Root 'source.txt'))
    Write-BranchText (Join-Path $cache.Root 'BepInEx/plugins/APIShared_Serp/APIShared.dll') $text
    Write-BranchText (Join-Path $cache.Root '.local/build-proof.json') '{"TestMode":"full"}'
    Write-BranchText ($cache.Root+'.build.json') '{"BuildToolOverrides":[]}'
    $cache.State.OwnerPid=2147483647;Set-ApiBranchRunPhase $cache 'sdk-ready';$cache.Lease.Dispose()
    $reused=Find-ReusableApiBranchRun $repo 'develop'
    Assert-Fixture ($null -ne $reused -and $reused.Root -eq $cache.Root) 'Matching full SDK cache was not reused'
    if($reused){$reused.Lease.Dispose()}
    Write-BranchText (Join-Path $cache.Root 'BepInEx/plugins/APIShared_Serp/APIShared.dll') 'CHANGED'
    $reused=Find-ReusableApiBranchRun $repo 'develop'
    Assert-Fixture ($null -eq $reused -or $reused.Root -ne $cache.Root) 'Changed SDK package cache was reused'
    if($reused){$reused.Lease.Dispose()}
    $previousFailure=$env:API_BRANCH_FIXTURE_FAIL
    try {
        $env:API_BRANCH_FIXTURE_FAIL='1'
        $failureOutput=Run-Fixture 'build.bat' @('/branch:develop','/nopause','/noinstall','/tests:none') 7
    } finally { $env:API_BRANCH_FIXTURE_FAIL=$previousFailure }
    $last=@(Get-ChildItem -LiteralPath (Join-Path $repo '.release-output/branch-builds/develop') -Filter '*.lifecycle.json' -File | Sort-Object Name)[-1]
    $failed=[IO.File]::ReadAllText($last.FullName)|ConvertFrom-Json
    Assert-Fixture ($failed.Phase -eq 'cleaned' -and -not [IO.Directory]::Exists($failed.Root)) 'Failed driver left a reproducible full source checkout'
    $current.Lease.Dispose()
    Write-Host "PASS: $cases standalone APIShared/publisher, source isolation, no-install and development installation cases."
    Write-Host "Fixture evidence retained: $fixture"
} finally { $env:SHCDE_GAME_DIR = $previousGame }
