function Assert-ApiWorkshopUnlinkedPath([string]$Path) {
    for ($part = [IO.Path]::GetFullPath($Path); $part; $part = [IO.Path]::GetDirectoryName($part)) {
        try {
            if ([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint) { throw "Linked packaging path: $part" }
        } catch [IO.FileNotFoundException] { }
        catch [IO.DirectoryNotFoundException] { }
    }
}

function Assert-ApiWorkshopUnlinkedTree([string]$Directory) {
    Assert-ApiWorkshopUnlinkedPath $Directory
    if (-not [IO.Directory]::Exists($Directory)) { throw "Missing packaging directory: $Directory" }
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($Directory)) {
        $attributes = [IO.File]::GetAttributes($entry)
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked packaging entry: $entry" }
        if ($attributes -band [IO.FileAttributes]::Directory) { Assert-ApiWorkshopUnlinkedTree $entry }
    }
}

function Get-ApiWorkshopAssetRows([string]$Directory) {
    Assert-ApiWorkshopUnlinkedTree $Directory
    $rows = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $Directory -File -Recurse -Force) {
        $relative = $file.FullName.Substring($Directory.Length + 1).Replace('\','/')
        $rows.Add($relative + ':' + (Get-WorkshopFileHash $file.FullName))
    }
    $rows.Sort([StringComparer]::Ordinal)
    return $rows.ToArray()
}

function Sync-ApiWorkshopAssets {
    param([Parameter(Mandatory)][string]$Root)
    $Root = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $source = [IO.Path]::GetFullPath((Join-Path $Root 'Workshop'))
    $package = [IO.Path]::GetFullPath((Join-Path $Root 'BepInEx/plugins/APIShared_Serp'))
    $destination = [IO.Path]::GetFullPath((Join-Path $package 'Workshop'))
    # Only generated assets below this project's package can be replaced. Check
    # both trees before deletion, including links nested inside the old output.
    if (-not $package.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetDirectoryName($destination) -cne $package -or [IO.Path]::GetFileName($destination) -cne 'Workshop') {
        throw 'Unsafe generated Workshop directory.'
    }
    $expected = @(Get-ApiWorkshopAssetRows $source)
    if ($expected.Count -eq 0) { throw 'Workshop source assets are empty.' }
    Assert-ApiWorkshopUnlinkedPath $destination
    if ([IO.File]::Exists($destination)) { throw 'Generated Workshop destination is a file.' }
    if ([IO.Directory]::Exists($destination)) {
        Assert-ApiWorkshopUnlinkedTree $destination
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    [void][IO.Directory]::CreateDirectory($package)
    Copy-Item -LiteralPath $source -Destination $package -Recurse -Force
    $actual = @(Get-ApiWorkshopAssetRows $destination)
    $currentSource = @(Get-ApiWorkshopAssetRows $source)
    if (($expected -join "`n") -cne ($actual -join "`n") -or ($expected -join "`n") -cne ($currentSource -join "`n")) {
        throw 'Workshop package paths/hashes differ from current sources.'
    }
}

# Only this previously shipped immutable view is obsolete. Never remove an
# edited copy or other files from the installed updater/template directory.
function Remove-ApiLegacyWorkshopView([string]$Directory) {
    foreach ($relative in @('Workshop/Workshop.xaml','Override/Workshop/Workshop.xaml')) {
    $path = Join-Path $Directory $relative
    Assert-ApiWorkshopUnlinkedPath $path
    if (-not [IO.File]::Exists($path)) { continue }
    if ((Get-WorkshopFileHash $path) -ceq '19ad493dddf6831f5cfe06f788ed4b7fb2c30af020bd159c304fd8ca2315e877') {
        Remove-Item -LiteralPath $path -Force
    } else {
        Write-Warning 'Preserved a locally modified obsolete Workshop/Workshop.xaml; Workshop UI registration has been removed.'
    }
}

}

function Get-WorkshopFileHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
    finally { $hash.Dispose(); $stream.Dispose() }
}

