# Offline package helpers. Runtime pair validation lives in bootstrap/src/Package/PairContract.cs.
function Assert-ApiBootstrapZipPair($Zip) {
    function Find-PairEntry([string]$Name){
        $matches=@($Zip.Entries|Where-Object { $_.FullName.Replace('\','/') -ceq $Name })
        if($matches.Count -gt 1){throw 'Duplicate pair entry.'}
        if($matches.Count -eq 1){return $matches[0]}
    }
    $bootstrap=Find-PairEntry 'BepInEx/patchers/APIShared.DependencyBootstrap.dll'
    $manifest=Find-PairEntry 'BepInEx/plugins/APIShared_Serp/dependency-bootstrap-pair.json'
    if(-not $bootstrap -and -not $manifest){return} # Historical packages without this feature remain valid.
    if(-not $bootstrap -or -not $manifest){throw 'Incomplete bootstrap pair in ZIP.'}
    $reader=[IO.StreamReader]::new($manifest.Open())
    try{$pair=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
    if((@($pair.PSObject.Properties.Name|Sort-Object)-join ',') -cne 'BootstrapSHA256,Guid,RuntimeSHA256,Schema,Version' -or ($pair.Schema -isnot [int] -and $pair.Schema -isnot [long]) -or $pair.Schema -ne 1 -or $pair.Guid -cne 'APIShared_Serp'){throw 'Invalid ZIP bootstrap pair.'}
    $version=$null
    if(-not [version]::TryParse([string]$pair.Version,[ref]$version)){throw 'Invalid ZIP pair version.'}
    $info=Find-PairEntry 'BepInEx/plugins/APIShared_Serp/info.json'
    if(-not $info){throw 'Missing ZIP plugin manifest.'}
    $reader=[IO.StreamReader]::new($info.Open())
    try{$metadata=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
    if($metadata.GUID -cne 'APIShared_Serp' -or $metadata.Version -cne $pair.Version){throw 'ZIP pair/plugin metadata mismatch.'}
    foreach($row in @(@('BepInEx/plugins/APIShared_Serp/APIShared.dll',$pair.RuntimeSHA256),@('BepInEx/patchers/APIShared.DependencyBootstrap.dll',$pair.BootstrapSHA256))){
        $entry=Find-PairEntry $row[0];if(-not $entry -or $row[1] -cnotmatch '^[a-f0-9]{64}$'){throw 'Invalid ZIP pair entry.'}
        $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
        try{$actual=[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$stream.Dispose();$sha.Dispose()}
        if($actual -cne $row[1]){throw 'ZIP pair digest mismatch.'}
    }
}
function Get-ApiBootstrapRecords([string]$Root) {
    $relative='BepInEx/patchers/APIShared.DependencyBootstrap.dll'
    $path=Join-Path $Root $relative
    Assert-ApiWorkshopUnlinkedPath $path
    if(-not [IO.File]::Exists($path)){throw 'Bootstrap output is missing.'}
    return @([pscustomobject]@{Path=$relative;Sha256=Get-WorkshopFileHash $path})
}
function New-ApiBootstrapPair([string]$Root) {
    $plugin=Join-Path $Root 'BepInEx/plugins/APIShared_Serp'
    $version=(Get-Content -Raw -LiteralPath (Join-Path $plugin 'info.json')|ConvertFrom-Json).Version
    $bootstrap=@(Get-ApiBootstrapRecords $Root)
    $record=[ordered]@{Schema=1;Guid='APIShared_Serp';Version=$version;RuntimeSHA256=Get-WorkshopFileHash (Join-Path $plugin 'APIShared.dll');BootstrapSHA256=$bootstrap[0].Sha256}
    [IO.File]::WriteAllText((Join-Path $plugin 'dependency-bootstrap-pair.json'),(($record|ConvertTo-Json) -replace "`r?`n","`r`n")+"`r`n",[Text.UTF8Encoding]::new($false))
    Assert-ApiBootstrapPair $Root
}
function Assert-ApiBootstrapPair([string]$Root) {
    $plugin=Join-Path $Root 'BepInEx/plugins/APIShared_Serp'
    $path=Join-Path $plugin 'dependency-bootstrap-pair.json'
    Assert-ApiWorkshopUnlinkedPath $path
    $pair=Get-Content -Raw -LiteralPath $path|ConvertFrom-Json
    if((@($pair.PSObject.Properties.Name|Sort-Object)-join ',') -cne 'BootstrapSHA256,Guid,RuntimeSHA256,Schema,Version' -or ($pair.Schema -isnot [int] -and $pair.Schema -isnot [long]) -or $pair.Schema -ne 1 -or $pair.Guid -cne 'APIShared_Serp'){throw 'Invalid bootstrap pair schema.'}
    $parsed=$null
    if(-not [version]::TryParse([string]$pair.Version,[ref]$parsed)){throw 'Invalid pair version.'}
    foreach($hash in @($pair.RuntimeSHA256,$pair.BootstrapSHA256)){if($hash -isnot [string] -or $hash -cnotmatch '^[a-f0-9]{64}$'){throw 'Invalid pair digest.'}}
    $bootstrap=@(Get-ApiBootstrapRecords $Root)
    $runtime=Join-Path $plugin 'APIShared.dll'
    Assert-ApiWorkshopUnlinkedPath $runtime
    if((Get-WorkshopFileHash $runtime) -cne $pair.RuntimeSHA256 -or $bootstrap[0].Sha256 -cne $pair.BootstrapSHA256){throw 'Incomplete or mixed bootstrap pair.'}
    if((Get-Content -Raw -LiteralPath (Join-Path $plugin 'info.json')|ConvertFrom-Json).Version -cne $pair.Version){throw 'Pair/manifest version mismatch.'}
}
function Copy-ApiBootstrapPayload([string]$Root,[string]$Stage) {
    Assert-ApiBootstrapPair $Root
    $target=Join-Path $Stage 'BepInEx/patchers/APIShared.DependencyBootstrap.dll'
    Assert-ApiWorkshopUnlinkedPath $target
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
    Copy-Item -LiteralPath (Join-Path $Root 'BepInEx/patchers/APIShared.DependencyBootstrap.dll') -Destination $target -Force
}
