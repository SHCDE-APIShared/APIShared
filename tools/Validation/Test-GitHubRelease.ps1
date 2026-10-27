$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -ne 5) { throw 'Run this regression with Windows PowerShell 5.1.' }
. (Join-Path $PSScriptRoot '../Release/GitHubRelease.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('APIShared release test ' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$fake = Join-Path $fixture 'gh.exe'
$previousPath = $env:PATH
$previousCase = $env:APISHARED_RELEASE_TEST_CASE
$previousLog = $env:APISHARED_RELEASE_TEST_LOG
$previousTag = $env:APISHARED_RELEASE_TEST_TAG
$fixtureVersion = [string]([IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../info.json')) | ConvertFrom-Json).Version
$fixtureTag = 'APIShared/v' + $fixtureVersion
$env:APISHARED_RELEASE_TEST_TAG = $fixtureTag
function Check([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
# Release notes must include every unpublished entry, even when intermediate versions
# never had a GitHub release. Drafts and prereleases cannot advance that baseline.
$notesManifest = @{ Version='2.1.3'; SerpChangelog=@(
    @{Version='2.1.4';Changes=@('future')}, @{Version='2.1.1';Changes=@('first change')},
    @{Version='2.1.3';Changes=@('latest change')}, @{Version='2.1.0';Changes=@('published change')}) }
$prior = Get-ApiPreviousReleaseVersion @(
    @{tagName='APIShared/v2.1.2';isDraft=$true;isPrerelease=$false},
    @{tagName='APIShared/v2.1.1';isDraft=$false;isPrerelease=$true},
    @{tagName='APIShared/v2.1.0';isDraft=$false;isPrerelease=$false},
    @{tagName='Other/v9.0.0';isDraft=$false;isPrerelease=$false}) $notesManifest.Version
Check ($prior -ceq '2.1.0') 'Release notes used a draft/prerelease/unrelated baseline.'
$notes = Get-ApiReleaseChangeNotes $notesManifest $prior
Check ($notes -ceq "### v2.1.3`r`n`r`n- latest change`r`n`r`n### v2.1.1`r`n`r`n- first change") 'Release notes lost, reordered or included already published/future changes.'
Check ((Get-ApiReleaseChangeNotes $notesManifest '').Contains('published change')) 'Initial-release notes lost an existing change.'
foreach ($invalid in @(
    @{Version='2.1.3';SerpChangelog=@(@{Version='2.1.2';Changes=@('missing current')})},
    @{Version='2.1.3';SerpChangelog=@(@{Version='2.1.3';Changes=@('one')},@{Version='2.1.3';Changes=@('duplicate')})},
    @{Version='2.1.3';SerpChangelog=@(@{Version='2.1.3';Changes=@(' ')})},
    @{Version='2.1.3';SerpChangelog=@(@{Version='2.1.3';Changes=@()})})) {
    $rejected = $false
    try { $null = Get-ApiReleaseChangeNotes $invalid '2.1.0' } catch { $rejected = $true }
    Check $rejected 'Invalid changelog was accepted for publication.'
}
Write-Host 'PASS: release changelog range, ordering, initial release and invalid entries.'
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
        if (args[0] == "release" && (args[1] == "create" || args[1] == "upload" || args[1] == "edit")) return 0;
        if (scenario == "missing") { Console.Error.WriteLine("release not found"); return 1; }
        if (scenario == "network") { Console.Error.WriteLine("connection timed out"); return 1; }
        if (scenario == "ambiguous") { Console.Error.WriteLine("release not found\nHTTP 401"); return 1; }
        if (scenario == "large") { Console.Error.Write(new string('x', 100000)); Console.Write(new string('y', 100000)); return 2; }
        if (scenario == "json") { Console.WriteLine("invalid json"); return 0; }
        string commit = scenario == "commit" ? "other" : "0123456789abcdef0123456789abcdef01234567";
        string draft = scenario == "mode" ? "true" : "false";
        Console.WriteLine("{\"tagName\":\"" + Environment.GetEnvironmentVariable("APISHARED_RELEASE_TEST_TAG") + "\",\"isDraft\":" + draft + ",\"targetCommitish\":\"" + commit + "\",\"assets\":[]}");
        return 0;
    }
}
'@
    $env:APISHARED_RELEASE_TEST_LOG = Join-Path $fixture 'calls.log'
    $env:APISHARED_RELEASE_TEST_CASE = 'missing'
    Check ($null -eq (Get-ApiGitHubRelease 'Fixture/APIShared' $fixtureTag $fake)) 'Missing release did not reach the creation decision.'
    $env:APISHARED_RELEASE_TEST_CASE = 'existing'
    $release = Get-ApiGitHubRelease 'Fixture/APIShared' $fixtureTag $fake
    Check ($release.targetCommitish -ceq '0123456789abcdef0123456789abcdef01234567') 'Existing release not returned.'
    foreach ($case in @('auth','access','network','ambiguous','json')) {
        $env:APISHARED_RELEASE_TEST_CASE = $case
        $rejected = $false
        try { $null = Get-ApiGitHubRelease 'Fixture/APIShared' $fixtureTag $fake } catch { $rejected = $true }
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
    $beforeGuard=[IO.File]::ReadAllText($env:APISHARED_RELEASE_TEST_LOG)
    $guardScript=Join-Path $PSScriptRoot '../Release/Release.ps1'
    $guardInfo=[Diagnostics.ProcessStartInfo]::new('powershell.exe',('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$guardScript+'" -PrepareOnly -Publish'))
    $guardInfo.UseShellExecute=$false;$guardInfo.CreateNoWindow=$true;$guardInfo.RedirectStandardOutput=$true;$guardInfo.RedirectStandardError=$true
    $guard=[Diagnostics.Process]::Start($guardInfo)
    try{
        $guardOut=$guard.StandardOutput.ReadToEndAsync();$guardError=$guard.StandardError.ReadToEndAsync()
        if(-not $guard.WaitForExit(10000)){$guard.Kill();throw 'PrepareOnly publication guard timed out.'}
        Check ($guard.ExitCode -ne 0 -and ($guardOut.Result+$guardError.Result).Contains('PrepareOnly cannot publish')) 'PrepareOnly+Publish must reject before any release work.'
        Check ([IO.File]::ReadAllText($env:APISHARED_RELEASE_TEST_LOG) -ceq $beforeGuard) 'PrepareOnly+Publish reached the GitHub CLI.'
    }finally{$guard.Dispose()}
    $driverSource = $null
    for ($ancestor = [IO.DirectoryInfo]$PSScriptRoot; $ancestor; $ancestor = $ancestor.Parent) {
        $candidate = Join-Path $ancestor.FullName 'Shared/Tools/Steam/Prepare-APISharedWorkshop.ps1'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $driverSource = $candidate; break }
    }
    if ($driverSource) {
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
    # Workspace driver policy is tested independently with real isolated worktrees.
    # These fixtures isolate only the existing release/publication confirmation paths.
    Write-Fixture (Join-Path $workspace 'Shared/Tools/ApiSharedRepository/MainSource.Common.ps1') @'
function Get-ApiMainWorkspace($Workspace) { return $Workspace }
function Resolve-ApiMainSelection($Workspace) { return 'fixture-main-selection' }
function Get-WorkshopApiRoot($Workspace) { return (Join-Path $Workspace 'APIShared') }
function Ensure-ApiMainBuild {}
function Assert-ApiMainDeliveryIdentity($Workspace,$Identity) { return 'fixture-main-selection' }
'@
    Write-Fixture (Join-Path $driverDirectory 'WorkshopWorkflow.Common.ps1') '# No platform-approval dependency.'
    Write-Fixture (Join-Path $workspace 'APIShared/info.json') (@{GUID='APIShared_Serp';Version=$fixtureVersion}|ConvertTo-Json -Compress)
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
        # Validation never publishes: an absent release must fail before download or creation.
        $expected = if ($case -eq 'existing') { 0 } else { 1 }
        Check ($result.ExitCode -eq $expected) ("Preparation /validate failed for ${case}: " + $result.Diagnostic)
        if ($expected -eq 0) { Check ($result.Output -match 'no release or build started') 'Validation did not exit before side effects.' }
        if ($case -eq 'missing') { Check ($result.Diagnostic -match 'without /validate') 'Missing APIShared release did not explain how preparation creates it.' }
    }
    # A missing release is created only after the human confirmation; refusing it must stop before any release work.
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
        Check ($result.ExitCode -eq 1 -and $result.Diagnostic -match 'Fixture confirmation reached: Type VEROEFFENTLICHEN') ("Missing-release preparation did not stop at the publication confirmation for " + $record + ": " + $result.Diagnostic)
        $calls = [IO.File]::ReadAllText($env:APISHARED_RELEASE_TEST_LOG)
        Check ($calls -notmatch 'release\|(create|upload|edit|download)') 'Missing release reached a release mutation or download.'
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
    return @{Version=$env:APISHARED_RELEASE_TEST_TAG.Substring('APIShared/v'.Length);ReleaseNotes='Fixture';ReleaseTag='fixture';Files=@(@{Path='APIShared.map'})}
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
    Write-Fixture (Join-Path $workspace '.release-output/SteamWorkshop/prepared/APIShared.json') '{"MainSource":{}}'
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
    $calls = [IO.File]::ReadAllText($env:APISHARED_RELEASE_TEST_LOG)
    Check ($calls -notmatch 'release\|(create|upload|edit|download)') 'Read-only test attempted a release mutation.'
    Write-Host 'PASS: maintainer preparation/upload drivers and production confirmations.'
    } else {
        Write-Host 'INFO: maintainer workspace drivers are unavailable in this standalone checkout; their extra integration tests run locally.'
    }
    # Exercise the production publication function, independently of Release.ps1's
    # AST, formatting, variable names, branch layout or packaging prefix.
    $repository = 'Fixture/APIShared'; $tag = $fixtureTag
    $commit = '0123456789abcdef0123456789abcdef01234567'
    $assets = @(); $notes = 'inert notes'; $Publish = $false
    $arguments = @('release','create',$tag)
    foreach ($case in @('missing','mode','existing','commit','auth','access','network')) {
        $env:APISHARED_RELEASE_TEST_CASE = $case
        [IO.File]::WriteAllText($env:APISHARED_RELEASE_TEST_LOG, '')
        $rejected = $false
        try { Invoke-ApiReleasePublication -Repository $repository -Tag $tag -Commit $commit -Assets $assets -Notes $notes -Publish $Publish -CreationArguments $arguments -Executable $fake } catch { $rejected = $true }
        $mutations = @([IO.File]::ReadAllLines($env:APISHARED_RELEASE_TEST_LOG) | Where-Object { $_ -match '^release\|(create|upload|edit)\|' } | ForEach-Object { ($_ -split '\|')[1] })
        if ($case -eq 'missing') { Check (-not $rejected -and ($mutations -join ',') -ceq 'create') 'First release did not select creation.' }
        elseif ($case -eq 'mode') { Check (-not $rejected -and ($mutations -join ',') -ceq 'upload,edit') 'Matching draft did not resume.' }
        else { Check ($rejected -and $mutations.Count -eq 0) ('Standalone release mutated after failure: ' + $case) }
    }
    Write-Host ('PASS: PS 5.1 standalone release probe/publication decision, missing/existing releases, authentication/access/network failures, strict missing detection, pipe capture, argument quoting and commit/mode conflicts. Fixtures: ' + $fixture)
} finally {
    if (Test-Path Function:gh) { Remove-Item Function:gh }
    $env:PATH = $previousPath
    $env:APISHARED_RELEASE_TEST_CASE = $previousCase
    $env:APISHARED_RELEASE_TEST_LOG = $previousLog
    $env:APISHARED_RELEASE_TEST_TAG = $previousTag
}
