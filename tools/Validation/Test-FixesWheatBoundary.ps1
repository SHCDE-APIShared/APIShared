[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$memoryPath = Join-Path $root 'src\Compatibility\FixesWheatMemory.cs'
$runtimePath = Join-Path $root 'src\Compatibility\FixesWheatStartup.cs'
$memory = [IO.File]::ReadAllText($memoryPath)
$runtime = [IO.File]::ReadAllText($runtimePath)
$contract = [IO.File]::ReadAllText((Join-Path $root 'src\Compatibility\FixesWheatContract.cs'))
# Explicit exception for a single audited startup operand, never a folder-wide exemption.
foreach ($check in @(
    @{ Text=$memory; Pattern='Marshal\.WriteByte\(pointer, value\)'; Count=1 },
    @{ Text=$memory; Pattern='Marshal\.Write\w*\('; Count=1 },
    @{ Text=$memory; Pattern='\bVirtualProtect\s*\('; Count=3 },
    @{ Text=$memory; Pattern='\bFlushInstructionCache\s*\('; Count=2 },
    @{ Text=$runtime; Pattern='FixesWheatMemory\.WriteOperand\('; Count=1 },
    @{ Text=$runtime; Pattern='Interlocked\.Exchange\(ref attempted, 1\)'; Count=1 },
    @{ Text=$runtime; Pattern='Monitor\.TryEnter\(engineLock, 0, ref taken\)'; Count=1 },
    @{ Text=$runtime; Pattern='RequireStartup\(\);'; Count=3 },
    @{ Text=$contract; Pattern='write\(OperandOffset, CorrectOffset\)'; Count=1 }
)) {
    if ([regex]::Matches($check.Text, $check.Pattern).Count -ne $check.Count) {
        throw "Wheat startup exception boundary changed: $($check.Pattern)"
    }
}
foreach ($pattern in @('finally', 'restored = VirtualProtect\(pointer, \(UIntPtr\)1, old',
    'flushed = FlushInstructionCache', 'if \(!restored \|\| !flushed\)', 'if \(old != observed\)')) {
    if ($memory -notmatch $pattern) { throw "Missing protection/cache boundary: $pattern" }
}
if ($contract -notmatch 'CorrectOffset = 0x10 \+ 0x58 \+ 8 \+ 8;' -or
    $contract -notmatch 'OperandOffset = 12;' -or $contract -notmatch 'StubLength = 75;' -or
    $contract -notmatch 'RequireEqual\(after, expected,') { throw 'Wheat byte/verification contract changed.' }
$files = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File -Filter '*.cs')
$calls = @($files | Select-String -Pattern 'FixesWheatStartup\.Initialize\(')
if ($calls.Count -ne 1 -or $calls[0].Path -ne (Join-Path $root 'src\Core\APISharedPlugin.cs')) {
    throw 'Wheat startup must be called only once from the plugin entry.'
}
$plugin = [IO.File]::ReadAllText($calls[0].Path)
if ($plugin -notmatch '(?s)private void Awake\(\)\s*\{\s*Compatibility\.FixesWheatStartup\.Initialize\(Logger\);' -or
    $plugin -notmatch '\[BepInDependency\("fixes", BepInDependency.DependencyFlags.SoftDependency\)\]') {
    throw 'Wheat startup must precede API publication with an optional Fixes dependency.'
}
foreach ($file in $files) {
    $text = [IO.File]::ReadAllText($file.FullName)
    if ($file.FullName -ne $runtimePath -and $text -match 'FixesWheatMemory\.') {
        throw "Unexpected native wheat-memory caller: $($file.FullName)"
    }
    if ($file.DirectoryName -eq (Split-Path $runtimePath)) {
        if ($text -match '\bCodePatch\.') { throw 'No generic CodePatch operation belongs to the wheat startup exception.' }
        if ($text -match '\b(?:OnDestroy|OnDisable|OnApplicationQuit|StartCoroutine|Update|LateUpdate|FixedUpdate)\s*\(|\+=|\.(?:Enable|Disable|Undo|Apply)\s*\(') {
            throw "Wheat correction acquired a later callback/toggle/teardown: $($file.FullName)"
        }
        if ($file.FullName -ne $memoryPath -and $text -match '\b(?:Marshal\.Write\w*|VirtualProtect|FlushInstructionCache)\s*\(') {
            throw "Wheat executable writer escaped its memory boundary: $($file.FullName)"
        }
    }
}
Write-Host 'PASS: narrow Fixes wheat startup mutation boundary (source checks only; no backend/game execution).'
