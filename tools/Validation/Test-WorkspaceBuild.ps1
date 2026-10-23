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
Copy-Item -LiteralPath (Join-Path $root 'tools/BuildChecks.ps1') -Destination (Join-Path $fixture 'tools/BuildChecks.ps1')
$inventory = Join-Path $fixture 'inventory'
foreach ($relative in @('src/new-topic/a.cs','other-layout/tool.ps1','assets/x.xaml','BIN/generated.cs','other-layout/obj/generated.cs','BepInEx/plugin/generated.cs','.local/generated.cs','.work/generated.cs','.release-output/generated.cs','.git/generated.cs','src/bin-like-name/kept.cs')) {
    Write-BranchText (Join-Path $inventory $relative) 'fixture'
}
$oldFiles = @(Get-ChildItem -LiteralPath $inventory -Recurse -File | Where-Object {
    ('\' + $_.FullName.Substring($inventory.Length).TrimStart('\','/')) -notmatch '[\\/](bin|obj|BepInEx|\.git|\.local|\.work|\.release-output)[\\/]'
} | ForEach-Object FullName | Sort-Object)
$newFiles = @(Get-ApiMaintainedFiles $inventory | ForEach-Object FullName | Sort-Object)
if ($oldFiles.Count -ne 4 -or ($oldFiles -join '|') -cne ($newFiles -join '|')) { throw 'Pruned enumeration changed the maintained source set.' }
# Only the build boundary is substituted. The production BAT/dispatchers run unchanged.
Write-BranchText (Join-Path $fixture 'tools/Build.ps1') 'param([int]$NoInstall, [switch]$WorkspaceBuild, [Parameter(ValueFromRemainingArguments)][string[]]$DriverArguments); . (Join-Path $PSScriptRoot "BuildChecks.ps1"); $mode=Get-ApiBuildTestMode "build.bat" $DriverArguments; Write-Host ("CHOICE working={0}; noinstall={1}; tests={2}" -f $WorkspaceBuild.IsPresent,$NoInstall,$mode); exit 0'
foreach ($selection in @('/workspace','/source:working','')) {
    foreach ($noInstall in @($false,$true)) {
        $arguments = @('/nopause')
        if ($selection) { $arguments += $selection }
        if ($noInstall) { $arguments += '/noinstall' }
        $output = @(& (Join-Path $fixture 'build.bat') @arguments 2>&1)
        if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch ("CHOICE working=True; noinstall=" + [int]$noInstall)) { throw ('Selection failed: ' + ($output -join "`n")) }
    }
}
foreach ($mode in @('full','quick','none')) {
    $output = @(& (Join-Path $fixture 'build.bat') /source:working /nopause /noinstall "/tests:$mode" 2>&1)
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch "CHOICE working=True; noinstall=1; tests=$mode") { throw 'Test selection did not reach the build boundary.' }
}
foreach ($arguments in @(@('/tests'),@('/tests:invalid'),@('/tests:full','/tests:none'))) {
    $rejected = $false
    try { $null = Get-ApiBuildTestMode 'build.bat' $arguments } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid or conflicting test mode accepted.' }
    $output = @(& (Join-Path $fixture 'build.bat') /nopause @arguments 2>&1)
    if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -match 'CHOICE working=') { throw 'Invalid test mode reached the build.' }
}
if ((Get-ApiBuildTestMode 'build.bat' @()) -ne 'full') { throw 'Default checks must remain full.' }
foreach ($mode in @('full','quick','none')) {
    $context = New-ApiBuildContext $mode
    $effects = [Collections.Generic.List[string]]::new()
    foreach ($selection in @('always','checked','full')) {
        Invoke-ApiBuildStep $context $selection -When $selection -Action { $effects.Add($selection) }
    }
    $expected = switch ($mode) { full { 'always,checked,full' }; quick { 'always,checked' }; none { 'always' } }
    if (($effects -join ',') -cne $expected -or $context.Steps.Count -ne 3) { throw 'Test selection executed or concealed the wrong steps.' }
}
$failedContext = New-ApiBuildContext full
$failed = $false
try { Invoke-ApiBuildStep $failedContext 'failure' -Action { throw 'fixture failure' } } catch { $failed = $true }
if (-not $failed -or $failedContext.Steps[0].Status -cne 'Failed') { throw 'A failed step was swallowed or recorded as passed.' }
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
foreach ($mode in @('full','quick','none')) {
    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture 'tools/Build.ps1') -WorkspaceBuild -NoInstall $choice -TestMode $mode 2>&1)
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $oldPreference }
    if ($code -eq 0 -or ($output -join "`n") -notmatch 'FIXTURE_TOOLCHAIN_BOUNDARY') { throw 'Production driver blocked a valid workspace choice before the toolchain.' }
}
}
foreach ($arguments in @(@('-TestMode','invalid'),@('-TestMode','quick','-DriverArguments','/tests:none'))) {
    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture 'tools/Build.ps1') -WorkspaceBuild @arguments 2>&1)
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $oldPreference }
    if ($code -eq 0 -or ($output -join "`n") -match 'FIXTURE_TOOLCHAIN_BOUNDARY') { throw 'Invalid direct PowerShell mode reached the toolchain.' }
}
foreach ($mode in @('full','quick','none')) {
    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& (Join-Path $fixture 'build.bat') /source:working /nopause /noinstall "/tests:$mode" 2>&1)
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $oldPreference }
    if ($code -eq 0 -or ($output -join "`n") -notmatch 'FIXTURE_TOOLCHAIN_BOUNDARY') { throw 'Production BAT did not reach the production driver with its test selection.' }
}
Write-Host 'PASS: independent source/installation choices, automatic default, conflicts and menus; no real builds/installations.'
Write-Host "Fixture retained: $fixture"
