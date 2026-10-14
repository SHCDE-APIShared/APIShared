param([Parameter(Mandatory=$true)][string]$GameRoot)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'tools/BuildProof.ps1')
. (Join-Path $root 'tools/WorkshopPackage.ps1')
$proof=Assert-ApiBuildProof $root
$GameRoot=[IO.Path]::GetFullPath($GameRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if([IO.Path]::GetFullPath($proof.GameDir).TrimEnd([IO.Path]::DirectorySeparatorChar) -cne $GameRoot){throw 'Use the installation checked by the current build proof.'}
if($env:SCDEModManagerLaunchId){throw 'Externally managed installations are excluded.'}
if(Get-Process -Name 'Stronghold Crusader Definitive Edition' -ErrorAction SilentlyContinue){throw 'Close the game before arming this test.'}
$installed=Join-Path $GameRoot 'BepInEx/plugins/APIShared_Serp'
$package=Join-Path $root 'BepInEx/plugins/APIShared_Serp'
foreach($file in @('APIShared.dll','Workshop/workshop-updater.ps1','Workshop/workshop-maintenance.ps1')){
    if((Get-ApiFileHash (Join-Path $installed $file)) -cne (Get-ApiFileHash (Join-Path $package $file))){throw ('Install the reviewed build through build.bat first: '+$file)}
}
$id=[guid]::NewGuid().ToString('N')
$evidence=Join-Path $root ('.local/live-restart-tests/'+$id);$scripts=Join-Path $evidence 'scripts'
$state=Join-Path $GameRoot '_APIShared/Workshop';$transactions=Join-Path $state 'Transactions';$transaction=Join-Path $transactions $id
$probeDirectory=Join-Path $GameRoot 'BepInEx/plugins/APISharedCoordinationProbe'
$probe=Join-Path $probeDirectory 'probe.txt';$testReceipt=Join-Path $state 'Receipts/18446744073709551614.json'
foreach($path in @($GameRoot,$state,$transactions,$transaction,$probeDirectory,$testReceipt)){Assert-ApiWorkshopUnlinkedPath $path}
if(Test-Path -LiteralPath $probeDirectory){throw 'Probe directory already exists; preserve it and inspect the previous test first.'}
if(Test-Path -LiteralPath $testReceipt){throw 'The reserved test receipt is already present; refusing to touch it.'}
if(Test-Path -LiteralPath $transactions){
    foreach($directory in Get-ChildItem -LiteralPath $transactions -Directory){
        $journal=Join-Path $directory.FullName 'journal.log'
        if(Test-Path -LiteralPath $journal){$lines=[IO.File]::ReadAllLines($journal);if($lines -ccontains 'applying' -and $lines -cnotcontains 'committed' -and $lines -cnotcontains 'restored'){throw ('Resolve existing interrupted history before this live test: '+$directory.FullName)}}
    }
}
[void][IO.Directory]::CreateDirectory($scripts);[void][IO.Directory]::CreateDirectory((Join-Path $transaction 'New'))
$utf8=[Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $transaction 'New/0'),('Owned APIShared live restart probe '+$id),$utf8)
$probeHash=Get-ApiFileHash (Join-Path $transaction 'New/0')
# The receipt operation deletes an explicitly verified absent, unused test ID.
# It never creates a fake installation receipt that discovery could interpret.
$operations="APISHARED-WORKSHOP-2`r`nwrite`tBepInEx/plugins/APISharedCoordinationProbe/probe.txt`t0`tmissing`t$probeHash`r`nreceipt-delete`t_APIShared/Workshop/Receipts/18446744073709551614.json`t1`tmissing`tmissing`r`n"
[IO.File]::WriteAllText((Join-Path $transaction 'operations.tsv'),$operations,$utf8)
$hash=Get-ApiFileHash (Join-Path $transaction 'operations.tsv')
[IO.File]::WriteAllText((Join-Path $transaction 'operations.sha256'),$hash,$utf8)
[IO.File]::WriteAllText((Join-Path $transaction 'diagnostics.txt'),'Owned interactive restart probe. No mod DLL or existing receipt is changed.',$utf8)
Copy-Item -LiteralPath (Join-Path $installed 'Workshop/workshop-updater.ps1'),(Join-Path $installed 'Workshop/workshop-maintenance.ps1') -Destination $scripts
$hook=@'
$script:liveOriginalWait=${function:Wait-GameExit}
function Wait-GameExit {
    if(-not $script:liveInjected -and $script:activityPhase -eq 'apply' -and $row.Ordinal -eq '0'){
        $script:liveInjected=$true
        [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'live-armed.flag'),'ready for an actual Steam game start')
        $watch=[Diagnostics.Stopwatch]::StartNew()
        while(-not (Game-Running)){
            if($watch.ElapsedMilliseconds -gt 90000){$script:keepInterrupted=$true;throw 'Live test cancelled: no game started within 90 seconds.'}
            Start-Sleep -Milliseconds 100
        }
    }
    & $script:liveOriginalWait
}
'@
[IO.File]::AppendAllText((Join-Path $scripts 'workshop-maintenance.ps1'),("`r`n"+$hook+"`r`n"),$utf8)
function Quote([string]$Value){if($Value.Contains('"') -or $Value.EndsWith('\')){throw 'Unsupported test argument'};'"'+$Value+'"'}
$info=[Diagnostics.ProcessStartInfo]::new('powershell.exe',('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File '+(Quote (Join-Path $scripts 'workshop-updater.ps1'))+' -GameRoot '+(Quote $GameRoot)+' -Transaction '+(Quote $transaction)+' -OperationsHash '+$hash+' -RestartSteam'))
$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
$process=[Diagnostics.Process]::Start($info);$output=$process.StandardOutput.ReadToEndAsync();$errors=$process.StandardError.ReadToEndAsync()
try{
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while(-not [IO.File]::Exists((Join-Path $scripts 'live-armed.flag'))){
        if($process.HasExited){throw "Live updater did not arm: $($output.Result) $($errors.Result)"}
        if($watch.ElapsedMilliseconds -gt 10000){throw 'Live updater did not reach the armed checkpoint'}
        Start-Sleep -Milliseconds 50
    }
    Write-Host 'JETZT das Spiel normal über Steam starten. Im Hinweis zum laufenden Update OK drücken.'
    Write-Host 'Dieser erste Start soll sich beenden; danach den automatischen Neustart abwarten.'
    Write-Host ('Transaktion: '+$transaction)
    if(-not $process.WaitForExit(225000)){throw 'Live updater timeout; only the owned test updater will be stopped.'}
    [IO.File]::WriteAllText((Join-Path $evidence 'updater-output.log'),($output.Result+"`r`n"+$errors.Result),$utf8)
    if($process.ExitCode -ne 0){throw ('Live updater failed; keep evidence at '+$evidence)}
    $history=[IO.File]::ReadAllLines((Join-Path $transaction 'journal.log'))
    if($history -cnotcontains 'committed' -or -not @($history | Where-Object {$_ -like 'pause *'}).Count -or -not @($history | Where-Object {$_ -like 'resumed *'}).Count){throw 'The live test did not establish a complete pause/resume/commit sequence.'}
    if([IO.File]::Exists($testReceipt)){throw 'Unexpected test receipt appeared; preserve evidence.'}
    $log=Join-Path $GameRoot 'BepInEx/LogOutput.log'
    $lines=if(Test-Path -LiteralPath $log){
        # The automatic second start may already have reopened the append log.
        $logStream=[IO.File]::Open($log,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        try{$logReader=[IO.StreamReader]::new($logStream);try{$logReader.ReadToEnd() -split '\r?\n'}finally{$logReader.Dispose()}}finally{$logStream.Dispose()}
    }else{@()}
    $quit=@($lines | Where-Object {$_ -like '*Workshop quit reason=previous confirmed updater still active*' -and $_ -like ('*transaction='+$id+';*')})
    [IO.File]::WriteAllLines((Join-Path $evidence 'quit-evidence.log'),[string[]]$quit,$utf8)
    if($quit.Count -ne 1){throw 'Exactly one runtime-authorized quit was not found; keep the game log for review.'}
    if((Get-ApiFileHash $probe) -cne $probeHash){throw 'Probe contents changed; preserve the file for review.'}
    Assert-ApiWorkshopUnlinkedPath $probeDirectory
    [IO.File]::Delete($probe)
    if([IO.Directory]::GetFileSystemEntries($probeDirectory).Length -eq 0){[IO.Directory]::Delete($probeDirectory)}
    Write-Host 'PASS: actual runtime logged one authorized quit; updater paused, resumed and committed. Observe the automatic Steam restart and confirm that the second start stays open.'
    Write-Host ('Evidence retained: '+$evidence)
}finally{
    if(-not $process.HasExited){$process.Kill();[void]$process.WaitForExit(3000)}
    $process.Dispose()
}
