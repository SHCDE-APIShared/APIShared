[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$files = @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Recurse -File -Filter '*.cs')

$errors = [System.Collections.Generic.List[string]]::new()
foreach ($file in $files) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    for ($index = 0; $index -lt $lines.Length; $index++) {
        $line = $lines[$index]
        if ($line -match '\.Hook\.(Enable|Disable)\s*\(' -or
            $line -match '\bCodePatch\.Write\s*\(' -or
            $line -match '\bGatehouseNativeMutation\b' -or
            $line -match '\bVirtualProtect\s*\(' -or
            $line -match '\bFlushInstructionCache\s*\(') {
            $errors.Add("$($file.FullName):$($index + 1): runtime executable-memory mutation: $($line.Trim())")
        }

        if ($line -notmatch '(classifierTransaction|transaction)\??\.Dispose\s*\(') { continue }
        $start = [Math]::Max(0, $index - 10)
        $context = [string]::Join("`n", $lines[$start..$index])
        $isInitializationRollback =
            $context -match '\bcatch\b' -or
            $context -match '!published' -or
            $context -match '!nativeInitialized' -or
            $context -match 'RollbackUnpublished' -or
            $context -match 'DisplacedByteCount' -or
            $context -match 'could not be installed'
        if (-not $isInitializationRollback) {
            $errors.Add("$($file.FullName):$($index + 1): published transaction teardown is not an initialization rollback")
        }
    }
}

# Extender fixes have no runtime unpatch path. Dispose is restricted to the
# single unpublished-candidate rollback helper; installed ILHooks are static roots.
$fixRoot = Join-Path $workspace 'src\ScriptExtenderFixes'
if (Test-Path -LiteralPath $fixRoot) {
    foreach ($file in Get-ChildItem -LiteralPath $fixRoot -File -Filter '*.cs') {
        $text = [IO.File]::ReadAllText($file.FullName)
        if ($text -match '\.(Undo|UnHook|Disable|Enable)\s*\(' -or
            $text -match '\b(?:Marshal\.Write\w*|CodePatch\.Write|VirtualProtect)\s*\(') {
            $errors.Add("$($file.FullName): Extender fix reaches a runtime executable mutation")
        }
        $withoutRollback = [regex]::Replace($text,
            '(?s)private static void RollbackUnpublished\(List<ILHook> candidates, ImGuiShutdownCompiler compilerCandidate\)\s*\{.*?\n\s*\}', '')
        if ($withoutRollback -match '\.Dispose\s*\(') {
            $errors.Add("$($file.FullName): hook teardown outside unpublished rollback")
        }
        if ($text -match 'OnDestroy\s*\(|OnDisable\s*\(|OnApplicationQuit\s*\(|StartCoroutine\s*\(') {
            $errors.Add("$($file.FullName): Extender fix depends on component lifecycle")
        }
    }
}

if ($errors.Count -ne 0) { throw ($errors -join [Environment]::NewLine) }
Write-Host 'PASS: APIShared has no executable-memory toggles or published transaction teardown.'
