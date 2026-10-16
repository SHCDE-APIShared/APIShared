[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$files = @(Get-ChildItem -LiteralPath (Join-Path $workspace 'src') -Recurse -File -Filter '*.cs')

$errors = [System.Collections.Generic.List[string]]::new()
& (Join-Path $PSScriptRoot 'Test-FixesWheatBoundary.ps1')
$wheatMemory = Join-Path $workspace 'src\Compatibility\FixesWheatMemory.cs'
foreach ($file in $files) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    for ($index = 0; $index -lt $lines.Length; $index++) {
        $line = $lines[$index]
        if ($line -match '\.Hook\.(Enable|Disable)\s*\(' -or
            $line -match '\bCodePatch\.Write\s*\(' -or
            $line -match '\bGatehouseNativeMutation\b' -or
            $line -match '\bVirtualProtect\s*\(' -or
            $line -match '\bFlushInstructionCache\s*\(') {
            if ($file.FullName -ne $wheatMemory) {
                $errors.Add("$($file.FullName):$($index + 1): runtime executable-memory mutation: $($line.Trim())")
            }
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

$workshop = [IO.File]::ReadAllText((Join-Path $workspace 'src/Workshop/WorkshopCleanupRuntime.cs'))
if ($workshop -match '\bhook\??\.(Dispose|Undo|Disable|Enable)\s*\(' -or $workshop -notmatch 'private static ILHook hook;' -or $workshop -notmatch 'cursor.MoveAfterLabels\(\)' -or $workshop -match 'OnDestroy\s*\(|OnDisable\s*\(|OnApplicationQuit\s*\(|StartCoroutine\s*\(') {
    $errors.Add('Workshop cleanup must retain its permanent hook, intercept incoming return labels and avoid component lifecycle teardown.')
}
# Audio owns permanent roots; only nonpublished native candidates can be disposed.
$audioPath = Join-Path $workspace 'src\Audio\SoundRuntime.cs'
$audio = [IO.File]::ReadAllText($audioPath)
$rollbackStart = $audio.IndexOf('private void RollbackUnpublished()')
$rollbackEnd = $audio.IndexOf('private void OnWrapper(', $rollbackStart)
if ($rollbackStart -lt 0 -or $rollbackEnd -lt 0) { throw 'Missing audio rollback boundary.' }
$rollback = $audio.Substring($rollbackStart, $rollbackEnd - $rollbackStart)
if ($rollback -notmatch 'if \(publicationStarted\) throw' -or $rollback -notmatch 'wrapperHook.IsInstalled' -or $rollback -notmatch 'emitHook.IsInstalled') { throw 'Audio unpublished rollback guards changed.' }
$outsideRollback = $audio.Remove($rollbackStart, $rollbackEnd - $rollbackStart)
if ($outsideRollback -match '\.(?:Dispose|Undo|Disable)\s*\(' -or
    [regex]::Matches($audio, 'RollbackUnpublished\(\);').Count -ne 1 -or
    $audio -notmatch 'catch \{ RollbackUnpublished\(\); throw; \}' -or
    [regex]::Matches($audio, '\.Enable\s*\(').Count -ne 3 -or
    [regex]::Matches($audio, '\.Apply\s*\(').Count -ne 1 -or
    $audio.IndexOf('publicationStarted = true;') -gt $audio.IndexOf('wrapperHook.Enable();') -or
    $audio.IndexOf('SoundContextPolicy.SetReady(true);') -lt $audio.IndexOf('emitHook.Enable();') -or
    $audio.IndexOf('playbackHook = new ILHook') -gt $audio.IndexOf('playbackHook.Apply();')) { throw 'Audio permanent publication contract changed.' }
foreach ($file in Get-ChildItem -LiteralPath (Split-Path $audioPath) -File -Filter '*.cs') {
    if ($file.FullName -eq $audioPath) { continue }
    if ([IO.File]::ReadAllText($file.FullName) -match '\.(?:Dispose|Undo|Disable|Enable|Apply)\s*\(') { throw "Unexpected audio hook mutation: $($file.Name)" }
}
if ($errors.Count -ne 0) { throw ($errors -join [Environment]::NewLine) }
Write-Host 'PASS: no executable-memory toggles or published teardown; audited one-time wheat startup exception checked separately.'
