param([Parameter(Mandatory=$true)][string]$GameRoot, [Parameter(Mandatory=$true)][string]$Transaction, [Parameter(Mandatory=$true)][string]$OperationsHash, [switch]$Recover, [switch]$RestartSteam)
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
$lock = $null
try {
    if ($env:SCDEModManagerLaunchId) { throw 'Externally managed installation.' }
    $GameRoot = [IO.Path]::GetFullPath($GameRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    Assert-Unlinked $GameRoot
    $state = Inside $GameRoot '_APIShared/Workshop'
    $transactionRoot = Inside $state 'Transactions'
    $Transaction = [IO.Path]::GetFullPath($Transaction)
    if (-not $Transaction.StartsWith($transactionRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetDirectoryName($Transaction) -ne $transactionRoot -or [IO.Path]::GetFileName($Transaction) -notmatch '^[a-f0-9]{32}$') { throw 'Invalid transaction directory.' }
    Assert-Unlinked $Transaction
    $gameExecutable = Inside $GameRoot 'Stronghold Crusader Definitive Edition.exe'
    $lock = [IO.File]::Open((Inside $state 'apply.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $journalPath = Inside $Transaction 'journal.log'
    $operationsPath = Inside $Transaction 'operations.tsv'
    if ($OperationsHash -notmatch '^[a-f0-9]{64}$' -or (Hash $operationsPath) -ne $OperationsHash) { throw 'Operation list changed.' }
    $lines = [IO.File]::ReadAllLines($operationsPath)
    if ($lines.Length -lt 2 -or $lines[0] -notin @('APISHARED-WORKSHOP-1','APISHARED-WORKSHOP-2')) { throw 'Unknown transaction protocol.' }
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
    foreach ($other in [IO.Directory]::EnumerateDirectories($transactionRoot)) {
        if ($other -eq $Transaction) { continue }
        Assert-Unlinked $other
        $otherJournal = Inside $other 'journal.log'
        if ([IO.File]::Exists($otherJournal)) { $otherLines = [IO.File]::ReadAllLines($otherJournal); if ($otherLines -contains 'applying' -and $otherLines -notcontains 'committed' -and $otherLines -notcontains 'restored') { throw "Recover the unfinished transaction first: $other" } }
    }
    $started = @($prior | Where-Object { $_ -match '^begin\t[0-9]+$' } | ForEach-Object { $_.Split([char]9)[1] })
    if ($started.Count -and -not $Recover) { throw 'Interrupted transaction: run with -Recover to restore its backup.' }
    [IO.File]::WriteAllText((Inside $Transaction 'ready.flag'),$OperationsHash,[Text.UTF8Encoding]::new($false))
    $cancelPath=Inside $Transaction 'cancel.flag'
    while (Game-Running) { if ([IO.File]::Exists($cancelPath)) { throw 'Handoff cancelled; no installation started.' }; Start-Sleep -Seconds 1 }
    if ([IO.File]::Exists($cancelPath)) { throw 'Handoff cancelled; no installation started.' }
    if ([IO.Directory]::Exists((Inside $GameRoot '_SE/.staging'))) { throw 'Script Extender staging takes precedence.' }
    if (-not $Recover) {
        # Full validation and backup precede any change to installed files.
        foreach ($row in $rows) {
            if ((Hash $row.Target) -ne $row.Before -or [IO.Directory]::Exists($row.Target)) { throw "Target changed: $($row.Relative)" }
            if ($row.Before -ne 'missing') { [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($row.Backup)); [IO.File]::Copy($row.Target,$row.Backup,$true); if ((Hash $row.Backup) -ne $row.Before) { throw 'Backup verification failed.' } }
        }
        Journal 'applying'
        try {
            foreach ($row in $rows) {
                if (Game-Running) { throw 'Game restarted during application.' }
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
            Journal 'committed'
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
        } catch { Journal ('failure '+$_.Exception.Message); $Recover = $true }
    }
    if ($Recover) {
        for ($index=$rows.Count-1; $index -ge 0; $index--) {
            $row=$rows[$index]; if ($started -notcontains $row.Ordinal) { continue }
            if (Game-Running) { throw 'Game must remain closed for recovery.' }
            $null = Inside $GameRoot $row.Relative
            if ($row.Before -eq 'missing') { if ([IO.File]::Exists($row.Target)) { [IO.File]::Delete($row.Target) } }
            else { Assert-Unlinked $row.Backup; if ((Hash $row.Backup) -ne $row.Before) { throw 'Recovery backup invalid.' }; [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($row.Target)); $null = Inside $GameRoot $row.Relative; [IO.File]::Copy($row.Backup,$row.Target,$true) }
            if ((Hash $row.Target) -ne $row.Before) { throw 'Recovery verification failed.' }
        }
        Journal 'restored'; exit 2
    }
} catch { if ($journalPath) { Journal ('failure '+$_.Exception.Message) }; Write-Error $_; exit 1 }
finally { if ($lock) { $lock.Dispose() } }
