[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [switch]$Worker,
    [string]$GateToken
)

# One hosted attribution operation. This is deliberately not a general build runner,
# a profiler installed in Core, or a way to turn a passing diagnostic into a fix.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:RUNNER_OS -ne 'Windows' -or $env:GITHUB_ACTIONS -ne 'true' -or
    $env:ALLOCATION_RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This diagnostic runs only on a Windows GitHub-hosted runner; local/self-hosted execution is not enabled.'
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$runnerTemp = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\') + '\'
if (-not $output.StartsWith($runnerTemp, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be a fresh child of RUNNER_TEMP.'
}
$dotnet = (Get-Command dotnet.exe -CommandType Application -ErrorAction Stop).Source
$sdkVersion = '10.0.404'
$runtimeVersion = '10.0.15'
$traceProvider = 'Microsoft-Windows-DotNETRuntime:432b0098:5,APIShared-CoreAllocationDiagnostics:ffffffffffffffff:5'

function Write-Json([string]$Path, [object]$Value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 14) + "`r`n")
    if ($bytes.Length -gt 4MB) { throw 'Diagnostic JSON extent exceeded.' }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}
function Get-Pin([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    [ordered]@{ path = $file.FullName; bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
function Get-SourcePins {
    $relative = @(
        'tests/Core.Tests/Core.Tests.csproj', 'tests/Core.Tests/ValueModifierTests.cs',
        'tests/Core.Tests/CoreAllocationDiagnostics.cs', 'tests/test.runsettings',
        'Directory.Build.props', 'Directory.Packages.props', '.github/workflows/tests.yml',
        'tools/Validation/Invoke-CoreAllocationDiagnostic.ps1'
    )
    if (Test-Path -LiteralPath (Join-Path $repository 'Directory.Build.targets')) { $relative += 'Directory.Build.targets' }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repository 'src/Modifiers') -File -Filter '*.cs') {
        $relative += 'src/Modifiers/' + $file.Name
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repository 'tools/AllocationTrace') -File) {
        if ($file.Extension -in '.cs', '.csproj') { $relative += 'tools/AllocationTrace/' + $file.Name }
    }
    if ($relative.Count -gt 64) { throw 'Selected diagnostic source extent changed.' }
    @($relative | Sort-Object -Unique | ForEach-Object { Get-Pin (Join-Path $repository $_) })
}

