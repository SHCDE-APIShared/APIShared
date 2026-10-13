param([string]$GameDir)
$ErrorActionPreference = 'Stop'
if (-not $GameDir) { $GameDir = $env:SHCDE_GAME_DIR }
if (-not $GameDir) { throw 'GameDir is required.' }
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')))
$managed = Join-Path $GameDir 'Stronghold Crusader Definitive Edition_Data\Managed'
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'Assembly-CSharp.dll'))
$noesis = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $managed 'Noesis.NoesisGUI.dll'))
try {
    foreach ($row in @(@('HoverStruct','System.Int32',$false),@('SelectedStruct','System.Int32',$false),@('HoverString','System.String',$true),@('SelectedString','System.String',$false),@('OtherVisible','System.Boolean',$false),@('OtherHighestPri','System.Boolean',$false))) {
        $field = @($game.MainModule.GetType('CrusaderDE.HUD_Main').Fields | Where-Object Name -CEQ $row[0])
        if ($field.Count -ne 1 -or $field[0].FieldType.FullName -cne $row[1] -or $field[0].IsPublic -ne $row[2] -or $field[0].IsStatic) { throw "Changed HUD field: $($row[0])" }
    }
    foreach ($row in @(
        @('GameData','GameData GameData::get_Instance()'),
        @('GameData','EngineInterface/PlayState GameData::get_lastGameState()'),
        @('CrusaderDE.MainViewModel','CrusaderDE.MainViewModel CrusaderDE.MainViewModel::get_Instance()'),
        @('CrusaderDE.MainViewModel','System.Boolean CrusaderDE.MainViewModel::get_RolloverBuilding_TooltipVis()')
    )) {
        $method = @($game.MainModule.GetType($row[0]).Methods | Where-Object FullName -CEQ $row[1])
        if ($method.Count -ne 1 -or -not $method[0].IsPublic) { throw "Changed game getter: $($row[1])" }
    }
    $hud = @($game.MainModule.GetType('CrusaderDE.MainViewModel').Fields | Where-Object Name -CEQ 'HUDmain')
    if ($hud.Count -ne 1 -or -not $hud[0].IsPublic -or $hud[0].FieldType.FullName -cne 'CrusaderDE.HUD_Main') { throw 'Changed public HUDmain field.' }
    foreach ($row in @(
        @('Noesis.Visual','Noesis.Matrix4 Noesis.Visual::TransformToAncestor(Noesis.Visual)'),
        @('Noesis.Matrix4','Noesis.Rect Noesis.Matrix4::TransformBounds(Noesis.Rect)'),
        @('Noesis.Rect','System.Void Noesis.Rect::.ctor(System.Single,System.Single,System.Single,System.Single)'),
        @('Noesis.Rect','System.Single Noesis.Rect::get_X()'),
        @('Noesis.FrameworkElement','System.Single Noesis.FrameworkElement::get_ActualWidth()'),
        @('Noesis.FrameworkElement','Noesis.FrameworkElement Noesis.FrameworkElement::get_Parent()'),
        @('Noesis.FrameworkElement','System.Void Noesis.FrameworkElement::add_Unloaded(Noesis.RoutedEventHandler)'),
        @('Noesis.FrameworkElement','System.Void Noesis.FrameworkElement::remove_Unloaded(Noesis.RoutedEventHandler)'),
        @('Noesis.FrameworkElement','System.Void Noesis.FrameworkElement::add_SizeChanged(Noesis.SizeChangedEventHandler)'),
        @('Noesis.FrameworkElement','System.Void Noesis.FrameworkElement::remove_SizeChanged(Noesis.SizeChangedEventHandler)')
    )) {
        $member = @($noesis.MainModule.GetType($row[0]).Methods | Where-Object FullName -CEQ $row[1])
        if ($member.Count -ne 1 -or -not $member[0].IsPublic) { throw "Changed public Noesis signature: $($row[1])" }
    }
    Write-Host 'PASS: six real HUD selection fields, five public game accesses and ten public Noesis layout/lifetime contracts.'
} finally { $game.Dispose(); $noesis.Dispose() }
