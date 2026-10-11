[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$PackageId, [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$MinimumAPISharedVersion,
    [string]$MinimumScriptExtenderVersion = '2.14.0', [string]$BootstrapMap = '', [string]$BootstrapGuid = '', [string]$BootstrapVersion = '', [string]$BootstrapFolder = '', [object[]]$Dependencies = @(), [object[]]$LegacyReleases = @())
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WorkshopPackage.ps1')
if ($PackageId -notmatch '^[A-Za-z0-9_-]+$' -or -not [Version]::TryParse($Version,[ref]([Version]$null))) { throw 'Invalid package identity/version.' }
$Directory = [IO.Path]::GetFullPath($Directory).TrimEnd('\','/')
$payload = Join-Path $Directory 'Payload'
foreach ($entry in Get-ChildItem -LiteralPath $payload -Force -Recurse) { if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked payload.' } }
for ($part=$Directory; $part; $part=[IO.Path]::GetDirectoryName($part)) { if ([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked source directory.' } }
$roots = @(Get-ChildItem -LiteralPath (Join-Path $payload 'BepInEx/plugins') -Directory | ForEach-Object { 'BepInEx/plugins/'+$_.Name })
if (-not $roots.Count -or $roots -match '^BepInEx/plugins/(APIShared_Serp|000shcdese)$') { throw 'Invalid roots.' }
$files = @(Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object {
    $relative=$_.FullName.Substring($payload.Length+1).Replace('\','/')
    if ($relative -notmatch '^BepInEx/plugins/[^/]+/.+' -or $relative -match '/(config|LobbyModSettings)(/|$)') { throw "Invalid payload target: $relative" }
    [ordered]@{Path=$relative;Size=$_.Length;Sha256=Get-WorkshopFileHash $_.FullName}
} | Sort-Object { $_.Path })
foreach ($root in $roots) { if (-not (Test-Path -LiteralPath (Join-Path $payload ($root+'/info.json')))) { throw 'Root lacks info.json.' } }
$manifest = [ordered]@{SchemaVersion=1;PackageId=$PackageId;Version=$Version;MinimumAPISharedVersion=$MinimumAPISharedVersion;MinimumScriptExtenderVersion=$MinimumScriptExtenderVersion;MaximumScriptExtenderVersion='';Roots=$roots;Files=$files;Dependencies=@($Dependencies);LegacyReleases=@($LegacyReleases)}
if ($BootstrapMap) {
    if ([IO.Path]::GetFileName($BootstrapMap) -cne $BootstrapMap -or -not $BootstrapMap.EndsWith('.map') -or -not $BootstrapGuid -or -not $BootstrapVersion -or $BootstrapFolder -notmatch '^BepInEx/plugins/[^/]+$' -or $roots -contains $BootstrapFolder) { throw 'Invalid bootstrap.' }
    $manifest.Bootstrap=[ordered]@{MapFile=$BootstrapMap;Guid=$BootstrapGuid;Version=$BootstrapVersion;PluginFolder=$BootstrapFolder}
}
$text=($manifest | ConvertTo-Json -Depth 20).Replace("`r`n","`n").Replace("`n","`r`n")+"`r`n"
$path=Join-Path $Directory 'apishared-workshop.json'; [IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))
if ([IO.File]::ReadAllText($path) -cne $text) { throw 'Manifest write mismatch.' }
return $manifest
