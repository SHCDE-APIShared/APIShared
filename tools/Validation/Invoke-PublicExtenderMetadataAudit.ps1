#requires -Version 7.4
<#
.SYNOPSIS
Reads selected metadata from a hash-pinned public Extender release, never a game installation.
.DESCRIPTION
The fixture is an explicit package/version audit, not an automatic compatibility gate.
Only the inspector process executes; its targets are data, with no dependency resolution.
Output is bounded JSON. Downloads and extracted DLLs belong to a fresh temporary directory
and are deleted individually after the audit. No target DLL or archive is an upload output.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ToolAssemblyPath,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$FixturePath = (Join-Path $PSScriptRoot 'PublicExtenderMetadata.2.14.4.json')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$artifactLimit = 8MB
$manifestLimit = 128KB

function Get-BytesHash([byte[]]$Bytes) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}
function Get-FilePin([string]$Path) {
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Expected a plain file.' }
    return [ordered]@{ bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
function Assert-ChildPath([string]$Path, [string]$Parent) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Owned file escaped its temporary directory.' }
    return $full
}
function Copy-BoundedStream([IO.Stream]$Source, [string]$Destination, [long]$ExpectedBytes,
    [Threading.CancellationToken]$CancellationToken) {
    $output = [IO.FileStream]::new($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $buffer = [byte[]]::new(65536)
        [long]$total = 0
        while (($read = $Source.ReadAsync($buffer, 0, $buffer.Length, $CancellationToken).GetAwaiter().GetResult()) -gt 0) {
            $total += $read
            if ($total -gt $ExpectedBytes) { throw 'Stream exceeded its exact fixture length.' }
            $output.Write($buffer, 0, $read)
        }
        if ($total -ne $ExpectedBytes) { throw 'Stream did not match its exact fixture length.' }
    } finally { $output.Dispose() }
}
function Receive-PublicArchive([object]$Archive, [string]$Destination) {
    $uri = [uri]$Archive.url
    if ($uri.Scheme -cne 'https' -or $uri.Host -cne 'gitlab.com' -or
        $uri.AbsolutePath -notmatch '^/api/v4/projects/[0-9]+/packages/generic/[^/]+/[^/]+/[^/]+\.zip$' -or $uri.Query) {
        throw 'Fixture must name a public GitLab HTTPS generic-package ZIP.'
    }
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $true; $handler.MaxAutomaticRedirections = 3
    $client = [Net.Http.HttpClient]::new($handler)
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(120))
    $response = $null; $stream = $null
    try {
        $client.DefaultRequestHeaders.UserAgent.ParseAdd('APIShared-PublicMetadataAudit/1')
        $response = $client.GetAsync($uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $timeout.Token).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) { throw "Public package returned HTTP $([int]$response.StatusCode)." }
        if ($response.RequestMessage.RequestUri.Scheme -cne 'https') { throw 'Public package redirected outside HTTPS.' }
        $length = $response.Content.Headers.ContentLength
        if ($null -ne $length -and $length -ne [long]$Archive.bytes) { throw 'HTTP length differs from the pinned package.' }
        $stream = $response.Content.ReadAsStreamAsync($timeout.Token).GetAwaiter().GetResult()
        Copy-BoundedStream $stream $Destination ([long]$Archive.bytes) $timeout.Token
    } finally {
        if ($stream) { $stream.Dispose() }; if ($response) { $response.Dispose() }
        $timeout.Dispose(); $client.Dispose(); $handler.Dispose()
    }
    $pin = Get-FilePin $Destination
    if ($pin.bytes -ne [long]$Archive.bytes -or $pin.sha256 -cne [string]$Archive.sha256) { throw 'Downloaded public archive hash mismatch.' }
    return $pin
}
function Expand-SelectedAssemblies([string]$ArchivePath, [object[]]$Assemblies, [string]$WorkRoot,
    [Collections.Generic.List[string]]$OwnedFiles) {
    $wanted = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($assembly in $Assemblies) {
        if ($assembly.name -notmatch '^[A-Za-z0-9.]+\.dll$' -or $assembly.entry -cne ('BepInEx/plugins/000shcdese/' + $assembly.name) -or
            $assembly.sha256 -notmatch '^[0-9a-f]{64}$' -or $assembly.bytes -le 0 -or $assembly.bytes -gt 2MB) { throw 'Invalid selected public assembly fixture.' }
        $wanted.Add([string]$assembly.entry, $assembly)
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        if ($zip.Entries.Count -gt 4096) { throw 'Public archive entry count exceeds audit bound.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $selected = @{}
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if (-not $name -or $name.StartsWith('/') -or $name.Contains(':') -or
                @($name.TrimEnd('/').Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -or -not $seen.Add($name)) {
                throw 'Public archive contains an unsafe or duplicate path.'
            }
            if ($wanted.ContainsKey($name)) { $selected[$name] = $entry }
        }
        if ($selected.Count -ne $wanted.Count) { throw 'Public archive lacks a required exact entry.' }
        foreach ($name in $wanted.Keys) {
            $assembly = $wanted[$name]; $entry = $selected[$name]
            if ($entry.Length -ne [long]$assembly.bytes) { throw 'Selected ZIP entry length differs from fixture.' }
            $path = Assert-ChildPath (Join-Path $WorkRoot $assembly.name) $WorkRoot
            $OwnedFiles.Add($path)
            $stream = $entry.Open()
            try { Copy-BoundedStream $stream $path ([long]$assembly.bytes) ([Threading.CancellationToken]::None) }
            finally { $stream.Dispose() }
            $pin = Get-FilePin $path
            if ($pin.sha256 -cne [string]$assembly.sha256) { throw 'Selected public DLL hash differs from fixture.' }
        }
    } finally { $zip.Dispose() }
}
function Invoke-MetadataProbe([string]$Dotnet, [string]$Tool, [string[]]$Arguments, [string]$WorkingDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Dotnet; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.WorkingDirectory = $WorkingDirectory
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Environment['DOTNET_ADD_GLOBAL_TOOLS_TO_PATH'] = 'false'
    $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $start.ArgumentList.Add($Tool)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Could not start metadata inspector.' }
        # This is the repository-built inspector, whose status/error printing is bounded.
        # These readers are not an in-flight memory limit for arbitrary child programs.
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(45000)) { $process.Kill($true); $process.WaitForExit(); throw 'Metadata inspector exceeded 45 seconds.' }
        $outText = $stdout.GetAwaiter().GetResult(); $errText = $stderr.GetAwaiter().GetResult()
        if ($outText.Length -gt 65536 -or $errText.Length -gt 65536) { throw 'Metadata inspector diagnostic output exceeded its bound.' }
        if ($process.ExitCode -ne 0) { throw "Metadata inspector refused: $($errText.Substring(0, [Math]::Min(2048, $errText.Length)))" }
    } finally { $process.Dispose() }
}

