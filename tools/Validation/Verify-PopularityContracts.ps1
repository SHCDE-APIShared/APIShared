param([string]$GameDir, [string]$ExtenderDir, [string]$ApiAssembly)
$ErrorActionPreference = 'Stop'
if (-not $GameDir) { $GameDir = $env:SHCDE_GAME_DIR }
if (-not $ExtenderDir) { $ExtenderDir = Join-Path $GameDir 'BepInEx/plugins/000shcdese' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $ApiAssembly) { $ApiAssembly = Join-Path $root 'BepInEx/plugins/APIShared_Serp/APIShared.dll' }
. (Join-Path $PSScriptRoot 'CompiledMembers.Common.ps1')
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GameDir 'BepInEx/core/Mono.Cecil.dll')))
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'Stronghold Crusader Definitive Edition_Data/Managed/Assembly-CSharp.dll'))
$se = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ExtenderDir 'SHCDESE.dll'))
try {
    $references = @(Get-ApiCompiledReferences -AssemblyPath $ApiAssembly)
    # The external types identify the contracts. Private service names, source
    # files, locals and the current number of modifiers are irrelevant.
    $state = $game.MainModule.GetType('EngineInterface').NestedTypes | Where-Object Name -eq 'PlayState'
    $stateRefs = @($references | Where-Object { $_.Owner -ceq 'EngineInterface/PlayState' -and $_.Kind -ceq 'Field' })
    if (-not $stateRefs.Count) { throw 'Candidate contains no PlayState references; refusing an empty contract check.' }
    foreach ($reference in $stateRefs) {
        $field = @($state.Fields | Where-Object FullName -CEQ $reference.FullName)
        if ($field.Count -ne 1 -or -not $field[0].IsPublic) { throw "Changed public report field: $($reference.FullName)" }
    }
    $vm = $game.MainModule.GetType('CrusaderDE.MainViewModel')
    foreach ($reference in @($references | Where-Object { $_.Owner -ceq 'CrusaderDE.MainViewModel' -and $_.Name -clike 'set_*' })) {
        $setter = @($vm.Methods | Where-Object FullName -CEQ $reference.FullName)
        if ($setter.Count -ne 1 -or -not $setter[0].IsPublic) { throw "Changed public HUD setter: $($reference.FullName)" }
    }
    $globals = $se.MainModule.GetType('SHCDESE.GameGlobals.GameGlobalsManager')
    $names = @($references | Where-Object { $_.Owner -ceq 'SHCDESE.GameGlobals.GameGlobalsManager' -and $_.Name -clike 'get_*' })
    if (-not $names.Count) { throw 'Candidate contains no global modifier references; refusing an empty contract check.' }
    foreach ($reference in $names) {
        $getter = @($globals.Methods | Where-Object FullName -CEQ $reference.FullName)
        if ($getter.Count -ne 1 -or -not $getter[0].IsPublic) { throw "Changed SE modifier: $($reference.FullName)" }
    }
    foreach ($pair in @(
        @{Name='RationsMode'; Members=@('None','Half','Full','Extra','Double')},
        @{Name='TaxesMode'; Members=@('HugeDonation','BigDonation','SmallDonation','None','Low','Moderate','High','Mean','Usurious','Cruel','MostCruel','Cruellest')}
    )) {
        $type = $se.MainModule.GetType('SHCDESE.Interop.Enums.' + $pair.Name)
        for ($i=0; $i -lt $pair.Members.Count; $i++) {
            $field = $type.Fields | Where-Object Name -eq $pair.Members[$i]
            if (-not $field.HasConstant -or [int]$field.Constant -ne $i) { throw "Changed enum: $($pair.Name).$($pair.Members[$i])" }
        }
    }
    Write-Host "PASS: compiled popularity/report references, $($names.Count) SE modifiers and explicit enum contracts against installed assemblies."
} finally { $game.Dispose(); $se.Dispose() }
