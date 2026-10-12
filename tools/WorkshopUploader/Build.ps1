$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'tools/BuildProof.ps1')
Initialize-ApiBuildEnvironment $root
$native=Join-Path $env:SHCDE_GAME_DIR 'Stronghold Crusader Definitive Edition_Data/Plugins/x86_64/steam_api64.dll'
if (-not (Test-Path -LiteralPath $native -PathType Leaf)) { throw "Installed Steam native library not found: $native" }
$output=Join-Path $root '.local/WorkshopUploader'
& dotnet build (Join-Path $PSScriptRoot 'WorkshopUploader.csproj') --configuration Release --output $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'Workshop uploader build failed.' }
Copy-Item -LiteralPath $native -Destination $output
foreach ($launcher in @('UploadMod.bat','UploadMod-linux.sh')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $launcher) -Destination $output -Force }
. (Join-Path $PSScriptRoot 'PublisherBuildProof.ps1')
$inputs=@(Get-WorkshopPublisherInputs $root $env:SHCDE_GAME_DIR)
$outputs=@(Get-ChildItem -LiteralPath $output -File|Where-Object {$_.Extension -in @('.exe','.dll','.config','.bat','.sh')}|ForEach-Object {[ordered]@{Name=$_.Name;Sha256=(Get-PublisherHash $_.FullName)}})
$proof=[ordered]@{SchemaVersion=1;GameDir=$env:SHCDE_GAME_DIR;Inputs=$inputs;Outputs=$outputs}
[IO.File]::WriteAllText((Join-Path $output 'build-proof.json'),(($proof|ConvertTo-Json -Depth 5)+"`r`n"),[Text.UTF8Encoding]::new($false))
Write-Host "Workshop uploader ready: $output"
