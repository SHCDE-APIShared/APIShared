param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot 'WorkshopTestFixture.Common.ps1')
$fixture=New-WorkshopTestFixture $root 'maintenance'
$fixtureError=$null
try {
$utf8=[Text.UTF8Encoding]::new($false)
function Write-Text([string]$Path,[string]$Value){[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path));[IO.File]::WriteAllText($Path,$Value,$utf8)}
function Hash([string]$Path){$s=[IO.File]::OpenRead($Path);$h=[Security.Cryptography.SHA256]::Create();try{[BitConverter]::ToString($h.ComputeHash($s)).Replace('-','').ToLowerInvariant()}finally{$h.Dispose();$s.Dispose()}}
function Digest([string]$Value){$h=[Security.Cryptography.SHA256]::Create();try{[BitConverter]::ToString($h.ComputeHash($utf8.GetBytes($Value))).Replace('-','').ToLowerInvariant()}finally{$h.Dispose()}}
function New-Case([string]$Name,[switch]$ShortOnly){
    $script:game=Join-Path $fixture ($Name+'/game')
    $script:state=Join-Path $game '_APIShared/Workshop'
    $script:transactions=Join-Path $state 'Transactions'
    $script:shortTransactions=Join-Path $game '_APIShared/T'
    if(-not $ShortOnly){[void][IO.Directory]::CreateDirectory($transactions)}
}
function New-Transaction([int]$Number,[string[]]$Items=@('123'),[switch]$Open,[switch]$Legacy,[switch]$Short){
    $id=$Number.ToString('x32')
    $dir=Join-Path $(if($Short){$shortTransactions}else{$transactions}) $id
    Write-Text (Join-Path $dir 'New/0') ('new '+$Number);Write-Text (Join-Path $dir 'Backup/0') ('old '+$Number)
    $before=Hash (Join-Path $dir 'Backup/0');$after=Hash (Join-Path $dir 'New/0')
    $rows=@("APISHARED-WORKSHOP-2","write`tBepInEx/plugins/Example/Example.dll`t0`t$before`t$after")
    $ordinal=1
    foreach($item in $Items){Write-Text (Join-Path $dir ('New/'+$ordinal)) ('receipt '+$Number);$rh=Hash (Join-Path $dir ('New/'+$ordinal));$rows+="receipt-write`t_APIShared/Workshop/Receipts/$item.json`t$ordinal`tmissing`t$rh";$ordinal++}
    Write-Text (Join-Path $dir 'operations.tsv') (($rows -join "`r`n")+"`r`n")
    Write-Text (Join-Path $dir 'operations.sha256') (Hash (Join-Path $dir 'operations.tsv'))
    Write-Text (Join-Path $dir 'journal.log') $(if($Open){"applying`r`nbegin`t0`r`n"}else{"applying`r`ncommitted`r`n"})
    $when=[DateTime]::SpecifyKind([DateTime]'2026-01-01',[DateTimeKind]::Utc).AddMinutes($Number)
    if($Legacy){[IO.File]::SetLastWriteTimeUtc((Join-Path $dir 'journal.log'),$when)}else{Write-Text (Join-Path $dir 'completed.utc') ($when.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))}
    return $dir
}
function Add-Reference([string]$Replacement,[string]$Original){
    $text=[IO.Path]::GetFileName($Original)+"`t"+(Hash (Join-Path $Original 'operations.tsv'))+"`t"+(Hash (Join-Path $Original 'journal.log'))+"`r`n"
    Write-Text (Join-Path $Replacement 'supersedes.tsv') $text;Write-Text (Join-Path $Replacement 'supersedes.sha256') (Hash (Join-Path $Replacement 'supersedes.tsv'))
}
function Run([int]$Expected=0,[switch]$External){
    $scriptPath=Join-Path $root 'Workshop/workshop-updater.ps1'
    $arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$scriptPath+'" -GameRoot "'+$game+'" -Maintain'
    $start=[Diagnostics.ProcessStartInfo]::new('powershell.exe',$arguments);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    if($External){$start.EnvironmentVariables['SCDEModManagerLaunchId']='fixture'}
    $p=[Diagnostics.Process]::Start($start)
    try{$out=$p.StandardOutput.ReadToEndAsync();$err=$p.StandardError.ReadToEndAsync();if(-not $p.WaitForExit(60000)){$p.Kill();throw 'Maintenance fixture timeout.'};if($p.ExitCode -ne $Expected){throw "Maintenance exit=$($p.ExitCode); $($out.Result) $($err.Result)"};Write-Text (Join-Path (Split-Path $game -Parent) ('result-'+[guid]::NewGuid().ToString('N')+'.txt')) ($out.Result+$err.Result)}finally{$p.Dispose()}
}
function Assert-Exists([string]$Path,[bool]$Expected){if([IO.Directory]::Exists($Path) -ne $Expected){throw "Unexpected directory existence ($Expected): $Path"}}

