param([Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+$')][string]$Key)
$ErrorActionPreference='Stop'
# A broken publisher executable can still explain the problem. Catalog data is never executed.
$locale=[Globalization.CultureInfo]::CurrentUICulture.Name
if ($locale -like 'de*') { $locale='de-DE' } elseif ($locale -like 'en*') { $locale='en-US' }
foreach($candidate in @($locale,'en-US')) {
    $path=Join-Path $PSScriptRoot ('Locales/'+$candidate+'.txt')
    if (-not (Test-Path -LiteralPath $path)) { continue }
    try { $lines=[IO.File]::ReadAllLines($path,[Text.UTF8Encoding]::new($false,$true)) }
    catch [IO.IOException] { continue }
    catch [UnauthorizedAccessException] { continue }
    catch [Text.DecoderFallbackException] { continue }
    foreach($line in $lines) {
        if ($line.StartsWith($Key+'=',[StringComparison]::Ordinal)) { Write-Output $line.Substring($Key.Length+1); exit 0 }
    }
}
Write-Output $Key
