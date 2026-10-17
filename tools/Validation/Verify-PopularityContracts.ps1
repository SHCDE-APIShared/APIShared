param([string]$GameDir, [string]$ExtenderDir)
$ErrorActionPreference = 'Stop'
if (-not $GameDir) { $GameDir = $env:SHCDE_GAME_DIR }
if (-not $ExtenderDir) { $ExtenderDir = Join-Path $GameDir 'BepInEx/plugins/000shcdese' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GameDir 'BepInEx/core/Mono.Cecil.dll')))
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'Stronghold Crusader Definitive Edition_Data/Managed/Assembly-CSharp.dll'))
$se = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ExtenderDir 'SHCDESE.dll'))
try {
    $source = [IO.File]::ReadAllText((Join-Path $root 'src/ScriptExtenderFixes/Popularity/PopularityPresentation.cs'))
    $state = $game.MainModule.GetType('EngineInterface').NestedTypes | Where-Object Name -eq 'PlayState'
    foreach ($name in @([regex]::Matches($source,'\bs\.(\w+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)) {
        $field = @($state.Fields | Where-Object Name -eq $name)
        if ($field.Count -ne 1 -or -not $field[0].IsPublic -or $field[0].FieldType.FullName -notin @('System.Int32','System.SByte','System.Byte','System.Int16','System.UInt16')) { throw "Changed public report field: $name" }
    }
    $vm = $game.MainModule.GetType('CrusaderDE.MainViewModel')
    foreach ($name in @([regex]::Matches($source,'\bvm\.(\w+)\s*=(?!=)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)) {
        $property = @($vm.Properties | Where-Object Name -eq $name)
        if ($property.Count -ne 1 -or -not $property[0].SetMethod.IsPublic -or $property[0].PropertyType.FullName -notin @('System.String','System.Boolean')) { throw "Changed public HUD setter: $name" }
    }
    $parameters = [IO.File]::ReadAllText((Join-Path $root 'src/ScriptExtenderFixes/Popularity/PopularityParameters.cs'))
    $globals = $se.MainModule.GetType('SHCDESE.GameGlobals.GameGlobalsManager')
    $names = @([regex]::Matches($parameters,'globals\.(\w+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    if ($names.Count -ne 41) { throw 'Expected 41 direct modifiers.' }
    foreach ($name in $names) {
        $property = @($globals.Properties | Where-Object Name -eq $name)
        if ($property.Count -ne 1 -or -not $property[0].GetMethod.IsPublic -or $property[0].PropertyType.FullName -cne 'RedBird.Core.Memory.Managed.ManagedValue`1<System.Int32>') { throw "Changed SE modifier: $name" }
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
    Write-Host 'PASS: popularity report public fields/setters and 41 SE modifiers/enums against installed assemblies.'
} finally { $game.Dispose(); $se.Dispose() }
