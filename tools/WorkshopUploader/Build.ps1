$ErrorActionPreference='Stop'
if (-not $env:SHCDE_GAME_DIR) { throw 'Set SHCDE_GAME_DIR first.' }
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output=Join-Path $root '.local/WorkshopUploader'
& dotnet build (Join-Path $PSScriptRoot 'WorkshopUploader.csproj') --configuration Release --output $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'Workshop uploader build failed.' }
$native=Join-Path $env:SHCDE_GAME_DIR 'Stronghold Crusader Definitive Edition_Data/Plugins/x86_64/steam_api64.dll'
if (-not (Test-Path -LiteralPath $native)) { throw 'Installed Steam native library not found.' }
Copy-Item -LiteralPath $native -Destination $output
. (Join-Path $PSScriptRoot 'PublisherBuildProof.ps1')
$inputs=@(Get-WorkshopPublisherInputs $root $env:SHCDE_GAME_DIR)
$outputs=@(Get-ChildItem -LiteralPath $output -File|Where-Object {$_.Extension -in @('.exe','.dll','.config')}|ForEach-Object {[ordered]@{Name=$_.Name;Sha256=(Get-PublisherHash $_.FullName)}})
$proof=[ordered]@{SchemaVersion=1;GameDir=$env:SHCDE_GAME_DIR;Inputs=$inputs;Outputs=$outputs}
[IO.File]::WriteAllText((Join-Path $output 'build-proof.json'),(($proof|ConvertTo-Json -Depth 5)+"`r`n"),[Text.UTF8Encoding]::new($false))
Write-Host "Workshop uploader ready: $output"
