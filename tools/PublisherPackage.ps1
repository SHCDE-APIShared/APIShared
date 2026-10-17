# The tool is deliberately outside BepInEx/plugins. This private inventory does
# not alter the public plugin inventory or permit arbitrary runtime binaries.
function Get-ApiPublisherNames {
    return @('UploadMod.bat','UploadMod-linux.sh','WorkshopUploader.exe','WorkshopUploader.exe.config',
        'com.rlabrecque.steamworks.net.dll','ICSharpCode.SharpZipLib.dll','steam_api64.dll',
        'UnityEngine.CoreModule.dll','UnityEngine.SharedInternalsModule.dll')
}
function Get-ApiPublisherFiles([string]$Directory,[switch]$Installed) {
    Assert-ApiWorkshopUnlinkedPath $Directory
    $names = @(Get-ApiPublisherNames)
    # Generated payloads own their complete inventory. An installation owns only
    # its delivered files; unrelated files must neither fail validation nor be removed.
    if (-not $Installed) {
        Assert-ApiWorkshopUnlinkedTree $Directory
        $actual = @(Get-ChildItem -LiteralPath $Directory -Recurse -File -Force | Where-Object Name -ne 'publisher-files.json')
        if ($actual.Count -ne $names.Count) { throw 'Incomplete or unexpected generated publisher contents.' }
        foreach ($file in $actual) {
            if ($file.DirectoryName -cne [IO.Path]::GetFullPath($Directory).TrimEnd('\') -or $file.Name -cnotin $names) { throw "Unexpected publisher payload: $($file.FullName)" }
        }
    }
    foreach ($name in $names) { Assert-ApiWorkshopUnlinkedPath (Join-Path $Directory $name) }
    return @($names | ForEach-Object { [pscustomobject]@{ Path=$_; Sha256=(Get-WorkshopFileHash (Join-Path $Directory $_)) } })
}
function Assert-ApiPublisherPackage([string]$Directory,[string]$Source,[switch]$Installed) {
    $actual = @(Get-ApiPublisherFiles $Directory -Installed:$Installed)
    Assert-ApiWorkshopUnlinkedPath (Join-Path $Directory 'publisher-files.json')
    $record = [IO.File]::ReadAllText((Join-Path $Directory 'publisher-files.json')) | ConvertFrom-Json
    if ($record.SchemaVersion -ne 1 -or $record.Guid -cne 'APISharedWorkshopPublisher' -or @($record.Files).Count -ne $actual.Count) { throw 'Invalid publisher inventory.' }
    foreach ($file in $actual) {
        if (@($record.Files | Where-Object { $_.Path -ceq $file.Path -and $_.Sha256 -ceq $file.Sha256 }).Count -ne 1) { throw "Publisher inventory mismatch: $($file.Path)" }
        if ($Source -and (Get-WorkshopFileHash (Join-Path $Source $file.Path)) -cne $file.Sha256) { throw "Stale publisher file: $($file.Path)" }
    }
}
function Sync-ApiPublisherPackage([string]$Root) {
    . (Join-Path $Root 'tools/WorkshopUploader/PublisherBuildProof.ps1')
    Assert-WorkshopPublisherBuild $Root
    $source = Join-Path $Root '.local/WorkshopUploader'
    $proof=[IO.File]::ReadAllText((Join-Path $source 'build-proof.json')) | ConvertFrom-Json
    $names=@(Get-ApiPublisherNames)
    if (@($proof.Outputs).Count -ne $names.Count -or @($proof.Outputs | Where-Object { $_.Name -cnotin $names }).Count) { throw 'Publisher build has unexpected or incomplete dependencies.' }
    foreach ($name in $names) { if (@($proof.Outputs | Where-Object Name -CEQ $name).Count -ne 1) { throw "Publisher build evidence lacks $name" } }
    $target = [IO.Path]::GetFullPath((Join-Path $Root 'BepInEx/tools/APISharedWorkshopPublisher'))
    Assert-ApiWorkshopUnlinkedPath $target
    [void][IO.Directory]::CreateDirectory($target)
    Assert-ApiWorkshopUnlinkedTree $target
    # Remove generated output only, after checking the complete build evidence.
    foreach ($file in Get-ChildItem -LiteralPath $target -File -Force) { Remove-Item -LiteralPath $file.FullName -Force }
    foreach ($name in Get-ApiPublisherNames) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination $target }
    $record = [ordered]@{ SchemaVersion=1; Guid='APISharedWorkshopPublisher'; Version=([IO.File]::ReadAllText((Join-Path $Root 'info.json')) | ConvertFrom-Json).Version; Files=@(Get-ApiPublisherFiles $target) }
    $text = ($record | ConvertTo-Json -Depth 6).Replace("`r`n","`n").Replace("`n","`r`n") + "`r`n"
    [IO.File]::WriteAllText((Join-Path $target 'publisher-files.json'),$text,[Text.UTF8Encoding]::new($false))
    Assert-ApiPublisherPackage $target $source
}
