[CmdletBinding(SupportsShouldProcess)]
param([switch]$Publish, [string]$WorkshopPackagerPath, [string]$PreviewPath, [switch]$TestRelease, [switch]$PrepareOnly)
$ErrorActionPreference = 'Stop'
if ($PrepareOnly -and $Publish) { throw 'PrepareOnly cannot publish a release.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$repository = 'SHCDE-APIShared/APIShared'
. (Join-Path $PSScriptRoot 'GitHubRelease.ps1')
$identity = (& gh api user --jq .login).Trim()
if ($LASTEXITCODE -ne 0 -or -not $identity) { throw 'Sign in with gh auth login first.' }
$top = (& git -C $root rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($top) -ne $root) {
    throw 'Publish from the APIShared Git checkout (standalone or submodule).'
}
$status = @(& git -C $root status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the repository Git state.' }
if ($status.Count -ne 0) { throw 'Commit the reviewed source changes before preparing a release.' }
$manifest = [IO.File]::ReadAllText((Join-Path $root 'info.json')) | ConvertFrom-Json
$version = [string]$manifest.Version
$parsed = $null
if (-not [version]::TryParse($version, [ref]$parsed)) { throw 'Invalid APIShared version.' }
$parsed = [version]::new($parsed.Major, $parsed.Minor, [Math]::Max(0, $parsed.Build), [Math]::Max(0, $parsed.Revision))
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not determine the reviewed source commit.' }
& gh api "repos/$repository/git/commits/$commit" --silent
if ($LASTEXITCODE -ne 0) { throw 'Push the reviewed source commit to the APIShared repository before preparing its release.' }
$tag = "APIShared/v$version"
if ($TestRelease) { if ($Publish) { throw 'Test releases must remain drafts.' }; $tag = "APIShared/test/v$version-$($commit.Substring(0,12))" }
& (Join-Path $root 'build.bat') /nopause /noinstall
if ($LASTEXITCODE -ne 0) { throw 'The release build or regression tests failed.' }
. (Join-Path $root 'tools/BuildProof.ps1')
$null = Assert-ApiBuildProof $root -Release
$package = Join-Path $root 'BepInEx\plugins\APIShared_Serp'
$dll = Join-Path $package 'APIShared.dll'
if ([Reflection.AssemblyName]::GetAssemblyName($dll).Version -ne $parsed) { throw 'Assembly and manifest versions differ.' }
$forbidden = @(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object {
    ($_.Extension -in @('.dll', '.exe') -and $_.Name -ne 'APIShared.dll') -or $_.Name -match '(?i)credential|token|secret'
})
if ($forbidden.Count -ne 0) { throw 'The package contains unexpected runtime dependencies or sensitive files.' }
$output = Join-Path $root ('.local\release\v' + $version)
if ($TestRelease) { $output = Join-Path $root ('.local\release\test\' + $commit) }
[IO.Directory]::CreateDirectory($output) | Out-Null
$staging = Join-Path $output ('APIShared-v' + $version + '-' + [Guid]::NewGuid().ToString('N'))
$pluginTarget = Join-Path $staging 'BepInEx\plugins\APIShared_Serp'
[IO.Directory]::CreateDirectory($pluginTarget) | Out-Null
Copy-Item -Path (Join-Path $package '*') -Destination $pluginTarget -Recurse
$asset = Join-Path $output "APIShared-v$version.zip"
 . (Join-Path $root 'tools/WorkshopPackage.ps1')
Assert-ApiWorkshopInventory -Directory $pluginTarget
. (Join-Path $root 'tools/PublisherPackage.ps1')
$publisherSource=Join-Path $root 'BepInEx/tools/APISharedWorkshopPublisher'
Assert-ApiPublisherPackage $publisherSource (Join-Path $root '.local/WorkshopUploader')
$publisherTarget=Join-Path $staging 'BepInEx/tools/APISharedWorkshopPublisher'
[void][IO.Directory]::CreateDirectory($publisherTarget)
Copy-Item -Path (Join-Path $publisherSource '*') -Destination $publisherTarget -Recurse
Assert-ApiPublisherPackage $publisherTarget $publisherSource
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path -LiteralPath $asset) { Assert-ApiWorkshopMap -MapPath $asset -Stage $staging }
else { [IO.Compression.ZipFile]::CreateFromDirectory($staging, $asset) }
$hash = (Get-ApiFileHash $asset)
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText("$asset.sha256", "$hash  $([IO.Path]::GetFileName($asset))`r`n", $utf8)
$assets = @($asset, "$asset.sha256")
$fileRecords = @(Get-ChildItem -LiteralPath $staging -Recurse -File | ForEach-Object {
    [ordered]@{ Path = $_.FullName.Substring($staging.Length).TrimStart('\').Replace('\','/'); Sha256 = (Get-ApiFileHash $_.FullName); Size = $_.Length }
})
$provenance = [ordered]@{ SchemaVersion = 1; Mod = 'APIShared'; PluginGuid = 'APIShared_Serp'; Version = $version; Tag = $tag; Commit = $commit; Package = [ordered]@{ Sha256 = $hash; Files = $fileRecords } }
if ($WorkshopPackagerPath) {
    if (-not (Test-Path -LiteralPath $WorkshopPackagerPath -PathType Leaf) -or -not (Test-Path -LiteralPath $PreviewPath -PathType Leaf)) { throw 'Workshop packager and preview are required.' }
    . (Join-Path $root 'tools/WorkshopPackage.ps1')
    Copy-Item -LiteralPath (Join-Path $root 'info.json') -Destination $staging
    Copy-Item -LiteralPath $PreviewPath -Destination (Join-Path $staging 'preview.png')
    $map = Join-Path $output 'APIShared.map'
    if (-not (Test-Path -LiteralPath $map)) {
        & $WorkshopPackagerPath -s $staging -o $map
        if ($LASTEXITCODE -ne 0) { throw 'APIShared Workshop packaging failed.' }
    }
    Set-ApiBootstrapMapPayload -MapPath $map -Stage $staging
    Assert-ApiWorkshopMap -MapPath $map -Stage $staging
    . (Join-Path $root 'tools/WorkshopUploader/PublisherBuildProof.ps1')
    Assert-WorkshopPublisherBuild $root
    $requestPath=Join-Path $output 'publisher-validation.json';$resultPath=Join-Path $output 'publisher-result.json'
    $request=[ordered]@{SchemaVersion=1;Mode='Production';PackageId='APIShared_Serp';Version=$version;SourceDirectory=$output;StateRoot=(Join-Path $output 'publisher-validation');PreviewFile=$PreviewPath;Title='APIShared';Description='Release validation';ChangeNote='';Visibility='Private';WorkshopId='';PreservePresentation=$false;ForbiddenItemIds=@()}
    [IO.File]::WriteAllText($requestPath,($request|ConvertTo-Json -Depth 5),$utf8)
    & (Join-Path $root '.local/WorkshopUploader/WorkshopUploader.exe') --validate $requestPath $resultPath
    if($LASTEXITCODE -ne 0){throw 'APIShared Workshop map failed complete publisher validation before release.'}
    $mapHash = (Get-ApiFileHash $map)
    [IO.File]::WriteAllText("$map.sha256", "$mapHash  APIShared.map`r`n", $utf8)
    $provenance.Map = [ordered]@{ File = 'APIShared.map'; Sha256 = $mapHash; Size = (Get-Item -LiteralPath $map).Length }
    $assets += @($map, "$map.sha256")
}
$provenancePath = Join-Path $output "APIShared-v$version.provenance.json"
[IO.File]::WriteAllText($provenancePath, (($provenance | ConvertTo-Json -Depth 10) -replace "`r?`n", "`r`n") + "`r`n", $utf8)
$assets += $provenancePath
$notes = Join-Path $staging 'release-notes.md'
$text = "APIShared $version`r`n`r`nSource commit: $commit`r`nZIP SHA-256: $hash`r`n`r`nRequires Script Extender $($manifest.MinimumScriptExtenderVersion) or newer. Install one central copy; consumer mods must not bundle APIShared.`r`n"
[IO.File]::WriteAllText($notes, $text, [Text.UTF8Encoding]::new($false))
$arguments = @('release', 'create', $tag) + $assets + @('--repo', $repository, '--target', $commit, '--title', "APIShared $version", '--notes-file', $notes)
if (-not $Publish) { $arguments += '--draft' }
if ($PrepareOnly) {
    Write-Host ('Validated local release assets: ' + $output + '; source=' + $commit + '; no GitHub release or tag created.')
    return
}
if ($PSCmdlet.ShouldProcess("$repository / $tag", 'Create release with the validated APIShared asset')) {
    $existing = Get-ApiGitHubRelease $repository $tag
    if ($null -ne $existing) {
        if (-not $existing.isDraft -or $existing.targetCommitish -ne $commit) { throw 'An existing published or different-source release must never be replaced.' }
        & gh release upload $tag @assets --repo $repository --clobber
        if ($LASTEXITCODE -ne 0) { throw 'Could not resume the matching draft assets.' }
        & gh release edit $tag --repo $repository --notes-file $notes --draft=$(-not $Publish)
    } else { & gh @arguments }
    if ($LASTEXITCODE -ne 0) { throw 'GitHub release creation failed; the validated local artifact was preserved.' }
}
