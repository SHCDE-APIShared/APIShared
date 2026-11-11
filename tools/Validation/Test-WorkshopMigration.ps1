$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../WorkshopMigration.ps1')
Add-Type -AssemblyName System.IO.Compression
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('ApiMigrationVersion-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
function New-Map([string]$Name, [string[]]$Metadata) {
    $map = Join-Path $fixture ($Name + '.map')
    $memory = [IO.MemoryStream]::new()
    $zip = [IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        foreach ($json in $Metadata) {
            $entry = $zip.CreateEntry('BepInEx/plugins/SerpsMods_Serp/Infrastructure/APIShared_Serp/info.json')
            $writer = [IO.StreamWriter]::new($entry.Open()); try { $writer.Write($json) } finally { $writer.Dispose() }
        }
        $entry = $zip.CreateEntry('BepInEx/plugins/SerpsMods_Serp/Infrastructure/APIShared_Serp/APIShared.dll')
        $writer = [IO.StreamWriter]::new($entry.Open()); try { $writer.Write('historical fixture') } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
    try { [IO.File]::WriteAllBytes($map, ([byte[]](1,2,3,4) + $memory.ToArray())) } finally { $memory.Dispose() }
    return $map
}
$old = New-Map 'old' @('{"GUID":"APIShared_Serp","Version":"0.4.10"}')
Assert-ApiWorkshopMigrationVersion '0.7.1' @($old) 'SerpsMods_Serp'
foreach ($version in @('0.4.10','0.4.9')) {
    $rejected = $false; try { Assert-ApiWorkshopMigrationVersion $version @($old) 'SerpsMods_Serp' } catch { $rejected = $true }
    if (-not $rejected) { throw 'Equal/older central version accepted.' }
}
foreach ($map in @((New-Map 'missing' @()),(New-Map 'duplicate' @('{"GUID":"APIShared_Serp","Version":"0.4.10"}','{"GUID":"APIShared_Serp","Version":"0.4.10"}')),(New-Map 'identity' @('{"GUID":"Foreign","Version":"0.4.10"}')),(New-Map 'invalid' @('{"GUID":"APIShared_Serp","Version":"broken"}')))) {
    $rejected = $false; try { Assert-ApiWorkshopMigrationVersion '0.7.1' @($map) 'SerpsMods_Serp' } catch { $rejected = $true }
    if (-not $rejected) { throw 'Unproven historical APIShared accepted.' }
}
# Generated-bootstrap readiness is exercised by Test-WorkshopBootstrapReadiness,
# including an already loaded foreign DLL. Warning wording proves no readiness contract.
Write-Host ('PASS: historical APIShared version ordering and ambiguous/missing metadata rejection. Fixtures: ' + $fixture)
