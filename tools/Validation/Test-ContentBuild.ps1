[CmdletBinding()]
param([Parameter(Mandatory)][string]$FixtureDirectory, [Parameter(Mandatory)][string]$MSBuild)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = [IO.Path]::GetFullPath($FixtureDirectory)
if (-not [IO.Directory]::Exists($fixture) -or @(Get-ChildItem -LiteralPath $fixture -Force).Count) {
    throw 'Provide an existing, empty test directory; this test retains its evidence.'
}
function Write-Fixture([string]$Relative, [string]$Text) {
    $path = Join-Path $fixture $Relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $Text, [Text.UTF8Encoding]::new($false))
}
Copy-Item -LiteralPath (Join-Path $root 'APIShared.csproj') -Destination $fixture
Write-Fixture 'Directory.Build.props' '<Project />'
Write-Fixture 'Directory.Build.targets' '<Project />'
Write-Fixture 'Content/APIShared.Content.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup></Project>'
Write-Fixture 'Content/Document.cs' 'public class Document {}'
Write-Fixture 'Content/Nested/Value.cs' 'public class Value { public const int Number = 1; }'
function Build-Content([string]$Label) {
    # Exercise the actual Framework project's target, including its incremental
    # decision. SDK compilation is real; only the Content sources are a fixture.
    $log = Join-Path $fixture ($Label + '.log')
    & $MSBuild (Join-Path $fixture 'APIShared.csproj') /t:BuildContentLibrary /p:Configuration=Release /nologo *> $log
    if ($LASTEXITCODE) { throw "Content build failed; see $log" }
    $stream = [IO.File]::OpenRead((Join-Path $fixture 'Content/bin/Release/netstandard2.0/APIShared.Content.dll'))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToBase64String($sha.ComputeHash($stream)) }
    finally { $stream.Dispose(); $sha.Dispose() }
}
$initial = Build-Content 'initial'
Write-Fixture 'Content/Nested/Value.cs' 'public class Value { public const int Number = 2; }'
$edited = Build-Content 'nested-edit'
if ($initial -eq $edited) { throw 'A nested source edit left stale Content output.' }
Write-Fixture 'Content/Nested/Added.cs' 'public class Added {}'
$added = Build-Content 'nested-add'
if ($edited -eq $added) { throw 'An added nested source left stale Content output.' }
# Only the explicitly created fixture file is removed; no recursive cleanup.
Remove-Item -LiteralPath (Join-Path $fixture 'Content/Nested/Added.cs')
$removed = Build-Content 'nested-remove'
if ($added -eq $removed -or $edited -ne $removed) { throw 'Removing a nested source failed to restore the deterministic output.' }
Write-Host 'PASS: Content target rebuilds nested edits, additions and removals; deterministic output restored.'
Write-Host "Fixture retained: $fixture"
