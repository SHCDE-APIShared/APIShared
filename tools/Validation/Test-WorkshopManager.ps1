$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$runtime=[IO.File]::ReadAllText((Join-Path $root 'src/Workshop/WorkshopRuntime.cs'))
$startup=[IO.File]::ReadAllText((Join-Path $root 'src/Workshop/WorkshopStartup.cs'))
if ($runtime -match 'RegisterLobbyModSettings|WorkshopViewModel|LibraryLoaded \+=' -or
    [IO.File]::Exists((Join-Path $root 'src/Workshop/WorkshopViewModel.cs')) -or
    [IO.File]::Exists((Join-Path $root 'Override/Workshop/Workshop.xaml'))) { throw 'Removed Workshop UI is still registered or shipped.' }
if ($runtime -notmatch 'private static IDisposable steamSubscription;' -or $runtime -notmatch 'WorkshopStartup.Run\(\)' -or
    $startup -notmatch 'Interlocked.Exchange\(ref attempted, 1\)' -or $startup -notmatch 'MessageBoxW') { throw 'Startup work must have one process-owned confirmation.' }
if($startup -match 'async\s+void\s+Run' -or $startup -notmatch 'Run\(\)\s*=>\s*WorkshopStartupGate.Run' -or
   $runtime -notmatch 'authorization.Quit' -or $runtime -notmatch 'authorization.Validate\(\)' -or
   @([regex]::Matches($runtime,'UnityEngine\.Application\.Quit\(\)')).Count -ne 1){throw 'Automatic updates must keep the startup scope and use confirmed handoff authority; only the public consumer workflow may quit directly.'}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'src/Workshop') -File -Filter '*.cs') {
    $source=[IO.File]::ReadAllText($file.FullName)
    if ($source -match '(?<!\r)\n') { throw "Workshop CRLF check failed: $($file.Name)" }
    if ($source -match '\b(Update|LateUpdate|FixedUpdate|StartCoroutine|OnDestroy|OnDisable|OnApplicationQuit)\s*\(\s*\)') { throw "Unsupported component lifetime: $($file.Name)" }
}
$linux=[IO.File]::ReadAllText((Join-Path $root 'tools/WorkshopUploader/UploadMod-linux.sh'))
if ($linux.Contains("`r") -or $linux -notmatch '--interactive "\$@"' -or $linux -notmatch 'STEAM_COMPAT_DATA_PATH') { throw 'Linux launcher must use LF and forward arguments through the game Proton prefix.' }
foreach ($script in @('workshop-updater.ps1','workshop-updater.sh')) {
    $text=[IO.File]::ReadAllText((Join-Path $root ('Workshop/'+$script)))
    if ($text -notmatch 'APISHARED-WORKSHOP-2' -or $text -notmatch 'SCDEModManagerLaunchId') { throw 'Updater lacks batch protocol or external-manager protection.' }
}
if ([IO.File]::ReadAllText((Join-Path $root 'src/Workshop/WorkshopSteamUpload.cs')) -match 'UploadWorkshopMap\(') { throw 'Workshop publishing must not reuse the vanilla upload singleton.' }
Write-Host 'PASS: rooted startup coordination, UI removal, Linux launcher, batch protocol and upload isolation.'
