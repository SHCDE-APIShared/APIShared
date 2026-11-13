[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Driver,
    [switch]$Api,
    [Parameter(ValueFromRemainingArguments)][string[]]$DriverArguments
)
. (Join-Path $PSScriptRoot 'BranchBuild.Common.ps1')
. (Join-Path $PSScriptRoot 'BranchRun.ps1')
$run = $null
$exitCode = 0
try {
    $testMode = Get-ApiBuildTestMode $Driver $DriverArguments
    $branch = Get-BranchChoice $DriverArguments
    if ($branch -eq 'working') {
        $root = [IO.Path]::GetFullPath($Repository).TrimEnd('\','/')
        $target = [IO.Path]::GetFullPath((Join-Path $root $Driver))
        if (-not $target.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or $Driver -notmatch '(^|[/\\])build\.bat$') { throw 'Invalid build driver.' }
        & $target @DriverArguments /workspace
        exit $LASTEXITCODE
    }
    # Only explicit no-install/full requests with understood arguments may reuse SDK output.
    $cacheArguments=@('/nopause','/noinstall','/tests:full','/keepsource','/branch:main','/branch:develop')
    $unknownArguments=@($DriverArguments | Where-Object { $_.ToLowerInvariant() -notin $cacheArguments })
    if ($Api -and $Driver -eq 'build.bat' -and $DriverArguments -icontains '/noinstall' -and
        $testMode -eq 'full' -and -not $unknownArguments.Count) {
        $run=Find-ReusableApiBranchRun $Repository $branch
        if ($run) {
            if ($DriverArguments -icontains '/keepsource') { $run.State.KeepSource=$true }
            Set-ApiBranchRunPhase $run 'sdk-ready' 'Reused full SDK; no installation requested'
            exit 0
        }
    }
    $run = New-ApiBranchRun $Repository $branch $Driver ($DriverArguments -icontains '/keepsource')
    $selection = New-BranchSource $Repository $branch $run.Root
    $selection | Add-Member NoteProperty Run $run
    $null = Invoke-SelectedBranchBuild -Repository $Repository -Driver $Driver -Branch $branch -DriverArguments $DriverArguments -ControllerRoot $Repository -Api:$Api -SourceSelection $selection
    if ($run.State.KeepSource) { Set-ApiBranchRunPhase $run 'retained' '/keepsource requested' }
    elseif ($Api -and $Driver -eq 'build.bat' -and $testMode -eq 'full' -and
        [IO.File]::Exists((Join-Path $run.Root 'tools/BuildProof.ps1'))) {
        . (Join-Path $run.Root 'tools/BuildProof.ps1')
        $null = Assert-ApiBuildProof $run.Root
        Set-ApiBranchRunPhase $run 'sdk-ready' 'Current SDK source retained for consumer proof validation'
        Remove-SupersededApiBranchRuns $run
    } else { Set-ApiBranchRunPhase $run 'retained' "Successful output retained for review (tests:$testMode); not promoted to a full SDK" }

} catch {
    Write-BranchMessage ('ERROR: ' + $_.Exception.Message)
    $exitCode = if ($_.Exception.Data.Contains('BuildExitCode')) { [int]$_.Exception.Data['BuildExitCode'] } else { 1 }
    if ($run) {
        try {
            if ($run.State.KeepSource) { Set-ApiBranchRunPhase $run 'retained' ('Failed run: ' + $_.Exception.Message) }
            else {
                if ([IO.File]::Exists((Join-Path $run.Root '.git'))) { Export-ApiBranchRunEvidence $run }
                Remove-ApiBranchRun $run
            }
        } catch { Set-ApiBranchRunPhase $run 'cleanup-failed' $_.Exception.Message }
    }
} finally { if ($run -and $run.Lease) { $run.Lease.Dispose() } }
exit $exitCode
