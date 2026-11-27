function Get-ApiPreviousReleaseVersion {
    param($Releases, [string]$CurrentVersion)
    $current = [version]$CurrentVersion
    $versions = @(foreach ($release in @($Releases)) {
        if (-not $release.isDraft -and -not $release.isPrerelease -and
            [string]$release.tagName -cmatch '^APIShared/v(\d+\.\d+\.\d+)$') {
            $version = [version]$Matches[1]
            if ($version -lt $current) { $version }
        }
    })
    return [string]($versions | Sort-Object -Descending | Select-Object -First 1)
}

function Get-ApiReleaseChangeNotes {
    param($Manifest, [AllowEmptyString()][string]$PreviousVersion)
    $current = [version]$Manifest.Version
    $previous = if ($PreviousVersion) { [version]$PreviousVersion } else { $null }
    if ($previous -and $previous -ge $current) { throw 'Previous APIShared release must precede the current version.' }
    $seen = [Collections.Generic.HashSet[version]]::new()
    foreach ($entry in @($Manifest.SerpChangelog)) {
        $version = [version]$entry.Version
        if (-not $seen.Add($version) -or @($entry.Changes).Count -eq 0) { throw "Duplicate/empty APIShared changelog entry: $version" }
        foreach ($change in $entry.Changes) {
            if ($change -isnot [string] -or [string]::IsNullOrWhiteSpace($change)) { throw "Invalid APIShared changelog text: $version" }
        }
    }
    if (-not $seen.Contains($current)) { throw "Missing APIShared changelog entry for $current." }
    $sections = @(foreach ($entry in @($Manifest.SerpChangelog | Where-Object {
        [version]$_.Version -le $current -and ($null -eq $previous -or [version]$_.Version -gt $previous)
    } | Sort-Object { [version]$_.Version } -Descending)) {
        (@("### v$($entry.Version)", '') + @($entry.Changes | ForEach-Object { '- ' + $_ })) -join "`r`n"
    })
    return $sections -join "`r`n`r`n"
}

# Release-only process adapter: stderr is diagnostic data, never a PowerShell error stream.
function Invoke-ApiReleasePublication {
    param([string]$Repository, [string]$Tag, [string]$Commit, [string[]]$Assets,
        [string]$Notes, [bool]$Publish, [string[]]$CreationArguments, [string]$Executable = 'gh')
    $existing = Get-ApiGitHubRelease $Repository $Tag $Executable
    if ($null -ne $existing) {
        if (-not $existing.isDraft -or $existing.targetCommitish -ne $Commit) {
            throw 'An existing published or different-source release must never be replaced.'
        }
        $result = Invoke-ApiReleaseGitHub (@('release', 'upload', $Tag) + $Assets + @('--repo', $Repository, '--clobber')) $Executable
        if ($result.ExitCode -ne 0) { throw ('Could not resume the matching draft assets. ' + $result.Diagnostic) }
        $result = Invoke-ApiReleaseGitHub @('release', 'edit', $Tag, '--repo', $Repository, '--notes-file', $Notes, "--draft=$(-not $Publish)") $Executable
    } else { $result = Invoke-ApiReleaseGitHub $CreationArguments $Executable }
    if ($result.ExitCode -ne 0) { throw ('GitHub release creation failed; the validated local artifact was preserved. ' + $result.Diagnostic) }
}

function ConvertTo-ApiReleaseProcessArgument([string]$Argument) {
    $escaped = [regex]::Replace($Argument, '(\\*)"', '$1$1\"')
    return '"' + [regex]::Replace($escaped, '(\\+)$', '$1$1') + '"'
}

function Invoke-ApiReleaseGitHub([string[]]$Arguments, [string]$Executable = 'gh') {
    $command = Get-Command $Executable -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $command.Source
    $start.Arguments = (@($Arguments | ForEach-Object { ConvertTo-ApiReleaseProcessArgument $_ }) -join ' ')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Could not start GitHub CLI.' }
        # Drain both pipes concurrently, including large CLI diagnostics.
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output.Result; Diagnostic = $errorOutput.Result.Trim() }
    } finally { $process.Dispose() }
}

function Get-ApiGitHubRelease([string]$Repository, [string]$Tag, [string]$Executable = 'gh') {
    $identity = Invoke-ApiReleaseGitHub @('api', 'user', '--jq', '.login') $Executable
    if ($identity.ExitCode -ne 0 -or -not $identity.Output.Trim()) {
        throw "GitHub authentication failed. Sign in with gh auth login. $($identity.Diagnostic)"
    }
    $access = Invoke-ApiReleaseGitHub @('api', "repos/$Repository", '--silent') $Executable
    if ($access.ExitCode -ne 0) { throw "Cannot access GitHub repository ${Repository}: $($access.Diagnostic)" }
    $result = Invoke-ApiReleaseGitHub @('release', 'view', $Tag, '--repo', $Repository, '--json', 'tagName,isDraft,targetCommitish,assets') $Executable
    if ($result.ExitCode -eq 0) { return ($result.Output | ConvertFrom-Json -ErrorAction Stop) }
    if ($result.ExitCode -eq 1 -and $result.Diagnostic -ceq 'release not found' -and -not $result.Output.Trim()) { return $null }
    throw "Cannot query GitHub release ${Repository} / ${Tag} (exit $($result.ExitCode)): $($result.Diagnostic)"
}
