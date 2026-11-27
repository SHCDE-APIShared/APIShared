[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot 'SourceContracts.Common.ps1')
& (Join-Path $PSScriptRoot 'Test-FixesWheatBoundary.ps1')
Invoke-ApiSourceContracts -Root $root -Group hooks
# Publication, candidate rollback, roots and incoming IL labels are exercised by
# NativeHookPublicationTests, AudioNativeContractTests and WorkshopHookTests.
# A catch block alone does not prove that hooks are still unpublished.
