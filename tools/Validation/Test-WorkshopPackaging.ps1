param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../WorkshopPackage.ps1')
. (Join-Path $PSScriptRoot '../BuildProof.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ApiWorkshopPackaging-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $fixture 'stage/BepInEx/plugins/Example'
[void][IO.Directory]::CreateDirectory((Join-Path $package 'config'))
try {
    $assetRoot = Join-Path $fixture 'asset-project'
    $assetSource = Join-Path $assetRoot 'Workshop'
    $assetOutput = Join-Path $assetRoot 'BepInEx/plugins/APIShared_Serp/Workshop'
    $viewSource = Join-Path $assetRoot 'Override/Workshop/Workshop.xaml'
    $viewTarget = Join-Path $assetRoot 'BepInEx/plugins/APIShared_Serp/Override/Workshop/Workshop.xaml'
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($viewSource))
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../Override/Workshop/Workshop.xaml') -Destination $viewSource
    $neighbor = Join-Path $assetRoot 'BepInEx/plugins/APIShared_Serp/config/user.json'
    [void][IO.Directory]::CreateDirectory((Join-Path $assetSource 'nested'))
    [void][IO.Directory]::CreateDirectory((Join-Path $assetOutput 'obsolete'))
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($neighbor))
    [IO.File]::WriteAllText($neighbor, 'preserve user data')
    [IO.File]::WriteAllText((Join-Path $assetSource 'nested/current.ps1'), 'current asset')
    [IO.File]::WriteAllText((Join-Path $assetOutput 'obsolete/old.ps1'), 'stale asset')
    [IO.File]::WriteAllText((Join-Path $assetOutput 'old.txt'), 'stale file')
    Copy-Item -LiteralPath $viewSource -Destination (Join-Path $assetOutput 'Workshop.xaml')
    Sync-ApiWorkshopAssets -Root $assetRoot
    Assert-ApiWorkshopView $assetRoot (Join-Path $assetRoot 'BepInEx/plugins/APIShared_Serp')
    $overrideNeighbor = Join-Path $assetRoot 'BepInEx/plugins/APIShared_Serp/Override/other.txt'
    [IO.File]::WriteAllText($overrideNeighbor, 'keep unrelated override')
    if ([IO.File]::Exists((Join-Path $assetOutput 'Workshop.xaml'))) { throw 'Obsolete unindexed view survived generated packaging.' }
    if (Test-Path -LiteralPath (Join-Path $assetOutput 'obsolete')) { throw 'Stale asset directory survived packaging.' }
    if (Test-Path -LiteralPath (Join-Path $assetOutput 'old.txt')) { throw 'Stale asset file survived packaging.' }
    $firstAssets = @(Get-ApiWorkshopAssetRows $assetOutput)
    if (($firstAssets -join '|') -cne (@(Get-ApiWorkshopAssetRows $assetSource) -join '|')) { throw 'Assets differ from source.' }
    Sync-ApiWorkshopAssets -Root $assetRoot
    if (($firstAssets -join '|') -cne (@(Get-ApiWorkshopAssetRows $assetOutput) -join '|')) { throw 'Asset packaging is not repeatable.' }
    if ([IO.File]::ReadAllText($overrideNeighbor) -cne 'keep unrelated override') { throw 'Packaging replaced neighboring Override data.' }
    $savedView = [IO.File]::ReadAllBytes($viewSource)
    [IO.File]::Delete($viewSource)
    $rejected = $false
    try { Sync-ApiWorkshopAssets -Root $assetRoot } catch { $rejected = $true }
    if (-not $rejected -or -not [IO.File]::Exists($viewTarget) -or (@(Get-ApiWorkshopAssetRows $assetOutput) -join '|') -cne ($firstAssets -join '|')) { throw 'Missing view source changed the previous package.' }
    [IO.File]::WriteAllBytes($viewSource, $savedView)
    if ([IO.File]::ReadAllText($neighbor) -cne 'preserve user data') { throw 'Packaging modified neighboring data.' }
    [IO.File]::WriteAllText($viewTarget, 'damaged view')
    $rejected = $false
    try { Assert-ApiWorkshopView $assetRoot (Join-Path $assetRoot 'BepInEx/plugins/APIShared_Serp') } catch { $rejected = $true }
    if (-not $rejected) { throw 'Damaged packaged view passed validation.' }
    Sync-ApiWorkshopAssets -Root $assetRoot
    $installed = Join-Path $fixture 'installed'
    [void][IO.Directory]::CreateDirectory((Join-Path $installed 'Workshop'))
    $oldView = Join-Path $installed 'Workshop/Workshop.xaml'
    Copy-Item -LiteralPath $viewSource -Destination $oldView
    [IO.File]::WriteAllText((Join-Path $installed 'Workshop/user.txt'), 'keep')
    Remove-ApiLegacyWorkshopView $installed
    if ([IO.File]::Exists($oldView)) { throw 'Known obsolete installed view was not removed.' }
    [IO.File]::WriteAllText($oldView, 'user edited')
    Remove-ApiLegacyWorkshopView $installed
    if ([IO.File]::ReadAllText($oldView) -cne 'user edited' -or [IO.File]::ReadAllText((Join-Path $installed 'Workshop/user.txt')) -cne 'keep') { throw 'Installed view cleanup changed user data.' }
    [IO.File]::Delete((Join-Path $assetSource 'nested/current.ps1'))
    [IO.File]::WriteAllText((Join-Path $assetSource 'replacement.txt'), 'replacement asset')
    Sync-ApiWorkshopAssets -Root $assetRoot
    if (Test-Path -LiteralPath (Join-Path $assetOutput 'nested/current.ps1')) { throw 'Removed source asset survived next package.' }
    $missingRoot = Join-Path $fixture 'missing-source'
    $missingOutput = Join-Path $missingRoot 'BepInEx/plugins/APIShared_Serp/Workshop'
    [void][IO.Directory]::CreateDirectory($missingOutput)
    [IO.File]::WriteAllText((Join-Path $missingOutput 'keep.txt'), 'keep')
    $rejected = $false
    try { Sync-ApiWorkshopAssets -Root $missingRoot } catch { $rejected = $true }
    if (-not $rejected -or -not (Test-Path -LiteralPath (Join-Path $missingOutput 'keep.txt'))) { throw 'Missing source caused destructive packaging.' }
    # Junctions can be created on Windows without developer-mode symlink rights.
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        $outside = Join-Path $fixture 'outside-assets'
        [void][IO.Directory]::CreateDirectory($outside)
        [IO.File]::WriteAllText((Join-Path $outside 'keep.txt'), 'outside')
        $link = Join-Path $assetOutput 'linked'
        $null = New-Item -ItemType Junction -Path $link -Target $outside
        try {
            $rejected = $false
            try { Sync-ApiWorkshopAssets -Root $assetRoot } catch { $rejected = $true }
            if (-not $rejected -or [IO.File]::ReadAllText((Join-Path $outside 'keep.txt')) -cne 'outside' -or
                -not (Test-Path -LiteralPath (Join-Path $assetOutput 'replacement.txt'))) { throw 'Linked output was not rejected before deletion.' }
        } finally { [IO.Directory]::Delete($link, $false) }
        $linkedRoot = Join-Path $fixture 'linked-project'
        $null = New-Item -ItemType Junction -Path $linkedRoot -Target $assetRoot
        try {
            $rejected = $false
            try { Sync-ApiWorkshopAssets -Root $linkedRoot } catch { $rejected = $true }
            if (-not $rejected -or -not (Test-Path -LiteralPath (Join-Path $assetOutput 'replacement.txt'))) { throw 'Linked project root was not rejected.' }
        } finally { [IO.Directory]::Delete($linkedRoot, $false) }
    }
    [IO.File]::WriteAllText((Join-Path $package 'info.json'), '{"GUID":"Example","Version":"1.0.0"}')
    [IO.File]::WriteAllText((Join-Path $package 'Example.dll'), 'immutable payload')
    [IO.File]::WriteAllText((Join-Path $package 'config/user.json'), 'user data')
    $inventory = New-ApiWorkshopInventory -Directory $package
    if ($inventory.Files.Count -ne 2) { throw 'Mutable settings were inventoried.' }
    Assert-ApiWorkshopInventory -Directory $package
    $stage = Join-Path $fixture 'stage'
    $zip = Join-Path $fixture 'package.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
    Assert-ApiWorkshopZipLayout -ZipPath $zip -Guid 'Example'
    Assert-ApiWorkshopMap -MapPath $zip -Stage $stage
    [IO.File]::WriteAllText((Join-Path $package 'Example.dll'), 'modified')
    $rejected = $false
    try { Assert-ApiWorkshopInventory -Directory $package } catch { $rejected = $true }
    if (-not $rejected) { throw 'Modified payload passed its inventory.' }
    foreach ($entryName in @('BepInEx/plugins/Other/Other.dll', 'BepInEx/plugins/Example/../escape.dll')) {
        $bad = Join-Path $fixture ([Guid]::NewGuid().ToString('N') + '.zip')
        $archive = [IO.Compression.ZipFile]::Open($bad, [IO.Compression.ZipArchiveMode]::Create)
        try { [void]$archive.CreateEntry($entryName) } finally { $archive.Dispose() }
        $rejected = $false
        try { Assert-ApiWorkshopZipLayout -ZipPath $bad -Guid 'Example' } catch { $rejected = $true }
        if (-not $rejected) { throw "Unsafe ZIP accepted: $entryName" }
    }
} finally {
    # The fixture is generated locally and contains no links or external paths.
    if (-not [IO.Path]::GetFullPath($fixture).StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture path.' }
    [IO.Directory]::Delete($fixture, $true)
}
Write-Host 'PASS: exact/repeatable Workshop assets, safe paths, inventory completeness, settings exclusion, hashes, ZIP ownership/layout and archive roundtrip.'
