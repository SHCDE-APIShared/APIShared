function Get-PublisherHash([string]$Path){
    $stream=[IO.File]::OpenRead($Path);$sha=[Security.Cryptography.SHA256]::Create()
    try{return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}finally{$stream.Dispose();$sha.Dispose()}
}
function Get-WorkshopPublisherInputs([string]$ApiRoot,[string]$GameDir){
    $project=Join-Path $ApiRoot 'tools/WorkshopUploader/WorkshopUploader.csproj'
    [xml]$xml=[IO.File]::ReadAllText($project)
    $paths=@($project,(Join-Path $ApiRoot 'tools/WorkshopUploader/Build.ps1'),(Join-Path $ApiRoot 'tools/WorkshopUploader/PublisherBuildProof.ps1'))
    $paths+=@((Join-Path $ApiRoot 'tools/WorkshopUploader/App.config'),(Join-Path $ApiRoot 'tools/WorkshopUploader/app.manifest'))
    $paths+=@((Join-Path $ApiRoot 'tools/WorkshopUploader/UploadMod.bat'),(Join-Path $ApiRoot 'tools/WorkshopUploader/UploadMod-linux.sh'))
    $paths+=(Join-Path $ApiRoot 'tools/WorkshopUploader/PublisherText.ps1')
    $paths+=@(Get-ChildItem -LiteralPath (Join-Path $ApiRoot 'Locales') -File -Filter '*.txt' | ForEach-Object {$_.FullName})
    $paths+=(Join-Path $GameDir 'Stronghold Crusader Definitive Edition_Data/Plugins/x86_64/steam_api64.dll')
    $paths+=@(Get-ChildItem -LiteralPath $ApiRoot -File|Where-Object {$_.Name -match '^Directory\..*\.(props|targets)$'}|ForEach-Object {$_.FullName})
    foreach($entry in $xml.Project.ItemGroup.Compile){if($entry.Include){$paths+=[IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $project) $entry.Include))}}
    foreach($entry in $xml.Project.ItemGroup.Reference){if($entry.HintPath){$paths+=([string]$entry.HintPath).Replace('$(GameDir)',$GameDir)}}
    return @($paths|Sort-Object -Unique|ForEach-Object {[ordered]@{Path=$_;Sha256=(Get-PublisherHash $_)}})
}
function Assert-WorkshopPublisherBuild([string]$ApiRoot){
    $proofPath=Join-Path $ApiRoot '.local/WorkshopUploader/build-proof.json'
    if(-not(Test-Path -LiteralPath $proofPath)){throw 'Build APIShared/tools/WorkshopUploader/build.bat first.'}
    $proof=[IO.File]::ReadAllText($proofPath)|ConvertFrom-Json
    $actual=@(Get-WorkshopPublisherInputs $ApiRoot $proof.GameDir)
    if($proof.SchemaVersion -ne 1 -or @($proof.Inputs).Count -ne $actual.Count){throw 'Rebuild WorkshopUploader: build inputs changed.'}
    foreach($inputRecord in $actual){if(@($proof.Inputs|Where-Object {$_.Path -ceq $inputRecord.Path -and $_.Sha256 -ceq $inputRecord.Sha256}).Count -ne 1){throw "Rebuild WorkshopUploader: $($inputRecord.Path) changed."}}
    foreach($output in $proof.Outputs){if((Get-PublisherHash (Join-Path $ApiRoot ('.local/WorkshopUploader/'+$output.Name))) -cne $output.Sha256){throw 'WorkshopUploader output differs from its build proof.'}}
}
