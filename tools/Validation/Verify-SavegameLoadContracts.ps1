param([string]$GameDir, [string]$ExtenderDir)
$ErrorActionPreference = 'Stop'
if (-not $GameDir) { $GameDir = $env:SHCDE_GAME_DIR }
if (-not $ExtenderDir) { $ExtenderDir = Join-Path $GameDir 'BepInEx\plugins\000shcdese' }
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')))
$gameAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'Stronghold Crusader Definitive Edition_Data\Managed\Assembly-CSharp.dll'))
$extenderAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ExtenderDir 'SHCDESE.dll'))
try {
    foreach ($contract in @(
        @{ Type = 'Platform_Multiplayer'; Method = 'HostLoadGame'; Parameters = 'System.String'; Return = 'System.Void' },
        @{ Type = 'Platform_Multiplayer'; Method = 'StartSave'; Parameters = 'EngineInterface/MultiplayerSetupData|FileHeader'; Return = 'System.Void' },
        @{ Type = 'MapFileManager'; Method = 'GetHeaderFromMpSaveFileName'; Parameters = 'System.String'; Return = 'FileHeader' },
        @{ Type = 'CrusaderDE.HUD_ConfirmationPopup'; Method = 'ShowConfirmationOKMessage'; Parameters = 'System.String|System.Action|System.String|System.Boolean'; Return = 'System.Void' }
    )) {
        $type = $gameAssembly.MainModule.GetType($contract.Type)
        $methods = @($type.Methods | Where-Object {
            $_.Name -ceq $contract.Method -and $_.IsPublic -and $_.ReturnType.FullName -ceq $contract.Return -and
            (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join '|') -ceq $contract.Parameters
        })
        if ($methods.Count -ne 1) { throw "Changed public save contract: $($contract.Type).$($contract.Method)" }
    }
    foreach ($contract in @(
        @{ Type = 'Platform_Multiplayer'; Fields = @('activeLobby','gameMembers') },
        @{ Type = 'Platform_Multiplayer/MPLobby'; Fields = @('id','isHost','members') },
        @{ Type = 'Platform_Multiplayer/MPLobbyMember'; Fields = @('id','dummyToBeKicked') },
        @{ Type = 'FileHeader'; Fields = @('fileName','filePath','xPlaySaveChecksum','xPlaySaveTime') }
    )) {
        $type = $gameAssembly.MainModule.GetType($contract.Type)
        foreach ($name in $contract.Fields) {
            $field = @($type.Fields | Where-Object { $_.Name -ceq $name -and $_.IsPublic })
            if ($field.Count -ne 1) { throw "Missing public save field: $($contract.Type).$name" }
        }
    }
    $type = $extenderAssembly.MainModule.GetType('SHCDESE.API.GameXAMLManagerAPI')
    $scope = @($type.Methods | Where-Object {
        $_.Name -ceq 'RunWithChangeOrigin' -and $_.ReturnType.FullName -ceq 'System.Void' -and
        (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join '|') -ceq 'SHCDESE.API.Components.ModManager.LobbyModSettingsChangeOrigin|System.Action'
    })
    if ($scope.Count -ne 1) { throw 'Changed Extender restore-origin scope.' }
    $runtimeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\src\Savegames'))
    $coordinator = [IO.File]::ReadAllText((Join-Path $runtimeRoot 'SavegameLoadCoordinator.cs'))
    if ($coordinator -match '\.(Undo|Disable)\s*\(' -or $coordinator -match '\bOn(Destroy|Disable|ApplicationQuit)\s*\(') {
        throw 'Save preparation contains a published-hook teardown.'
    }
    Write-Host '[Contracts] Real game save entries/fields and installed Extender origin scope verified.'
} finally { $extenderAssembly.Dispose(); $gameAssembly.Dispose() }
