[CmdletBinding()]
param([Parameter(Mandatory)][string]$FixtureDirectory, [Parameter(Mandatory)][string]$MSBuild)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = [IO.Path]::GetFullPath($FixtureDirectory)
if (-not [IO.Directory]::Exists($fixture) -or @(Get-ChildItem -LiteralPath $fixture -Force).Count) {
    throw 'Provide an existing, empty test directory; this test retains its evidence.'
}
function Write-Fixture([string]$Relative, [string]$Text, [string]$ProjectDirectory = $fixture) {
    $path = Join-Path $ProjectDirectory $Relative
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $Text, [Text.UTF8Encoding]::new($false))
}
function Initialize-Fixture([string]$ProjectDirectory) {
    [void][IO.Directory]::CreateDirectory($ProjectDirectory)
    Copy-Item -LiteralPath (Join-Path $root 'APIShared.csproj') -Destination $ProjectDirectory
    Copy-Item -LiteralPath (Join-Path $root 'Directory.Build.props') -Destination $ProjectDirectory
    Write-Fixture 'Content/APIShared.Content.csproj' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup></Project>' $ProjectDirectory
    Write-Fixture 'Content/Document.cs' 'public class Document {}' $ProjectDirectory
    Write-Fixture 'Content/Nested/Value.cs' 'public class Value { public const int Number = 1; }' $ProjectDirectory
    Write-Fixture 'tests/Directory.Build.props' '<Project><Import Project="../Directory.Build.props" /></Project>' $ProjectDirectory
    $testDirectory = Join-Path $ProjectDirectory 'tests/APISharedTests'
    [void][IO.Directory]::CreateDirectory($testDirectory)
    Copy-Item -LiteralPath (Join-Path $root 'tests/APISharedTests/APISharedTests.csproj') -Destination $testDirectory
    # Resolve only the actual Content reference from each copied project. This
    # needs no proprietary assemblies, game stubs, package restore or test runner.
    Write-Fixture 'Directory.Build.targets' @'
<Project>
  <Target Name="RecordContentReference">
    <ItemGroup>
      <_ContentReference Include="@(Reference)" Condition="'%(Reference.Identity)' == 'APIShared.Content'" />
    </ItemGroup>
    <ResolveAssemblyReference Assemblies="@(_ContentReference)" SearchPaths="{HintPathFromItem}" FindDependencies="false">
      <Output TaskParameter="ResolvedFiles" ItemName="_ResolvedContent" />
    </ResolveAssemblyReference>
    <Error Condition="'@(_ResolvedContent)' == ''" Text="Content reference did not resolve through its actual HintPath." />
    <WriteLinesToFile File="$(ContentReferenceReport)" Lines="@(_ResolvedContent->'%(FullPath)')" Overwrite="true" />
    <WriteLinesToFile File="$(ContentReferenceReport).paths" Lines="OutputPath=$(OutputPath);IntermediateOutputPath=$(IntermediateOutputPath);MSBuildProjectExtensionsPath=$(MSBuildProjectExtensionsPath)" Overwrite="true" />
  </Target>
  <Target Name="RecordTestContentReference">
    <Exec Command="dotnet msbuild &quot;$(MSBuildThisFileDirectory)tests/APISharedTests/APISharedTests.csproj&quot; /t:RecordContentReference /p:Configuration=$(Configuration) /p:ContentReferenceReport=&quot;$(ContentReferenceReport)&quot; /nologo"
          EnvironmentVariables="DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false;DOTNET_GENERATE_ASPNET_CERTIFICATE=false;APISharedArtifactsRoot=$(_APISharedArtifactsRoot)" />
  </Target>
</Project>
'@ $ProjectDirectory
}
function Get-ContentHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToBase64String($sha.ComputeHash($stream)) }
    finally { $stream.Dispose(); $sha.Dispose() }
}
function Invoke-FixtureTarget([string]$ProjectDirectory, [string]$Target, [string]$Label, [string]$ArtifactsRoot = '', [string]$Report = '') {
    $log = Join-Path $fixture ($Label + '.log')
    $arguments = @((Join-Path $ProjectDirectory 'APIShared.csproj'), "/t:$Target", '/p:Configuration=Release', '/nologo')
    # Explicit empty global property also prevents an inherited opt-in environment
    # setting from changing the default-location regression lane.
    # CLI property parsing is a separate escaping boundary from the nested
    # Exec environment array. Preserve literal percent sequences before encoding
    # semicolon separators; shell quoting alone does not protect a /p value.
    $propertyRoot = $ArtifactsRoot.Replace('%', '%25').Replace(';', '%3B')
    $arguments += "/p:APISharedArtifactsRoot=$propertyRoot"
    if ($Report) { $arguments += "/p:ContentReferenceReport=$Report" }
    & $MSBuild @arguments *> $log
    if ($LASTEXITCODE) { throw "Fixture target $Target failed; see $log" }
}
function Build-Content([string]$Label, [string]$ProjectDirectory = $fixture, [string]$ArtifactsRoot = '') {
    # Exercise the actual Framework project's target and the real SDK compiler.
    # The SDK remains responsible for incremental checks, including source removal.
    Invoke-FixtureTarget $ProjectDirectory 'BuildContentLibrary' $Label $ArtifactsRoot
    $relative = 'Content/bin/Release/netstandard2.0/APIShared.Content.dll'
    if ($ArtifactsRoot) { $relative = 'bin/APIShared.Content/Release/netstandard2.0/APIShared.Content.dll' }
    $outputDirectory = $ProjectDirectory
    if ($ArtifactsRoot) { $outputDirectory = $ArtifactsRoot }
    return Get-ContentHash (Join-Path $outputDirectory $relative)
}
function Assert-ContentReference([string]$ProjectDirectory, [string]$ArtifactsRoot, [string]$Target, [string]$Label, [string]$ExpectedHash) {
    $report = Join-Path $fixture ($Label + '.txt')
    Invoke-FixtureTarget $ProjectDirectory $Target $Label $ArtifactsRoot $report
    $resolved = @(Get-Content -LiteralPath $report)
    $expected = [IO.Path]::GetFullPath((Join-Path $ArtifactsRoot 'bin/APIShared.Content/Release/netstandard2.0/APIShared.Content.dll'))
    if ($resolved.Count -ne 1 -or [IO.Path]::GetFullPath($resolved[0]) -ne $expected) {
        throw "$Label resolved Content from the wrong output: $resolved"
    }
    if ((Get-ContentHash $resolved[0]) -ne $ExpectedHash) { throw "$Label resolved stale Content bytes." }
    $artifactPrefix = [IO.Path]::GetFullPath($ArtifactsRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $paths = @{}
    foreach ($line in @(Get-Content -LiteralPath ($report + '.paths'))) {
        $parts = $line -split '=', 2
        if ($parts.Count -ne 2 -or -not $parts[1]) { throw "$Label has an empty build path: $line" }
        $fullPath = [IO.Path]::GetFullPath($parts[1])
        if (-not $fullPath.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Label has a build path outside the isolated root: $line"
        }
        $paths[$parts[0]] = $fullPath
    }
    if ($paths.Count -ne 3) { throw "$Label did not report all output/intermediate paths." }
    return $paths
}
Initialize-Fixture $fixture
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

function Test-IsolatedContent([string]$Label, [string]$ArtifactsDirectoryName) {
    # Separate fresh sources prevent the default builds above from concealing a
    # nested Content build that ignores the routed root. Both paths contain spaces;
    # the normalized root forwarded through Exec also has a trailing separator.
    $isolatedProject = Join-Path $fixture ("isolated project $Label with spaces")
    $artifacts = Join-Path $fixture $ArtifactsDirectoryName
    Initialize-Fixture $isolatedProject
    $isolated = Build-Content ($Label + '-initial') $isolatedProject $artifacts
    Write-Fixture 'Content/Nested/Value.cs' 'public class Value { public const int Number = 3; }' $isolatedProject
    $isolatedEdited = Build-Content ($Label + '-edit') $isolatedProject $artifacts
    if ($isolated -eq $isolatedEdited) { throw 'An isolated nested edit left stale Content output.' }
    $apiPaths = Assert-ContentReference $isolatedProject $artifacts 'RecordContentReference' ($Label + '-api-reference') $isolatedEdited
    $testPaths = Assert-ContentReference $isolatedProject $artifacts 'RecordTestContentReference' ($Label + '-test-reference') $isolatedEdited
    foreach ($name in @('OutputPath', 'IntermediateOutputPath', 'MSBuildProjectExtensionsPath')) {
        if ($apiPaths[$name] -eq $testPaths[$name]) { throw "API/test projects share their isolated $name." }
    }
    foreach ($relative in @('BepInEx', 'bin', 'obj', 'Content/bin', 'Content/obj', 'tests/APISharedTests/bin', 'tests/APISharedTests/obj')) {
        if (Test-Path -LiteralPath (Join-Path $isolatedProject $relative)) {
            throw "The isolated build wrote a default project output directory: $relative"
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $artifacts 'obj/APIShared.Content/project.assets.json'))) {
        throw 'Content restore metadata did not use the isolated per-project intermediate directory.'
    }
}

