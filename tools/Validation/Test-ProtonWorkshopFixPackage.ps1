[CmdletBinding()]
param([string]$Root)
$ErrorActionPreference='Stop'
if(-not $Root){$Root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))}
. (Join-Path $Root 'tools/ProtonWorkshopFixPackage.ps1')
$proof=Assert-ProtonFixBuild $Root
$fixture=Join-Path $Root ('.local/ProtonWorkshopFix/test-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
[void][IO.Directory]::CreateDirectory($fixture)
$version=[string]([IO.File]::ReadAllText((Join-Path $Root 'info.json'))|ConvertFrom-Json).Version
$asset=New-ProtonFixAsset $Root $fixture $version
$expected=@{
    'BepInEx/patchers/APIShared.ProtonWorkshopFix.dll'=$proof.AssemblySHA256
    'INSTALL-linux.txt'=(Get-ApiFileHash (Join-Path $Root 'proton-fix/INSTALL-linux.txt'))
    'APIShared.ProtonPaths.cfg.example'=(Get-ApiFileHash (Join-Path $Root 'proton-fix/APIShared.ProtonPaths.cfg.example'))
}
Assert-ProtonFixZip $asset.Path $expected
function Require-Rejection([scriptblock]$Action,[string]$Reason) {
    $rejected=$false
    try{& $Action}catch{$rejected=$true}
    if(-not $rejected){throw "Proton package test did not reject $Reason"}
}
$extra=Join-Path $fixture 'extra.zip';[IO.File]::Copy($asset.Path,$extra)
$zip=[IO.Compression.ZipFile]::Open($extra,[IO.Compression.ZipArchiveMode]::Update)
try{$entry=$zip.CreateEntry('BepInEx/plugins/APIShared_Serp/APIShared.dll');$stream=$entry.Open();try{$stream.WriteByte(1)}finally{$stream.Dispose()}}
finally{$zip.Dispose()}
Require-Rejection {Assert-ProtonFixZip $extra $expected} 'an embedded APIShared runtime'
$missing=Join-Path $fixture 'missing.zip';[IO.File]::Copy($asset.Path,$missing)
$zip=[IO.Compression.ZipFile]::Open($missing,[IO.Compression.ZipArchiveMode]::Update)
try{$zip.GetEntry('BepInEx/patchers/APIShared.ProtonWorkshopFix.dll').Delete()}finally{$zip.Dispose()}
Require-Rejection {Assert-ProtonFixZip $missing $expected} 'a missing patcher'
$changed=@{};foreach($key in $expected.Keys){$changed[$key]=$expected[$key]};$changed['INSTALL-linux.txt']='0'*64
Require-Rejection {Assert-ProtonFixZip $asset.Path $changed} 'changed payload bytes'
$unsafe=Join-Path $fixture 'unsafe-directory.zip';[IO.File]::Copy($asset.Path,$unsafe)
$zip=[IO.Compression.ZipFile]::Open($unsafe,[IO.Compression.ZipArchiveMode]::Update)
try{[void]$zip.CreateEntry('../outside/')}finally{$zip.Dispose()}
Require-Rejection {Assert-ProtonFixZip $unsafe $expected} 'an unexpected/escaping directory'
# Attestation must cover the real shared hashing/environment helper as well as
# the assembly. Mutate only this retained owned fixture, never working sources.
$proofRoot=Join-Path $fixture 'proof'
foreach($relative in @('proton-fix','src/Workshop/Proton','tests/ProtonWorkshopFix.Tests')) {
    $source=Join-Path $Root $relative
    foreach($file in Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }) {
        $target=Join-Path (Join-Path $proofRoot $relative) $file.FullName.Substring($source.Length+1)
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target));[IO.File]::Copy($file.FullName,$target)
    }
}
foreach($relative in @('tools/Build-ProtonWorkshopFix.ps1','tools/ProtonWorkshopFixPackage.ps1','tools/BuildProof.ps1',
    'tools/Validation/test-proton-extender-updater.sh','tools/Validation/Test-ProtonWorkshopFixPackage.ps1')) {
    $target=Join-Path $proofRoot $relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target));[IO.File]::Copy((Join-Path $Root $relative),$target)
}
foreach($file in Get-ChildItem -LiteralPath $Root -File | Where-Object Name -Match '^Directory\..*\.(props|targets)$') {
    [IO.File]::Copy($file.FullName,(Join-Path $proofRoot $file.Name))
}
$isolated=Get-ProtonFixInputState $proofRoot $proof.GameDir $proof.MSBuild
$copyProof=[pscustomobject]@{SchemaVersion=1;TestMode='full';Commit=$proof.Commit;GameDir=$proof.GameDir;MSBuild=$proof.MSBuild;Fingerprint=$isolated.Fingerprint;Inputs=$isolated.Inputs;AssemblySHA256=$proof.AssemblySHA256}
$copyDll=Join-Path $proofRoot '.local/ProtonWorkshopFix/bin/APIShared.ProtonWorkshopFix.dll'
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copyDll))
[IO.File]::Copy((Join-Path $Root '.local/ProtonWorkshopFix/bin/APIShared.ProtonWorkshopFix.dll'),$copyDll)
[IO.File]::WriteAllText((Join-Path $proofRoot '.local/ProtonWorkshopFix/build-proof.json'),($copyProof|ConvertTo-Json -Depth 5))
$null=Assert-ProtonFixBuild $proofRoot
$sharedHelper=Join-Path $proofRoot 'tools/BuildProof.ps1'
$original=[IO.File]::ReadAllBytes($sharedHelper)
[IO.File]::AppendAllText($sharedHelper,"`r`n# owned source-change fixture`r`n")
Require-Rejection {Assert-ProtonFixBuild $proofRoot} 'changed shared build-proof inputs'
[IO.File]::WriteAllBytes($sharedHelper,$original)
$stream=[IO.File]::Open($copyDll,[IO.FileMode]::Append,[IO.FileAccess]::Write)
try{$stream.WriteByte(0)}finally{$stream.Dispose()}
Require-Rejection {Assert-ProtonFixBuild $proofRoot} 'changed compiled patcher bytes'
Write-Host ('PASS: separate Proton asset, dependency/resource identity, extra/missing/changed payloads, directories and stale source/assembly evidence rejected. Evidence: '+$fixture)
