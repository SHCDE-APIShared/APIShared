$ErrorActionPreference = 'Stop'
# A surrounding branch build marks its own process as already dispatched. The fixture
# must exercise the top-level selection, so it must not inherit that marker.
foreach ($name in @('SHCDE_BRANCH_SOURCE_ROOT','SHCDE_BRANCH_PINNED')) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('build-choice-' + [guid]::NewGuid().ToString('N').Substring(0,8))
[void][IO.Directory]::CreateDirectory((Join-Path $fixture 'tools/BranchBuild'))
. (Join-Path $root 'tools/BranchBuild/BranchBuild.Common.ps1')
Copy-Item -LiteralPath (Join-Path $root 'build.bat') -Destination $fixture
foreach ($name in @('BranchBuild.Common.ps1','Invoke-BranchBuild.ps1')) {
    Copy-Item -LiteralPath (Join-Path $root "tools/BranchBuild/$name") -Destination (Join-Path $fixture 'tools/BranchBuild')
}
# Only the build boundary is substituted. The production BAT/dispatchers run unchanged.
Write-BranchText (Join-Path $fixture 'tools/Build.ps1') 'param([int]$NoInstall, [switch]$WorkspaceBuild); Write-Host ("CHOICE working={0}; noinstall={1}" -f $WorkspaceBuild.IsPresent,$NoInstall); exit 0'
foreach ($selection in @('/workspace','/source:working','')) {
    foreach ($noInstall in @($false,$true)) {
        $arguments = @('/nopause')
        if ($selection) { $arguments += $selection }
        if ($noInstall) { $arguments += '/noinstall' }
        $output = @(& (Join-Path $fixture 'build.bat') @arguments 2>&1)
        if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch ("CHOICE working=True; noinstall=" + [int]$noInstall)) { throw ('Selection failed: ' + ($output -join "`n")) }
    }
}
foreach ($arguments in @(@('/workspace','/branch:main','/nopause'),@('/source:working','/branch:develop','/nopause'))) {
    $output = @(& (Join-Path $fixture 'build.bat') @arguments 2>&1)
    if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -match 'CHOICE working=') { throw 'Contradictory sources reached the build.' }
}
function Read-Host { param($Prompt) return '1' }
if ((Get-BranchChoice @()) -ne 'main') { throw 'Main menu failed.' }
function Read-Host { param($Prompt) return '2' }
if ((Get-BranchChoice @()) -ne 'develop') { throw 'Develop menu failed.' }
Remove-Item Function:Read-Host
# Stop the real PowerShell driver before any tool/game access, after its argument checks.
Copy-Item -LiteralPath (Join-Path $root 'tools/Build.ps1') -Destination (Join-Path $fixture 'tools/Build.ps1') -Force
Write-BranchText (Join-Path $fixture 'tools/BuildProof.ps1') "function Initialize-ApiBuildEnvironment { param([string]`$Root) throw 'FIXTURE_TOOLCHAIN_BOUNDARY' }"
foreach ($choice in @(0,1)) {
    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture 'tools/Build.ps1') -WorkspaceBuild -NoInstall $choice 2>&1)
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $oldPreference }
    if ($code -eq 0 -or ($output -join "`n") -notmatch 'FIXTURE_TOOLCHAIN_BOUNDARY') { throw 'Production driver blocked a valid workspace choice before the toolchain.' }
}
Write-Host 'PASS: independent source/installation choices, automatic default, conflicts and menus; no real builds/installations.'
Write-Host "Fixture retained: $fixture"