# Offline packaging; runtime uses APIShared.Internal.DependencyFreeJson.
function New-ApiWorkshopInventory {
    param([Parameter(Mandatory)][string]$Directory, [object[]]$LegacyFiles = @())
    $info = [IO.File]::ReadAllText((Join-Path $Directory 'info.json')) | ConvertFrom-Json
    $files = @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Where-Object {
        $_.Name -ne 'apishared-workshop-files.json' -and $_.FullName -notmatch '[\\/](LobbyModSettings|config)[\\/]'
    } | ForEach-Object {
        [ordered]@{ Path = $_.FullName.Substring($Directory.TrimEnd('\').Length).TrimStart('\').Replace('\','/'); Sha256 = (Get-WorkshopFileHash $_.FullName) }
    } | Sort-Object { $_.Path })
    if ($files.Count -eq 0) { throw 'Cannot inventory an empty package.' }
    $record = [ordered]@{ SchemaVersion = 1; Guid = [string]$info.GUID; Version = [string]$info.Version; Files = $files; LegacyFiles = @($LegacyFiles) }
    $path = Join-Path $Directory 'apishared-workshop-files.json'
    $expected = (($record | ConvertTo-Json -Depth 10) -replace "`r?`n", "`r`n") + "`r`n"
    [IO.File]::WriteAllText($path, $expected, [Text.UTF8Encoding]::new($false))
    if ([IO.File]::ReadAllText($path) -cne $expected) { throw 'Inventory write verification failed.' }
    return $record
}

function Set-ApiBootstrapMapPayload {
    param([Parameter(Mandatory)][string]$MapPath, [Parameter(Mandatory)][string]$Stage)
    $Stage=[IO.Path]::GetFullPath($Stage).TrimEnd('\')
    # Keep the native map prefix, replacing template residue with the exact staging inventory.
    Add-Type -AssemblyName System.IO.Compression
    $mapBytes=[IO.File]::ReadAllBytes($MapPath)
    $zipOffset=-1
    for ($index=0; $index -le $mapBytes.Length-4; $index++) {
        if ($mapBytes[$index] -eq 80 -and $mapBytes[$index+1] -eq 75 -and $mapBytes[$index+2] -eq 3 -and $mapBytes[$index+3] -eq 4) { $zipOffset=$index; break }
    }
    if ($zipOffset -lt 0) { throw 'Bootstrap map has no ZIP section.' }
    $zipStream=[IO.MemoryStream]::new()
    try {
        $zip=[IO.Compression.ZipArchive]::new($zipStream,[IO.Compression.ZipArchiveMode]::Create,$true)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $Stage -Recurse -File | Sort-Object FullName) {
                $entry=$zip.CreateEntry($file.FullName.Substring($Stage.Length).TrimStart('\').Replace('\','/'))
                $entryStream=$entry.Open()
                $input=[IO.File]::OpenRead($file.FullName)
                try { $input.CopyTo($entryStream) } finally { $input.Dispose(); $entryStream.Dispose() }
            }
        } finally { $zip.Dispose() }
        $output=[IO.File]::Open($MapPath,[IO.FileMode]::Create,[IO.FileAccess]::Write)
        try { $output.Write($mapBytes,0,$zipOffset); $zipStream.Position=0; $zipStream.CopyTo($output) } finally { $output.Dispose() }
    } finally { $zipStream.Dispose() }
    Assert-ApiWorkshopMap -MapPath $MapPath -Stage $Stage
}

function Assert-ApiWorkshopMap {
    param([string]$MapPath, [string]$Stage)
    Add-Type -AssemblyName System.IO.Compression
    $bytes = [IO.File]::ReadAllBytes($MapPath)
    $offset = -1
    for ($i = 0; $i -le $bytes.Length - 4; $i++) {
        if ($bytes[$i] -eq 80 -and $bytes[$i+1] -eq 75 -and $bytes[$i+2] -eq 3 -and $bytes[$i+3] -eq 4) { $offset = $i; break }
    }
    if ($offset -lt 0) { throw 'Workshop map has no ZIP payload.' }
    $stream = [IO.MemoryStream]::new($bytes, $offset, $bytes.Length-$offset, $false)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
    try {
        $expected = @(Get-ChildItem -LiteralPath $Stage -Recurse -File | ForEach-Object { $_.FullName.Substring($Stage.Length).TrimStart('\').Replace('\','/') } | Sort-Object)
        $actual = @($zip.Entries | Where-Object Name | ForEach-Object { $_.FullName.Replace('\','/') } | Sort-Object)
        if (($expected -join '|') -cne ($actual -join '|')) { throw 'Workshop map paths differ from staging.' }
        foreach ($entry in $zip.Entries | Where-Object Name) {
            $source = Join-Path $Stage $entry.FullName.Replace('/','\')
            $input = $entry.Open(); $hash = [Security.Cryptography.SHA256]::Create()
            try { $actualHash = [BitConverter]::ToString($hash.ComputeHash($input)).Replace('-','').ToLowerInvariant() }
            finally { $hash.Dispose(); $input.Dispose() }
            if ($actualHash -cne (Get-WorkshopFileHash $source)) { throw "Workshop map hash mismatch: $($entry.FullName)" }
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
}

# Validate published inventories against the final extracted payload, including
# completeness. This runs offline; mods do not need another runtime dependency.
function Assert-ApiWorkshopInventory {
    param([Parameter(Mandatory)][string]$Directory)
    $info = [IO.File]::ReadAllText((Join-Path $Directory 'info.json')) | ConvertFrom-Json
    $inventory = [IO.File]::ReadAllText((Join-Path $Directory 'apishared-workshop-files.json')) | ConvertFrom-Json
    if (-not ($inventory.SchemaVersion -is [int] -or $inventory.SchemaVersion -is [long]) -or $inventory.SchemaVersion -ne 1 -or
        $inventory.Guid -cne $info.GUID -or $inventory.Version -cne $info.Version) { throw 'Inventory identity/schema mismatch.' }
    $expected = @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Where-Object {
        $_.Name -ne 'apishared-workshop-files.json' -and $_.FullName -notmatch '[\\/](LobbyModSettings|config)[\\/]'
    } | ForEach-Object { $_.FullName.Substring($Directory.TrimEnd('\').Length).TrimStart('\').Replace('\','/') } | Sort-Object)
    $actual = @($inventory.Files | ForEach-Object { [string]$_.Path } | Sort-Object)
    if (($actual -join '|') -cne ($expected -join '|') -or $actual.Count -eq 0) { throw 'Inventory paths differ from final payload.' }
    foreach ($record in $inventory.Files) {
        if ([string]$record.Sha256 -cne (Get-WorkshopFileHash (Join-Path $Directory $record.Path.Replace('/','\')))) { throw "Inventory hash mismatch: $($record.Path)" }
    }
}

# Before extraction, accept only this package's GUID directory. Reject ambiguous
# paths even if an archive extractor happens to sanitize them on this machine.
function Assert-ApiWorkshopZipLayout {
    param([Parameter(Mandatory)][string]$ZipPath, [Parameter(Mandatory)][string]$Guid)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $prefix = "BepInEx/plugins/$Guid/"
    $toolPrefix = 'BepInEx/tools/APISharedWorkshopPublisher/'
    $toolEntries = @{}
    try {
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\','/')
            if ($name.Contains(':') -or $name -match '(^|/)\.\.?(/|$)' -or $name.Contains('//') -or
                (($entry.ExternalAttributes -shr 16 -band 0xF000) -eq 0xA000) -or
                -not $seen.Add($name.TrimEnd('/')) -or
                ($entry.Name -and (-not $name.StartsWith($prefix, [StringComparison]::Ordinal) -and
                    -not ($Guid -ceq 'APIShared_Serp' -and $name.StartsWith($toolPrefix, [StringComparison]::Ordinal))))) {
                throw "Unsafe or unexpected APIShared ZIP entry: $name"
            }
            if (-not $entry.Name -and $name -notin @('BepInEx/','BepInEx/plugins/',$prefix) -and
                -not ($Guid -ceq 'APIShared_Serp' -and $name -cin @('BepInEx/tools/',$toolPrefix)) -and
                -not $name.StartsWith($prefix, [StringComparison]::Ordinal)) { throw "Unexpected APIShared ZIP directory: $name" }
            if ($entry.Name -and $name.StartsWith($toolPrefix, [StringComparison]::Ordinal)) { $toolEntries.Add($name.Substring($toolPrefix.Length),$entry) }
        }
        if ($seen.Count -eq 0) { throw 'APIShared ZIP has no files.' }
        if ($Guid -ceq 'APIShared_Serp') {
            . (Join-Path $PSScriptRoot 'PublisherPackage.ps1')
            $allowed=@(Get-ApiPublisherNames)+@('publisher-files.json')
            if ($toolEntries.Count -ne $allowed.Count -or @($toolEntries.Keys | Where-Object { $_ -cnotin $allowed }).Count) { throw 'Incomplete or unexpected ZIP publisher.' }
            $reader=[IO.StreamReader]::new($toolEntries['publisher-files.json'].Open())
            try { $record=$reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
            if ($record.SchemaVersion -ne 1 -or $record.Guid -cne 'APISharedWorkshopPublisher' -or @($record.Files).Count -ne $allowed.Count-1) { throw 'Invalid ZIP publisher inventory.' }
            foreach ($name in Get-ApiPublisherNames) {
                $matches=@($record.Files | Where-Object Path -CEQ $name)
                if ($matches.Count -ne 1) { throw 'Ambiguous ZIP publisher inventory.' }
                $input=$toolEntries[$name].Open(); $sha=[Security.Cryptography.SHA256]::Create()
                try { $hash=[BitConverter]::ToString($sha.ComputeHash($input)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose(); $input.Dispose() }
                if ($hash -cne $matches[0].Sha256) { throw "ZIP publisher hash mismatch: $name" }
            }
        }
    } finally { $zip.Dispose() }
}
