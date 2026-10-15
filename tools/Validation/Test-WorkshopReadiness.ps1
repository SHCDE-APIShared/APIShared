param([Parameter(Mandatory)][string]$ApiAssembly,[switch]$ExpectLegacyFailure,[switch]$SkipTimeout)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture=Join-Path $root ('.local/readiness-tests/'+[guid]::NewGuid().ToString('N'))
$assets=Join-Path $fixture 'assembly/Workshop'
[void][IO.Directory]::CreateDirectory($assets)
$utf8=[Text.UTF8Encoding]::new($false)
$copy=Join-Path $fixture 'assembly/APIShared.dll'
Copy-Item -LiteralPath $ApiAssembly -Destination $copy
$writer=@'
param([string]$GameRoot,[string]$Transaction,[string]$OperationsHash,[switch]$RestartSteam)
$ErrorActionPreference='Stop'
$path=Join-Path $Transaction 'ready.flag'
$mode=[IO.File]::ReadAllText((Join-Path $Transaction 'fixture-mode.txt'))
function Wait-FixtureStop {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while(-not [IO.File]::Exists((Join-Path $Transaction 'fixture-stop.flag')) -and -not [IO.File]::Exists((Join-Path $Transaction 'cancel.flag')) -and $watch.Elapsed.TotalSeconds -lt 30){Start-Sleep -Milliseconds 20}
}
[IO.File]::WriteAllText((Join-Path $Transaction 'fixture-pid.txt'),$PID.ToString())
if($mode -eq 'exited-empty'){exit 7}
if($mode -eq 'exited-valid' -or $mode -eq 'wrong-hash'){
    $value=if($mode -eq 'wrong-hash'){'b'*64}else{$OperationsHash}
    [IO.File]::WriteAllText($path,$value)
    exit 7
}
$share=if($mode -eq 'shared'){[IO.FileShare]::Read}else{[IO.FileShare]::None}
$stream=[IO.FileStream]::new($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,$share)
try {
    $bytes=[Text.Encoding]::UTF8.GetBytes($OperationsHash)
    $stream.Write($bytes,0,16);$stream.Flush()
    [IO.File]::WriteAllText((Join-Path $Transaction 'fixture-partial.flag'),'partial')
    Start-Sleep -Milliseconds 600
    $stream.Write($bytes,16,$bytes.Length-16);$stream.Flush()
    [IO.File]::WriteAllText((Join-Path $Transaction 'fixture-complete.flag'),'complete')
    if($mode -eq 'shared' -or $mode -eq 'timeout'){
        Wait-FixtureStop
    }
} finally {$stream.Dispose()}
if($mode -eq 'exclusive'){Wait-FixtureStop}
'@
$writer=$writer.Replace("`r`n","`n").Replace("`n","`r`n")
[IO.File]::WriteAllText((Join-Path $assets 'workshop-updater.ps1'),$writer,$utf8)
$assembly=[Reflection.Assembly]::LoadFrom($copy)
$type=$assembly.GetType('APIShared.Workshop.WorkshopUpdaterLauncher',$true)
$launch=$type.GetMethod('Launch',[Reflection.BindingFlags]'Static,NonPublic')
Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Threading;
public sealed class WorkshopReadinessFixtureQueue : SynchronizationContext {
    readonly ConcurrentQueue<Action> callbacks = new ConcurrentQueue<Action>();
    public override void Post(SendOrPostCallback callback, object state) { callbacks.Enqueue(() => callback(state)); }
    public void Pump() { Action action; while (callbacks.TryDequeue(out action)) action(); }
}
'@
$cases=if($ExpectLegacyFailure){@('shared','exclusive')}else{@('shared','exclusive','exited-valid','exited-empty','wrong-hash')}
if(-not $ExpectLegacyFailure -and -not $SkipTimeout){$cases+='timeout'}
foreach($mode in $cases){
    $transaction=Join-Path $fixture $mode
    [void][IO.Directory]::CreateDirectory($transaction)
    [IO.File]::WriteAllText((Join-Path $transaction 'operations.tsv'),'synthetic pinned operation data',$utf8)
    [IO.File]::WriteAllText((Join-Path $transaction 'fixture-mode.txt'),$mode,$utf8)
    $previous=[Threading.SynchronizationContext]::Current
    $queue=[WorkshopReadinessFixtureQueue]::new()
    [Threading.SynchronizationContext]::SetSynchronizationContext($queue)
    try {
        $task=$launch.Invoke($null,[object[]]@([string]$transaction,[string]$fixture))
        $watch=[Diagnostics.Stopwatch]::StartNew()
        if($mode -in @('exited-valid','exited-empty','wrong-hash')){
            # Delay the actual launcher's continuation until its owned updater
            # has exited. This makes a matching-but-dead handoff deterministic.
            $pidPath=Join-Path $transaction 'fixture-pid.txt'
            while(-not [IO.File]::Exists($pidPath) -and $watch.Elapsed.TotalSeconds -lt 5){Start-Sleep -Milliseconds 20}
            $stream=[IO.FileStream]::new($pidPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
            $reader=[IO.StreamReader]::new($stream)
            try{$ownedPid=[int]$reader.ReadToEnd()}finally{$reader.Dispose()}
            $owned=$null
            try{$owned=[Diagnostics.Process]::GetProcessById($ownedPid)}catch [ArgumentException]{}
            if($owned){try{if(-not $owned.WaitForExit(5000)){throw 'Owned updater did not exit.'}}finally{$owned.Dispose()}}
        }
        while(-not $task.IsCompleted -and $watch.Elapsed.TotalSeconds -lt 25){$queue.Pump();Start-Sleep -Milliseconds 20}
        if(-not $task.IsCompleted){throw 'The actual launcher exceeded its readiness deadline.'}
        if($ExpectLegacyFailure){
            $failure=if($task.IsFaulted){$task.Exception.InnerException}else{$null}
            if($null -eq $failure -or $failure -isnot [IO.IOException] -or ($failure.HResult -band 65535) -notin @(32,33)){
                throw 'The released launcher did not reproduce its sharing violation.'
            }
            Write-Host ('PASS: released launcher reproduces readiness sharing failure: '+$mode)
        }elseif($mode -in @('shared','exclusive')){
            $null=$task.GetAwaiter().GetResult()
            if(-not [IO.File]::Exists((Join-Path $transaction 'fixture-complete.flag'))){throw 'A partial readiness hash was accepted.'}
            Write-Host ('PASS: actual launcher confirms the complete hash despite the updater writer: '+$mode)
        }else{
            if(-not $task.IsFaulted -or $task.Exception.InnerException -isnot [IO.IOException]){throw ('Invalid handoff was accepted: '+$mode)}
            if($mode -eq 'timeout'){
                if($watch.Elapsed.TotalSeconds -lt 19 -or $watch.Elapsed.TotalSeconds -gt 24 -or -not [IO.File]::Exists((Join-Path $transaction 'cancel.flag'))){throw 'Timeout did not cancel within the existing readiness budget.'}
                if($task.Exception.InnerException.InnerException -isnot [IO.IOException]){throw 'Timeout lost its last sharing-error diagnostic.'}
                Write-Host ('PASS: exclusive readiness lock times out and cancels after '+[math]::Round($watch.Elapsed.TotalSeconds,2)+' seconds, retaining its I/O cause.')
            }else{
                if($task.Exception.InnerException.Message -notlike '*Exit code: 7*'){throw 'Updater exit diagnostic was lost.'}
                Write-Host ('PASS: failed updater cannot confirm a handoff: '+$mode)
            }
        }
    }finally{[Threading.SynchronizationContext]::SetSynchronizationContext($previous);[IO.File]::WriteAllText((Join-Path $transaction 'fixture-stop.flag'),'stop',$utf8)}
}
Write-Host ('Readiness handoff evidence retained: '+$fixture)
