param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
[xml]$xaml=[IO.File]::ReadAllText((Join-Path $root 'Override/Workshop/Workshop.xaml'))
$interactive=@($xaml.SelectNodes('//*[local-name()="Button" or local-name()="CheckBox" or local-name()="ComboBox" or local-name()="TextBox"]'))
foreach ($control in $interactive) {
    if (-not $control.GetAttribute('ToolTip') -or $control.GetAttribute('ToolTipService.ShowDuration') -cne '60000') { throw 'Workshop UI control lacks its nonempty 60000ms tooltip.' }
}
$style=$xaml.SelectSingleNode('//*[local-name()="Style"]')
foreach ($pair in @(@('Background','#FF1D1710'),@('Foreground','White'),@('BorderBrush','#FFF2D48A'),@('seui:ToolTipResolutionScale.Enabled','True'),@('FontSize','{x:Static api:ToolTipPresentation.FontSize}'),@('MaxWidth','{x:Static api:ToolTipPresentation.MaximumWidth}'))) {
    $setter=$style.SelectSingleNode('./*[local-name()="Setter"][@Property="'+$pair[0]+'"]')
    if (-not $setter -or $setter.GetAttribute('Value') -cne $pair[1]) { throw 'Workshop tooltip differs from shared style.' }
}
if (-not $style.SelectSingleNode('.//*[local-name()="ControlTemplate"]//*[local-name()="TextBlock"][@TextWrapping="Wrap"]')) { throw 'Workshop tooltip must wrap.' }
$view=[IO.File]::ReadAllText((Join-Path $root 'src/Workshop/WorkshopViewModel.cs'))
foreach ($binding in [regex]::Matches($xaml.OuterXml,'\{Binding\s+(\w+)')) { if ($view -notmatch ('\b'+$binding.Groups[1].Value+'\b')) { throw ('Unknown Workshop binding: '+$binding.Groups[1].Value) } }
if ($view -notmatch 'internal static string Pick\(string en, string de\)' -or $view -notmatch 'German \? de : en') { throw 'Workshop text must provide German plus English fallback for all other languages.' }
foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $root 'src/Workshop') -File -Filter '*.cs')+@(Get-Item -LiteralPath (Join-Path $root 'Override/Workshop/Workshop.xaml'))) {
    if ([IO.File]::ReadAllText($file.FullName) -match '(?<!\r)\n') { throw ('Workshop CRLF check failed: '+$file.FullName) }
}
$runtime=[IO.File]::ReadAllText((Join-Path $root 'src/Workshop/WorkshopRuntime.cs'))
$viewMatch=[regex]::Match($runtime,'const string viewPath = "([^"]+)";')
if (-not $viewMatch.Success -or -not [IO.File]::Exists((Join-Path $root ('Override/' + $viewMatch.Groups[1].Value))) -or
    $runtime -notmatch 'GetModifiedFilePath\(viewPath,' -or [IO.File]::Exists((Join-Path $root 'Workshop/Workshop.xaml'))) {
    throw 'Workshop registration must resolve its one view through the Override index.'
}
[xml]$project=[IO.File]::ReadAllText((Join-Path $root 'APIShared.csproj'))
if (-not $project.SelectSingleNode('//*[local-name()="Content"][@Include="Override\**\*"]')) { throw 'Workshop Override resources are not included in the runtime project.' }
if ($runtime -match '\b(Update|LateUpdate|FixedUpdate|StartCoroutine|OnDestroy|OnDisable|OnApplicationQuit)\s*\(' -or $runtime -notmatch 'private static IDisposable steamSubscription;' -or $runtime -notmatch 'LibraryLoaded \+=') { throw 'Workshop scheduling must be publisher-rooted.' }
if ([IO.File]::ReadAllText((Join-Path $root 'src/Workshop/WorkshopSteamUpload.cs')) -match 'UploadWorkshopMap\(') { throw 'Workshop publishing must not reuse the vanilla upload singleton.' }
Write-Host ('PASS: Workshop manager publisher lifetime, vanilla upload isolation, XAML bindings and '+$interactive.Count+' localized tooltip controls.')
