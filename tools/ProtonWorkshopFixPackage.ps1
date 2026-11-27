# Separate manual asset. This helper never adds its DLL to the normal APIShared package.
. (Join-Path $PSScriptRoot 'BuildProof.ps1')
function Get-ProtonFixInputState([string]$Root,[string]$GameDir,[string]$MSBuild) {
    $Root=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $files=@(Get-ChildItem -LiteralPath (Join-Path $Root 'proton-fix') -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs','.csproj','.sh','.bat','.txt','.example')
    })
    $files+=@(Get-ChildItem -LiteralPath (Join-Path $Root 'src/Workshop/Proton') -File -Filter '*.cs')
    $files+=@(Get-ChildItem -LiteralPath (Join-Path $Root 'tests/ProtonWorkshopFix.Tests') -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs','.csproj','.config')
    })
    foreach($relative in @('tools/Build-ProtonWorkshopFix.ps1','tools/ProtonWorkshopFixPackage.ps1','tools/BuildProof.ps1',
        'tools/Validation/test-proton-extender-updater.sh','tools/Validation/Test-ProtonWorkshopFixPackage.ps1')) {
        $files+=Get-Item -LiteralPath (Join-Path $Root $relative)
    }
    $files+=@(Get-ChildItem -LiteralPath $Root -File | Where-Object Name -Match '^Directory\..*\.(props|targets)$')
    $rows=[Collections.Generic.List[string]]::new()
    foreach($file in $files | Sort-Object FullName -Unique) {
        $rows.Add($file.FullName.Substring($Root.Length+1).Replace('\','/')+':'+(Get-ApiFileHash $file.FullName))
    }
    [xml]$project=[IO.File]::ReadAllText((Join-Path $Root 'proton-fix/ProtonWorkshopFix.csproj'))
    foreach($node in $project.SelectNodes('//*[local-name()="HintPath"]')) {
        $reference=$node.InnerText.Replace('$(GameDir)',$GameDir)
        if($reference.Contains('$(')){throw 'Unresolved Proton fix reference.'}
        $rows.Add('reference/'+$node.ParentNode.Include+':'+(Get-ApiFileHash $reference))
    }
    # The actual installed Extender is used by the dry-transform contract tests, never shipped.
    $rows.Add('test-reference/SHCDESE:'+(Get-ApiFileHash (Join-Path $GameDir 'BepInEx/plugins/000shcdese/SHCDESE.dll')))
    $rows.Add('compiler:'+(Get-ApiFileHash $MSBuild))
    $ordered=$rows.ToArray();[Array]::Sort($ordered,[StringComparer]::Ordinal)
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$fingerprint=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($ordered -join "`n"))))).Replace('-','').ToLowerInvariant()}
    finally{$sha.Dispose()}
    return [pscustomobject]@{Fingerprint=$fingerprint;Inputs=$ordered}
}
function Assert-ProtonFixAssembly([string]$Root,[string]$AssemblyPath,[string]$GameDir) {
    if(-not ('Mono.Cecil.AssemblyDefinition' -as [type])) {
        [void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GameDir 'BepInEx/core/Mono.Cecil.dll')))
    }
    $module=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($AssemblyPath)
    try {
        if($module.Name.Name -cne 'APIShared.ProtonWorkshopFix'){throw 'Wrong standalone patcher identity.'}
        $allowed=@('mscorlib','System','System.Core','BepInEx','Mono.Cecil','MonoMod.Utils','MonoMod.RuntimeDetour')
        foreach($reference in $module.MainModule.AssemblyReferences) {
            if($reference.Name -cnotin $allowed){throw "Unexpected standalone patcher dependency: $($reference.Name)"}
        }
        $entry=@($module.MainModule.Types | Where-Object { $_.IsPublic -and $_.Name -eq 'ProtonWorkshopPatcher' })
        if($entry.Count -ne 1){throw 'Standalone patcher entry point is missing.'}
        $resources=@($module.MainModule.Resources | Where-Object Name -CEQ 'APIShared.ProtonWorkshopFix.extender-updater.sh')
        if($resources.Count -ne 1){throw 'Embedded Proton updater is missing.'}
        $stream=$resources[0].GetResourceStream();$sha=[Security.Cryptography.SHA256]::Create()
        try{$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}
        finally{$stream.Dispose();$sha.Dispose()}
        if($hash -cne (Get-ApiFileHash (Join-Path $Root 'proton-fix/Assets/extender-updater.sh'))){throw 'Embedded updater differs from its attested source.'}
    } finally {$module.Dispose()}
}
function Assert-ProtonFixBuild([string]$Root) {
    $path=Join-Path $Root '.local/ProtonWorkshopFix/build-proof.json'
    if(-not [IO.File]::Exists($path)){throw 'Build the standalone Proton Workshop fix first.'}
    $proof=[IO.File]::ReadAllText($path)|ConvertFrom-Json
    if($proof.SchemaVersion -ne 1 -or $proof.TestMode -cne 'full'){throw 'Standalone Proton fix requires full build evidence.'}
    $actual=Get-ProtonFixInputState $Root $proof.GameDir $proof.MSBuild
    if($actual.Fingerprint -cne $proof.Fingerprint){throw 'Standalone Proton fix build inputs changed; rebuild it.'}
    $dll=Join-Path $Root '.local/ProtonWorkshopFix/bin/APIShared.ProtonWorkshopFix.dll'
    if((Get-ApiFileHash $dll) -cne $proof.AssemblySHA256){throw 'Standalone Proton fix DLL changed after testing.'}
    Assert-ProtonFixAssembly $Root $dll $proof.GameDir
    return $proof
}
function Assert-ProtonFixZip([string]$Path,[hashtable]$Expected) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($entry in $zip.Entries) {
            if($entry.FullName.EndsWith('/')){
                $prefix=$entry.FullName
                if(-not @($Expected.Keys | Where-Object { $_.StartsWith($prefix,[StringComparison]::Ordinal) }).Count){throw 'Unexpected Proton fix ZIP directory.'}
                continue
            }
            if($entry.FullName -cnotin $Expected.Keys -or -not $seen.Add($entry.FullName)) {throw "Unexpected/duplicate Proton fix ZIP entry: $($entry.FullName)"}
            $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
            try{$hash=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}
            finally{$stream.Dispose();$sha.Dispose()}
            if($hash -cne $Expected[$entry.FullName]){throw "Proton fix ZIP hash mismatch: $($entry.FullName)"}
        }
        if($seen.Count -ne $Expected.Count){throw 'Incomplete standalone Proton fix ZIP.'}
    } finally {$zip.Dispose()}
}
function New-ProtonFixAsset([string]$Root,[string]$OutputDirectory,[string]$Version) {
    if($Version -cnotmatch '^\d+\.\d+\.\d+(\.\d+)?$'){throw 'Invalid Proton fix asset version.'}
    $proof=Assert-ProtonFixBuild $Root
    [void][IO.Directory]::CreateDirectory($OutputDirectory)
    $stage=Join-Path $Root ('.local/ProtonWorkshopFix/p-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
    $payload=[ordered]@{
        'BepInEx/patchers/APIShared.ProtonWorkshopFix.dll'=(Join-Path $Root '.local/ProtonWorkshopFix/bin/APIShared.ProtonWorkshopFix.dll')
        'INSTALL-linux.txt'=(Join-Path $Root 'proton-fix/INSTALL-linux.txt')
        'APIShared.ProtonPaths.cfg.example'=(Join-Path $Root 'proton-fix/APIShared.ProtonPaths.cfg.example')
    }
    $expected=@{}
    foreach($relative in $payload.Keys) {
        $destination=Join-Path $stage $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        [IO.File]::Copy($payload[$relative],$destination,$false)
        $expected[$relative]=Get-ApiFileHash $destination
    }
    $asset=Join-Path $OutputDirectory "APIShared-ProtonWorkshopFix-v$Version.zip"
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if(-not [IO.File]::Exists($asset)){
        # Framework's CreateFromDirectory can emit Windows backslashes. Explicit
        # portable ZIP names are necessary for the manual Linux extraction path.
        $archive=[IO.Compression.ZipFile]::Open($asset,[IO.Compression.ZipArchiveMode]::Create)
        try{foreach($relative in $payload.Keys){[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $stage $relative),$relative,[IO.Compression.CompressionLevel]::Optimal)}}
        finally{$archive.Dispose()}
    }
    Assert-ProtonFixZip $asset $expected
    $hash=Get-ApiFileHash $asset;$utf8=[Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText("$asset.sha256","$hash  $([IO.Path]::GetFileName($asset))`r`n",$utf8)
    $provenance=[ordered]@{SchemaVersion=1;Component='APIShared.ProtonWorkshopFix';Version=$Version;Commit=$proof.Commit;BuildFingerprint=$proof.Fingerprint;Sha256=$hash;Files=$expected;PlatformAcceptance='Automated evidence only; real Proton/Steam acceptance is recorded separately.'}
    $provenancePath=Join-Path $OutputDirectory "APIShared-ProtonWorkshopFix-v$Version.provenance.json"
    [IO.File]::WriteAllText($provenancePath,(($provenance|ConvertTo-Json -Depth 5).Replace("`r`n","`n").Replace("`n","`r`n")+"`r`n"),$utf8)
    return [pscustomobject]@{Path=$asset;Sha256=$hash;Provenance=$provenancePath;Assets=@($asset,"$asset.sha256",$provenancePath)}
}
