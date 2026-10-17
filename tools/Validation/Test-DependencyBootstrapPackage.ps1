param([string]$PackageRoot)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'tools/WorkshopPackage.ps1')
. (Join-Path $root 'tools/DependencyBootstrapPackage.ps1')
. (Join-Path $root 'tools/PublisherPackage.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fixture=Join-Path $root ('.local/bootstrap-package-tests/'+[guid]::NewGuid().ToString('N'))
$stage=Join-Path $fixture 'package'
$plugin=Join-Path $stage 'BepInEx/plugins/APIShared_Serp'
function Write-TestFile($Path,$Text){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))|Out-Null;[IO.File]::WriteAllText($Path,$Text)}
function Check($Condition,$Name){if(-not $Condition){throw "FAIL: $Name"};Write-Host "PASS: $Name"}
function Reject([scriptblock]$Action,$Name){$failed=$false;try{& $Action}catch{$failed=$true};Check $failed $Name}
Write-TestFile (Join-Path $plugin 'APIShared.dll') 'runtime fixture'
Write-TestFile (Join-Path $plugin 'info.json') '{"GUID":"APIShared_Serp","Version":"1.2.3"}'
Write-TestFile (Join-Path $stage 'BepInEx/patchers/APIShared.DependencyBootstrap.dll') 'bootstrap fixture'
New-ApiBootstrapPair $stage
$install=Join-Path $fixture 'install'
Complete-ApiBootstrapInstallation -Root $stage -GameDir $install -NoInstall
Check (-not (Test-Path -LiteralPath $install)) 'NoInstall creates no installation files'
$installedPlugin=Join-Path $install 'BepInEx/plugins/APIShared_Serp'
[IO.Directory]::CreateDirectory($installedPlugin)|Out-Null
Copy-Item -LiteralPath (Join-Path $plugin 'APIShared.dll'),(Join-Path $plugin 'info.json'),(Join-Path $plugin 'dependency-bootstrap-pair.json') -Destination $installedPlugin
$foreignInstall=Join-Path $install 'BepInEx/patchers/Unrelated.dll'
Write-TestFile $foreignInstall 'preserve foreign patcher'
Complete-ApiBootstrapInstallation -Root $stage -GameDir $install
Assert-ApiBootstrapPair $install
Check ([IO.File]::ReadAllText($foreignInstall) -ceq 'preserve foreign patcher') 'complete installation preserves unrelated patcher'
Write-TestFile (Join-Path $installedPlugin 'APIShared.dll') 'different runtime'
Reject {Complete-ApiBootstrapInstallation -Root $stage -GameDir $install} 'mixed runtime rejected before bootstrap copy'
$null=New-ApiWorkshopInventory $plugin
$tools=Join-Path $stage 'BepInEx/tools/APISharedWorkshopPublisher'
$files=@(foreach($name in Get-ApiPublisherNames){$path=Join-Path $tools $name;Write-TestFile $path 'publisher fixture';[ordered]@{Path=$name;Sha256=Get-WorkshopFileHash $path}})
Write-TestFile (Join-Path $tools 'publisher-files.json') (@{SchemaVersion=1;Guid='APISharedWorkshopPublisher';Version='1.2.3';Files=$files;LegacyFiles=@()}|ConvertTo-Json -Depth 5)
function Make-Zip($Name){$path=Join-Path $fixture ($Name+'.zip');[IO.Compression.ZipFile]::CreateFromDirectory($stage,$path);return $path}
$zip=Make-Zip 'valid'
Assert-ApiWorkshopZipLayout $zip 'APIShared_Serp'
Check $true 'actual ZIP validator accepts exact complete bootstrap pair'
Reject {Assert-ApiWorkshopZipLayout $zip 'OtherMod'} 'foreign package cannot deliver bootstrap'
$bootstrap=Join-Path $stage 'BepInEx/patchers/APIShared.DependencyBootstrap.dll'
Write-TestFile $bootstrap 'changed'
Reject {Assert-ApiBootstrapPair $stage} 'changed same-version bootstrap rejected'
$zip=Make-Zip 'changed';Reject {Assert-ApiWorkshopZipLayout $zip 'APIShared_Serp'} 'ZIP changed bootstrap rejected'
Write-TestFile $bootstrap 'bootstrap fixture'
$foreign=Join-Path $stage 'BepInEx/patchers/Foreign.dll';Write-TestFile $foreign 'foreign'
$zip=Make-Zip 'foreign';Reject {Assert-ApiWorkshopZipLayout $zip 'APIShared_Serp'} 'ZIP rejects other patcher path'
Remove-Item -LiteralPath $foreign
$game=Join-Path $fixture 'game'
$target=Join-Path $game 'BepInEx/patchers/APIShared.DependencyBootstrap.dll'
Write-TestFile $target 'bootstrap fixture'
$remove=Join-Path $root 'tools/Remove-DependencyBootstrap.ps1'
& $remove -GameDir $game -PackageRoot $stage
Check (Test-Path -LiteralPath $target) 'default removal is preview'
Write-TestFile $target 'changed after preparation'
Reject {& $remove -GameDir $game -PackageRoot $stage -Apply} 'changed target preserved'
Write-TestFile $target 'bootstrap fixture'
$claim=Join-Path $game 'BepInEx/plugins/Other/dependency-bootstrap-pair.json'
Write-TestFile $claim '{}'
Reject {& $remove -GameDir $game -PackageRoot $stage -Apply} 'competing claim preserved'
Remove-Item -LiteralPath $claim
$pending=Join-Path $game '_SE/.staging/new.txt';Write-TestFile $pending 'pending'
Reject {& $remove -GameDir $game -PackageRoot $stage -Apply} 'pending staging preserved'
Remove-Item -LiteralPath $pending
[IO.Directory]::Delete([IO.Path]::GetDirectoryName($pending),$false)
$oldManager=$env:SCDEModManagerLaunchId
try{$env:SCDEModManagerLaunchId='fixture';Reject {& $remove -GameDir $game -PackageRoot $stage -Apply} 'external manager preserved'}finally{$env:SCDEModManagerLaunchId=$oldManager}
$other=Join-Path $game 'BepInEx/patchers/Foreign.dll';Write-TestFile $other 'foreign'
& $remove -GameDir $game -PackageRoot $stage -WhatIf -Apply
Check (Test-Path -LiteralPath $target) 'WhatIf preserves bootstrap'
& $remove -GameDir $game -PackageRoot $stage -Apply
Check (-not (Test-Path -LiteralPath $target) -and [IO.File]::ReadAllText($other) -ceq 'foreign') 'exact removal preserves foreign file'
& $remove -GameDir $game -PackageRoot $stage -Apply
# An actual junction tests both validators; every path stays below this fixture.
$linked=Join-Path $fixture 'linked';[IO.Directory]::CreateDirectory((Join-Path $linked 'BepInEx'))|Out-Null
$null=New-Item -ItemType Junction -Path (Join-Path $linked 'BepInEx/patchers') -Target (Join-Path $stage 'BepInEx/patchers')
Reject {& $remove -GameDir $linked -PackageRoot $stage -Apply} 'linked removal target preserved'
if($PackageRoot){Assert-ApiBootstrapPair $PackageRoot;Check $true 'built package pair verified'}
Write-Host "Bootstrap package/removal fixtures retained: $fixture"
