$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Run this regression with Windows PowerShell 5.1.' }
. (Join-Path $PSScriptRoot '../Release/GitHubRelease.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('APIShared release test ' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$fake = Join-Path $fixture 'gh.exe'
$previousPath = $env:PATH
$previousCase = $env:APISHARED_RELEASE_TEST_CASE
$previousLog = $env:APISHARED_RELEASE_TEST_LOG
function Check([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Write-Fixture([string]$Path, [string]$Text) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
try {
    # A real executable reproduces PS 5.1 native stderr handling; it never contacts GitHub.
    Add-Type -OutputAssembly $fake -OutputType ConsoleApplication -TypeDefinition @'
using System;
using System.IO;
public static class FakeReleaseCli {
    public static int Main(string[] args) {
        string scenario = Environment.GetEnvironmentVariable("APISHARED_RELEASE_TEST_CASE");
        File.AppendAllText(Environment.GetEnvironmentVariable("APISHARED_RELEASE_TEST_LOG"), string.Join("|", args) + "\n");
        if (args[0] == "--validate" || args[0] == "--inspect") {
            if ((scenario == "package" && args[0] == "--validate") ||
                (scenario == "owner" && args[0] == "--inspect")) return 1;
            File.WriteAllText(args[2], "{\"Fingerprint\":\"fixture-fingerprint\"}");
            return 0;
        }
        if (args[0] == "--publish") { Console.Error.WriteLine("Unexpected publication"); return 99; }
        if (Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]) == "git") {
            if (Array.IndexOf(args, "rev-parse") >= 0) Console.WriteLine("0123456789abcdef0123456789abcdef01234567");
            return 0;
        }
        if (args[0] == "echo") { foreach (string arg in args) Console.WriteLine(arg); return 0; }
        if (args[0] == "api") {
            if (args[1] == "user") {
                if (scenario == "auth") { Console.Error.WriteLine("authentication failed"); return 4; }
                Console.WriteLine("FixtureAccount"); return 0;
            }
            if (scenario == "access") { Console.Error.WriteLine("HTTP 403: Forbidden"); return 1; }
            return 0;
        }
        if (scenario == "missing") { Console.Error.WriteLine("release not found"); return 1; }
        if (scenario == "network") { Console.Error.WriteLine("connection timed out"); return 1; }
        if (scenario == "ambiguous") { Console.Error.WriteLine("release not found\nHTTP 401"); return 1; }
        if (scenario == "large") { Console.Error.Write(new string('x', 100000)); Console.Write(new string('y', 100000)); return 2; }
        if (scenario == "json") { Console.WriteLine("invalid json"); return 0; }
        string commit = scenario == "commit" ? "other" : "0123456789abcdef0123456789abcdef01234567";
        string draft = scenario == "mode" ? "true" : "false";
        Console.WriteLine("{\"tagName\":\"APIShared/v0.7.1\",\"isDraft\":" + draft + ",\"targetCommitish\":\"" + commit + "\",\"assets\":[]}");
        return 0;
    }
}
'@
    $env:APISHARED_RELEASE_TEST_LOG = Join-Path $fixture 'calls.log'
    $env:APISHARED_RELEASE_TEST_CASE = 'missing'
    Check ($null -eq (Get-ApiGitHubRelease 'Fixture/APIShared' 'APIShared/v0.7.1' $fake)) 'Missing release did not reach the creation decision.'
    $env:APISHARED_RELEASE_TEST_CASE = 'existing'
    $release = Get-ApiGitHubRelease 'Fixture/APIShared' 'APIShared/v0.7.1' $fake
    Check ($release.targetCommitish -ceq '0123456789abcdef0123456789abcdef01234567') 'Existing release not returned.'
    foreach ($case in @('auth','access','network','ambiguous','json')) {
        $env:APISHARED_RELEASE_TEST_CASE = $case
        $rejected = $false
        try { $null = Get-ApiGitHubRelease 'Fixture/APIShared' 'APIShared/v0.7.1' $fake } catch { $rejected = $true }
        Check $rejected ('Failure accepted as missing release: ' + $case)
    }
    $env:APISHARED_RELEASE_TEST_CASE = 'large'
    $result = Invoke-ApiReleaseGitHub @('release','view','test') $fake
    Check ($result.ExitCode -eq 2 -and $result.Output.Length -eq 100000 -and $result.Diagnostic.Length -eq 100000) 'Concurrent pipe capture failed.'
    $arguments = @('echo','path with spaces','quote"inside','C:\trailing space\','a&b','')
    $result = Invoke-ApiReleaseGitHub $arguments $fake
    Check ($result.Output -ceq (($arguments -join "`r`n") + "`r`n")) 'Windows argument quoting changed arguments.'

    # Maintainer workspace drivers are not part of the standalone APIShared checkout.
    # Always test APIShared's own release probe and publication decision; exercise
    # the extra drivers only when the surrounding maintainer workspace is present.
    $env:PATH = $fixture + ';' + $previousPath
    $driverSource = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../Shared/Tools/Steam/Prepare-APISharedWorkshop.ps1'))
    if (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($driverSource))) {
    # Run the actual preparation driver in an isolated workspace with inert dependencies.
    $workspace = Join-Path $fixture 'workspace with spaces'
    $driverDirectory = Join-Path $workspace 'Shared/Tools/Steam'
    $driver = Join-Path $driverDirectory 'Prepare-APISharedWorkshop.ps1'
    Write-Fixture $driver ([IO.File]::ReadAllText($driverSource))
    $configuration = @'
function Get-WorkshopConfiguration {
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    return @{ Root=$root; Packager=(Join-Path $root 'packager.exe'); APIShared=@{Guid='APIShared_Serp';Preview=(Join-Path $root 'preview.png')} }
}
'@
    Write-Fixture (Join-Path $driverDirectory 'WorkshopConfiguration.ps1') $configuration
    Write-Fixture (Join-Path $driverDirectory 'WorkshopWorkflow.Common.ps1') '# No platform-approval dependency.'
    Write-Fixture (Join-Path $workspace 'APIShared/info.json') '{"GUID":"APIShared_Serp","Version":"0.7.1"}'
    Write-Fixture (Join-Path $workspace 'APIShared/tools/BuildProof.ps1') 'function Assert-ApiBuildProof {}'
    Write-Fixture (Join-Path $workspace 'APIShared/tools/Release/GitHubRelease.ps1') ([IO.File]::ReadAllText((Join-Path $PSScriptRoot '../Release/GitHubRelease.ps1')))
    Write-Fixture (Join-Path $workspace 'packager.exe') 'inert fixture'
    Write-Fixture (Join-Path $workspace 'preview.png') 'inert fixture'
    Copy-Item -LiteralPath $fake -Destination (Join-Path $fixture 'git.exe')
    $env:PATH = $fixture + ';' + $previousPath
    $powershell = Join-Path $PSHOME 'powershell.exe'
    foreach ($case in @('missing','existing','commit','mode','auth','access','network')) {
        $env:APISHARED_RELEASE_TEST_CASE = $case
        $result = Invoke-ApiReleaseGitHub @('-NoProfile','-ExecutionPolicy','Bypass','-File',$driver,'-ValidateOnly','1') $powershell
        $expected = if ($case -in @('missing','existing')) { 0 } else { 1 }
        Check ($result.ExitCode -eq $expected) ("Preparation /validate failed for ${case}: " + $result.Diagnostic)
        if ($expected -eq 0) { Check ($result.Output -match 'no release or build started') 'Validation did not exit before side effects.' }
    }
    # Stop at the existing human confirmation without accepting it or starting a release.
    $confirmationDriver = Join-Path $fixture 'confirmation wrapper.ps1'
    Write-Fixture $confirmationDriver @'
param([string]$Driver,[switch]$ValidateUpload)
function Read-Host { param([string]$Prompt) throw ("Fixture confirmation reached: " + $Prompt) }
if ($ValidateUpload) { & $Driver -Target APIShared -Validate }
elseif ($Driver.EndsWith('Upload-Workshop.ps1')) { & $Driver -Target APIShared }
else { & $Driver }
exit $LASTEXITCODE
'@
    $platformPath = Join-Path $workspace 'APIShared/.local/workshop-platform-tests.json'
    foreach ($record in @('absent','outdated','malformed')) {
        if ($record -eq 'outdated') { Write-Fixture $platformPath '{"SchemaVersion":1,"Windows":false}' }
        if ($record -eq 'malformed') { Write-Fixture $platformPath 'invalid json' }
        $env:APISHARED_RELEASE_TEST_CASE = 'missing'
        $result = Invoke-ApiReleaseGitHub @('-NoProfile','-ExecutionPolicy','Bypass','-File',$confirmationDriver,'-Driver',$driver) $powershell
        Check ($result.ExitCode -eq 1 -and $result.Diagnostic -match 'Fixture confirmation reached:\s*Type\s+VEROEFFENTLICHEN') ("Production preparation blocked by " + $record + " approval record: " + $result.Diagnostic)
    }
    # Exercise the actual upload driver with an inert publisher and isolated preparation state.
    $upload = Join-Path $driverDirectory 'Upload-Workshop.ps1'
    Write-Fixture $upload ([IO.File]::ReadAllText((Join-Path ([IO.Path]::GetDirectoryName($driverSource)) 'Upload-Workshop.ps1')))
    Write-Fixture (Join-Path $driverDirectory 'WorkshopChangelog.ps1') ([IO.File]::ReadAllText((Join-Path ([IO.Path]::GetDirectoryName($driverSource)) 'WorkshopChangelog.ps1')))
    Write-Fixture (Join-Path $driverDirectory 'SteamWorkshopHistory.ps1') ([IO.File]::ReadAllText((Join-Path ([IO.Path]::GetDirectoryName($driverSource)) 'SteamWorkshopHistory.ps1')))
    Write-Fixture (Join-Path $driverDirectory 'WorkshopConfiguration.ps1') @'
function Get-WorkshopConfiguration {
    $root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
    return @{Root=$root;AppId=3024040;APIShared=@{Guid='APIShared_Serp';Title='Fixture';Visibility='Public'};SerpsMods=@{Guid='SerpsMods_Serp'}}
}
'@
    Write-Fixture (Join-Path $driverDirectory 'WorkshopWorkflow.Common.ps1') @'
function Get-WorkshopFolder($Config,$Target,$Test) { return (Join-Path $Config.Root 'content') }
function Assert-WorkshopPreparation {
    if ($env:APISHARED_RELEASE_TEST_CASE -eq 'proof') { throw 'Prepared file changed: APIShared.map' }
    return @{Version='0.7.1';ReleaseNotes='Fixture';ReleaseTag='fixture';Files=@(@{Path='APIShared.map'})}
}
function Assert-WorkshopPublisherBuild {}
function Get-WorkshopStateRoot($Config,$Test) { return (Join-Path $Config.Root 'state') }
function Get-WorkshopItemId { return '' }
function Write-SteamWorkshopJson($Path,$Value) { [IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 20)) }
'@
    $publisher = Join-Path $workspace 'APIShared/.local/WorkshopUploader/WorkshopUploader.exe'
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $publisher))
    Copy-Item -LiteralPath $fake -Destination $publisher
    Write-Fixture (Join-Path $workspace 'content/APIShared.map') 'inert map'
    foreach ($record in @('absent','outdated','malformed')) {
        if ($record -eq 'absent') { Remove-Item -LiteralPath $platformPath }
        elseif ($record -eq 'outdated') { Write-Fixture $platformPath '{"SchemaVersion":1,"Windows":false}' }
        else { Write-Fixture $platformPath 'invalid json' }
        $env:APISHARED_RELEASE_TEST_CASE = 'missing'
        $result = Invoke-ApiReleaseGitHub @('-NoProfile','-ExecutionPolicy','Bypass','-File',$confirmationDriver,'-Driver',$upload) $powershell
        Check ($result.ExitCode -eq 1 -and $result.Output -match 'Fixture confirmation reached:\s*Type\s+HOCHLADEN') ("Production upload blocked by " + $record + " approval record: " + $result.Output + $result.Diagnostic)
        if ($record -ne 'absent') {
            $expected = if ($record -eq 'outdated') { '{"SchemaVersion":1,"Windows":false}' } else { 'invalid json' }
            Check ([IO.File]::ReadAllText($platformPath) -ceq $expected) 'Platform record was modified.'
        }
    }
    foreach ($case in @('proof','package','owner')) {
        $env:APISHARED_RELEASE_TEST_CASE = $case
        $result = Invoke-ApiReleaseGitHub @('-NoProfile','-ExecutionPolicy','Bypass','-File',$confirmationDriver,'-Driver',$upload) $powershell
        $expected = switch ($case) { 'proof' {'Prepared file changed'} 'package' {'Complete package validation failed'} 'owner' {'Steam item/prerequisite validation failed'} }
        Check ($result.ExitCode -eq 1 -and $result.Output -match $expected -and $result.Output -notmatch 'Fixture confirmation reached') ("Upload accepted " + $case + " failure.")
    }
    $env:APISHARED_RELEASE_TEST_CASE = 'missing'
    $calls = [IO.File]::ReadAllText($env:APISHARED_RELEASE_TEST_LOG)
    Check ($calls -notmatch '--publish') 'Production test attempted a Steam publication.'
    Check ($calls -notmatch 'release\|(create|upload|edit|download)') 'Production test attempted a release mutation.'
    [IO.File]::WriteAllText($env:APISHARED_RELEASE_TEST_LOG,'')
    $result = Invoke-ApiReleaseGitHub @('-NoProfile','-ExecutionPolicy','Bypass','-File',$confirmationDriver,'-Driver',$upload,'-ValidateUpload') $powershell
    Check ($result.ExitCode -eq 0 -and $result.Output -match 'without Steam initialization or upload') 'Upload /validate failed.'
    $validationCalls = [IO.File]::ReadAllText($env:APISHARED_RELEASE_TEST_LOG)
    Check ($validationCalls -match '--validate' -and $validationCalls -notmatch '--inspect|--publish') 'Upload /validate contacted Steam.'
    foreach ($relative in @('WorkshopWorkflow.Common.ps1','Prepare-APISharedWorkshop.ps1','Create-SteamModPack.ps1','Upload-Workshop.ps1')) {
        $source = [IO.File]::ReadAllText((Join-Path ([IO.Path]::GetDirectoryName($driverSource)) $relative))
        Check ($source -notmatch 'Assert-WorkshopPlatformProof|workshop-platform-tests.json') ("Approval dependency retained in " + $relative)
    }
    $calls = [IO.File]::ReadAllText($env:APISHARED_RELEASE_TEST_LOG)
    Check ($calls -notmatch 'release\|(create|upload|edit|download)') 'Read-only test attempted a release mutation.'
    Write-Host 'PASS: maintainer preparation/upload drivers and production confirmations.'
    } else {
        Write-Host 'INFO: maintainer workspace drivers are unavailable in this standalone checkout; their extra integration tests run locally.'
    }
    $releaseDriver = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../Release/Release.ps1'))
    Check ($releaseDriver -match 'Get-ApiGitHubRelease \$repository \$tag' -and $releaseDriver -notmatch 'gh release view') 'Standalone release driver bypasses the shared probe.'
    # Execute the actual publication decision with mutation commands replaced by an inert function.
    # No build or packaging prefix of Release.ps1 is executed by this test.
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../Release/Release.ps1'), [ref]$tokens, [ref]$errors)
    $decision = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and $node.Clauses[0].Item1.Extent.Text -match '\$PSCmdlet.ShouldProcess'
    }, $true))
    Check ($decision.Count -eq 1 -and -not $errors) 'Standalone publication decision not found.'
    $body = $decision[0].Clauses[0].Item2.Extent.Text
    $publication = [scriptblock]::Create($body.Substring(1, $body.Length - 2))
    $repository = 'Fixture/APIShared'; $tag = 'APIShared/v0.7.1'
    $commit = '0123456789abcdef0123456789abcdef01234567'
    $assets = @(); $notes = 'inert notes'; $Publish = $false
    $arguments = @('release','create',$tag)
    function gh { $script:mutations += [string]$args[1]; $global:LASTEXITCODE = 0 }
    foreach ($case in @('missing','mode','existing','commit','auth','access','network')) {
        $env:APISHARED_RELEASE_TEST_CASE = $case
        $script:mutations = @(); $rejected = $false
        try { & $publication } catch { $rejected = $true }
        if ($case -eq 'missing') { Check (-not $rejected -and ($script:mutations -join ',') -ceq 'create') 'First release did not select creation.' }
        elseif ($case -eq 'mode') { Check (-not $rejected -and ($script:mutations -join ',') -ceq 'upload,edit') 'Matching draft did not resume.' }
        else { Check ($rejected -and $script:mutations.Count -eq 0) ('Standalone release mutated after failure: ' + $case) }
    }
    Write-Host ('PASS: PS 5.1 standalone release probe/publication decision, missing/existing releases, authentication/access/network failures, strict missing detection, pipe capture, argument quoting and commit/mode conflicts. Fixtures: ' + $fixture)
} finally {
    if (Test-Path Function:gh) { Remove-Item Function:gh }
    $env:PATH = $previousPath
    $env:APISHARED_RELEASE_TEST_CASE = $previousCase
    $env:APISHARED_RELEASE_TEST_LOG = $previousLog
}
