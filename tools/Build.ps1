[CmdletBinding(PositionalBinding=$false)]
param([int]$NoInstall = 0, [switch]$WorkspaceBuild,
    [ValidateSet('full','quick','none')][string]$TestMode = 'full',
    [Parameter(Position=0,ValueFromRemainingArguments)][string[]]$DriverArguments)
$ErrorActionPreference = 'Stop'
$buildWatch = [Diagnostics.Stopwatch]::StartNew()
Write-Host '[Build] APIShared: checking toolchain and installed game references...'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'BuildProof.ps1')
. (Join-Path $PSScriptRoot 'BuildChecks.ps1')
$context = $null
$succeeded = $false
function Invoke-Tool([string]$Executable, [string[]]$Arguments) {
    Write-Host ('[Build] Running {0}: {1}' -f [IO.Path]::GetFileName($Executable), $Arguments[0])
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}
# csc /optimize- marks the assembly DebuggableAttribute(DisableOptimizations), which also
# turns off JIT optimization on .NET Framework. The shipped Release DLL must not carry it.
function Assert-ApiOptimizedAssembly([string]$Path, [string]$GameDir) {
    if (-not ('Mono.Cecil.AssemblyDefinition' -as [type])) {
        [void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')))
    }
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Path)
    try {
        $debuggable = @($assembly.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'System.Diagnostics.DebuggableAttribute' })
        if ($debuggable.Count -ne 1 -or $debuggable[0].ConstructorArguments.Count -ne 1) { throw "$Path has no single DebuggableAttribute(DebuggingModes)." }
        $modes = [int]$debuggable[0].ConstructorArguments[0].Value
        if ($modes -band [int][Diagnostics.DebuggableAttribute+DebuggingModes]::DisableOptimizations) {
            throw "$Path was compiled without optimization (DebuggableAttribute $modes); Release must set Optimize."
        }
        Write-Host ('[Build] Optimized Release assembly verified: DebuggableAttribute({0}).' -f $modes)
    } finally { $assembly.Dispose() }
}
try {
    $choices = @($DriverArguments)
    if ($PSBoundParameters.ContainsKey('TestMode')) { $choices += '/tests:' + $TestMode }
    $TestMode = Get-ApiBuildTestMode 'build.bat' $choices
    $context = New-ApiBuildContext $TestMode
    Write-Host ('[{0:yyyy-MM-dd HH:mm:ss.fff}] Test mode: {1}' -f [DateTime]::Now,$TestMode)
    Initialize-ApiBuildEnvironment $root
    $game = $env:SHCDE_GAME_DIR
    if (-not $game) { throw 'Set SHCDE_GAME_DIR to your Stronghold Crusader Definitive Edition installation.' }
    $game = [IO.Path]::GetFullPath($game)
    $extender = $env:SHCDESE_EXTENDER_DIR
    if (-not $extender) { $extender = Join-Path $game 'BepInEx\plugins\000shcdese' }
    if (-not (Test-Path -LiteralPath (Join-Path $game 'Stronghold Crusader Definitive Edition_Data\Managed\Assembly-CSharp.dll'))) { throw 'The real installed Assembly-CSharp.dll was not found in SHCDE_GAME_DIR.' }
    if (-not (Test-Path -LiteralPath (Join-Path $extender 'SHCDESE.dll'))) { throw 'Install Script Extender or set SHCDESE_EXTENDER_DIR.' }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 10 SDK (dotnet).' }
    $msbuild = $env:SHCDE_MSBUILD
    if (-not $msbuild) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio Build Tools or set SHCDE_MSBUILD.' }
        $msbuild = (& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1)
    }
    if (-not $msbuild -or -not (Test-Path -LiteralPath $msbuild)) { throw 'MSBuild was not found. Set SHCDE_MSBUILD.' }
    $inputState = Get-ApiInputState $root $game $extender $msbuild
    $proofPath = Join-Path $root '.local/build-proof.json'
    if (Test-Path -LiteralPath $proofPath) { Remove-Item -LiteralPath $proofPath }
    if ($NoInstall -eq 0 -and (Get-Process -Name 'Stronghold Crusader Definitive Edition' -ErrorAction SilentlyContinue)) { throw 'Close the game before installation.' }
    Invoke-ApiBuildStep $context 'build-source-selection' -Action {
        Invoke-Tool 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools/Validation/Test-WorkspaceBuild.ps1'))
    }
    Invoke-ApiBuildStep $context 'dependency-bootstrap-contracts' -Action {
        & (Join-Path $root 'tools/Validation/Test-DependencyBootstrap.ps1') -GameDir $game -MSBuild $msbuild
    }
    Invoke-ApiBuildStep $context 'standalone-validation' -When checked -Action {
        & (Join-Path $root 'tools/Validation/Test-Standalone.ps1') -GameDir $game -ExtenderDir $extender -CheckMode $TestMode -BuildContext $context
    }
    $results = Join-Path $root '.local/test-results'
    foreach ($project in @('Content.Tests/Content.Tests.csproj','PopularityDisplay.Tests/PopularityDisplay.Tests.csproj','Core.Tests/Core.Tests.csproj','APISharedTests/APISharedTests.csproj','LobbyModSettingsPresetTests/LobbyModSettingsPresetTests.csproj')) {
        Invoke-ApiBuildStep $context $project -Action {
            Invoke-Tool 'dotnet' @('test', (Join-Path $root ('tests/' + $project)), '--configuration', 'Release', "-p:GameDir=$game", "-p:ExtenderDir=$extender", '--logger', 'trx', '--results-directory', $results)
        }
    }
    foreach ($suite in @('AiMarketSell.NativeTests','PlagueHeight.NativeTests')) {
        Invoke-ApiBuildStep $context $suite -Action {
            Invoke-Tool 'dotnet' @('run', '--project', (Join-Path $root ("tests/$suite/$suite.csproj")),
                '--configuration', 'Release', "-p:GameDir=$game", "-p:ExtenderDir=$extender", '--',
                (Join-Path $game 'Stronghold Crusader Definitive Edition_Data/Plugins/x86_64/CrusaderDE.dll'))
        }
    }
    Invoke-ApiBuildStep $context 'APIShared.csproj' -Category compile -When always -Action {
        Invoke-Tool $msbuild @((Join-Path $root 'APIShared.csproj'), '/t:Rebuild', '/p:Configuration=Release', "/p:GameDir=$game", "/p:ExtenderDir=$extender")
    }
    Invoke-ApiBuildStep $context 'compiled-popularity-contracts' -When checked -Action {
        & (Join-Path $root 'tools/Validation/Verify-PopularityContracts.ps1') -GameDir $game -ExtenderDir $extender -ApiAssembly (Join-Path $root 'BepInEx/plugins/APIShared_Serp/APIShared.dll')
    }
    Invoke-ApiBuildStep $context 'dependency-bootstrap.csproj' -Category compile -When always -Action {
        Invoke-Tool $msbuild @((Join-Path $root 'bootstrap/APIShared.DependencyBootstrap.csproj'), '/t:Rebuild', '/p:Configuration=Release', "/p:GameDir=$game")
    }
    Invoke-ApiBuildStep $context 'workshop-process-readiness' -Action {
        Invoke-Tool 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools/Validation/Test-WorkshopReadiness.ps1'), '-ApiAssembly', (Join-Path $root 'BepInEx/plugins/APIShared_Serp/APIShared.dll'))
    }
    if ($NoInstall -eq 0) {
        Invoke-ApiBuildStep $context 'installed-consumer-references' -Category installation -When always -Action {
            & (Join-Path $root 'tools/Validation/Verify-WorkshopConsumerReferences.ps1') -CandidateAssembly (Join-Path $root 'BepInEx/plugins/APIShared_Serp/APIShared.dll') -GameDir $game
        }
    }
    foreach ($project in @('tests/PublicPresetConsumer/APISharedPresetConsumerTests.csproj','examples/ThirdPartyMod/ThirdPartyMod.csproj')) {
        Invoke-ApiBuildStep $context $project -Action {
            Invoke-Tool $msbuild @((Join-Path $root $project), '/t:Rebuild', '/p:Configuration=Release', "/p:GameDir=$game", "/p:ExtenderDir=$extender")
        }
    }
    Write-Host '[Build] Preparing package and build provenance...'
    . (Join-Path $PSScriptRoot 'WorkshopPackage.ps1')
    $package = Join-Path $root 'BepInEx\plugins\APIShared_Serp'
    $packagePatches = [IO.Path]::GetFullPath((Join-Path $package 'Patches'))
    if (-not $packagePatches.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package patch directory.' }
    if (Test-Path -LiteralPath $packagePatches) { Remove-Item -LiteralPath $packagePatches -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $root 'info.json') -Destination $package
    Copy-Item -LiteralPath (Join-Path $root 'Patches') -Destination $package -Recurse -Force
    Invoke-ApiBuildStep $context 'WorkshopUploader' -Category compile -When always -Action {
        if ($WorkspaceBuild) { & (Join-Path $root 'tools/WorkshopUploader/build.bat') /workspace /nopause }
        else { & (Join-Path $root 'tools/WorkshopUploader/build.bat') /nopause }
        if ($LASTEXITCODE -ne 0) { throw 'Publisher build failed.' }
    }
    . (Join-Path $PSScriptRoot 'PublisherPackage.ps1')
    Sync-ApiPublisherPackage $root
    Sync-ApiWorkshopAssets -Root $root
    Remove-ApiLegacyWorkshopView $package
    foreach ($required in @('APIShared.dll','APIShared.xml','APIShared.Content.dll','APIShared.Content.xml','info.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $package $required))) { throw ($required + ' is missing from the prepared package.') }
    }
    Assert-ApiOptimizedAssembly (Join-Path $package 'APIShared.dll') $game
    Assert-ApiOptimizedAssembly (Join-Path $package 'APIShared.Content.dll') $game
    $unexpected = @(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object { $_.Extension -in @('.dll','.exe') -and $_.Name -notin @('APIShared.dll','APIShared.Content.dll') })
    if ($unexpected.Count) { throw 'The package contains unexpected runtime binaries.' }
    . (Join-Path $PSScriptRoot 'DependencyBootstrapPackage.ps1')
    New-ApiBootstrapPair $root
    Invoke-ApiBuildStep $context 'compiled-bootstrap-pair-contracts' -Action {
        Invoke-Tool (Join-Path $root 'tests/DependencyBootstrap.Tests/bin/APIShared.DependencyBootstrap.Tests.exe') @($game,'pair',$root)
    }
    Invoke-ApiBuildStep $context 'bootstrap-package-fixtures' -Action {
        & (Join-Path $root 'tools/Validation/Test-DependencyBootstrapPackage.ps1') -PackageRoot $root
    }
    $null = New-ApiWorkshopInventory -Directory $package
    Invoke-ApiBuildStep $context 'package-integrity-and-evidence' -Category package -When always -Action {
        Write-ApiBuildProof $root $game $extender $msbuild $inputState -TestMode $TestMode
    }
    if ($NoInstall -ne 0) {
        $succeeded = $true
        Write-Host ('APIShared built successfully in {0:N1}s; tests:{1}; installation skipped.' -f $buildWatch.Elapsed.TotalSeconds,$TestMode)
        exit 0
    }
    if (Get-Process -Name 'Stronghold Crusader Definitive Edition' -ErrorAction SilentlyContinue) { throw 'The game started during the build; close it before installing.' }
    $destination = Join-Path $game 'BepInEx\plugins\APIShared_Serp'
    # Always install the separate plugin. Workshop migration owns historical
    # embedded files; this driver preserves user data and neighboring plugins.
    $pluginRoot = [IO.Path]::GetFullPath((Join-Path $game 'BepInEx\plugins')) + [IO.Path]::DirectorySeparatorChar
    $destination = [IO.Path]::GetFullPath($destination)
    if (-not $destination.StartsWith($pluginRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($destination) -ne 'APIShared_Serp') { throw 'Unsafe installation destination.' }
    for ($current = $destination; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and ([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked installation destination.' }
    }
    [void][IO.Directory]::CreateDirectory($destination)
    Assert-ApiWorkshopUnlinkedPath (Join-Path $destination 'Override/Workshop/Workshop.xaml')
    Remove-ApiLegacyWorkshopView $destination
    foreach ($item in Get-ChildItem -LiteralPath $package -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $destination -Recurse -Force
    }
    if ($destination -ieq [IO.Path]::GetFullPath((Join-Path $game 'BepInEx/plugins/APIShared_Serp'))) {
        Complete-ApiBootstrapInstallation -Root $root -GameDir $game
    } else {
        Write-Host 'Dependency bootstrap: alternate plugin layout retained; bootstrap deployment is not part of this layout.'
    }
    $toolSource = Join-Path $root 'BepInEx/tools/APISharedWorkshopPublisher'
    $toolTarget = Join-Path $game 'BepInEx/tools/APISharedWorkshopPublisher'
    Assert-ApiPublisherPackage $toolSource (Join-Path $root '.local/WorkshopUploader')
    Assert-ApiWorkshopUnlinkedPath $toolTarget
    [void][IO.Directory]::CreateDirectory($toolTarget)
    Assert-ApiWorkshopUnlinkedTree $toolTarget
    foreach ($file in Get-ChildItem -LiteralPath $toolSource -File) { Copy-Item -LiteralPath $file.FullName -Destination $toolTarget -Force }
    Assert-ApiPublisherPackage $toolTarget $toolSource -Installed
    $succeeded = $true
    Write-Host ('APIShared built and installed successfully in {0:N1}s; tests:{1}.' -f $buildWatch.Elapsed.TotalSeconds,$TestMode)
} catch { Write-Host $_.ScriptStackTrace; Write-Error $_ -ErrorAction Continue; exit 1 }
finally { if ($context) { Save-ApiBuildSteps $root $context $succeeded } }