New-Case 'hundred-updates';$dirs=@(1..100|ForEach-Object{New-Transaction $_ -Legacy:($_ -lt 50) -Short:($_ % 2 -eq 0)})
foreach($path in @('Receipts/123.json','Publishing/Example.json','Uploads/active/Content/payload')){Write-Text (Join-Path $state $path) 'preserve'}
Write-Text (Join-Path $game 'BepInEx/plugins/Example/user.txt') 'game untouched'
Run
if(@([IO.Directory]::EnumerateDirectories($transactions)).Count -ne 1 -or @([IO.Directory]::EnumerateDirectories($shortTransactions)).Count -ne 1){throw '100 mixed-root updates did not retain exactly two transactions.'}
foreach($dir in $dirs[98..99]){Assert-Exists $dir $true;Assert-Exists (Join-Path $dir 'New') $false;if([IO.File]::ReadAllText((Join-Path $dir 'Backup/0')) -notlike 'old *'){throw 'Retained backup lost.'}}
foreach($path in @('Receipts/123.json','Publishing/Example.json','Uploads/active/Content/payload')){if([IO.File]::ReadAllText((Join-Path $state $path)) -cne 'preserve'){throw 'Protected state changed.'}}
if([IO.File]::ReadAllText((Join-Path $game 'BepInEx/plugins/Example/user.txt')) -cne 'game untouched'){throw 'Game file changed.'}
Run
if([IO.File]::Exists((Join-Path $state 'cleanup.pending.tsv'))){throw 'Completed worklist accumulated.'}
Write-Host 'PASS: 100 updates, two retained backups, legacy timestamps, staging removal, repeatability and protected receipts/publishing/uploads/game files.'

New-Case 'batch';$one=New-Transaction 1;$two=New-Transaction 2 @('456');$batch=New-Transaction 3 @('123','456') -Short;$four=New-Transaction 4;$five=New-Transaction 5 -Short
Run;Assert-Exists $one $false;foreach($dir in @($two,$batch,$four,$five)){Assert-Exists $dir $true};Write-Host 'PASS: per-package retention preserves whole mixed batches.'

New-Case 'protected';$open=New-Transaction 1 -Open;$unknown=New-Transaction 2;Write-Text (Join-Path $unknown 'user-notes.txt') 'unknown';$bad=New-Transaction 3;Write-Text (Join-Path $bad 'completed.utc') 'invalid';$broken=New-Transaction 4;Write-Text (Join-Path $broken 'operations.tsv') 'broken';$null=New-Transaction 5;$null=New-Transaction 6
Run;foreach($dir in @($open,$unknown,$bad,$broken)){Assert-Exists $dir $true;Assert-Exists (Join-Path $dir 'New') $true};Write-Host 'PASS: open, unknown, malformed timestamp and corrupted operations remain protected.'

New-Case 'replacement-chain';$old=New-Transaction 1 -Open;$repair=New-Transaction 2;Add-Reference $repair $old;$null=New-Transaction 3;$null=New-Transaction 4
Run;Assert-Exists $old $false;Assert-Exists $repair $false;Write-Host 'PASS: retired failed source is removed before its successful replacement proof.'

