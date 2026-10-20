param([Parameter(Mandatory=$true)][string]$GameDir, [Parameter(Mandatory=$true)][string]$FixtureDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = [IO.Path]::GetFullPath($FixtureDirectory)
if (-not [IO.Directory]::Exists($fixture) -or @(Get-ChildItem -LiteralPath $fixture -Force).Count) {
    throw 'Provide an existing, empty test directory; this test retains its evidence.'
}
$cecil = Join-Path $GameDir 'BepInEx/core/Mono.Cecil.dll'
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes($cecil))

function Write-PluginFixture([string]$Path, [string]$Guid, [string]$Marker) {
    $assembly = [Mono.Cecil.AssemblyDefinition]::CreateAssembly(
        [Mono.Cecil.AssemblyNameDefinition]::new('ConsumerFixture', [version]'1.0.0.0'), 'ConsumerFixture', [Mono.Cecil.ModuleKind]::Dll)
    try {
        $module = $assembly.MainModule
        $type = [Mono.Cecil.TypeDefinition]::new('Fixture', 'Plugin', [Mono.Cecil.TypeAttributes]::Public, $module.TypeSystem.Object)
        $module.Types.Add($type)
        $scope = [Mono.Cecil.AssemblyNameReference]::new('BepInEx', [version]'5.0.0.0')
        $module.AssemblyReferences.Add($scope)
        $attributeType = [Mono.Cecil.TypeReference]::new('BepInEx', 'BepInPlugin', $module, $scope)
        $constructor = [Mono.Cecil.MethodReference]::new('.ctor', $module.TypeSystem.Void, $attributeType)
        $constructor.HasThis = $true
        $attribute = [Mono.Cecil.CustomAttribute]::new($constructor)
        foreach ($value in @($Guid, 'Fixture plugin', '1.0.0')) {
            $constructor.Parameters.Add([Mono.Cecil.ParameterDefinition]::new($module.TypeSystem.String))
            $attribute.ConstructorArguments.Add([Mono.Cecil.CustomAttributeArgument]::new($module.TypeSystem.String, $value))
        }
        $type.CustomAttributes.Add($attribute)
        if ($Marker -ne 'absent') {
            $flags = [int][Mono.Cecil.FieldAttributes]::Public -bor [int][Mono.Cecil.FieldAttributes]::Static
            if ($Marker -ne 'nonconstant') { $flags = $flags -bor [int][Mono.Cecil.FieldAttributes]::Literal -bor [int][Mono.Cecil.FieldAttributes]::HasDefault }
            if ($Marker -eq 'private') { $flags = ($flags -band (-bnot [int][Mono.Cecil.FieldAttributes]::FieldAccessMask)) -bor [int][Mono.Cecil.FieldAttributes]::Private }
            $field = [Mono.Cecil.FieldDefinition]::new('UsesApiSharedSaveLoadDialog', [Mono.Cecil.FieldAttributes]$flags, $module.TypeSystem.Boolean)
            if ($Marker -ne 'nonconstant') { $field.Constant = ($Marker -ne 'false') }
            if ($Marker -eq 'other-type') {
                $other = [Mono.Cecil.TypeDefinition]::new('Fixture', 'Unrelated', [Mono.Cecil.TypeAttributes]::Public, $module.TypeSystem.Object)
                $module.Types.Add($other); $other.Fields.Add($field)
            } else { $type.Fields.Add($field) }
        }
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
        $assembly.Write($Path)
    } finally { $assembly.Dispose() }
}

foreach ($case in @(
    @{Name='no-optional-plugin'; Guid=''; Marker='absent'; Reject=$false},
    @{Name='renamed-legacy'; Guid='ExtendedData_Serp'; Marker='absent'; Reject=$true},
    @{Name='false-marker'; Guid='ExtendedData_Serp'; Marker='false'; Reject=$true},
    @{Name='nonconstant-marker'; Guid='ExtendedData_Serp'; Marker='nonconstant'; Reject=$true},
    @{Name='private-marker'; Guid='ExtendedData_Serp'; Marker='private'; Reject=$true},
    @{Name='unrelated-marker'; Guid='ExtendedData_Serp'; Marker='other-type'; Reject=$true},
    @{Name='compatible'; Guid='ExtendedData_Serp'; Marker='true'; Reject=$false},
    @{Name='foreign-guid'; Guid='Independent.Author.Mod'; Marker='absent'; Reject=$false}
)) {
    $directory = Join-Path $fixture $case.Name
    $game = Join-Path $directory 'game'
    $core = Join-Path $game 'BepInEx/core'
    [void][IO.Directory]::CreateDirectory($core)
    [void][IO.Directory]::CreateDirectory((Join-Path $game 'BepInEx/plugins'))
    Copy-Item -LiteralPath $cecil -Destination $core
    $candidate = Join-Path $directory 'APIShared.dll'
    $assembly = [Mono.Cecil.AssemblyDefinition]::CreateAssembly(
        [Mono.Cecil.AssemblyNameDefinition]::new('APIShared', [version]'1.0.0.0'), 'APIShared', [Mono.Cecil.ModuleKind]::Dll)
    try { $assembly.Write($candidate) } finally { $assembly.Dispose() }
    if ($case.Guid) { Write-PluginFixture (Join-Path $game 'BepInEx/plugins/arbitrary-folder/renamed.dll') $case.Guid $case.Marker }
    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Verify-WorkshopConsumerReferences.ps1') -CandidateAssembly $candidate -GameDir $game 2>&1)
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $oldPreference }
    [IO.File]::WriteAllText((Join-Path $directory 'result.log'), (($output -join "`r`n") + "`r`n"))
    if ($case.Reject) {
        if ($code -eq 0 -or ($output -join ' ') -notmatch 'Update ExtendedData together with APIShared') {
            throw ('FAIL: ' + $case.Name + ' must reject with a concrete shared-update instruction. ' + ($output -join ' '))
        }
    } elseif ($code -ne 0) { throw ('FAIL: ' + $case.Name + ': ' + ($output -join ' ')) }
    Write-Host ('PASS: ' + $case.Name)
}
Write-Host ('Consumer compatibility fixtures retained: ' + $fixture)
