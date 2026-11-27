param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$baseline='d2102cad962e241018aab834fb83230470e23ef5'
$manifest=[IO.File]::ReadAllText((Join-Path $root 'info.json')) | ConvertFrom-Json
if($manifest.Version -ne '0.9.2' -or $manifest.SerpChangelog[0].Version -ne '0.9.2' -or $manifest.SerpChangelog[1].Version -ne '0.9.1' -or $manifest.SerpChangelog[2].Version -ne '0.9.0'){throw 'Hotfix version/changelog must distinguish 0.9.2 from the released 0.9.1 and 0.9.0 history.'}
$files=@(& git -C $root ls-tree -r --name-only $baseline -- src)
if($LASTEXITCODE){throw 'The published 0.9.0 baseline must be available for the hotfix scope check.'}
foreach($relative in $files){
    if($relative.StartsWith('src/Workshop/')){continue}
    $expected=(& git -C $root show ($baseline+':'+$relative)) -join "`n"
    if($LASTEXITCODE){throw ('Cannot read baseline: '+$relative)}
    $actual=[IO.File]::ReadAllText((Join-Path $root $relative)).Replace("`r`n","`n").TrimEnd([char]10)
    if($relative -eq 'src/Core/APISharedPlugin.cs'){$actual=$actual.Replace('PluginVersion = "0.9.2"','PluginVersion = "0.9.0"')}
    if($actual -cne $expected.TrimEnd([char]10)){throw ('Non-Workshop runtime changed against the published release: '+$relative)}
}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File -Filter '*.cs'){
    $relative=$file.FullName.Substring($root.Length+1).Replace('\','/')
    if($relative -notin $files -and -not $relative.StartsWith('src/Workshop/')){throw ('Unrelated new runtime source: '+$relative)}
}
Write-Host 'PASS: runtime differs from published 0.9.0 only in Workshop and the release version.'
