[CmdletBinding()]
param([string]$GameDir, [string]$ExtenderDir, [switch]$SourceOnly,
    [ValidateSet('full','quick')][string]$CheckMode = 'full', $BuildContext = $null)
$ErrorActionPreference = 'Stop'
$apiRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $PSScriptRoot '../BuildChecks.ps1')
if (-not $BuildContext) { $BuildContext = New-ApiBuildContext $CheckMode }
Invoke-ApiBuildStep $BuildContext 'current-source-boundaries' -When checked -Action {
. (Join-Path $PSScriptRoot 'RuntimePreflight.Common.ps1')
Assert-SERuntimeModPreflight ([pscustomobject]@{ Name = 'APIShared'; Plugin = 'APIShared_Serp'; Project = 'APIShared.csproj' }) $apiRoot
. (Join-Path $PSScriptRoot 'DependencyMetadata.Common.ps1')
$source = (Get-ChildItem -LiteralPath (Join-Path $apiRoot 'src') -Recurse -File -Filter '*.cs' | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join [Environment]::NewLine
Assert-PluginDependencyMetadata $source ([IO.File]::ReadAllText((Join-Path $apiRoot 'info.json')) | ConvertFrom-Json) 'APIShared'
$maintained = @(Get-ApiMaintainedFiles $apiRoot)
$projects = @($maintained | Where-Object Extension -EQ '.csproj')
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
$files = @($maintained | Where-Object {
    $_.Extension -match '^\.(cs|csproj|ps1|bat|md|json|xaml|props|targets)$'
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
Write-Host "PASS: standalone source/project boundaries ($links links), runtime JSON/lifecycle/scheduling and XAML."
}
foreach ($check in @('Test-PermanentHooks.ps1')) {
    Invoke-ApiBuildStep $BuildContext $check -When checked -Action { & (Join-Path $PSScriptRoot $check) }
}
foreach ($check in @('Test-DependencyMetadata.ps1','Test-Documentation.ps1','Test-WorkshopPackaging.ps1','Test-PublisherPackage.ps1','Test-BuildProof.ps1','Test-ShellScripts.ps1','Test-WorkshopMigration.ps1')) {
    Invoke-ApiBuildStep $BuildContext $check -Action { & (Join-Path $PSScriptRoot $check) }
}
if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
    foreach ($check in @('Test-GitHubRelease.ps1','Test-WorkshopRecovery.ps1','Test-WorkshopCoordination.ps1','Test-WorkshopMaintenance.ps1')) {
        Invoke-ApiBuildStep $BuildContext $check -Action {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $check)
            if ($LASTEXITCODE -ne 0) { throw "$check failed in Windows PowerShell." }
        }
    }
} else {
    foreach ($check in @('test-workshop-coordination.sh','test-workshop-maintenance.sh','test-workshop-updater.sh')) {
        Invoke-ApiBuildStep $BuildContext $check -Action {
            & bash (Join-Path $PSScriptRoot $check)
            if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 77) { throw "$check failed." }
        }
    }
}
# Historical hotfix scope remains an explicit opt-in audit, independent of version strings.
if (-not $SourceOnly) {
    foreach ($check in @('Verify-SavegameLoadContracts.ps1','Verify-AudioContracts.ps1','Verify-Interop.ps1','Verify-WorkshopContracts.ps1','Verify-BuildingTooltipContracts.ps1','Test-TooltipAnchors.ps1','Verify-HudExtrasContracts.ps1','Verify-ManagedInterceptionContracts.ps1','Verify-NetworkContracts.ps1')) {
        Invoke-ApiBuildStep $BuildContext $check -When checked -Action {
            $parameters = @{ GameDir=$GameDir; ExtenderDir=$ExtenderDir }
            if ($check -in @('Verify-BuildingTooltipContracts.ps1','Verify-ManagedInterceptionContracts.ps1')) { $parameters.Remove('ExtenderDir') }
            if ($check -eq 'Test-TooltipAnchors.ps1') { $parameters.Remove('GameDir') }
            & (Join-Path $PSScriptRoot $check) @parameters
        }
    }
}
Write-Host "PASS: standalone APIShared validation; tests:$CheckMode; source-only:$SourceOnly."
