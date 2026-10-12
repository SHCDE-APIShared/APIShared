# Build-time checks only. Historical map paths must first be verified against upload history.
function Assert-ApiWorkshopMigrationVersion([string]$Version, [string[]]$LegacyMapPaths, [string]$HostGuid) {
    Add-Type -AssemblyName System.IO.Compression
    $current = [version]$Version
    $entryName = 'BepInEx/plugins/' + $HostGuid + '/Infrastructure/APIShared_Serp/info.json'
    foreach ($map in $LegacyMapPaths) {
        $bytes = [IO.File]::ReadAllBytes($map)
        $offset = -1
        for ($i = 0; $i -le $bytes.Length - 4; $i++) {
            if ($bytes[$i] -eq 80 -and $bytes[$i+1] -eq 75 -and $bytes[$i+2] -eq 3 -and $bytes[$i+3] -eq 4) { $offset = $i; break }
        }
        if ($offset -lt 0) { throw "Historical map has no ZIP payload: $map" }
        $stream = [IO.MemoryStream]::new($bytes, $offset, $bytes.Length-$offset, $false)
        $zip = $null
        try {
            $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
            $entries = @($zip.Entries | Where-Object { $_.FullName.Replace('\','/').Equals($entryName,[StringComparison]::OrdinalIgnoreCase) })
            if ($entries.Count -gt 1) { throw "Ambiguous historical APIShared metadata: $map" }
            if (-not $entries.Count) {
                $prefix = $entryName.Substring(0, $entryName.Length - 'info.json'.Length)
                if (@($zip.Entries | Where-Object { $_.FullName.Replace('\','/').StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) }).Count) {
                    throw "Embedded APIShared has no version evidence: $map"
                }
                continue
            }
            $reader = [IO.StreamReader]::new($entries[0].Open())
            try { $info = $reader.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop } finally { $reader.Dispose() }
            if ($info.GUID -cne 'APIShared_Serp' -or -not $info.Version) { throw "Invalid historical APIShared identity: $map" }
            $old = [version]$info.Version
            if ($current -le $old) { throw "Central APIShared $Version must be newer than embedded APIShared $old in $map." }
        } finally { if ($zip) { $zip.Dispose() }; $stream.Dispose() }
    }
}
