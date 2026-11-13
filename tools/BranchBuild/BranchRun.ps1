# Standalone committed SDK runs retain their current source root because BuildProof validates it.
function Set-ApiBranchRunPhase($Run, [string]$Phase, [string]$Detail = '') {
    $Run.State.Phase = $Phase; $Run.State.Detail = $Detail
    $Run.State.UpdatedUtc = [DateTime]::UtcNow.ToString('o')
    Write-BranchText ($Run.Root + '.lifecycle.json') (($Run.State | ConvertTo-Json -Depth 8) + "`r`n")
}
function New-ApiBranchRun([string]$Repository, [string]$Branch, [string]$Driver, [bool]$Keep) {
    if ($Branch -cnotin @('main','develop')) { throw 'Unsupported standalone branch run.' }
    $repositoryPath = [IO.Path]::GetFullPath($Repository).TrimEnd('\','/')
    $commit = ((Invoke-BranchGit $repositoryPath @('rev-parse','--verify',"refs/heads/${Branch}^{commit}")) -join '').Trim()
    if ($commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Invalid branch commit.' }
    $base = Join-Path $repositoryPath ('.release-output/branch-builds/' + $Branch)
    Assert-BranchUnlinkedPath $base
    [void][IO.Directory]::CreateDirectory($base)
    $legacy = @(Get-ChildItem -LiteralPath $base -Directory | Where-Object { -not [IO.File]::Exists($_.FullName + '.lifecycle.json') })
    if ($legacy.Count) { Write-BranchMessage ("Legacy APIShared sources retained for review: " + $legacy.Count) }
    $number = 1
    do { $root = Join-Path $base ($number.ToString('D3')); $number++ } while ([IO.Directory]::Exists($root) -or [IO.File]::Exists($root + '.lifecycle.json') -or [IO.File]::Exists($root + '.lease'))
    # CreateNew is the final allocation arbiter for simultaneous controllers.
    try { $lease = [IO.File]::Open($root + '.lease', 'CreateNew', 'ReadWrite', 'None') }
    catch [IO.IOException] { return New-ApiBranchRun $repositoryPath $Branch $Driver $Keep }
    $state = [ordered]@{ SchemaVersion=1; Protocol='APISharedBranchRun'; Repository=$repositoryPath; Root=$root;
        Branch=$Branch; Commit=$commit; Driver=$Driver; ControllerHash=(Get-ApiBranchControllerHash $repositoryPath); KeepSource=$Keep; ConsumerSafeToRetire=$false; OwnerPid=$PID;
        OwnerStartedUtc=(Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o'); Phase='preparing'; Detail=''; UpdatedUtc='' }
    $run = [pscustomobject]@{ Root=$root; Branch=$Branch; Commit=$commit; Lease=$lease; State=$state }
    try { Set-ApiBranchRunPhase $run 'preparing'; return $run } catch { $lease.Dispose(); throw }
}
function Test-ApiBranchRunProtected([string]$Repository, [string]$Root) {
    # Standalone and workspace indexes can explicitly protect a retained SDK.
    foreach ($index in @((Join-Path $Repository '.work/INDEX.json'), (Join-Path (Split-Path -Parent $Repository) '.work/INDEX.json'))) {
        if (-not [IO.File]::Exists($index)) { continue }
        $text = [IO.File]::ReadAllText($index)
        $null = $text | ConvertFrom-Json # Unreadable protection data never authorizes cleanup.
        $normal = $text.Replace('\\','\').Replace('/','\')
        if ($normal.IndexOf($Root.Replace('/','\'), [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
        $relative = $Root.Substring($Repository.TrimEnd('\','/').Length).TrimStart('\','/')
        if ($normal.IndexOf($relative.Replace('/','\'), [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    }
    return $false
}
function Assert-ApiBranchTree([string]$Root) {
    Assert-BranchUnlinkedPath $Root
    $stack = [Collections.Generic.Stack[string]]::new(); $stack.Push($Root)
    while ($stack.Count) {
        foreach ($item in @(Get-ChildItem -LiteralPath $stack.Pop() -Force)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked SDK contents retained.' }
            if ($item.PSIsContainer) {
                if ($item.Name -eq '.git') { throw 'Nested repository retained.' }
                if ([IO.File]::Exists((Join-Path $item.FullName '.git'))) { throw 'Nested repository retained.' }
                $stack.Push($item.FullName)
            }
        }
    }
}
function Assert-ApiBranchRunPath($Run) {
    $root=[IO.Path]::GetFullPath($Run.Root).TrimEnd('\','/')
    if ($Run.Branch -cnotin @('main','develop') -or $Run.State.Branch -cne $Run.Branch -or $Run.State.Root -ine $root -or $Run.Root -ine $root) { throw 'Run identity mismatch.' }
    $base=[IO.Path]::GetFullPath((Join-Path $Run.State.Repository ('.release-output/branch-builds/'+$Run.Branch))).TrimEnd('\','/')
    if ([IO.Path]::GetDirectoryName($root) -ine $base -or [IO.Path]::GetFileName($root) -cnotmatch '^[0-9]{3,}$') { throw 'SDK run path is not an allocated branch run.' }
    Assert-BranchUnlinkedPath $root
}
function Assert-ApiBranchRunInactive($Run) {
    $owner=Get-Process -Id $Run.State.OwnerPid -ErrorAction SilentlyContinue
    if ($owner -and $owner.StartTime.ToUniversalTime().ToString('o') -ceq $Run.State.OwnerStartedUtc) { throw 'Run owner is still active.' }
    # A controller can exit while its compiler or test subprocess still uses the source.
    # Failure to inspect processes is uncertainty, never authority to delete.
    $processes=@(Get-CimInstance Win32_Process -ErrorAction Stop)
    if (@($processes | Where-Object { $_.ParentProcessId -eq $Run.State.OwnerPid }).Count) { throw 'Run subprocess is still active.' }
    if (@($processes | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine.IndexOf($Run.Root,[StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count) { throw 'Source path is used by a running process.' }
}
function Export-ApiBranchRunEvidence($Run) {
    Assert-ApiBranchRunPath $Run
    $root = $Run.Root
    if (@(Invoke-BranchGit $root @('diff','--cached','--name-only')).Count) { throw 'Staged source changes retained.' }
    Assert-ApiBranchTree $root
    $record = $null
    # Every difference is either an attested build-tool rewrite or unique user data.
    $recordPath = $root + '.build.json'
    $allowed = @{}
    if ([IO.File]::Exists($recordPath)) {
        $record = [IO.File]::ReadAllText($recordPath) | ConvertFrom-Json
        foreach ($item in $record.BuildToolOverrides) { $allowed[[string]$item.Path] = [string]$item.Sha256 }
    }
    $changes = @(Invoke-BranchGit $root @('-c','core.quotepath=false','diff','--name-only','--no-renames','HEAD'))
    foreach ($relative in $changes) {
        $path = Join-Path $root $relative
        if (-not $allowed.ContainsKey($relative) -or -not [IO.File]::Exists($path) -or (Get-BranchHash $path) -cne $allowed[$relative]) {
            throw "Unattested source difference retained: $relative"
        }
    }
    if (@(Invoke-BranchGit $root @('ls-files','--others','--exclude-standard')).Count) { throw 'Untracked source retained.' }
    if (Test-ApiBranchRunProtected $Run.State.Repository $root) { throw 'Protected SDK source retained.' }
    # Unknown ignored files are not assumed reproducible merely because Git ignores them.
    foreach ($relative in @(Invoke-BranchGit $root @('ls-files','--others','--ignored','--exclude-standard'))) {
        $relative = $relative.Replace('\','/')
        if ($relative -match '(^|/)(bin|obj)/' -or $relative -match '^BepInEx/' -or
            $relative -match '^\.local/(build-proof\.json|build-steps\.json|check-cache\.json|test-results/|WorkshopUploader/|test-temp/|bootstrap-package-tests/|coordination-tests/|readiness-tests/|recovery-tests/|maintenance-tests/)') { continue }
        throw "Unknown ignored data retained: $relative"
    }
    $evidence = $root + '.evidence'
    Assert-BranchUnlinkedPath $evidence
    [void][IO.Directory]::CreateDirectory($evidence)
    $manifest = @()
    foreach ($relative in @('.local/build-proof.json','.local/build-steps.json','.local/test-results')) {
        $source = Join-Path $root $relative
        if (-not (Test-Path -LiteralPath $source)) { continue }
        $files = if ([IO.File]::Exists($source)) { @(Get-Item -LiteralPath $source) } else { @(Get-ChildItem -LiteralPath $source -Recurse -File) }
        foreach ($file in $files) {
            Assert-BranchUnlinkedPath $file.FullName
            $rel = $file.FullName.Substring($root.Length + 1)
            $to = Join-Path $evidence $rel
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
            [IO.File]::Copy($file.FullName,$to,$true)
            $hash = Get-BranchHash $file.FullName
            if ((Get-BranchHash $to) -cne $hash) { throw 'Evidence copy mismatch.' }
            $manifest += [ordered]@{Path=$rel; Sha256=$hash}
        }
    }
    # Installed bytes can be important forensic evidence. No-install outputs are reproducible.
    if ($record -and -not $record.NoInstall) {
        foreach ($relative in @('BepInEx','.local/WorkshopUploader')) {
            $source = Join-Path $root $relative
            if (-not [IO.Directory]::Exists($source)) { continue }
            foreach ($file in @(Get-ChildItem -LiteralPath $source -Recurse -File)) {
                Assert-BranchUnlinkedPath $file.FullName
                $rel=$file.FullName.Substring($root.Length+1); $to=Join-Path $evidence $rel
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to)); [IO.File]::Copy($file.FullName,$to,$true)
                $hash=Get-BranchHash $file.FullName; if ((Get-BranchHash $to) -cne $hash) { throw 'Installed artifact copy mismatch.' }
                $manifest += [ordered]@{Path=$rel;Sha256=$hash}
            }
        }
    }
    Write-BranchText (Join-Path $evidence 'manifest.json') (([ordered]@{Commit=$Run.Commit; Files=$manifest} | ConvertTo-Json -Depth 8) + "`r`n")
}
function Remove-ApiBranchRun($Run) {
    Assert-ApiBranchRunPath $Run
    $root = [IO.Path]::GetFullPath($Run.Root).TrimEnd('\','/')
    $base = [IO.Path]::GetFullPath((Join-Path $Run.State.Repository '.release-output/branch-builds')).TrimEnd('\','/')
    if (-not $root.StartsWith($base+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'SDK cleanup path escapes build area.' }
    Assert-BranchUnlinkedPath $root
    if (Test-ApiBranchRunProtected $Run.State.Repository $root) { throw 'Protected SDK source retained.' }
    Set-ApiBranchRunPhase $Run 'cleaning'
    if ([IO.File]::Exists((Join-Path $root '.git'))) {
        $top=((Invoke-BranchGit $root @('rev-parse','--show-toplevel')) -join '').Trim()
        if ([IO.Path]::GetFullPath($top) -ine $root) { throw 'Worktree identity mismatch.' }
        $common=((Invoke-BranchGit $root @('rev-parse','--path-format=absolute','--git-common-dir')) -join '').Trim()
        $expected=((Invoke-BranchGit $Run.State.Repository @('rev-parse','--path-format=absolute','--git-common-dir')) -join '').Trim()
        if ($common -ine $expected) { throw 'Foreign repository retained.' }
        $head=((Invoke-BranchGit $root @('rev-parse','HEAD')) -join '').Trim()
        if ($head -cne $Run.Commit) { throw 'Changed detached commit retained.' }
        if (-not @(Invoke-BranchGit $Run.State.Repository @('for-each-ref','--format=%(refname)',('--contains='+$head))).Count) { throw 'Unreferenced detached commit retained.' }
        Assert-ApiBranchTree $root
        Invoke-BranchGit $Run.State.Repository @('worktree','remove','--force',$root) | Out-Null
    } elseif ([IO.Directory]::Exists($root)) {
        if (@(Get-ChildItem -LiteralPath $root -Force).Count) { throw 'Unregistered nonempty source retained.' }
        [IO.Directory]::Delete($root,$false)
    }
    Set-ApiBranchRunPhase $Run 'cleaned'
}
function Remove-SupersededApiBranchRuns($Current) {
    foreach ($file in @(Get-ChildItem -LiteralPath (Split-Path -Parent $Current.Root) -File -Filter '*.lifecycle.json')) {
        $lease = $null; $old = $null
        try {
            $state = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
            if ($state.SchemaVersion -ne 1 -or $state.Protocol -cne 'APISharedBranchRun' -or $state.Root -ieq $Current.Root -or
                $state.Repository -ine $Current.State.Repository -or $state.Branch -cne $Current.Branch -or $state.KeepSource -or
                $state.Phase -notin @('sdk-ready','preparing','cleanup-failed')) { continue }
            if ($state.Phase -eq 'sdk-ready' -and (-not $state.PSObject.Properties['ConsumerSafeToRetire'] -or -not $state.ConsumerSafeToRetire)) {
                Write-BranchMessage "Prior SDK retained until consumers explicitly release it: $($state.Root)"
                continue
            }
            if ($file.FullName -ine ($state.Root+'.lifecycle.json')) { throw 'Run metadata path mismatch.' }
            $lease=[IO.File]::Open($state.Root+'.lease','Open','ReadWrite','None')
            $old=[pscustomobject]@{Root=$state.Root;Branch=$state.Branch;Commit=$state.Commit;State=$state;Lease=$lease}
            Assert-ApiBranchRunPath $old
            Assert-ApiBranchRunInactive $old
            if ([IO.Directory]::Exists($old.Root)) { Export-ApiBranchRunEvidence $old }
            Remove-ApiBranchRun $old
            Write-BranchMessage "Superseded APIShared source removed: $($old.Root)"
        } catch { Write-BranchMessage "APIShared source retained for review: $($file.Name): $($_.Exception.Message)" }
        finally { if ($lease) { $lease.Dispose() } }
    }
}

function Get-ApiBranchControllerHash([string]$Repository) {
    $rows=@()
    foreach ($relative in @('tools/BranchBuild/BranchRun.ps1','tools/BranchBuild/Invoke-BranchBuild.ps1','tools/BranchBuild/BranchBuild.Common.ps1','tools/BuildChecks.ps1')) {
        $rows += $relative + ':' + (Get-BranchHash (Join-Path $Repository $relative))
    }
    $sha=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($rows -join "`n"))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
}
function Find-ReusableApiBranchRun([string]$Repository, [string]$Branch) {
    $repositoryPath=[IO.Path]::GetFullPath($Repository).TrimEnd('\','/')
    $base=Join-Path $repositoryPath ('.release-output/branch-builds/'+$Branch)
    if (-not [IO.Directory]::Exists($base)) { return $null }
    $commit=((Invoke-BranchGit $repositoryPath @('rev-parse','--verify',"refs/heads/${Branch}^{commit}")) -join '').Trim()
    $controllerHash=Get-ApiBranchControllerHash $repositoryPath
    foreach ($file in @(Get-ChildItem -LiteralPath $base -File -Filter '*.lifecycle.json' | Sort-Object Name -Descending)) {
        $lease=$null
        try {
            $state=[IO.File]::ReadAllText($file.FullName)|ConvertFrom-Json
            if ($state.SchemaVersion -ne 1 -or $state.Protocol -cne 'APISharedBranchRun' -or $state.Repository -ine $repositoryPath -or
                $state.Branch -cne $Branch -or $state.Commit -cne $commit -or $state.Driver -cne 'build.bat' -or
                $state.Phase -cne 'sdk-ready' -or $state.ConsumerSafeToRetire -or $state.ControllerHash -cne $controllerHash) { continue }
            if ($file.FullName -ine ($state.Root+'.lifecycle.json')) { throw 'Cache metadata path mismatch.' }
            $run=[pscustomobject]@{Root=$state.Root;Branch=$state.Branch;Commit=$state.Commit;State=$state;Lease=$null}
            Assert-ApiBranchRunPath $run
            $lease=[IO.File]::Open($state.Root+'.lease','Open','ReadWrite','None')
            Assert-ApiBranchRunInactive $run
            $head=((Invoke-BranchGit $run.Root @('rev-parse','HEAD')) -join '').Trim()
            if ($head -cne $commit -or @(Invoke-BranchGit $run.Root @('diff','--cached','--name-only')).Count) { throw 'Cached source identity changed.' }
            Assert-ApiBranchTree $run.Root
            if (@(Invoke-BranchGit $run.Root @('ls-files','--others','--exclude-standard')).Count) { throw 'Cached source has untracked inputs.' }
            $record=[IO.File]::ReadAllText($run.Root+'.build.json')|ConvertFrom-Json
            $allowed=@{};foreach($item in $record.BuildToolOverrides){$allowed[[string]$item.Path]=[string]$item.Sha256}
            foreach($relative in @(Invoke-BranchGit $run.Root @('-c','core.quotepath=false','diff','--name-only','--no-renames','HEAD'))) {
                $path=Join-Path $run.Root $relative
                if (-not $allowed.ContainsKey($relative) -or -not [IO.File]::Exists($path) -or (Get-BranchHash $path) -cne $allowed[$relative]) { throw 'Cached sources changed.' }
            }
            . (Join-Path $run.Root 'tools/BuildProof.ps1')
            $proof=Assert-ApiBuildProof $run.Root
            if ($proof.TestMode -cne 'full') { throw 'SDK cache lacks full test evidence.' }
            foreach ($pair in @(@('SHCDE_GAME_DIR','GameDir'),@('SHCDESE_EXTENDER_DIR','ExtenderDir'),@('SHCDE_MSBUILD','MSBuild'))) {
                $requested=[Environment]::GetEnvironmentVariable($pair[0],'Process')
                if ($requested -and [IO.Path]::GetFullPath($requested) -ine [IO.Path]::GetFullPath([string]$proof.($pair[1]))) { throw 'Cached SDK machine context differs.' }
            }
            $run.Lease=$lease; $lease=$null
            Write-BranchMessage "Reusing verified full APIShared SDK: $($run.Root)"
            return $run
        } catch { Write-BranchMessage "SDK cache not reused: $($file.Name): $($_.Exception.Message)" }
        finally { if ($lease) { $lease.Dispose() } }
    }
    return $null
}
