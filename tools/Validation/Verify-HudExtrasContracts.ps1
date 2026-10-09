param([string]$GameDir, [string]$ExtenderDir)
$ErrorActionPreference = 'Stop'
if (-not $GameDir) { $GameDir = $env:SHCDE_GAME_DIR }
if (-not $GameDir) { throw 'GameDir is required for side-HUD managed contract verification.' }
if (-not $ExtenderDir) { $ExtenderDir = Join-Path $GameDir 'BepInEx\plugins\000shcdese' }
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')))
$gameAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'Stronghold Crusader Definitive Edition_Data\Managed\Assembly-CSharp.dll'))
$extenderAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ExtenderDir 'SHCDESE.dll'))
function Assert-PublicMethod($assembly, [string]$typeName, [string]$signature) {
    $type = $assembly.MainModule.GetType($typeName)
    $methods = @($type.Methods | Where-Object FullName -CEQ $signature)
    if ($methods.Count -ne 1 -or -not $methods[0].IsPublic) { throw "Missing/nonpublic installed member: $signature" }
    Write-Output "Public installed HUD contract: $signature"
}
foreach ($signature in @(
    'CrusaderDE.MainViewModel CrusaderDE.MainViewModel::get_Instance()',
    'System.Boolean CrusaderDE.MainViewModel::get_Show_HUD_Extras()',
    'System.Boolean CrusaderDE.MainViewModel::get_Show_HUD_Extras_Button_Objectves()',
    'System.Boolean CrusaderDE.MainViewModel::get_Show_HUD_Extras_Button_Freebuild()',
    'System.Void CrusaderDE.MainViewModel::add_PropertyChanged(System.ComponentModel.PropertyChangedEventHandler)',
    'System.Void CrusaderDE.MainViewModel::remove_PropertyChanged(System.ComponentModel.PropertyChangedEventHandler)'
)) { Assert-PublicMethod $gameAssembly 'CrusaderDE.MainViewModel' $signature }
$loaded = @($gameAssembly.MainModule.GetType('CrusaderDE.MainViewModel').Fields | Where-Object Name -CEQ 'viewModelLoaded')
if ($loaded.Count -ne 1 -or -not $loaded[0].IsPublic -or -not $loaded[0].IsStatic -or $loaded[0].FieldType.FullName -cne 'System.Boolean') { throw 'Invalid installed MainViewModel.viewModelLoaded field.' }
foreach ($index in 1..4) { Assert-PublicMethod $gameAssembly 'CrusaderDE.PropEx' "System.Void CrusaderDE.PropEx::SetSprite$index(Noesis.UIElement,System.Object)" }
Assert-PublicMethod $extenderAssembly 'SHCDESE.API.GameXAMLManagerAPI' 'Noesis.FrameworkElement SHCDESE.API.GameXAMLManagerAPI::FindGlobalElement(System.String)'
Assert-PublicMethod $extenderAssembly 'SHCDESE.API.GameXAMLManagerAPI' 'SHCDESE.API.GameXAMLManagerAPI SHCDESE.API.GameXAMLManagerAPI::get_Instance()'
Write-Output 'PASS: side-HUD accesses use installed public managed members; no native or publicized contract is required.'