New-Case 'locked-predecessor';$old=New-Transaction 1 -Open;$repair=New-Transaction 2 -Short;Add-Reference $repair $old;$null=New-Transaction 3;$null=New-Transaction 4 -Short
$held=[IO.File]::Open((Join-Path $old 'operations.tsv'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try {
    Run;Assert-Exists $old $true;Assert-Exists $repair $true
    $pending=[IO.File]::ReadAllLines((Join-Path $state 'cleanup.pending.tsv'))
    if($pending[0] -cne 'APISHARED-WORKSHOP-CLEANUP-2' -or
        $pending -cnotcontains ("D`t"+[IO.Path]::GetFileName($old)+"`tall`tlegacy") -or
        $pending -cnotcontains ("D`t"+[IO.Path]::GetFileName($repair)+"`tall`tshort")){throw 'Failed deletion did not retain both transaction locations.'}
}finally{$held.Dispose()}
Run;Assert-Exists $old $false;Assert-Exists $repair $false;Write-Host 'PASS: locked predecessor keeps replacement evidence; partial deletion resumes after unlocking.'

New-Case 'pending-dependencies';$old=New-Transaction 1 -Open;$repair=New-Transaction 2;Add-Reference $repair $old
$id=[IO.Path]::GetFileName($repair);$payload="D`t$id`tall`nR`t$id`t"+[IO.Path]::GetFileName($old)+"`n"
foreach($file in @([IO.Directory]::EnumerateFiles($repair,'*',[IO.SearchOption]::AllDirectories))){$relative=$file.Substring($repair.Length+1).Replace('\','/');$payload+="F`t$id`t$relative`t"+(Hash $file)+"`n"}
Write-Text (Join-Path $state 'cleanup.pending.tsv') ("APISHARED-WORKSHOP-CLEANUP-1`n"+(Digest $payload)+"`n"+$payload)
[IO.File]::Delete((Join-Path $repair 'supersedes.tsv'))
Run;Assert-Exists $old $true;Assert-Exists $repair $true;if(-not [IO.File]::Exists((Join-Path $repair 'journal.log'))){throw 'Missing proof metadata bypassed saved dependencies.'}
Write-Host 'PASS: pending dependency pins protect a proof even after partial reference metadata removal.'

New-Case 'legacy-pending-short-alias';$short=New-Transaction 1 -Open -Short
$id=[IO.Path]::GetFileName($short)
$payload="D`t$id`tnew`nF`t$id`tNew/0`t"+(Hash (Join-Path $short 'New/0'))+"`n"
Write-Text (Join-Path $state 'cleanup.pending.tsv') ("APISHARED-WORKSHOP-CLEANUP-1`n"+(Digest $payload)+"`n"+$payload)
Run;Assert-Exists (Join-Path $short 'New') $true
if([IO.File]::Exists((Join-Path $state 'cleanup.pending.tsv'))){throw 'Already absent legacy target did not complete its pending action.'}
Write-Host 'PASS: a v1 cleanup list cannot act on a matching short-path ID.'

New-Case 'pending-after-last-directory' -ShortOnly
$payload="D`t00000000000000000000000000000001`tall`tshort`n"
Write-Text (Join-Path $state 'cleanup.pending.tsv') ("APISHARED-WORKSHOP-CLEANUP-2`n"+(Digest $payload)+"`n"+$payload)
Run
if([IO.File]::Exists((Join-Path $state 'cleanup.pending.tsv'))){throw 'Pending cleanup survived removal of its last transaction root.'}
Write-Host 'PASS: a completed worklist can retire after both transaction roots disappear.'

New-Case 'duplicate-id';$old=New-Transaction 1;$short=New-Transaction 1 -Short
$null=New-Transaction 2;$null=New-Transaction 3 -Short
Run;Assert-Exists (Join-Path $old 'New') $true;Assert-Exists (Join-Path $short 'New') $true
Write-Host 'PASS: ambiguous transaction IDs defer cleanup across both roots.'

# Emulate a process exit after each file deletion, using the actual immutable
# pending protocol. No production fault-injection switches are introduced.
foreach($boundary in 0..7){
    New-Case ('interruption-'+$boundary);$old=New-Transaction 1;$null=New-Transaction 2;$null=New-Transaction 3
    $files=@('Backup/0','New/0','New/1','completed.utc','journal.log','operations.sha256','operations.tsv')
    $payload="D`t"+[IO.Path]::GetFileName($old)+"`tall`n"
    foreach($file in $files){$payload+="F`t"+[IO.Path]::GetFileName($old)+"`t$file`t"+(Hash (Join-Path $old $file))+"`n"}
    Write-Text (Join-Path $state 'cleanup.pending.tsv') ("APISHARED-WORKSHOP-CLEANUP-1`n"+(Digest $payload)+"`n"+$payload)
    for($i=0;$i -lt $boundary;$i++){[IO.File]::Delete((Join-Path $old $files[$i]))}
    Run;Assert-Exists $old $false
}
Write-Host 'PASS: deletion resumes across individual payload and metadata deletion boundaries.'

New-Case 'recovery-snapshots';$old=New-Transaction 1
$request=Join-Path $transactions ((2).ToString('x32'))
$ref=[IO.Path]::GetFileName($old)+"`t"+(Hash (Join-Path $old 'operations.tsv'))+"`t"+(Hash (Join-Path $old 'journal.log'))
Write-Text (Join-Path $request 'operations.tsv') ("APISHARED-WORKSHOP-RECOVERY-1`r`nstop`r`n$ref`r`n")
Write-Text (Join-Path $request 'journal.log') "recovering`r`ncommitted`r`n"
$snapshot=Join-Path $request ('RecoveryBackup/'+[IO.Path]::GetFileName($old)+'/0');Write-Text $snapshot 'interrupted original bytes'
Run;Assert-Exists $old $true;Assert-Exists $request $true;if([IO.File]::ReadAllText($snapshot) -cne 'interrupted original bytes'){throw 'Needed recovery snapshot deleted.'}
$null=New-Transaction 3;$null=New-Transaction 4;Run;Assert-Exists $old $false;Assert-Exists $request $false
Write-Host 'PASS: recovery snapshots follow the retention of their original transaction.'

New-Case 'changed-pending-content';$old=New-Transaction 1;$null=New-Transaction 2;$null=New-Transaction 3
$file='Backup/0';$payload="D`t"+[IO.Path]::GetFileName($old)+"`tnew`nF`t"+[IO.Path]::GetFileName($old)+"`tNew/0`t"+(Hash (Join-Path $old 'New/0'))+"`n"
Write-Text (Join-Path $state 'cleanup.pending.tsv') ("APISHARED-WORKSHOP-CLEANUP-1`n"+(Digest $payload)+"`n"+$payload)
Write-Text (Join-Path $old 'New/0') 'changed after approval';Run;Assert-Exists $old $true;if([IO.File]::ReadAllText((Join-Path $old 'New/0')) -cne 'changed after approval'){throw 'Changed cleanup target deleted.'}
Write-Host 'PASS: changed pending cleanup targets are preserved.'

New-Case 'corrupt-pending';$old=New-Transaction 1;$null=New-Transaction 2;$null=New-Transaction 3
Write-Text (Join-Path $state 'cleanup.pending.tsv') "APISHARED-WORKSHOP-CLEANUP-1`ninvalid-checksum`nD`tbad`tall`n"
Run;Assert-Exists $old $true;Assert-Exists (Join-Path $old 'New') $true
Write-Host 'PASS: corrupted pending worklist cannot authorize deletion.'

New-Case 'updater-lock';$old=New-Transaction 1;$null=New-Transaction 2;$null=New-Transaction 3
$held=[IO.File]::Open((Join-Path $state 'apply.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{Run -Expected 1;Assert-Exists $old $true}finally{$held.Dispose()};Run;Assert-Exists $old $false
New-Case 'external';$old=New-Transaction 1;$null=New-Transaction 2;$null=New-Transaction 3;Run -Expected 1 -External;Assert-Exists $old $true
Write-Host 'PASS: concurrent updater and external manager prevent cleanup.'

New-Case 'junction';$old=New-Transaction 1;$null=New-Transaction 2;$null=New-Transaction 3
$outside=Join-Path (Split-Path $game -Parent) 'outside';[void][IO.Directory]::CreateDirectory($outside);Write-Text (Join-Path $outside '0') 'outside bytes'
$link=Join-Path $old 'RecoveryBackup';$null=New-Item -ItemType Junction -Path $link -Target $outside
Run;Assert-Exists $old $true;if([IO.File]::ReadAllText((Join-Path $outside '0')) -cne 'outside bytes'){throw 'Cleanup followed a junction.'}
Write-Host 'PASS: linked content is retained without touching its target.'

# The fixture substitutes only the maintenance callback. Production updater code
# must keep both the new payload and final receipt even if that callback throws.
New-Case 'post-commit-failure';$staged=New-Transaction 1 -Open
[IO.File]::Delete((Join-Path $staged 'journal.log'));[IO.File]::Delete((Join-Path $staged 'completed.utc'))
Write-Text (Join-Path $game 'BepInEx/plugins/Example/Example.dll') 'old 1'
$scripts=Join-Path (Split-Path $game -Parent) 'scripts';[void][IO.Directory]::CreateDirectory($scripts)
Copy-Item -LiteralPath (Join-Path $root 'Workshop/workshop-updater.ps1'),(Join-Path $root 'Workshop/workshop-maintenance.ps1') -Destination $scripts
[IO.File]::AppendAllText((Join-Path $scripts 'workshop-maintenance.ps1'),"`r`nfunction Invoke-WorkshopMaintenance { throw 'isolated unexpected maintenance failure' }`r`n")
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $scripts 'workshop-updater.ps1') -GameRoot $game -Transaction $staged -OperationsHash (Hash (Join-Path $staged 'operations.tsv'))
if($LASTEXITCODE -ne 0 -or [IO.File]::ReadAllText((Join-Path $game 'BepInEx/plugins/Example/Example.dll')) -cne 'new 1' -or [IO.File]::ReadAllText((Join-Path $game '_APIShared/Workshop/Receipts/123.json')) -cne 'receipt 1'){throw 'Post-commit maintenance error rolled back a successful installation.'}
$history=[IO.File]::ReadAllLines((Join-Path $staged 'journal.log'))
if($history -cnotcontains 'committed' -or $history -ccontains 'restored'){throw 'Post-commit maintenance error changed the terminal outcome.'}
Write-Host 'PASS: an unexpected post-commit maintenance exception cannot roll back payload or receipt.'

# The legacy transaction directory itself would exceed MAX_PATH, while the
# longest transaction file here fits at the compact path. After testing a
# fresh installation, keep an empty legacy root to exercise lookup on update.
New-Case ('x' * (194-$fixture.Length-6)) -ShortOnly
$target='BepInEx/plugins/Example/Example.dll';$receipt='_APIShared/Workshop/Receipts/123.json'
$before='missing';$receiptBefore='missing'
foreach($operation in @('first install','next update')){
    $id=[guid]::NewGuid().ToString('N')
    $staged=Join-Path $shortTransactions $id
    if(([IO.Path]::Combine($transactions,$id)).Length -le 259 -or
        (Join-Path $staged 'operations.sha256').Length -gt 259){throw 'Fixture does not straddle the classic Windows path limit.'}
    Write-Text (Join-Path $staged 'New/0') $operation
    Write-Text (Join-Path $staged 'New/1') ($operation+' receipt')
    $after=Hash (Join-Path $staged 'New/0');$receiptHash=Hash (Join-Path $staged 'New/1')
    Write-Text (Join-Path $staged 'operations.tsv') "APISHARED-WORKSHOP-1`r`nwrite`t$target`t0`t$before`t$after`r`nreceipt-write`t$receipt`t1`t$receiptBefore`t$receiptHash`r`n"
    Write-Text (Join-Path $staged 'operations.sha256') (Hash (Join-Path $staged 'operations.tsv'))
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $root 'Workshop/workshop-updater.ps1') -GameRoot $game -Transaction $staged -OperationsHash (Hash (Join-Path $staged 'operations.tsv'))
    if($LASTEXITCODE -ne 0 -or (Hash (Join-Path $game $target)) -cne $after -or (Hash (Join-Path $game $receipt)) -cne $receiptHash){throw "Deep short-path operation failed: $operation"}
    if($operation -ceq 'first install'){
        Assert-Exists $transactions $false
        if(-not [IO.File]::Exists((Join-Path $state 'apply.lock'))){throw 'Short-path installation did not use the shared lock.'}
        [void][IO.Directory]::CreateDirectory($transactions)
        $before=$after;$receiptBefore=$receiptHash
    }
}
if(@([IO.Directory]::EnumerateFileSystemEntries($transactions)).Count){throw 'Short-path update wrote to the legacy directory.'}
Write-Host 'PASS: a deep Windows game root uses the short path for first install and update with an empty legacy root.'
Write-Host "Maintenance fixture completed: $fixture"

} catch {$fixtureError=$_;throw} finally {
    Complete-WorkshopTestFixture $root 'maintenance' $fixture (-not $fixtureError) ([string]$fixtureError)
}
