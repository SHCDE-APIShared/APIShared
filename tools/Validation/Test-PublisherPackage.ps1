$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot '../WorkshopPackage.ps1')
. (Join-Path $PSScriptRoot '../PublisherPackage.ps1')
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('APISharedPublisher-'+[Guid]::NewGuid().ToString('N'))
try {
$stage=Join-Path $fixture 'stage'
$publisher=Join-Path $stage 'BepInEx/tools/APISharedWorkshopPublisher'
$plugin=Join-Path $stage 'BepInEx/plugins/APIShared_Serp'
[void][IO.Directory]::CreateDirectory($publisher)
[void][IO.Directory]::CreateDirectory($plugin)
foreach ($name in Get-ApiPublisherNames) {
    $file = Join-Path $publisher $name
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($file))
    [IO.File]::WriteAllText($file,'fixture '+$name)
}
$record=[ordered]@{ SchemaVersion=1; Guid='APISharedWorkshopPublisher'; Version='1.0.0'; Files=@(Get-ApiPublisherFiles $publisher) }
[IO.File]::WriteAllText((Join-Path $publisher 'publisher-files.json'),($record | ConvertTo-Json -Depth 5))
Assert-ApiPublisherPackage $publisher
# Additional installed files are not part of our payload or our deletion authority.
$installed=Join-Path $fixture 'installed'
Copy-Item -LiteralPath $publisher -Destination $installed -Recurse
foreach($relative in @('fixture.txt','user-helper.dll','notes/history.txt')) {
    $path=Join-Path $installed $relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path,'user data')
}
Assert-ApiPublisherPackage $installed $publisher -Installed
foreach($relative in @('fixture.txt','user-helper.dll','notes/history.txt')) {
    if([IO.File]::ReadAllText((Join-Path $installed $relative)) -cne 'user data'){throw 'Unrelated installed file changed.'}
}
$owned=Join-Path $installed 'UploadMod.bat'
[IO.File]::WriteAllText($owned,'changed')
$failed=$false;try{Assert-ApiPublisherPackage $installed $publisher -Installed}catch{$failed=$true}
if(-not $failed){throw 'Changed owned installed file accepted.'}
Remove-Item -LiteralPath $owned
$failed=$false;try{Assert-ApiPublisherPackage $installed $publisher -Installed}catch{$failed=$true}
if(-not $failed){throw 'Missing owned installed file accepted.'}
[IO.File]::WriteAllText((Join-Path $plugin 'info.json'),'{"GUID":"APIShared_Serp","Version":"1.0.0"}')
$null=New-ApiWorkshopInventory $plugin
$zip=Join-Path $fixture 'valid.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$zip)
Assert-ApiWorkshopZipLayout $zip 'APIShared_Serp'
$launcher=Join-Path $publisher 'UploadMod-linux.sh'
[IO.File]::WriteAllText($launcher,'changed')
$failed=$false; try { Assert-ApiPublisherPackage $publisher } catch { $failed=$true }
if (-not $failed) { throw 'Changed Linux launcher passed publisher inventory.' }
$zip=Join-Path $fixture 'tampered.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$zip)
$failed=$false; try { Assert-ApiWorkshopZipLayout $zip 'APIShared_Serp' } catch { $failed=$true }
if (-not $failed) { throw 'Changed publisher bytes accepted before extraction.' }
[IO.File]::WriteAllText($launcher,'fixture UploadMod-linux.sh')
[IO.File]::WriteAllText((Join-Path $publisher 'extra.dll'),'unexpected')
$failed=$false; try { Assert-ApiPublisherPackage $publisher } catch { $failed=$true }
if (-not $failed) { throw 'Arbitrary publisher DLL was accepted.' }
Write-Host 'PASS: installed extras preserved, missing/changed owned files rejected; generated payload/ZIP ownership and hashes enforced.'
} finally {
    # This test creates no links; never clean an unverified tree.
    if(Test-Path -LiteralPath $fixture){Assert-ApiWorkshopUnlinkedTree $fixture;Remove-Item -LiteralPath $fixture -Recurse -Force}
}
