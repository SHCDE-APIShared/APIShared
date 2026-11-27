[CmdletBinding(SupportsShouldProcess=$true)]
param([Parameter(Mandatory)][string]$GameDir,[Parameter(Mandatory)][string]$PackageRoot,[switch]$Apply)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'WorkshopPackage.ps1')
. (Join-Path $PSScriptRoot 'DependencyBootstrapPackage.ps1')
if($env:SCDEModManagerLaunchId){throw 'Externally managed installation; preserve bootstrap.'}
foreach($process in Get-Process -Name 'Stronghold Crusader Definitive Edition' -ErrorAction SilentlyContinue){
    if([IO.Path]::GetDirectoryName($process.Path) -ieq [IO.Path]::GetFullPath($GameDir).TrimEnd('\','/')){throw 'Close the game before removal.'}
}
Assert-ApiBootstrapPair $PackageRoot
$expected=@(Get-ApiBootstrapRecords $PackageRoot)[0].Sha256
$target=[IO.Path]::GetFullPath((Join-Path $GameDir 'BepInEx/patchers/APIShared.DependencyBootstrap.dll'))
function Assert-Removal {
    Assert-ApiWorkshopUnlinkedPath $target
    if(-not [IO.File]::Exists($target)){return}
    if((Get-WorkshopFileHash $target) -cne $expected){throw 'Bootstrap changed; preserve it.'}
    if(Test-Path -LiteralPath (Join-Path $GameDir '_SE/.staging')){throw 'Pending Script Extender staging; preserve bootstrap.'}
    $plugins=Join-Path $GameDir 'BepInEx/plugins'
    if(Test-Path -LiteralPath $plugins){
        Assert-ApiWorkshopUnlinkedTree $plugins
        $canonical=[IO.Path]::GetFullPath((Join-Path $plugins 'APIShared_Serp/dependency-bootstrap-pair.json'))
        foreach($manifest in Get-ChildItem -LiteralPath $plugins -Recurse -File -Filter 'dependency-bootstrap-pair.json'){
            if($manifest.FullName -ine $canonical){throw 'Competing pair claim; preserve bootstrap.'}
            $record=Get-Content -Raw -LiteralPath $manifest.FullName|ConvertFrom-Json
            if($record.BootstrapSHA256 -cne $expected){throw 'Installed package claims another bootstrap; preserve it.'}
        }
    }
    # Unknown ownership through workshop subscriptions cannot be inferred from files.
    # This is deliberately a manual maintenance action on the exact verified file.
}
Assert-Removal
if(-not [IO.File]::Exists($target)){Write-Host 'Bootstrap already absent.';return}
if(-not $Apply){Write-Host "PREVIEW: would remove only $target (SHA256 $expected). Use -Apply after reviewing ownership.";return}
if($PSCmdlet.ShouldProcess($target,'Remove exact package-verified bootstrap only')){
    Assert-Removal
    Remove-Item -LiteralPath $target
    Write-Host 'Removed bootstrap; APIShared runtime and all other patchers preserved.'
}
