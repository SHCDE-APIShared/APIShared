[CmdletBinding()]
param([int]$NoInstall = 0)
$ErrorActionPreference = 'Stop'
$buildWatch = [Diagnostics.Stopwatch]::StartNew()
Write-Host '[Build] APIShared: checking toolchain and installed game references...'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'BuildProof.ps1')
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
    Write-Host '[Build] Validating runtime, XAML, native hooks and real interop contracts...'
    & (Join-Path $root 'tools\Validation\Test-Standalone.ps1') -GameDir $game -ExtenderDir $extender
    $results = Join-Path $root '.local\test-results'
    foreach ($project in @('Core.Tests\Core.Tests.csproj','APISharedTests\APISharedTests.csproj','LobbyModSettingsPresetTests\LobbyModSettingsPresetTests.csproj')) {
        Invoke-Tool 'dotnet' @('test', (Join-Path $root ('tests\' + $project)), '--configuration', 'Release', "-p:GameDir=$game", "-p:ExtenderDir=$extender", '--logger', 'trx', '--results-directory', $results)
    }
    Invoke-Tool $msbuild @((Join-Path $root 'APIShared.csproj'), '/t:Rebuild', '/p:Configuration=Release', "/p:GameDir=$game", "/p:ExtenderDir=$extender")
    Invoke-Tool 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools/Validation/Test-WorkshopReadiness.ps1'), '-ApiAssembly', (Join-Path $root 'BepInEx/plugins/APIShared_Serp/APIShared.dll'))
    if ($NoInstall -eq 0) {
        & (Join-Path $root 'tools/Validation/Verify-WorkshopConsumerReferences.ps1') -CandidateAssembly (Join-Path $root 'BepInEx/plugins/APIShared_Serp/APIShared.dll') -GameDir $game
    }
    foreach ($project in @('tests\PublicPresetConsumer\APISharedPresetConsumerTests.csproj','examples\ThirdPartyMod\ThirdPartyMod.csproj')) {
        Invoke-Tool $msbuild @((Join-Path $root $project), '/t:Rebuild', '/p:Configuration=Release', "/p:GameDir=$game", "/p:ExtenderDir=$extender")
    }
    Write-Host '[Build] Preparing package and build provenance...'
    . (Join-Path $PSScriptRoot 'WorkshopPackage.ps1')
    $package = Join-Path $root 'BepInEx\plugins\APIShared_Serp'
    $packagePatches = [IO.Path]::GetFullPath((Join-Path $package 'Patches'))
    if (-not $packagePatches.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package patch directory.' }
    if (Test-Path -LiteralPath $packagePatches) { Remove-Item -LiteralPath $packagePatches -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $root 'info.json') -Destination $package
    Copy-Item -LiteralPath (Join-Path $root 'Patches') -Destination $package -Recurse -Force
    & (Join-Path $root 'tools/WorkshopUploader/build.bat') /nopause
    if ($LASTEXITCODE -ne 0) { throw 'Publisher build failed.' }
    . (Join-Path $PSScriptRoot 'PublisherPackage.ps1')
    Sync-ApiPublisherPackage $root
    Sync-ApiWorkshopAssets -Root $root
    Remove-ApiLegacyWorkshopView $package
    foreach ($required in @('APIShared.dll','APIShared.xml','info.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $package $required))) { throw ($required + ' is missing from the prepared package.') }
    }
    Assert-ApiOptimizedAssembly (Join-Path $package 'APIShared.dll') $game
    $unexpected = @(Get-ChildItem -LiteralPath $package -Recurse -File | Where-Object { $_.Extension -in @('.dll','.exe') -and $_.Name -ne 'APIShared.dll' })
    if ($unexpected.Count) { throw 'The package contains unexpected runtime binaries.' }
    $null = New-ApiWorkshopInventory -Directory $package
    Write-ApiBuildProof $root $game $extender $msbuild $inputState
    if ($NoInstall -ne 0) { Write-Host ('APIShared built and tested successfully in {0:N1}s; installation skipped.' -f $buildWatch.Elapsed.TotalSeconds); exit 0 }
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
    $toolSource = Join-Path $root 'BepInEx/tools/APISharedWorkshopPublisher'
    $toolTarget = Join-Path $game 'BepInEx/tools/APISharedWorkshopPublisher'
    Assert-ApiPublisherPackage $toolSource (Join-Path $root '.local/WorkshopUploader')
    Assert-ApiWorkshopUnlinkedPath $toolTarget
    [void][IO.Directory]::CreateDirectory($toolTarget)
    Assert-ApiWorkshopUnlinkedTree $toolTarget
    foreach ($file in Get-ChildItem -LiteralPath $toolSource -File) { Copy-Item -LiteralPath $file.FullName -Destination $toolTarget -Force }
    Assert-ApiPublisherPackage $toolTarget $toolSource
    Write-Host ('APIShared built, tested and installed successfully in {0:N1}s.' -f $buildWatch.Elapsed.TotalSeconds)
} catch { Write-Error $_; exit 1 }
