[CmdletBinding()]
param([string]$GameDir, [string]$ExtenderDir)
$ErrorActionPreference = 'Stop'
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')))
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'Stronghold Crusader Definitive Edition_Data\Managed\Assembly-CSharp.dll'))
$se = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ExtenderDir 'SHCDESE.dll'))
try {
    $sfx = $game.MainModule.GetType('SFXManager')
    $instance = @($sfx.Fields | Where-Object { $_.Name -eq 'instance' -and $_.IsStatic -and $_.IsPublic -and $_.FieldType.FullName -eq 'SFXManager' })
    if ($instance.Count -ne 1) { throw 'Public SFX singleton field changed.' }
    $play = @($sfx.Methods | Where-Object { $_.Name -eq 'playSound' -and $_.IsPublic -and -not $_.IsStatic -and $_.ReturnType.FullName -eq 'System.Void' -and (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',') -eq 'System.Int32,System.Single,System.Single,System.Boolean' })
    if ($play.Count -ne 1) { throw 'Actual public sound playback signature changed.' }
    $map = $game.MainModule.GetType('GameMap')
    $process = @($map.Methods | Where-Object { $_.Name -eq 'processTestMap' -and $_.IsPublic -and -not $_.IsStatic -and $_.ReturnType.FullName -eq 'System.Void' -and (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',') -eq 'System.Int16[],System.Int32,EngineInterface/PlayState,System.Byte[]' })
    if ($process.Count -ne 1) { throw 'Actual GameMap playback signature changed.' }
    $effects = @($sfx.Fields | Where-Object { $_.Name -eq 'fx_list' -and -not $_.IsStatic -and $_.IsPrivate -and $_.FieldType.FullName -eq 'System.Collections.Generic.List`1<SFXManager/sh1_sound_effect>' })
    if ($effects.Count -ne 1) { throw 'Encapsulated private effect-table reflection contract changed.' }
    $clips = @($sfx.Fields | Where-Object { $_.Name -eq 'play_list' -and $_.IsPrivate -and $_.FieldType.FullName -eq 'System.Collections.Generic.List`1<SFXManager/sh1_sound>' })
    if ($clips.Count -ne 1) { throw 'Encapsulated clip table changed.' }
    $clipType = @($sfx.NestedTypes | Where-Object Name -eq 'sh1_sound')[0]
    if (@($clipType.Fields | Where-Object { $_.Name -eq 'clip' -and $_.IsPublic -and $_.FieldType.FullName -eq 'UnityEngine.AudioClip' }).Count -ne 1) { throw 'Actual clip field changed.' }
    $audio = $game.MainModule.GetType('MyAudioManager')
    if (@($audio.Methods | Where-Object { $_.Name -eq 'playSFX' -and $_.IsPublic -and $_.ReturnType.FullName -eq 'System.Void' -and (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',') -eq 'UnityEngine.AudioClip,System.Single,System.Single,System.Boolean,System.Boolean' }).Count -ne 1) { throw 'Public custom playback signature changed.' }
    if (@($audio.Properties | Where-Object { $_.Name -eq 'Instance' -and $_.GetMethod.IsPublic -and $_.GetMethod.IsStatic }).Count -ne 1) { throw 'Public audio singleton changed.' }
    foreach ($spec in @(
        @('SHCDESE.API.GameAssetManagerAPI','GetModFileBinaryContent','System.String,System.String,System.Byte[]&'),
        @('SHCDESE.API.GameMapArchiveManagerAPI','TryReadBinaryFile','System.String,System.Boolean'),
        @('SHCDESE.API.UnityMainThreadDispatcher','EnqueueStatic','System.Action'),
        @('SHCDESE.Extensions.AudioClipExtensions','LoadAudioClipFromBytes','System.Byte[]'))) {
        $type = $se.MainModule.GetType($spec[0])
        if ($null -eq $type -or @($type.Methods | Where-Object { $_.Name -eq $spec[1] -and $_.IsPublic -and (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',') -eq $spec[2] }).Count -ne 1) { throw "Public Extender audio contract changed: $($spec[0]).$($spec[1])" }
    }
    $unit = $se.MainModule.GetType('SHCDESE.Interop.GameUnit')
    foreach ($name in @('r_UnitChimp','r_AIState','r_CurrentTilePositionX','r_CurrentTilePositionY')) {
        $field = @($unit.Fields | Where-Object { $_.Name -eq $name -and $_.IsPublic -and -not $_.IsStatic })
        if ($field.Count -ne 1) { throw "Public unit field changed: $name" }
        Write-Host "Audio public field: $($field[0].FullName)"
    }
    Write-Host 'PASS: real installed audio access signatures; private effect-table access is explicitly encapsulated and validated.'
} finally { $game.Dispose(); $se.Dispose() }
