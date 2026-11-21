# Real process kills against the safe Workshop installation core (src/Workshop/Install).
# Deterministic step faults are covered by tests/Core.Tests/WorkshopSafeInstallTests; this script
# kills the actual process at random moments, which also covers points between two statements.
#   swap:      kill the swap, then run the preloader (recovery + pending request)
#   preloader: kill the preloader itself during recovery, then run it again
#   stage:     kill staging during the copy, then stage again
#   group:     kill at reachable transaction boundaries where multiple packages and receipts change
# Invariants: no moment exposes a partial plugin folder; afterwards the complete new version, its
# receipt and no leftovers. Fixtures live in .local/workshop-install and are removed on success.
param([int]$SwapKills = 150, [int]$PreloaderKills = 60, [int]$StageKills = 30, [int]$Seed = 20261118)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $root 'tests\WorkshopInstall.Harness\WorkshopInstall.Harness.csproj'
& dotnet msbuild $project /t:Build /p:Configuration=Release /verbosity:quiet /nologo
if ($LASTEXITCODE -ne 0) { throw 'Workshop install harness build failed.' }
$harness = Join-Path $root 'tests\WorkshopInstall.Harness\bin\WorkshopInstall.Harness.exe'
$work = Join-Path $root ('.local\workshop-install\' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$random = [Random]::new($Seed)
$failures = [Collections.Generic.List[string]]::new()
$outcomes = @{}

function Invoke-Harness([string[]]$Arguments) {
    $output = & $harness @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('harness ' + ($Arguments -join ' ') + ': ' + ($output -join ' ')) }
    return @($output)[-1]
}
function Start-Harness([string[]]$Arguments) {
    $quoted = $Arguments | ForEach-Object { '"' + $_ + '"' }
    $start = [Diagnostics.ProcessStartInfo]::new($harness, ($quoted -join ' '))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    return [Diagnostics.Process]::Start($start)
}
function Start-Killed([string[]]$Arguments, [int]$MinMs, [int]$MaxMs) {
    $process = Start-Harness $Arguments
    try {
        Start-Sleep -Milliseconds ($random.Next($MinMs, $MaxMs))
        if ($process.HasExited) { return 'finished' }
        $process.Kill()
        $process.WaitForExit()
        return 'killed'
    } finally { $process.Dispose() }
}
function Kill-GroupAt([string]$Game, [string]$Step) {
    $signal = Join-Path (Split-Path $Game) 'paused'
    if (Test-Path -LiteralPath $signal) { Remove-Item -LiteralPath $signal }
    $process = Start-Harness @('group-run', $Game, $Step)
    try {
        $wait = [Diagnostics.Stopwatch]::StartNew()
        while (-not ([IO.File]::Exists($signal))) {
            if ($process.HasExited -or $wait.Elapsed.TotalSeconds -ge 10) { throw "Group did not reach $Step." }
            Start-Sleep -Milliseconds 20
        }
    } finally {
        try {
            if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        } finally { $process.Dispose() }
    }
}
function Remove-Fixture([string]$Directory) {
    $resolved = (Resolve-Path -LiteralPath $Directory).ProviderPath
    $workRoot = [IO.Path]::GetFullPath($work)
    if ($resolved -ne $workRoot -and -not $resolved.StartsWith($workRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected installation test fixture path."
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
function New-Game([string]$Name) {
    $game = Join-Path $work ($Name + '\game')
    [void][IO.Directory]::CreateDirectory($game)
    return $game
}
function Assert-Final([string]$Label, [string]$Game) {
    $state = Invoke-Harness @('check', $Game)
    if ($state -ne 'new,new|{"Version":"2"}|clean') { $failures.Add("$Label final $state") }
}
function Count([string]$Key) { $outcomes[$Key] = 1 + [int]$outcomes[$Key] }

try {
    for ($i = 1; $i -le $SwapKills; $i++) {
        $game = New-Game "s$i"
        $null = Invoke-Harness @('prepare', $game)
        $how = Start-Killed @('apply', $game, '25') 20 260
        $seen = (Invoke-Harness @('check', $game)).Split('|')[0]
        if ($seen -match 'partial') { $failures.Add("swap $i exposed $seen") }
        $null = Invoke-Harness @('preloader', $game)
        Assert-Final "swap $i" $game
        Count ("swap/$how/" + $seen)
        Remove-Fixture (Split-Path $game)
    }
    for ($i = 1; $i -le $PreloaderKills; $i++) {
        $game = New-Game "p$i"
        $null = Invoke-Harness @('prepare', $game)
        # Leave an interrupted swap behind first, then kill the recovering preloader.
        $null = Start-Killed @('apply', $game, '40') 60 200
        $how = Start-Killed @('preloader', $game, '25') 20 220
        $seen = (Invoke-Harness @('check', $game)).Split('|')[0]
        if ($seen -match 'partial') { $failures.Add("preloader $i exposed $seen") }
        $null = Invoke-Harness @('preloader', $game)
        Assert-Final "preloader $i" $game
        Count ("preloader/$how/" + $seen)
        Remove-Fixture (Split-Path $game)
    }
    for ($i = 1; $i -le $StageKills; $i++) {
        $game = New-Game "t$i"
        $how = Start-Killed @('stage', $game, '1') 40 900
        $container = Join-Path $game '_APIShared\I\s\77'
        $published = Test-Path -LiteralPath (Join-Path $container '.apishared-complete')
        if ((Test-Path -LiteralPath $container) -and -not $published) { $failures.Add("stage $i published an incomplete container") }
        $null = Invoke-Harness @('stage', $game)
        if (-not (Test-Path -LiteralPath (Join-Path $container '.apishared-complete')) -or (Test-Path -LiteralPath ($container + '.tmp'))) { $failures.Add("stage $i retry incomplete") }
        Count ("stage/$how/published=$published")
        Remove-Fixture (Split-Path $game)
    }
    foreach ($step in @('prepared', 'old-moved', 'new-moved', 'receipt-moved', 'rollback-old', 'committed', 'retired')) {
        $game = New-Game ("g-" + $step)
        $null = Invoke-Harness @('group-prepare', $game)
        if ($step -eq 'rollback-old') { Kill-GroupAt $game 'old-moved' }
        Kill-GroupAt $game $step
        $null = Invoke-Harness @('group-run', $game)
        $seen = Invoke-Harness @('group-check', $game)
        $version = if ($step -in @('committed', 'retired')) { 2 } else { 1 }
        $expected = "1:${version}/${version},2:${version}/${version}|clean"
        if ($seen -ne $expected) { $failures.Add("group/$step expected $expected but got $seen") }
        Count ("group/" + $step)
        Remove-Fixture (Split-Path $game)
    }
} finally {
    $outcomes.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Host ("  {0,-34} {1}" -f $_.Name, $_.Value) }
    if ($failures.Count -eq 0 -and (Test-Path -LiteralPath $work)) { Remove-Fixture $work }
}
if ($failures.Count -ne 0) { $failures | ForEach-Object { Write-Host "FAIL $_" }; throw "$($failures.Count) Workshop installation kill check(s) failed; fixtures kept in $work." }
Write-Host "PASS: Workshop installation survives $SwapKills swap, $PreloaderKills preloader, $StageKills staging and 7 group process kills."
