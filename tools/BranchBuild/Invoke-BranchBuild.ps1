[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Driver,
    [switch]$Api,
    [Parameter(ValueFromRemainingArguments)][string[]]$DriverArguments
)
. (Join-Path $PSScriptRoot 'BranchBuild.Common.ps1')
try {
    $branch = Get-BranchChoice $DriverArguments
    if ($branch -eq 'working') {
        $root = [IO.Path]::GetFullPath($Repository).TrimEnd('\','/')
        $target = [IO.Path]::GetFullPath((Join-Path $root $Driver))
        if (-not $target.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or $Driver -notmatch '(^|[/\\])build\.bat$') { throw 'Invalid build driver.' }
        & $target @DriverArguments /workspace
        exit $LASTEXITCODE
    }
    $null = Invoke-SelectedBranchBuild -Repository $Repository -Driver $Driver -Branch $branch -DriverArguments $DriverArguments -ControllerRoot $Repository -Api:$Api
    exit 0
} catch {
    Write-BranchMessage ('ERROR: ' + $_.Exception.Message)
    if ($_.Exception.Data.Contains('BuildExitCode')) { exit [int]$_.Exception.Data['BuildExitCode'] }
    exit 1
}
