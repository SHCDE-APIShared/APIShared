# Dot-sourced by the updater. The caller owns apply.lock for the whole scan and
# deletion pass. No game, receipt, publishing or upload target is ever deleted.
function Maintenance-Files([string]$Directory,[string]$Prefix='') {
    Assert-Unlinked $Directory
    foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($Directory)) {
        Assert-Unlinked $path
        $relative = $Prefix+[IO.Path]::GetFileName($path)
        if ([IO.Directory]::Exists($path)) {
            if ($relative -notmatch '^(New|Backup|RecoveryBackup)(/[a-f0-9]{32})?$') { throw "Unexpected maintenance directory: $path" }
            Maintenance-Files $path ($relative+'/')
        } else { $relative }
    }
}
function Maintenance-Allowed([string]$Relative) {
    return $Relative -cmatch '^(operations\.tsv|operations\.sha256|diagnostics\.txt|journal\.log|ready\.flag|cancel\.flag|recovery-cancel\.flag|supersedes\.tsv|supersedes\.sha256|completed\.utc|New/(0|[1-9][0-9]*)|Backup/(0|[1-9][0-9]*)|RecoveryBackup/[a-f0-9]{32}/(0|[1-9][0-9]*))$'
}
function Maintenance-Completed([string]$Directory) {
    # Optional, immutable sidecar: never append to a hashed recovery journal.
    $path = Inside $Directory 'completed.utc'
    if (-not [IO.File]::Exists($path)) {
        $value = [DateTime]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",[Globalization.CultureInfo]::InvariantCulture)
        [IO.File]::WriteAllText($path,$value,[Text.UTF8Encoding]::new($false))
    }
}
function Maintenance-Scan {
    $nodes = @{}
    foreach ($directory in [IO.Directory]::EnumerateDirectories($transactionRoot)) {
        $id = [IO.Path]::GetFileName($directory)
        $node = [pscustomobject]@{Id=$id;Directory=$directory;Valid=$false;Terminal=$false;Resolved=$false;Request=$false;Items=@();Refs=@();Files=@();Keep=$false;Time=[DateTime]::MinValue;Reason='unknown transaction'}
        $nodes[$id]=$node
        try {
            if ($id -cnotmatch '^[a-f0-9]{32}$') { throw 'Unknown transaction directory name.' }
            $node.Files=@(Maintenance-Files $directory)
            foreach ($file in $node.Files) { if (-not (Maintenance-Allowed $file)) { throw "Unexpected transaction file: $file" } }
            $operations=Inside $directory 'operations.tsv'; $journal=Inside $directory 'journal.log'
            if (-not [IO.File]::Exists($operations) -or -not [IO.File]::Exists($journal)) { throw 'Incomplete transaction metadata.' }
            $lines=[IO.File]::ReadAllLines($operations); $history=@(Read-Journal $directory)
            $node.Terminal=$history -ccontains 'committed' -or $history -ccontains 'restored'
            $node.Resolved=$node.Terminal
            if ([IO.File]::Exists((Inside $directory 'operations.sha256')) -and [IO.File]::ReadAllText((Inside $directory 'operations.sha256')) -cne (Hash $operations)) { throw 'Operation hash pin changed.' }
            if ($lines.Count -ge 3 -and $lines[0] -ceq 'APISHARED-WORKSHOP-RECOVERY-1') {
                if ($lines[1] -cnotin @('restart','stop')) { throw 'Invalid recovery disposition.' }
                $node.Request=$true
                $node.Refs=@($lines | Select-Object -Skip 2 | ForEach-Object { Read-Reference $_ })
                foreach ($file in $node.Files) {
                    if ($file -cmatch '^(New|Backup)/') { throw 'Unexpected recovery payload.' }
                    if ($file -cmatch '^RecoveryBackup/([^/]+)/' -and $matches[1] -cnotin @($node.Refs | ForEach-Object { $_[0] })) { throw 'Unowned recovery backup.' }
                }
            } else {
                $rows=@(Read-RecoveryRows $directory)
                $node.Items=@($rows | Where-Object { $_[0] -clike 'receipt-*' } | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_[1]) })
                foreach ($line in $history) {
                    if ($line.StartsWith("begin`t",[StringComparison]::Ordinal) -and ($line.Substring(6) -notmatch '^(0|[1-9][0-9]*)$' -or [long]$line.Substring(6) -ge $rows.Count)) { throw 'Invalid journal ordinal.' }
                }
                foreach ($file in $node.Files) {
                    if ($file -cmatch '^(New|Backup)/([0-9]+)$') {
                        $kind=$matches[1]; $ordinal=[long]$matches[2]
                        if ($ordinal -ge $rows.Count) { throw 'Unowned payload ordinal.' }
                        $expected=if($kind -ceq 'New'){$rows[$ordinal][4]}else{$rows[$ordinal][3]}
                        if ($expected -eq 'missing' -or (Hash (Inside $directory $file)) -cne $expected) { throw "Changed transaction payload: $file" }
                    } elseif ($file -cmatch '^RecoveryBackup/') { throw 'Unexpected installation recovery backup.' }
                }
                if ([IO.File]::Exists((Inside $directory 'supersedes.tsv'))) {
                    $referenceFile=Inside $directory 'supersedes.tsv'
                    if ([IO.File]::ReadAllText((Inside $directory 'supersedes.sha256')) -cne (Hash $referenceFile)) { throw 'Changed replacement pin.' }
                    $node.Refs=@([IO.File]::ReadAllLines($referenceFile) | ForEach-Object { Read-Reference $_ })
                } elseif ([IO.File]::Exists((Inside $directory 'supersedes.sha256'))) { throw 'Missing replacement references.' }
            }
            $completion=Inside $directory 'completed.utc'
            if ([IO.File]::Exists($completion)) {
                $value=[IO.File]::ReadAllText($completion)
                $node.Time=[DateTime]::ParseExact($value,"yyyy-MM-dd'T'HH:mm:ss'Z'",[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
            } else { $node.Time=[IO.File]::GetLastWriteTimeUtc($journal) }
            $node.Valid=$true; $node.Reason=''
        } catch { $node.Keep=$true; $node.Reason=$_.Exception.Message }
    }
    # Only a validated committed replacement resolves its pinned predecessors.
    foreach ($node in @($nodes.Values)) {
        if (-not $node.Valid -or $node.Request -or @(Read-Journal $node.Directory) -cnotcontains 'committed') { continue }
        foreach ($reference in $node.Refs) {
            if (-not $nodes.ContainsKey($reference[0])) { continue }
            $old=$nodes[$reference[0]]
            if ($reference[1] -ceq (Hash (Inside $old.Directory 'operations.tsv')) -and $reference[2] -ceq (Hash (Inside $old.Directory 'journal.log'))) { $old.Resolved=$true }
        }
    }
    foreach ($node in @($nodes.Values)) {
        if ($node.Valid -and $node.Request -and -not $node.Terminal) {
            # An unsuccessful request may be retired only after every original
            # was conclusively resolved. Missing originals alone prove nothing.
            $all=$node.Refs.Count -gt 0
            foreach ($r in $node.Refs) { if (-not $nodes.ContainsKey($r[0]) -or -not $nodes[$r[0]].Valid -or -not $nodes[$r[0]].Resolved) { $all=$false } }
            $node.Resolved=$all
        }
        if (-not $node.Valid -or -not $node.Resolved) { $node.Keep=$true }
    }
    $byItem=@{}
    foreach ($node in @($nodes.Values)) {
        if (-not $node.Valid -or $node.Request -or -not $node.Terminal) { continue }
        foreach ($item in $node.Items) { if(-not $byItem.ContainsKey($item)){$byItem[$item]=@()}; $byItem[$item]+=$node }
    }
    foreach ($item in $byItem.Keys) {
        foreach ($node in @($byItem[$item] | Sort-Object @{Expression='Time';Descending=$true},@{Expression='Id';Descending=$true} | Select-Object -First 2)) { $node.Keep=$true }
    }
    # Retained requests protect originals; retained originals protect their proof
    # and any interrupted-byte snapshots. Iterate because repairs can be chained.
    do {
        $changed=$false
        foreach ($node in @($nodes.Values)) {
            foreach ($r in $node.Refs) {
                if (-not $nodes.ContainsKey($r[0])) { continue }
                $old=$nodes[$r[0]]
                if (($old.Keep -and -not $node.Keep) -or ($node.Request -and $node.Keep -and -not $old.Keep)) {
                    if($node.Request -and $node.Keep){$old.Keep=$true}else{$node.Keep=$true}; $changed=$true
                }
            }
        }
    } while($changed)
    return $nodes
}
function Maintenance-Digest([string]$Text) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
}
function Maintenance-SavePlan($Actions) {
    $lines=[Collections.Generic.List[string]]::new()
    foreach($action in $Actions) {
        $lines.Add("D`t"+$action.Id+"`t"+$action.Mode)
        if($action.Mode -ceq 'all'){foreach($reference in $action.Refs){$lines.Add("R`t"+$action.Id+"`t"+$reference[0])}}
        foreach($file in $action.Files) { $lines.Add("F`t"+$action.Id+"`t"+$file+"`t"+(Hash (Inside (Inside $transactionRoot $action.Id) $file))) }
    }
    $payload=($lines -join "`n")+"`n"
    $text="APISHARED-WORKSHOP-CLEANUP-1`n"+(Maintenance-Digest $payload)+"`n"+$payload
    $temporary=Inside $state 'cleanup.pending.tmp'; $pending=Inside $state 'cleanup.pending.tsv'
    $stream=[IO.File]::Open($temporary,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $bytes=[Text.Encoding]::UTF8.GetBytes($text); $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::Move($temporary,$pending)
}
function Maintenance-Resume {
    $pending=Inside $state 'cleanup.pending.tsv'
    if(-not [IO.File]::Exists($pending)){return}
    $text=[IO.File]::ReadAllText($pending); $parts=$text.Split(@("`n"),3,[StringSplitOptions]::None)
    if($parts.Length -ne 3 -or $parts[0] -cne 'APISHARED-WORKSHOP-CLEANUP-1' -or $parts[1] -cne (Maintenance-Digest $parts[2])){throw 'Invalid pending cleanup checksum.'}
    $actions=[Collections.Generic.List[object]]::new(); $ids=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $current=$null
    foreach($line in $parts[2].Split([char]10)) {
        if(-not $line){continue}; $r=$line.Split([char]9)
        if($r[0] -ceq 'D' -and $r.Length -eq 3 -and $r[1] -cmatch '^[a-f0-9]{32}$' -and $r[2] -cin @('all','new') -and $ids.Add($r[1])) {
            $current=[pscustomobject]@{Id=$r[1];Mode=$r[2];Files=@{};Refs=@()}; $actions.Add($current)
        } elseif($r[0] -ceq 'R' -and $r.Length -eq 3 -and $current -and $r[1] -ceq $current.Id -and $current.Mode -ceq 'all' -and $r[2] -cmatch '^[a-f0-9]{32}$' -and $r[2] -cnotin $current.Refs){$current.Refs+=,$r[2]
        } elseif($r[0] -ceq 'F' -and $r.Length -eq 4 -and $current -and $r[1] -ceq $current.Id -and (Maintenance-Allowed $r[2]) -and $r[3] -cmatch '^[a-f0-9]{64}$' -and -not $current.Files.ContainsKey($r[2]) -and ($current.Mode -ceq 'all' -or $r[2].StartsWith('New/',[StringComparison]::Ordinal))) { $current.Files[$r[2]]=$r[3] }
        else {throw 'Invalid pending cleanup action.'}
    }
    $remaining=$false
    foreach($action in $actions) {
        $directory=Inside $transactionRoot $action.Id
        if(-not [IO.Directory]::Exists($directory)){continue}
        try {
            $files=@(Maintenance-Files $directory)
            # Full deletion requires an exact remaining subset of the approved
            # immutable inventory. Unknown/newly changed content cancels deletion.
            foreach($file in $files) {
                if(-not (Maintenance-Allowed $file)){throw "Unexpected cleanup content: $directory/$file"}
                if($action.Mode -ceq 'new' -and -not $file.StartsWith('New/',[StringComparison]::Ordinal)){continue}
                if(-not $action.Files.ContainsKey($file) -or (Hash (Inside $directory $file)) -cne $action.Files[$file]){throw "Cleanup inventory changed: $directory/$file"}
            }
            if($action.Mode -ceq 'all') {
                foreach($predecessor in $action.Refs){if([IO.Directory]::Exists((Inside $transactionRoot $predecessor))){throw 'Cleanup proof still has an approved predecessor.'}}
                # A proof is never removed while even a partially deleted source
                # directory remains. The plan lists sources before their proof.
                $reference=Inside $directory 'supersedes.tsv'
                if([IO.File]::Exists($reference)) { foreach($line in [IO.File]::ReadAllLines($reference)){ $r=Read-Reference $line; if([IO.Directory]::Exists((Inside $transactionRoot $r[0]))){throw 'Cleanup proof still has a predecessor.'} } }
            }
            # Remove the approved terminal/superseded journal before operations
            # metadata: a crash must never leave an 'applying' journal whose
            # operations hash no longer matches its retained replacement proof.
            foreach($file in @($files | Sort-Object @{Expression={if($_ -ceq 'journal.log'){0}else{1}}},@{Expression={$_}})) {
                if(-not $action.Files.ContainsKey($file)){continue}
                $path=Inside $directory $file
                if((Hash $path) -cne $action.Files[$file]){throw 'Cleanup file changed before delete.'}
                $length=([IO.FileInfo]$path).Length; [IO.File]::Delete($path); $script:maintenanceBytes+=$length
            }
            # Delete empty directories only, after rechecking containment/links.
            foreach($child in @([IO.Directory]::EnumerateDirectories($directory))) {
                Assert-Unlinked $child
                foreach($nested in @([IO.Directory]::EnumerateDirectories($child))) { Assert-Unlinked $nested; if(-not @([IO.Directory]::EnumerateFileSystemEntries($nested)).Count){[IO.Directory]::Delete($nested,$false)} }
                if(-not @([IO.Directory]::EnumerateFileSystemEntries($child)).Count){[IO.Directory]::Delete($child,$false)}
            }
            if($action.Mode -ceq 'all') { if(@([IO.Directory]::EnumerateFileSystemEntries($directory)).Count){throw 'Cleanup directory is not empty.'}; [IO.Directory]::Delete($directory,$false); $script:maintenanceRemoved++ }
        } catch { $remaining=$true; $script:maintenanceDeferred++; Write-Output ('Workshop maintenance deferred: '+$directory+'; '+$_.Exception.Message) }
    }
    if(-not $remaining){[IO.File]::Delete($pending)}
}
function Invoke-WorkshopMaintenance {
    $script:maintenanceBytes=[long]0; $script:maintenanceRemoved=0; $script:maintenanceDeferred=0
    try {
        $pending=Inside $state 'cleanup.pending.tsv'
        if([IO.File]::Exists($pending)){Maintenance-Resume}
        if(-not [IO.File]::Exists($pending)) {
            $nodes=Maintenance-Scan; $actions=[Collections.Generic.List[object]]::new(); $visiting=@{}; $visited=@{}
            function Visit-MaintenanceNode($Node) {
                if($visited.ContainsKey($Node.Id)){return}
                if($visiting.ContainsKey($Node.Id)){throw 'Cyclic cleanup references; history retained.'}
                $visiting[$Node.Id]=$true
                foreach($r in $Node.Refs){if($nodes.ContainsKey($r[0]) -and -not $nodes[$r[0]].Keep){Visit-MaintenanceNode $nodes[$r[0]]}}
                $visited[$Node.Id]=$true; $visiting.Remove($Node.Id)
                if(-not $Node.Valid -or -not $Node.Resolved){$script:maintenanceDeferred++; if($Node.Reason){Write-Output ('Workshop maintenance retained: '+$Node.Directory+'; '+$Node.Reason)}; return}
                $mode=if($Node.Keep){'new'}else{'all'}
                $files=@($Node.Files | Where-Object {$mode -ceq 'all' -or $_.StartsWith('New/',[StringComparison]::Ordinal)})
                if($files.Count -or $mode -ceq 'all'){$actions.Add([pscustomobject]@{Id=$Node.Id;Mode=$mode;Files=$files;Refs=$Node.Refs})}
            }
            foreach($node in @($nodes.Values | Sort-Object Id)){Visit-MaintenanceNode $node}
            if($actions.Count){Maintenance-SavePlan $actions; Maintenance-Resume}
        }
    } catch { $script:maintenanceDeferred++; Write-Output ('Workshop maintenance warning: '+$_.Exception.Message) }
    Write-Output ("Workshop maintenance: removed=$script:maintenanceRemoved; freedBytes=$script:maintenanceBytes; deferred=$script:maintenanceDeferred")
}
