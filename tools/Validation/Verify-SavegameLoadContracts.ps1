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
        @{ Type = 'CrusaderDE.HUD_ConfirmationPopup'; Method = 'ShowConfirmationOKMessage'; Parameters = 'System.String|System.Action|System.String|System.Boolean'; Return = 'System.Void' },
        @{ Type = 'CrusaderDE.HUD_LoadSaveRequester'; Method = 'OpenLoadSaveRequester'; Parameters = 'Enums/RequesterTypes|System.Action`2<System.String,FileHeader>|System.Action|System.Int32|System.Boolean|System.Boolean'; Return = 'System.Void' },
        @{ Type = 'CrusaderDE.HUD_LoadSaveRequester'; Method = 'ButtonClicked'; Parameters = 'System.Int32|System.Boolean'; Return = 'System.Void' }
    )) {
        $type = $gameAssembly.MainModule.GetType($contract.Type)
        $methods = @($type.Methods | Where-Object {
            $_.Name -ceq $contract.Method -and $_.IsPublic -and $_.ReturnType.FullName -ceq $contract.Return -and
            (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join '|') -ceq $contract.Parameters
        })
        if ($methods.Count -ne 1) { throw "Changed public save contract: $($contract.Type).$($contract.Method)" }
    }
    # Private managed entry is intercepted through the validated hook helper, never called
    # directly. It encloses the delayed overwrite callback and Vanilla filename bookkeeping.
    $execute = @($gameAssembly.MainModule.GetType('CrusaderDE.HUD_LoadSaveRequester').Methods | Where-Object {
        $_.Name -ceq 'RunOKAction' -and $_.IsPrivate -and -not $_.IsStatic -and
        $_.ReturnType.FullName -ceq 'System.Void' -and $_.Parameters.Count -eq 0
    })
    if ($execute.Count -ne 1) { throw 'Changed private requester execution contract.' }
    foreach ($contract in @(
        @{ Type = 'Platform_Multiplayer'; Fields = @('activeLobby','gameMembers') },
        @{ Type = 'Platform_Multiplayer/MPLobby'; Fields = @('id','isHost','members') },
        @{ Type = 'Platform_Multiplayer/MPLobbyMember'; Fields = @('id','dummyToBeKicked') },
        @{ Type = 'FileHeader'; Fields = @('fileName','filePath','xPlaySaveChecksum','xPlaySaveTime') },
        @{ Type = 'CrusaderDE.MainViewModel'; Fields = @('viewModelLoaded','HUDLoadSaveRequester') },
        @{ Type = 'CrusaderDE.FileRow'; Fields = @('fileHeader') }
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
    # Record comparison: the save archive reader and the player names in its messages.
    $steamAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'Stronghold Crusader Definitive Edition_Data\Managed\com.rlabrecque.steamworks.net.dll'))
    try {
        foreach ($contract in @(
            @{ Assembly = $extenderAssembly; Type = 'SHCDESE.API.Components.Archive.MapArchive'; Method = 'TryLoad'; Parameters = 'System.String|SHCDESE.API.Components.Archive.MapArchive&'; Return = 'System.Boolean'; Static = $true },
            @{ Assembly = $extenderAssembly; Type = 'SHCDESE.API.Components.Archive.MapArchive'; Method = 'TryReadBinaryFile'; Parameters = 'System.String|System.Boolean'; Return = 'System.Byte[]'; Static = $false },
            @{ Assembly = $extenderAssembly; Type = 'SHCDESE.IO.ZipUtil'; Method = 'FindEocdHeaderIndex'; Parameters = 'System.String'; Return = 'System.Int64'; Static = $true },
            @{ Assembly = $extenderAssembly; Type = 'SHCDESE.IO.ZipUtil'; Method = 'FindFirstZipHeaderIndex'; Parameters = 'System.String'; Return = 'System.Int64'; Static = $true },
            @{ Assembly = $steamAssembly; Type = 'Steamworks.SteamFriends'; Method = 'GetFriendPersonaName'; Parameters = 'Steamworks.CSteamID'; Return = 'System.String'; Static = $true }
        )) {
            $type = $contract.Assembly.MainModule.GetType($contract.Type)
            $methods = @($type.Methods | Where-Object {
                $_.Name -ceq $contract.Method -and $_.IsPublic -and $_.IsStatic -eq $contract.Static -and $_.ReturnType.FullName -ceq $contract.Return -and
                (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join '|') -ceq $contract.Parameters
            })
            if ($methods.Count -ne 1) { throw "Changed save record comparison contract: $($contract.Type).$($contract.Method)" }
        }
    } finally { $steamAssembly.Dispose() }
    $runtimeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\src\Savegames'))
    $coordinator = [IO.File]::ReadAllText((Join-Path $runtimeRoot 'SavegameLoadCoordinator.cs'))
    if ($coordinator -match '\.(Undo|Disable)\s*\(' -or $coordinator -match '\bOn(Destroy|Disable|ApplicationQuit)\s*\(') {
        throw 'Save preparation contains a published-hook teardown.'
    }
    Write-Host '[Contracts] Real game save entries/fields, installed Extender origin scope and record comparison readers verified.'
} finally { $extenderAssembly.Dispose(); $gameAssembly.Dispose() }