Test-IsolatedContent 'isolated-spaces' 'isolated artifacts with spaces'
Test-IsolatedContent 'isolated-semicolon' 'isolated artifacts with spaces;ordinary'

function Test-RejectedPercentRoot([string]$Target) {
    $project = Join-Path $fixture ('rejected percent root ' + $Target)
    $artifacts = Join-Path $fixture ('rejected artifacts ' + $Target + ';literal%3B')
    Initialize-Fixture $project
    $propertyRoot = $artifacts.Replace('%', '%25').Replace(';', '%3B')
    $log = Join-Path $fixture ('rejected-percent-' + $Target + '.log')
    if ($Target -eq 'BuildContentLibrary') {
        $arguments = @((Join-Path $project 'APIShared.csproj'), '/t:BuildContentLibrary',
            '/p:Configuration=Release', "/p:APISharedArtifactsRoot=$propertyRoot", '/nologo')
        & $MSBuild @arguments *> $log
    } else {
        # Actual SDK Restore on tiny Content sources; no API/game references.
        $arguments = @('msbuild', (Join-Path $project 'Content/APIShared.Content.csproj'), '/t:Restore',
            '/p:Configuration=Release', "/p:APISharedArtifactsRoot=$propertyRoot", '/nologo')
        # The standalone fixture may inherit an isolated CLI home. Force these
        # child defaults explicitly and restore the fixture process afterward.
        $priorFirstUse = @{}
        foreach ($name in @('DOTNET_ADD_GLOBAL_TOOLS_TO_PATH', 'DOTNET_GENERATE_ASPNET_CERTIFICATE')) {
            $priorFirstUse[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        }
        try {
            foreach ($name in $priorFirstUse.Keys) { [Environment]::SetEnvironmentVariable($name, 'false', 'Process') }
            & dotnet @arguments *> $log
        }
        finally {
            foreach ($name in $priorFirstUse.Keys) {
                [Environment]::SetEnvironmentVariable($name, $priorFirstUse[$name], 'Process')
            }
        }
    }
    $exit = $LASTEXITCODE
    $output = [IO.File]::ReadAllText($log)
    # The stable code distinguishes declared refusal from unrelated tool failure.
    if ($exit -eq 0 -or $output -notmatch '\bAPISHARED1001\b') {
        throw "Percent root did not receive the named refusal for $Target; see $log"
    }
    if ($output -match '(?m)^\s+dotnet build ') { throw "Rejected root launched nested Content for $Target." }
    foreach ($relative in @('BepInEx', 'bin', 'obj', 'Content/bin', 'Content/obj', 'tests/APISharedTests/bin', 'tests/APISharedTests/obj')) {
        if (Test-Path -LiteralPath (Join-Path $project $relative)) {
            throw "Rejected root created a default build artifact directory for ${Target}: $relative"
        }
    }
    # Check the observed wrong decoded name too; relocation is not refusal.
    foreach ($path in @($artifacts, $artifacts.Replace('%3B', ';'))) {
        if (Test-Path -LiteralPath $path) { throw "Rejected root created or redirected artifacts for ${Target}: $path" }
    }
    Write-Host "PASS: $Target rejects literal percent roots before build artifacts or nested Content launch."
}

Test-RejectedPercentRoot 'BuildContentLibrary'
Test-RejectedPercentRoot 'Restore'
Write-Host 'PASS: Content incrementality and exact spaces/semicolon routing; percent roots explicitly refused before build/restore.'
Write-Host "Fixture retained: $fixture"
