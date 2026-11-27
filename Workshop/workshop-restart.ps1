param(
    [Parameter(Mandatory=$true)][string]$Directory,
    [Parameter(Mandatory=$true)][string]$GameExe,
    [Parameter(Mandatory=$true)][int]$GamePid,
    [Parameter(Mandatory=$true)][long]$GameStart,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{32}$')][string]$Batch,
    [switch]$Handoff
)

$ErrorActionPreference = 'Stop'
$GameExe = [IO.Path]::GetFullPath($GameExe)
$Directory = [IO.Path]::GetFullPath($Directory)
$expected = [IO.Path]::Combine([IO.Path]::GetDirectoryName($GameExe), '_APIShared', 'I', 'restart', $Batch)
if (-not [string]::Equals($Directory, $expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected restart status directory.' }

function Assert-Unlinked([string]$Path) {
    for ($part = $Path; $part; $part = [IO.Path]::GetDirectoryName($part)) {
        try {
            if ([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint) { throw "Linked restart path: $part" }
        } catch [IO.FileNotFoundException] { }
        catch [IO.DirectoryNotFoundException] { }
    }
}

function Set-Status([string]$Name, [string]$Value) {
    $target = [IO.Path]::Combine($Directory, $Name)
    $temp = $target + '.tmp'
    Assert-Unlinked $target
    Assert-Unlinked $temp
    $encoding = [Text.UTF8Encoding]::new($false)
    $bytes = $encoding.GetBytes($Value)
    $stream = [IO.FileStream]::new($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::Move($temp, $target)
}

function Has-Status([string]$Name) {
    $path = [IO.Path]::Combine($Directory, $Name)
    Assert-Unlinked $path
    return [IO.File]::Exists($path) -and [IO.File]::ReadAllText($path).Trim() -ceq $Batch
}

function Game-Running {
    if (Has-Status 'claimed') { return $true }
    $processes = [Diagnostics.Process]::GetProcessesByName([IO.Path]::GetFileNameWithoutExtension($GameExe))
    try {
        foreach ($process in $processes) {
            try {
                if (-not $process.HasExited -and [string]::Equals($process.MainModule.FileName, $GameExe, [StringComparison]::OrdinalIgnoreCase)) { return $true }
            } catch {
                # An inaccessible process with the same name is conservatively busy.
                if (-not $process.HasExited) { return $true }
            }
        }
        return $false
    } finally { foreach ($process in $processes) { $process.Dispose() } }
}

function Complete-Restart([string]$Status) {
    Set-Status 'result' ($Status + ' [' + [DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture) + ']')
}

function Get-SteamProcessLog([int]$SessionId) {
    $clients = [Diagnostics.Process]::GetProcessesByName('steam')
    try {
        foreach ($client in $clients) {
            if ($client.SessionId -ne $SessionId) { continue }
            $path = [IO.Path]::Combine([IO.Path]::GetDirectoryName($client.MainModule.FileName), 'logs', 'gameprocess_log.txt')
            if ([IO.File]::Exists($path)) { return $path }
        }
    } finally { foreach ($client in $clients) { $client.Dispose() } }
    throw 'Could not locate the running Steam client process log.'
}

function Get-SteamTracking([string]$Log, [int]$OriginalPid, [datetime]$Started) {
    # Bind the release to this launch, not to an old entry for this app or a reused PID.
    $state = 'unknown'
    foreach ($line in Get-Content -LiteralPath $Log -Tail 4096) {
        if ($line -match '^\[(?<at>[0-9-]+ [0-9:]+)\] AppID 3024040 adding PID (?<pid>[0-9]+) as a tracked process(?: |$)') {
            if ($state -eq 'released') { return 'restarted' }
            if ([int]$Matches.pid -eq $OriginalPid) {
                $at = [datetime]::ParseExact($Matches.at, 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
                if ($at -ge $Started.AddSeconds(-2)) { $state = 'running' }
            }
        } elseif ($state -eq 'running' -and $line -match '^\[[^\]]+\] Remove 3024040 from running list\s*$') {
            $state = 'released'
        } elseif ($state -ne 'unknown' -and $line -match '^\[[^\]]+\] Client version: ') {
            return 'unknown'
        }
    }
    return $state
}

function Start-DetachedWaiter {
    # Steam can retain a game while descendants of the game are still alive. Ask the
    # local WMI provider to start the waiter outside the game's process tree.
    $shell = [IO.Path]::Combine([Environment]::SystemDirectory, 'WindowsPowerShell', 'v1.0', 'powershell.exe')
    $command = '"{0}" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{1}" -Directory "{2}" -GameExe "{3}" -GamePid {4} -GameStart {5} -Batch {6}' -f
        $shell, $PSCommandPath, $Directory, $GameExe, $GamePid, $GameStart, $Batch
    # Windows PowerShell needs a console for -File. Keep that new console hidden.
    $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0; CreateFlags = [uint32]16 }
    $created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = $command
        ProcessStartupInformation = $startup
    }
    if ($created.ReturnValue -ne 0 -or $created.ProcessId -le 0) {
        throw ('Could not detach the restart helper from the game process (WMI ' + $created.ReturnValue + ').')
    }
}

Assert-Unlinked $Directory
Assert-Unlinked $GameExe
$original = $null
try {
    if ($Handoff) { Start-DetachedWaiter; return }
    $original = [Diagnostics.Process]::GetProcessById($GamePid)
    # Open and retain this exact process handle before sending readiness.
    $null = $original.Handle
    if ($original.StartTime.ToUniversalTime().Ticks -ne $GameStart -or
        -not [string]::Equals($original.MainModule.FileName, $GameExe, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The waiting process does not identify the calling game.'
    }
    $self = [Diagnostics.Process]::GetCurrentProcess()
    try {
        if ($self.SessionId -ne $original.SessionId) { throw 'The restart helper is in a different Windows session than the game.' }
    } finally { $self.Dispose() }
    $steamLog = Get-SteamProcessLog $original.SessionId
    Assert-Unlinked $steamLog
    $originalStarted = $original.StartTime
    if ((Get-SteamTracking $steamLog $GamePid $originalStarted) -ne 'running') {
        throw 'Steam has not recorded this game process in its running list.'
    }
    Set-Status 'ready' $Batch
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Has-Status 'armed')) {
        if ($elapsed.Elapsed.TotalSeconds -ge 30 -or $original.WaitForExit(100)) {
            if (Has-Status 'armed') { break }
            Complete-Restart 'cancelled before shutdown was authorized'
            return
        }
    }
    if (-not $original.WaitForExit(120000)) { throw 'Timed out waiting for the game to close.' }
    $elapsed.Restart()
    while ($true) {
        if (Game-Running) { Complete-Restart 'game already started'; return }
        $tracking = 'unknown'
        try { $tracking = Get-SteamTracking $steamLog $GamePid $originalStarted }
        catch [IO.IOException] { }
        if ($tracking -eq 'restarted') { Complete-Restart 'game already started'; return }
        if ($tracking -eq 'released') { break }
        if ($elapsed.Elapsed.TotalSeconds -ge 120) { throw 'Steam did not release the game from its running list.' }
        Start-Sleep -Milliseconds 500
    }
    if (Game-Running) { Complete-Restart 'game already started'; return }
    $launch = [Diagnostics.ProcessStartInfo]::new('steam://run/3024040')
    $launch.UseShellExecute = $true
    $handle = [Diagnostics.Process]::Start($launch)
    if ($null -ne $handle) { $handle.Dispose() }
    $elapsed.Restart()
    while ($elapsed.Elapsed.TotalSeconds -lt 45) {
        if (Game-Running) { Complete-Restart 'game started through Steam'; return }
        Start-Sleep -Milliseconds 500
    }
    throw 'Steam did not start the game after the launch request.'
} catch {
    Complete-Restart ('failed: ' + $_.Exception.Message + ' Open the game manually in Steam.')
    exit 1
} finally {
    if ($null -ne $original) { $original.Dispose() }
}