$workRoot = [IO.Path]::GetFullPath($WorkDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (Test-Path -LiteralPath $workRoot) { throw 'Work directory must be fresh.' }
if (Test-Path -LiteralPath $outputRoot) { throw 'Output directory must be fresh.' }
if ($workRoot -eq $outputRoot -or $workRoot.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith($workRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output and work directories must be disjoint.' }
$tool = (Resolve-Path -LiteralPath $ToolAssemblyPath).Path
$fixtureBytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $FixturePath).Path)
if ($fixtureBytes.Length -gt 65536) { throw 'Public fixture exceeds its bound.' }
$fixture = [Text.Encoding]::UTF8.GetString($fixtureBytes) | ConvertFrom-Json
if ($fixture.schema -cne 'public-extender-metadata-fixture/1' -or $fixture.archive.bytes -le 0 -or
    $fixture.archive.bytes -gt 64MB -or $fixture.archive.sha256 -notmatch '^[0-9a-f]{64}$' -or
    @($fixture.assemblies).Count -lt 1 -or @($fixture.assemblies).Count -gt 4 -or
    @($fixture.probes).Count -lt 1 -or @($fixture.probes).Count -gt 8) { throw 'Invalid public audit fixture bounds.' }
$declaredOutputBytes = [long]0
foreach ($probe in $fixture.probes) {
    if ($probe.maxOutputBytes -le 0 -or $probe.maxOutputBytes -gt 2MB) { throw 'Invalid probe output bound.' }
    $declaredOutputBytes += [long]$probe.maxOutputBytes
}
if ($declaredOutputBytes + $manifestLimit -gt $artifactLimit) { throw 'Declared JSON bounds exceed the artifact budget.' }
[void][IO.Directory]::CreateDirectory($workRoot); [void][IO.Directory]::CreateDirectory($outputRoot)
$ownedFiles = [Collections.Generic.List[string]]::new()
$reportCaps = [Collections.Generic.Dictionary[string, long]]::new([StringComparer]::OrdinalIgnoreCase)
$audit = [ordered]@{ schema = 'public-extender-metadata-audit/1'; status = 'running'; release = $fixture.release;
    commit = $env:GITHUB_SHA; fixtureSha256 = (Get-BytesHash $fixtureBytes); tool = (Get-FilePin $tool);
    archiveUrl = $fixture.archive.url; archive = $null; probes = @(); failure = $null;
    limits = 'Public package metadata only: no target execution, dependency resolution, installed folders, native ABI, payment or live-game qualification.' }
try {
    $archivePath = Assert-ChildPath (Join-Path $workRoot 'public-extender.zip') $workRoot; $ownedFiles.Add($archivePath)
    $audit.archive = Receive-PublicArchive $fixture.archive $archivePath
    Expand-SelectedAssemblies $archivePath @($fixture.assemblies) $workRoot $ownedFiles
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($probe in $fixture.probes) {
        if ($probe.id -notmatch '^[a-z0-9-]{1,80}$' -or $probe.id -eq 'audit' -or -not $ids.Add([string]$probe.id) -or
            $probe.maxOutputBytes -le 0 -or $probe.maxOutputBytes -gt 2MB) { throw 'Invalid probe identity or output bound.' }
        $assemblies = @($fixture.assemblies | Where-Object name -CEQ $probe.assembly)
        if ($assemblies.Count -ne 1) { throw 'Probe must select exactly one allowlisted assembly.' }
        $assembly = $assemblies[0]; $target = Assert-ChildPath (Join-Path $workRoot $assembly.name) $workRoot
        $output = Assert-ChildPath (Join-Path $outputRoot ($probe.id + '.json')) $outputRoot
        $reportCaps.Add($output, [long]$probe.maxOutputBytes)
        $arguments = @('--input', $target, '--sha256', [string]$assembly.sha256, '--output', $output,
            '--types', [string]$probe.type, '--max-output-bytes', [string]$probe.maxOutputBytes)
        if ($probe.includeNested) { $arguments += '--include-nested' }
        if ($probe.PSObject.Properties['methods']) { $arguments += @('--methods', ($probe.methods -join ';')) }
        if ($probe.PSObject.Properties['calledMember']) { $arguments += @('--called-member', [string]$probe.calledMember) }
        $before = Get-FilePin $target
        Invoke-MetadataProbe $dotnet $tool $arguments $workRoot
        $after = Get-FilePin $target; $result = Get-FilePin $output
        if ($result.bytes -gt [long]$probe.maxOutputBytes) {
            Remove-Item -LiteralPath (Assert-ChildPath $output $outputRoot) -Force
            throw 'Metadata probe output exceeded its bound; the oversized report was removed.'
        }
        if ($before.sha256 -cne $after.sha256 -or $before.sha256 -cne [string]$assembly.sha256) { throw 'Metadata probe changed its input.' }
        $report = [IO.File]::ReadAllText($output) | ConvertFrom-Json
        if ($report.schema -ne 1 -or $report.inputSha256 -cne [string]$assembly.sha256 -or $report.inputBytes -ne [long]$assembly.bytes) {
            throw 'Metadata JSON did not identify the selected public input.'
        }
        $audit.probes += [ordered]@{ id = $probe.id; input = $assembly.name; before = $before; after = $after;
            arguments = $arguments; output = [ordered]@{ name = ($probe.id + '.json'); bytes = $result.bytes; sha256 = $result.sha256 } }
    }
    $audit.status = 'passed'
} catch {
    $audit.status = 'failed'; $message = $_.Exception.Message
    $audit.failure = $message.Substring(0, [Math]::Min(2048, $message.Length))
} finally {
    # The upload step also runs after refusal. Retain only our recognized, bounded
    # reports; never allow a failed child to leave an oversized JSON upload behind.
    foreach ($file in Get-ChildItem -LiteralPath $outputRoot -File -Filter '*.json') {
        $verifiedOutput = Assert-ChildPath $file.FullName $outputRoot
        if (-not $reportCaps.ContainsKey($verifiedOutput) -or
            ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -gt $reportCaps[$verifiedOutput]) {
            Remove-Item -LiteralPath $verifiedOutput -Force
            $audit.status = 'failed'; $audit.failure = 'An unrecognized or oversized JSON report was removed.'
        }
    }
    foreach ($path in $ownedFiles) {
        $verifiedPath = Assert-ChildPath $path $workRoot
        if (Test-Path -LiteralPath $verifiedPath) { Remove-Item -LiteralPath $verifiedPath -Force }
    }
    # Non-recursive removal also refuses unexpected leftovers rather than deleting them.
    if (Test-Path -LiteralPath $workRoot) { [IO.Directory]::Delete($workRoot, $false) }
    $json = $audit | ConvertTo-Json -Depth 12
    $manifestBytes = [Text.UTF8Encoding]::new($false).GetBytes($json + "`n")
    if ($manifestBytes.Length -gt $manifestLimit) { throw 'Audit provenance exceeded 128 KiB.' }
    $total = ($manifestBytes.Length + [long](Get-ChildItem -LiteralPath $outputRoot -File -Filter '*.json' | Measure-Object Length -Sum).Sum)
    if ($total -gt $artifactLimit) { throw 'Combined JSON exceeded 8 MiB.' }
    [IO.File]::WriteAllBytes((Assert-ChildPath (Join-Path $outputRoot 'audit.json') $outputRoot), $manifestBytes)
}
if ($audit.status -cne 'passed') { throw $audit.failure }
Write-Host "PASS: $($audit.probes.Count) public-release metadata probes; JSON only."
