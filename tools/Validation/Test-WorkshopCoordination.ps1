param([string]$ApiAssembly='')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture=Join-Path $root ('.local/coordination-tests/'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$utf8=[Text.UTF8Encoding]::new($false)
$activityType=if($ApiAssembly){([Reflection.Assembly]::LoadFrom($ApiAssembly)).GetType('APIShared.Workshop.WorkshopUpdaterActivity',$true)}else{$null}
function Text([string]$Path,[string]$Value){[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path));[IO.File]::WriteAllText($Path,$Value,$utf8)}
function Hash([string]$Path){
    if(-not [IO.File]::Exists($Path)){return 'missing'}
    $stream=[IO.File]::OpenRead($Path);$sha=[Security.Cryptography.SHA256]::Create()
    try{[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$stream.Dispose()}
}
function Quote([string]$Value){if($Value.Contains('"') -or $Value.EndsWith('\')){throw 'Unsafe fixture argument'};'"'+$Value+'"'}
$stub=Join-Path $fixture 'FixtureGame.exe'
Add-Type -TypeDefinition 'using System; using System.IO; using System.Threading; public static class WorkshopFixtureGame { public static void Main(string[] args) { while (!File.Exists(args[0])) Thread.Sleep(20); } }' -OutputAssembly $stub -OutputType ConsoleApplication

# Only a private helper copy injects the checkpoint. The full production updater,
# including its real process detector and pause/revalidation code, is unchanged.
$hook=@'
$script:originalWait=${function:Wait-GameExit}
$script:originalMaintenance=${function:Invoke-WorkshopMaintenance}
$script:fixtureOrdinals=@{}
function Start-FixtureGame {
    $info=[Diagnostics.ProcessStartInfo]::new($gameExecutable,('"'+(Join-Path $GameRoot 'fixture-game-exit.flag')+'"'))
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $process=[Diagnostics.Process]::Start($info)
    try{[IO.File]::WriteAllText((Join-Path $GameRoot 'fixture-game.pid'),$process.Id.ToString())}finally{$process.Dispose()}
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Game-Running)){if($watch.ElapsedMilliseconds -gt 3000){throw 'Fixture game was not detected'};Start-Sleep -Milliseconds 20}
}
function Wait-GameExit {
    $repeated='__CASE__' -eq 'two-pauses'
    if($script:activityPhase -eq '__PHASE__' -and (($repeated -and $row.Ordinal -in @('0','1') -and -not $script:fixtureOrdinals.ContainsKey($row.Ordinal)) -or (-not $repeated -and -not $script:injected -and ('__PHASE__' -eq 'restore' -or $row.Ordinal -eq '__ORDINAL__')))){
        if($repeated){
            if(-not $script:injected){$script:pauseMilliseconds=117000}
            $script:fixtureOrdinals[$row.Ordinal]=$true
            [IO.File]::Delete((Join-Path $GameRoot 'fixture-game-exit.flag'))
        }
        $script:injected=$true;Start-FixtureGame
        if('__CASE__' -eq 'timeout'){$script:pauseMilliseconds=117000}
    }
    & $script:originalWait
}
function Invoke-WorkshopMaintenance {
    if('__CASE__' -eq 'completed'){
        Start-FixtureGame
        [IO.File]::WriteAllText((Join-Path $GameRoot 'fixture-completed.flag'),'committed')
        $watch=[Diagnostics.Stopwatch]::StartNew()
        while(-not [IO.File]::Exists((Join-Path $GameRoot 'fixture-game-exit.flag'))){if($watch.ElapsedMilliseconds -gt 10000){throw 'Fixture completion release timed out'};Start-Sleep -Milliseconds 20}
    }
    & $script:originalMaintenance
}
'@
foreach($case in @('backup','first-write','receipt','restore','timeout','two-pauses','changed-target','changed-source','changed-backup','changed-operations','staging','killed','completed')){
    Write-Host "Checking Windows updater coordination: $case"
    $game=Join-Path $fixture ($case+'/game');$scripts=Join-Path $fixture ($case+'/scripts')
    [void][IO.Directory]::CreateDirectory($scripts)
    $state=Join-Path $game '_APIShared/Workshop';$old=Join-Path $state ('Transactions/'+[guid]::NewGuid().ToString('N'))
    $target=Join-Path $game 'BepInEx/plugins/Example/Example.dll';$receipt=Join-Path $state 'Receipts/123.json'
    Text $target 'original';Text (Join-Path $old 'New/0') 'updated';Text (Join-Path $old 'New/1') 'receipt'
    $before=Hash $target;$after=Hash (Join-Path $old 'New/0');$receiptHash=Hash (Join-Path $old 'New/1')
    Text (Join-Path $old 'operations.tsv') ("APISHARED-WORKSHOP-2`r`nwrite`tBepInEx/plugins/Example/Example.dll`t0`t$before`t$after`r`nreceipt-write`t_APIShared/Workshop/Receipts/123.json`t1`tmissing`t$receiptHash`r`n")
    $transaction=$old
    if($case -eq 'restore'){
        Text (Join-Path $old 'Backup/0') 'original';Text $target 'updated';Text (Join-Path $old 'journal.log') "applying`r`nbegin`t0`r`ndone`t0`r`n"
        $transaction=Join-Path $state ('Transactions/'+[guid]::NewGuid().ToString('N'))
        Text (Join-Path $transaction 'operations.tsv') ("APISHARED-WORKSHOP-RECOVERY-1`r`nstop`r`n"+[IO.Path]::GetFileName($old)+"`t"+(Hash (Join-Path $old 'operations.tsv'))+"`t"+(Hash (Join-Path $old 'journal.log'))+"`r`n")
    }
    $phase=if($case -eq 'backup'){'backup'}elseif($case -eq 'restore'){'restore'}else{'apply'}
    $ordinal=if($case -eq 'receipt'){1}else{0}
    Copy-Item -LiteralPath (Join-Path $root 'Workshop/workshop-updater.ps1'),(Join-Path $root 'Workshop/workshop-maintenance.ps1') -Destination $scripts
    $injection=$hook.Replace('__CASE__',$case).Replace('__PHASE__',$phase).Replace('__ORDINAL__',$ordinal.ToString())
    if($case -eq 'completed'){$injection=$injection.Replace("-eq 'apply'","-eq 'never'")}
    [IO.File]::AppendAllText((Join-Path $scripts 'workshop-maintenance.ps1'),("`r`n"+$injection+"`r`n"),$utf8)
    [void][IO.Directory]::CreateDirectory($game);$exe=Join-Path $game 'Stronghold Crusader Definitive Edition.exe';Copy-Item -LiteralPath $stub -Destination $exe
    $info=[Diagnostics.ProcessStartInfo]::new('powershell.exe',('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File '+(Quote (Join-Path $scripts 'workshop-updater.ps1'))+' -GameRoot '+(Quote $game)+' -Transaction '+(Quote $transaction)+' -OperationsHash '+(Hash (Join-Path $transaction 'operations.tsv'))))
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $updater=[Diagnostics.Process]::Start($info);$output=$updater.StandardOutput.ReadToEndAsync();$errors=$updater.StandardError.ReadToEndAsync()
    try{
        $watch=[Diagnostics.Stopwatch]::StartNew();$record=''
        do{
            if($updater.HasExited){throw "Updater exited before checkpoint: $($output.Result) $($errors.Result)"}
            try{$stream=[IO.File]::Open((Join-Path $state 'apply.lock'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite);$reader=[IO.StreamReader]::new($stream);try{$record=$reader.ReadToEnd()}finally{$reader.Dispose()}}catch [IO.IOException]{}
            if($record -match "`tpaused`t" -or ($case -eq 'completed' -and [IO.File]::Exists((Join-Path $game 'fixture-completed.flag')))){break}
            if($watch.ElapsedMilliseconds -gt 15000){throw "Checkpoint timed out: $case"};Start-Sleep -Milliseconds 20
        }while($true)
        Start-Sleep -Milliseconds 100
        if($activityType){
            $activity=$activityType.GetMethod('ReadWindows',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,[object[]]@([string]$game))
            $active=$activityType.GetField('Active',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($activity)
            if($active -ne ($case -ne 'completed')){throw 'Built runtime disagrees with live production updater activity'}
        }
        $blocked=$false
        try{$contender=[IO.File]::Open((Join-Path $state 'apply.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::ReadWrite);$contender.Dispose()}catch [IO.IOException]{$blocked=$true}
        if(-not $blocked){throw 'The active updater did not exclude competing writers'}
        $expected=if($case -in @('receipt','restore','completed')){'updated'}else{'original'}
        if([IO.File]::ReadAllText($target) -cne $expected){throw 'A write happened before the pause checkpoint'}
        if($case -eq 'completed'){
            if($record -notmatch "`tcompleted`t" -or [IO.File]::ReadAllText($receipt) -cne 'receipt'){throw 'Committed update was not marked completed'}
        }elseif([IO.File]::Exists($receipt)){throw 'Receipt committed while the game was running'}
        if($case -eq 'changed-target'){Text $target 'foreign modification'}
        if($case -eq 'changed-source'){Text (Join-Path $old 'New/0') 'changed source'}
        if($case -eq 'changed-backup'){Text (Join-Path $old 'Backup/0') 'changed backup'}
        if($case -eq 'changed-operations'){[IO.File]::AppendAllText((Join-Path $old 'operations.tsv'),"`r`n")}
        if($case -eq 'staging'){[void][IO.Directory]::CreateDirectory((Join-Path $game '_SE/.staging'))}
        if($case -eq 'killed'){$updater.Kill()}
        $firstPid=if($case -eq 'two-pauses'){[IO.File]::ReadAllText((Join-Path $game 'fixture-game.pid'))}else{''}
        if($case -ne 'timeout'){Text (Join-Path $game 'fixture-game-exit.flag') 'exit'}
        if($case -eq 'two-pauses'){
            $watch=[Diagnostics.Stopwatch]::StartNew()
            while([IO.File]::ReadAllText((Join-Path $game 'fixture-game.pid')) -ceq $firstPid -or [IO.File]::ReadAllText($target) -cne 'updated'){
                if($updater.HasExited -or $watch.ElapsedMilliseconds -gt 10000){throw 'Second game restart was not reached'}
                Start-Sleep -Milliseconds 20
            }
            # Keep the second owned game running: the remaining shared budget,
            # not a newly reset two-minute budget, must end this updater.
        }
        if(-not $updater.WaitForExit(15000)){throw 'Updater did not finish after checkpoint release'}
        if($case -in @('timeout','two-pauses','changed-target','changed-source','changed-backup','changed-operations','staging')){
            if($updater.ExitCode -eq 0){throw 'Unsafe resumed update reported success'}
            $expected=if($case -eq 'changed-target'){'foreign modification'}elseif($case -eq 'two-pauses'){'updated'}else{'original'}
            if([IO.File]::ReadAllText($target) -cne $expected -or [IO.File]::Exists($receipt)){throw 'Failed pause validation overwrote targets or committed a receipt'}
            $journal=[IO.File]::ReadAllLines((Join-Path $transaction 'journal.log'))
            if($journal -ccontains 'committed' -or $journal -ccontains 'restored'){throw 'Failed pause validation hid the unresolved transaction'}
        }elseif($case -ne 'killed'){
            if($updater.ExitCode -ne 0){throw "Updater failed: $($output.Result) $($errors.Result)"}
            $expected=if($case -eq 'restore'){'original'}else{'updated'}
            if([IO.File]::ReadAllText($target) -cne $expected){throw 'Resumed updater produced wrong payload'}
            if($case -ne 'restore' -and [IO.File]::ReadAllText($receipt) -cne 'receipt'){throw 'Resumed updater did not commit receipt'}
        }else{
            $probe=[IO.File]::Open((Join-Path $state 'apply.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::ReadWrite);$probe.Dispose()
            if([IO.File]::ReadAllText($target) -cne 'original'){throw 'Killed updater changed a paused target'}
        }
        if($activityType){
            $activity=$activityType.GetMethod('ReadWindows',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,[object[]]@([string]$game))
            if($activityType.GetField('Busy',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($activity)){throw 'Exited updater left a live runtime lock indication'}
        }
        $originalBackup=Join-Path $old 'Backup/0'
        if($case -ne 'changed-backup' -and [IO.File]::Exists($originalBackup) -and (Hash $originalBackup) -cne $before){throw 'Resuming changed the original rollback backup'}
    }finally{
        Text (Join-Path $game 'fixture-game-exit.flag') 'exit'
        if(-not $updater.HasExited){$updater.Kill();[void]$updater.WaitForExit(3000)};$updater.Dispose()
        $pidFile=Join-Path $game 'fixture-game.pid'
        if([IO.File]::Exists($pidFile)){
            try{$owned=[Diagnostics.Process]::GetProcessById([int][IO.File]::ReadAllText($pidFile));try{if(-not $owned.WaitForExit(3000) -and [IO.Path]::GetFullPath($owned.MainModule.FileName) -ceq [IO.Path]::GetFullPath($exe)){$owned.Kill();[void]$owned.WaitForExit(3000)}}finally{$owned.Dispose()}}catch [ArgumentException]{}
        }
    }
}
Write-Host "PASS: real fixture game detection, exclusive writer/readable status, backup/apply/receipt/restore pauses, validation, timeout, process termination and post-commit safety. Evidence: $fixture"
