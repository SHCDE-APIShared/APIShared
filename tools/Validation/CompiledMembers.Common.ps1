# Inspect the selected candidate without loading/executing the plugin. Member
# references preserve their declaring type/signature across private source refactors.
function Get-ApiCompiledReferences {
    param([Parameter(Mandatory)][string]$AssemblyPath)
    if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) { throw "Build the selected APIShared candidate first: $AssemblyPath" }
    $candidate = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($AssemblyPath)
    try {
        foreach ($reference in $candidate.MainModule.GetMemberReferences()) {
            [pscustomobject]@{ Owner=$reference.DeclaringType.FullName; Name=$reference.Name; FullName=$reference.FullName; Kind=$(if ($reference -is [Mono.Cecil.FieldReference]) { 'Field' } else { 'Method' }) }
        }
    } finally { $candidate.Dispose() }
}
