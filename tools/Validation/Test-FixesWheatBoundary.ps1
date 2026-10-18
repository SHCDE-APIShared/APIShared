[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot 'SourceContracts.Common.ps1')
Invoke-ApiSourceContracts -Root $root -Group wheat
# Exact native bytes/offsets, before/after verification and rejected writes are
# tested by FixesWheatContractTests and the installed-backend execution fixture.
# These syntax checks only guard the narrow startup/memory ownership boundary.
