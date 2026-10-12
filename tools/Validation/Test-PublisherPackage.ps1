$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../WorkshopPackage.ps1')
. (Join-Path $PSScriptRoot '../PublisherPackage.ps1')
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('APISharedPublisher-'+[Guid]::NewGuid().ToString('N'))
$stage=Join-Path $fixture 'stage'
$publisher=Join-Path $stage 'BepInEx/tools/APISharedWorkshopPublisher'
$plugin=Join-Path $stage 'BepInEx/plugins/APIShared_Serp'
[void][IO.Directory]::CreateDirectory($publisher)
[void][IO.Directory]::CreateDirectory($plugin)
foreach ($name in Get-ApiPublisherNames) { [IO.File]::WriteAllText((Join-Path $publisher $name),'fixture '+$name) }
$record=[ordered]@{ SchemaVersion=1; Guid='APISharedWorkshopPublisher'; Version='1.0.0'; Files=@(Get-ApiPublisherFiles $publisher) }
[IO.File]::WriteAllText((Join-Path $publisher 'publisher-files.json'),($record | ConvertTo-Json -Depth 5))
Assert-ApiPublisherPackage $publisher
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
Write-Host "PASS: complete publisher inventory, Linux launcher hashes, narrow ZIP tool ownership, tampering and extra DLL rejection. Fixtures: $fixture"
