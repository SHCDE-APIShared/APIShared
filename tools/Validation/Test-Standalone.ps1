[CmdletBinding()]
param([string]$GameDir, [string]$ExtenderDir, [switch]$SourceOnly)
$ErrorActionPreference = 'Stop'
$apiRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $PSScriptRoot 'RuntimePreflight.Common.ps1')
Assert-SERuntimeModPreflight ([pscustomobject]@{ Name = 'APIShared'; Plugin = 'APIShared_Serp'; Project = 'APIShared.csproj' }) $apiRoot
if (-not $SourceOnly) { & (Join-Path $PSScriptRoot 'Verify-SavegameLoadContracts.ps1') -GameDir $GameDir -ExtenderDir $ExtenderDir }
. (Join-Path $PSScriptRoot 'DependencyMetadata.Common.ps1')
$source = (Get-ChildItem -LiteralPath (Join-Path $apiRoot 'src') -Recurse -File -Filter '*.cs' | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join [Environment]::NewLine
Assert-PluginDependencyMetadata $source ([IO.File]::ReadAllText((Join-Path $apiRoot 'info.json')) | ConvertFrom-Json) 'APIShared'
$projects = @(Get-ChildItem -LiteralPath $apiRoot -Recurse -File -Filter '*.csproj' | Where-Object FullName -NotMatch '[\\/](bin|obj|BepInEx)[\\/]')
$links = 0
foreach ($project in $projects) {
    [xml]$xml = [IO.File]::ReadAllText($project.FullName)
    foreach ($entry in $xml.SelectNodes('//*[local-name()="Compile" or local-name()="ProjectReference"][@Include]')) {
        $include = [string]$entry.Include
        if ($include -match '\$\(|[*?]') { continue }
        $target = [IO.Path]::GetFullPath((Join-Path $project.Directory.FullName $include))
        if (-not $target.StartsWith($apiRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "External source/project link: $target" }
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Missing source/project link: $target" }
        $links++
    }
}
$files = @(Get-ChildItem -LiteralPath $apiRoot -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj|BepInEx|\.git)[\\/]' -and $_.Extension -match '^\.(cs|csproj|ps1|bat|md|json|xaml|props|targets)$'
})
foreach ($file in $files) {
    $text = [IO.File]::ReadAllText($file.FullName)
    if ($file.Extension -eq '.cs' -and $text -cmatch '\busing\s+Shared\s*;|\bnamespace\s+Shared\b|(?<!API)\bShared\.(?!MissionEvents\.")') {
        throw "Workspace Shared type dependency: $($file.FullName)"
    }
}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $apiRoot 'Patches') -Recurse -File -Filter '*.xaml') {
    [xml]$xml = [IO.File]::ReadAllText($file.FullName)
    foreach ($content in $xml.SelectNodes('//*[local-name()="Content"]')) {
        $roots = @($content.ChildNodes | Where-Object NodeType -eq ([Xml.XmlNodeType]::Element))
        if ($roots.Count -ne 1) { throw "XAML Content requires one root: $($file.FullName)" }
    }
}
& (Join-Path $PSScriptRoot 'Test-DependencyMetadata.ps1')
& (Join-Path $PSScriptRoot 'Test-PermanentHooks.ps1')
& (Join-Path $PSScriptRoot 'Test-Documentation.ps1')
& (Join-Path $PSScriptRoot 'Test-WorkshopPackaging.ps1')
& (Join-Path $PSScriptRoot 'Test-PublisherPackage.ps1')
& (Join-Path $PSScriptRoot 'Test-BuildProof.ps1')
if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-GitHubRelease.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Windows PowerShell release regression failed.' }
}
& (Join-Path $PSScriptRoot 'Test-WorkshopManager.ps1')
& (Join-Path $PSScriptRoot 'Test-ShellScripts.ps1')
if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-WorkshopRecovery.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Workshop updater recovery regression failed.' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-WorkshopCoordination.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Workshop updater coordination regression failed.' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Test-WorkshopMaintenance.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Workshop updater maintenance regression failed.' }
} else {
    & bash (Join-Path $PSScriptRoot 'test-workshop-coordination.sh')
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 77) { throw 'Linux Workshop updater coordination regression failed.' }
    & bash (Join-Path $PSScriptRoot 'test-workshop-maintenance.sh')
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 77) { throw 'Linux Workshop updater maintenance regression failed.' }
    & bash (Join-Path $PSScriptRoot 'test-workshop-updater.sh')
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 77) { throw 'Linux Workshop updater control-flow regression failed.' }
}
& (Join-Path $PSScriptRoot 'Test-WorkshopMigration.ps1')
if (-not $SourceOnly) { & (Join-Path $PSScriptRoot 'Verify-AudioContracts.ps1') -GameDir $GameDir -ExtenderDir $ExtenderDir }
if (-not $SourceOnly) { & (Join-Path $PSScriptRoot 'Verify-Interop.ps1') -GameDir $GameDir -ExtenderDir $ExtenderDir }
if (-not $SourceOnly) { & (Join-Path $PSScriptRoot 'Verify-WorkshopContracts.ps1') -GameDir $GameDir -ExtenderDir $ExtenderDir }
if (-not $SourceOnly) { & (Join-Path $PSScriptRoot 'Verify-BuildingTooltipContracts.ps1') -GameDir $GameDir }
if (-not $SourceOnly) { & (Join-Path $PSScriptRoot 'Verify-HudExtrasContracts.ps1') -GameDir $GameDir -ExtenderDir $ExtenderDir }
if (-not $SourceOnly) { & (Join-Path $PSScriptRoot 'Verify-ManagedInterceptionContracts.ps1') -GameDir $GameDir }
if (-not $SourceOnly) { & (Join-Path $PSScriptRoot 'Verify-NetworkContracts.ps1') -GameDir $GameDir -ExtenderDir $ExtenderDir }
Write-Host "PASS: standalone APIShared boundaries ($links links), runtime JSON/lifecycle/scheduling and XAML; installed interop is checked in the local full run."
