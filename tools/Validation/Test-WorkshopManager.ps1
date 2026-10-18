$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot 'SourceContracts.Common.ps1')
Invoke-ApiSourceContracts -Root $root -Group workshop
if ([IO.File]::Exists((Join-Path $root 'Override/Workshop/Workshop.xaml'))) { throw 'Removed Workshop UI is still shipped.' }
# Runtime CRLF is checked on evaluated Compile inputs by the preflight.
$linux=[IO.File]::ReadAllText((Join-Path $root 'tools/WorkshopUploader/UploadMod-linux.sh'))
if ($linux.Contains("`r") -or $linux -notmatch '--interactive "\$@"' -or $linux -notmatch 'STEAM_COMPAT_DATA_PATH') { throw 'Linux launcher must use LF and forward arguments through the game Proton prefix.' }
foreach ($script in @('workshop-updater.ps1','workshop-updater.sh')) {
    $text=[IO.File]::ReadAllText((Join-Path $root ('Workshop/'+$script)))
    if ($text -notmatch 'APISHARED-WORKSHOP-2' -or $text -notmatch 'SCDEModManagerLaunchId') { throw 'Updater lacks batch protocol or external-manager protection.' }
}
# Runtime lifecycle and upload ownership are parsed by SourceContracts; a method
# name in a comment/string or an ordinary service Update is not a Unity callback.
Write-Host 'PASS: rooted startup coordination, UI removal, Linux launcher, batch protocol and upload isolation.'
