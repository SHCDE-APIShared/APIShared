# Local build evidence only; no game or mod-workspace dependency is introduced.
function Get-ApiFileHash([string]$Path) {
    $stream=[IO.File]::OpenRead($Path); $sha=[Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}
function Get-ApiInputState([string]$Root, [string]$GameDir, [string]$ExtenderDir, [string]$MSBuild) {
    $Root=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $rows = [Collections.Generic.List[string]]::new()
    $files = @(Get-ChildItem -LiteralPath (Join-Path $Root 'src'),(Join-Path $Root 'Properties'),(Join-Path $Root 'Patches') -Recurse -File |
        Where-Object { $_.Extension -in @('.cs','.xaml') })
    $files += @(Get-ChildItem -LiteralPath $Root -File | Where-Object { $_.Name -match '^Directory\..*\.(props|targets)$|^APIShared\.csproj$|^info\.json$' })
    $files += @(Get-Item -LiteralPath (Join-Path $Root 'tools/Build.ps1'),(Join-Path $Root 'tools/BuildProof.ps1'))
    foreach ($file in $files | Sort-Object FullName -Unique) {
        $relative = $file.FullName.Substring($Root.Length + 1).Replace('\','/')
        $rows.Add($relative + ':' + (Get-ApiFileHash $file.FullName))
    }
    [xml]$project = [IO.File]::ReadAllText((Join-Path $Root 'APIShared.csproj'))
    foreach ($node in $project.SelectNodes('//*[local-name()="HintPath"]')) {
        $path = $node.InnerText.Replace('$(GameDir)',$GameDir).Replace('$(ExtenderDir)',$ExtenderDir)
        if ($path.Contains('$(')) { throw "Unresolved build reference: $path" }
        if (-not [IO.Path]::IsPathRooted($path)) { $path = Join-Path $Root $path }
        $name = $node.ParentNode.Include
        $rows.Add('reference/' + $name + ':' + (Get-ApiFileHash $path))
    }
    $rows.Add('MSBuild:' + (Get-ApiFileHash $MSBuild))
    # Ordinal order is stable across Windows PowerShell 5 and PowerShell 7 cultures.
    $rows.Sort([StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes($rows -join "`n")
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
    return [pscustomobject]@{ Fingerprint=$fingerprint; Inputs=$rows.ToArray() }
}
function Get-ApiPackageFiles([string]$Root) {
    $package = [IO.Path]::GetFullPath((Join-Path $Root 'BepInEx/plugins/APIShared_Serp'))
    return @(Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ Path=$_.FullName.Substring($package.Length+1).Replace('\','/'); Sha256=(Get-ApiFileHash $_.FullName) }
    })
}
function Write-ApiBuildProof([string]$Root, [string]$GameDir, [string]$ExtenderDir, [string]$MSBuild, $InitialState) {
    $final = Get-ApiInputState $Root $GameDir $ExtenderDir $MSBuild
    if ($InitialState.Fingerprint -ne $final.Fingerprint) { throw 'APIShared inputs changed during the build. Build again.' }
    $commit = (& git -C $Root rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { $commit = $null }
    $proof = [ordered]@{ SchemaVersion=1; Commit=$commit; CreatedUtc=[DateTime]::UtcNow.ToString('o'); GameDir=$GameDir; ExtenderDir=$ExtenderDir; MSBuild=$MSBuild; Fingerprint=$final.Fingerprint; Inputs=$final.Inputs; Files=(Get-ApiPackageFiles $Root) }
    $directory = Join-Path $Root '.local'
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $directory 'build-proof.json'),(($proof | ConvertTo-Json -Depth 6).Replace("`r`n","`n").Replace("`n","`r`n") + "`r`n"),[Text.UTF8Encoding]::new($false))
}
function Assert-ApiBuildProof([string]$Root, [switch]$Release) {
    $path = Join-Path $Root '.local/build-proof.json'
    if (-not (Test-Path -LiteralPath $path)) { throw 'APIShared build evidence is missing. Run APIShared/build.bat /nopause first.' }
    $proof = [IO.File]::ReadAllText($path) | ConvertFrom-Json
    if ($proof.SchemaVersion -ne 1) { throw 'Unsupported APIShared build evidence. Build APIShared again.' }
    $state = Get-ApiInputState $Root $proof.GameDir $proof.ExtenderDir $proof.MSBuild
    if ($state.Fingerprint -ne $proof.Fingerprint) { throw 'APIShared package is stale. Run APIShared/build.bat /nopause first.' }
    $actual = @(Get-ApiPackageFiles $Root)
    $expected = @($proof.Files)
    if ($actual.Count -ne $expected.Count) { throw 'APIShared package contents changed after the build. Build APIShared again.' }
    foreach ($file in $expected) {
        $match = @($actual | Where-Object { $_.Path -ceq $file.Path -and $_.Sha256 -ceq $file.Sha256 })
        if ($match.Count -ne 1) { throw "APIShared package hash mismatch: $($file.Path). Build APIShared again." }
    }
    if ($Release) {
        $commit = (& git -C $Root rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or $commit -ne $proof.Commit) { throw 'APIShared release evidence belongs to another commit. Rebuild the committed source.' }
        $status = @(& git -C $Root status --porcelain --untracked-files=normal)
        if ($LASTEXITCODE -ne 0 -or $status.Count) { throw 'APIShared release requires a clean checkout.' }
    }
    return $proof
}
