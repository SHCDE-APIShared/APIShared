[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Guid, [Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$MinimumAPISharedVersion, [Parameter(Mandatory)][string]$APISharedWorkshopId)
$ErrorActionPreference='Stop'
if ($Guid -notmatch '^[A-Za-z0-9_-]+$' -or $Name -notmatch '^[A-Za-z0-9 _-]+$' -or $Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$' -or $MinimumAPISharedVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$' -or $APISharedWorkshopId -notmatch '^[1-9][0-9]*$') { throw 'Unsafe bootstrap template value.' }
$Directory=[IO.Path]::GetFullPath($Directory)
foreach ($required in @('Localization','Locales')) { if (Test-Path -LiteralPath (Join-Path $Directory $required)) { throw "Refusing to overwrite existing bootstrap resources: $required" } }
for ($part=$Directory; $part; $part=[IO.Path]::GetDirectoryName($part)) { if ((Test-Path -LiteralPath $part) -and ([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked output directory.' } }
[void][IO.Directory]::CreateDirectory($Directory)
foreach ($template in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../Workshop/Bootstrap') -File) {
    $text=[IO.File]::ReadAllText($template.FullName).Replace('@@GUID@@',$Guid).Replace('@@NAME@@',$Name).Replace('@@VERSION@@',$Version).Replace('@@MINIMUM@@',$MinimumAPISharedVersion).Replace('@@WORKSHOP@@',$APISharedWorkshopId).Replace("`r`n","`n").Replace("`n","`r`n")
    $output=Join-Path $Directory $template.Name.Replace('.template','')
    if (Test-Path -LiteralPath $output) { throw "Refusing to overwrite existing bootstrap source: $output" }
    [IO.File]::WriteAllText($output,$text,[Text.UTF8Encoding]::new($false)); if ([IO.File]::ReadAllText($output) -cne $text) { throw 'Template write mismatch.' }
}
$info=[ordered]@{GUID=$Guid;Author='Workshop publisher';Name=$Name+' Workshop bootstrap';Version=$Version;Manifest=1;MinimumScriptExtenderVersion='2.14.0';Dependencies=@();NetworkMode=0}
[IO.File]::WriteAllText((Join-Path $Directory 'info.json'),($info | ConvertTo-Json -Depth 5).Replace("`r`n","`n").Replace("`n","`r`n")+"`r`n",[Text.UTF8Encoding]::new($false))
[void][IO.Directory]::CreateDirectory((Join-Path $Directory 'Localization'))
foreach ($name in @('TextCatalog.cs','UiText.cs','GameLocale.cs')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('../src/Localization/'+$name)) -Destination (Join-Path $Directory 'Localization')
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../Locales') -Destination (Join-Path $Directory 'Locales') -Recurse
