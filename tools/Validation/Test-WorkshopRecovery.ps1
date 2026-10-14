param([string]$Bash = '', [switch]$RequireBash)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = Join-Path $root ('.local/recovery-tests/' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$utf8 = [Text.UTF8Encoding]::new($false)
function Write-Text([string]$Path,[string]$Value) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path,$Value,$utf8)
}
function Hash([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { return 'missing' }
    $stream=[IO.File]::OpenRead($Path); $sha=[Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose(); $stream.Dispose() }
}
function Quote([string]$Value) { if ($Value.Contains('"') -or $Value.EndsWith('\')) { throw 'Unsafe fixture argument.' }; '"'+$Value+'"' }
function New-Case([string]$Name) {
    $script:game = Join-Path $fixture ($Name+'/game')
    $script:state = Join-Path $game '_APIShared/Workshop/Transactions'
    [void][IO.Directory]::CreateDirectory($state)
    $script:target = 'BepInEx/plugins/Example/Example.dll'
    $script:receipt = '_APIShared/Workshop/Receipts/123.json'
}
function New-Old([string]$Target = $script:target,[string]$Receipt = $script:receipt,[switch]$MissingBackup,[switch]$NoBegin) {
    $dir = Join-Path $state ([guid]::NewGuid().ToString('N'))
    Write-Text (Join-Path $dir 'Backup/0') 'original'
    $before = Hash (Join-Path $dir 'Backup/0')
    Write-Text (Join-Path $game $Target) 'partial new bytes'
    $after = Hash (Join-Path $game $Target)
    Write-Text (Join-Path $game $Receipt) 'original receipt'
    Write-Text (Join-Path $dir 'operations.tsv') ("APISHARED-WORKSHOP-1`r`nwrite`t$Target`t0`t$before`t$after`r`nreceipt-write`t$Receipt`t1`tmissing`t$after`r`n")
    Write-Text (Join-Path $dir 'journal.log') $(if ($NoBegin) { "applying`r`n" } else { "applying`r`nbegin`t0`r`nfailure Interrupted fixture`r`n" })
    Write-Text (Join-Path $dir 'cancel.flag') 'old cancelled handoff'
    if ($MissingBackup) { [IO.File]::Delete((Join-Path $dir 'Backup/0')) }
    return $dir
}
function Reference([string]$Dir) { [IO.Path]::GetFileName($Dir)+"`t"+(Hash (Join-Path $Dir 'operations.tsv'))+"`t"+(Hash (Join-Path $Dir 'journal.log')) }
function New-Request([string[]]$Old) {
    $request = Join-Path $state ([guid]::NewGuid().ToString('N'))
    Write-Text (Join-Path $request 'operations.tsv') ("APISHARED-WORKSHOP-RECOVERY-1`r`nstop`r`n" + (($Old | ForEach-Object { Reference $_ }) -join "`r`n") + "`r`n")
    return $request
}
function New-Repair([string[]]$Old) {
    $dir = Join-Path $state ([guid]::NewGuid().ToString('N'))
    Write-Text (Join-Path $dir 'New/0') 'complete latest package'
    Write-Text (Join-Path $dir 'New/1') 'latest receipt'
    $before = Hash (Join-Path $game $target); $receiptBefore = Hash (Join-Path $game $receipt)
    $after = Hash (Join-Path $dir 'New/0'); $receiptAfter = Hash (Join-Path $dir 'New/1')
    Write-Text (Join-Path $dir 'operations.tsv') ("APISHARED-WORKSHOP-2`r`nwrite`t$target`t0`t$before`t$after`r`nreceipt-write`t$receipt`t1`t$receiptBefore`t$receiptAfter`r`n")
    Write-Text (Join-Path $dir 'supersedes.tsv') ((($Old | ForEach-Object { Reference $_ }) -join "`r`n")+"`r`n")
    Write-Text (Join-Path $dir 'supersedes.sha256') (Hash (Join-Path $dir 'supersedes.tsv'))
    return $dir
}
function Run([string]$Dir,[int]$Expected=0,[switch]$Recover,[switch]$Replacement,[switch]$External,[switch]$WrongHash) {
    $hash = Hash (Join-Path $Dir 'operations.tsv')
    if ($script:backend -eq 'windows') {
        $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File '+(Quote (Join-Path $root 'Workshop/workshop-updater.ps1'))+' -GameRoot '+(Quote $game)+' -Transaction '+(Quote $Dir)+' -OperationsHash '+$hash
        if ($Recover) { $arguments += ' -Recover' }
        if ($Replacement) { $arguments += ' -SupersedesHash '+$(if ($WrongHash) { 'a'*64 } else { Hash (Join-Path $Dir 'supersedes.tsv') }) }
        $start = [Diagnostics.ProcessStartInfo]::new('powershell.exe',$arguments)
    } else {
        # Git Bash is useful for these file/protocol fixtures. Full Proton process
        # detection, Wine Z: mapping and Steam restart still need a live Linux test.
        $hostGame = '/'+$game.Substring(0,1).ToLowerInvariant()+$game.Substring(2).Replace('\','/'); $hostDir = '/'+$Dir.Substring(0,1).ToLowerInvariant()+$Dir.Substring(2).Replace('\','/')
        $arguments = (Quote ((Join-Path $root 'Workshop/workshop-updater.sh').Replace('\','/')))+' '+(Quote $hostGame)+' '+(Quote $hostDir)+' '+$hash+' '+$(if ($Recover) { '--recover' } else { '--fixture' })
        if ($Replacement) { $arguments += ' '+$(if ($WrongHash) { 'a'*64 } else { Hash (Join-Path $Dir 'supersedes.tsv') }) }
        $start = [Diagnostics.ProcessStartInfo]::new($Bash,$arguments)
    }
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    if ($External) { $start.EnvironmentVariables['SCDEModManagerLaunchId']='fixture' }
    $p = [Diagnostics.Process]::Start($start)
    try {
        $stdout=$p.StandardOutput.ReadToEndAsync(); $stderr=$p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit(30000)) { $p.Kill(); throw "Updater fixture timeout: $Dir" }
        if ($p.ExitCode -ne $Expected) { throw "Updater $script:backend expected $Expected, got $($p.ExitCode): $($stdout.Result) $($stderr.Result); evidence=$Dir" }
    } finally { $p.Dispose() }
}
function Assert-Text([string]$Path,[string]$Expected) { if ([IO.File]::ReadAllText($Path) -cne $Expected) { throw "Unexpected fixture bytes: $Path" } }
if ($RequireBash -and -not $Bash) { throw 'Bash backend required but unavailable.' }
if ($Bash) {
    & $Bash -c 'command -v flock >/dev/null && command -v sync >/dev/null'
    if ($LASTEXITCODE -ne 0) { throw 'Bash backend requires real flock and coreutils; Git Bash alone is insufficient.' }
} else { Write-Host 'SKIP: host Bash runtime fixtures require an explicitly supplied environment with flock/coreutils. Windows fixtures still run.' }
$backends = @('windows'); if ($Bash) { $backends += 'bash' }
foreach ($backend in $backends) {
    New-Case "$backend-single"; $old=New-Old; $request=New-Request @($old)
    Run $request; Assert-Text (Join-Path $game $target) 'original'
    if ([IO.File]::ReadAllLines((Join-Path $old 'journal.log')) -notcontains 'restored') { throw 'Rollback did not finish.' }
    if (-not [IO.Directory]::Exists((Join-Path $request 'RecoveryBackup'))) { throw 'Interrupted bytes were not saved.' }

    New-Case "$backend-independent"; $one=New-Old; $two=New-Old -Target 'BepInEx/plugins/Second/Second.dll' -Receipt '_APIShared/Workshop/Receipts/456.json'
    Run (New-Request @($one,$two)); Assert-Text (Join-Path $game $target) 'original'; Assert-Text (Join-Path $game 'BepInEx/plugins/Second/Second.dll') 'original'

    New-Case "$backend-overlap"; $one=New-Old; $two=New-Old
    Run (New-Request @($one,$two)) -Expected 1; Assert-Text (Join-Path $game $target) 'partial new bytes'

    New-Case "$backend-corrupt-backup"; $one=New-Old; $two=New-Old -Target 'BepInEx/plugins/Second/Second.dll' -Receipt '_APIShared/Workshop/Receipts/456.json' -MissingBackup
    Run (New-Request @($one,$two)) -Expected 1; Assert-Text (Join-Path $game $target) 'partial new bytes'

    New-Case "$backend-already-restored"; $old=New-Old -MissingBackup; Write-Text (Join-Path $game $target) 'original'
    Run (New-Request @($old)); Assert-Text (Join-Path $game $target) 'original'

    New-Case "$backend-before-first-write"; $old=New-Old -NoBegin -MissingBackup
    Run (New-Request @($old)); Assert-Text (Join-Path $game $target) 'partial new bytes'

    New-Case "$backend-staging"; $old=New-Old; [void][IO.Directory]::CreateDirectory((Join-Path $game '_SE/.staging'))
    Run (New-Request @($old)) -Expected 1; Assert-Text (Join-Path $game $target) 'partial new bytes'
    [IO.Directory]::Delete((Join-Path $game '_SE/.staging'),$false)
    Run (New-Request @($old)); Assert-Text (Join-Path $game $target) 'original'

    New-Case "$backend-external"; $old=New-Old
    Run (New-Request @($old)) -Expected 1 -External; Assert-Text (Join-Path $game $target) 'partial new bytes'

    New-Case "$backend-changed-journal"; $old=New-Old; $request=New-Request @($old)
    [IO.File]::AppendAllText((Join-Path $old 'journal.log'),"failure changed`r`n")
    Run $request -Expected 1; Assert-Text (Join-Path $game $target) 'partial new bytes'

    New-Case "$backend-repair"; $old=New-Old -MissingBackup
    Write-Text (Join-Path $game 'BepInEx/plugins/Example/config/user.json') 'user settings'
    Write-Text (Join-Path $game 'BepInEx/plugins/Example/unknown.txt') 'unknown'
    $repair=New-Repair @($old); Run $repair -Replacement
    Assert-Text (Join-Path $game $target) 'complete latest package'; Assert-Text (Join-Path $game $receipt) 'latest receipt'
    Assert-Text (Join-Path $repair 'Backup/0') 'partial new bytes'
    Assert-Text (Join-Path $game 'BepInEx/plugins/Example/config/user.json') 'user settings'
    Assert-Text (Join-Path $game 'BepInEx/plugins/Example/unknown.txt') 'unknown'
    # A fresh normal update is no longer blocked by the superseded failure.
    $next=New-Repair @(); [IO.File]::Delete((Join-Path $next 'supersedes.tsv')); [IO.File]::Delete((Join-Path $next 'supersedes.sha256'))
    Run $next

    New-Case "$backend-unconfirmed-repair"; $old=New-Old -MissingBackup; $repair=New-Repair @($old)
    Run $repair -Expected 1 -Replacement -WrongHash; Assert-Text (Join-Path $game $target) 'partial new bytes'

    New-Case "$backend-manual-legacy"; $old=New-Old
    Run $old -Recover -Expected 2; Assert-Text (Join-Path $game $target) 'original'

    if ($backend -eq 'windows') {
        New-Case "$backend-locked"; $old=New-Old; $request=New-Request @($old)
        $held=[IO.File]::Open((Join-Path $game $target),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
        try { Run $request -Expected 1 } finally { $held.Dispose() }
        Assert-Text (Join-Path $game $target) 'partial new bytes'
        Run (New-Request @($old)); Assert-Text (Join-Path $game $target) 'original'
    }

    New-Case "$backend-cancelled-request"; $old=New-Old; $request=New-Request @($old)
    Write-Text (Join-Path $request 'cancel.flag') 'cancelled new request'
    Run $request -Expected 1; Assert-Text (Join-Path $game $target) 'partial new bytes'
    Run (New-Request @($old)); Assert-Text (Join-Path $game $target) 'original'

    New-Case "$backend-repair-failure-retry"; $old=New-Old -MissingBackup; $repair=New-Repair @($old)
    Write-Text (Join-Path $repair 'New/0') 'damaged download'
    Run $repair -Expected 1 -Replacement; Assert-Text (Join-Path $game $target) 'partial new bytes'
    if ([IO.File]::ReadAllLines((Join-Path $old 'journal.log')) -contains 'restored') { throw 'Failed repair suppressed the old failure.' }
    Run (New-Repair @($old)) -Replacement; Assert-Text (Join-Path $game $target) 'complete latest package'
    Write-Host "PASS: $backend recovery protocol, full preflight, independent/overlapping transactions, cancelled handoff, repair, subsequent update and legacy rollback."
}
Write-Host "Recovery fixture evidence retained: $fixture"
