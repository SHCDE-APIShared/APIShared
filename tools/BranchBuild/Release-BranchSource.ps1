[CmdletBinding(SupportsShouldProcess=$true, ConfirmImpact='Medium')]
param([Parameter(Mandatory)][string]$Root, [switch]$Release)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'BranchBuild.Common.ps1')
. (Join-Path $PSScriptRoot 'BranchRun.ps1')
$lease=$null
try {
    $rootPath=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    Assert-BranchUnlinkedPath $rootPath
    $state=[IO.File]::ReadAllText($rootPath+'.lifecycle.json')|ConvertFrom-Json
    if ($state.SchemaVersion -ne 1 -or $state.Protocol -cne 'APISharedBranchRun') { throw 'Unsupported run ownership record.' }
    $run=[pscustomobject]@{Root=$rootPath;Branch=$state.Branch;Commit=$state.Commit;State=$state;Lease=$null}
    Assert-ApiBranchRunPath $run
    if (-not $Release) {
        Write-BranchMessage "PREVIEW: would validate and retire $rootPath. Use -Release only after all SDK consumers have released this source."
        exit 0
    }
    $lease=[IO.File]::Open($rootPath+'.lease','Open','ReadWrite','None');$run.Lease=$lease
    Assert-ApiBranchRunInactive $run
    if ($PSCmdlet.ShouldProcess($rootPath,'Preserve required evidence and retire explicitly unused SDK source through Git')) {
        Export-ApiBranchRunEvidence $run
        # Explicit user release is the consumer-lifetime attestation; never infer it from age.
        $run.State.ConsumerSafeToRetire=$true
        Set-ApiBranchRunPhase $run 'cleaning' 'Explicit release: all consumers finished'
        Remove-ApiBranchRun $run
        Write-BranchMessage "Released APIShared source: $rootPath"
    }
} catch { Write-BranchMessage ('ERROR: '+$_.Exception.Message); exit 1 }
finally { if ($lease) { $lease.Dispose() } }
