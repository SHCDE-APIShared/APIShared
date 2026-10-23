# Build-only helpers; no runtime or neighboring-workspace dependency.
function Get-ApiMaintainedFiles([Parameter(Mandatory)][string]$Root) {
    # These are generated-output boundaries, not a prescribed feature layout.
    # Prune before descending: old snapshots/fixtures can dwarf maintained sources.
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Root))
    while ($pending.Count) {
        $directory = $pending.Pop()
        Get-ChildItem -LiteralPath $directory -File
        foreach ($child in Get-ChildItem -LiteralPath $directory -Directory) {
            if ($child.Name -notin @('bin','obj','BepInEx','.git','.local','.work','.release-output')) { $pending.Push($child.FullName) }
        }
    }
}
function Get-ApiBuildTestMode {
    param([string]$Driver = 'build.bat', [string[]]$DriverArguments = @())
    $choices = @($DriverArguments | Where-Object { $_ -match '^/tests(?::|$)' })
    if ($choices.Count -and $Driver.Replace('\','/') -ine 'build.bat') { throw '/tests:full|quick|none is supported by APIShared/build.bat only.' }
    $modes = @($choices | ForEach-Object {
        if ($_ -notmatch '^/tests:(full|quick|none)$') { throw 'Use /tests:full, /tests:quick or /tests:none.' }
        $Matches[1].ToLowerInvariant()
    } | Sort-Object -Unique)
    if ($modes.Count -gt 1) { throw 'Conflicting /tests arguments.' }
    if ($modes.Count) { return $modes[0] }
    return 'full'
}
function New-ApiBuildContext([ValidateSet('full','quick','none')][string]$Mode = 'full') {
    return [pscustomobject]@{ Mode=$Mode; StartedUtc=[DateTime]::UtcNow.ToString('o'); Steps=[Collections.Generic.List[object]]::new() }
}
function Invoke-ApiBuildStep {
    param([Parameter(Mandatory)]$Context, [Parameter(Mandatory)][string]$Name,
        [string]$Category = 'tests', [ValidateSet('always','checked','full')][string]$When = 'full',
        [Parameter(Mandatory)][scriptblock]$Action)
    $started = [DateTime]::UtcNow.ToString('o')
    $skip = ($When -eq 'full' -and $Context.Mode -ne 'full') -or ($When -eq 'checked' -and $Context.Mode -eq 'none')
    if ($skip) {
        Write-Host ('[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] SKIP {2} (tests:{3})' -f [DateTime]::Now,$Category,$Name,$Context.Mode)
        $Context.Steps.Add([pscustomobject]@{ Name=$Name; Category=$Category; Selection=$When; Status='Skipped'; StartedUtc=$started; DurationMs=0 })
        return
    }
    Write-Host ('[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] START {2}' -f [DateTime]::Now,$Category,$Name)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $status = 'Failed'
    try { & $Action | Out-Host; $status = 'Passed' }
    finally {
        $watch.Stop()
        $Context.Steps.Add([pscustomobject]@{ Name=$Name; Category=$Category; Selection=$When; Status=$status; StartedUtc=$started; DurationMs=$watch.ElapsedMilliseconds })
        Write-Host ('[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] {2} {3} ({4} ms)' -f [DateTime]::Now,$Category,$status.ToUpperInvariant(),$Name,$watch.ElapsedMilliseconds)
    }
}
function Save-ApiBuildSteps([string]$Root, $Context, [bool]$Succeeded) {
    $directory = Join-Path $Root '.local'
    [void][IO.Directory]::CreateDirectory($directory)
    $record = [ordered]@{ SchemaVersion=1; TestMode=$Context.Mode; StartedUtc=$Context.StartedUtc; FinishedUtc=[DateTime]::UtcNow.ToString('o'); Succeeded=$Succeeded; Steps=$Context.Steps.ToArray() }
    $text = ($record | ConvertTo-Json -Depth 6).Replace("`r`n","`n").Replace("`n","`r`n") + "`r`n"
    [IO.File]::WriteAllText((Join-Path $directory 'build-steps.json'),$text,[Text.UTF8Encoding]::new($false))
}
