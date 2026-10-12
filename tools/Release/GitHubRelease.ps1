# Release-only process adapter: stderr is diagnostic data, never a PowerShell error stream.
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
