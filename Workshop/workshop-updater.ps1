param([Parameter(Mandatory=$true)][string]$GameRoot, [string]$Transaction, [string]$OperationsHash, [switch]$Recover, [switch]$RestartSteam, [string]$SupersedesHash = '', [switch]$Maintain, [switch]$ProgressWindow, [string]$Language = 'en')
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
function Test-TransactionDirectory([string]$Path) {
    Assert-Unlinked $Path
    try {
        if (-not ([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::Directory)) { throw "Expected transaction directory: $Path" }
        return $true
    }
    catch [IO.FileNotFoundException] { return $false }
    catch [IO.DirectoryNotFoundException] { return $false }
}
function Find-TransactionInRoot([string]$RootKind, [string]$Id) {
    $root = $transactionRoots[$RootKind]
    if (-not (Test-TransactionDirectory $root)) { return }
    if ($RootKind -ceq 'short') {
        $candidate = Inside $root $Id
        if (Test-TransactionDirectory $candidate) { return $candidate }
        return
    }
    # Probing a nonexistent legacy path can exceed MAX_PATH even when the
    # compact transaction fits. Validate only actual matching legacy entries.
    foreach ($candidate in [IO.Directory]::EnumerateFileSystemEntries($root)) {
        if ([string]::Equals([IO.Path]::GetFileName($candidate), $Id, [StringComparison]::OrdinalIgnoreCase)) {
            if (Test-TransactionDirectory $candidate) { return $candidate }
            return
        }
    }
}
function Resolve-Transaction([string]$Id) {
    if ($Id -notmatch '^[a-f0-9]{32}$') { throw 'Invalid transaction ID.' }
    $legacy = Find-TransactionInRoot 'legacy' $Id
    $short = Find-TransactionInRoot 'short' $Id
    if ($legacy -and $short) { throw "Duplicate transaction ID: $Id" }
    if ($short) { return $short }
    return $legacy
}
function Get-TransactionDirectories {
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($root in $transactionRoots.Values) {
        if (-not (Test-TransactionDirectory $root)) { continue }
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($root)) {
            $id = [IO.Path]::GetFileName($path)
            if ($id -match '^[a-f0-9]{32}$' -and -not $ids.Add($id)) { throw "Duplicate transaction ID: $id" }
            if ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::Directory) { $path }
        }
    }
}
function Hash([string]$Path) {
    Pump-UpdaterProgress
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
        Pump-UpdaterProgress
        Start-Sleep -Milliseconds 200
    }
    $script:pauseMilliseconds += $watch.Elapsed.TotalMilliseconds
    $script:keepInterrupted=$true
    if ($cancelPath -and [IO.File]::Exists($cancelPath)) { throw 'Updater handoff cancelled before resuming.' }
    if ([IO.Directory]::Exists((Inside $GameRoot '_SE/.staging'))) { throw 'Script Extender staging takes precedence after the pause; restart and confirm recovery.' }
    if ((Hash $operationsPath) -cne $OperationsHash) { $script:keepInterrupted=$true; throw 'Operations changed during game-exit pause.' }
    if ($SupersedesHash -and (Hash $supersedesPath) -cne $SupersedesHash) { $script:keepInterrupted=$true; throw 'Replacement references changed during game-exit pause.' }
    foreach ($reference in $script:recoveryReferences) { $null=Assert-Reference $reference }
    if ($script:resumeState) {
        $resume=$script:resumeState
        if ((Hash (Inside $resume.Directory 'operations.tsv')) -cne $resume.OperationsHash) { throw 'Resumed operations changed during game-exit pause.' }
        $null=Assert-ResumeState $resume.Directory $resume.Rows $resume.Next $resume.Partial
    }
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
    $directory = Resolve-Transaction $Cells[0]
    if (-not $directory -or (Hash (Inside $directory 'operations.tsv')) -ne $Cells[1] -or (Hash (Inside $directory 'journal.log')) -ne $Cells[2]) { throw "Recovery transaction changed: $($Cells[0])" }
    return $directory
}
function Superseded([string]$Directory) {
    foreach ($candidate in @(Get-TransactionDirectories)) {
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
# Timestamped diagnostic line. Readers ignore it: progress, recovery and maintenance
# only interpret begin/done/applying/committed/restored/failure. Never written after a
# commit marker of the same journal, so committed history keeps its final content.
function Journal-Trace([string]$Text) { if ($journalPath) { Journal ('trace '+[DateTime]::UtcNow.ToString('o')+'; '+$Text) } }
function Journal-Original([string]$Path,[string]$Text) { $journalPath=$Path; Journal $Text }
# Optional progress window (-ProgressWindow, Windows only). It tells the player that files
# are being written after the game closed. Presentation only: any failure disables it,
# is journaled as ui-warning and never changes the installation result. The texts are
# ASCII because Windows PowerShell reads this BOM-less script with the ANSI code page.
$script:progress=$null; $script:progressFailed=$false; $script:progressClosing=$false; $script:progressRestoring=$false; $script:resumeState=$null
# Intro names what is installed where, so the player can tell this window from foreign software.
$script:uiText = if ($Language -eq 'de') {
    @{Title='Stronghold Crusader - Workshop-Mods werden installiert'
      Intro='Abonnierte Stronghold Crusader Workshop-Mods werden in den BepInEx-Ordner des Spiels installiert:'
      IntroRestore='Der vorherige Stand der Stronghold Crusader Workshop-Mods im BepInEx-Ordner des Spiels wird wiederhergestellt:'
      Warning='Bitte den PC nicht ausschalten und das Spiel nicht starten, solange dieses Fenster angezeigt wird.'
      Backup='Bisherige Dateien werden gesichert'; Apply='Mod-Dateien werden installiert'; Restore='Der vorherige Stand wird wiederhergestellt'; Finish='Abschluss'}
} else {
    @{Title='Stronghold Crusader - Installing Workshop mods'
      Intro='Subscribed Stronghold Crusader Workshop mods are being installed into the game''s BepInEx folder:'
      IntroRestore='The previous state of the Stronghold Crusader Workshop mods in the game''s BepInEx folder is being restored:'
      Warning='Do not turn off the PC or start the game while this window is shown.'
      Backup='Backing up current files'; Apply='Installing mod files'; Restore='Restoring the previous state'; Finish='Finishing'}
}
function Show-UpdaterProgress([string]$Phase,[int]$Value=-1,[int]$Maximum=0) {
    if (-not $ProgressWindow -or $script:progressFailed) { return }
    try {
        if (-not $script:progress) {
            Add-Type -AssemblyName System.Windows.Forms,System.Drawing
            $form=[Windows.Forms.Form]::new()
            $form.Text=$script:uiText.Title; $form.FormBorderStyle=[Windows.Forms.FormBorderStyle]::FixedDialog
            $form.ControlBox=$false; $form.MaximizeBox=$false; $form.MinimizeBox=$false; $form.ShowInTaskbar=$true; $form.TopMost=$true
            $form.StartPosition=[Windows.Forms.FormStartPosition]::CenterScreen; $form.ClientSize=[Drawing.Size]::new(600,150)
            # Closing would only hide progress while the updater continues; keep it until the end.
            $form.Add_FormClosing({ param($source,$e) if (-not $script:progressClosing) { $e.Cancel=$true } })
            # The label sizes itself to its wrapped text; the bar and window follow it below.
            $label=[Windows.Forms.Label]::new(); $label.AutoSize=$true; $label.MaximumSize=[Drawing.Size]::new(568,0); $label.Location=[Drawing.Point]::new(16,12)
            $bar=[Windows.Forms.ProgressBar]::new(); $bar.Size=[Drawing.Size]::new(568,20)
            $form.Controls.Add($label); $form.Controls.Add($bar)
            $script:progress=[pscustomobject]@{Form=$form;Label=$label;Bar=$bar}
            # A launcher's hidden STARTUPINFO can swallow the first ShowWindow call of this process.
            $form.Show(); $form.Hide(); $form.Show()
        }
        if ($Phase -eq 'Restore') { $script:progressRestoring=$true }
        $text=$script:uiText[$Phase]; if ($Maximum -gt 0 -and $Value -ge 0) { $text+=' ('+$Value+'/'+$Maximum+')' }
        $intro=if ($script:progressRestoring) { $script:uiText.IntroRestore } else { $script:uiText.Intro }
        $line=[Environment]::NewLine
        $script:progress.Label.Text=$intro+$line+[IO.Path]::Combine($GameRoot,'BepInEx','plugins')+$line+$line+$script:uiText.Warning+$line+$line+$text
        $bar=$script:progress.Bar
        $top=$script:progress.Label.Bottom+10
        if ($bar.Top -ne $top) { $bar.Location=[Drawing.Point]::new(16,$top); $script:progress.Form.ClientSize=[Drawing.Size]::new(600,$top+$bar.Height+14) }
        if ($Maximum -gt 0 -and $Value -ge 0) { $bar.Style=[Windows.Forms.ProgressBarStyle]::Continuous; $bar.Maximum=$Maximum; $bar.Value=[Math]::Min($Value,$Maximum) }
        else { $bar.Style=[Windows.Forms.ProgressBarStyle]::Marquee }
        [Windows.Forms.Application]::DoEvents()
    } catch {
        $script:progressFailed=$true; $message=$_.Exception.Message
        Close-UpdaterProgress
        try { if ($journalPath) { Journal ('ui-warning '+$message) } } catch { }
    }
}
function Pump-UpdaterProgress { if ($script:progress) { try { [Windows.Forms.Application]::DoEvents() } catch { } } }
function Close-UpdaterProgress {
    if (-not $script:progress) { return }
    $script:progressClosing=$true
    try { $script:progress.Form.Close(); $script:progress.Form.Dispose() } catch { }
    $script:progress=$null
}
function Restart-Game {
    Close-UpdaterProgress
    try { $launch=[Diagnostics.ProcessStartInfo]::new('steam://rungameid/3024040'); $launch.UseShellExecute=$true; [void][Diagnostics.Process]::Start($launch) }
    catch { Journal ('restart-warning '+$_.Exception.Message) }
}
function Remove-EmptyDeletedFolders($Rows) {
    try {
        foreach ($row in $Rows | Where-Object { $_.Kind -eq 'delete' }) {
            $directory = [IO.Path]::GetDirectoryName($row.Target)
            while ($directory -and $directory.StartsWith((Inside $GameRoot 'BepInEx/plugins')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
                Assert-Unlinked $directory
                if (-not [IO.Directory]::Exists($directory) -or @([IO.Directory]::EnumerateFileSystemEntries($directory)).Count) { break }
                [IO.Directory]::Delete($directory,$false); $directory = [IO.Path]::GetDirectoryName($directory)
            }
        }
    } catch { Journal ('cleanup-warning '+$_.Exception.Message) }
}
# Resume finishes one interrupted installation with its own staged copies instead of
# restoring it. It is defined only for a sequential journal (begin/done pairs in ordinal
# order, at most one trailing begin) whose completed targets hold their new bytes, whose
# remaining targets still hold their original bytes and whose staged copies and backups
# are intact. The original journal keeps recording begin/done, so a resume that is itself
# interrupted remains recoverable by rollback or another resume.
function Read-ResumePoint([string]$Directory,[int]$Count) {
    $next=0; $pending=-1
    foreach ($line in @(Read-Journal $Directory)) {
        if ($line -cmatch "^(begin|done)`t(0|[1-9][0-9]{0,8})$") {
            $ordinal=[int]$matches[2]
            if ($matches[1] -ceq 'begin') { if ($pending -ge 0 -or $ordinal -ne $next) { throw 'Journal progress is not sequential.' }; $pending=$ordinal }
            else { if ($ordinal -ne $pending) { throw 'Journal progress is not sequential.' }; $pending=-1; $next++ }
        } elseif ($line -cmatch '^(begin|done)(\s|$)') { throw 'Malformed journal progress marker.' }
    }
    if ($next -gt $Count -or $pending -ge $Count) { throw 'Journal progress exceeds the operation list.' }
    return [pscustomobject]@{Next=$next;Pending=$pending}
}
# $Next is the first row not completed; $Partial a begun row that may hold partial bytes
# (-1 for none). Returns the current hash of that row.
function Assert-ResumeState([string]$Directory,$Rows,[int]$Next,[int]$Partial) {
    $partialHash=$null
    for ($i=0; $i -lt $Rows.Count; $i++) {
        $r=$Rows[$i]; $target=Inside $GameRoot $r[1]
        if ([IO.Directory]::Exists($target)) { throw "Resume target is a directory: $($r[1])" }
        $current=Hash $target
        if ($i -lt $Next) { if ($current -cne $r[4]) { throw "Completed target changed: $($r[1])" }; continue }
        if ($i -eq $Partial) { $partialHash=$current } elseif ($current -cne $r[3]) { throw "Target changed: $($r[1])" }
        if ($r[0] -clike '*write' -and (Hash (Inside $Directory ('New/'+$i))) -cne $r[4]) { throw "Staged file missing or changed: $($r[1])" }
        if ($r[3] -cne 'missing' -and (Hash (Inside $Directory ('Backup/'+$i))) -cne $r[3]) { throw "Backup missing or changed: $($r[1])" }
    }
    return $partialHash
}
function Assert-NoOtherUnfinished([string]$Directory) {
    foreach ($other in @(Get-TransactionDirectories)) {
        if ($other -eq $Directory -or $other -eq $Transaction) { continue }
        $history=@(Read-Journal $other)
        if ($history -ccontains 'applying' -and $history -cnotcontains 'committed' -and $history -cnotcontains 'restored' -and -not (Superseded $other)) { throw "Another unfinished transaction requires recovery first: $other" }
    }
}
function Invoke-Resume($References) {
    if ($References.Count -ne 1) { throw 'Resume requires exactly one interrupted transaction.' }
    $reference=$References[0]
    $original=Assert-Reference $reference
    $history=@(Read-Journal $original)
    if ($history -cnotcontains 'applying' -or $history -ccontains 'committed' -or $history -ccontains 'restored') { throw 'Resume target is no longer interrupted.' }
    if ([IO.File]::Exists((Inside $original 'supersedes.tsv'))) { throw 'A complete repair cannot be resumed.' }
    Assert-NoOtherUnfinished $original
    $rows=@(Read-RecoveryRows $original)
    $point=Read-ResumePoint $original $rows.Count
    # The full hash check of targets, staged copies and backups runs after the game exits
    # (below): before readiness it could exceed the launcher's 20-second handoff budget for
    # large packages. The game already ran the same check (WorkshopRecovery.ResumeError).
    [IO.File]::WriteAllText((Inside $Transaction 'ready.flag'),$OperationsHash,[Text.UTF8Encoding]::new($false))
    Publish-Activity 'waiting'
    Journal-Trace ('handoff ready; pid='+$PID+'; mode=recover; disposition=resume; completed='+$point.Next+'/'+$rows.Count)
    $script:step='resume waiting for game exit'
    $cancelPath=Inside $Transaction 'cancel.flag'
    Wait-GameExit
    if ([IO.File]::Exists($cancelPath)) { throw 'Resume handoff cancelled.' }
    if ([IO.Directory]::Exists((Inside $GameRoot '_SE/.staging'))) { throw 'Script Extender staging takes precedence; restart and retry recovery.' }
    Journal-Trace 'game exit confirmed'
    Show-UpdaterProgress 'Apply' $point.Next $rows.Count
    $script:step='resume final validation and interrupted-file backup'
    $original=Assert-Reference $reference
    Assert-NoOtherUnfinished $original
    $point=Read-ResumePoint $original $rows.Count
    $partial=Assert-ResumeState $original $rows $point.Next $point.Pending
    if ($point.Pending -ge 0 -and $partial -cne 'missing' -and $partial -cne $rows[$point.Pending][3] -and $partial -cne $rows[$point.Pending][4]) {
        $saved=Inside $Transaction ('RecoveryBackup/'+[IO.Path]::GetFileName($original)+'/'+$point.Pending)
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved)); [IO.File]::Copy((Inside $GameRoot $rows[$point.Pending][1]),$saved,$false)
        if ((Hash $saved) -cne $partial) { throw 'Interrupted-file backup verification failed.' }
    }
    $originalJournal=Inside $original 'journal.log'
    # The original journal changes from here on, so its pinned reference hash no longer
    # applies; a pause revalidates the progress state against the targets instead.
    $script:recoveryReferences=@()
    $script:resumeState=[pscustomobject]@{Directory=$original;Rows=$rows;OperationsHash=$reference[1];Next=$point.Next;Partial=$point.Pending}
    Journal 'resuming'
    Publish-Activity 'apply'
    $script:step='resume installation writes and receipt commit'
    for ($i=$point.Next; $i -lt $rows.Count; $i++) {
        Show-UpdaterProgress 'Apply' $i $rows.Count
        $script:resumeState.Next=$i
        Wait-GameExit
        $r=$rows[$i]; $target=Inside $GameRoot $r[1]
        if ($i -ne $point.Pending) {
            if ((Hash $target) -cne $r[3]) { throw "Target changed: $($r[1])" }
            Journal-Original $originalJournal ("begin`t"+$i)
        }
        if ($r[0] -clike '*write') {
            $new=Inside $original ('New/'+$i); Assert-Unlinked $new
            if ((Hash $new) -cne $r[4]) { throw 'Staged file changed.' }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)); $null=Inside $GameRoot $r[1]
            [IO.File]::Copy($new,$target,$true)
        } elseif ([IO.File]::Exists($target)) { [IO.File]::Delete($target) }
        if ((Hash $target) -cne $r[4]) { throw 'Write verification failed.' }
        Journal-Original $originalJournal ("done`t"+$i)
        $script:resumeState.Partial=-1
        if (($i+1) % 100 -eq 0) { Journal-Trace ('applied='+($i+1)+'/'+$rows.Count) }
    }
    if ((Hash (Inside $original 'operations.tsv')) -cne $reference[1]) { throw 'Operations changed before commit.' }
    Publish-Activity 'committing'
    Journal-Original $originalJournal 'committed'
    try { Maintenance-Completed $original } catch { Write-Output ('Workshop maintenance completion warning: '+$_.Exception.Message) }
    Journal 'committed'
    $script:resumeState=$null
    Show-UpdaterProgress 'Finish'
    Completed-Maintenance
    Remove-EmptyDeletedFolders @($rows | Where-Object { $_[0] -ceq 'delete' } | ForEach-Object { [pscustomobject]@{Kind='delete';Target=(Inside $GameRoot $_[1])} })
    # The game launcher always passes -RestartSteam; manual and fixture runs do not restart.
    if ($RestartSteam) { Restart-Game }
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
    $transactionRoots = [ordered]@{
        legacy = Inside $state 'Transactions'
        short = Inside $GameRoot '_APIShared/T'
    }
    if ($Maintain) {
        if (-not (Test-TransactionDirectory $transactionRoots.legacy) -and -not (Test-TransactionDirectory $transactionRoots.short) -and -not [IO.File]::Exists((Inside $state 'cleanup.pending.tsv'))) {
            Write-Output 'Workshop maintenance: removed=0; freedBytes=0; deferred=0'; exit 0
        }
    } else {
        $Transaction = [IO.Path]::GetFullPath($Transaction)
        $transactionId = [IO.Path]::GetFileName($Transaction)
        if ((Resolve-Transaction $transactionId) -ne $Transaction) { throw 'Invalid transaction directory.' }
    }
    [void][IO.Directory]::CreateDirectory($state)
    $lock = [IO.File]::Open((Inside $state 'apply.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)
    Publish-Activity 'waiting'
    if($Maintain){Invoke-WorkshopMaintenance; exit 0}
    if ((Resolve-Transaction $transactionId) -ne $Transaction) { throw 'Invalid transaction directory.' }
    $gameExecutable = Inside $GameRoot 'Stronghold Crusader Definitive Edition.exe'
    $journalPath = Inside $Transaction 'journal.log'
    $operationsPath = Inside $Transaction 'operations.tsv'
    if ($OperationsHash -notmatch '^[a-f0-9]{64}$' -or (Hash $operationsPath) -ne $OperationsHash) { throw 'Operation list changed.' }
    $lines = [IO.File]::ReadAllLines($operationsPath)
    if ($lines.Length -ge 3 -and $lines[0] -ceq 'APISHARED-WORKSHOP-RECOVERY-1') {
        $script:activityMode='recover'
        $script:step = 'recovery preflight'
        $null = @(Get-TransactionDirectories)
        if ($lines[1] -cnotin @('restart','stop','resume')) { throw 'Invalid recovery request.' }
        $references = @($lines | Select-Object -Skip 2 | ForEach-Object { Read-Reference $_ })
        $script:recoveryReferences=$references
        if ($lines[1] -ceq 'resume') { Invoke-Resume $references; exit 0 }
        $directories = @(); $restore = @(); $targets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($reference in $references) {
            $directory = Assert-Reference $reference
            if (@(Read-Journal $directory) -notcontains 'applying' -or @(Read-Journal $directory) -contains 'committed' -or @(Read-Journal $directory) -contains 'restored') { throw 'Recovery target is no longer interrupted.' }
            foreach ($row in @(Read-RecoveryRows $directory)) { if (-not $targets.Add($row[1])) { throw 'Overlapping transactions require complete repair.' } }
            $restore += @(Get-RestoreRows $directory); $directories += $directory
        }
        [IO.File]::WriteAllText((Inside $Transaction 'ready.flag'),$OperationsHash,[Text.UTF8Encoding]::new($false))
        Publish-Activity 'waiting'
        Journal-Trace ('handoff ready; pid='+$PID+'; mode=recover; disposition='+$lines[1])
        $script:step = 'recovery waiting for game exit'
        $cancelPath = Inside $Transaction 'cancel.flag'
        Wait-GameExit
        if ([IO.File]::Exists($cancelPath)) { throw 'Recovery handoff cancelled.' }
        if ([IO.Directory]::Exists((Inside $GameRoot '_SE/.staging'))) { throw 'Script Extender staging takes precedence; restart and retry recovery.' }
        Journal-Trace 'game exit confirmed'
        Show-UpdaterProgress 'Restore'
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
        Show-UpdaterProgress 'Finish'
        Completed-Maintenance
        if ($lines[1] -eq 'restart') { Restart-Game }
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
    foreach ($other in @(Get-TransactionDirectories)) {
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
    Journal-Trace ('handoff ready; pid='+$PID+'; mode='+$script:activityMode+'; operations='+$rows.Count)
    # Legacy manual -Recover is an explicit new recovery consent, not the old
    # cancelled installation. Dialog recovery uses a fresh request directory.
    $cancelPath=Inside $Transaction $(if ($Recover) { 'recovery-cancel.flag' } else { 'cancel.flag' })
    Wait-GameExit
    if ([IO.File]::Exists($cancelPath)) { throw 'Handoff cancelled; no installation started.' }
    if ([IO.Directory]::Exists((Inside $GameRoot '_SE/.staging'))) { throw 'Script Extender staging takes precedence.' }
    Journal-Trace 'game exit confirmed'
    if (-not $Recover) {
        Publish-Activity 'backup'
        $script:step = 'installation validation and backup'
        if ($SupersedesHash -and (Hash $supersedesPath) -cne $SupersedesHash) { throw 'Replacement references changed before application.' }
        foreach ($line in $(if ($SupersedesHash) { [IO.File]::ReadAllLines($supersedesPath) })) { $null = Assert-Reference (Read-Reference $line) }
        # Full validation and backup precede any change to installed files.
        $count = 0
        foreach ($row in $rows) {
            Show-UpdaterProgress 'Backup' $count $rows.Count; $count++
            Wait-GameExit
            if ((Hash $row.Target) -ne $row.Before -or [IO.Directory]::Exists($row.Target)) { throw "Target changed: $($row.Relative)" }
            if ($row.Before -ne 'missing') { [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($row.Backup)); [IO.File]::Copy($row.Target,$row.Backup,$true); if ((Hash $row.Backup) -ne $row.Before) { throw 'Backup verification failed.' } }
        }
        Journal-Trace 'backup complete'
        Journal 'applying'
        Publish-Activity 'apply'
        $script:step = 'installation writes and receipt commit'
        $committed = $false
        $count = 0
        try {
            foreach ($row in $rows) {
                Show-UpdaterProgress 'Apply' $count $rows.Count
                if ($count -and $count % 100 -eq 0) { Journal-Trace ('applied='+$count+'/'+$rows.Count) }
                $count++
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
            Show-UpdaterProgress 'Finish'
            Completed-Maintenance
            Remove-EmptyDeletedFolders $rows
            if ($RestartSteam) { Restart-Game }
            exit 0
        }
    }
    if ($Recover) {
        Show-UpdaterProgress 'Restore'
        Publish-Activity 'restore'
        $script:step = 'rollback preflight and restore'
        $restore = @(Get-RestoreRows $Transaction)
        Restore-Rows $restore
        Publish-Activity 'committing'
        Journal 'restored'; Completed-Maintenance; exit 2
    }
} catch { if ($journalPath) { Journal ('failure '+$_.Exception.Message) }; Write-Error $_; exit 1 }
finally { Close-UpdaterProgress; if ($lock) { try { $lock.SetLength(0); $lock.Flush($true) } finally { $lock.Dispose() } } }