if ($Worker) {
    if ([string]::IsNullOrEmpty($GateToken) -or -not (Test-Path -LiteralPath $output -PathType Container)) {
        throw 'Only the existing owner can admit a worker.'
    }
    # Before GO no subprocess is started. Assignment failure can therefore terminate
    # exactly this retained worker, without guessing at PIDs or unrelated processes.
    $until = [Diagnostics.Stopwatch]::StartNew()
    $gate = Join-Path $output 'GO.json'
    while (-not (Test-Path -LiteralPath $gate)) {
        if ($until.Elapsed.TotalSeconds -gt 5) { throw 'The owner did not assign/admit this worker.' }
        Start-Sleep -Milliseconds 20
    }
    $admission = Get-Content -LiteralPath $gate -Raw | ConvertFrom-Json
    if ($admission.token -cne $GateToken -or $admission.workerPid -ne $PID) { throw 'Worker admission differs.' }
    $invocations = [Collections.Generic.List[object]]::new()
    $testExit = $null
    $reportExit = $null
    $status = 'INCONCLUSIVE'
    $failure = $null
    $targetResult = $null
    $suiteCounters = $null
    $validatedCohort = $null
    $buildRoot = Join-Path $output 'artifacts'
    $toolOutput = Join-Path $output 'trace-tool'
    $sourceBefore = $null
    $PSNativeCommandUseErrorActionPreference = $false

    function Invoke-DotNet([string]$Name, [string[]]$Arguments) {
        # The changes are confined to this owned worker, and restored after EVERY
        # invocation. No user/machine environment or package-tool setting is written.
        $overrides = [ordered]@{
            DOTNET_CLI_HOME = (Join-Path $output 'cli-home')
            DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
            DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
            DOTNET_CLI_TELEMETRY_OPTOUT = '1'
            DOTNET_NOLOGO = '1'
            DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
            DOTNET_CLI_USE_MSBUILD_SERVER = '0'
            MSBUILDDISABLENODEREUSE = '1'
        }
        $saved = @{}
        foreach ($key in $overrides.Keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
        $logPath = Join-Path $output ($Name + '.log')
        $clock = [Diagnostics.Stopwatch]::StartNew()
        if ($Name -ceq 'trace-report') { [IO.File]::Create((Join-Path $output 'trace-report.started')).Dispose() }
        try {
            foreach ($key in $overrides.Keys) { [Environment]::SetEnvironmentVariable($key, $overrides[$key], 'Process') }
            Push-Location -LiteralPath $output
            try { & $dotnet @Arguments *> $logPath; $raw = $LASTEXITCODE } finally { Pop-Location }
        }
        finally {
            foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
        }
        $invocations.Add([ordered]@{ name = $Name; executable = $dotnet; arguments = $Arguments; rawExit = $raw;
            elapsedSeconds = $clock.Elapsed.TotalSeconds; log = Get-Pin $logPath })
        return [int]$raw
    }

    try {
        $sourceBefore = Get-SourcePins
        Write-Json (Join-Path $output 'source-inputs.json') $sourceBefore
        Write-Json (Join-Path $output 'global.json') @{ sdk = @{ version = $sdkVersion; rollForward = 'disable'; allowPrerelease = $false } }
        $head = (& git.exe --no-optional-locks -C $repository rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') { throw 'Cannot record the actual checkout.' }
        $parents = (& git.exe --no-optional-locks -C $repository rev-list --parents -n 1 HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Cannot record actual checkout parents.' }
        Write-Json (Join-Path $output 'checkout.json') @{ head = $head; parents = $parents; sourceBase = '6b8132b2fd6f3b6bf912f58a203fb5be5dca63ea' }
        if ((Invoke-DotNet 'sdk-version' @('--version')) -ne 0) { throw 'SDK identity command failed.' }
        if ((Get-Content -LiteralPath (Join-Path $output 'sdk-version.log') -Raw).Trim() -cne $sdkVersion) { throw 'Selected SDK is not exactly 10.0.404.' }
        if ((Invoke-DotNet 'dotnet-info' @('--info')) -ne 0) { throw 'Cannot record the selected host.' }

        $common = @('--configuration', 'Release', '--disable-build-servers', '-m:2',
            '-p:UseSharedCompilation=false', '-nodeReuse:false', '-p:RuntimeFrameworkVersion=10.0.15',
            '-p:RollForward=Disable', '-p:TargetLatestRuntimePatch=false')
        $coreProject = Join-Path $repository 'tests/Core.Tests/Core.Tests.csproj'
        $coreArguments = @('build', $coreProject) + $common + @("-p:APISharedArtifactsRoot=$buildRoot", '-p:CoreAllocationDiagnostics=true', "-bl:$(Join-Path $output 'core-build.binlog')")
        if ((Invoke-DotNet 'core-build' $coreArguments) -ne 0) { throw 'The diagnostic test cohort did not build.' }
        $toolProject = Join-Path $repository 'tools/AllocationTrace/AllocationTrace.csproj'
        $toolArguments = @('build', $toolProject) + $common + @('--output', $toolOutput,
            "-p:APISharedArtifactsRoot=$(Join-Path $output 'tool-artifacts')")
        if ((Invoke-DotNet 'report-build' $toolArguments) -ne 0) { throw 'The separate trace report tool did not build.' }
        $tool = Join-Path $toolOutput 'APIShared.AllocationTrace.dll'
        $toolFiles = @(Get-ChildItem -LiteralPath $toolOutput -File | Where-Object { $_.Extension -in '.dll', '.pdb', '.json' })
        if ($toolFiles.Count -gt 64) { throw 'Trace report provider extent changed.' }
        Write-Json (Join-Path $output 'trace-report-providers.json') @($toolFiles | ForEach-Object { Get-Pin $_.FullName })
        if ((Invoke-DotNet 'report-controls' @('exec', '--fx-version', $runtimeVersion, $tool, '--self-test')) -ne 0) {
            throw 'The report rejection controls failed.'
        }

        $coreDirectory = Join-Path $buildRoot 'bin/Core.Tests/Release/net10.0'
        $assembly = Join-Path $coreDirectory 'Core.Tests.dll'
        $cohortNames = @('Core.Tests.dll', 'Core.Tests.pdb', 'Core.Tests.deps.json', 'Core.Tests.runtimeconfig.json')
        $binaryBefore = @($cohortNames | ForEach-Object { Get-Pin (Join-Path $coreDirectory $_) })
        Write-Json (Join-Path $output 'compiled-cohort-before.json') $binaryBefore
        $runtimeConfig = Get-Content -LiteralPath (Join-Path $coreDirectory 'Core.Tests.runtimeconfig.json') -Raw | ConvertFrom-Json
        if ($runtimeConfig.runtimeOptions.framework.version -cne $runtimeVersion -or $runtimeConfig.runtimeOptions.rollForward -cne 'Disable') {
            throw 'The test runtime configuration is not pinned to 10.0.15/Disable.'
        }

        $settings = [xml](Get-Content -LiteralPath (Join-Path $repository 'tests/test.runsettings') -Raw)
        $configuration = $settings.RunSettings.RunConfiguration
        if ($null -eq $configuration -or $configuration.TargetPlatform -cne 'x64' -or $configuration.MaxCpuCount -ne '1') {
            throw 'The original X64/one-test-process runsettings changed.'
        }
        $traceEnvironment = [ordered]@{
            DOTNET_EnableEventPipe = '1'
            DOTNET_EventPipeConfig = $traceProvider
            DOTNET_EventPipeOutputPath = (Join-Path $output 'allocation-{pid}.nettrace')
            DOTNET_EventPipeCircularMB = '40'
            DOTNET_EventPipeOutputStreaming = '1'
            DOTNET_EventPipeEnableStackwalk = '1'
            COMPlus_EventPipeEnableStackwalk = '1'
            API_SHARED_ALLOCATION_COHORT = (Join-Path $output 'cohort.json')
        }
        $environmentNode = $settings.CreateElement('EnvironmentVariables')
        foreach ($key in $traceEnvironment.Keys) {
            $node = $settings.CreateElement($key); $node.InnerText = $traceEnvironment[$key]
            [void]$environmentNode.AppendChild($node)
        }
        if ($null -ne $configuration.SelectSingleNode('EnvironmentVariables')) { throw 'The source runsettings has an unexpected environment block.' }
        [void]$configuration.AppendChild($environmentNode)
        $settingsPath = Join-Path $output 'allocation.runsettings'
        $settings.Save($settingsPath)
        $runtimeSettings = [ordered]@{}
        foreach ($key in @('DOTNET_TieredCompilation', 'COMPlus_TieredCompilation', 'DOTNET_TieredPGO', 'COMPlus_TieredPGO',
            'DOTNET_ReadyToRun', 'COMPlus_ReadyToRun', 'DOTNET_gcServer', 'COMPlus_gcServer')) {
            $runtimeSettings[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        }
        Write-Json (Join-Path $output 'diagnostic-environment.json') @{ testhost = $traceEnvironment; preservedRuntimeSettings = $runtimeSettings;
            safeDotnetDefaults = @{ DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'; DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false' } }

        # One full-suite invocation, with the actual compiled cohort. No filter,
        # extra warm-up, success retry, or conditional assertion is introduced.
        $testArguments = @('test', $coreProject, '--configuration', 'Release', '--no-build', '--no-restore',
            '--logger', 'trx', '--results-directory', (Join-Path $output 'TestResults'), '--settings', $settingsPath,
            '--diag', (Join-Path $output 'vstest.diag.log'), "-p:APISharedArtifactsRoot=$buildRoot", '-p:CoreAllocationDiagnostics=true',
            '-p:RuntimeFrameworkVersion=10.0.15', '-p:RollForward=Disable', '-p:TargetLatestRuntimePatch=false')
        $testExit = Invoke-DotNet 'core-test' $testArguments
        $binaryAfter = @($cohortNames | ForEach-Object { Get-Pin (Join-Path $coreDirectory $_) })
        Write-Json (Join-Path $output 'compiled-cohort-after.json') $binaryAfter
        if (($binaryBefore | ConvertTo-Json -Depth 4 -Compress) -cne ($binaryAfter | ConvertTo-Json -Depth 4 -Compress)) { throw 'Compiled test bytes changed during the run.' }
        $trx = @(Get-ChildItem -LiteralPath (Join-Path $output 'TestResults') -File -Filter '*.trx')
        if ($trx.Count -ne 1 -or $trx[0].Length -gt 4MB) { throw 'The original full-suite TRX is missing or unbounded.' }
        $trxDocument = [xml](Get-Content -LiteralPath $trx[0].FullName -Raw)
        $target = @($trxDocument.SelectNodes("//*[local-name()='UnitTestResult']") | Where-Object {
            $_.testName -ceq 'InvalidInputsAreRejectedAndCompiledCommonPathDoesNotAllocate'
        })
        if ($target.Count -ne 1) { throw 'Cannot identify the actual strict allocation test result.' }
        $counterNode = $trxDocument.SelectSingleNode("//*[local-name()='ResultSummary']/*[local-name()='Counters']")
        if ($null -eq $counterNode) { throw 'The full-suite TRX counters are missing.' }
        $suiteCounters = [ordered]@{}
        foreach ($attribute in $counterNode.Attributes) { $suiteCounters[$attribute.Name] = $attribute.Value }
        $errorNode = $target[0].SelectSingleNode(".//*[local-name()='Message']")
        $errorText = if ($null -eq $errorNode) { $null } else { $errorNode.InnerText }
        $errorTruncated = $null -ne $errorText -and $errorText.Length -gt 4096
        if ($errorTruncated) { $errorText = $errorText.Substring(0,4096) }
        $targetResult = @{ testName = $target[0].testName; outcome = $target[0].outcome;
            errorMessage = $errorText; errorMessageTruncated = $errorTruncated }
        Write-Json (Join-Path $output 'original-test-outcome.json') @{ originalTestExit = $testExit;
            fullSuiteCounters = $suiteCounters; target = $targetResult; trx = Get-Pin $trx[0].FullName }
        $cohortPath = Join-Path $output 'cohort.json'
        if (-not (Test-Path -LiteralPath $cohortPath) -or (Get-Item -LiteralPath $cohortPath).Length -gt 64KB) { throw 'Missing/bounded actual child cohort evidence.' }
        $cohort = Get-Content -LiteralPath $cohortPath -Raw | ConvertFrom-Json
        if ($cohort.processId -lt 1 -or $cohort.testAssembly.sha256 -cne $binaryBefore[0].sha256 -or
            -not [IO.Path]::GetFullPath($cohort.testAssembly.path).Equals([IO.Path]::GetFullPath($assembly), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The helper did not identify the compiled testhost cohort.'
        }
        $runtimeDirectory = Join-Path (Split-Path -Parent $dotnet) ('shared/Microsoft.NETCore.App/' + $runtimeVersion)
        $expectedCoreLib = Get-Pin (Join-Path $runtimeDirectory 'System.Private.CoreLib.dll')
        if ($cohort.coreLib.sha256 -cne $expectedCoreLib.sha256 -or
            -not [IO.Path]::GetFullPath($cohort.coreLib.path).Equals($expectedCoreLib.path, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The actual testhost did not use the selected official 10.0.15 CoreLib.'
        }
        foreach ($name in @('coreclr.dll', 'clrjit.dll')) {
            $module = @($cohort.runtimeModules | Where-Object { $_.name -ieq $name })
            $expected = Get-Pin (Join-Path $runtimeDirectory $name)
            if ($module.Count -ne 1 -or $module[0].sha256 -cne $expected.sha256 -or
                -not [IO.Path]::GetFullPath($module[0].path).Equals($expected.path, [StringComparison]::OrdinalIgnoreCase)) {
                throw "The actual testhost runtime module differs: $name"
            }
        }
        $validatedCohort = Get-Pin $cohortPath
        $trace = Join-Path $output ('allocation-' + $cohort.processId + '.nettrace')
        if (-not (Test-Path -LiteralPath $trace) -or (Get-Item -LiteralPath $trace).Length -gt 128MB) { throw 'The exact testhost trace is missing or over budget.' }
        $reportExit = Invoke-DotNet 'trace-report' @('exec', '--fx-version', $runtimeVersion, $tool,
            '--trace', $trace, '--cohort', $cohortPath, '--output', (Join-Path $output 'allocation-report.json'))
        if ($reportExit -ne 0) { throw 'Trace attribution is incomplete; preserve the original test result.' }
        if ($targetResult.outcome -ceq 'Passed' -and
            @($cohort.windows | Where-Object { $_.window -in 1,2 -and $_.after -ne $_.before }).Count -eq 0) {
            $status = 'NOT_REPRODUCED'
        }
        elseif ($testExit -ne 0 -and $target[0].outcome -ceq 'Failed' -and
            @($cohort.windows | Where-Object { $_.window -in 1,2 -and $_.after -gt $_.before }).Count -gt 0) {
            $status = 'FAILED_ALLOCATION_WITH_QUALIFIED_TRACE'
        }
        else { throw 'Another failure is not an attributed occurrence of issue 124.' }
    }
    catch { $failure = [ordered]@{ type = $_.Exception.GetType().FullName; message = $_.Exception.Message } }
    finally {
        try {
            $sourceAfter = Get-SourcePins
            Write-Json (Join-Path $output 'source-inputs-after.json') $sourceAfter
            if ($null -eq $sourceBefore -or ($sourceBefore | ConvertTo-Json -Depth 4 -Compress) -cne ($sourceAfter | ConvertTo-Json -Depth 4 -Compress)) {
                $status = 'INCONCLUSIVE'; if ($null -eq $failure) { $failure = @{ type = 'SourceChanged'; message = 'Selected source inputs changed.' } }
            }
        } catch { $status = 'INCONCLUSIVE'; if ($null -eq $failure) { $failure = @{ type = 'SourceReadback'; message = $_.Exception.Message } } }
        Write-Json (Join-Path $output 'invocations.json') @($invocations.ToArray())
        Write-Json (Join-Path $output 'worker-result.json') @{ schema = 'core-allocation-diagnostic-worker/1'; status = $status;
            originalTestExit = $testExit; reportExit = $reportExit; failure = $failure;
            fullSuiteCounters = $suiteCounters; target = $targetResult; cohort = $validatedCohort;
            attributionOnly = $true; productionFix = $false; rerunPerformed = $false }
    }
    # Qualified evidence does not erase a failing test. A passing/incomplete single
    # occurrence is deliberately not a green correction of issue 124.
    if ($null -ne $testExit -and $testExit -ne 0) { exit $testExit }
    exit 2
}

if (Test-Path -LiteralPath $output) { throw 'Fresh output required; do not overwrite another run.' }
[void][IO.Directory]::CreateDirectory($output)
[void][IO.Directory]::CreateDirectory((Join-Path $output 'cli-home'))

# Kept local to this diagnostic: exact Popen-equivalent worker handle, no global
# process enumeration/termination and no detached build server is authorized.
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
public sealed class CoreAllocationOwnedJob {
    [StructLayout(LayoutKind.Sequential)] struct Basic {
        public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWorking, MaxWorking;
        public uint ActiveLimit; public UIntPtr Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong A,B,C,D,E,F; }
    [StructLayout(LayoutKind.Sequential)] struct Limits {
        public Basic Basic; public Io Io; public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob;
    }
    [StructLayout(LayoutKind.Sequential)] struct Counts {
        public long User,Kernel,PeriodUser,PeriodKernel; public uint Faults,Total,Active,Terminated;
    }
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr CreateJobObjectW(IntPtr security, string name);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int kind,ref Limits value,int bytes);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job,int kind,ref Counts value,int bytes,IntPtr returned);
    [DllImport("kernel32.dll", SetLastError=true, EntryPoint="QueryInformationJobObject")] static extern bool QueryLimits(IntPtr job,int kind,ref Limits value,int bytes,IntPtr returned);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateJobObject(IntPtr job,uint code);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetHandleInformation(IntPtr handle,out uint flags);
    IntPtr handle;
    static void Require(bool value) { if(!value) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public CoreAllocationOwnedJob() {
        handle=CreateJobObjectW(IntPtr.Zero,null); if(handle==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits=new Limits(); limits.Basic.Flags=0x2300; // kill on close + process/job commit limits
        limits.ProcessMemory=new UIntPtr(2UL*1024*1024*1024); limits.JobMemory=new UIntPtr(3UL*1024*1024*1024);
        try { Require(SetInformationJobObject(handle,9,ref limits,Marshal.SizeOf<Limits>())); }
        catch { CloseHandle(handle); handle=IntPtr.Zero; throw; }
    }
    public void Assign(Process process) { Require(AssignProcessToJobObject(handle,process.Handle)); }
    public uint Active() { var c=new Counts(); Require(QueryInformationJobObject(handle,1,ref c,Marshal.SizeOf<Counts>(),IntPtr.Zero)); return c.Active; }
    public ulong[] Peaks() { var l=new Limits(); Require(QueryLimits(handle,9,ref l,Marshal.SizeOf<Limits>(),IntPtr.Zero)); return new[]{l.PeakProcess.ToUInt64(),l.PeakJob.ToUInt64()}; }
    public void Stop() { Require(TerminateJobObject(handle,1)); }
    public bool CloseAndVerify() {
        var old=handle; Require(CloseHandle(old)); handle=IntPtr.Zero; uint flags;
        bool exists=GetHandleInformation(old,out flags); return !exists && Marshal.GetLastWin32Error()==6;
    }
    public static bool HandleInvalid(IntPtr value) { uint flags; bool exists=GetHandleInformation(value,out flags); return !exists && Marshal.GetLastWin32Error()==6; }
}
'@

$job = $null; $child = $null; $raw = $null; $errorReason = $null
$closed = $false; $empty = $false; $peaks = $null; $identity = $null
$drain = [ordered]@{ attempted = $false; initial = $null; final = $null }
$cleanupErrors = [Collections.Generic.List[object]]::new()
$workerTerminal = [ordered]@{ signaled = $false; handleClosed = $false; handleInvalidated = $false }
$started = [Diagnostics.Stopwatch]::StartNew()
$stdoutStream = $null; $stderrStream = $null; $stdoutCopy = $null; $stderrCopy = $null
$reportClock = $null
try {
    $job = [CoreAllocationOwnedJob]::new()
    $token = [Guid]::NewGuid().ToString('N')
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile','-NonInteractive','-File',$PSCommandPath,'-OutputDirectory',$output,'-Worker','-GateToken',$token)) { $start.ArgumentList.Add($argument) }
    $start.WorkingDirectory = $output
    # Tracing is added only to the generated testhost settings, not inherited by
    # the owner, worker, compiler, CLI, or report process.
    foreach ($key in @('DOTNET_EnableEventPipe','COMPlus_EnableEventPipe','DOTNET_EventPipeConfig','COMPlus_EventPipeConfig',
        'DOTNET_EventPipeOutputPath','COMPlus_EventPipeOutputPath','DOTNET_EventPipeCircularMB','COMPlus_EventPipeCircularMB',
        'DOTNET_EventPipeOutputStreaming','COMPlus_EventPipeOutputStreaming','DOTNET_EventPipeEnableStackwalk','COMPlus_EventPipeEnableStackwalk',
        'API_SHARED_ALLOCATION_COHORT')) { [void]$start.Environment.Remove($key) }
    $start.Environment['DOTNET_ADD_GLOBAL_TOOLS_TO_PATH'] = 'false'
    $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $child = [Diagnostics.Process]::Start($start)
    $stdoutStream = [IO.File]::Create((Join-Path $output 'worker.log'))
    $stderrStream = [IO.File]::Create((Join-Path $output 'worker.stderr.log'))
    $stdoutCopy = $child.StandardOutput.BaseStream.CopyToAsync($stdoutStream)
    $stderrCopy = $child.StandardError.BaseStream.CopyToAsync($stderrStream)
    $job.Assign($child)
    $identity = @{ pid = $child.Id; startedUtc = $child.StartTime.ToUniversalTime().ToString('O'); executable = $start.FileName }
    Write-Json (Join-Path $output 'GO.pending.json') @{ token = $token; workerPid = $child.Id }
    [IO.File]::Move((Join-Path $output 'GO.pending.json'), (Join-Path $output 'GO.json'))
    while (-not $child.WaitForExit(100)) {
        $files = @(Get-ChildItem -LiteralPath $output -File -Recurse -ErrorAction Stop)
        $traceBytes = [long]0; $logBytes = [long]0; $totalBytes = [long]0; $derivedBytes = [long]0
        $derivedRoot = Join-Path $output 'allocation-report.json.work'
        foreach ($file in $files) {
            $totalBytes += $file.Length
            if ($file.Extension -eq '.nettrace') { $traceBytes += $file.Length }
            if ($file.Extension -eq '.log') { $logBytes += $file.Length }
            if ($file.FullName.StartsWith($derivedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { $derivedBytes += $file.Length }
        }
        if ($null -eq $reportClock -and (Test-Path -LiteralPath (Join-Path $output 'trace-report.started'))) { $reportClock = [Diagnostics.Stopwatch]::StartNew() }
        if ($started.Elapsed.TotalSeconds -gt 240) { $errorReason = 'DIAGNOSTIC_DEADLINE' }
        elseif ($null -ne $reportClock -and $reportClock.Elapsed.TotalSeconds -gt 110) { $errorReason = 'TRACE_READER_DEADLINE' }
        elseif ($traceBytes -ge 96MB) { $errorReason = 'TRACE_STOP_LIMIT' }
        elseif ($derivedBytes -gt 256MB) { $errorReason = 'DERIVED_TRACE_STOP_LIMIT' }
        elseif ($logBytes -gt 4MB) { $errorReason = 'LOG_STOP_LIMIT' }
        elseif ($totalBytes -gt 512MB -or $files.Count -gt 4096) { $errorReason = 'OWNED_OUTPUT_LIMIT' }
        if ($null -ne $errorReason) { $job.Stop(); break }
    }
    if (-not $child.WaitForExit(1500)) { throw 'The exact worker is not terminal.' }
    $raw = $child.ExitCode
}
catch { if ($null -eq $errorReason) { $errorReason = 'OWNER_EXCEPTION: ' + $_.Exception.Message } }
finally {
    if ($null -ne $job) {
        try {
            $drain.initial = $job.Active()
            if ($drain.initial -ne 0) {
                if ($null -eq $errorReason) { $errorReason = 'DESCENDANTS_AFTER_WORKER_EXIT' }
                $drain.attempted = $true
                $job.Stop()
                $drainClock = [Diagnostics.Stopwatch]::StartNew()
                do { $drain.final = $job.Active(); if ($drain.final -eq 0) { break }; Start-Sleep -Milliseconds 25 } while ($drainClock.Elapsed.TotalSeconds -lt 1.5)
                $empty = $drain.final -eq 0
            } else { $drain.final = 0; $empty = $true }
        } catch { $cleanupErrors.Add(@{ stage = 'job-drain'; message = $_.Exception.Message }) }
        try { $peaks = $job.Peaks() } catch { $cleanupErrors.Add(@{ stage = 'job-peaks'; message = $_.Exception.Message }) }
        try { $closed = $job.CloseAndVerify() } catch { $cleanupErrors.Add(@{ stage = 'job-close'; message = $_.Exception.Message }) }
    }
    if ($null -ne $child) {
        $workerHandle = [IntPtr]::Zero
        try {
            $workerHandle = $child.Handle
            if (-not $child.HasExited) { $child.Kill() } # exact GO-gated worker on failed assignment
            if (-not $child.WaitForExit(1500)) { throw 'The retained worker did not terminate.' }
            $workerTerminal.signaled = $true
            $raw = $child.ExitCode
            foreach ($copy in @($stdoutCopy, $stderrCopy)) {
                if ($null -ne $copy) {
                    if (-not $copy.Wait(1500)) { throw 'The owned worker output did not finish within the cleanup bound.' }
                    [void]$copy.GetAwaiter().GetResult()
                }
            }
        } catch { $cleanupErrors.Add(@{ stage = 'worker-terminal'; message = $_.Exception.Message }) }
        finally {
            try {
                $child.Dispose(); $workerTerminal.handleClosed = $true
                if ($workerHandle -ne [IntPtr]::Zero) { $workerTerminal.handleInvalidated = [CoreAllocationOwnedJob]::HandleInvalid($workerHandle) }
            } catch { $cleanupErrors.Add(@{ stage = 'worker-close'; message = $_.Exception.Message }) }
        }
    }
    foreach ($stream in @($stdoutStream, $stderrStream)) {
        if ($null -ne $stream) {
            try { $stream.Dispose() } catch { $cleanupErrors.Add(@{ stage = 'worker-stream-close'; message = $_.Exception.Message }) }
        }
    }
}
if ((-not $empty -or -not $closed -or $cleanupErrors.Count -ne 0 -or -not $workerTerminal.signaled -or
    -not $workerTerminal.handleClosed -or -not $workerTerminal.handleInvalidated) -and $null -eq $errorReason) { $errorReason = 'OWNED_CLOSURE_INCOMPLETE' }
# A final fast write can occur after the last live-loop sample. Recheck the same
# owned tree after all worker streams/handles are terminal, before qualifying it.
$finalBudget = $null
try {
    $files = @(Get-ChildItem -LiteralPath $output -File -Recurse -ErrorAction Stop)
    $finalBudget = [ordered]@{ fileCount = $files.Count; traceBytes = [long]0; logBytes = [long]0;
        derivedBytes = [long]0; totalBytes = [long]0; reservedOwnerResultBytes = 4MB }
    $derivedRoot = Join-Path $output 'allocation-report.json.work'
    foreach ($file in $files) {
        $finalBudget.totalBytes += $file.Length
        if ($file.Extension -eq '.nettrace') { $finalBudget.traceBytes += $file.Length }
        if ($file.Extension -eq '.log') { $finalBudget.logBytes += $file.Length }
        if ($file.FullName.StartsWith($derivedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { $finalBudget.derivedBytes += $file.Length }
    }
    if ($finalBudget.traceBytes -gt 128MB -or $finalBudget.logBytes -gt 4MB -or $finalBudget.derivedBytes -gt 256MB -or
        $finalBudget.totalBytes + $finalBudget.reservedOwnerResultBytes -gt 512MB -or $finalBudget.fileCount + 1 -gt 4096) {
        if ($null -eq $errorReason) { $errorReason = 'FINAL_OWNED_OUTPUT_LIMIT' }
    }
} catch { if ($null -eq $errorReason) { $errorReason = 'FINAL_OWNED_OUTPUT_UNKNOWN: ' + $_.Exception.Message } }
$workerResult = $null
try {
    $workerResultPath = Join-Path $output 'worker-result.json'
    if (Test-Path -LiteralPath $workerResultPath) { $workerResult = Get-Content -LiteralPath $workerResultPath -Raw | ConvertFrom-Json }
} catch { if ($null -eq $errorReason) { $errorReason = 'WORKER_RESULT_UNREADABLE' } }
$ownerStatus = 'INCONCLUSIVE'
if ($null -ne $workerResult) {
    $statusProperty = $workerResult.PSObject.Properties['status']
    if ($null -eq $statusProperty -or $statusProperty.Value -notin 'INCONCLUSIVE','NOT_REPRODUCED','FAILED_ALLOCATION_WITH_QUALIFIED_TRACE') {
        if ($null -eq $errorReason) { $errorReason = 'WORKER_RESULT_STATUS_UNKNOWN' }
    } elseif ($null -eq $errorReason) { $ownerStatus = $statusProperty.Value }
}
Write-Json (Join-Path $output 'owner-result.json') @{ schema = 'core-allocation-diagnostic-owner/1'; workerRawExit = $raw;
    status = $ownerStatus; error = $errorReason; worker = $identity; elapsedSeconds = $started.Elapsed.TotalSeconds;
    jobEmpty = $empty; jobClosedAndInvalidated = $closed; peaks = $peaks; drain = $drain;
    cleanupErrors = @($cleanupErrors.ToArray()); workerTerminal = $workerTerminal; finalBudget = $finalBudget; outcome = $workerResult;
    limits = @{ processMiB = 2048; jobMiB = 3072; workerSeconds = 240; traceReaderSeconds = 110;
        traceStopMiB = 96; maximumAcceptedTraceMiB = 128; derivedTraceMiB = 256; outputMiB = 512; logMiB = 4 };
    productionFix = $false; normalCiChanged = $false }
if ($null -ne $raw -and $raw -ne 0) { exit $raw }
exit 2
