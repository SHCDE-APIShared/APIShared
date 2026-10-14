param([Parameter(Mandatory=$true)][string]$GameRoot, [string]$Transaction, [string]$OperationsHash, [switch]$Recover, [switch]$RestartSteam, [string]$SupersedesHash = '', [switch]$Maintain)
$ErrorActionPreference = 'Stop'
if ($env:SCDEModManagerLaunchId) { throw 'Externally managed installation; no changes are allowed.' }
# Runs in a separate process. Receipts form the final phase and roll back first.
function Assert-Unlinked([string]$Path) {
    for ($part = [IO.Path]::GetFullPath($Path); $part; $part = [IO.Path]::GetDirectoryName($part)) {
        try { if ([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint) { throw "Linked path: $part" } }
        catch [IO.FileNotFoundException] { }
        catch [IO.DirectoryNotFoundException] { }
    }
}
function Inside([string]$Root, [string]$Relative) {
    if ($Relative -match '[\\:\x00-\x1f\x7f]' -or $Relative -match '(^|/)(\.|\.\.|)(/|$)' -or $Relative -match '[. ](/|$)') { throw 'Invalid relative path.' }
    $path = [IO.Path]::GetFullPath([IO.Path]::Combine($Root,$Relative.Replace('/',[IO.Path]::DirectorySeparatorChar)))
    if (-not $path.StartsWith($Root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Escaping path.' }
    Assert-Unlinked $path
    return $path
}
function Hash([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { return 'missing' }
    $stream=[IO.File]::OpenRead($Path); $sha=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose(); $stream.Dispose() }
}
function Journal([string]$Text) {
    if ($Text.StartsWith('failure ',[StringComparison]::Ordinal)) { $Text = 'failure '+[DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss.fff')+'; step='+$script:step+'; '+$Text.Substring(8) }
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text + [Environment]::NewLine)
    $stream = [IO.FileStream]::new($journalPath,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}
function Game-Running {
    foreach ($process in [Diagnostics.Process]::GetProcessesByName('Stronghold Crusader Definitive Edition')) {
        try { if ([IO.Path]::GetFullPath($process.MainModule.FileName) -eq $gameExecutable) { return $true } }
        catch { if (-not $process.HasExited) { throw 'Cannot inspect a game process; refusing application.' } }
        finally { $process.Dispose() }
    }
    return $false
}
function Publish-Activity([string]$Phase) {
    $script:activityPhase = $Phase
    if (-not $lock) { return }
    $id = if ($Transaction) { [IO.Path]::GetFileName($Transaction) } else { '-' }
    $record = @('APISHARED-WORKSHOP-ACTIVE-1','windows',$PID.ToString(),$script:ownerStart,$script:activityMode,$Phase,$id,$OperationsHash) -join "`t"
    $bytes = [Text.Encoding]::UTF8.GetBytes($record)
    $lock.Position=0; $lock.SetLength(0); $lock.Write($bytes,0,$bytes.Length); $lock.Flush($true)
}
function Wait-GameExit {
    if (-not (Game-Running)) { return }
    $phase = $script:activityPhase; Publish-Activity 'paused'
    Journal ('pause '+[DateTime]::UtcNow.ToString('o')+'; step='+$script:step+'; waiting for game exit')
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (Game-Running) {
        if (($script:pauseMilliseconds+$watch.Elapsed.TotalMilliseconds) -ge 120000) {
            $script:keepInterrupted=$true; throw 'Game exit wait exceeded 120 seconds; keep transaction backups and confirm recovery on the next start.'
        }
        if ($cancelPath -and [IO.File]::Exists($cancelPath)) { $script:keepInterrupted=$true; throw 'Updater handoff cancelled while waiting for game exit.' }
        Start-Sleep -Milliseconds 200
    }
    $script:pauseMilliseconds += $watch.Elapsed.TotalMilliseconds
    $script:keepInterrupted=$true
    if ($cancelPath -and [IO.File]::Exists($cancelPath)) { throw 'Updater handoff cancelled before resuming.' }
    if ([IO.Directory]::Exists((Inside $GameRoot '_SE/.staging'))) { throw 'Script Extender staging takes precedence after the pause; restart and confirm recovery.' }
    if ((Hash $operationsPath) -cne $OperationsHash) { $script:keepInterrupted=$true; throw 'Operations changed during game-exit pause.' }
    if ($SupersedesHash -and (Hash $supersedesPath) -cne $SupersedesHash) { $script:keepInterrupted=$true; throw 'Replacement references changed during game-exit pause.' }
    foreach ($reference in $script:recoveryReferences) { $null=Assert-Reference $reference }
    if ($script:activityMode -eq 'install' -and $phase -ne 'restore') {
        foreach ($row in $rows) {
            $expected=if($phase -eq 'apply' -and $started -contains $row.Ordinal){$row.After}else{$row.Before}
            if((Hash $row.Target) -cne $expected){throw ('Target changed during game-exit pause: '+$row.Relative)}
            if($row.Kind -like '*write' -and (Hash $row.New) -cne $row.After){throw ('Staged source changed during game-exit pause: '+$row.Relative)}
            if($row.Before -ne 'missing' -and ($phase -eq 'apply' -or [IO.File]::Exists($row.Backup)) -and (Hash $row.Backup) -cne $row.Before){throw ('Backup changed during game-exit pause: '+$row.Relative)}
        }
    } elseif ($phase -eq 'restore') {
        foreach($row in $script:restoreSnapshot){
            $current=Hash $row.Target
            if($current -cne $row.Before -and $current -cne $row.Current){throw ('Recovery target changed during game-exit pause: '+$row.Relative)}
            if($current -cne $row.Before -and $row.Before -ne 'missing' -and (Hash $row.Backup) -cne $row.Before){throw ('Recovery backup changed during game-exit pause: '+$row.Relative)}
        }
    }
    $script:keepInterrupted=$false
    Journal ('resumed '+[DateTime]::UtcNow.ToString('o')+'; step='+$script:step)
    Publish-Activity $phase
}
function Read-Journal([string]$Directory) {
    $path = Inside $Directory 'journal.log'
    if ([IO.File]::Exists($path)) { [IO.File]::ReadAllLines($path) }
}
function Read-Reference([string]$Line) {
    $cells = $Line.Split([char]9)
    if ($cells.Length -ne 3 -or $cells[0] -notmatch '^[a-f0-9]{32}$' -or $cells[1] -notmatch '^[a-f0-9]{64}$' -or $cells[2] -notmatch '^[a-f0-9]{64}$') { throw 'Invalid recovery reference.' }
    return ,$cells
}
function Assert-Reference([string[]]$Cells) {
    $directory = Inside $transactionRoot $Cells[0]
    if ((Hash (Inside $directory 'operations.tsv')) -ne $Cells[1] -or (Hash (Inside $directory 'journal.log')) -ne $Cells[2]) { throw "Recovery transaction changed: $directory" }
    return $directory
}
function Superseded([string]$Directory) {
    foreach ($candidate in [IO.Directory]::EnumerateDirectories($transactionRoot)) {
        $file = Inside $candidate 'supersedes.tsv'
        if (@(Read-Journal $candidate) -notcontains 'committed' -or -not [IO.File]::Exists($file)) { continue }
        $pin = Inside $candidate 'supersedes.sha256'
        if (-not [IO.File]::Exists($pin) -or [IO.File]::ReadAllText($pin) -cne (Hash $file)) { throw 'Committed replacement references changed.' }
        foreach ($line in [IO.File]::ReadAllLines($file)) {
            $cells = Read-Reference $line
            if ($cells[0] -eq [IO.Path]::GetFileName($Directory) -and $cells[1] -eq (Hash (Inside $Directory 'operations.tsv')) -and $cells[2] -eq (Hash (Inside $Directory 'journal.log'))) { return $true }
        }
    }
    return $false
}
function Read-RecoveryRows([string]$Directory) {
    $data = [IO.File]::ReadAllLines((Inside $Directory 'operations.tsv'))
    if ($data.Length -lt 2 -or $data[0] -notin @('APISHARED-WORKSHOP-1','APISHARED-WORKSHOP-2')) { throw 'Unknown recovery protocol.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $receipt = $false
    for ($i=1; $i -lt $data.Length; $i++) {
        $r = $data[$i].Split([char]9)
        if ($r.Length -ne 5 -or $r[2] -cne ($i-1).ToString() -or $r[3] -notmatch '^(missing|[a-f0-9]{64})$' -or $r[4] -notmatch '^(missing|[a-f0-9]{64})$' -or -not $seen.Add($r[1])) { throw 'Malformed recovery operation.' }
        if ($r[0] -cin @('receipt-write','receipt-delete')) {
            if ($r[1] -cnotmatch '^_APIShared/Workshop/Receipts/[1-9][0-9]*\.json$' -or ($data[0] -eq 'APISHARED-WORKSHOP-1' -and $i -ne $data.Length-1)) { throw 'Invalid recovery receipt.' }
            $receipt = $true
        } elseif ($receipt -or $r[0] -cnotin @('write','delete') -or $r[1] -cnotmatch '^BepInEx/plugins/[^/]+/.+' -or $r[1] -match '^BepInEx/plugins/(APIShared_Serp|000shcdese)/|/(config|LobbyModSettings)(/|$)') { throw 'Protected recovery target.' }
        if (($r[0] -clike '*write' -and $r[4] -eq 'missing') -or ($r[0] -cnotlike '*write' -and $r[4] -ne 'missing')) { throw 'Invalid recovery after hash.' }
        $null = Inside $GameRoot $r[1]
        Write-Output -NoEnumerate $r
    }
    if (-not $receipt) { throw 'Missing recovery receipt.' }
}
function Get-RestoreRows([string]$Directory) {
    $all = @(Read-RecoveryRows $Directory)
    $begun = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in @(Read-Journal $Directory)) {
        if (-not $line.StartsWith("begin`t",[StringComparison]::Ordinal)) { continue }
        $ordinal = $line.Substring(6)
        if ($ordinal -notmatch '^(0|[1-9][0-9]*)$' -or [long]$ordinal -ge $all.Count) { throw 'Invalid recovery journal ordinal.' }
        [void]$begun.Add($ordinal)
    }
    foreach ($r in $all) {
        if (-not $begun.Contains($r[2])) { continue }
        $target = Inside $GameRoot $r[1]; $backup = Inside $Directory ('Backup/'+$r[2]); $current = Hash $target
        if ([IO.Directory]::Exists($target)) { throw 'Recovery target is a directory.' }
        if ($current -ne $r[3] -and $r[3] -ne 'missing' -and (Hash $backup) -ne $r[3]) { throw "Recovery backup invalid: $($r[1])" }
        [pscustomobject]@{Target=$target;Backup=$backup;Before=$r[3];Current=$current;Relative=$r[1];Ordinal=$r[2];Directory=$Directory}
    }
}
function Restore-Rows($Rows) {
    $script:restoreSnapshot=$Rows
    # Full validation is performed by Get-RestoreRows before the first write.
    for ($i=$Rows.Count-1; $i -ge 0; $i--) {
        $r = $Rows[$i]
        Wait-GameExit
        $null = Inside $GameRoot $r.Relative
        if ((Hash $r.Target) -eq $r.Before) { continue }
        if ((Hash $r.Target) -ne $r.Current) { throw 'Recovery target changed after validation.' }
        if ($r.Before -eq 'missing') { if ([IO.File]::Exists($r.Target)) { [IO.File]::Delete($r.Target) } }
        else {
            Assert-Unlinked $r.Backup
            if ((Hash $r.Backup) -ne $r.Before) { throw 'Recovery backup changed.' }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($r.Target)); [IO.File]::Copy($r.Backup,$r.Target,$true)
        }
        if ((Hash $r.Target) -ne $r.Before) { throw 'Recovery verification failed.' }
    }
}
. (Join-Path $PSScriptRoot 'workshop-maintenance.ps1')
function Completed-Maintenance {
    Publish-Activity 'completed'
    # Cleanup failure is deliberately outside the installation failure contract.
    try { Maintenance-Completed $Transaction } catch { Write-Output ('Workshop maintenance completion warning: '+$_.Exception.Message) }
    try { Invoke-WorkshopMaintenance } catch { Write-Output ('Workshop maintenance warning after completion: '+$_.Exception.Message) }
}
$lock = $null
$script:activityMode='maintenance'; $script:pauseMilliseconds=0; $script:keepInterrupted=$false; $script:recoveryReferences=@()
$ownerProcess=[Diagnostics.Process]::GetCurrentProcess()
try { $script:ownerStart=$ownerProcess.StartTime.ToUniversalTime().Ticks.ToString([Globalization.CultureInfo]::InvariantCulture) } finally { $ownerProcess.Dispose() }
$script:step = 'validation'
try {
    if ($env:SCDEModManagerLaunchId) { throw 'Externally managed installation.' }
    $GameRoot = [IO.Path]::GetFullPath($GameRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    Assert-Unlinked $GameRoot
    $state = Inside $GameRoot '_APIShared/Workshop'
    $transactionRoot = Inside $state 'Transactions'
    if($Maintain -and -not [IO.Directory]::Exists($transactionRoot)){Write-Output 'Workshop maintenance: removed=0; freedBytes=0; deferred=0'; exit 0}
    $lock = [IO.File]::Open((Inside $state 'apply.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
    Publish-Activity 'waiting'
    if($Maintain){Invoke-WorkshopMaintenance; exit 0}
    $Transaction = [IO.Path]::GetFullPath($Transaction)
    if (-not $Transaction.StartsWith($transactionRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetDirectoryName($Transaction) -ne $transactionRoot -or [IO.Path]::GetFileName($Transaction) -notmatch '^[a-f0-9]{32}$') { throw 'Invalid transaction directory.' }
    Assert-Unlinked $Transaction
    $gameExecutable = Inside $GameRoot 'Stronghold Crusader Definitive Edition.exe'
    $journalPath = Inside $Transaction 'journal.log'
    $operationsPath = Inside $Transaction 'operations.tsv'
    if ($OperationsHash -notmatch '^[a-f0-9]{64}$' -or (Hash $operationsPath) -ne $OperationsHash) { throw 'Operation list changed.' }
    $lines = [IO.File]::ReadAllLines($operationsPath)
    if ($lines.Length -ge 3 -and $lines[0] -ceq 'APISHARED-WORKSHOP-RECOVERY-1') {
        $script:activityMode='recover'
        $script:step = 'recovery preflight'
        if ($lines[1] -cnotin @('restart','stop')) { throw 'Invalid recovery request.' }
        $directories = @(); $restore = @(); $targets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $references = @($lines | Select-Object -Skip 2 | ForEach-Object { Read-Reference $_ })
        $script:recoveryReferences=$references
        foreach ($reference in $references) {
            $directory = Assert-Reference $reference
            if (@(Read-Journal $directory) -notcontains 'applying' -or @(Read-Journal $directory) -contains 'committed' -or @(Read-Journal $directory) -contains 'restored') { throw 'Recovery target is no longer interrupted.' }
            foreach ($row in @(Read-RecoveryRows $directory)) { if (-not $targets.Add($row[1])) { throw 'Overlapping transactions require complete repair.' } }
            $restore += @(Get-RestoreRows $directory); $directories += $directory
        }
        [IO.File]::WriteAllText((Inside $Transaction 'ready.flag'),$OperationsHash,[Text.UTF8Encoding]::new($false))
        Publish-Activity 'waiting'
        $script:step = 'recovery waiting for game exit'
        $cancelPath = Inside $Transaction 'cancel.flag'
        Wait-GameExit
        if ([IO.File]::Exists($cancelPath)) { throw 'Recovery handoff cancelled.' }
        if ([IO.Directory]::Exists((Inside $GameRoot '_SE/.staging'))) { throw 'Script Extender staging takes precedence; restart and retry recovery.' }
        $restore = @()
        $script:step = 'recovery final validation and interrupted-file backup'
        foreach ($reference in $references) { $directory = Assert-Reference $reference; $restore += @(Get-RestoreRows $directory) }
        # Preserve the interrupted bytes too. A failed recovery never consumes backups.
        foreach ($r in $restore) {
            Wait-GameExit
            if ($r.Current -eq 'missing' -or $r.Current -eq $r.Before) { continue }
            $saved = Inside $Transaction ('RecoveryBackup/'+[IO.Path]::GetFileName($r.Directory)+'/'+$r.Ordinal)
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved)); [IO.File]::Copy($r.Target,$saved,$false)
            if ((Hash $saved) -ne $r.Current) { throw 'Interrupted-file backup verification failed.' }
        }
        Journal 'recovering'
        Publish-Activity 'restore'
        $script:step = 'restore original files and receipts'
        Restore-Rows $restore
        Publish-Activity 'committing'
        foreach ($directory in $directories) {
            $requestJournal = $journalPath; $journalPath = Inside $directory 'journal.log'
            try { Journal 'restored' } finally { $journalPath = $requestJournal }
        }
        Journal 'committed'
        Completed-Maintenance
        if ($lines[1] -eq 'restart') { $launch=[Diagnostics.ProcessStartInfo]::new('steam://rungameid/3024040'); $launch.UseShellExecute=$true; [void][Diagnostics.Process]::Start($launch) }
        exit 0
    }
    if ($lines.Length -lt 2 -or $lines[0] -notin @('APISHARED-WORKSHOP-1','APISHARED-WORKSHOP-2')) { throw 'Unknown transaction protocol.' }
    $script:activityMode=if($Recover){'recover'}else{'install'}
    $batch = $lines[0] -eq 'APISHARED-WORKSHOP-2'; $receiptPhase = $false
    $rows = @(); $targets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    for ($index = 1; $index -lt $lines.Length; $index++) {
        $cells = $lines[$index].Split([char]9)
        if ($cells.Length -ne 5 -or $cells[2] -ne ($index-1).ToString() -or $cells[3] -notmatch '^(missing|[a-f0-9]{64})$' -or $cells[4] -notmatch '^(missing|[a-f0-9]{64})$') { throw 'Malformed operation.' }
        $kind,$relative,$ordinal,$before,$after = $cells
        $isReceipt = $kind -in @('receipt-write','receipt-delete')
        if ($isReceipt) { if ((-not $batch -and $index -ne $lines.Length-1) -or $relative -notmatch '^_APIShared/Workshop/Receipts/[1-9][0-9]*\.json$') { throw 'Receipt must be in the final phase.' }; $receiptPhase = $true }
        elseif ($kind -notin @('write','delete') -or $relative -notmatch '^BepInEx/plugins/[^/]+/.+' -or $relative -match '^BepInEx/plugins/(APIShared_Serp|000shcdese)/' -or $relative -match '/(config|LobbyModSettings)(/|$)') { throw 'Protected target.' }
        if (-not $isReceipt -and $receiptPhase) { throw 'Payload operations cannot follow receipts.' }
        if ($index -eq $lines.Length-1 -and -not $isReceipt) { throw 'Missing final receipt.' }
        if (-not $targets.Add($relative)) { throw 'Duplicate target.' }
        $target = Inside $GameRoot $relative; $new = Inside $Transaction ('New/'+$ordinal); $backup = Inside $Transaction ('Backup/'+$ordinal)
        if ($kind -like '*write') { if ($after -eq 'missing' -or (-not $Recover -and (Hash $new) -ne $after)) { throw 'Staged file is invalid.' } }
        elseif ($after -ne 'missing') { throw 'Invalid delete.' }
        $rows += [pscustomobject]@{Kind=$kind;Relative=$relative;Target=$target;New=$new;Backup=$backup;Before=$before;After=$after;Ordinal=$ordinal}
    }
    $prior = if ([IO.File]::Exists($journalPath)) { [IO.File]::ReadAllLines($journalPath) } else { @() }
    if ($prior -contains 'committed' -or $prior -contains 'restored') { throw 'Transaction already finished.' }
    $superseding = @{}
    $supersedesPath = Inside $Transaction 'supersedes.tsv'
    if ([IO.File]::Exists($supersedesPath)) {
        if ($SupersedesHash -notmatch '^[a-f0-9]{64}$' -or (Hash $supersedesPath) -ne $SupersedesHash) { throw 'Unconfirmed or changed replacement references.' }
        if ([IO.File]::ReadAllText((Inside $Transaction 'supersedes.sha256')) -cne $SupersedesHash) { throw 'Replacement pin changed.' }
        foreach ($line in [IO.File]::ReadAllLines($supersedesPath)) {
            $ref = Read-Reference $line; $old = Assert-Reference $ref
            foreach ($r in @(Read-RecoveryRows $old)) {
                if ($r[0] -like 'receipt-*') { if ($rows.Relative -notcontains $r[1]) { throw 'Replacement does not cover interrupted receipt.' } }
                else { $prefix = ($r[1].Split('/')[0..2] -join '/')+'/'; if (-not @($rows | Where-Object { $_.Relative.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) }).Count) { throw 'Replacement does not cover interrupted plugin root.' } }
            }
            $superseding[$old] = $true
        }
    } elseif ($SupersedesHash) { throw 'Missing replacement references.' }
    foreach ($other in [IO.Directory]::EnumerateDirectories($transactionRoot)) {
        if ($other -eq $Transaction) { continue }
        Assert-Unlinked $other
        $otherJournal = Inside $other 'journal.log'
        if ([IO.File]::Exists($otherJournal)) {
            $otherLines = [IO.File]::ReadAllLines($otherJournal)
            if ($otherLines -contains 'applying' -and $otherLines -notcontains 'committed' -and $otherLines -notcontains 'restored' -and -not $superseding.ContainsKey($other) -and -not (Superseded $other)) {
                if (-not $Recover) { throw "Recover the unfinished transaction first: $other" }
                foreach ($r in @(Read-RecoveryRows $other)) { if ($targets.Contains($r[1])) { throw 'Overlapping transactions require complete repair.' } }
            }
        }
    }
    $started = @($prior | Where-Object { $_ -match '^begin\t[0-9]+$' } | ForEach-Object { $_.Split([char]9)[1] })
    if ($started.Count -and -not $Recover) { throw 'Interrupted transaction: run with -Recover to restore its backup.' }
    [IO.File]::WriteAllText((Inside $Transaction 'ready.flag'),$OperationsHash,[Text.UTF8Encoding]::new($false))
    Publish-Activity 'waiting'
    # Legacy manual -Recover is an explicit new recovery consent, not the old
    # cancelled installation. Dialog recovery uses a fresh request directory.
    $cancelPath=Inside $Transaction $(if ($Recover) { 'recovery-cancel.flag' } else { 'cancel.flag' })
    Wait-GameExit
    if ([IO.File]::Exists($cancelPath)) { throw 'Handoff cancelled; no installation started.' }
    if ([IO.Directory]::Exists((Inside $GameRoot '_SE/.staging'))) { throw 'Script Extender staging takes precedence.' }
    if (-not $Recover) {
        Publish-Activity 'backup'
        $script:step = 'installation validation and backup'
        if ($SupersedesHash -and (Hash $supersedesPath) -cne $SupersedesHash) { throw 'Replacement references changed before application.' }
        foreach ($line in $(if ($SupersedesHash) { [IO.File]::ReadAllLines($supersedesPath) })) { $null = Assert-Reference (Read-Reference $line) }
        # Full validation and backup precede any change to installed files.
        foreach ($row in $rows) {
            Wait-GameExit
            if ((Hash $row.Target) -ne $row.Before -or [IO.Directory]::Exists($row.Target)) { throw "Target changed: $($row.Relative)" }
            if ($row.Before -ne 'missing') { [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($row.Backup)); [IO.File]::Copy($row.Target,$row.Backup,$true); if ((Hash $row.Backup) -ne $row.Before) { throw 'Backup verification failed.' } }
        }
        Journal 'applying'
        Publish-Activity 'apply'
        $script:step = 'installation writes and receipt commit'
        $committed = $false
        try {
            foreach ($row in $rows) {
                Wait-GameExit
                $null = Inside $GameRoot $row.Relative
                if ((Hash $row.Target) -ne $row.Before) { throw "Target changed: $($row.Relative)" }
                Journal ("begin`t"+$row.Ordinal); $started += $row.Ordinal
                if ($row.Kind -like '*write') {
                    Assert-Unlinked $row.New
                    if ((Hash $row.New) -ne $row.After) { throw 'Staged file changed.' }
                    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($row.Target)); $null = Inside $GameRoot $row.Relative
                    [IO.File]::Copy($row.New,$row.Target,$true)
                }
                elseif ([IO.File]::Exists($row.Target)) { [IO.File]::Delete($row.Target) }
                if ((Hash $row.Target) -ne $row.After) { throw 'Write verification failed.' }
                Journal ("done`t"+$row.Ordinal)
            }
            if ($SupersedesHash -and (Hash $supersedesPath) -cne $SupersedesHash) { throw 'Replacement references changed before commit.' }
            Publish-Activity 'committing'
            Journal 'committed'
            $committed = $true
        } catch {
            if ($script:keepInterrupted -or (Game-Running)) { throw }
            Journal ('failure '+$_.Exception.Message); $Recover = $true
        }
        # Commit is the irreversible success boundary. Maintenance, empty-folder
        # cleanup and Steam restart can never enter the installation rollback.
        if ($committed) {
            Completed-Maintenance
            try {
            foreach ($row in $rows | Where-Object { $_.Kind -eq 'delete' }) {
                $directory = [IO.Path]::GetDirectoryName($row.Target)
                while ($directory -and $directory.StartsWith((Inside $GameRoot 'BepInEx/plugins')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
                    Assert-Unlinked $directory
                    if (-not [IO.Directory]::Exists($directory) -or @([IO.Directory]::EnumerateFileSystemEntries($directory)).Count) { break }
                    [IO.Directory]::Delete($directory,$false); $directory = [IO.Path]::GetDirectoryName($directory)
                }
            }
            } catch { Journal ('cleanup-warning '+$_.Exception.Message) }
            if ($RestartSteam) {
                try { $launch=[Diagnostics.ProcessStartInfo]::new('steam://rungameid/3024040'); $launch.UseShellExecute=$true; [void][Diagnostics.Process]::Start($launch) }
                catch { Journal ('restart-warning '+$_.Exception.Message) }
            }
            exit 0
        }
    }
    if ($Recover) {
        Publish-Activity 'restore'
        $script:step = 'rollback preflight and restore'
        $restore = @(Get-RestoreRows $Transaction)
        Restore-Rows $restore
        Publish-Activity 'committing'
        Journal 'restored'; Completed-Maintenance; exit 2
    }
} catch { if ($journalPath) { Journal ('failure '+$_.Exception.Message) }; Write-Error $_; exit 1 }
finally { if ($lock) { try { $lock.SetLength(0); $lock.Flush($true) } finally { $lock.Dispose() } } }
