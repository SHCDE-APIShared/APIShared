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
    # Diagnostic-only fixture instrumentation. The real production project,
    # its routing expressions and its Exec environment are unchanged.
    $apiPath = Join-Path $ProjectDirectory 'APIShared.csproj'
    $apiXml = New-Object Xml.XmlDocument
    $apiXml.PreserveWhitespace = $true
    $apiXml.Load($apiPath)
    $contentExec = @($apiXml.GetElementsByTagName('Exec') | Where-Object {
        $_.ParentNode.GetAttribute('Name') -eq 'BuildContentLibrary'
    })
    if ($contentExec.Count -ne 1) { throw 'One actual Content build command is required.' }
    # GetProperty with an explicit Build target records values from the same
    # SDK invocation that builds Content, not a substitute evaluation process.
    $properties = 'APISharedArtifactsRoot,_APISharedArtifactsRoot,BaseOutputPath,BaseIntermediateOutputPath,MSBuildProjectExtensionsPath,OutputPath,IntermediateOutputPath'
    $contentExec[0].SetAttribute('Command', ($contentExec[0].GetAttribute('Command') + ' -target:Build -getProperty:' + $properties))
    [IO.File]::WriteAllText($apiPath, $apiXml.OuterXml, [Text.UTF8Encoding]::new($false))
    Write-Fixture 'Content/APIShared.Content.csproj' @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>netstandard2.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup>
  <Target Name="RecordArtifactEnvironment" BeforeTargets="CoreCompile">
    <!-- No environment override: the probe inherits the actual SDK task's OS
         environment. Root is read by PowerShell and encoded before any MSBuild
         property/list formatting. It never appears in this command string. -->
    <Exec Command="powershell.exe -NoProfile -NonInteractive -File &quot;$(MSBuildProjectDirectory)/RecordArtifactEnvironment.ps1&quot; -Report &quot;$(MSBuildProjectDirectory)/ARTIFACT_ENVIRONMENT.json&quot;" />
  </Target>
</Project>
'@ $ProjectDirectory
    Write-Fixture 'Content/RecordArtifactEnvironment.ps1' @'
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Report)
$ErrorActionPreference = 'Stop'
$rootValue = [Environment]::GetEnvironmentVariable('APISharedArtifactsRoot', 'Process')
$encoded = $null
if ($null -ne $rootValue) { $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($rootValue)) }
$record = [ordered]@{
    schema = 'content-sdk-inherited-os-environment/1'
    observer = 'PowerShell child of actual SDK CoreCompile-before target; no environment overrides'
    processId = $PID
    rootPresent = ($null -ne $rootValue)
    rootUtf8Base64 = $encoded
    addGlobalToolsToPath = [Environment]::GetEnvironmentVariable('DOTNET_ADD_GLOBAL_TOOLS_TO_PATH', 'Process')
    generateAspnetCertificate = [Environment]::GetEnvironmentVariable('DOTNET_GENERATE_ASPNET_CERTIFICATE', 'Process')
}
[IO.File]::WriteAllText($Report, ($record | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))
'@ $ProjectDirectory
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
function Record-OuterArtifactProperties([string]$Label, [string]$ProjectDirectory, [string]$ArtifactsRoot) {
    # Use the same actual outer executable, project, Configuration and /p codec
    # as Invoke-FixtureTarget. With no target, GetProperty does not resolve game
    # references or build the API. JSON avoids path-as-item-list diagnostics.
    $propertyRoot = $ArtifactsRoot.Replace('%', '%25').Replace(';', '%3B')
    $properties = 'APISharedArtifactsRoot,_APISharedArtifactsRoot,BaseOutputPath,BaseIntermediateOutputPath,MSBuildProjectExtensionsPath,OutputPath,IntermediateOutputPath'
    $arguments = @((Join-Path $ProjectDirectory 'APIShared.csproj'), '/p:Configuration=Release', '/nologo',
        "/p:APISharedArtifactsRoot=$propertyRoot", ('-getProperty:' + $properties))
    $request = [ordered]@{ requestedRoot = $ArtifactsRoot; powershellArguments = $arguments; executable = $MSBuild }
    [IO.File]::WriteAllText((Join-Path $fixture ($Label + '-outer-request.json')),
        ($request | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    $log = Join-Path $fixture ($Label + '-outer-properties.json')
    & $MSBuild @arguments *> $log
    if ($LASTEXITCODE) { throw "Outer property evaluation failed; see $log" }
}
function Build-Content([string]$Label, [string]$ProjectDirectory = $fixture, [string]$ArtifactsRoot = '') {
    # Exercise the actual Framework project's target and the real SDK compiler.
    # The SDK remains responsible for incremental checks, including source removal.
    Record-OuterArtifactProperties $Label $ProjectDirectory $ArtifactsRoot
    Invoke-FixtureTarget $ProjectDirectory 'BuildContentLibrary' $Label $ArtifactsRoot
    # Retain raw inherited OS input before the existing expected-output read
    # can fail. The per-build copy cannot be overwritten by the next edit.
    $environmentReport = Join-Path $ProjectDirectory 'Content/ARTIFACT_ENVIRONMENT.json'
    if (-not (Test-Path -LiteralPath $environmentReport -PathType Leaf)) { throw 'Actual SDK environment probe did not run.' }
    Copy-Item -LiteralPath $environmentReport -Destination (Join-Path $fixture ($Label + '-sdk-environment.json'))
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
# The real semicolon and literal percent sequence expose delimiter splitting
# and unintended extra decoding after the outer /p input is escaped.
Test-IsolatedContent 'isolated-delimiters' 'isolated artifacts with spaces;literal%3B'
Write-Host 'PASS: Content rebuilds nested edits/additions/removals; spaces and semicolon/literal-percent roots resolve through both actual project HintPaths.'
Write-Host "Fixture retained: $fixture"
