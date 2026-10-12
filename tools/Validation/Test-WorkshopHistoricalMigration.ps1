param([Parameter(Mandatory)][string]$Workspace)
$ErrorActionPreference = 'Stop'
$script:Root = [IO.Path]::GetFullPath($Workspace)
$script:OutputRoot = Join-Path $script:Root '.release-output/SerpsMods'
$PackGuid = 'SerpsMods_Serp'
. (Join-Path $PSScriptRoot '../WorkshopPackage.ps1')
. (Join-Path $PSScriptRoot '../WorkshopMigration.ps1')
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $script:Root 'Shared/Tools/Steam/Create-SteamModPack.ps1'), [ref]$null, [ref]$null)
foreach ($name in @('Get-Sha256','Find-MapZipOffset','Get-MapFileRecords','Get-HistoryMapPath','Export-MapEntry')) {
    $definition = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true))
    if ($definition.Count -ne 1) { throw "Missing production helper: $name" }
    Invoke-Expression $definition[0].Extent.Text
}
function Fail-Pack($Code,$Message) { throw $Message }
function Invoke-Checked { throw 'Historical regression must not download or publish.' }
$history = [IO.File]::ReadAllText((Join-Path $script:Root '.release-output/SteamWorkshop/items/3024040-SerpsMods.upload-history.json')) | ConvertFrom-Json
$uploads = @($history.Uploads | Where-Object { $_.PSObject.Properties.Name -notcontains 'Format' -or $_.Format -eq 'Map' })
$maps = @($uploads | ForEach-Object { Get-HistoryMapPath -Upload $_ })
$version = ([IO.File]::ReadAllText((Join-Path $script:Root 'APIShared/info.json')) | ConvertFrom-Json).Version
Assert-ApiWorkshopMigrationVersion $version $maps $PackGuid
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $script:Root 'APIShared/tests/Core.Tests/bin/Release/net10.0/Core.Tests.dll')))
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$prepare = $assembly.GetType('APIShared.Workshop.WorkshopInstallation').GetMethod('Prepare',$flags)
$stage = $assembly.GetType('APIShared.Workshop.WorkshopTransaction').GetMethod('Stage',$flags)
$action = [Enum]::Parse($assembly.GetType('APIShared.Workshop.WorkshopAction'),'Install')
$fixture = Join-Path $script:Root ('APIShared/.local/historical-migration-' + [guid]::NewGuid().ToString('N'))
function Write-Fixture([string]$Path,[string]$Text) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path,$Text,[Text.UTF8Encoding]::new($false))
}
$checked = 0
for ($index=0; $index -lt $uploads.Count; $index++) {
    $map = $maps[$index]
    $old = @(Get-MapFileRecords -MapPath $map -SourcePack $uploads[$index].PackVersion | Where-Object {
        $_.Path.StartsWith('BepInEx/plugins/SerpsMods_Serp/Infrastructure/APIShared_Serp/',[StringComparison]::Ordinal) -and $_.Path -notmatch '/(config|LobbyModSettings)/'
    })
    if (-not $old.Count) { continue }
    foreach ($variant in @('original','modified','missing','unproven','changed-after-plan','external','linked','foreign-owner')) {
        $run = Join-Path $fixture ($uploads[$index].PackVersion + '/' + $variant)
        $game = Join-Path $run 'game'; $source = Join-Path $run 'source'
        $payloadRoot = 'BepInEx/plugins/SerpsMods_Serp'
        $files = @()
        foreach ($row in @(@{Path=$payloadRoot+'/info.json';Text='{"GUID":"SerpsMods_Serp","Version":"99.0.0"}'},@{Path=$payloadRoot+'/Host.dll';Text='new host fixture'})) {
            $path = Join-Path $source ('Payload/'+$row.Path); Write-Fixture $path $row.Text
            $files += [ordered]@{Path=$row.Path;Size=([IO.FileInfo]$path).Length;Sha256=(Get-WorkshopFileHash $path)}
        }
        foreach ($file in $old) { Export-MapEntry -MapPath $map -EntryPath $file.Path -Destination (Join-Path $game $file.Path) }
        $changed = ($old | Where-Object Path -Like '*/APIShared.dll' | Select-Object -First 1)
        if (-not $changed) { $changed = $old[0] }
        if ($variant -eq 'modified') { Write-Fixture (Join-Path $game $changed.Path) 'user changed bytes' }
        if ($variant -eq 'missing') { [IO.File]::Delete((Join-Path $game $changed.Path)) }
        $protected = @('BepInEx/plugins/APIShared_Serp/APIShared.dll','BepInEx/plugins/SerpsModsWorkshopBootstrap_Serp/bootstrap.dll',($payloadRoot+'/Infrastructure/APIShared_Serp/unknown.dll'),($payloadRoot+'/Infrastructure/APIShared_Serp/config/user.json'))
        foreach ($path in $protected) { Write-Fixture (Join-Path $game $path) 'preserve user or separate owner' }
        $legacy = if ($variant -eq 'unproven') { @() } else { @([ordered]@{Version=$uploads[$index].PackVersion;Files=@($old | ForEach-Object { [ordered]@{Path=$_.Path;Size=$_.Size;Sha256=$_.Sha256} })}) }
        $manifest = [ordered]@{SchemaVersion=1;PackageId=$PackGuid;Version='99.0.0';MinimumAPISharedVersion=$version;Roots=@($payloadRoot);Files=$files;Dependencies=@();LegacyReleases=@($legacy)}
        Write-Fixture (Join-Path $source 'apishared-workshop.json') ($manifest | ConvertTo-Json -Depth 12)
        if ($variant -eq 'linked') {
            $linked = [IO.Path]::GetFullPath((Join-Path $game ($payloadRoot+'/Infrastructure/APIShared_Serp')))
            $outside = [IO.Path]::GetFullPath((Join-Path $run 'outside'))
            foreach ($path in @($linked,$outside)) { if (-not $path.StartsWith([IO.Path]::GetFullPath($fixture)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture move escaped test root.' } }
            [IO.Directory]::Move($linked,$outside)
            $null = New-Item -ItemType Junction -Path $linked -Target $outside
        }
        if ($variant -eq 'foreign-owner') {
            $receipt = [ordered]@{WorkshopId='987';PackageId='OtherPackage';Version='1.0.0';Files=@([ordered]@{Path=$changed.Path;Size=$changed.Size;Sha256=$changed.Sha256});ModGuids=@('OtherGuid');Dependencies=@();ManifestHash=('a'*64);TransactionId=('b'*32)}
            Write-Fixture (Join-Path $game '_APIShared/Workshop/Receipts/987.json') ($receipt | ConvertTo-Json -Depth 12)
        }
        if ($variant -in @('linked','foreign-owner')) {
            $rejected=$false; try { $null=$prepare.Invoke($null,[object[]]@('fixture',[string]$game,[ulong]3788821961,[string]$source,$action.PSObject.BaseObject)) } catch { $rejected=$true }
            if (-not $rejected) { throw "Unsafe migration was accepted: $variant" }
            foreach ($path in $protected) { if ([IO.File]::ReadAllText((Join-Path $game $path)) -cne 'preserve user or separate owner') { throw 'Rejected plan changed protected data.' } }
            continue
        }
        $plan = $prepare.Invoke($null,[object[]]@('fixture',[string]$game,[ulong]3788821961,[string]$source,$action.PSObject.BaseObject))
        $expected = if ($variant -eq 'unproven') { 0 } elseif ($variant -in @('modified','missing')) { $old.Count-1 } else { $old.Count }
        if ($plan.RemovedFiles.Count -ne $expected) { throw "Wrong deletion plan: $($uploads[$index].PackVersion) / $variant" }
        # Merely preparing (including declining this plan) must not change installed files.
        foreach ($path in $protected) { if ([IO.File]::ReadAllText((Join-Path $game $path)) -cne 'preserve user or separate owner') { throw 'Planning modified protected data.' } }
        if ($variant -eq 'changed-after-plan') {
            Write-Fixture (Join-Path $game $changed.Path) 'changed after consent'
            $rejected=$false; try { $null=$stage.Invoke($null,[object[]]@($plan.PSObject.BaseObject)) } catch { $rejected=$true }
            if (-not $rejected) { throw 'Changed target was staged for deletion.' }
            continue
        }
        $transaction = $stage.Invoke($null,[object[]]@($plan.PSObject.BaseObject))
        $start = [Diagnostics.ProcessStartInfo]::new('powershell.exe')
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        foreach ($arg in @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $script:Root 'APIShared/Workshop/workshop-updater.ps1'),'-GameRoot',$game,'-Transaction',$transaction,'-OperationsHash',(Get-WorkshopFileHash (Join-Path $transaction 'operations.tsv')))) { [void]$start.ArgumentList.Add($arg) }
        if ($variant -eq 'external') { $start.Environment['SCDEModManagerLaunchId']='fixture' }
        $process = [Diagnostics.Process]::Start($start)
        try {
            $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(30000)) { throw 'Historical updater did not finish.' }
            $expectedExit = if ($variant -eq 'external') { 1 } else { 0 }
            if ($process.ExitCode -ne $expectedExit) { throw ($stdout.Result+$stderr.Result) }
        } finally { $process.Dispose() }
        foreach ($file in $old) {
            $keep = $variant -in @('unproven','external') -or ($variant -eq 'modified' -and $file.Path -eq $changed.Path)
            if ([IO.File]::Exists((Join-Path $game $file.Path)) -ne $keep) { throw "Incorrect historical file result: $variant / $($file.Path)" }
        }
        foreach ($path in $protected) { if ([IO.File]::ReadAllText((Join-Path $game $path)) -cne 'preserve user or separate owner') { throw 'Updater changed protected data.' } }
        $receipt = Join-Path $game '_APIShared/Workshop/Receipts/3788821961.json'
        if ([IO.File]::Exists($receipt) -eq ($variant -eq 'external')) { throw 'Incorrect receipt outcome.' }
        $checked++
    }
    Write-Host ('PASS historical map '+$uploads[$index].PackVersion+' ('+$old.Count+' embedded files)')
}
Write-Host ('PASS: '+$uploads.Count+' confirmed maps, '+$checked+' applied/refused updater cases, hash preservation, missing/unproven evidence, post-plan mutation and external manager. Fixtures: '+$fixture)
